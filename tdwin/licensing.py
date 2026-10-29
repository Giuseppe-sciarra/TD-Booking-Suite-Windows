"""Licence of the Windows edition.

licenze/td_licenze.py signs every licence offline with the seller's Ed25519 private key; the program holds only
the public key, so it checks a licence but can never make one. A licence may be bound to one installation code,
built from this installation and this PC: a data folder copied to another PC shows another code."""
import base64,binascii,hashlib,json,os,time
from datetime import date,datetime,timedelta
from django.conf import settings
from django.utils import timezone
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

PREFIX="TDBS1."
TRIAL_DAYS=7
TRIAL_LIMITS={"attivita":1,"postazioni":2}

def unb64(text):return base64.urlsafe_b64decode(text+"="*(-len(text)%4))
def normalize(code):return "".join(ch for ch in (code or "").upper() if ch.isalnum())

def machine_id():
    if os.name=="nt":
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r"SOFTWARE\Microsoft\Cryptography",0,winreg.KEY_READ|winreg.KEY_WOW64_64KEY) as k:
            return winreg.QueryValueEx(k,"MachineGuid")[0]
    try:
        with open("/etc/machine-id") as f:return f.read().strip()
    except OSError:return ""

def install_code(install_id=None):
    digest=hashlib.sha256(f"tdbs-installazione\n{install_id or settings.TDWIN_CONFIG['install_id']}\n{machine_id()}".encode()).digest()
    code=base64.b32encode(digest).decode()[:16]
    return "-".join(code[i:i+4] for i in range(0,16,4))

TIPI={"pc":"Un PC solo","server":"Server con postazioni","portale":"Portale, più attività"}

def public_key():
    """The seller's public key, from the settings while working on the sources. In the installed program the key is
    written by windows/compile_tdwin.py straight into the compiled parse(): no file or setting can swap it."""
    return settings.TDWIN_LICENSE_KEY

def verifier(key):
    """The Ed25519 check, after making sure it still refuses a forged signature: a verifier patched to accept
    anything (the way to crack the licences without the seller's key) stops the program instead."""
    public=Ed25519PublicKey.from_public_bytes(unb64(key))
    try:public.verify(b"\x00"*64,b"tdbs-controllo")
    except InvalidSignature:return public
    raise Tampered("La verifica delle licenze di questo programma è stata modificata: reinstalla TD Booking Suite.")

class MissingKey(Exception):pass
class Tampered(ValueError):pass
def signed(text,prefix):
    """The JSON a text carries when the seller signed it with his key: a licence (TDBS1.) or, 2.13.0, the state of
    the licences TD Licenze publishes (TDST1.). The prefix is part of what is signed: one never passes for the other.
    InvalidSignature for a forgery, ValueError or binascii.Error for a text cut or broken."""
    key=public_key()  # compile_tdwin.py: the seller's key
    if not key:raise MissingKey()
    public=verifier(key)
    body,signature=text[len(prefix):].split(".")
    signature=unb64(signature)
    if len(signature)!=64:raise ValueError("cut")  # a paste cut short, not a forgery
    public.verify(signature,(prefix+body).encode())
    return json.loads(unb64(body))

def parse(text):
    """The licence's data when the seller signed it; ValueError with the reason otherwise."""
    text="".join((text or "").split())
    if not text.startswith(PREFIX):raise ValueError("Non è una licenza di TD Booking Suite: deve iniziare con TDBS1.")
    try:
        raw=signed(text,PREFIX)
        data={"id":str(raw["id"]),"intestatario":str(raw["intestatario"]),"attivita":int(raw["attivita"]),"postazioni":int(raw["postazioni"]),
              "installazione":str(raw.get("installazione") or ""),"emessa":str(raw.get("emessa") or ""),
              "scadenza":date.fromisoformat(raw["scadenza"]) if raw.get("scadenza") else None}
    except MissingKey:raise ValueError("Questa installazione non ha la chiave per verificare le licenze: aggiorna il programma.") from None
    except Tampered:raise
    except InvalidSignature:raise ValueError("Licenza non valida: la firma non corrisponde, forse è stata modificata.") from None
    except (ValueError,KeyError,TypeError,binascii.Error):raise ValueError("Licenza incompleta: copiala di nuovo per intero.") from None
    if data["attivita"]<1 or data["postazioni"]<0:raise ValueError("Licenza non valida.")
    # the kind written by TD Licenze, or the one the numbers say for licences made before kinds existed
    kind=raw.get("tipo")
    data["tipo"]=kind if kind in TIPI else "portale" if data["attivita"]>1 else "pc" if data["postazioni"]==0 else "server"
    data["sostituisce"]=str(raw.get("sostituisce") or "")
    return data

def problem(data,today,code=None):
    """Why a genuine licence does not apply here, or an empty string."""
    code=code or install_code()
    if data["installazione"] and normalize(data["installazione"])!=normalize(code):
        return f"Questa licenza è per l'installazione {data['installazione']}, non per questa ({code})."
    if data["scadenza"] and data["scadenza"]<today:return f"La licenza è scaduta il {data['scadenza']:%d/%m/%Y}."
    return ""

def path():return settings.TDWIN_DATA/"licenza"/"licenza.txt"
_file={"stamp":None,"value":(None,"")}
def installed():
    """The installed licence and why it is unreadable, read again only when the file changes."""
    try:info=path().stat()
    except FileNotFoundError:return None,""
    stamp=(info.st_mtime_ns,info.st_size,info.st_ino)
    if _file["stamp"]!=stamp:
        try:_file["value"]=(parse(path().read_text("utf-8")),"")
        except ValueError as ex:_file["value"]=(None,str(ex))
        _file["stamp"]=stamp
    return _file["value"]

def marker(name):return settings.TDWIN_DATA/"licenza"/name
def read_time(name):
    try:return datetime.fromisoformat(marker(name).read_text("utf-8").strip())
    except (OSError,ValueError):return None
def write_time(name,value):
    """Atomic; a service that may not write the licence folder keeps the copy in the database only."""
    target=marker(name)
    try:
        target.parent.mkdir(parents=True,exist_ok=True)
        temporary=target.with_suffix(".tmp");temporary.write_text(value.isoformat()+"\n",encoding="utf-8");os.replace(temporary,target)
    except OSError:pass

def clock():
    """Now, never earlier than the last time the program ran (kept in the database and in the data folder):
    a clock moved back stretches neither the trial nor the licence."""
    from .models import Orologio
    real=timezone.now()
    seen=[o.visto for o in Orologio.objects.filter(pk=1)]+[t for t in (read_time("visto.txt"),) if t]
    last=max(seen) if seen else None
    if last is None or real>last+timedelta(minutes=10):
        Orologio.objects.update_or_create(pk=1,defaults={"visto":real});write_time("visto.txt",real)
        return real
    return max(real,last)

def trial_start():
    """When the trial began: the database row or, if someone deleted it, the copy in the data folder."""
    from .models import Prova
    found=[p.attivata for p in Prova.objects.filter(pk=1)]+[t for t in (read_time("prova.txt"),) if t]
    return min(found) if found else None

def start_trial(who):
    """The 7-day trial, once per installation: (start, started now)."""
    from .models import Prova
    first=trial_start()
    start=first or timezone.now()
    _,created=Prova.objects.get_or_create(pk=1,defaults={"attivata":start,"da":who})
    if not read_time("prova.txt"):write_time("prova.txt",start)
    return start,first is None and created

def status(now=None):
    """What this installation may do now: kind licenza, prova or bloccato, with its limits."""
    real=now or timezone.now()
    now=now or clock();today=timezone.localdate(now)
    late=now-real>timedelta(days=1)
    data,reason=installed()
    if data:
        reason=problem(data,today)
        # 2.13.0: revoked by the seller, or replaced by a newer one, as the online state said
        if not reason and revoked_id(data["id"]) in revoked():reason=f"La licenza {data['id']} non vale più: è stata revocata o sostituita da chi te l'ha venduta. Chiedigli quella nuova."
        if not reason:return {**data,"kind":"licenza","giorni":(data["scadenza"]-today).days if data["scadenza"] else None}
    start=trial_start()
    end=start+timedelta(days=TRIAL_DAYS) if start else None
    if end and now<end:
        return {**TRIAL_LIMITS,"kind":"prova","fine":end,"giorni":-((now-end)//timedelta(days=1)),"problema":reason}
    if not reason:reason=f"La prova di {TRIAL_DAYS} giorni è finita: per continuare serve la licenza." if start else "Per usare il programma inserisci la licenza o attiva la prova."
    if late:reason+=f" La data di questo PC è indietro: il programma conta dall'ultimo uso, il {timezone.localtime(now):%d/%m/%Y}."
    limits={"attivita":data["attivita"],"postazioni":data["postazioni"]} if data else TRIAL_LIMITS
    return {**limits,"kind":"bloccato","problema":reason,"prova_usata":bool(start)}

_applied={"at":0.0,"value":None}
def apply(force=False):
    """The core's edition and limits follow the licence: checked again once a minute and after every change."""
    if not force and _applied["value"] and time.monotonic()-_applied["at"]<60:return _applied["value"]
    from core.models import Tenant
    state=status()
    # more businesses than the licence allows (an expired Portale licence) stay visible: Private would hide them
    portal=state["attivita"]>1 or Tenant.objects.count()>1
    settings.EDITION="cloud" if portal else "private"
    settings.TENANT_LIMIT=state["attivita"]
    settings.EDITION_LABEL="Windows Portale" if portal else "Windows"
    _applied.update(at=time.monotonic(),value=state)
    return state

# 2.13.0: the state of the licences, online. TD Licenze publishes it signed with the seller's key: for each bound
# installation its current licence, sealed with a key only that installation knows (its code), and the ids of the
# licences revoked or replaced. Read once a day where there is Internet; offline nothing changes.
STATE_PREFIX="TDST1."
def code_digest(code,purpose):return hashlib.sha256(f"tdbs-{purpose}\n{normalize(code)}".encode()).digest()
def revoked_id(licence_id):return hashlib.sha256(f"tdbs-revoca\n{licence_id}".encode()).hexdigest()
def revoked():
    try:return set(json.loads(marker("revocate.json").read_text("utf-8")))
    except (OSError,ValueError):return set()
def download(url):
    import urllib.request
    request=urllib.request.Request(url,headers={"User-Agent":"TD-Booking-Suite","Cache-Control":"no-cache"})
    with urllib.request.urlopen(request,timeout=20) as answer:return answer.read(2_000_000).decode("utf-8")

def check_online(fetch=None):
    """The published state applied here: the licence changed for this installation (permanent, with an expiry,
    other numbers) is installed by itself, a revoked or replaced one stops. Returns what happened, in words, or ""
    when nothing changed; OSError offline, ValueError for a file that is not the seller's."""
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    url=getattr(settings,"TDWIN_STATO_LICENZE","")
    if not url:return ""
    text="".join((fetch or download)(url).split())
    if not text.startswith(STATE_PREFIX):raise ValueError("Lo stato delle licenze online non è leggibile.")
    try:raw=signed(text,STATE_PREFIX)
    except MissingKey:return ""
    except Tampered:raise
    except (InvalidSignature,ValueError,binascii.Error):raise ValueError("Lo stato delle licenze online non è firmato da chi vende il programma: lo ignoro.") from None
    try:issued=datetime.fromisoformat(str(raw["emesso"]))
    except (KeyError,ValueError):raise ValueError("Lo stato delle licenze online non è leggibile.") from None
    last=read_time("stato-licenze.txt")
    # an older state, served again, never undoes a newer one
    if last and issued<last:return ""
    said=""
    code=install_code();data,_=installed()
    entry=(raw.get("installazioni") or {}).get(code_digest(code,"installazione").hex())
    if entry and entry.get("licenza"):
        try:
            licence=AESGCM(code_digest(code,"licenza")).decrypt(unb64(entry["nonce"]),unb64(entry["licenza"]),b"tdbs-stato").decode()
            new=parse(licence)
        except Exception:new=None
        if new and normalize(new["installazione"])==normalize(code) and (not data or new["id"]!=data["id"]):
            target=path();target.parent.mkdir(parents=True,exist_ok=True)
            temporary=target.with_suffix(".tmp");temporary.write_text(licence+"\n",encoding="utf-8");os.replace(temporary,target)
            said=f"Arrivata da chi vende il programma la licenza {new['id']}"+(f", fino al {new['scadenza']:%d/%m/%Y}" if new["scadenza"] else ", senza scadenza")+"."
    ids=sorted(str(x) for x in raw.get("revocate") or [])
    before=revoked()
    try:
        marker("revocate.json").parent.mkdir(parents=True,exist_ok=True)
        temporary=marker("revocate.tmp");temporary.write_text(json.dumps(ids),encoding="utf-8");os.replace(temporary,marker("revocate.json"))
    except OSError:pass
    data,_=installed()
    if data and revoked_id(data["id"]) in set(ids)-before and not said:said=f"La licenza {data['id']} è stata revocata da chi vende il programma."
    write_time("stato-licenze.txt",issued)
    write_time("controllo-online.txt",timezone.now())
    apply(force=True)
    return said

def last_check():return read_time("controllo-online.txt")

def keep_online():
    """The worker's thread: a first look a few minutes after start, then once a day, every hour while offline."""
    import logging
    wait=180
    while True:
        time.sleep(wait)
        try:
            said=check_online()
            if said:logging.getLogger("tdwin").warning(said)
            wait=86400
        except (OSError,ValueError) as ex:
            logging.getLogger("tdwin").info("Controllo online delle licenze: %s",ex);wait=3600

def install(text):
    """Check a pasted licence against this installation and make it the active one."""
    from core.models import Tenant
    from .models import Postazione
    data=parse(text)
    reason=problem(data,timezone.localdate(clock()))
    if reason:raise ValueError(reason)
    businesses=Tenant.objects.count()
    if businesses>data["attivita"]:
        raise ValueError(f"Qui ci sono {businesses} attività e questa licenza ne consente {data['attivita']}: serve una licenza con più attività.")
    target=path();target.parent.mkdir(parents=True,exist_ok=True)
    temporary=target.with_suffix(".tmp")
    temporary.write_text("".join(text.split())+"\n",encoding="utf-8")
    os.replace(temporary,target)
    apply(force=True)
    paired=Postazione.objects.filter(token__isnull=False).count()
    return data,paired-data["postazioni"] if paired>data["postazioni"] else 0
