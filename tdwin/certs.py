"""HTTPS on the local network: a certification authority made for this installation signs the server certificate.

The client program trusts that authority after the first connection (its fingerprint is on the Postazioni page);
browsers and phones can install it from /postazione/certificato.crt. The server certificate covers the configured
names, this PC's names and addresses, and is renewed by itself a month before it expires."""
import datetime,ipaddress,os,socket
from urllib.parse import urlparse
from django.conf import settings
from cryptography import x509
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID,ExtendedKeyUsageOID

def folder():return settings.TDWIN_DATA/"certs"
def ca_path():return folder()/"ca.crt"

def names():
    """Every name and address a workstation may use to reach this PC."""
    found=["localhost","127.0.0.1","::1"]+[h for h in settings.TDWIN_CONFIG.get("indirizzi",[]) if h]
    found.append(urlparse(settings.PUBLIC_ORIGIN).hostname)
    host=socket.gethostname();found+=[host,socket.getfqdn()]
    try:found+=[info[4][0] for info in socket.getaddrinfo(host,None)]
    except OSError:pass
    seen=[]
    for name in found:
        name=(name or "").strip().lower().split("%")[0]
        if name and name not in seen:seen.append(name)
    return seen

def _alt(name):
    try:return x509.IPAddress(ipaddress.ip_address(name))
    except ValueError:return x509.DNSName(name)

def _write(path,data):
    temporary=path.with_suffix(path.suffix+".tmp")
    with open(temporary,"wb") as f:f.write(data)
    os.replace(temporary,path)

def _key_pem(key):return key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())

def authority():
    folder().mkdir(parents=True,exist_ok=True)
    key_path=folder()/"ca.key"
    if key_path.exists() and ca_path().exists():
        return serialization.load_pem_private_key(key_path.read_bytes(),None),x509.load_pem_x509_certificate(ca_path().read_bytes())
    key=ec.generate_private_key(ec.SECP256R1())
    now=datetime.datetime.now(datetime.timezone.utc)
    subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,f"TD Booking Suite CA {socket.gethostname()}"[:64]),x509.NameAttribute(NameOID.ORGANIZATION_NAME,"TD Booking Suite")])
    cert=(x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now-datetime.timedelta(days=1)).not_valid_after(now+datetime.timedelta(days=3650*2))
        .add_extension(x509.BasicConstraints(ca=True,path_length=0),critical=True)
        .add_extension(x509.KeyUsage(digital_signature=False,content_commitment=False,key_encipherment=False,data_encipherment=False,key_agreement=False,key_cert_sign=True,crl_sign=True,encipher_only=False,decipher_only=False),critical=True)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()),critical=False)
        .sign(key,hashes.SHA256()))
    _write(key_path,_key_pem(key));_write(ca_path(),cert.public_bytes(serialization.Encoding.PEM))
    return key,cert

def ensure(now=None):
    """Paths of the server certificate (with the authority after it) and its key, made or renewed when needed."""
    ca_key,ca=authority()
    cert_path,key_path=folder()/"server.crt",folder()/"server.key"
    now=now or datetime.datetime.now(datetime.timezone.utc)
    wanted=names()
    if cert_path.exists() and key_path.exists():
        current=x509.load_pem_x509_certificates(cert_path.read_bytes())[0]
        try:alt=current.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
        except x509.ExtensionNotFound:alt=[]
        have={str(n.value).lower() for n in alt}
        if current.not_valid_after_utc-now>datetime.timedelta(days=30) and set(wanted)<=have and current.issuer==ca.subject:return cert_path,key_path
    key=ec.generate_private_key(ec.SECP256R1())
    cert=(x509.CertificateBuilder().subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,(urlparse(settings.PUBLIC_ORIGIN).hostname or "localhost")[:64])]))
        .issuer_name(ca.subject).public_key(key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now-datetime.timedelta(days=1)).not_valid_after(now+datetime.timedelta(days=397))
        .add_extension(x509.SubjectAlternativeName([_alt(n) for n in wanted]),critical=False)
        .add_extension(x509.BasicConstraints(ca=False,path_length=None),critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]),critical=False)
        .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()),critical=False)
        .sign(ca_key,hashes.SHA256()))
    _write(key_path,_key_pem(key))
    _write(cert_path,cert.public_bytes(serialization.Encoding.PEM)+ca.public_bytes(serialization.Encoding.PEM))
    return cert_path,key_path

def fingerprint():
    """SHA-256 of the authority, in groups of four: the client program shows the same at the first connection."""
    if not ca_path().exists():return ""
    raw=x509.load_pem_x509_certificate(ca_path().read_bytes()).fingerprint(hashes.SHA256()).hex().upper()
    return " ".join(raw[i:i+4] for i in range(0,len(raw),4))
