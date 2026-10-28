"""Licence of the Windows edition.

licenze/td_licenze.py signs every licence offline with the seller's Ed25519 private key; the program holds only
the public key, so it checks a licence but can never make one. A licence may be bound to one installation code,
built from this installation and this PC: a data folder copied to another PC shows another code."""
import base64,binascii,hashlib,json,os,time
from datetime import date,timedelta
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

def parse(text):
    """The licence's data when the seller signed it; ValueError with the reason otherwise."""
    text="".join((text or "").split())
    if not text.startswith(PREFIX):raise ValueError("Non è una licenza di TD Booking Suite: deve iniziare con TDBS1.")
    if not settings.TDWIN_LICENSE_KEY:raise ValueError("Questa installazione non ha la chiave per verificare le licenze: aggiorna il programma.")
    try:
        body,signature=text[len(PREFIX):].split(".")
        signature=unb64(signature)
        if len(signature)!=64:raise ValueError("cut")  # a paste cut short, not a forgery
        Ed25519PublicKey.from_public_bytes(unb64(settings.TDWIN_LICENSE_KEY)).verify(signature,(PREFIX+body).encode())
        raw=json.loads(unb64(body))
        data={"id":str(raw["id"]),"intestatario":str(raw["intestatario"]),"attivita":int(raw["attivita"]),"postazioni":int(raw["postazioni"]),
              "installazione":str(raw.get("installazione") or ""),"emessa":str(raw.get("emessa") or ""),
              "scadenza":date.fromisoformat(raw["scadenza"]) if raw.get("scadenza") else None}
    except InvalidSignature:raise ValueError("Licenza non valida: la firma non corrisponde, forse è stata modificata.") from None
    except (ValueError,KeyError,TypeError,binascii.Error):raise ValueError("Licenza incompleta: copiala di nuovo per intero.") from None
    if data["attivita"]<1 or data["postazioni"]<0:raise ValueError("Licenza non valida.")
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

def status(now=None):
    """What this installation may do now: kind licenza, prova or bloccato, with its limits."""
    from .models import Prova
    now=now or timezone.now();today=timezone.localdate(now)
    data,reason=installed()
    if data:
        reason=problem(data,today)
        if not reason:return {**data,"kind":"licenza","giorni":(data["scadenza"]-today).days if data["scadenza"] else None}
    trial=Prova.objects.first()
    if trial and now<trial.fine:
        # shortcut: a clock moved back stretches the trial, fine for a 7-day trial
        return {**TRIAL_LIMITS,"kind":"prova","fine":trial.fine,"giorni":-((now-trial.fine)//timedelta(days=1)),"problema":reason}
    if not reason:reason=f"La prova di {TRIAL_DAYS} giorni è finita: per continuare serve la licenza." if trial else "Per usare il programma inserisci la licenza o attiva la prova."
    limits={"attivita":data["attivita"],"postazioni":data["postazioni"]} if data else TRIAL_LIMITS
    return {**limits,"kind":"bloccato","problema":reason,"prova_usata":bool(trial)}

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

def install(text):
    """Check a pasted licence against this installation and make it the active one."""
    from core.models import Tenant
    from .models import Postazione
    data=parse(text)
    reason=problem(data,timezone.localdate())
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
