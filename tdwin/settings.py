"""Windows edition: the core settings, fed by config.json in the data folder, plus licence, workstations and backups.

config.json is written by windows/setup-server.ps1 and readable only by the program's services and administrators:
{"origine": "https://ufficio-pc:8443", "indirizzi": [...], "porta": 8443, "install_id": "...", "pg_bin": "...", "env": {...}}"""
import json,os
from pathlib import Path
DATA=Path(os.environ.get("TDBS_DATA") or Path(os.environ.get("ProgramData",r"C:\ProgramData"))/"TD Booking Suite")
CONFIG=json.loads((DATA/"config.json").read_text("utf-8"))
for name,value in CONFIG.get("env",{}).items():os.environ.setdefault(name,value)
os.environ.setdefault("PUBLIC_ORIGIN",CONFIG["origine"])
# tdwin.licensing.apply moves the edition to cloud when the licence allows more businesses
os.environ.setdefault("TDBS_EDITION","private")
# sold under the seller's own licence: the footer shows the version without the AGPL source link
os.environ.setdefault("TDBS_SOURCE_URL","")
# a program on one's own PC: with Resta collegato it opens signed in until Esci (2.13.0: the core's default, 0)
# 2.20.4: Python on Windows trusts only the roots already in the Windows store, and Windows fetches a missing one only
# for the browsers: with SSL_CERT_FILE every default context (updates, WordPress site, calendars, mail) also loads
# certifi's, next to the store (ssl.SSLContext.load_default_certs). TD Licenze had the same trouble with GitHub
try:
    import certifi
    os.environ.setdefault("SSL_CERT_FILE",certifi.where())
except ImportError:pass
from tdbooking.settings import *  # noqa: F401,F403
from tdbooking.settings import INSTALLED_APPS,MIDDLEWARE,ALLOWED_HOSTS,CSRF_TRUSTED_ORIGINS

TDWIN_DATA=DATA
# 2.13.0: the state of the licences TD Licenze publishes in the seller's public repo, read once a day when online
TDWIN_STATO_LICENZE=os.environ.get("TDWIN_STATO_LICENZE","https://raw.githubusercontent.com/Giuseppe-sciarra/TD-Licenze-Stato/main/stato-licenze.json")
# 2.14.0: the public releases with the MSI, looked at for new versions (also in windows/client/MainForm.cs)
TDWIN_RILASCI="Giuseppe-sciarra/TD-Booking-Suite-per-Windows"
# 2.13.0: the devices from which an owner the administrator allows enters without password
TRUSTED_DEVICE="tdwin.middleware.trusted_device"
TDWIN_CONFIG=CONFIG
_key=Path(__file__).with_name("licenza-chiave-pubblica.txt")
TDWIN_LICENSE_KEY=_key.read_text().strip() if _key.exists() else ""

INSTALLED_APPS=INSTALLED_APPS+["tdwin"]
ROOT_URLCONF="tdwin.urls"
_guard=MIDDLEWARE.index("core.middleware.GuardMiddleware")
MIDDLEWARE=MIDDLEWARE[:_guard]+["tdwin.middleware.WindowsMiddleware"]+MIDDLEWARE[_guard:]
MIDDLEWARE=[("tdwin.middleware.CsrfMiddleware" if m=="django.middleware.csrf.CsrfViewMiddleware" else m) for m in MIDDLEWARE]
# the configured address plus this PC's names and addresses, the same that the certificate covers
from tdwin.netnames import names as _names
_port=int(CONFIG.get("porta",8443))
_hosts=[f"[{h}]" if ":" in h else h for h in _names(CONFIG.get("indirizzi",[]))]
ALLOWED_HOSTS=ALLOWED_HOSTS+_hosts
CSRF_TRUSTED_ORIGINS=CSRF_TRUSTED_ORIGINS+["https://"+h+("" if _port==443 else f":{_port}") for h in _hosts]
# Un PC solo (standalone) or a server with workstations, chosen in the installer
TDWIN_STANDALONE=CONFIG.get("modalita","server")=="standalone"
# 2.10.0: the booking page and the website: always on a server; on Un PC solo only once the administrator turns them on
TDWIN_ONLINE=not TDWIN_STANDALONE or bool(CONFIG.get("online"))
PUBLIC_BOOKING=TDWIN_ONLINE
# 2.16.0: the server PC is in the shop: it talks to the till itself (network or USB), no bridge needed
TILL_DIRECT=True
def EXTRA_NAV(request):
    """The program's own entries. Configurazione guidata goes once there is a licence (or the trial) and a business:
    from then on the menu keeps only what is used; /platform/inizia/ still opens from its address."""
    from tdwin.views import setup_done
    return ([] if setup_done(request) else [{"label":"Configurazione guidata","icon":"compass","url":"/platform/inizia/"}])+[{"label":"Licenza","icon":"key","url":"/platform/licenza/"}]+(
        [] if TDWIN_STANDALONE else [{"label":"Postazioni","icon":"monitor","url":"/platform/postazioni/"}])+[{"label":"Backup","icon":"archive","url":"/platform/backup/"}]+(
        [{"label":"Online e sito" if TDWIN_STANDALONE else "Rete e certificato","icon":"globe","url":"/platform/rete/"}])+[{"label":"Stato del programma","icon":"list","url":"/platform/stato/"}]
# the program's own pages (guided setup, backup, workstations, network); the compact look is in the core since 2.10.0
EXTRA_CSS=["tdwin/tdwin.css"]
SLOW_LOG_URL="/platform/stato/lento/"  # 2.20.8: the window reports the pages it waited for (static/app.js)
# who is who: the program's administrator configures, the business's owner and people work
AGENCY_NAMES={"area":"Configurazione del programma","admin":"Amministratore del programma","who":"amministratore del programma","access":"Accesso dell'amministratore"}
ROLE_LABELS={"owner":"Titolare"}
LOGIN_NOTE=({"title":"Amministratore del programma","text":"Configura licenza, "+("" if TDWIN_STANDALONE else "postazioni, ")+"backup e attività. Vede agenda e clienti solo se alla configurazione guidata ha scelto di lavorare lui nell'attività."},
    {"title":"Titolare e persone dell'attività","text":"Agenda, prenotazioni, clienti, servizi e orari: il lavoro di ogni giorno. Le persone con accesso le aggiunge l'amministratore del programma."})
MORE_TENANTS_HINT="Per più attività serve una licenza Portale con più attività: la inserisci da Licenza."
EDITION_LABEL="Windows"
# accounts have a username, the email is optional; Un PC solo answers only on 127.0.0.1: password alone,
# with workstations Authenticator and passkeys stay optional, without reminders
LOGIN_USERNAMES=True
MFA_ENABLED=not TDWIN_STANDALONE
MFA_REMINDER=""  # 2.20.5: no "Proteggi il tuo account" banner on the PC program, site linked or not; Authenticator stays in Account e sicurezza
SHOP_MAILS=True  # 2.20.11: no agency here: the shop switches on, times and writes every email to its clients itself
STATIC_ROOT=Path(__file__).resolve().parent.parent/"static-collected"  # made by windows/build.ps1
# client import: the sealed file between the steps and the upload itself stay in a folder the web service may write
IMPORT_DIR=str(DATA/"import")
FILE_UPLOAD_TEMP_DIR=IMPORT_DIR
try:os.makedirs(IMPORT_DIR,exist_ok=True)  # the installer makes it, with the web service's permission
except OSError:pass
