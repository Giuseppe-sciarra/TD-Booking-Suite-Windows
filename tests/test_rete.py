"""Rete e certificato: public address, trusted proxies, an uploaded certificate loaded without a restart."""
import datetime,ssl
import pytest
from cryptography import x509
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import ec,rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import NameOID
from django.conf import settings
from django.test import Client
from tdwin import rete
from tdwin.models import Postazione
from tests.conftest import login

def test_starting_from_zero_keeps_addresses_port_type_and_installation_code(tmp_path):
    from tdwin import setup_files
    before,_=setup_files.prepare(tmp_path/"prima",origine="https://ufficio.test:9443",porta=9443,indirizzi="192.168.1.10",modalita="server")
    after,_=setup_files.prepare(tmp_path/"nuova",keep=tmp_path/"prima"/"config.json")
    kept=("origine","indirizzi","porta","install_id","modalita","online")
    assert {k:after[k] for k in kept}=={k:before[k] for k in kept}  # the licence, bound to the installation code, stays valid
    assert after["env"]["DJANGO_SECRET_KEY"]!=before["env"]["DJANGO_SECRET_KEY"]  # but new keys: a new program
    # what the installer says now applies over them, as on a change of type
    changed,_=setup_files.prepare(tmp_path/"cambio",keep=tmp_path/"prima"/"config.json",modalita="standalone")
    assert changed["modalita"]=="standalone" and changed["ascolto"]=="127.0.0.1" and changed["install_id"]==before["install_id"]

def certificate(name="booking.example.com",days=90,key=None,start=-1):
    key=key or rsa.generate_private_key(public_exponent=65537,key_size=2048)
    now=datetime.datetime.now(datetime.timezone.utc)
    subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,name)])
    cert=(x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now+datetime.timedelta(days=start)).not_valid_after(now+datetime.timedelta(days=days))
        .add_extension(x509.SubjectAlternativeName([x509.DNSName(name)]),critical=False).sign(key,hashes.SHA256()))
    return cert,key

def pem(cert,key,password=None):
    enc=serialization.BestAvailableEncryption(password) if password else serialization.NoEncryption()
    return cert.public_bytes(serialization.Encoding.PEM)+key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.TraditionalOpenSSL,enc)

def pfx(cert,key,password=b"segreta"):
    return pkcs12.serialize_key_and_certificates(b"td",key,cert,None,serialization.BestAvailableEncryption(password))

def test_address_and_proxy_lines():
    assert rete.origin("booking.example.com/")=="https://booking.example.com"
    assert rete.origin("https://Booking.Example.com:443")=="https://booking.example.com" and rete.origin("https://x.it:8443")=="https://x.it:8443"
    assert rete.origin("https://[2001:db8::1]:8443")=="https://[2001:db8::1]:8443" and rete.origin("")==""
    for bad in ("http://x.it","https://x.it/percorso","https://a b","https://user@x.it"):
        with pytest.raises(ValueError):rete.origin(bad)
    nets=rete.proxies("192.168.1.10\n10.0.0.0/24, ::1")
    assert [str(n) for n in nets]==["192.168.1.10/32","10.0.0.0/24","::1/128"]
    with pytest.raises(ValueError):rete.proxies("192.168.1.300")
    with pytest.raises(ValueError,match="rete intera"):rete.proxies("192.168.1.10/24")  # an address with its netmask is not a network
    for forwarded,seen in (("198.51.100.7:51234","198.51.100.7"),("[2001:db8::7]:51234","2001:db8::7"),("2001:db8::7","2001:db8::7")):
        assert rete.client({"REMOTE_ADDR":"10.0.0.5","HTTP_X_FORWARDED_FOR":forwarded},nets)==seen  # IIS ARR adds the port
    # only a trusted proxy may say who the visitor is, and only its own entry on the right counts
    meta={"REMOTE_ADDR":"10.0.0.5","HTTP_X_FORWARDED_FOR":"6.6.6.6, 198.51.100.7"}
    assert rete.client(meta,nets)=="198.51.100.7" and rete.client({**meta,"REMOTE_ADDR":"203.0.113.9"},nets)=="203.0.113.9"
    assert rete.client({"REMOTE_ADDR":"10.0.0.5","HTTP_X_FORWARDED_FOR":"nonsense"},nets)=="10.0.0.5"

def test_reading_certificates_and_their_mistakes():
    cert,key=certificate()
    for data,password in ((pem(cert,key),""),(pem(cert,key,b"pw"),"pw"),(pfx(cert,key),"segreta"),(pem(cert,key),"typed for nothing")):
        chain,key_pem,info=rete.read(data,password)
        assert chain.startswith(b"-----BEGIN CERTIFICATE") and b"PRIVATE KEY" in key_pem and info["nomi"]==["booking.example.com"] and info["autofirmato"]
    mistakes={"password sbagliata":(pfx(cert,key),"altra"),"protetta":(pem(cert,key,b"pw"),""),"Manca la chiave":(cert.public_bytes(serialization.Encoding.PEM),""),
        "non è quella":(cert.public_bytes(serialization.Encoding.PEM)+pem(*certificate())[-2000:],""),"scaduto":(pem(*certificate(days=-1,start=-30)),""),
        "Nel file non c'è":(b"-----BEGIN NIENTE-----",""),"non leggibile":(b"\x00\x01binario",""),}
    other_cert,other_key=certificate()
    mistakes["non è quella"]=(cert.public_bytes(serialization.Encoding.PEM)+other_key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()),"")
    for words,(data,password) in mistakes.items():
        with pytest.raises(ValueError,match=words):rete.read(data,password)
    assert rete.covers(["*.example.com"],"booking.example.com") and not rete.covers(["*.example.com"],"a.b.example.com") and not rete.covers(["x.it"],"y.it")

class Adapter:
    context=None

def test_uploaded_certificate_is_served_without_restart_and_falls_back_when_expired():
    adapter=Adapter();rete.ADAPTERS.append(adapter)
    internal,_=rete.paths()
    rete.served(*rete.paths())
    cert,key=certificate(key=ec.generate_private_key(ec.SECP256R1()))
    info=rete.install(pfx(cert,key),"segreta")
    assert info["soggetto"]=="booking.example.com" and rete.paths()[0]==rete.own_paths()[0]
    assert isinstance(adapter.context,ssl.SSLContext)  # the next handshakes get the new certificate
    raw=cert.fingerprint(hashes.SHA256()).hex().upper()
    assert rete.fingerprint()==" ".join(raw[i:i+4] for i in range(0,len(raw),4))
    first=adapter.context;rete.apply(force=True)
    assert adapter.context is first  # nothing changed, nothing reloaded
    rete.use_internal()
    assert rete.paths()[0]==internal and adapter.context is not first
    # an uploaded certificate past its date is no longer shown: the internal one comes back on its own
    rete.install(pfx(*certificate(days=1)),"segreta")
    own=rete.own_paths()[0]
    expired,_=certificate(days=-1,start=-30)
    own.write_bytes(expired.public_bytes(serialization.Encoding.PEM))
    assert rete.paths()[0]==internal

def test_page_is_for_the_supreme_administrator_and_applies_at_once(admin,trial):
    from core.models import User
    user,staff=admin
    plain=User.objects.create_user("titolare@example.invalid","A-test-password-12345")
    assert login(plain).get("/platform/rete/").status_code==403
    page=staff.get("/platform/rete/").content.decode()
    assert "Rete e certificato" in page and "Interno" in page and "Rete e certificato" in staff.get("/platform/").content.decode()
    assert "deve essere https" in staff.post("/platform/rete/",{"azione":"indirizzi","indirizzo":"http://x.it"},follow=True).content.decode()
    staff.post("/platform/rete/",{"azione":"indirizzi","indirizzo":"booking.example.com","proxy":"10.0.0.5"})
    assert "booking.example.com" in settings.ALLOWED_HOSTS and settings.PUBLIC_ORIGIN=="https://booking.example.com"
    from tdwin import certs
    from tdwin.models import Rete
    assert Rete.get().proxy=="10.0.0.5" and "booking.example.com" not in certs.names()  # the authority the workstations know stays
    assert "certificato interno non è riconosciuto" not in staff.get("/platform/rete/").content.decode()  # a proxy is set
    # through the proxy: the public name is accepted and the visitor's address is the real one
    far=Client(REMOTE_ADDR="10.0.0.5",HTTP_X_FORWARDED_FOR="198.51.100.7",HTTP_HOST="booking.example.com")
    assert far.get("/postazione/").status_code==200
    from tdwin import postazioni
    item,code=postazioni.new_code("Telefono")
    far.post("/postazione/",{"codice":code})
    far.get("/platform/")
    assert Postazione.objects.get(pk=item.pk).ultimo_ip=="198.51.100.7"
    cert,key=certificate()
    from django.core.files.uploadedfile import SimpleUploadedFile
    r=staff.post("/platform/rete/",{"azione":"certificato","password":"","certificato":SimpleUploadedFile("server.crt",cert.public_bytes(serialization.Encoding.PEM)),
        "chiave":SimpleUploadedFile("server.key",key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))},follow=True).content.decode()
    assert "caricato" in r and "Caricato" in r and "autofirmato" in r
    assert "Torna al certificato interno" in r
    page=staff.get("/platform/postazioni/").content.decode()
    assert "https://booking.example.com" in page and rete.fingerprint() in page

def test_certificate_files_survive_concurrent_checks_and_half_written_pairs(monkeypatch):
    import threading
    from tdwin import certs,netnames
    # a public address the PC finds by itself (a rotating IPv6 one) stays out: it would remake the authority
    monkeypatch.setattr(netnames,"own_addresses",lambda:{"2001:db8::5","192.168.1.20"})
    assert "192.168.1.20" in certs.names() and "2001:db8::5" not in certs.names()
    errors=[]
    def check():
        try:certs.ensure()
        except Exception as ex:errors.append(ex)
    threads=[threading.Thread(target=check) for _ in range(8)]
    for t in threads:t.start()
    for t in threads:t.join()
    cert,key=certs.ensure()
    assert not errors and certs.loadable(cert,key)
    # a key of another pair next to the certificate, as after a stop between the two files: made again, not kept
    other=ec.generate_private_key(ec.SECP256R1())
    key.write_bytes(other.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
    assert not certs.loadable(cert,key)
    cert,key=certs.ensure()
    assert certs.loadable(cert,key)
    # an uploaded file that cannot be used leaves the internal certificate in its place
    rete.install(pfx(*certificate()),"segreta")
    own,_=rete.own_paths()
    assert rete.paths()==(own,own) and b"PRIVATE KEY" in own.read_bytes()
    own.write_bytes(own.read_bytes().split(b"-----BEGIN PRIVATE KEY")[0])
    assert rete.paths()==(cert,key)

def test_a_declared_proxy_is_never_the_server_pc_and_new_origins_count_at_once(admin,trial):
    user,staff=admin
    assert staff.get("/platform/").status_code==200
    # CSRF: a proxy that rewrites the Host sends the public Origin; it counts as soon as it is saved
    from django.test import Client
    c=login(user,Client(enforce_csrf_checks=True))
    c.get("/platform/rete/")
    token=c.cookies["csrftoken"].value
    post=lambda:c.post("/platform/rete/",{"azione":"indirizzi","indirizzo":"https://booking.example.com","proxy":""},HTTP_ORIGIN="https://booking.example.com",HTTP_HOST="192.168.1.10",HTTP_X_CSRFTOKEN=token).status_code
    assert post()==403  # not trusted yet, and Django has now computed its list
    staff.post("/platform/rete/",{"azione":"indirizzi","indirizzo":"booking.example.com","proxy":""})
    assert post()==302
    # a proxy on this very PC that adds no X-Forwarded-For: its visitors are not the server PC
    staff.post("/platform/rete/",{"azione":"indirizzi","indirizzo":"booking.example.com","proxy":"127.0.0.1"})
    assert staff.get("/platform/").url.startswith("/postazione/")
