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
    assert remote().post("/primo-avvio/",{"email":"a@example.invalid","password":PASSWORD,"password2":PASSWORD}).status_code==403
    c=Client()
    assert "almeno 12" in c.post("/primo-avvio/",{"email":"a@example.invalid","password":"corta","password2":"corta"}).content.decode()
    assert "non coincidono" in c.post("/primo-avvio/",{"email":"a@example.invalid","password":PASSWORD,"password2":PASSWORD+"x"}).content.decode()
    assert c.post("/primo-avvio/",{"email":"Admin@Example.invalid","password":PASSWORD,"password2":PASSWORD}).url=="/login/"
    admin=User.objects.get()
    assert admin.is_staff and admin.is_superuser and admin.email=="admin@example.invalid"
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
    for _ in range(postazioni.MAX_TRIES):
        assert "non valido" in remote().post("/postazione/",{"codice":wrong}).content.decode()
    assert "non valido" in remote().post("/postazione/",{"codice":code}).content.decode()
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
