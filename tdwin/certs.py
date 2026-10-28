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
    from .netnames import names as local_names
    return local_names([*settings.TDWIN_CONFIG.get("indirizzi",[]),urlparse(settings.PUBLIC_ORIGIN).hostname])

def _alt(name):
    try:return x509.IPAddress(ipaddress.ip_address(name))
    except ValueError:return x509.DNSName(name)

def _write(path,data):
    temporary=path.with_suffix(path.suffix+".tmp")
    with open(temporary,"wb") as f:f.write(data)
    os.replace(temporary,path)

def _key_pem(key):return key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())

# the authority signs only for this PC's names and for private addresses: a stolen key cannot impersonate other sites
PRIVATE=[ipaddress.ip_network(n) for n in ("10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","127.0.0.0/8","169.254.0.0/16","100.64.0.0/10","::1/128","fc00::/7","fe80::/10")]

def permitted(ca,name):
    try:subtrees=ca.extensions.get_extension_for_class(x509.NameConstraints).value.permitted_subtrees or []
    except x509.ExtensionNotFound:return True
    try:
        address=ipaddress.ip_address(name)
        return any(isinstance(t,x509.IPAddress) and address in t.value for t in subtrees)
    except ValueError:
        return any(isinstance(t,x509.DNSName) and (name==t.value or name.endswith("."+t.value)) for t in subtrees)

def authority(wanted):
    """The authority of this installation; made again, with a new fingerprint, when a new name falls outside it."""
    folder().mkdir(parents=True,exist_ok=True)
    key_path=folder()/"ca.key"
    if key_path.exists() and ca_path().exists():
        ca=x509.load_pem_x509_certificate(ca_path().read_bytes())
        if all(permitted(ca,n) for n in wanted):return serialization.load_pem_private_key(key_path.read_bytes(),None),ca
        print("Nuovi indirizzi fuori dall'autorità del programma: ne creo una nuova. Le postazioni chiederanno di confermare la nuova impronta.")
    key=ec.generate_private_key(ec.SECP256R1())
    now=datetime.datetime.now(datetime.timezone.utc)
    subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,f"TD Booking Suite CA {socket.gethostname()}"[:64]),x509.NameAttribute(NameOID.ORGANIZATION_NAME,"TD Booking Suite")])
    ips,dns=[],[]
    for n in wanted:
        try:address=ipaddress.ip_address(n)
        except ValueError:dns.append(x509.DNSName(n));continue
        if not any(address in net for net in PRIVATE if net.version==address.version):ips.append(x509.IPAddress(ipaddress.ip_network(address)))
    cert=(x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now-datetime.timedelta(days=1)).not_valid_after(now+datetime.timedelta(days=3650*2))
        .add_extension(x509.BasicConstraints(ca=True,path_length=0),critical=True)
        .add_extension(x509.KeyUsage(digital_signature=False,content_commitment=False,key_encipherment=False,data_encipherment=False,key_agreement=False,key_cert_sign=True,crl_sign=True,encipher_only=False,decipher_only=False),critical=True)
        .add_extension(x509.NameConstraints(permitted_subtrees=dns+[x509.IPAddress(n) for n in PRIVATE]+ips,excluded_subtrees=None),critical=True)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()),critical=False)
        .sign(key,hashes.SHA256()))
    _write(key_path,_key_pem(key));_write(ca_path(),cert.public_bytes(serialization.Encoding.PEM))
    return key,cert

def ensure(now=None):
    """Paths of the server certificate (with the authority after it) and its key, made or renewed when needed."""
    wanted=names()
    ca_key,ca=authority(wanted)
    cert_path,key_path=folder()/"server.crt",folder()/"server.key"
    now=now or datetime.datetime.now(datetime.timezone.utc)
    if cert_path.exists() and key_path.exists():
        current=x509.load_pem_x509_certificates(cert_path.read_bytes())[0]
        try:alt=current.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
        except x509.ExtensionNotFound:alt=[]
        have={str(n.value).lower() for n in alt}
        signed=current.extensions.get_extension_for_class(x509.AuthorityKeyIdentifier).value.key_identifier==x509.SubjectKeyIdentifier.from_public_key(ca_key.public_key()).digest
        if current.not_valid_after_utc-now>datetime.timedelta(days=30) and set(wanted)<=have and signed:return cert_path,key_path
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
