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
from .models import Prova

def first_run(request):
    """The supreme administrator: configures the program, businesses, workstations, backups and licence.
    Only from the server PC and only while no administrator exists."""
    from .middleware import configured
    if configured():return redirect("/login/")
    if not request.server_pc:return HttpResponse("Il primo avvio si fa dal PC server.",status=403,content_type="text/plain; charset=utf-8")
    error="";email=""
    if request.method=="POST":
        email=request.POST.get("email","").strip().casefold();password=request.POST.get("password","")
        try:
            validate_email(email)
            if password!=request.POST.get("password2",""):raise ValidationError("Le due password non coincidono.")
            validate_password(password)
            User.objects.create_superuser(email,password)
            from core.models import SiteSettings
            site=SiteSettings.current()
            if site.name=="Booking Suite":site.name="TD Booking Suite";site.save(update_fields=["name"])  # the core's neutral default
        except ValidationError as ex:error=" ".join(ex.messages)
        else:
            messages.success(request,"Amministratore creato: entra con email e password.")
            return redirect("/login/")
    return render(request,"tdwin/primo_avvio.html",{"error":error,"email":email})

def licence_view(request):
    state=licensing.apply(force=True)
    if request.method=="POST":
        staff_only(request);fresh(request)
        if request.POST.get("azione")=="prova":
            trial,created=Prova.objects.get_or_create(pk=1,defaults={"attivata":timezone.now(),"da":request.user.email})
            if created:messages.success(request,f"Prova attivata fino al {timezone.localtime(trial.fine):%d/%m/%Y alle %H:%M}.")
            else:messages.error(request,"La prova si attiva una volta sola: per continuare serve la licenza.")
            licensing.apply(force=True)
        else:
            try:data,over=licensing.install(request.POST.get("licenza",""))
            except ValueError as ex:messages.error(request,str(ex))
            else:
                messages.success(request,f"Licenza {data['id']} attivata per {data['intestatario']}.")
                if over:messages.warning(request,f"Le postazioni registrate sono {over} più di quelle della licenza: le ultime registrate non entrano finché non ne revochi {over} da Postazioni.")
        return redirect("/platform/licenza/")
    return render(request,"tdwin/licenza.html",{"state":state,"code":licensing.install_code(),
        "trial_available":not Prova.objects.exists() and state["kind"]!="licenza","staff":request.user.is_staff})
