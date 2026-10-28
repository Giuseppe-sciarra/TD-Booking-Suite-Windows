"""Workstations: every PC or browser that opens the program from the network is registered once with a 6-digit
code made on the Postazioni page. It keeps a long-lived cookie; the licence says how many may be registered."""
import secrets
from datetime import timedelta
from django.conf import settings
from django.contrib import messages
from django.core.exceptions import ValidationError
from django.db import transaction
from django.db.models import F
from django.http import HttpResponse
from django.shortcuts import render,redirect
from django.utils import timezone
from core.crypto import token_hash
from core.identity import safe_next,fresh
from core.site import staff_only
from .models import Postazione
from . import licensing

COOKIE="tdbs_postazione" if settings.DEV else "__Host-tdbs_postazione"
CODE_MINUTES=15
MAX_TRIES=10  # wrong codes, counted on every pending code: then they all stop working

def code_hash(code):return token_hash("postazione:"+code)
def limit():return licensing.apply()["postazioni"]
def paired():return Postazione.objects.filter(token__isnull=False)
def allowed_ids():
    """Over the licence's limit, the workstations registered first keep working."""
    return set(paired().order_by("abbinata").values_list("pk",flat=True)[:limit()])

def registered(request):
    token=request.COOKIES.get(COOKIE,"")
    return paired().filter(token=token_hash(token)).first() if token else None

def current(request):
    found=registered(request)
    if not found or found.pk not in allowed_ids():return None
    now=timezone.now()
    if not found.ultimo_accesso or now-found.ultimo_accesso>timedelta(minutes=5):
        Postazione.objects.filter(pk=found.pk).update(ultimo_accesso=now,ultimo_ip=request.META.get("REMOTE_ADDR"))
        request.renew_postazione=True
    return found

def set_cookie(response,token):
    # browsers keep a cookie at most 400 days: it is set again while the workstation is in use
    response.set_cookie(COOKIE,token,max_age=400*86400,secure=not settings.DEV,httponly=True,samesite="Lax",path="/")

def pending():return Postazione.objects.filter(token__isnull=True,scadenza_codice__gt=timezone.now(),tentativi__lt=MAX_TRIES)

def new_code(name):
    name=" ".join((name or "").split())[:80]
    if not name:raise ValueError("Dai un nome alla postazione, per esempio Reception o PC di Marco.")
    with transaction.atomic():
        if paired().count()+pending().count()>=limit():
            raise ValueError(f"La licenza consente {limit()} postazioni in rete e sono tutte usate: revocane una o chiedi una licenza con più postazioni.")
        code=f"{secrets.randbelow(10**6):06d}"
        item=Postazione.objects.create(nome=name,codice=code_hash(code),scadenza_codice=timezone.now()+timedelta(minutes=CODE_MINUTES))
    return item,code

def pair(code):
    """The cookie token for the workstation waiting for this code; ValueError otherwise."""
    code="".join(ch for ch in code or "" if ch.isdigit())
    error="";token=None
    with transaction.atomic():
        waiting=pending().select_for_update()
        match=waiting.filter(codice=code_hash(code)).first() if len(code)==6 else None
        if not match:
            waiting.update(tentativi=F("tentativi")+1)
            error="Codice non valido o scaduto: fanne generare uno nuovo dalla pagina Postazioni."
        elif paired().count()>=limit():
            error=f"La licenza consente {limit()} postazioni in rete e sono tutte usate."
        else:
            token=secrets.token_urlsafe(32)
            match.token=token_hash(token);match.codice="";match.scadenza_codice=None;match.abbinata=timezone.now()
            match.save(update_fields=["token","codice","scadenza_codice","abbinata"])
    if error:raise ValueError(error)
    return token,match

def pair_view(request):
    target=safe_next(request.POST.get("next") or request.GET.get("next"))
    if request.server_pc or current(request):return redirect(target)
    error="";over=registered(request) is not None
    if request.method=="POST":
        try:token,item=pair(request.POST.get("codice",""))
        except ValueError as ex:error=str(ex)
        else:
            response=redirect(target)
            set_cookie(response,token)
            return response
    return render(request,"tdwin/postazione.html",{"next":target,"error":error,"over":over,"limit":limit()},status=403 if error else 200)

def admin_view(request):
    from . import certs
    staff_only(request)
    code=None
    if request.method=="POST":
        fresh(request)
        action=request.POST.get("azione")
        try:
            if action=="nuova":
                item,code=new_code(request.POST.get("nome"))
            elif action=="revoca":
                Postazione.objects.filter(pk=request.POST.get("id")).delete()
                messages.success(request,"Postazione revocata: per usarla di nuovo serve un nuovo codice.")
                return redirect("/platform/postazioni/")
        except ValidationError:messages.error(request,"Postazione non trovata.")
        except ValueError as ex:messages.error(request,str(ex))
        if not code:return redirect("/platform/postazioni/")
    allowed=allowed_ids()
    items=list(paired().order_by("abbinata"))
    for p in items:p.attiva=p.pk in allowed
    return render(request,"tdwin/postazioni.html",{"items":items,"waiting":list(pending().order_by("creata")),"limit":limit(),"code":code,
        "origin":settings.PUBLIC_ORIGIN,"fingerprint":certs.fingerprint(),"minutes":CODE_MINUTES})

def ca_view(request):
    """The local certification authority, for phones and browsers that should trust the program's address."""
    from . import certs
    if not certs.ca_path().exists():return HttpResponse(status=404)
    response=HttpResponse(certs.ca_path().read_bytes(),content_type="application/x-x509-ca-cert")
    response["Content-Disposition"]='attachment; filename="TD-Booking-Suite-CA.crt"'
    return response
