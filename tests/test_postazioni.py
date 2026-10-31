"""First start from the server PC only; workstations registered with a code; the licence's limit."""
from datetime import timedelta
import pytest
from django.test import Client
from django.utils import timezone
from core.models import User
from tdwin import postazioni,licensing
from tdwin.models import Postazione
from tests.conftest import REMOTE,login,PASSWORD

def remote(**extra):return Client(REMOTE_ADDR=REMOTE,**extra)

def test_first_start_only_from_the_server_pc():
    assert Client().get("/platform/").url=="/primo-avvio/"
    far=remote().get("/platform/")
    assert far.status_code==503 and "dal PC server" in far.content.decode()
    assert remote().post("/primo-avvio/",{"username":"admin","password":PASSWORD,"password2":PASSWORD}).status_code==403
    c=Client()
    page=c.get("/primo-avvio/").content.decode()
    from django.conf import settings
    code=(settings.TDWIN_DATA/"licenza"/"primo-avvio.txt").read_text().strip()
    assert "primo-avvio.txt" in page and code not in page  # the code is only in the admin-only data folder
    assert "Codice del primo avvio sbagliato" in c.post("/primo-avvio/",{"codice":"AAAA-BBBB","username":"admin","password":PASSWORD,"password2":PASSWORD}).content.decode()
    assert not User.objects.exists()
    first=lambda **kw:c.post("/primo-avvio/",{"codice":code.lower().replace("-"," "),**kw})  # spaces and small letters are fine
    assert "Scegli lo username" in c.post("/primo-avvio/",{"codice":code,"password":PASSWORD,"password2":PASSWORD}).content.decode()  # as written in the file
    assert "almeno 12" in first(username="admin",password="corta",password2="corta").content.decode()
    assert "non coincidono" in first(username="admin",password=PASSWORD,password2=PASSWORD+"x").content.decode()
    # the administrator is signed in at once and goes on with the guided setup
    assert "Scegli lo username" in first(email="a@example.invalid",password=PASSWORD,password2=PASSWORD).content.decode()
    assert "Username non valido" in first(username="a@b",password=PASSWORD,password2=PASSWORD).content.decode()
    assert first(username="Admin",email="Admin@Example.invalid",password=PASSWORD,password2=PASSWORD,remember="1").url=="/platform/inizia/"
    assert not (settings.TDWIN_DATA/"licenza"/"primo-avvio.txt").exists()  # used once
    assert "Configurazione guidata" in c.get("/platform/inizia/").content.decode()
    # Resta collegato: until Esci (2.13.0), a cookie renewed at every visit for the longest a browser keeps it
    cookie=c.cookies[settings.SESSION_COOKIE_NAME]
    assert settings.SESSION_REMEMBER_DAYS==0 and int(cookie["max-age"])>=399*86400
    admin=User.objects.get()
    assert admin.is_staff and admin.is_superuser and (admin.username,admin.email)==("admin","admin@example.invalid")
    from core.models import SiteSettings
    assert SiteSettings.current().name=="TD Booking Suite"
    assert Client().get("/primo-avvio/").url=="/login/" and User.objects.count()==1

def test_a_proxy_in_front_is_never_the_server_pc(admin,trial):
    c=Client(HTTP_X_FORWARDED_FOR="198.51.100.7")
    assert c.get("/platform/").url.startswith("/postazione/")

def test_workstation_registers_with_a_code_and_can_be_revoked(admin,trial):
    user,staff=admin
    far=remote()
    assert far.get("/platform/clienti/?x=1").url=="/postazione/?next=%2Fplatform%2Fclienti%2F%3Fx%3D1"
    assert far.post("/login/",{"email":user.email,"password":PASSWORD}).status_code==403
    assert remote().get("/book/qualcosa/").status_code==404  # booking pages need no registration
    page=staff.post("/platform/postazioni/",{"azione":"nuova","nome":"Reception"}).content.decode()
    item=Postazione.objects.get()
    import re
    code=re.search(r'id="pair-code">(\d{3}) (\d{3})<',page);code=code[1]+code[2]
    assert item.codice and item.token is None and code not in item.codice
    answer=far.post("/postazione/",{"codice":code[:3]+" "+code[3:],"next":"/platform/clienti/"})
    assert answer.url=="/platform/clienti/" and answer.cookies[postazioni.COOKIE]["httponly"]
    item.refresh_from_db();assert item.token and not item.codice and item.abbinata
    signed=login(user,far)
    assert signed.get("/platform/").status_code==200
    item.refresh_from_db();assert item.ultimo_ip==REMOTE and item.ultimo_accesso
    assert far.post("/postazione/",{"codice":code}).status_code==302  # already registered: straight in
    listing=staff.get("/platform/postazioni/").content.decode()
    assert "Reception" in listing and "1 di 2 consentite" in listing
    staff.post("/platform/postazioni/",{"azione":"revoca","id":str(item.pk)})
    assert not Postazione.objects.exists() and signed.get("/platform/").url.startswith("/postazione/")

def test_wrong_codes_burn_the_pending_ones(admin,trial):
    item,code=postazioni.new_code("Sala")
    wrong="000000" if code!="000000" else "111111"
    # one address: a few tries, then it waits; the others can still pair
    for _ in range(postazioni.TRIES_PER_ADDRESS):
        assert "non valido" in remote().post("/postazione/",{"codice":wrong}).content.decode()
    assert remote().post("/postazione/",{"codice":code}).status_code==403
    for n in range(postazioni.MAX_TRIES-postazioni.TRIES_PER_ADDRESS):
        assert "non valido" in Client(REMOTE_ADDR=f"198.51.100.{n}").post("/postazione/",{"codice":wrong}).content.decode()
    assert "non valido" in Client(REMOTE_ADDR="198.51.100.250").post("/postazione/",{"codice":code}).content.decode()
    item.refresh_from_db();assert item.token is None
    late,code2=postazioni.new_code("Ufficio")
    Postazione.objects.filter(pk=late.pk).update(scadenza_codice=timezone.now()-timedelta(seconds=1))
    with pytest.raises(ValueError,match="scaduto"):postazioni.pair(code2)

def test_licence_limits_the_workstations(admin,trial,seller):
    for n in range(2):
        item,code=postazioni.new_code(f"PC {n}");postazioni.pair(code)
    with pytest.raises(ValueError,match="consente 2 postazioni"):postazioni.new_code("Terzo")
    user,staff=admin
    assert "consente 2 postazioni" in staff.post("/platform/postazioni/",{"azione":"nuova","nome":"Terzo"},follow=True).content.decode()
    licensing.install(seller(postazioni=5))
    item,code=postazioni.new_code("Terzo");token,_=postazioni.pair(code)
    data,over=licensing.install(seller(postazioni=1))
    assert over==2
    first=Postazione.objects.order_by("abbinata").first()
    late=remote();late.cookies[postazioni.COOKIE]=token
    page=late.get("/postazione/").content.decode()
    assert "oltre il limite" in page and late.get("/platform/").url.startswith("/postazione/")
    assert "Oltre il limite" in staff.get("/platform/postazioni/").content.decode() and first.pk in postazioni.allowed_ids()

def test_certificate_download_and_fingerprint(admin,trial):
    from tdwin import certs
    certs.ensure()
    answer=remote().get("/postazione/certificato.crt")
    assert answer.status_code==200 and b"BEGIN CERTIFICATE" in answer.content
    user,staff=admin
    assert certs.fingerprint()[:9] in staff.get("/platform/postazioni/").content.decode()

def test_certificates_cover_this_pc_and_the_authority_only_signs_for_it(settings):
    import ssl,socket
    from cryptography import x509
    from tdwin import certs
    cert,key=certs.ensure()
    ca=x509.load_pem_x509_certificate(certs.ca_path().read_bytes())
    assert certs.permitted(ca,"ufficio.test") and certs.permitted(ca,"192.168.7.9") and not certs.permitted(ca,"www.banca.example")
    leaf=x509.load_pem_x509_certificates(cert.read_bytes())[0]
    alt={str(n.value) for n in leaf.extensions.get_extension_for_class(x509.SubjectAlternativeName).value}
    assert {"localhost","127.0.0.1","ufficio.test",socket.gethostname().lower()}<=alt
    # Python's own verification, strict, with the name constraints
    context=ssl.create_default_context(cafile=str(certs.ca_path()));context.load_cert_chain(cert,key)
    first=certs.fingerprint()
    assert certs.ensure()==(cert,key) and certs.fingerprint()==first  # nothing changes without a reason
    settings.TDWIN_CONFIG={**settings.TDWIN_CONFIG,"indirizzi":["ufficio.test","booking.example.com"]}
    certs.ensure()
    assert certs.fingerprint()!=first and certs.permitted(x509.load_pem_x509_certificate(certs.ca_path().read_bytes()),"booking.example.com")

def test_workstation_cookie_is_renewed_while_in_use(admin,trial):
    user,staff=admin
    item,code=postazioni.new_code("Reception");token,_=postazioni.pair(code)
    far=login(user,remote());far.cookies[postazioni.COOKIE]=token
    answer=far.get("/platform/")
    assert answer.status_code==200 and answer.cookies[postazioni.COOKIE]["max-age"]==400*86400
    assert postazioni.COOKIE not in far.get("/platform/").cookies  # once every few minutes, not on every page

def test_admin_recovery_link_opens_without_a_licence(admin):
    user,staff=admin
    from core.platform import invitation
    user.set_unusable_password();user.save()
    link=invitation(user)
    from django.test import Client
    page=Client().get(link[link.index("/invite/"):])
    assert page.status_code==200 and "password" in page.content.decode().lower()

def test_an_owner_enters_without_password_from_this_pc_and_the_workstations(admin,trial):
    """2.13.0: Entra senza password, as the administrator turns it on: the server PC and a registered workstation."""
    from core.models import Tenant,Membership
    from core.tenancy import scope
    t=Tenant.objects.create(name="Salone Bella",slug="salone-bella")
    owner=User.objects.create_user("",PASSWORD,username="giulia");Membership.objects.create(tenant=t,user=owner,role="owner",can_schedule=True)
    staff=admin[1]
    m=Membership.objects.get(user=owner)
    assert "Entra senza password" in staff.get(f"/t/{t.slug}/members/").content.decode()
    staff.post(f"/t/{t.slug}/members/",{"action":"quick_on","member":m.pk})
    owner.refresh_from_db();assert owner.quick_login
    here=Client()
    assert "Entra come giulia" in here.get("/login/").content.decode()
    here.post("/login/senza-password/",{"user":str(owner.pk)})
    assert here.session["_auth_user_id"]==str(owner.pk) and here.get(f"/t/{t.slug}/").status_code==200
    # a device that is not registered never reaches the login page; a registered one does
    assert remote().get("/login/").url.startswith("/postazione/")
    assert remote().post("/login/senza-password/",{"user":str(owner.pk)}).status_code==403
    item,code=postazioni.new_code("Reception");token,_=postazioni.pair(code)
    desk=remote();desk.cookies[postazioni.COOKIE]=token
    assert "Entra come giulia" in desk.get("/login/").content.decode()
    desk.post("/login/senza-password/",{"user":str(owner.pk)});assert desk.session["_auth_user_id"]==str(owner.pk)
    with scope(t):
        from core.models import Audit
        assert Audit.objects.filter(action="login.quick").count()==2

def test_the_window_copies_the_day_sheet_as_who_is_signed_in(admin,trial):
    """2.14.0: the window of a workstation keeps today's and tomorrow's sheet with the address in its own script
    (MainForm.cs): a registered workstation reads it as who is signed in, without keeping the session awake; signed
    out or not registered it gets a redirect, which the script skips."""
    import re
    from django.urls import NoReverseMatch,reverse
    from core.models import Tenant,Membership
    from tests.conftest import ROOT
    try:reverse("agenda_sheet",args=["x"])
    except NoReverseMatch:pytest.skip("core prima della 2.14.0: niente foglio del giorno")
    script=(ROOT/"windows"/"client"/"MainForm.cs").read_text("utf-8")
    assert "'X-TDBS-Refresh': '1'" in script
    t=Tenant.objects.create(name="Salone Bella",slug="salone-bella")
    owner=User.objects.create_user("",PASSWORD,username="giulia");Membership.objects.create(tenant=t,user=owner,role="owner")
    url=re.search(r"get\('(/t/SLUG/[^']+)' \+ day\)",script)[1].replace("SLUG",t.slug)+"2026-11-02"
    item,code=postazioni.new_code("Reception");token,_=postazioni.pair(code)
    desk=login(owner,remote());desk.cookies[postazioni.COOKIE]=token
    s=desk.session;s["last_seen"]-=600;s.save();seen=s["last_seen"]
    page=desk.get(url,HTTP_X_TDBS_REFRESH="1");html=page.content.decode()
    assert page.status_code==200 and "Foglio del giorno" in html and 'value="2026-11-02"' in html and "<main" in html and 'rel="stylesheet"' in html
    assert desk.session["last_seen"]==seen
    assert Client().get(url).status_code==302 and remote().get(url).status_code==302
