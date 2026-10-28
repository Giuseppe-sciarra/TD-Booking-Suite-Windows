"""Static files of the Windows edition (core and tdwin) into static-collected, at build time: windows/build.ps1."""
import base64,json,os,secrets,sys,tempfile
from pathlib import Path
root=Path(__file__).resolve().parent.parent
sys.path[:0]=[str(root),str(root/"suite")]
data=Path(tempfile.mkdtemp(prefix="tdwin-build-"))
(data/"config.json").write_text(json.dumps({"origine":"https://localhost:8443","indirizzi":[],"porta":8443,"install_id":"build","env":{}}))
os.environ.update(TDBS_DATA=str(data),TDBS_DEV="1",DJANGO_SECRET_KEY=secrets.token_urlsafe(64),DJANGO_SETTINGS_MODULE="tdwin.settings",
    TDBS_KEYS=json.dumps({k:base64.urlsafe_b64encode(secrets.token_bytes(32)).decode() for k in ("v1","index")}))
import django;django.setup()
from django.core.management import call_command
call_command("collectstatic",interactive=False,verbosity=0)
print("File statici pronti")
