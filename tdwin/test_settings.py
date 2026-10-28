import base64,json,os,tempfile
from pathlib import Path
_data=Path(tempfile.mkdtemp(prefix="tdwin-test-"))
(_data/"config.json").write_text(json.dumps({"origine":"http://localhost:8000","indirizzi":["ufficio.test","192.168.1.10"],"porta":8443,"install_id":"installazione-di-prova","env":{}}))
os.environ["TDBS_DATA"]=str(_data)
os.environ["TDBS_TESTING"]="1"
os.environ["TDBS_DEV"]="1"
os.environ.setdefault("DJANGO_SECRET_KEY","test-only-"+("x"*64))
os.environ.setdefault("TDBS_KEYS",'{"v1":"'+base64.urlsafe_b64encode(b"x"*32).decode()+'","index":"'+base64.urlsafe_b64encode(b"i"*32).decode()+'"}')
from .settings import *  # noqa: F401,F403
from .settings import STORAGES
STORAGES["staticfiles"]={"BACKEND":"django.contrib.staticfiles.storage.StaticFilesStorage"}
TDWIN_LICENSE_KEY=""  # each test signs with its own key pair
