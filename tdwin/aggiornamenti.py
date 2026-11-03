"""2.14.0: new versions of the program, proposed and never installed behind the user's back (the PCs are often off
at night). The worker looks at the newest public release on GitHub after start and once a day; the page shows it to
the program's administrator and to the titolare with Aggiorna ora. That link is the release's MSI: the program's
window (windows/client/MainForm.cs) downloads it, checks it with the SHA-256 published next to it and starts
msiexec with Windows' confirmation; a browser simply downloads it. The MSI keeps Un PC solo, server or postazione
and, on a server, copies the database before changing it (setup-server.ps1)."""
import json,logging,re,time
from django.conf import settings
from django.utils import timezone
log=logging.getLogger("tdwin")

def versione(tag):  # X.Y.Z only, with or without v: a 2.15.0-rc1 is not a release
    m=re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)",(tag or "").strip())
    return tuple(map(int,m.groups())) if m else None
def msi_url(ver):return f"https://github.com/{settings.TDWIN_RILASCI}/releases/download/v{ver}/TD-Booking-Suite-{ver}.msi"
def notes_url(ver):return f"https://github.com/{settings.TDWIN_RILASCI}/releases/tag/v{ver}"

def newer(release,current):
    """The version of a final release newer than current, with its MSI and .sha256 attached; "" otherwise."""
    new,now=versione(release.get("tag_name")),versione(current)
    if release.get("draft") or release.get("prerelease") or not new or not now or new<=now:return ""
    ver="%d.%d.%d"%new
    names={a.get("name") for a in release.get("assets") or []}
    return ver if {f"TD-Booking-Suite-{ver}.msi",f"TD-Booking-Suite-{ver}.msi.sha256"}<=names else ""

def check(fetch=None):
    """The look at GitHub, stored for the Licenza page; returns it in words. OSError offline, ValueError for an
    answer that is not a release."""
    from .licensing import download
    from .models import Aggiornamenti
    row=Aggiornamenti.get()
    try:release=json.loads((fetch or download)(f"https://api.github.com/repos/{settings.TDWIN_RILASCI}/releases/latest"))
    except (OSError,ValueError) as ex:
        row.esito="GitHub non raggiungibile da questo PC: riprovo più tardi." if isinstance(ex,OSError) else "La risposta di GitHub non è leggibile: riprovo più tardi."
        row.save(update_fields=["esito"]);raise
    row.disponibile=newer(release if isinstance(release,dict) else {},settings.VERSION)
    row.esito=f"È disponibile la versione {row.disponibile}." if row.disponibile else f"La {settings.VERSION} è l'ultima versione."
    row.ultimo_controllo=timezone.now();row.save()
    return row.esito

def keep():
    """The worker's thread: a first look a couple of minutes after start, then once a day, every hour while offline."""
    from django.db import close_old_connections
    from .models import Aggiornamenti
    wait=120
    while True:
        time.sleep(wait)
        try:
            close_old_connections()
            if Aggiornamenti.get().modo!="spento":check()
            wait=86400
        except (OSError,ValueError):wait=3600
        except Exception:log.exception("controllo degli aggiornamenti non riuscito");wait=3600

def notice(request,user):
    """Aggiorna ora, for the program's administrator and the titolare: on the server PC when GitHub has a newer
    version, on a postazione also when its window is older than the server (the window says its version)."""
    from core.models import Membership
    from .models import Aggiornamenti
    row=Aggiornamenti.get()
    if row.modo=="spento" or not (user.is_staff or Membership.objects.filter(user=user,role="owner").exists()):return None
    window=re.search(r"TDBookingSuite/(\d+\.\d+\.\d+)",request.META.get("HTTP_USER_AGENT",""))
    mine=versione(window.group(1) if window else settings.VERSION)
    target=row.disponibile if versione(row.disponibile) and versione(row.disponibile)>versione(settings.VERSION) else settings.VERSION
    if mine>=versione(target):return None
    return {"title":f"È disponibile la versione {target}","text":"Aggiorna ora la scarica, la controlla e la installa con la conferma di Windows: il programma si chiude per qualche minuto. Prenotazioni, clienti e impostazioni restano",
            "url":msi_url(target),"action":"Aggiorna ora","more_url":notes_url(target),"more":"Cosa cambia"}
