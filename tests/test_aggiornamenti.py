"""2.14.0 — new versions proposed in the program: the public release on GitHub (network mocked), never installed by itself."""
import json
import pytest
from django.conf import settings
from django.test import RequestFactory
from tdwin import aggiornamenti
from tdwin.models import Aggiornamenti

def release(tag,files=None,**extra):
    ver=tag.lstrip("v")
    names=[f"TD-Booking-Suite-{ver}.msi",f"TD-Booking-Suite-{ver}.msi.sha256"] if files is None else files
    return {"tag_name":tag,"assets":[{"name":n} for n in names],**extra}

def later(n=1):
    a,b,c=map(int,settings.VERSION.split("."));return f"{a}.{b}.{c+n}"

def test_only_a_newer_final_release_with_msi_and_hash():
    assert aggiornamenti.newer(release("v2.15.0"),"2.14.0")=="2.15.0"
    assert aggiornamenti.newer(release("2.14.10"),"2.14.9")=="2.14.10"  # numbers, not strings
    for rel,current in ((release("v2.14.0"),"2.14.0"),(release("v2.13.9"),"2.14.0"),(release("v2.15.0-rc1"),"2.14.0"),
                        (release("v2.15.0",prerelease=True),"2.14.0"),(release("v2.15.0",draft=True),"2.14.0"),
                        (release("v2.15.0",files=["TD-Booking-Suite-2.15.0.msi"]),"2.14.0"),(release("v2.15.0"),"")):
        assert aggiornamenti.newer(rel,current)=="",(rel,current)

def test_check_stores_the_result_and_offline_says_so():
    seen=[]
    def fetch(url):seen.append(url);return json.dumps(release("v"+later()))
    assert aggiornamenti.check(fetch)==f"È disponibile la versione {later()}."
    row=Aggiornamenti.get()
    assert row.disponibile==later() and row.ultimo_controllo and seen==["https://api.github.com/repos/Giuseppe-sciarra/TD-Booking-Suite-per-Windows/releases/latest"]
    assert aggiornamenti.check(lambda url:json.dumps(release("v"+settings.VERSION))).startswith(f"La {settings.VERSION} è l'ultima")
    assert Aggiornamenti.get().disponibile==""
    with pytest.raises(OSError):aggiornamenti.check(lambda url:(_ for _ in ()).throw(OSError("offline")))
    assert "non raggiungibile" in Aggiornamenti.get().esito
    with pytest.raises(ValueError):aggiornamenti.check(lambda url:"<html>")

def test_notice_for_administrator_and_titolare_and_old_windows(admin):
    from core.models import Membership,Tenant,User
    user,c=admin
    t=Tenant.objects.create(slug="bar",name="Bar")
    owner=User.objects.create_user("o@example.invalid","A-test-password-12345");Membership.objects.create(tenant=t,user=owner,role="owner")
    operator=User.objects.create_user("p@example.invalid","A-test-password-12345");Membership.objects.create(tenant=t,user=operator,role="operator")
    request=lambda ua="":RequestFactory().get("/",HTTP_USER_AGENT=ua)
    assert aggiornamenti.notice(request(),user) is None  # nothing newer
    Aggiornamenti.objects.update_or_create(pk=1,defaults={"disponibile":later()})
    n=aggiornamenti.notice(request(),user)
    assert n["action"]=="Aggiorna ora" and n["url"]==f"https://github.com/Giuseppe-sciarra/TD-Booking-Suite-per-Windows/releases/download/v{later()}/TD-Booking-Suite-{later()}.msi"
    assert n["more_url"].endswith(f"/releases/tag/v{later()}") and aggiornamenti.notice(request(),owner) and aggiornamenti.notice(request(),operator) is None
    # a postazione whose window is older than the server: the server's version, even when GitHub has nothing newer
    Aggiornamenti.objects.filter(pk=1).update(disponibile="")
    assert aggiornamenti.notice(request("Mozilla/5.0 Edg/130 TDBookingSuite/2.13.0"),user)["url"].endswith(f"TD-Booking-Suite-{settings.VERSION}.msi")
    assert aggiornamenti.notice(request(f"Mozilla/5.0 TDBookingSuite/{settings.VERSION}"),user) is None
    Aggiornamenti.objects.filter(pk=1).update(disponibile=later(),modo="spento")
    assert aggiornamenti.notice(request(),user) is None

def test_licence_page_proposes_and_switches_off(admin,monkeypatch):
    user,c=admin
    monkeypatch.setattr("tdwin.licensing.download",lambda url:json.dumps(release("v"+later())))
    page=c.post("/platform/licenza/",{"azione":"aggiornamenti","modo":"avvisa"},follow=True).content.decode()
    assert f"È disponibile la versione {later()}" in page and f"TD-Booking-Suite-{later()}.msi" in page and "Versione installata" in page
    page=c.post("/platform/licenza/",{"azione":"aggiornamenti","modo":"spento"},follow=True).content.decode()
    assert "Aggiornamenti spenti" in page and f"TD-Booking-Suite-{later()}.msi" not in page and Aggiornamenti.get().modo=="spento"
