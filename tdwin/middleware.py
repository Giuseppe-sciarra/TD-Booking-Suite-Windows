"""Windows edition, on every request: the first start, the registered workstations and the licence."""
import time
from urllib.parse import urlencode
from django.http import HttpResponse
from django.shortcuts import redirect
from django.utils import timezone
from . import netnames

# reachable from any device: booking pages and links for the customers of the businesses, the pairing page
PUBLIC=("/book/","/action/","/brand/","/site/","/static/","/prezzi/","/privacy/","/perche-sceglierci/","/perche-sceglierlo/","/prova/","/api/wordpress/","/postazione/")
# needed before the administrator exists
SETUP=("/static/","/site/","/primo-avvio/")
# still open without a licence: sign in, the licence itself and the backups, so the data stays reachable
UNLICENSED=SETUP+("/postazione/","/login/","/logout/","/verify/","/security/","/passkey/","/invite/","/platform/licenza/","/platform/backup/")

_local={"at":-1e9,"value":set()}
def local_addresses():
    """This PC's addresses, read again once a minute: a new IP or a cable plugged in counts at once."""
    if time.monotonic()-_local["at"]>60:_local.update(at=time.monotonic(),value=netnames.own_addresses())
    return _local["value"]

def is_local(request):
    """The server PC itself. Not when a proxy adds its headers: everything would look local.
    shortcut: a port forward on the server PC (netsh portproxy, a proxy without X-Forwarded-For) also looks
    local and skips the workstation count; acceptable while the licence is a deterrent, not DRM"""
    meta=request.META
    return meta.get("REMOTE_ADDR") in local_addresses() and not (meta.get("HTTP_X_FORWARDED_FOR") or meta.get("HTTP_FORWARDED"))

_ready=[]
def configured():
    if not _ready:
        from core.models import User
        if User.objects.filter(is_staff=True).exists():_ready.append(True)
    return bool(_ready)

class WindowsMiddleware:
    def __init__(self,get_response):self.get_response=get_response
    def __call__(self,request):
        from . import licensing,postazioni
        path=request.path
        request.server_pc=is_local(request);request.postazione=None
        if not configured():
            if path.startswith(SETUP):return self.get_response(request)
            if request.server_pc:return redirect("/primo-avvio/")
            return HttpResponse("Il programma non è ancora configurato: completa il primo avvio dal PC server.",status=503,content_type="text/plain; charset=utf-8")
        if not request.server_pc and not path.startswith(PUBLIC):
            request.postazione=postazioni.current(request)
            if not request.postazione:
                if request.method in ("GET","HEAD"):return redirect("/postazione/?"+urlencode({"next":request.get_full_path()}))
                return HttpResponse("Questa postazione non è registrata: aprila e inserisci il codice dalla pagina Postazioni.",status=403,content_type="text/plain; charset=utf-8")
        state=licensing.apply()
        request.licenza=state
        if state["kind"]=="bloccato" and not path.startswith(UNLICENSED):
            if path.startswith(PUBLIC) or path.startswith("/api/"):
                return HttpResponse("Servizio non attivo.",status=503,content_type="text/plain; charset=utf-8")
            return redirect("/platform/licenza/")
        request.extra_notices=notices(request,state)
        response=self.get_response(request)
        if getattr(request,"renew_postazione",False):postazioni.set_cookie(response,request.COOKIES[postazioni.COOKIE])
        return response

def notices(request,state):
    """The trial's last days, a licence about to expire, backups off or failing: on top of the agency's pages."""
    user=getattr(request,"user",None)
    if not (user and user.is_authenticated and user.is_staff) or request.path.startswith(("/book/","/action/")):return []
    found=[]
    if state["kind"]=="prova" and not request.path.startswith("/platform/licenza/"):
        found.append({"title":"Versione di prova","text":f"Mancano {state['giorni']} giorn{'o' if state['giorni']==1 else 'i'}: poi il programma chiede la licenza","url":"/platform/licenza/","action":"Inserisci la licenza"})
    elif state["kind"]=="licenza" and state["giorni"] is not None and state["giorni"]<=15:
        found.append({"title":"Licenza in scadenza","text":f"Scade il {state['scadenza']:%d/%m/%Y}: chiedi il rinnovo","url":"/platform/licenza/","action":"Apri Licenza"})
    if not request.path.startswith("/platform/backup/"):
        from .models import ImpostazioniBackup,Esecuzione
        last=Esecuzione.objects.exclude(esito="in corso").first()
        if not ImpostazioniBackup.get().attivo:
            found.append({"title":"Backup non attivo","text":"Se il PC si rompe perdi prenotazioni e clienti: attiva il backup automatico","url":"/platform/backup/","action":"Attiva il backup"})
        elif last and last.esito!="ok":
            found.append({"title":"Ultimo backup non riuscito","text":f"Del {timezone.localtime(last.inizio):%d/%m/%Y %H:%M}: controlla le destinazioni","url":"/platform/backup/","action":"Apri Backup"})
    return found
