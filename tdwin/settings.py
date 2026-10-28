"""Windows edition: the core settings, fed by config.json in the data folder, plus licence, workstations and backups.

config.json is written by windows/setup-server.ps1 and readable only by the program's services and administrators:
{"origine": "https://ufficio-pc:8443", "indirizzi": [...], "porta": 8443, "install_id": "...", "pg_bin": "...", "env": {...}}"""
import json,os,socket
from pathlib import Path
DATA=Path(os.environ.get("TDBS_DATA") or Path(os.environ.get("ProgramData",r"C:\ProgramData"))/"TD Booking Suite")
CONFIG=json.loads((DATA/"config.json").read_text("utf-8"))
for name,value in CONFIG.get("env",{}).items():os.environ.setdefault(name,value)
os.environ.setdefault("PUBLIC_ORIGIN",CONFIG["origine"])
# tdwin.licensing.apply moves the edition to cloud when the licence allows more businesses
os.environ.setdefault("TDBS_EDITION","private")
# sold under the seller's own licence: the footer shows the version without the AGPL source link
os.environ.setdefault("TDBS_SOURCE_URL","")
from tdbooking.settings import *  # noqa: F401,F403
from tdbooking.settings import INSTALLED_APPS,MIDDLEWARE,ALLOWED_HOSTS,CSRF_TRUSTED_ORIGINS

TDWIN_DATA=DATA
TDWIN_CONFIG=CONFIG
TDWIN_PG_BIN=Path(CONFIG.get("pg_bin") or ".")
_key=Path(__file__).with_name("licenza-chiave-pubblica.txt")
TDWIN_LICENSE_KEY=_key.read_text().strip() if _key.exists() else ""

INSTALLED_APPS=INSTALLED_APPS+["tdwin"]
ROOT_URLCONF="tdwin.urls"
_guard=MIDDLEWARE.index("core.middleware.GuardMiddleware")
MIDDLEWARE=MIDDLEWARE[:_guard]+["tdwin.middleware.WindowsMiddleware"]+MIDDLEWARE[_guard:]
# the configured address plus this PC's names and addresses: the server PC and the network reach it either way
_port=int(CONFIG.get("porta",8443))
_hosts=[h.strip().lower() for h in CONFIG.get("indirizzi",[]) if h.strip()]+["localhost","127.0.0.1","::1",socket.gethostname().lower(),socket.getfqdn().lower()]
_hosts=[f"[{h}]" if ":" in h else h for h in dict.fromkeys(_hosts)]
ALLOWED_HOSTS=ALLOWED_HOSTS+_hosts
CSRF_TRUSTED_ORIGINS=CSRF_TRUSTED_ORIGINS+["https://"+h+("" if _port==443 else f":{_port}") for h in _hosts]
EXTRA_NAV=[{"label":"Licenza","url":"/platform/licenza/"},{"label":"Postazioni","url":"/platform/postazioni/"},{"label":"Backup","url":"/platform/backup/"}]
MORE_TENANTS_HINT="Per più attività serve una licenza Portale con più attività: la inserisci da Licenza."
EDITION_LABEL="Windows"
STATIC_ROOT=Path(__file__).resolve().parent.parent/"static-collected"  # made by windows/build.ps1
