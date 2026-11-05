"""Reaching the server from outside: the public address, the proxies in front of it and the certificate it shows.

Set by the supreme administrator in Rete e certificato and applied without a restart: the address and the
proxies at once, a new certificate from the next connection. The web service and the worker both apply it,
so the links in the emails carry the public address.
"""
import datetime,ipaddress,os,re,ssl,tempfile,threading,time
from urllib.parse import urlparse
from cryptography import x509
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.serialization import pkcs12
from django.conf import settings

ADAPTERS=[]  # the SSL adapter of the HTTPS server, registered by serve_https: a new certificate goes into it
_base={}
_applied={"at":-1e9,"value":None}
_served={"stamp":None}

def keep():
    """For the worker: the public address of the email links follows the page within a minute."""
    from django.db import close_old_connections
    while True:
        close_old_connections()
        try:apply(force=True)
        except Exception:pass  # database restarting: the next round tries again
        time.sleep(60)

def row():
    from .models import Rete
    return Rete.get()

# ---------- address and proxies
def origin(text):
    """https://name[:port] from what the administrator typed, '' to clear. ValueError when it is not an address."""
    text=(text or "").strip().rstrip("/")
    if not text:return ""
    if "://" not in text:text="https://"+text
    try:
        u=urlparse(text);host=u.hostname;port=u.port
    except ValueError:raise ValueError("Indirizzo non valido: scrivilo come https://booking.esempio.it")
    if u.scheme!="https":raise ValueError("L'indirizzo pubblico deve essere https://: da fuori il collegamento va sempre cifrato.")
    if u.path or u.query or u.username or not host:raise ValueError("Solo il nome e la porta, senza percorso: per esempio https://booking.esempio.it")
    try:ipaddress.ip_address(host);name=f"[{host}]" if ":" in host else host
    except ValueError:
        if not re.fullmatch(r"(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?",host):raise ValueError("Nome del server non valido.")
        name=host
    return "https://"+name+(f":{port}" if port and port!=443 else "")

def proxies(text):
    """Trusted proxies, one address or network per line (192.168.1.10, 10.0.0.0/24). ValueError on a wrong line."""
    out=[]
    for line in re.split(r"[\s,;]+",text or ""):
        if not line:continue
        # strict: 192.168.1.10/24, an address copied with its netmask, would trust the whole office network
        try:out.append(ipaddress.ip_network(line))
        except ValueError:raise ValueError(f"«{line[:60]}»: scrivi l'IP del proxy (192.168.1.10) o una rete intera (192.168.1.0/24).")
    return out

def trusted(meta,nets):
    try:return any(ipaddress.ip_address(meta.get("REMOTE_ADDR","")) in net for net in nets)
    except ValueError:return False

def client(meta,nets):
    """The visitor's address: from a trusted proxy the one it appends to X-Forwarded-For (nginx, NPMplus, Caddy
    and IIS ARR add it on the right; anything to its left is written by the visitor and can be false). IIS can
    add the port too: 198.51.100.7:51234, [2001:db8::7]:51234."""
    remote=meta.get("REMOTE_ADDR","")
    if not trusted(meta,nets):return remote
    last=meta.get("HTTP_X_FORWARDED_FOR","").split(",")[-1].strip()
    if last.startswith("["):last=last[1:].split("]")[0]
    elif last.count(":")==1:last=last.split(":")[0]
    try:return str(ipaddress.ip_address(last))
    except ValueError:return remote

_reloading=threading.Lock()
def apply(force=False):
    """Allowed names, public address and certificate as the administrator set them; read again once a minute."""
    if not force and _applied["value"] is not None and time.monotonic()-_applied["at"]<60:return _applied["value"]
    if not _base:_base.update(hosts=list(settings.ALLOWED_HOSTS),csrf=list(settings.CSRF_TRUSTED_ORIGINS),origin=settings.PUBLIC_ORIGIN)
    r=row()
    try:trusted=proxies(r.proxy)
    except ValueError:trusted=[]
    public=r.indirizzo or ""
    host=urlparse(public).hostname if public else ""
    name=(f"[{host}]" if ":" in host else host) if host else ""
    settings.ALLOWED_HOSTS=_base["hosts"]+([name] if name and name not in _base["hosts"] else [])
    settings.CSRF_TRUSTED_ORIGINS=_base["csrf"]+([public] if public and public not in _base["csrf"] else [])
    settings.PUBLIC_ORIGIN=public or _base["origin"]
    state={"origin":public,"proxies":trusted,"own":r.certificato=="proprio","info":r.info}
    if settings.TESTING or force:  # a change just saved in Rete e certificato: served at once
        try:reload()
        except Exception:pass  # keep serving the certificate already loaded; the page shows the problem
    elif _reloading.acquire(blocking=False):
        # 2.20.5: in a thread of its own: the check reads this PC's names (its full name asks the network's DNS, seconds
        # on some routers) and every request of that minute waited for it
        def run():
            from django.db import connection
            try:reload()
            except Exception:pass
            finally:_reloading.release();connection.close()
        threading.Thread(target=run,daemon=True).start()
    _applied.update(at=time.monotonic(),value=state)
    return state

# ---------- certificate
def own_paths():
    """The uploaded certificate, its chain and its key in one file: replaced in one step, never half."""
    from .certs import folder
    path=folder()/"proprio.pem"
    return path,path

def valid_until(path):
    return x509.load_pem_x509_certificates(path.read_bytes())[0].not_valid_after_utc

def paths():
    """What the server shows: the uploaded certificate while it is valid and usable, otherwise the internal one."""
    from .certs import LOCK,ensure,loadable
    if row().certificato=="proprio":
        cert,key=own_paths()
        with LOCK:
            try:
                if valid_until(cert)>datetime.datetime.now(datetime.timezone.utc) and loadable(cert,key):return cert,key
            except (OSError,ValueError):pass
    return ensure()

def served(cert,key):_served["stamp"]=_stamp(cert,key)
def _stamp(cert,key):return (str(cert),cert.stat().st_mtime_ns,str(key),key.stat().st_mtime_ns)

def reload():
    """A new context for the HTTPS server when the certificate to show has changed: handshakes in progress
    keep the old one, the next ones get the new one."""
    if not ADAPTERS:return  # the worker: no HTTPS server to give it to
    cert,key=paths()
    stamp=_stamp(cert,key)
    if stamp==_served["stamp"]:return
    context=ssl.create_default_context(purpose=ssl.Purpose.CLIENT_AUTH)
    context.load_cert_chain(str(cert),str(key))
    for adapter in ADAPTERS:adapter.context=context
    _served["stamp"]=stamp

def fingerprint():
    """What the client program shows at the first connection: the internal authority's fingerprint, or the
    uploaded certificate's own when that is the one served (a certificate Windows trusts needs no check)."""
    cert,_=paths()
    if cert==own_paths()[0]:
        raw=x509.load_pem_x509_certificates(cert.read_bytes())[0].fingerprint(hashes.SHA256()).hex().upper()
        return " ".join(raw[i:i+4] for i in range(0,len(raw),4))
    from .certs import fingerprint as authority
    return authority()

def describe(cert):
    try:names=[str(n.value) for n in cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value]
    except x509.ExtensionNotFound:names=[]
    cn=cert.subject.get_attributes_for_oid(x509.NameOID.COMMON_NAME)
    issuer=cert.issuer.get_attributes_for_oid(x509.NameOID.ORGANIZATION_NAME) or cert.issuer.get_attributes_for_oid(x509.NameOID.COMMON_NAME)
    return {"soggetto":cn[0].value if cn else cert.subject.rfc4514_string(),"emittente":issuer[0].value if issuer else cert.issuer.rfc4514_string(),
        "nomi":names,"dal":cert.not_valid_before_utc.isoformat(),"scadenza":cert.not_valid_after_utc.isoformat(),
        "autofirmato":cert.issuer==cert.subject}

def read(data,password=""):
    """Certificate chain and private key from a .pfx/.p12, or from PEM text with the certificates and the key
    (one file or two put together). ValueError with the reason when they cannot be used."""
    secret=password.encode() if password else None
    if b"-----BEGIN" in data:
        try:chain=x509.load_pem_x509_certificates(data)
        except ValueError:raise ValueError("Nel file non c'è un certificato in formato PEM.")
        block=re.search(rb"-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----.+?-----END (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----",data,re.S)
        if not block:raise ValueError("Manca la chiave privata: carica anche il file della chiave (.key), oppure il .pfx.")
        try:
            try:key=serialization.load_pem_private_key(block[0],secret)
            except TypeError:
                if secret is None:raise ValueError("La chiave privata è protetta: scrivi la sua password.")
                key=serialization.load_pem_private_key(block[0],None)  # a password typed for a key that has none
        except ValueError as ex:raise ValueError(str(ex) if "protetta" in str(ex) else "Chiave privata non leggibile o password sbagliata.")
    else:
        try:key,first,extra=pkcs12.load_key_and_certificates(data,secret)
        except ValueError:raise ValueError("File .pfx non leggibile o password sbagliata.")
        if not key or not first:raise ValueError("Nel file .pfx mancano il certificato o la chiave privata.")
        chain=[first]+list(extra or [])
    mine=key.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)
    leaf=next((c for c in chain if c.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)==mine),None)
    if not leaf:raise ValueError("La chiave privata non è quella di questo certificato.")
    now=datetime.datetime.now(datetime.timezone.utc)
    if leaf.not_valid_after_utc<=now:raise ValueError(f"Il certificato è scaduto il {leaf.not_valid_after_utc:%d/%m/%Y}.")
    if leaf.not_valid_before_utc>now:raise ValueError(f"Il certificato vale solo dal {leaf.not_valid_before_utc:%d/%m/%Y}.")
    # the server's certificate first, then the authorities that signed it, as browsers expect
    pem=b"".join(c.public_bytes(serialization.Encoding.PEM) for c in [leaf]+[c for c in chain if c is not leaf])
    key_pem=key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())
    return pem,key_pem,describe(leaf)

def install(data,password=""):
    """Check the files, prove that the HTTPS server can load them, then make them the certificate it shows."""
    from .certs import LOCK,loadable,replace
    pem,key_pem,info=read(data,password)
    path,_=own_paths()
    path.parent.mkdir(parents=True,exist_ok=True)
    handle,work=tempfile.mkstemp(dir=path.parent,prefix="proprio.")
    try:
        with os.fdopen(handle,"wb") as f:f.write(pem+key_pem)
        if not loadable(work,work):raise ValueError("Il server HTTPS non accetta questo certificato: prova a esportarlo di nuovo come .pfx.")
        with LOCK:replace(work,path)
    finally:
        if os.path.exists(work):os.remove(work)
    r=row();r.certificato="proprio";r.info=info;r.save()
    apply(force=True)
    return info

def use_internal():
    r=row();r.certificato="interno";r.save()
    apply(force=True)

def warnings(state):
    """What does not add up between the address, the certificate and the proxies."""
    out=[];info=state["info"];public=state["origin"]
    host=(urlparse(public).hostname or "") if public else ""
    if state["own"]:
        until=datetime.datetime.fromisoformat(info["scadenza"])
        days=(until-datetime.datetime.now(datetime.timezone.utc)).days
        if days<0:out.append(f"Il certificato caricato è scaduto il {until:%d/%m/%Y}: il server è tornato al certificato interno. Carica quello rinnovato.")
        elif days<=30:out.append(f"Il certificato caricato scade il {until:%d/%m/%Y}: carica quello rinnovato prima di allora.")
        if host and not covers(info["nomi"],host):out.append(f"Il certificato caricato non vale per {host}: da fuori browser e telefoni daranno un avviso.")
        if info.get("autofirmato"):out.append("Il certificato caricato è autofirmato: i browser lo segnalano finché non viene installato tra quelli attendibili; il programma per le postazioni lo accetta dopo il controllo dell'impronta.")
    elif public and not state["proxies"]:
        out.append("Da fuori il certificato interno non è riconosciuto dai browser: o metti davanti un proxy con il suo certificato e lo scrivi tra i proxy fidati, o carichi qui un certificato per "+host+". Il programma per le postazioni funziona lo stesso, dopo il controllo dell'impronta.")
    return out

def covers(names,host):
    host=host.lower()
    for n in names:
        n=n.lower()
        if n==host or (n.startswith("*.") and host.count(".")==n.count(".") and host.endswith(n[1:])):return True
    return False
