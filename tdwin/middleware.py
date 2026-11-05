"""Windows edition, on every request: the first start, the registered workstations and the licence."""
import re,sys,time
from urllib.parse import urlencode
from django.conf import settings
from django.http import HttpResponse
from django.shortcuts import redirect
from django.utils import timezone
from . import netnames

# reachable from any device: booking pages and links for the customers of the businesses, the pairing page
OFFLINE=("/book/","/api/wordpress/")
PUBLIC=("/book/","/action/","/brand/","/site/","/static/","/prezzi/","/privacy/","/perche-sceglierci/","/perche-sceglierlo/","/prova/","/api/wordpress/","/postazione/")
# needed before the administrator exists
SETUP=("/static/","/site/","/primo-avvio/")
# still open without a licence: sign in, the licence itself and the backups, so the data stays reachable
UNLICENSED=SETUP+("/postazione/","/login/","/logout/","/verify/","/security/","/passkey/","/invite/","/platform/licenza/","/platform/backup/","/platform/inizia/")

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

def trusted_device(request):
    """2.13.0: Entra senza password (core/quick.py): the server PC itself or a registered workstation, as this
    middleware found them; a browser or phone that is neither never gets here past /postazione/."""
    return bool(getattr(request,"server_pc",False) or getattr(request,"postazione",None))

from django.middleware.csrf import CsrfViewMiddleware
class CsrfMiddleware(CsrfViewMiddleware):
    """Django's, with the trusted origins read on every check: the public address of Rete e certificato counts
    at once, also behind a proxy that rewrites the Host."""
    csrf_trusted_origins_hosts=property(CsrfViewMiddleware.csrf_trusted_origins_hosts.real_func)
    allowed_origins_exact=property(CsrfViewMiddleware.allowed_origins_exact.real_func)
    allowed_origin_subdomains=property(CsrfViewMiddleware.allowed_origin_subdomains.real_func)

TAMPERED={"kind":"bloccato","attivita":1,"postazioni":0,"problema":"Il controllo della licenza di questo programma è stato modificato: reinstalla TD Booking Suite."}

_allowed={"at":-1e9,"limit":None,"value":set()}
def allowed_businesses(limit):
    """None while the businesses fit the licence, otherwise the first ones created, as many as it allows.
    Read again once a minute."""
    if _allowed["limit"]!=limit or time.monotonic()-_allowed["at"]>60:
        from core.models import Tenant
        slugs=list(Tenant.objects.order_by("created","pk").values_list("slug",flat=True))
        _allowed.update(at=time.monotonic(),limit=limit,value=set(slugs[:max(1,limit)]) if len(slugs)>max(1,limit) else None)
    return _allowed["value"]

_ready=[]
def configured():
    if not _ready:
        from core.models import User
        if User.objects.filter(is_staff=True).exists():_ready.append(True)
    return bool(_ready)

class WindowsMiddleware:
    def __init__(self,get_response):self.get_response=get_response
    def __call__(self,request):
        """2.20.5: a page slower than 3 seconds leaves a line in TDBookingWeb.err.log (path only, no query: it can
        hold a client's name), to see what stops the program on a PC we cannot reach."""
        start=time.monotonic()
        try:return self.serve(request)
        finally:
            took=time.monotonic()-start
            if took>3:
                print(f"Pagina lenta: {request.method} {request.path} {took:.1f} s",file=sys.stderr,flush=True)
                from .stato import note
                note("programma",f"{request.method} {request.path}",took)
    def serve(self,request):
        from . import licensing,postazioni,rete
        path=request.path
        network=rete.apply()
        # a declared proxy is never the server PC, even on this PC and without X-Forwarded-For
        request.server_pc=is_local(request) and not rete.trusted(request.META,network["proxies"]);request.postazione=None
        # behind a trusted proxy the visitor's own address, for the login limits and the workstation list
        if network["proxies"]:request.META["REMOTE_ADDR"]=rete.client(request.META,network["proxies"])
        if not configured():
            if path.startswith(SETUP):return self.get_response(request)
            if request.server_pc:return redirect("/primo-avvio/")
            return HttpResponse("Il programma non è ancora configurato: completa il primo avvio dal PC server.",status=503,content_type="text/plain; charset=utf-8")
        # 2.10.0: Un PC solo without online: no booking page and no website connection, also from this PC
        if not settings.TDWIN_ONLINE and path.startswith(OFFLINE):
            return HttpResponse("La pagina prenotazioni non è attiva: l'amministratore del programma la accende da Online e sito.",status=404,content_type="text/plain; charset=utf-8")
        if not request.server_pc and not path.startswith(PUBLIC):
            request.postazione=postazioni.current(request)
            if not request.postazione:
                if request.method in ("GET","HEAD"):return redirect("/postazione/?"+urlencode({"next":request.get_full_path()}))
                return HttpResponse("Questa postazione non è registrata: aprila e inserisci il codice dalla pagina Postazioni.",status=403,content_type="text/plain; charset=utf-8")
        state=licensing.apply()
        # the installed program is compiled: a check swapped from Python (to crack the licence) is not, and stops here
        if type(licensing.apply) is not type(is_local) or type(licensing.status) is not type(is_local):state=TAMPERED
        request.licenza=state
        # businesses beyond the licence (the core's limit could be edited out): their pages stay closed, data kept
        over=re.match(r"^/(t|book|action)/([a-zA-Z0-9_-]+)/",path)
        allowed=allowed_businesses(state["attivita"]) if over else None
        if allowed is not None and over[2] not in allowed:
            return HttpResponse(f"Questa attività va oltre le {state['attivita']} della licenza: serve una licenza con più attività. I dati restano e sono nei backup.",status=402,content_type="text/plain; charset=utf-8")
        if state["kind"]=="bloccato" and not path.startswith(UNLICENSED):
            if path.startswith(PUBLIC) or path.startswith("/api/"):
                return HttpResponse("Servizio non attivo.",status=503,content_type="text/plain; charset=utf-8")
            user=getattr(request,"user",None)
            return redirect("/platform/inizia/" if user and user.is_authenticated and user.is_staff else "/platform/licenza/")
        request.extra_notices=notices(request,state)
        # the program's administrator lands on the guided setup until a licence and a business exist
        user=getattr(request,"user",None)
        if path=="/" and request.method=="GET" and user and user.is_authenticated and user.is_staff and request.session.get("mfa"):
            from core.models import Membership,Tenant
            if state["kind"]=="bloccato" or not Tenant.objects.exists():return redirect("/platform/inizia/")
            # the administrator who works with this account opens his business, like a titolare
            mine=list(Membership.objects.filter(user=user).values_list("tenant__slug",flat=True)[:2])
            if len(mine)==1:return redirect("dashboard",mine[0])
        response=self.get_response(request)
        if getattr(request,"renew_postazione",False):postazioni.set_cookie(response,request.COOKIES[postazioni.COOKIE])
        return response

def notices(request,state):
    """The trial's last days, a licence about to expire, backups off or failing: on top of the agency's pages."""
    user=getattr(request,"user",None)
    if not (user and user.is_authenticated) or request.path.startswith(("/book/","/action/")):return []
    from . import aggiornamenti
    found=[n for n in (aggiornamenti.notice(request,user),) if n]  # 2.14.0: also for the titolare
    if not user.is_staff:return found
    if state["kind"]=="prova" and not request.path.startswith("/platform/licenza/"):
        found.append({"title":"Versione di prova","text":f"Mancano {state['giorni']} giorn{'o' if state['giorni']==1 else 'i'}: poi il programma chiede la licenza","url":"/platform/licenza/","action":"Inserisci la licenza"})
    elif state["kind"]=="licenza" and state["giorni"] is not None and state["giorni"]<=15:
        found.append({"title":"Licenza in scadenza","text":f"Scade il {state['scadenza']:%d/%m/%Y}: chiedi il rinnovo","url":"/platform/licenza/","action":"Apri Licenza"})
    if not request.path.startswith("/platform/stato/"):
        from .stato import recent
        n=recent()
        if n:found.append({"title":"Pagine lente","text":f"Nelle ultime 24 ore {n} pagin{'a ha' if n==1 else 'e hanno'} fatto aspettare più di qualche secondo: lo stato del programma dice quali","url":"/platform/stato/","action":"Apri Stato del programma"})
    if not request.path.startswith("/platform/backup/"):
        from .models import ImpostazioniBackup,Esecuzione
        last=Esecuzione.objects.exclude(esito="in corso").first()
        if not ImpostazioniBackup.get().attivo:
            found.append({"title":"Backup non attivo","text":"Se il PC si rompe perdi prenotazioni e clienti: attiva il backup automatico","url":"/platform/backup/","action":"Attiva il backup"})
        elif last and last.esito!="ok":
            found.append({"title":"Ultimo backup non riuscito","text":f"Del {timezone.localtime(last.inizio):%d/%m/%Y %H:%M}: controlla le destinazioni","url":"/platform/backup/","action":"Apri Backup"})
    if not request.path.startswith("/platform/rete/"):
        from . import rete
        for text in rete.warnings(rete.apply())[:1]:
            if "scad" in text:found.append({"title":"Certificato del server","text":text,"url":"/platform/rete/","action":"Apri Rete e certificato"})
    return found
