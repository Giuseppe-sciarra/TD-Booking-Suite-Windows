"""Licences made by td_licenze.py, checked by the program; the 7-day trial; what stops without a licence."""
import getpass
from datetime import date,timedelta
import pytest
from django.conf import settings
from django.test import Client
from django.utils import timezone
from core.models import Tenant
from tdwin import licensing
from tdwin.models import Prova
from tests.conftest import load_tool

def test_generator_makes_keys_and_licences_the_program_accepts(tmp_path,monkeypatch,settings,capsys):
    tool=load_tool();folder=tmp_path/"vendor"
    answers=iter(["chiave-segreta-1","chiave-segreta-1"])
    monkeypatch.setattr(getpass,"getpass",lambda prompt="":next(answers))
    tool.main(["--cartella",str(folder),"chiave"])
    assert b"ENCRYPTED" in (folder/"chiave-privata.pem").read_bytes() and tool.key_encrypted(folder)
    with pytest.raises(SystemExit,match="esiste già"):tool.main(["--cartella",str(folder),"chiave"])
    settings.TDWIN_LICENSE_KEY=tool.public_key_text(folder)
    code=licensing.install_code()
    out=tmp_path/"emesse"
    licence,path,data=tool.create(folder,"  Bar   Roma ",3,5,code.lower().replace("-",""),"2030-12-31",out,"chiave-segreta-1")
    assert path.exists() and licence in path.read_text(encoding="utf-8")
    data=licensing.parse(licence)
    assert data["intestatario"]=="Bar Roma" and data["attivita"]==3 and data["postazioni"]==5 and data["installazione"]==code
    assert data["scadenza"]==date(2030,12,31) and data["id"].endswith("-0001")
    monkeypatch.setattr(getpass,"getpass",lambda prompt="":"chiave-segreta-1")
    tool.main(["--cartella",str(folder),"--uscita",str(out),"crea","--intestatario","Pizzeria","--attivita","1","--postazioni","0"])
    second=tool.registry(folder)[-1]["licenza"]
    assert licensing.parse(second)["id"].endswith("-0002") and licensing.parse(second)["installazione"]==""
    assert len(tool.registry(folder))==2 and len(list(out.glob("licenza-TD-*.txt")))==2
    with pytest.raises(ValueError,match="errata"):tool.create(folder,"Altro",1,0,"","",out,"sbagliata")
    with pytest.raises(ValueError,match="16 lettere"):tool.create(folder,"Altro",1,0,"ABC","",out,"chiave-segreta-1")
    monkeypatch.setattr(getpass,"getpass",lambda prompt="":"sbagliata")
    with pytest.raises(SystemExit,match="errata"):tool.main(["--cartella",str(folder),"crea","--intestatario","Altro"])
    assert tool.read(second,tool.public_key_text(folder))["intestatario"]=="Pizzeria"
    with pytest.raises(ValueError,match="modificata"):tool.read(second[:-3]+"AAA",tool.public_key_text(folder))

def test_licence_kinds_and_a_replacement_for_more_workstations(tmp_path,settings):
    tool=load_tool();folder=tmp_path/"vendor";out=tmp_path/"emesse"
    tool.make_key(folder,"chiave-segreta-1");settings.TDWIN_LICENSE_KEY=tool.public_key_text(folder)
    # a register written by the earlier TD Licenze, without the new columns: kept and rewritten once
    old=["id","emessa","intestatario","attivita","postazioni","installazione","scadenza","licenza"]
    (folder/"registro-licenze.csv").write_text(",".join(old)+"\nTD-2025-0009,2025-05-01,Bar Vecchio,1,2,,,TDBS1.x.y\n",encoding="utf-8")
    code=licensing.install_code()
    licence,_,data=tool.create(folder,"Salone Bella",9,9,code,"",out,"chiave-segreta-1",tipo="pc")
    parsed=licensing.parse(licence)
    assert (data["tipo"],data["attivita"],data["postazioni"])==("pc",1,0) and parsed["tipo"]=="pc"
    with pytest.raises(ValueError,match="almeno 2"):tool.create(folder,"X",1,3,"","",out,"chiave-segreta-1",tipo="portale")
    rows=tool.registry(folder)
    assert [r["id"] for r in rows]==["TD-2025-0009",data["id"]] and tool.tipo_of(rows[0])=="server" and rows[0]["intestatario"]=="Bar Vecchio"
    # more workstations: the same client and installation, a new number, the old one marked as replaced
    new,_,more=tool.create(folder,"","1","5","","",out,"chiave-segreta-1",tipo="server",sostituisce=data["id"])
    parsed=licensing.parse(new)
    assert more["id"]!=data["id"] and (parsed["intestatario"],parsed["installazione"],parsed["postazioni"],parsed["tipo"],parsed["sostituisce"])==("Salone Bella",code,5,"server",data["id"])
    assert tool.registry(folder)[-1]["sostituisce"]==data["id"]
    with pytest.raises(ValueError,match="non è nel registro"):tool.create(folder,"","1","5","","",out,"chiave-segreta-1",sostituisce="TD-1999-0001")
    # from the command line what is not given stays as it was: more workstations keep the expiry, a new expiry keeps them
    import getpass
    from unittest import mock
    with mock.patch.object(getpass,"getpass",lambda prompt="":"chiave-segreta-1"):
        tool.main(["--cartella",str(folder),"--uscita",str(out),"crea","--intestatario","Studio","--tipo","server","--postazioni","5","--scadenza","2027-01-31"])
        first=tool.registry(folder)[-1]["id"]
        tool.main(["--cartella",str(folder),"--uscita",str(out),"crea","--sostituisce",first,"--postazioni","8"])
        row=tool.registry(folder)[-1]
        assert (row["postazioni"],row["scadenza"],row["tipo"],row["intestatario"])==("8","2027-01-31","server","Studio")
        tool.main(["--cartella",str(folder),"--uscita",str(out),"crea","--sostituisce",row["id"],"--scadenza","2028-01-31"])
        row=tool.registry(folder)[-1]
        assert (row["postazioni"],row["scadenza"],row["tipo"])==("8","2028-01-31","server")
        tool.main(["--cartella",str(folder),"--uscita",str(out),"crea","--sostituisce",row["id"],"--scadenza",""])
        assert tool.registry(folder)[-1]["scadenza"]==""  # "" is for ever
    # licences made before the kinds existed still say what they are
    assert licensing.parse(tool.sign(tool.load_key(folder,"chiave-segreta-1"),{"id":"A","intestatario":"B","attivita":1,"postazioni":0,"installazione":"","scadenza":""}))["tipo"]=="pc"

def test_tampered_foreign_and_broken_licences_are_refused(seller):
    good=seller()
    assert licensing.parse(good)["intestatario"]=="Bar Roma"
    assert licensing.parse(good[:20]+"\n "+good[20:])["id"]  # pasted across lines
    body,signature=good[len("TDBS1."):].split(".")
    import base64,json
    raw=json.loads(base64.urlsafe_b64decode(body+"=="))
    raw["postazioni"]=99
    forged="TDBS1."+base64.urlsafe_b64encode(json.dumps(raw).encode()).decode().rstrip("=")+"."+signature
    with pytest.raises(ValueError,match="firma"):licensing.parse(forged)
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
    with pytest.raises(ValueError,match="firma"):licensing.parse(seller.tool.sign(Ed25519PrivateKey.generate(),{"id":"X"}))
    with pytest.raises(ValueError,match="TDBS1"):licensing.parse("ciao")
    with pytest.raises(ValueError,match="incompleta"):licensing.parse(good[:-10])
    with pytest.raises(ValueError,match="incompleta"):licensing.parse(seller.tool.sign(seller.key,{"id":"X"}))

def test_program_without_public_key_says_so(seller,settings):
    licence=seller();settings.TDWIN_LICENSE_KEY=""
    with pytest.raises(ValueError,match="aggiorna il programma"):licensing.parse(licence)

def test_bound_licence_works_only_on_this_installation(seller,settings):
    with pytest.raises(ValueError,match="per l'installazione AAAA-BBBB-CCCC-DDDD"):licensing.install(seller(installazione="AAAA-BBBB-CCCC-DDDD"))
    data,over=licensing.install(seller(installazione=licensing.install_code()))
    assert over==0 and licensing.status()["kind"]=="licenza"
    # the same data folder on another PC: another installation code
    settings.TDWIN_CONFIG={**settings.TDWIN_CONFIG,"install_id":"copiata-altrove"}
    state=licensing.status()
    assert state["kind"]=="bloccato" and "non per questa" in state["problema"]

def test_expiry(seller):
    with pytest.raises(ValueError,match="scaduta"):licensing.install(seller(scadenza="2020-01-01"))
    end=timezone.localdate()+timedelta(days=10)
    licensing.install(seller(scadenza=end.isoformat()))
    assert licensing.status()["giorni"]==10
    later=licensing.status(now=timezone.now()+timedelta(days=11))
    assert later["kind"]=="bloccato" and "scaduta" in later["problema"]

def test_trial_lasts_seven_days_and_only_once(admin,seller):
    user,c=admin
    assert licensing.apply(force=True)["kind"]=="bloccato"
    assert c.get("/platform/").url=="/platform/inizia/"  # the administrator goes to the guided setup, licence first
    page=c.get("/platform/licenza/").content.decode()
    assert "Il programma è fermo" in page and "Attiva la prova di 7 giorni" in page and licensing.install_code() in page
    c.post("/platform/licenza/",{"azione":"prova"})
    state=licensing.apply(force=True)
    assert state["kind"]=="prova" and state["giorni"]==7 and state["attivita"]==1 and state["postazioni"]==2
    assert c.get("/platform/").status_code==200
    assert "Mancano 7 giorni" in c.get("/platform/clienti/").content.decode()
    assert "Attiva la prova" not in c.get("/platform/licenza/").content.decode()
    Prova.objects.update(attivata=timezone.now()-timedelta(days=7,seconds=1))
    state=licensing.apply(force=True)
    assert state["kind"]=="bloccato" and "prova di 7 giorni è finita" in state["problema"]
    assert "Attiva la prova" not in c.get("/platform/licenza/").content.decode()
    messages=c.post("/platform/licenza/",{"azione":"prova"},follow=True).content.decode()
    assert "una volta sola" in messages and Prova.objects.count()==1 and licensing.apply(force=True)["kind"]=="bloccato"

def test_without_licence_only_login_licence_and_backup_stay_open(admin):
    user,c=admin
    Tenant.objects.create(slug="bar",name="Bar")
    assert c.get("/platform/backup/").status_code==200
    assert c.get("/security/").status_code==200
    assert Client().get("/login/").status_code==200
    assert c.get("/t/bar/").url=="/platform/inizia/" and c.get("/platform/inizia/").status_code==200
    public=Client().get("/book/bar/")
    assert public.status_code==503 and "Servizio non attivo" in public.content.decode()

def test_licence_page_installs_and_portal_moves_the_edition(admin,seller):
    user,c=admin
    page=c.post("/platform/licenza/",{"azione":"licenza","licenza":seller(attivita=3,postazioni=4,intestatario="Agenzia Sole")},follow=True).content.decode()
    assert "attivata per Agenzia Sole" in page and "versione portale" in page and "Edizione Windows Portale" in page
    assert settings.EDITION=="cloud" and settings.TENANT_LIMIT==3
    for n in range(3):Tenant.objects.create(slug=f"a{n}",name=f"A{n}")
    from core.agency import can_create
    assert not can_create()
    assert "Hai raggiunto il numero massimo" in c.get("/platform/clienti/nuovo/").content.decode()
    bad=c.post("/platform/licenza/",{"azione":"licenza","licenza":seller(attivita=1)},follow=True).content.decode()
    assert "Qui ci sono 3 attività e questa licenza ne consente 1" in bad and licensing.status()["attivita"]==3

def test_single_licence_keeps_the_private_edition(admin,seller):
    user,c=admin
    c.post("/platform/licenza/",{"azione":"licenza","licenza":seller()})
    assert settings.EDITION=="private" and "Edizione Windows<" in c.get("/platform/").content.decode()

def test_only_the_administrator_installs(seller):
    from core.models import User,Tenant,Membership
    from tests.conftest import login
    User.objects.create_superuser("admin@example.invalid","A-test-password-12345")
    t=Tenant.objects.create(slug="bar",name="Bar")
    owner=User.objects.create_user("owner@example.invalid","A-test-password-12345")
    Membership.objects.create(tenant=t,user=owner,role="owner")
    c=login(owner)
    page=c.get("/platform/licenza/").content.decode()
    assert "la inserisce l'amministratore" in page and "Attiva la licenza" not in page
    assert c.post("/platform/licenza/",{"azione":"licenza","licenza":seller()}).status_code==403
    assert licensing.status()["kind"]=="bloccato"

def test_hard_to_crack(admin,seller,monkeypatch):
    user,c=admin
    licence=seller()
    # a verifier patched to accept any signature: the program stops instead of trusting it
    from cryptography.hazmat.primitives.asymmetric import ed25519
    real=ed25519.Ed25519PublicKey.from_public_bytes
    class Yes:
        def verify(self,signature,data):return None
    monkeypatch.setattr(licensing.Ed25519PublicKey,"from_public_bytes",staticmethod(lambda raw:Yes()))
    with pytest.raises(ValueError,match="modificata"):licensing.parse(licence)
    monkeypatch.setattr(licensing.Ed25519PublicKey,"from_public_bytes",staticmethod(real))
    assert licensing.parse(licence)["intestatario"]=="Bar Roma"
    # the trial: deleting its row does not give a new one, the data folder remembers it
    start,created=licensing.start_trial("admin")
    assert created and licensing.status()["kind"]=="prova"
    Prova.objects.all().delete()
    assert licensing.trial_start()==start and licensing.status()["kind"]=="prova"
    assert licensing.start_trial("admin")==(start,False)
    assert "Attiva la prova" not in c.get("/platform/licenza/").content.decode()
    # the clock moved back after the program ran: trial and expiry count from the last time it ran
    from tdwin.models import Orologio
    Orologio.objects.update_or_create(pk=1,defaults={"visto":timezone.now()+timedelta(days=30)})
    state=licensing.status()
    assert state["kind"]=="bloccato" and "indietro" in state["problema"]
    with pytest.raises(ValueError,match="scaduta"):licensing.install(seller(scadenza=(timezone.localdate()+timedelta(days=10)).isoformat()))
    Orologio.objects.all().delete();(settings.TDWIN_DATA/"licenza"/"visto.txt").unlink()
    assert licensing.status()["kind"]=="prova"

def test_businesses_beyond_the_licence_stay_closed(admin,seller):
    from core.models import Membership
    user,c=admin
    licensing.start_trial("admin")  # 1 business
    first=Tenant.objects.create(slug="prima",name="Prima");second=Tenant.objects.create(slug="seconda",name="Seconda")
    for t in (first,second):Membership.objects.create(tenant=t,user=user,role="owner")
    assert c.get("/t/prima/").status_code==200
    page=c.get("/t/seconda/")
    assert page.status_code==402 and "oltre le 1" in page.content.decode()
    assert Client().get("/book/seconda/").status_code==402
    licensing.install(seller(attivita=2,tipo="portale"))
    assert c.get("/t/seconda/").status_code==200

def test_the_generator_keeps_everything_in_its_own_folder(tmp_path,monkeypatch):
    tool=load_tool()
    from pathlib import Path
    assert tool.default_folder()==Path(tool.__file__).resolve().parent  # the script's folder, TD Licenze.exe's once built
    folder=tmp_path/"chiavetta";monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(getpass,"getpass",lambda prompt="":"")
    tool.main(["--cartella",str(folder),"chiave"])
    tool.main(["--cartella",str(folder),"crea","--intestatario","Bar"])
    assert list((folder/"licenze-emesse").glob("licenza-TD-*.txt")) and not (tmp_path/"licenze-emesse").exists()
