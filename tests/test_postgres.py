"""The real thing, with PostgreSQL: the setup steps of setup-server.ps1, a backup with pg_dump, the HTTPS service,
and a restore over changed data. Runs when TDWIN_PG_ADMIN points to a PostgreSQL superuser, for example
TDWIN_PG_ADMIN=postgresql://postgres:password@127.0.0.1:55432/postgres"""
import json,os,socket,ssl,subprocess,sys,time,urllib.request
from urllib.parse import urlparse
import pytest
from tests.conftest import ROOT

ADMIN=os.environ.get("TDWIN_PG_ADMIN","")
pytestmark=[pytest.mark.skipif(not ADMIN,reason="serve TDWIN_PG_ADMIN"),pytest.mark.django_db(transaction=True)]

def free_port():
    s=socket.socket();s.bind(("127.0.0.1",0));port=s.getsockname()[1];s.close();return port

class Install:
    def __init__(self,data,port):
        self.data=data;self.port=port
        self.env={k:v for k,v in os.environ.items() if not k.startswith(("TDBS_","DJANGO_","PUBLIC_ORIGIN","DATABASE_URL"))}
        self.env.update(TDBS_DATA=str(data),DJANGO_SETTINGS_MODULE="tdwin.settings",PYTHONPATH=f"{ROOT}{os.pathsep}{ROOT/'suite'}")
    def run(self,*args,admin=False,check=True,**extra):
        env={**self.env,**extra}
        if admin:
            a=json.loads((self.data/"db-admin.json").read_text())
            env["DATABASE_URL"]=a["url"];env["APP_DB_PASSWORD"]=a["app_password"]
        done=subprocess.run([sys.executable,"-m","django",*args],env=env,capture_output=True,text=True,timeout=300,cwd=ROOT)
        if check and done.returncode:raise AssertionError(done.stdout+done.stderr)
        return done
    def shell(self,code,**kw):return self.run("shell","--no-imports","-c",code,**kw).stdout.strip()

def psql(sql,db="postgres"):
    u=urlparse(ADMIN)
    return subprocess.run(["psql","-h",u.hostname,"-p",str(u.port),"-U",u.username,"-d",db,"-tAc",sql],env={**os.environ,"PGPASSWORD":u.password},capture_output=True,text=True,check=True).stdout.strip()

@pytest.fixture
def install(tmp_path):
    from tdwin import setup_files
    u=urlparse(ADMIN);data=tmp_path/"ProgramData";port=free_port()
    config,admin=setup_files.prepare(data,origine=f"https://ufficio.test:{port}",porta=port,indirizzi="ufficio.test",pg_port=u.port)
    # the cluster belongs to the test: use its superuser password instead of the generated one
    a=json.loads((data/"db-admin.json").read_text());a["url"]=ADMIN.rsplit("/",1)[0]+"/tdwin_e2e";(data/"db-admin.json").write_text(json.dumps(a))
    c=json.loads((data/"config.json").read_text());c["env"]["DATABASE_URL"]=c["env"]["DATABASE_URL"].rsplit("/",1)[0]+"/tdwin_e2e";(data/"config.json").write_text(json.dumps(c))
    for db in psql("SELECT datname FROM pg_database WHERE datname LIKE 'tdwin_e2e%'").split():psql(f'DROP DATABASE "{db}"')
    psql("CREATE DATABASE tdwin_e2e")
    yield Install(data,port)
    for db in psql("SELECT datname FROM pg_database WHERE datname LIKE 'tdwin_e2e%'").split():
        psql(f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='{db}'");psql(f'DROP DATABASE "{db}"')

def test_setup_backup_https_and_restore(install,seller):
    data=install.data
    assert len(json.loads((data/"config.json").read_text())["env"]["DJANGO_SECRET_KEY"])>50
    again=__import__("tdwin.setup_files").setup_files.prepare(data,porta=install.port)[0]
    assert again["install_id"]==json.loads((data/"config.json").read_text())["install_id"]  # an update keeps the keys
    install.run("migrate","--noinput",admin=True)
    install.run("grant_runtime",admin=True)
    install.run("check_runtime",TDBS_EDITION="cloud")
    install.run("collectstatic","--noinput","-v0")
    install.shell("from core.models import User,Tenant;User.objects.create_superuser('admin@example.invalid','A-test-password-12345');Tenant.objects.create(slug='bar-roma',name='Bar Roma');Tenant.objects.create(slug='pizzeria',name='Pizzeria')")
    # two businesses: the service must take the edition from the licence before the core's runtime check
    # HTTPS: the service makes the certificates, checks the database and answers with the local authority
    server=subprocess.Popen([sys.executable,"-m","django","serve_https"],env=install.env,cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
    try:
        for _ in range(60):
            try:
                context=ssl.create_default_context(cafile=str(data/"certs"/"ca.crt"))
                page=urllib.request.urlopen(urllib.request.Request(f"https://localhost:{install.port}/login/"),context=context,timeout=5)
                break
            except (OSError,ssl.SSLError):time.sleep(0.5)
        else:raise AssertionError(server.stdout.read() if server.poll() is not None else "il server non risponde")
        assert page.status==200 and "Strict-Transport-Security" in page.headers
        blocked=urllib.request.urlopen(urllib.request.Request(f"https://localhost:{install.port}/platform/"),context=context,timeout=5)
        assert blocked.url.endswith("/login/")
    finally:
        server.terminate();server.wait(10)
    # backup with the real pg_dump
    out=install.shell("from tdwin import backup;backup.set_password('password-dei-backup-1');j=backup.run();print(j.esito,j.file,j.dettagli)")
    status,name=out.split()[:2]
    assert status=="ok",out
    file=data/"backup"/name
    # the data changes and the installation gets other keys: the restore brings back both
    before=json.loads((data/"config.json").read_text())
    install.shell("from core.models import Tenant;Tenant.objects.all().delete()")
    changed=json.loads((data/"config.json").read_text());changed["env"]["DJANGO_SECRET_KEY"]="x"*64;changed["install_id"]="altro";(data/"config.json").write_text(json.dumps(changed))
    (data/"pw").write_text("sbagliata-password")
    wrong=install.run("ripristina_backup",str(file),"--password-file",str(data/"pw"),admin=True,check=False)
    assert wrong.returncode and "errata" in wrong.stderr
    (data/"pw").write_text("password-dei-backup-1\n")
    done=install.run("ripristina_backup",str(file),"--password-file",str(data/"pw"),admin=True)
    assert "Ripristino completato" in done.stdout
    install.run("migrate","--noinput",admin=True);install.run("grant_runtime",admin=True);install.run("check_runtime",TDBS_EDITION="cloud")
    restored=json.loads((data/"config.json").read_text())
    assert restored["env"]["DJANGO_SECRET_KEY"]==before["env"]["DJANGO_SECRET_KEY"] and restored["install_id"]==before["install_id"]
    assert install.shell("from core.models import Tenant;print(sorted(Tenant.objects.values_list('name',flat=True)))")=="['Bar Roma', 'Pizzeria']"
    assert any(db.startswith("tdwin_e2e_prima_") for db in psql("SELECT datname FROM pg_database").split())
    assert not list((data/"tmp").iterdir())
