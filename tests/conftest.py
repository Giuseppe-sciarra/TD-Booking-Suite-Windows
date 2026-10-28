import importlib.util,secrets,shutil,time
from pathlib import Path
import pytest
from django.conf import settings
from django.test import Client

ROOT=Path(__file__).resolve().parent.parent
REMOTE="203.0.113.20"  # TEST-NET-3: never one of this PC's addresses
PASSWORD="A-test-password-12345"

def load_tool():
    spec=importlib.util.spec_from_file_location("td_licenze",ROOT/"licenze"/"td_licenze.py")
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    return module

@pytest.fixture(autouse=True)
def fresh_state(db):
    """Every test starts from an empty data folder and no cached licence state."""
    from tdwin import licensing,middleware,rete
    for name in ("licenza","backup","tmp","certs"):shutil.rmtree(settings.TDWIN_DATA/name,ignore_errors=True)
    licensing._file.update(stamp=None,value=(None,""));licensing._applied.update(at=0.0,value=None);middleware._ready.clear();middleware._allowed.update(at=-1e9,limit=None,value=None)
    saved={k:getattr(settings,k) for k in ("EDITION","TENANT_LIMIT","EDITION_LABEL","ALLOWED_HOSTS","CSRF_TRUSTED_ORIGINS","PUBLIC_ORIGIN")}
    rete._applied.update(at=-1e9,value=None);rete._base.clear();rete._served.update(stamp=None);rete.ADAPTERS.clear()
    yield
    licensing._applied.update(at=0.0,value=None);rete._applied.update(at=-1e9,value=None);rete._base.clear();rete.ADAPTERS.clear()
    for k,v in saved.items():setattr(settings,k,v)

@pytest.fixture
def seller(settings,tmp_path):
    """The seller's key pair, made by the real td_licenze.py; returns a function that signs a licence."""
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
    tool=load_tool();key=Ed25519PrivateKey.generate()
    settings.TDWIN_LICENSE_KEY=tool.public_text(key)
    def make(**fields):
        data={"id":"TD-2026-0001","emessa":"2026-10-28","intestatario":"Bar Roma","attivita":1,"postazioni":2,"installazione":"","scadenza":""}
        data.update(fields)
        return tool.sign(key,data)
    make.key=key;make.tool=tool
    return make

def login(user,client=None):
    client=client or Client()
    client.force_login(user,backend="core.identity.EmailBackend")
    s=client.session;s["mfa"]=True;s["auth_version"]=user.auth_version;s["last_seen"]=time.time();s["verified_at"]=time.time();s.save()
    return client

@pytest.fixture
def admin():
    from core.models import User
    user=User.objects.create_superuser(secrets.token_hex(4)+"@example.invalid",PASSWORD)
    return user,login(user)

@pytest.fixture
def trial():
    from django.utils import timezone
    from tdwin.models import Prova
    return Prova.objects.create(pk=1,attivata=timezone.now())
