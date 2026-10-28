"""Backups: encrypted with the backup password, copied to folder, network, FTP, FTPS and SFTP, kept N at a time."""
import json,sys,tarfile,types
from datetime import datetime,time as clock,timedelta
from pathlib import Path
import pytest
from django.conf import settings
from django.utils import timezone
from tdwin import backup
from tdwin.models import ImpostazioniBackup,Destinazione,Esecuzione
from tests import servers

PASS="password-dei-backup-1"

@pytest.fixture
def fake_pg(monkeypatch):
    """pg_dump writes a small file: the real one runs in test_restore.py against PostgreSQL."""
    calls=[]
    def pg(args,timeout=3600):
        calls.append(args)
        if Path(args[0]).name=="pg_dump":Path(args[args.index("-f")+1]).write_bytes(b"PGDMP finto contenuto del database")
    monkeypatch.setattr(backup,"pg",pg)
    return calls

def destination(**fields):
    secret=fields.pop("secret","")
    d=Destinazione(nome=fields.pop("nome","Destinazione"),**fields)
    backup.seal_secret(d,secret);d.save()
    return d

def opened(path):
    """The members of a backup file, opened with the password."""
    key=backup.derive(PASS,backup.read_salt(path))
    plain=path.with_name(path.name+".tar");backup.decrypt(path,plain,key)
    with tarfile.open(plain) as tar:return {m.name:tar.extractfile(m).read() for m in tar.getmembers()}

def test_encryption_needs_the_right_password_and_every_byte(tmp_path):
    source=tmp_path/"in.bin";source.write_bytes(b"dati "*300000)
    salt=b"s"*16;key=backup.derive(PASS,salt)
    backup.encrypt(source,tmp_path/"out.tdbk",key,salt)
    assert b"dati" not in (tmp_path/"out.tdbk").read_bytes() and backup.read_salt(tmp_path/"out.tdbk")==salt
    backup.decrypt(tmp_path/"out.tdbk",tmp_path/"back.bin",key)
    assert (tmp_path/"back.bin").read_bytes()==source.read_bytes()
    with pytest.raises(ValueError,match="errata"):backup.decrypt(tmp_path/"out.tdbk",tmp_path/"wrong.bin",backup.derive("altra-password-12",salt))
    damaged=bytearray((tmp_path/"out.tdbk").read_bytes());damaged[5000]^=1;(tmp_path/"bad.tdbk").write_bytes(damaged)
    with pytest.raises(ValueError,match="danneggiato"):backup.decrypt(tmp_path/"bad.tdbk",tmp_path/"bad.bin",key)
    assert not (tmp_path/"wrong.bin").exists() and not (tmp_path/"bad.bin").exists() and not list(tmp_path.glob("*.part"))
    (tmp_path/"other").write_bytes(b"ciao")
    with pytest.raises(ValueError,match="Non è un backup"):backup.read_salt(tmp_path/"other")

def test_backup_holds_database_keys_licence_and_keeps_n_copies(fake_pg,tmp_path,seller):
    from tdwin import licensing
    licensing.install(seller())
    row=ImpostazioniBackup.get()
    job=backup.run()
    assert job.esito=="errore" and "password dei backup" in job.dettagli
    backup.set_password(PASS)
    row=ImpostazioniBackup.get();row.conserva_locali=2;row.save()
    disk=destination(nome="Disco",tipo="cartella",percorso=str(tmp_path/"E"/"Backup"),conserva=3)
    names=[]
    for n in range(4):
        job=backup.run(manual=True)
        assert job.esito=="ok",job.dettagli
        names.append(job.file);Esecuzione.objects.filter(pk=job.pk).update(inizio=job.inizio)
        Path(backup.local_folder()/job.file).rename(backup.local_folder()/f"{backup.prefix()}-2026010{n}-000000.tdbk")
        Path(disk.percorso,job.file).rename(Path(disk.percorso,f"{backup.prefix()}-2026010{n}-000000.tdbk"))
    assert sorted(p.name for p in backup.local_folder().glob("*.tdbk"))==[f"{backup.prefix()}-20260102-000000.tdbk",f"{backup.prefix()}-20260103-000000.tdbk"]
    assert len(list(Path(disk.percorso).glob("*.tdbk")))==3
    members=opened(backup.local_folder()/f"{backup.prefix()}-20260103-000000.tdbk")
    assert members["database.dump"].startswith(b"PGDMP") and members["licenza.txt"].startswith(b"TDBS1.")
    secrets=json.loads(members["segreti.json"])
    assert secrets["DJANGO_SECRET_KEY"]==settings.SECRET_KEY and json.loads(secrets["TDBS_KEYS"])==settings.KEYS
    assert json.loads(members["info.json"])["install_id"]==settings.TDWIN_CONFIG["install_id"]
    assert not list(backup.work_root().iterdir())  # nothing unencrypted left behind

def test_one_unreachable_destination_does_not_stop_the_others(fake_pg,tmp_path):
    backup.set_password(PASS)
    destination(nome="NAS spento",tipo="ftp",host="127.0.0.1",porta=1,percorso="/x")
    good=destination(nome="Disco",tipo="cartella",percorso=str(tmp_path/"ok"))
    job=backup.run()
    assert job.esito=="parziale" and "NAS spento: non riuscito, collegamento rifiutato" in job.dettagli and "Disco: copiato" in job.dettagli
    assert list(Path(good.percorso).glob("*.tdbk"))

def test_ftp_and_ftps_with_pinned_certificate(fake_pg,tmp_path):
    backup.set_password(PASS)
    (tmp_path/"ftp").mkdir();server,port=servers.ftp(tmp_path/"ftp")
    destination(nome="FTP",tipo="ftp",host="127.0.0.1",porta=port,utente="bk",secret="segreta-ftp",percorso="/backup/booking",conserva=1)
    cert=servers.self_signed(tmp_path/"cert.pem");(tmp_path/"ftps").mkdir();tls,tls_port=servers.ftp(tmp_path/"ftps",tls=True,certfile=cert)
    secure=destination(nome="FTPS",tipo="ftps",host="127.0.0.1",porta=tls_port,utente="bk",secret="segreta-ftp",percorso="booking")
    for n in range(2):
        job=backup.run();assert job.esito=="ok",job.dettagli
        old=f"{backup.prefix()}-2020010{n}-000000.tdbk"  # the next run, in the same second, gets another name
        (backup.local_folder()/job.file).rename(backup.local_folder()/old)
        for root in ("ftp/backup/booking","ftps/booking"):
            for f in (tmp_path/root).glob("*.tdbk"):
                if f.name==job.file:f.rename(f.with_name(old))
    assert len(list((tmp_path/"ftp/backup/booking").glob("*.tdbk")))==1 and len(list((tmp_path/"ftps/booking").glob("*.tdbk")))==2
    secure.refresh_from_db();assert secure.impronta.startswith("sha256 ")
    server.close_all();tls.close_all()
    other=servers.self_signed(tmp_path/"other.pem");tls2,port2=servers.ftp(tmp_path/"ftps",tls=True,certfile=other)
    Destinazione.objects.filter(pk=secure.pk).update(porta=port2);secure.refresh_from_db()
    with pytest.raises(ValueError,match="un altro certificato"):backup.check(secure)
    tls2.close_all()

def test_sftp_with_pinned_host_key(fake_pg,tmp_path):
    import paramiko
    backup.set_password(PASS);root=tmp_path/"sftp";root.mkdir()
    key=paramiko.ECDSAKey.generate();listener,port=servers.sftp(root,key)
    d=destination(nome="SFTP",tipo="sftp",host="127.0.0.1",porta=port,utente="bk",secret="segreta-sftp",percorso="backup/booking")
    job=backup.run();assert job.esito=="ok",job.dettagli
    assert (root/"backup/booking"/job.file).exists()
    d.refresh_from_db();assert d.impronta.startswith("ecdsa-sha2-nistp256 ")
    wrong=destination(nome="SFTP2",tipo="sftp",host="127.0.0.1",porta=port,utente="bk",secret="sbagliata",percorso="x")
    with pytest.raises(Exception):backup.check(wrong)
    listener.close()
    listener2,port2=servers.sftp(root)  # same address, another key
    Destinazione.objects.filter(pk=d.pk).update(porta=port2);d.refresh_from_db()
    with pytest.raises(ValueError,match="un'altra chiave"):backup.check(d)
    listener2.close()

def test_network_folder_uses_its_own_credentials(fake_pg,tmp_path,monkeypatch):
    """smbclient replaced by the local disk: checks the UNC paths and the session the program opens."""
    disk=tmp_path/"nas";sessions=[]
    def real(path):
        assert path.startswith("\\\\nas\\backup");return disk/path[2:].replace("\\","/")
    fake=types.SimpleNamespace(
        register_session=lambda server,**kw:sessions.append((server,kw["username"],kw["password"])),
        makedirs=lambda path,exist_ok:real(path).mkdir(parents=True,exist_ok=exist_ok),
        open_file=lambda path,mode:open(real(path),mode),
        replace=lambda a,b:real(a).replace(real(b)),
        listdir=lambda path:[p.name for p in real(path).iterdir()],
        remove=lambda path:real(path).unlink())
    monkeypatch.setitem(sys.modules,"smbclient",fake)
    backup.set_password(PASS)
    destination(nome="NAS",tipo="rete",percorso="//nas/backup/booking/",utente="ufficio",secret="segreta-nas",conserva=2)
    for n in range(3):
        job=backup.run();assert job.esito=="ok",job.dettagli
        folder=disk/"nas/backup/booking";(folder/job.file).rename(folder/f"{backup.prefix()}-2019010{n}-000000.tdbk")
        (backup.local_folder()/job.file).unlink()
    assert sessions[0]==("nas","ufficio","segreta-nas") and len(list((disk/"nas/backup/booking").glob("*.tdbk")))==2

def test_worker_runs_scheduled_requested_and_test_jobs(fake_pg,tmp_path):
    backup.set_password(PASS)
    row=ImpostazioniBackup.get();row.attivo=True;row.ora=clock(1,30);row.save()
    tz=timezone.get_current_timezone()
    early=datetime(2026,11,2,1,0,tzinfo=tz);late=datetime(2026,11,2,8,0,tzinfo=tz)
    assert backup.tick(early) is None
    assert backup.tick(late).esito=="ok"
    assert backup.tick(late+timedelta(hours=2)) is None  # once a day
    ImpostazioniBackup.objects.update(richiesto=timezone.now())
    job=backup.tick(late+timedelta(hours=3))
    assert job.manuale and ImpostazioniBackup.get().richiesto is None
    assert backup.tick(datetime(2026,11,3,1,31,tzinfo=tz)).esito=="ok"
    d=destination(nome="Disco",tipo="cartella",percorso=str(tmp_path/"d"),prova_richiesta=timezone.now())
    (tmp_path/"file").write_text("un file, non una cartella")
    missing=destination(nome="Disco staccato",tipo="cartella",percorso=str(tmp_path/"file"/"sotto"),prova_richiesta=timezone.now())
    backup.tick(early)
    d.refresh_from_db();missing.refresh_from_db()
    assert d.ultimo_esito=="Collegamento riuscito" and d.prova_richiesta is None and not list(Path(d.percorso).iterdir())
    assert missing.ultimo_esito.startswith("Non riuscito")

def test_backup_page(admin,trial,fake_pg,tmp_path):
    user,c=admin
    page=c.get("/platform/backup/").content.decode()
    assert "Da impostare" in page and "Backup non attivo" not in page
    assert "Backup non attivo" in c.get("/platform/").content.decode()
    assert "Prima imposta la password" in c.post("/platform/backup/",{"azione":"impostazioni","attivo":"on","ora":"02:00","conserva_locali":"5"},follow=True).content.decode()
    assert "non coincidono" in c.post("/platform/backup/",{"azione":"password","password":PASS,"password2":"x"},follow=True).content.decode()
    c.post("/platform/backup/",{"azione":"password","password":PASS,"password2":PASS})
    c.post("/platform/backup/",{"azione":"impostazioni","attivo":"on","ora":"02:00","conserva_locali":"5"})
    row=ImpostazioniBackup.get()
    assert row.attivo and row.ora==clock(2) and row.conserva_locali==5 and row.chiave
    bad=c.post("/platform/backup/",{"azione":"destinazione","nome":"Disco","tipo":"cartella","percorso":"Backup","conserva":"3"}).content.decode()
    assert "percorso completo" in bad and not Destinazione.objects.exists()
    assert "Serve l&#x27;indirizzo del server" in c.post("/platform/backup/",{"azione":"destinazione","nome":"F","tipo":"sftp","percorso":"/b","conserva":"3"}).content.decode()
    c.post("/platform/backup/",{"azione":"destinazione","nome":"FTP ufficio","tipo":"ftps","host":"ftp.example.invalid","utente":"u","password_nuova":"segreta","percorso":"/b","conserva":"3","attiva":"on"})
    d=Destinazione.objects.get()
    assert d.prova_richiesta and backup.secret(d)=="segreta" and "segreta" not in d.password
    c.post(f"/platform/backup/?modifica={d.pk}",{"azione":"destinazione","id":str(d.pk),"nome":"FTP ufficio","tipo":"ftps","host":"ftp.example.invalid","utente":"u","password_nuova":"","percorso":"/c","conserva":"3","attiva":"on"})
    d.refresh_from_db();assert d.percorso=="/c" and backup.secret(d)=="segreta"
    c.post("/platform/backup/",{"azione":"esegui"})
    job=backup.tick()
    assert job.manuale and job.esito=="parziale"
    page=c.get("/platform/backup/").content.decode()
    assert job.file in page and "In parte" in page and "Ultimo backup non riuscito" in c.get("/platform/").content.decode()
    download=c.get(f"/platform/backup/scarica/{job.file}")
    assert download.status_code==200 and b"".join(download.streaming_content).startswith(backup.MAGIC)
    assert c.get("/platform/backup/scarica/..%2Fconfig.json").status_code==404
    c.post("/platform/backup/",{"azione":"elimina","id":str(d.pk)})
    assert not Destinazione.objects.exists()

def test_backup_page_is_for_the_administrator(trial):
    from core.models import User,Tenant,Membership
    from tests.conftest import login
    User.objects.create_superuser("admin@example.invalid","A-test-password-12345")
    t=Tenant.objects.create(slug="bar",name="Bar");owner=User.objects.create_user("o@example.invalid","A-test-password-12345")
    Membership.objects.create(tenant=t,user=owner,role="owner")
    assert login(owner).get("/platform/backup/").status_code==403

def test_worker_start_empties_the_work_folder_but_keeps_it():
    root=backup.work_root();(root/"vecchio").mkdir();(root/"vecchio"/"database.dump").write_text("in chiaro");(root/"x.tar").write_text("x")
    backup.clean_work()
    assert root.is_dir() and not list(root.iterdir())

def test_other_installations_copies_and_cut_files_are_left_alone(fake_pg,tmp_path,monkeypatch):
    backup.set_password(PASS)
    shared=tmp_path/"nas";shared.mkdir()
    theirs=[shared/f"tdbs-{'b'*16}-2020010{n}-000000.tdbk" for n in range(4)]
    for f in theirs:f.write_bytes(b"di un altro ufficio")
    destination(nome="NAS condiviso",tipo="cartella",percorso=str(shared),conserva=1)
    for n in range(3):
        job=backup.run();assert job.esito=="ok",job.dettagli
        (shared/job.file).rename(shared/f"{backup.prefix()}-2021010{n}-000000.tdbk")
        (backup.local_folder()/job.file).unlink()
    mine=sorted(p.name for p in shared.glob(f"{backup.prefix()}-*.tdbk"))
    assert len(mine)==1 and all(f.exists() for f in theirs)
    def broken(source,dest,key,salt):
        Path(dest).write_bytes(b"mezzo file");raise OSError(28,"spazio esaurito")
    monkeypatch.setattr(backup,"encrypt",broken)
    job=backup.run()
    assert job.esito=="errore" and "spazio esaurito" in job.dettagli
    assert not list(backup.local_folder().glob("*.tdbk")) and not list(backup.local_folder().glob("*.part"))

def test_another_server_clears_the_pin(admin,trial):
    user,c=admin
    d=destination(nome="SFTP",tipo="sftp",host="vecchio.example.com",utente="u",secret="x",percorso="/b",impronta="ssh-ed25519 VECCHIA")
    form={"azione":"destinazione","id":str(d.pk),"nome":"SFTP","tipo":"sftp","utente":"u","percorso":"/b","conserva":"3","attiva":"on"}
    c.post(f"/platform/backup/?modifica={d.pk}",{**form,"host":"vecchio.example.com"})
    d.refresh_from_db();assert d.impronta=="ssh-ed25519 VECCHIA"
    c.post(f"/platform/backup/?modifica={d.pk}",{**form,"host":"nuovo.example.com"})
    d.refresh_from_db();assert d.host=="nuovo.example.com" and d.impronta==""

def test_ftps_recognised_once_is_never_pinned_later(monkeypatch):
    import ssl
    class Fake:
        def __init__(self):self.sock=types.SimpleNamespace(getpeercert=lambda binary_form=True:b"certificato-falso")
        def login(self,user,password):Fake.sent=password
        def prot_p(self):pass
        def cwd(self,part):pass
    verified={"ok":True}
    def ftps(d,verify):
        if verify and not verified["ok"]:raise ssl.SSLCertVerificationError("non riconosciuto")
        return Fake()
    monkeypatch.setattr(backup,"ftps",ftps)
    d=Destinazione.objects.create(nome="NAS",tipo="ftps",host="nas.example.invalid",percorso="/backup")
    backup.seal_secret(d,"password-del-nas");d.save()
    backup.FTP(d);d.refresh_from_db()
    assert d.impronta==backup.VERIFIED
    # later the certificate is no longer recognised: someone in between gets nothing
    verified["ok"]=False;Fake.sent=None
    with pytest.raises(ValueError,match="non è più riconosciuto"):backup.FTP(d)
    assert Fake.sent is None
    # a NAS with its own certificate from the start is pinned at the first connection, as before
    other=Destinazione.objects.create(nome="NAS 2",tipo="ftps",host="nas2.example.invalid",percorso="/")
    backup.FTP(other);other.refresh_from_db()
    assert other.impronta.startswith("sha256 ")
