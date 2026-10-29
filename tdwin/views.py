from datetime import timedelta
from django.conf import settings
from django.contrib import messages
from django.contrib.auth.password_validation import validate_password
from django.core.exceptions import ValidationError
from django.core.validators import validate_email
from django.http import HttpResponse
from django.shortcuts import render,redirect
from django.utils import timezone
from core.identity import fresh
from core.models import User
from core.site import staff_only
from . import licensing

def primo_avvio_file():return settings.TDWIN_DATA/"licenza"/"primo-avvio.txt"

def primo_avvio_code():
    """A one-time code for the first start, in the data folder: only Windows administrators read that folder, so a
    normal user signed in on the server PC cannot create the program's administrator. Made once, by the web service,
    in the licence folder, the one part of the data folder it may write."""
    import secrets
    target=primo_avvio_file()
    try:return target.read_text("utf-8").strip()
    except OSError:
        letters="ABCDEFGHJKLMNPQRSTUVWXYZ23456789"  # no look-alike O/0, I/1
        code="-".join("".join(secrets.choice(letters) for _ in range(4)) for _ in range(2))
        target.parent.mkdir(parents=True,exist_ok=True);target.write_text(code+"\n",encoding="utf-8")
        return code

def first_run(request):
    """The supreme administrator: configures the program, businesses, workstations, backups and licence. Only from
    the server PC, only while no administrator exists, and with the code from the admin-only data folder."""
    import secrets
    from .middleware import configured
    if configured():return redirect("/login/")
    if not request.server_pc:return HttpResponse("Il primo avvio si fa dal PC server.",status=403,content_type="text/plain; charset=utf-8")
    code=primo_avvio_code();code_file=str(primo_avvio_file())
    error="";email="";username=""
    if request.method=="POST":
        from core.models import clean_username
        email=request.POST.get("email","").strip().casefold();password=request.POST.get("password","")
        try:
            typed="".join(ch for ch in request.POST.get("codice") or "" if ch.isalnum()).upper()
            if not secrets.compare_digest(typed,"".join(ch for ch in code if ch.isalnum())):
                raise ValidationError("Codice del primo avvio sbagliato: aprilo come indicato sotto e ricopialo.")
            username=clean_username(request.POST.get("username",""))
            if not username:raise ValidationError("Scegli lo username dell'amministratore.")
            if email:validate_email(email)
            if password!=request.POST.get("password2",""):raise ValidationError("Le due password non coincidono.")
            validate_password(password)
            admin=User.objects.create_superuser(email,password,username=username)
            try:primo_avvio_file().unlink()
            except OSError:pass
            from core.models import SiteSettings
            site=SiteSettings.current()
            if site.name=="Booking Suite":site.name="TD Booking Suite";site.save(update_fields=["name"])  # the core's neutral default
        except ValidationError as ex:error=" ".join(ex.messages)
        else:
            # whoever just chose the password is already in: straight on with the guided setup
            from core.identity import finish_login
            finish_login(request,admin,remember=request.POST.get("remember")=="1")
            messages.success(request,"Amministratore del programma creato. Ora la licenza e la tua attività.")
            return redirect("/platform/inizia/")
    return render(request,"tdwin/primo_avvio.html",{"error":error,"email":email,"username":username,"standalone":settings.TDWIN_STANDALONE,"code_file":code_file})

def licence_view(request):
    state=licensing.apply(force=True)
    if request.method=="POST":
        staff_only(request);fresh(request)
        if request.POST.get("azione")=="prova":
            start,created=licensing.start_trial(request.user.login_name)
            if created:messages.success(request,f"Prova attivata fino al {timezone.localtime(start+timedelta(days=licensing.TRIAL_DAYS)):%d/%m/%Y alle %H:%M}.")
            else:messages.error(request,"La prova si attiva una volta sola: per continuare serve la licenza.")
            licensing.apply(force=True)
        elif request.POST.get("azione")=="online":
            # 2.13.0: the state of the licences now, instead of waiting for the daily look
            try:said=licensing.check_online()
            except OSError:messages.error(request,"Internet non raggiungibile da questo PC: la licenza in uso resta valida, riprovo più tardi.")
            except ValueError as ex:messages.error(request,str(ex))
            else:messages.success(request,said or "Controllato: la licenza è aggiornata.")
        else:
            try:data,over=licensing.install(request.POST.get("licenza",""))
            except ValueError as ex:messages.error(request,str(ex))
            else:
                messages.success(request,f"Licenza {data['id']} attivata per {data['intestatario']}.")
                if over:messages.warning(request,f"Le postazioni registrate sono {over} più di quelle della licenza: le ultime registrate non entrano finché non ne revochi {over} da Postazioni.")
        return redirect("/platform/inizia/" if request.POST.get("next")=="/platform/inizia/" else "/platform/licenza/")
    return render(request,"tdwin/licenza.html",{"state":state,"code":licensing.install_code(),
        "trial_available":not licensing.trial_start() and state["kind"]!="licenza","staff":request.user.is_staff,"standalone":settings.TDWIN_STANDALONE,
        "tipo_label":licensing.TIPI.get(state.get("tipo",""),""),"online":bool(settings.TDWIN_STATO_LICENZE),"last_check":licensing.last_check()})

# ---------- guided setup: licence, the business and its owner, backups, workstations, then work as the owner
def create_business(name,preset,email,password,actor,owner=None,username=""):
    """The business and its owner, with the password typed now: what Clienti, Nuovo cliente does, in one step.
    owner: an existing account that becomes the owner, the administrator himself when he works with the program.
    Otherwise the owner's username, his email optional, or an account that already exists."""
    from django.db import transaction
    from django.utils.text import slugify
    from core.forms import TenantForm
    from core.models import Membership,Tenant,audit
    from core.platform import private_guard,seed_tenant,grant_access,account_for
    from core.presets import PRESETS,first_settings
    from core.site import tenant_defaults
    from core.tenancy import scope
    name=" ".join((name or "").split())
    if not name:raise ValidationError("Scrivi il nome dell'attività.")
    base=slugify(name)[:40].strip("-") or "attivita";slug=base;n=2
    while Tenant.objects.filter(slug=slug).exists():slug=f"{base}-{n}";n+=1
    form=TenantForm({**tenant_defaults(),"name":name[:100],"slug":slug})
    if not form.is_valid():raise ValidationError([e for errors in form.errors.values() for e in errors])
    with transaction.atomic():
        private_guard()
        tenant=form.save()
        if preset in PRESETS:tenant.save(update_fields=first_settings(tenant,preset))
        seed_tenant(tenant,preset)
        user=owner
        if owner is None:
            user,new=account_for({"username":username,"email":email})
            if user.is_staff:
                raise ValidationError("È l'account dell'amministratore del programma: per lavorare tu con questo account scegli «Io, con questo account»; il titolare ne ha uno suo.")
            # someone else's account not activated yet stays his: a password typed here would take it over
            if not new and not user.has_usable_password():raise ValidationError("Questo account non è ancora attivo: deve attivarlo dal suo invito.")
        Membership.objects.create(tenant=tenant,user=user,role="owner",can_schedule=True)
        # someone who already has an account keeps their password: one typed here would be ignored, better say it
        if owner is None and user.has_usable_password() and password:
            raise ValidationError("Questo account ha già una password, che resta quella: lascia vuoti i campi della password.")
        if owner is None and not user.has_usable_password():
            if password!=password.strip() or not password:raise ValidationError("Scegli la password del titolare.")
            validate_password(password,user);grant_access(user,password)
        with scope(tenant):audit(tenant,actor,"tenant.created",tenant.pk)
    return tenant

def setup_done(request):
    """The guided setup is over: a licence or the trial, and at least one business."""
    from core.models import Tenant
    state=getattr(request,"licenza",None) or licensing.apply()
    return state["kind"]!="bloccato" and Tenant.objects.exists()

def guide(request):
    """Configurazione guidata, for the program's administrator: every step says if it is done and leads to it."""
    from core.models import Membership,Tenant
    from core.presets import CHOICES
    from .models import ImpostazioniBackup,Postazione
    staff_only(request)
    error="";form={}
    if request.method=="POST" and request.POST.get("azione")=="attivita":
        fresh(request)
        form=request.POST
        try:
            # "io": the administrator works in the business too, one account for everything (a program used by its owner)
            me=request.POST.get("chi")=="io"
            if not me and request.POST.get("password")!=request.POST.get("password2"):raise ValidationError("Le due password del titolare non coincidono.")
            tenant=create_business(request.POST.get("nome"),request.POST.get("tipo",""),request.POST.get("email",""),request.POST.get("password",""),request.user,
                owner=request.user if me else None,username=request.POST.get("username",""))
        except ValidationError as ex:error=" ".join(ex.messages)
        except ValueError as ex:error=str(ex)
        else:
            messages.success(request,f"{tenant.name} è pronta: lavori tu, con questo account." if me else f"{tenant.name} è pronta, con il suo titolare.")
            return redirect("/platform/inizia/")
    state=licensing.apply(force=True)
    businesses=[]
    for t in Tenant.objects.order_by("created"):
        owner=Membership.objects.filter(tenant=t,role="owner").select_related("user").order_by("pk").first()
        businesses.append({"tenant":t,"owner":owner.user.login_name if owner else "","me":bool(owner and owner.user_id==request.user.pk)})
    backup=ImpostazioniBackup.get()
    steps={"licenza":state["kind"]!="bloccato","attivita":bool(businesses),"backup":bool(backup.attivo and backup.chiave),
        "postazioni":Postazione.objects.filter(token__isnull=False).exists()}
    return render(request,"tdwin/inizia.html",{"state":state,"steps":steps,"businesses":businesses,"backup":backup,
        "presets":CHOICES[1:],"error":error,"form":form,"standalone":settings.TDWIN_STANDALONE,
        "trial_available":not licensing.trial_start() and state["kind"]!="licenza","more":state["attivita"]>len(businesses),
        "office":settings.TDWIN_CONFIG.get("origine",""),"tipo_label":licensing.TIPI.get(state.get("tipo",""),"")})

def leave_as(request):
    """Esci ed entra come titolare: the administrator's session ends, the sign-in page already has the owner's username."""
    from django.contrib.auth import logout
    from django.http import HttpResponseNotAllowed
    from urllib.parse import urlencode
    if request.method!="POST":return HttpResponseNotAllowed(["POST"])
    staff_only(request)
    email=request.POST.get("email","").strip()[:254]
    logout(request)
    return redirect("/login/?"+urlencode({"email":email}) if email else "/login/")
