"""Restore a .tdbk backup: lanciato da windows\\ripristina-backup.ps1 con i servizi fermi, come amministratore.

The backup goes into a side database first; only when pg_restore succeeds the current database is renamed
(kept, never dropped) and the restored one takes its name. Then config.json gets the keys of the backup."""
import getpass,json,shutil,tarfile,tempfile
from pathlib import Path
from django.conf import settings
from django.core.management.base import BaseCommand,CommandError
from django.utils import timezone
class Command(BaseCommand):
    help="Ripristina un backup .tdbk, con i servizi fermi."
    requires_system_checks=[]
    def add_arguments(self,p):
        p.add_argument("file");p.add_argument("--password-file",help="file con la password dei backup, al posto della domanda")
    def handle(self,*a,**kw):
        import psycopg
        from psycopg import sql
        from tdwin import backup,licensing
        source=Path(kw["file"])
        if not source.is_file():raise CommandError(f"File non trovato: {source}")
        password=Path(kw["password_file"]).read_text("utf-8").strip() if kw["password_file"] else getpass.getpass("Password dei backup: ")
        try:key=backup.derive(password,backup.read_salt(source))
        except ValueError as ex:raise CommandError(str(ex)) from None
        stamp=timezone.localtime().strftime("%Y%m%d_%H%M%S")
        with tempfile.TemporaryDirectory(dir=backup.work_root()) as work:
            work=Path(work)
            try:backup.decrypt(source,work/"bundle.tar",key)
            except ValueError as ex:raise CommandError(str(ex)) from None
            with tarfile.open(work/"bundle.tar") as tar:tar.extractall(work,filter="data")
            info=json.loads((work/"info.json").read_text("utf-8"));secrets=json.loads((work/"segreti.json").read_text("utf-8"))
            self.stdout.write(f"Backup del {info['creato'][:16].replace('T',' ')} dal PC {info['pc']}, versione {info['versione']}")
            db=backup.admin_db();name=settings.DATABASES["default"]["NAME"];side=name+"_ripristino";old=f"{name}_prima_{stamp}"
            with psycopg.connect(host=db["HOST"] or "127.0.0.1",port=db["PORT"] or 5432,user=db["USER"],password=db["PASSWORD"],dbname="postgres",autocommit=True) as conn:
                conn.execute(sql.SQL("DROP DATABASE IF EXISTS {}").format(sql.Identifier(side)))
                conn.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(side)))
                try:backup.pg([backup.tool("pg_restore"),"--no-owner","--no-privileges","--exit-on-error","--no-password","-d",side,str(work/"database.dump")])
                except RuntimeError as ex:
                    conn.execute(sql.SQL("DROP DATABASE IF EXISTS {}").format(sql.Identifier(side)))
                    raise CommandError(f"Ripristino non riuscito, i dati attuali non sono stati toccati. {ex}") from None
                conn.execute("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=%s AND pid<>pg_backend_pid()",[name])
                exists=conn.execute("SELECT 1 FROM pg_database WHERE datname=%s",[name]).fetchone()
                if exists:conn.execute(sql.SQL("ALTER DATABASE {} RENAME TO {}").format(sql.Identifier(name),sql.Identifier(old)))
                conn.execute(sql.SQL("ALTER DATABASE {} RENAME TO {}").format(sql.Identifier(side),sql.Identifier(name)))
            config_path=settings.TDWIN_DATA/"config.json"
            shutil.copyfile(config_path,config_path.with_name(f"config.prima_{stamp}.json"))
            config=json.loads(config_path.read_text("utf-8"))
            config["env"].update({k:secrets[k] for k in ("DJANGO_SECRET_KEY","TDBS_KEYS","TDBS_ACTIVE_KEY")})
            config["install_id"]=info["install_id"]
            # written in place: the file keeps the permissions set by the installer
            with open(config_path,"r+",encoding="utf-8") as f:f.write(json.dumps(config,ensure_ascii=False,indent=1));f.truncate()
            if (work/"licenza.txt").exists():
                licensing.path().parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(work/"licenza.txt",licensing.path())
        self.stdout.write(f"Ripristino completato. Il database di prima resta come {old}"+("" if exists else ", non c'era")+": quando sei sicuro puoi eliminarlo.")
