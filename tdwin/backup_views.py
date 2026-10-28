import re
from datetime import datetime,timezone as dt_timezone
from django import forms
from django.contrib import messages
from django.http import FileResponse,Http404
from django.shortcuts import render,redirect
from django.utils import timezone
from core.identity import fresh
from core.site import staff_only
from . import backup
from .models import ImpostazioniBackup,Destinazione,Esecuzione

class DestinationForm(forms.ModelForm):
    password_nuova=forms.CharField(required=False,widget=forms.PasswordInput(render_value=False),label="Password")
    class Meta:
        model=Destinazione
        fields=["nome","tipo","attiva","percorso","host","porta","utente","conserva"]
        labels={"nome":"Nome","tipo":"Tipo","attiva":"Attiva","percorso":"Cartella","host":"Server","porta":"Porta","utente":"Utente","conserva":"Copie da tenere"}
    def __init__(self,*a,**kw):
        kw.setdefault("label_suffix","");super().__init__(*a,**kw)
        self.fields["tipo"].choices=Destinazione.TIPI
    def clean(self):
        data=super().clean();kind=data.get("tipo");path=(data.get("percorso") or "").strip()
        if kind=="cartella" and not re.match(r"^([A-Za-z]:[\\/]|\\\\)",path):self.add_error("percorso","Scrivi il percorso completo, per esempio E:\\Backup o \\\\nas\\backup.")
        if kind=="rete" and not path.replace("/","\\").startswith("\\\\"):self.add_error("percorso","Una cartella di rete inizia con \\\\, per esempio \\\\nas\\backup\\booking.")
        if kind in ("ftp","ftps","sftp") and not data.get("host"):self.add_error("host","Serve l'indirizzo del server.")
        if kind=="sftp" and not data.get("utente"):self.add_error("utente","Per SFTP serve l'utente.")
        data["percorso"]=path
        return data

def page(request):
    staff_only(request)
    row=ImpostazioniBackup.get()
    edit=Destinazione.objects.filter(pk=request.GET.get("modifica")).first() if re.fullmatch(r"[0-9a-f-]{36}",request.GET.get("modifica","")) else None
    form=DestinationForm(instance=edit)
    if request.method=="POST":
        fresh(request)
        action=request.POST.get("azione")
        target=Destinazione.objects.filter(pk=request.POST.get("id")).first() if re.fullmatch(r"[0-9a-f-]{36}",request.POST.get("id","")) else None
        if action=="impostazioni":
            try:
                row.ora=datetime.strptime(request.POST.get("ora",""),"%H:%M").time()
                row.conserva_locali=max(1,min(365,int(request.POST.get("conserva_locali") or 7)))
            except ValueError:messages.error(request,"Ora nel formato 01:30 e copie da tenere tra 1 e 365.")
            else:
                row.attivo=bool(request.POST.get("attivo"))
                if row.attivo and not row.chiave:messages.error(request,"Prima imposta la password dei backup.")
                else:
                    row.save(update_fields=["attivo","ora","conserva_locali"])
                    messages.success(request,"Backup automatico attivo ogni giorno alle "+row.ora.strftime("%H:%M")+"." if row.attivo else "Backup automatico spento.")
        elif action=="password":
            password=request.POST.get("password","")
            if password!=request.POST.get("password2",""):messages.error(request,"Le due password non coincidono.")
            else:
                try:backup.set_password(password)
                except ValueError as ex:messages.error(request,str(ex))
                else:messages.success(request,"Password dei backup salvata. Scrivila e conservala lontano dal PC: senza, i backup non si aprono.")
        elif action=="esegui":
            if not row.chiave:messages.error(request,"Prima imposta la password dei backup.")
            else:
                ImpostazioniBackup.objects.filter(pk=1).update(richiesto=timezone.now())
                messages.success(request,"Backup avviato: tra qualche minuto lo trovi nello storico qui sotto.")
        elif action=="destinazione":
            form=DestinationForm(request.POST,instance=target)
            if form.is_valid():
                d=form.save(commit=False)
                if form.cleaned_data["password_nuova"]:backup.seal_secret(d,form.cleaned_data["password_nuova"])
                if target and (target.host,target.porta)!=(d.host,d.porta):d.impronta=""  # another server, another key
                d.prova_richiesta=timezone.now();d.save()
                messages.success(request,"Destinazione salvata: ora la provo, l'esito compare qui sotto tra qualche secondo.")
                return redirect("/platform/backup/")
            messages.error(request,"Controlla i campi della destinazione.")
        elif target and action=="prova":
            Destinazione.objects.filter(pk=target.pk).update(prova_richiesta=timezone.now(),ultimo_esito="Prova in corso")
            messages.success(request,"Prova avviata: ricarica la pagina tra qualche secondo.")
        elif target and action=="impronta":
            Destinazione.objects.filter(pk=target.pk).update(impronta="")
            messages.success(request,"Impronta azzerata: il prossimo collegamento registra quella nuova.")
        elif target and action=="elimina":
            target.delete();messages.success(request,"Destinazione eliminata. I backup già copiati lì restano dove sono.")
        if action!="destinazione" or form.is_valid():return redirect("/platform/backup/")
    folder=backup.local_folder()
    files=[{"name":p.name,"size":p.stat().st_size,"date":datetime.fromtimestamp(p.stat().st_mtime,tz=dt_timezone.utc)} for p in sorted(folder.glob(backup.PATTERN),reverse=True)] if folder.exists() else []
    return render(request,"tdwin/backup.html",{"row":row,"form":form,"edit":edit,"destinations":Destinazione.objects.order_by("creata"),
        "runs":Esecuzione.objects.all()[:20],"files":files,"folder":folder})

def download(request,name):
    staff_only(request);fresh(request)
    if not re.fullmatch(r"tdbs-\d{8}-\d{6}\.tdbk",name):raise Http404
    path=backup.local_folder()/name
    if not path.exists():raise Http404
    return FileResponse(path.open("rb"),as_attachment=True,filename=name)
