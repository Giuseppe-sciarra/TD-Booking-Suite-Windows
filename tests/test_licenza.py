"""Licences made by td-licenze.py, checked by the program; the 7-day trial; what stops without a licence."""
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
    assert b"ENCRYPTED" in (folder/"chiave-privata.pem").read_bytes()
    with pytest.raises(SystemExit,match="esiste già"):tool.main(["--cartella",str(folder),"chiave"])
    settings.TDWIN_LICENSE_KEY=(folder/"chiave-pubblica.txt").read_text().strip()
    code=licensing.install_code()
    monkeypatch.setattr(getpass,"getpass",lambda prompt="":"chiave-segreta-1")
    out=tmp_path/"emesse"
    licence=tool.create(folder,"  Bar   Roma ",3,5,code.lower().replace("-",""),"2030-12-31",out)
    data=licensing.parse(licence)
    assert data["intestatario"]=="Bar Roma" and data["attivita"]==3 and data["postazioni"]==5 and data["installazione"]==code
    assert data["scadenza"]==date(2030,12,31) and data["id"].endswith("-0001")
    second=tool.create(folder,"Pizzeria",1,0,"","",out)
    assert licensing.parse(second)["id"].endswith("-0002") and licensing.parse(second)["installazione"]==""
    assert len(tool.registry(folder))==2 and len(list(out.glob("licenza-TD-*.txt")))==2
    monkeypatch.setattr(getpass,"getpass",lambda prompt="":"sbagliata")
    with pytest.raises(SystemExit,match="errata"):tool.create(folder,"Altro",1,0,"","",out)
    with pytest.raises(SystemExit,match="16 lettere"):tool.create(folder,"Altro",1,0,"ABC","",out)

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
    assert c.get("/platform/").url=="/platform/licenza/"
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
    assert c.get("/t/bar/").url=="/platform/licenza/"
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
