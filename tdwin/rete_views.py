"""Rete e certificato: public address, trusted proxies and the certificate, for the supreme administrator only."""
import datetime,sys
from pathlib import Path
from django.conf import settings
from django.contrib import messages
from django.shortcuts import render,redirect
from core.identity import fresh
from core.site import staff_only
from . import rete

MAX_FILE=200_000  # a certificate with its chain and key is a few KB

def page(request):
    staff_only(request)
    r=rete.row()
    if request.method=="POST":
        fresh(request)
        action=request.POST.get("azione")
        try:
            if action=="indirizzi":
                r.indirizzo=rete.origin(request.POST.get("indirizzo",""))
                r.proxy="\n".join(str(n.network_address) if n.num_addresses==1 else str(n) for n in rete.proxies(request.POST.get("proxy","")))
                r.save();rete.apply(force=True)
                messages.success(request,"Indirizzi salvati: valgono da subito.")
            elif action=="certificato":
                files=[request.FILES.get(k) for k in ("certificato","chiave")]
                if not files[0]:raise ValueError("Scegli il file del certificato: .pfx, oppure .crt o .pem con la sua chiave.")
                if any(f and f.size>MAX_FILE for f in files):raise ValueError("File troppo grande per un certificato.")
                info=rete.install(b"\n".join(f.read() for f in files if f),request.POST.get("password",""))
                messages.success(request,f"Certificato per {info['soggetto']} caricato: il server lo usa dalla prossima connessione.")
            elif action=="interno":
                rete.use_internal()
                messages.success(request,"Il server usa di nuovo il certificato interno.")
            return redirect("network")
        except ValueError as ex:messages.error(request,str(ex))
        except OSError:messages.error(request,"Non riesco a scrivere il certificato nella cartella del programma: riprova tra qualche secondo.")
    state=rete.apply()
    cert,_=rete.paths()
    from cryptography import x509
    served=rete.describe(x509.load_pem_x509_certificates(cert.read_bytes())[0])
    served["scadenza"]=datetime.datetime.fromisoformat(served["scadenza"])
    return render(request,"tdwin/rete.html",{"row":r,"state":state,"served":served,"own_served":cert==rete.own_paths()[0],
        "warnings":rete.warnings(state),"fingerprint":rete.fingerprint(),"office":settings.TDWIN_CONFIG["origine"],"standalone":settings.TDWIN_STANDALONE,
        "port":settings.TDWIN_CONFIG.get("porta",8443),"online":settings.TDWIN_ONLINE,
        # where the installer put the setup script: server\python\python.exe and server\script\setup-server.ps1
        "script":str(Path(sys.executable).resolve().parent.parent/"script"/"setup-server.ps1")})
