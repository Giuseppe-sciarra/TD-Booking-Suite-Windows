"""Configurazione guidata, the business with its owner, the two accounts, Un PC solo or server."""
import json
from django.test import Client
from core.models import Membership,Tenant,User
from tdwin import licensing,setup_files
from tests.conftest import login,PASSWORD

def test_guided_setup_from_licence_to_the_owner(admin):
    user,staff=admin
    assert staff.get("/").url=="/platform/inizia/"  # blocked: straight to the guide, licence first
    page=staff.get("/platform/inizia/").content.decode()
    assert page.count("Da fare")==2 and "Prima la licenza" in page and "Postazioni e accesso da fuori" in page
    assert staff.post("/platform/licenza/",{"azione":"prova","next":"/platform/inizia/"}).url=="/platform/inizia/"
    assert licensing.apply(force=True)["kind"]=="prova"
    page=staff.get("/platform/inizia/").content.decode()
    assert "Chi lavora nell'attività" in page and 'value="altro" checked' in page  # server: the owner has his own account by default
    form={"azione":"attivita","nome":"Salone  Bella","tipo":"parrucchiere","username":"Titolare","email":"Titolare@Example.invalid","password":PASSWORD,"password2":PASSWORD}
    assert "non coincidono" in staff.post("/platform/inizia/",{**form,"password2":"altro"}).content.decode()
    assert "amministratore del programma" in staff.post("/platform/inizia/",{**form,"username":"","email":user.email}).content.decode()
    assert "scegli lo username" in staff.post("/platform/inizia/",{**form,"username":""}).content.decode()
    assert not Tenant.objects.exists()
    assert staff.post("/platform/inizia/",form).url=="/platform/inizia/"
    tenant=Tenant.objects.get()
    owner=Membership.objects.get(tenant=tenant)
    assert tenant.name=="Salone Bella" and tenant.slug=="salone-bella" and tenant.business_type=="parrucchiere"
    assert owner.role=="owner" and (owner.user.username,owner.user.email)==("titolare","titolare@example.invalid") and owner.user.check_password(PASSWORD)
    page=staff.get("/platform/inizia/").content.decode()
    assert "Titolare: titolare" in page and "Esci ed entra come titolare" in page
    assert staff.get("/").url!="/platform/inizia/"  # licence and business in place: the usual dashboard
    # the administrator leaves, the sign-in page already has the owner's username and explains the two accounts
    out=staff.post("/platform/inizia/esci/",{"email":"titolare"})
    assert out.url=="/login/?email=titolare"
    page=staff.get(out.url).content.decode()
    assert 'value="titolare"' in page and "Username o email" in page and "Amministratore del programma" in page and "Titolare e persone" in page
    assert staff.get("/platform/inizia/").status_code in (302,403)
    # the owner works in the business, the administrator's pages stay closed to him
    owner_client=Client()
    assert owner_client.post("/login/",{"email":"titolare","password":PASSWORD}).status_code==302
    assert owner_client.get(f"/t/{tenant.slug}/contacts/").status_code==200
    assert ">Titolare<" in owner_client.get(f"/t/{tenant.slug}/").content.decode()  # not "Amministratore", that is the other account
    assert owner_client.get("/platform/inizia/").status_code==403 and owner_client.post("/platform/inizia/esci/").status_code==403

def test_the_administrator_is_named_as_such(admin,trial):
    user,staff=admin
    page=staff.get("/platform/licenza/").content.decode()
    assert "Configurazione del programma" in page and "Amministratore del programma" in page and "Area agenzia" not in page
    assert "Configurazione guidata" in page  # in the menu

def test_one_pc_hides_workstations(admin,trial,settings):
    user,staff=admin
    settings.TDWIN_STANDALONE=True
    page=staff.get("/platform/inizia/").content.decode()
    assert "Un PC solo" in page and "Postazioni e accesso da fuori" not in page

def test_install_type_decides_address_and_listening(tmp_path,monkeypatch):
    import socket
    monkeypatch.setattr(socket,"gethostname",lambda:"Ufficio-PC")
    data=tmp_path/"dati"
    config,_=setup_files.prepare(data,modalita="standalone")
    assert (config["modalita"],config["ascolto"],config["origine"])==("standalone","127.0.0.1","https://localhost:8443")
    config,_=setup_files.prepare(data,modalita="server")
    assert (config["modalita"],config["ascolto"],config["origine"])==("server","0.0.0.0","https://ufficio-pc:8443")
    config,_=setup_files.prepare(data,porta=9443)
    assert config["origine"]=="https://ufficio-pc:9443" and config["modalita"]=="server"  # an automatic address follows the port
    config,_=setup_files.prepare(data,origine="https://booking.example.com:9443",modalita="standalone")
    config,_=setup_files.prepare(data,modalita="server")
    assert config["origine"]=="https://booking.example.com:9443"  # an address given by hand stays
    # 2.10.0: Un PC solo with online on answers from the network; off again, only to itself. A server is always online
    config,_=setup_files.prepare(data,modalita="standalone",online="on")
    assert (config["online"],config["ascolto"])==(True,"0.0.0.0")
    assert setup_files.prepare(data)[0]["ascolto"]=="0.0.0.0"  # an update keeps the choice
    config,_=setup_files.prepare(data,online="off")
    assert (config["online"],config["ascolto"])==(False,"127.0.0.1")
    # a 2.8 server has no install type: it stays a server
    old=json.loads((data/"config.json").read_text());old.pop("modalita");old.pop("ascolto")
    (data/"config.json").write_text(json.dumps(old))
    assert setup_files.prepare(data)[0]["modalita"]=="server"

def test_forgotten_password_from_the_server_pc(admin):
    import io,pytest
    from django.core.management import call_command
    from django.core.management.base import CommandError
    user,staff=admin
    owner=User.objects.create_user("",PASSWORD,username="titolare")
    from core.models import Audit
    from core.tenancy import scope
    shop=Tenant.objects.create(slug="bar",name="Bar");Membership.objects.create(tenant=shop,user=owner,role="owner")
    with pytest.raises(CommandError,match="--conferma"):call_command("recupera_accesso",email="titolare")
    with pytest.raises(CommandError,match="Nessun account"):call_command("recupera_accesso",email="nessuno@example.invalid",conferma=True)
    for who,email in (("utente","Titolare"),("amministratore",user.email)):
        out=io.StringIO();call_command("recupera_accesso",email=email,conferma=True,stdout=out)
        kind,link=out.getvalue().split()
        assert kind==who and "/invite/" in link
        assert Client().get(link[link.index("/invite/"):]).status_code==200
    owner.refresh_from_db()
    assert not owner.has_usable_password()
    with scope(shop):assert Audit.objects.filter(tenant=shop,action="member.password_reset",target=str(owner.pk),detail__via="console").exists()

def test_one_account_configures_and_works(admin,trial,settings):
    user,staff=admin
    settings.TDWIN_STANDALONE=True
    assert 'value="io" checked' in staff.get("/platform/inizia/").content.decode()  # Un PC solo: one account by default
    assert staff.post("/platform/inizia/",{"azione":"attivita","nome":"Studio Rossi","tipo":"","chi":"io","email":"","password":"","password2":""}).url=="/platform/inizia/"
    tenant=Tenant.objects.get();owner=Membership.objects.get(tenant=tenant)
    assert owner.user==user and owner.role=="owner"
    user.refresh_from_db();assert user.check_password(PASSWORD) and User.objects.count()==1  # no second account, same password
    page=staff.get("/platform/inizia/").content.decode()
    assert "Lavori tu, con questo account" in page and f'href="/t/{tenant.slug}/"' in page and "Esci ed entra come" not in page
    # the program opens on the business, agenda and clients need no support grant, the configuration stays his
    assert staff.get("/").url==f"/t/{tenant.slug}/"
    assert staff.get(f"/t/{tenant.slug}/contacts/").status_code==200
    assert staff.get("/platform/licenza/").status_code==200 and staff.get("/platform/backup/").status_code==200

def test_one_pc_signs_in_with_the_password_alone(admin,trial,settings):
    user,staff=admin
    settings.MFA_ENABLED=False  # what tdwin.settings does for Un PC solo
    page=Client().get("/login/").content.decode()
    assert "Username o email" in page and "passkey-login" not in page
    assert "Proteggi il tuo account" not in staff.get("/platform/licenza/").content.decode()
    settings.MFA_ENABLED=True  # with workstations: optional, and only the administrator is reminded
    assert "Proteggi il tuo account" in staff.get("/platform/licenza/").content.decode()


def test_one_pc_keeps_the_booking_page_off_until_the_administrator_turns_it_on(admin,trial,settings):
    from tdwin import middleware
    user,staff=admin
    settings.TDWIN_STANDALONE=True;settings.TDWIN_ONLINE=False;settings.PUBLIC_BOOKING=False
    t=Tenant.objects.first()
    r=staff.get(f"/book/{t.slug}/") if t else staff.get("/book/x/")
    assert r.status_code==404 and "Online e sito" in r.content.decode()
    assert staff.get("/api/wordpress/connect/").status_code==404
    page=staff.get("/platform/rete/").content.decode()
    assert "Prenotazioni online e sito" in page and 'data-online="on"' in page and "Spente" in page and "Indirizzo pubblico" not in page
    assert "tdwin/rete.js" in page and "-Online on" in page
    # turned on: the page and the network settings
    settings.TDWIN_ONLINE=True;settings.PUBLIC_BOOKING=True
    page=staff.get("/platform/rete/").content.decode()
    assert 'data-online="off"' in page and "Accese" in page and "Indirizzo pubblico" in page
    assert staff.get("/book/nessuna/").status_code!=404 or "Online e sito" not in staff.get("/book/nessuna/").content.decode()
