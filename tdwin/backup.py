"""Backups: the database, the keys that open it, the licence and the installation id, in one file encrypted with
the backup password. Without the password the file cannot be opened, by anyone.

The worker writes the file in the data folder every night (or right away with "Esegui ora") and copies it to each
active destination: a folder or external disk, a network folder (NAS), FTP, FTPS or SFTP. Each keeps the last N copies."""
import ftplib,hashlib,io,json,logging,os,shutil,socket,ssl,subprocess,tarfile,tempfile,time
from fnmatch import fnmatch
from pathlib import Path,PurePosixPath
from django.conf import settings
from django.utils import timezone
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers import Cipher,algorithms,modes
from cryptography.hazmat.primitives.kdf.scrypt import Scrypt
from core.crypto import protect,reveal,b64,unb64
from .models import ImpostazioniBackup,Destinazione,Esecuzione

log=logging.getLogger("tdwin.backup")
MAGIC=b"TDBS-WINBACKUP-1\n"
PATTERN="tdbs-*.tdbk"
CHUNK=1<<20
NO_WINDOW=getattr(subprocess,"CREATE_NO_WINDOW",0)

def local_folder():return settings.TDWIN_DATA/"backup"
def work_root():
    root=settings.TDWIN_DATA/"tmp";root.mkdir(parents=True,exist_ok=True)
    return root
def clean_work():
    """Unencrypted leftovers of a backup or restore cut short: the worker removes them when it starts.
    The folder itself stays: on Windows the worker may write inside it but not create it again."""
    root=settings.TDWIN_DATA/"tmp"
    for item in root.iterdir() if root.is_dir() else ():
        if item.is_dir():shutil.rmtree(item,ignore_errors=True)
        else:item.unlink(missing_ok=True)

# encryption: header, nonce, AES-256-GCM stream, tag; the key comes from the backup password and the header's salt
def derive(password,salt):return Scrypt(salt=salt,length=32,n=2**15,r=8,p=1).derive(password.encode())

def set_password(password):
    if len(password)<12:raise ValueError("La password dei backup deve avere almeno 12 caratteri.")
    salt=os.urandom(16);row=ImpostazioniBackup.get()
    row.sale=b64(salt);row.chiave=protect(b64(derive(password,salt)),"backup-key");row.password_impostata=timezone.now()
    row.save(update_fields=["sale","chiave","password_impostata"])

def encrypt(source,dest,key,salt):
    header=MAGIC+salt;nonce=os.urandom(12)
    engine=Cipher(algorithms.AES(key),modes.GCM(nonce)).encryptor();engine.authenticate_additional_data(header)
    with open(source,"rb") as src,open(dest,"xb") as out:
        out.write(header+nonce)
        while chunk:=src.read(CHUNK):out.write(engine.update(chunk))
        out.write(engine.finalize()+engine.tag)

def read_salt(path):
    with open(path,"rb") as f:head=f.read(len(MAGIC)+16)
    if not head.startswith(MAGIC) or len(head)<len(MAGIC)+16:raise ValueError("Non è un backup di TD Booking Suite per Windows.")
    return head[len(MAGIC):]

def decrypt(source,dest,key):
    """Writes dest only when the password and every byte of the file are right."""
    size=os.path.getsize(source);start=len(MAGIC)+16
    if size<start+12+16:raise ValueError("Backup troncato o non valido.")
    temporary=Path(str(dest)+".part")
    with open(source,"rb") as src:
        header=src.read(start);nonce=src.read(12)
        src.seek(-16,2);tag=src.read(16);src.seek(start+12)
        engine=Cipher(algorithms.AES(key),modes.GCM(nonce,tag)).decryptor();engine.authenticate_additional_data(header)
        remaining=size-start-12-16
        try:
            with open(temporary,"wb") as out:
                while remaining:
                    chunk=src.read(min(CHUNK,remaining));remaining-=len(chunk)
                    out.write(engine.update(chunk))
                engine.finalize()
        except InvalidTag:
            temporary.unlink(missing_ok=True)
            raise ValueError("Password dei backup errata, oppure il file è danneggiato.") from None
    os.replace(temporary,dest)

# the database, with the worker's superuser: the web service cannot read db-admin.json
def admin_db():
    import dj_database_url
    return dj_database_url.parse(json.loads((settings.TDWIN_DATA/"db-admin.json").read_text("utf-8"))["url"])
def pg_env(db):
    return {**os.environ,"PGPASSWORD":db["PASSWORD"],"PGHOST":db["HOST"] or "127.0.0.1","PGPORT":str(db["PORT"] or 5432),"PGUSER":db["USER"]}
def tool(name):
    folder=settings.TDWIN_CONFIG.get("pg_bin")
    return str(Path(folder)/name) if folder else name
def pg(args,timeout=3600):
    done=subprocess.run(args,env=pg_env(admin_db()),capture_output=True,text=True,timeout=timeout,creationflags=NO_WINDOW)
    if done.returncode:raise RuntimeError(f"{Path(args[0]).name}: "+(done.stderr.strip().splitlines() or ["errore"])[-1][:300])

def bundle(work,dest):
    from . import licensing
    info={"versione":settings.VERSION,"creato":timezone.now().isoformat(),"pc":socket.gethostname(),"install_id":settings.TDWIN_CONFIG["install_id"]}
    secrets={"DJANGO_SECRET_KEY":settings.SECRET_KEY,"TDBS_KEYS":json.dumps(settings.KEYS,separators=(",",":")),"TDBS_ACTIVE_KEY":settings.ACTIVE_KEY}
    with tarfile.open(dest,"w") as tar:
        tar.add(work/"database.dump","database.dump")
        for name,data in (("info.json",info),("segreti.json",secrets)):
            raw=json.dumps(data,ensure_ascii=False,indent=1).encode()
            entry=tarfile.TarInfo(name);entry.size=len(raw);entry.mtime=int(time.time())
            tar.addfile(entry,io.BytesIO(raw))
        if licensing.path().exists():tar.add(licensing.path(),"licenza.txt")

def describe(ex):
    text=str(ex) or ex.__class__.__name__
    if isinstance(ex,socket.timeout):text="il server non risponde"
    elif isinstance(ex,ConnectionRefusedError):text="collegamento rifiutato: controlla indirizzo e porta"
    elif isinstance(ex,socket.gaierror):text="indirizzo del server sconosciuto"
    elif isinstance(ex,ftplib.error_perm):text="il server FTP rifiuta: "+text
    return text[:300]

# destinations
def secret(d):return reveal(d.password,"backup-dest:"+str(d.pk)) if d.password else ""
def seal_secret(d,password):d.password=protect(password,"backup-dest:"+str(d.pk)) if password else ""

def pin(d,seen):
    """The first connection records the server's key or certificate; later ones must show the same."""
    if not d.impronta:
        d.impronta=seen;Destinazione.objects.filter(pk=d.pk).update(impronta=seen)
    elif d.impronta!=seen:
        raise ValueError("il server ha un'altra chiave o un altro certificato rispetto al primo collegamento. Se lo hai cambiato tu, azzera l'impronta della destinazione")

class Folder:
    def __init__(self,d):
        self.root=Path(d.percorso)
        self.root.mkdir(parents=True,exist_ok=True)
    def put(self,local,name):
        shutil.copyfile(local,self.root/(name+".part"));os.replace(self.root/(name+".part"),self.root/name)
    def list(self):return [p.name for p in self.root.glob(PATTERN)]
    def remove(self,name):(self.root/name).unlink()
    def close(self):pass

def unc(path):
    path=path.strip().replace("/","\\")
    return "\\\\"+path.lstrip("\\").rstrip("\\")

class Network:
    """A network folder \\\\nas\\cartella with its own user, without mapping a drive in Windows."""
    def __init__(self,d):
        import smbclient
        self.smb=smbclient;self.root=unc(d.percorso)
        server=self.root[2:].split("\\")[0]
        if d.utente:smbclient.register_session(server,username=d.utente,password=secret(d),port=d.porta or 445,connection_timeout=30)
        smbclient.makedirs(self.root,exist_ok=True)
    def put(self,local,name):
        with open(local,"rb") as src,self.smb.open_file(self.root+"\\"+name+".part",mode="wb") as out:shutil.copyfileobj(src,out,CHUNK)
        self.smb.replace(self.root+"\\"+name+".part",self.root+"\\"+name)
    def list(self):return [n for n in self.smb.listdir(self.root) if fnmatch(n,PATTERN)]
    def remove(self,name):self.smb.remove(self.root+"\\"+name)
    def close(self):pass

class TLSReuse(ftplib.FTP_TLS):
    """Many NAS and FTP servers want the data connection to reuse the TLS session of the control one."""
    def ntransfercmd(self,cmd,rest=None):
        conn,size=ftplib.FTP.ntransfercmd(self,cmd,rest)
        if self._prot_p:conn=self.context.wrap_socket(conn,server_hostname=self.host,session=self.sock.session)
        return conn,size

class FTP:
    def __init__(self,d):
        tls=d.tipo=="ftps"
        if tls:
            context=ssl.create_default_context();context.check_hostname=False;context.verify_mode=ssl.CERT_NONE  # pinned below instead
            self.ftp=TLSReuse(context=context)
        else:self.ftp=ftplib.FTP()
        self.ftp.connect(d.host,d.porta or 21,timeout=60)
        if tls:
            self.ftp.auth()
            pin(d,"sha256 "+hashlib.sha256(self.ftp.sock.getpeercert(binary_form=True)).hexdigest())
        self.ftp.login(d.utente or "anonymous",secret(d))
        if tls:self.ftp.prot_p()
        for part in PurePosixPath(d.percorso or "/").parts:
            try:self.ftp.cwd(part)
            except ftplib.error_perm:self.ftp.mkd(part);self.ftp.cwd(part)
    def put(self,local,name):
        with open(local,"rb") as f:self.ftp.storbinary(f"STOR {name}.part",f,CHUNK)
        self.ftp.rename(name+".part",name)
    def list(self):
        try:names=self.ftp.nlst()
        except ftplib.error_perm:names=[]  # some servers answer 550 for an empty folder
        return [PurePosixPath(n).name for n in names if fnmatch(PurePosixPath(n).name,PATTERN)]
    def remove(self,name):self.ftp.delete(name)
    def close(self):
        try:self.ftp.quit()
        except (OSError,EOFError,ftplib.Error):self.ftp.close()

class SFTP:
    def __init__(self,d):
        import paramiko
        # shortcut: password login only, add key files when a customer's server needs them
        self.transport=paramiko.Transport(socket.create_connection((d.host,d.porta or 22),timeout=30))
        self.transport.banner_timeout=30
        self.transport.start_client(timeout=30)
        key=self.transport.get_remote_server_key()
        pin(d,key.get_name()+" "+b64(hashlib.sha256(key.asbytes()).digest()))
        self.transport.auth_password(d.utente,secret(d))
        self.sftp=paramiko.SFTPClient.from_transport(self.transport)
        self.root=d.percorso or "."
        current=""
        for part in PurePosixPath(self.root).parts:
            current=str(PurePosixPath(current)/part) if current else part
            try:self.sftp.stat(current)
            except IOError:self.sftp.mkdir(current)
    def _path(self,name):return str(PurePosixPath(self.root)/name)
    def put(self,local,name):
        self.sftp.put(str(local),self._path(name+".part"))
        try:self.sftp.posix_rename(self._path(name+".part"),self._path(name))
        except IOError:self.sftp.rename(self._path(name+".part"),self._path(name))
    def list(self):return [n for n in self.sftp.listdir(self.root) if fnmatch(n,PATTERN)]
    def remove(self,name):self.sftp.remove(self._path(name))
    def close(self):self.transport.close()

KINDS={"cartella":Folder,"rete":Network,"ftp":FTP,"ftps":FTP,"sftp":SFTP}

def send(d,local):
    target=KINDS[d.tipo](d)
    try:
        target.put(local,local.name)
        if d.conserva:
            for name in sorted(target.list())[:-d.conserva]:target.remove(name)
    finally:target.close()

def check(d):
    """Write, list and delete a small file: the same steps of a real backup."""
    with tempfile.TemporaryDirectory(dir=work_root()) as work:
        probe=Path(work)/"prova-collegamento.txt";probe.write_text("TD Booking Suite: prova di scrittura del backup\n")
        target=KINDS[d.tipo](d)
        try:
            target.put(probe,probe.name)
            target.remove(probe.name)
        finally:target.close()

def prune(folder,keep):
    for old in sorted(folder.glob(PATTERN))[:-keep] if keep else []:old.unlink()

def run(manual=False):
    row=ImpostazioniBackup.get()
    job=Esecuzione.objects.create(inizio=timezone.now(),manuale=manual)
    lines=[];outcome="ok"
    try:
        if not row.chiave:raise ValueError("Manca la password dei backup: impostala nella pagina Backup.")
        salt,key=unb64(row.sale),unb64(reveal(row.chiave,"backup-key"))
        folder=local_folder();folder.mkdir(parents=True,exist_ok=True)
        while (folder/(name:=f"tdbs-{timezone.localtime():%Y%m%d-%H%M%S}.tdbk")).exists():time.sleep(1)  # two runs in the same second
        with tempfile.TemporaryDirectory(dir=work_root()) as work:
            work=Path(work)
            pg([tool("pg_dump"),"-Fc","--no-password","-d",settings.DATABASES["default"]["NAME"],"-f",str(work/"database.dump")])
            bundle(work,work/"bundle.tar")
            encrypt(work/"bundle.tar",folder/name,key,salt)
        job.file=name;job.dimensione=(folder/name).stat().st_size
        prune(folder,row.conserva_locali)
        lines.append(f"Copia sul PC server: {folder/name}")
        for d in Destinazione.objects.filter(attiva=True).order_by("creata"):
            try:send(d,folder/name);lines.append(f"{d.nome}: copiato")
            except Exception as ex:  # one unreachable destination must not stop the others
                outcome="parziale";lines.append(f"{d.nome}: non riuscito, {describe(ex)}")
    except Exception as ex:
        log.exception("backup non riuscito")
        outcome="errore";lines.append(describe(ex))
    job.esito=outcome;job.fine=timezone.now();job.dettagli="\n".join(lines)
    job.save(update_fields=["esito","fine","dettagli","file","dimensione"])
    return job

def tick(now=None):
    """One pass of the worker: destination tests, a backup asked from the page, the nightly one."""
    for d in Destinazione.objects.filter(prova_richiesta__isnull=False):
        try:check(d);result="Collegamento riuscito"
        except Exception as ex:result="Non riuscito: "+describe(ex)
        Destinazione.objects.filter(pk=d.pk).update(prova_richiesta=None,ultimo_esito=result,ultimo_controllo=timezone.now())
    row=ImpostazioniBackup.get();local=timezone.localtime(now or timezone.now())
    # a PC that was off at the set time makes the backup as soon as it is on again, the same day
    due=row.attivo and local.time()>=row.ora and row.ultimo_giorno!=local.date()
    if row.richiesto or due:
        ImpostazioniBackup.objects.filter(pk=1).update(richiesto=None,**({"ultimo_giorno":local.date()} if due else {}))
        return run(manual=bool(row.richiesto))

def loop():
    from django.db import close_old_connections
    from . import licensing
    while True:
        try:
            close_old_connections();licensing.apply();tick()
        except Exception:log.exception("giro dei backup non riuscito")
        time.sleep(15)
