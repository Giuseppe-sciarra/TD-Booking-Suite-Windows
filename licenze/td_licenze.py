"""TD Licenze: crea le licenze di TD Booking Suite per Windows.

Gira sul PC di chi vende il programma, non va mai installato dai clienti. La finestra è TD Licenze.exe,
costruito da GitHub Actions (td_licenze_gui.py); da riga di comando serve Python con il pacchetto cryptography:
    pip install cryptography

    python td_licenze.py            crea una licenza facendo le domande
    python td_licenze.py chiave     la prima volta: crea la chiave privata e stampa quella pubblica
    python td_licenze.py crea --intestatario "Bar Roma" --tipo server --postazioni 3 --installazione ABCD-EFGH-IJKL-MNOP
    python td_licenze.py crea --sostituisce TD-2026-0007 --postazioni 5     stessa installazione, postazioni cambiate
    python td_licenze.py verifica   controlla una licenza incollata e ne mostra i dati
    python td_licenze.py elenco     le licenze emesse, dal registro
    python td_licenze.py collega Giuseppe-sciarra/TD-Licenze-Dati    il registro anche nella repo privata di GitHub
    python td_licenze.py sincronizza                                  registro aggiornato da e verso GitHub

La chiave privata firma le licenze: chi la ha può crearne quante vuole. La build scrive dentro TD Licenze.exe quella
del secret TD_LICENZE_CHIAVE della repo, cifrata con la sua password; senza, sta nella cartella del programma
(--cartella, di serie quella di TD Licenze.exe o di questo file). Registro e licenze emesse si salvano lì e da
nessun'altra parte, così la cartella intera va su una chiavetta; con una repo collegata il registro va anche su GitHub.
Il programma contiene solo la chiave pubblica: verifica le licenze ma non può crearle."""
import argparse,base64,csv,getpass,io,json,os,re
from datetime import date
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey,Ed25519PublicKey

PREFIX="TDBS1."
FIELDS=["id","emessa","intestatario","tipo","attivita","postazioni","installazione","scadenza","sostituisce","licenza"]
# the three kinds of licence: Un PC solo has no workstations, a server one business, a portal more businesses
TIPI={"pc":"Un PC solo","server":"Server con postazioni","portale":"Portale, più attività"}
def tipo_of(data):
    """The kind written in the licence, or the one its numbers say (licences made before kinds existed)."""
    if data.get("tipo") in TIPI:return data["tipo"]
    return "portale" if int(data["attivita"])>1 else "pc" if int(data["postazioni"])==0 else "server"

def b64(raw):return base64.urlsafe_b64encode(raw).decode().rstrip("=")
def unb64(text):return base64.urlsafe_b64decode(text+"="*(-len(text)%4))
def default_folder():
    """The program's own folder: TD Licenze.exe's, or this file's. Everything it saves stays there, on a USB stick too."""
    import sys
    return Path(sys.executable).resolve().parent if getattr(sys,"frozen",False) else Path(__file__).resolve().parent
def legacy_folder():
    """Where versions before 2.9 kept the key: only read, to bring an old key into the program's folder."""
    base=os.environ.get("APPDATA")
    return Path(base)/"TD Licenze" if base else Path.home()/".td-licenze"
def normalize_code(code):
    code="".join(ch for ch in code.upper() if ch.isalnum())
    if code and not re.fullmatch(r"[A-Z2-7]{16}",code):raise ValueError("Il codice installazione ha 16 lettere e cifre, come ABCD-EFGH-IJKL-MNOP.")
    return "-".join(code[i:i+4] for i in range(0,len(code),4))

# the key: made once, kept with a password
def key_path(folder):return Path(folder)/"chiave-privata.pem"
def builtin():
    """(private key PEM, public key) that the build writes into TD Licenze.exe from the private repo's secret
    TD_LICENZE_CHIAVE (licenze/incorpora_chiave.py): every copy downloaded from Actions has them, the private one
    encrypted with its password. Empty while working on the sources."""
    try:import chiave_incorporata as k
    except ImportError:return b"",""
    return k.CHIAVE.encode(),k.PUBBLICA
def key_built_in():return bool(builtin()[0])
def key_bytes(folder):
    """The key inside TD Licenze.exe wins; a key file in the folder is for copies built without it."""
    return builtin()[0] or (key_path(folder).read_bytes() if key_path(folder).exists() else b"")
def has_key(folder):return bool(key_bytes(folder))
def key_encrypted(folder):return b"ENCRYPTED" in key_bytes(folder)
def public_text(key):return b64(key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw))
def public_key_text(folder):return builtin()[1] or (Path(folder)/"chiave-pubblica.txt").read_text().strip()

def make_key(folder,password):
    """The key pair, once: the private key protected by the password, the public one for the program."""
    folder=Path(folder);folder.mkdir(parents=True,exist_ok=True)
    if key_built_in():raise ValueError("La chiave è già dentro questo TD Licenze: non ne creo un'altra.")
    if has_key(folder):raise ValueError(f"La chiave esiste già in {key_path(folder)}: non la sovrascrivo, ne perderesti le licenze.")
    key=Ed25519PrivateKey.generate()
    crypt=serialization.BestAvailableEncryption(password.encode()) if password else serialization.NoEncryption()
    pem=key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,crypt)
    with os.fdopen(os.open(key_path(folder),os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600),"wb") as f:f.write(pem)
    (folder/"chiave-pubblica.txt").write_text(public_text(key)+"\n")
    return public_text(key)

def load_key(folder,password=None):
    if not has_key(folder):raise ValueError(f"Manca la chiave privata in {key_path(folder)}: la prima volta creala con  python td_licenze.py chiave")
    try:key=serialization.load_pem_private_key(key_bytes(folder),password.encode() if password else None)
    except (ValueError,TypeError):raise ValueError("Password della chiave errata.") from None
    # the key built in must be the pair of the public key built in with it, the one of the program
    if key_built_in() and builtin()[1] and public_text(key)!=builtin()[1]:
        raise ValueError("La chiave dentro TD Licenze non è la coppia della chiave pubblica del programma: controlla il secret TD_LICENZE_CHIAVE e tdwin/licenza-chiave-pubblica.txt.")
    return key

def sign(key,data):
    body=b64(json.dumps(data,ensure_ascii=False,separators=(",",":"),sort_keys=True).encode())
    return PREFIX+body+"."+b64(key.sign((PREFIX+body).encode()))

def read(text,public=None):
    """The data of a licence; ValueError when the signature does not match the public key."""
    text="".join(text.split())
    if not text.startswith(PREFIX):raise ValueError("Non è una licenza TDBS1.")
    try:
        body,signature=text[len(PREFIX):].split(".")
        if public:Ed25519PublicKey.from_public_bytes(unb64(public)).verify(unb64(signature),(PREFIX+body).encode())
        return json.loads(unb64(body))
    except Exception:raise ValueError("Licenza non valida o modificata.") from None

def registry(folder):
    path=Path(folder)/"registro-licenze.csv"
    if not path.exists():return []
    with path.open(newline="",encoding="utf-8") as f:return list(csv.DictReader(f))
def record(folder,row):
    path=Path(folder)/"registro-licenze.csv";new=not path.exists()
    if not new:
        with path.open(newline="",encoding="utf-8") as f:header=next(csv.reader(f),[])
        if header!=FIELDS:
            # a register from an earlier TD Licenze: rewritten once with the new columns, every licence kept
            rows=registry(folder)
            temporary=path.with_suffix(".tmp")
            with temporary.open("w",newline="",encoding="utf-8") as f:
                w=csv.DictWriter(f,fieldnames=FIELDS);w.writeheader()
                for r in rows:w.writerow({k:r.get(k) or "" for k in FIELDS})
            os.replace(temporary,path)
    with path.open("a",newline="",encoding="utf-8") as f:
        w=csv.DictWriter(f,fieldnames=FIELDS)
        if new:w.writeheader()
        w.writerow(row)

# the register in a private GitHub repo: TD Licenze on any PC finds every licence ever made
SYNC_FILE="registro-github.json"
API="https://api.github.com"
class SyncError(Exception):pass

def csv_text(rows):
    out=io.StringIO();w=csv.DictWriter(out,fieldnames=FIELDS,lineterminator="\n");w.writeheader()
    for r in rows:w.writerow({k:r.get(k) or "" for k in FIELDS})
    return out.getvalue()
def write_registry(folder,rows):
    path=Path(folder)/"registro-licenze.csv";temporary=path.with_suffix(".tmp")
    temporary.write_text(csv_text(rows),encoding="utf-8");os.replace(temporary,path)
def merge(local,remote):
    """Every licence of both, once each, oldest first: (rows, how many only in remote, how many only in local)."""
    key=lambda r:(r.get("id",""),r.get("licenza",""))
    here={key(r) for r in local};there={key(r) for r in remote}
    rows={key(r):{k:r.get(k) or "" for k in FIELDS} for r in remote+local}
    return sorted(rows.values(),key=lambda r:(r["emessa"],r["id"])),len(there-here),len(here-there)

def _seal_key(password,salt):
    from cryptography.hazmat.primitives.kdf.scrypt import Scrypt
    return Scrypt(salt=salt,length=32,n=2**15,r=8,p=1).derive((password or "").encode())
def save_sync(folder,repo,token,password):
    """The repo and its token, the token sealed with the key's password: the folder alone does not give it away."""
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    repo=repo.strip().strip("/").removeprefix("https://github.com/")
    if not re.fullmatch(r"[\w.-]+/[\w.-]+",repo):raise ValueError("Scrivi la repo come proprietario/nome, per esempio Giuseppe-sciarra/TD-Licenze-Dati.")
    if not token.strip():raise ValueError("Incolla il token di GitHub.")
    salt,nonce=os.urandom(16),os.urandom(12)
    sealed=AESGCM(_seal_key(password,salt)).encrypt(nonce,token.strip().encode(),repo.encode())
    target=Path(folder)/SYNC_FILE
    target.write_text(json.dumps({"repo":repo,"sale":b64(salt),"nonce":b64(nonce),"token":b64(sealed)}),encoding="utf-8")
    return repo
def builtin_sync():
    """registro-github.json from the secret TD_LICENZE_REGISTRO, written into the exe with the key: same sealed token."""
    try:import chiave_incorporata as k
    except ImportError:return None
    return json.loads(getattr(k,"REGISTRO","") or "null")
def sync_settings(folder):
    """The repo of the register: the folder's file wins ({} = disconnected here), else the one inside TD Licenze."""
    try:return json.loads((Path(folder)/SYNC_FILE).read_text(encoding="utf-8"))
    except OSError:return builtin_sync()
    except ValueError:return None
def sync_token(folder,password):
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    cfg=sync_settings(folder)
    try:return AESGCM(_seal_key(password,unb64(cfg["sale"]))).decrypt(unb64(cfg["nonce"]),unb64(cfg["token"]),cfg["repo"].encode()).decode()
    except Exception:raise SyncError("Non riesco ad aprire il token di GitHub con questa password: collega di nuovo la repo.") from None

def github(method,path,token,body=None):
    """(status, answer) of the GitHub API; SyncError when GitHub cannot be reached."""
    import urllib.error,urllib.request
    request=urllib.request.Request(API+path,method=method,data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization":"Bearer "+token,"Accept":"application/vnd.github+json","X-GitHub-Api-Version":"2022-11-28","User-Agent":"TD-Licenze"})
    try:
        with urllib.request.urlopen(request,timeout=20) as answer:return answer.status,json.loads(answer.read() or b"null")
    except urllib.error.HTTPError as ex:
        try:data=json.loads(ex.read() or b"null")
        except ValueError:data=None
        return ex.code,data
    except (urllib.error.URLError,OSError) as ex:raise SyncError("GitHub non raggiungibile: senza Internet lavoro sulla copia della cartella e mando tutto la prossima volta.") from ex

def sync(folder,password):
    """The register merged both ways with the one in the private repo: (licences, arrived, sent), or None when no
    repo is connected. The copy in the folder is always the full one, also without Internet."""
    cfg=sync_settings(folder)
    if not cfg:return None
    token=sync_token(folder,password)
    path=f"/repos/{cfg['repo']}/contents/registro-licenze.csv"
    for _ in range(3):
        # shortcut: the Contents API returns the file up to 1 MB (some 2000 licences); past that read it as raw
        status,data=github("GET",path,token)
        if status==200:remote_text,sha=base64.b64decode(data["content"]).decode("utf-8"),data["sha"]
        elif status==404:
            if github("GET",f"/repos/{cfg['repo']}",token)[0]!=200:raise SyncError(f"La repo {cfg['repo']} non c'è, o il token non può leggerla.")
            remote_text,sha="",None  # an empty repo: the register is written now
        elif status in (401,403):raise SyncError("GitHub rifiuta il token: forse è scaduto. Fanne uno nuovo e collega di nuovo la repo.")
        else:raise SyncError(f"GitHub risponde {status}: riprova più tardi.")
        remote=list(csv.DictReader(io.StringIO(remote_text))) if remote_text else []
        rows,arrived,sent=merge(registry(folder),remote)
        write_registry(folder,rows)
        if not sent and sha:return len(rows),arrived,0
        body={"message":f"TD Licenze: {len(rows)} licenze","content":base64.b64encode(csv_text(rows).encode("utf-8")).decode()}
        if sha:body["sha"]=sha
        status,data=github("PUT",path,token,body)
        if status in (200,201):return len(rows),arrived,sent
        if status in (401,403):raise SyncError("Il token può leggere la repo ma non scriverci: dagli il permesso Contents, Read and write.")
        if status not in (409,422):raise SyncError(f"GitHub risponde {status}: riprova più tardi.")
        # someone wrote the register in between, from another PC: read it again and merge
    raise SyncError("Il registro su GitHub cambia di continuo: riprova tra poco.")

def sync_message(result):
    licences,arrived,sent=result
    parts=[f"{licences} licenze nel registro"]+([f"{arrived} arrivate da GitHub"] if arrived else [])+([f"{sent} mandate a GitHub"] if sent else [])
    return ", ".join(parts)+"."

def describe(data):
    return (f"{TIPI[tipo_of(data)]}. Attività: {data['attivita']}, postazioni in rete: {data['postazioni']}, "
            +(f"solo per l'installazione {data['installazione']}" if data["installazione"] else "valida su qualunque installazione")
            +(f", fino al {date.fromisoformat(data['scadenza']):%d/%m/%Y}" if data["scadenza"] else ", senza scadenza"))

def find(folder,licence_id):
    row=next((r for r in registry(folder) if r["id"]==licence_id),None)
    if not row:raise ValueError(f"La licenza {licence_id} non è nel registro di questa cartella.")
    return row

def create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,password=None,tipo="",sostituisce=""):
    """A signed licence, written to its file and to the register: (licence, file, data).
    sostituisce: the number of the licence it replaces, for the same installation (more or fewer workstations,
    another expiry): the client pastes the new one over the old one."""
    if sostituisce:
        old=find(folder,sostituisce)
        intestatario=intestatario or old["intestatario"];installazione=installazione or old["installazione"]
        # what is not given (None) stays as in the licence it replaces; with new numbers and no kind, the numbers say it
        if not tipo and attivita is None and postazioni is None:tipo=tipo_of(old)
        attivita=old["attivita"] if attivita is None else attivita
        postazioni=old["postazioni"] if postazioni is None else postazioni
        scadenza=old["scadenza"] if scadenza is None else scadenza
    attivita=1 if attivita is None else attivita;postazioni=2 if postazioni is None else postazioni
    intestatario=" ".join((intestatario or "").split())
    if not intestatario:raise ValueError("Serve l'intestatario.")
    attivita,postazioni=int(attivita),int(postazioni)
    if attivita<1 or postazioni<0:raise ValueError("Attività almeno 1, postazioni 0 o più.")
    tipo=tipo or tipo_of({"attivita":attivita,"postazioni":postazioni})
    if tipo not in TIPI:raise ValueError("Tipo di licenza: pc, server o portale.")
    if tipo=="pc":attivita,postazioni=1,0
    elif tipo=="server":attivita=1
    elif attivita<2:raise ValueError("Una licenza Portale ha almeno 2 attività.")
    installazione=normalize_code(installazione or "")
    scadenza=(scadenza or "").strip()
    if scadenza:
        try:date.fromisoformat(scadenza)
        except ValueError:raise ValueError("Scadenza nel formato AAAA-MM-GG, per esempio 2027-12-31.") from None
    key=load_key(folder,password)
    today=date.today()
    number=1+sum(1 for r in registry(folder) if r["id"].startswith(f"TD-{today.year}-"))
    data={"id":f"TD-{today.year}-{number:04d}","emessa":today.isoformat(),"intestatario":intestatario,"tipo":tipo,"attivita":attivita,
          "postazioni":postazioni,"installazione":installazione,"scadenza":scadenza}
    if sostituisce:data["sostituisce"]=sostituisce
    licence=sign(key,data)
    record(folder,{**data,"sostituisce":sostituisce,"licenza":licence})
    out=Path(out);out.mkdir(parents=True,exist_ok=True)
    slug=re.sub(r"[^a-z0-9]+","-",intestatario.lower()).strip("-")[:40] or "cliente"
    path=out/f"licenza-{data['id']}-{slug}.txt"
    path.write_text(f"Licenza TD Booking Suite per {intestatario}\n{describe(data)}"
        +(f"\nSostituisce la licenza {sostituisce}: incollala al posto di quella." if sostituisce else "")
        +"\n\nApri TD Booking Suite, entra con l'amministratore del programma, menu Licenza, e incolla tutta la riga qui sotto:\n\n"+licence+"\n",encoding="utf-8")
    return licence,path,data

# command line
def password_for(folder):return getpass.getpass("Password della chiave: ") if has_key(folder) and key_encrypted(folder) else None

def cli_make_key(folder):
    if has_key(folder):raise ValueError(f"La chiave esiste già in {key_path(folder)}: non la sovrascrivo, ne perderesti le licenze.")
    password=getpass.getpass("Password per proteggere la chiave (consigliata, invio per nessuna): ")
    if password and password!=getpass.getpass("Ripeti la password: "):raise ValueError("Le password non coincidono.")
    public=make_key(folder,password)
    print(f"Chiave privata creata: {key_path(folder)}\nFanne subito una copia su una chiavetta: senza, non puoi più emettere licenze.\n")
    print("Chiave pubblica, da mettere nel programma (file tdwin/licenza-chiave-pubblica.txt della repo):\n"+public)

def cli_sync(folder,password):
    """The register brought up to date with GitHub, if a repo is connected; without Internet it only says so."""
    try:
        result=sync(folder,password)
        if result:print("GitHub: "+sync_message(result))
    except SyncError as ex:print("GitHub: "+str(ex))

def cli_create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,tipo="",sostituisce=""):
    password=password_for(folder)
    cli_sync(folder,password)  # the latest register first: the new number follows the licences made on other PCs
    licence,path,data=create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,password,tipo,sostituisce)
    print(f"\nLicenza {data['id']} per {data['intestatario']}:\n\n{licence}\n\nSalvata anche in {path}")
    cli_sync(folder,password)

def cli_connect(folder,repo):
    password=password_for(folder)
    load_key(folder,password)  # the token is sealed with the key's password: it must be the right one
    token=getpass.getpass("Token di GitHub (non si vede mentre lo incolli): ")
    print("Repo collegata: "+save_sync(folder,repo,token,password))
    cli_sync(folder,password)

def ask(prompt,default=""):
    value=input(f"{prompt}{f' [{default}]' if default else ''}: ").strip()
    return value or default

def main(argv=None):
    p=argparse.ArgumentParser(description="Crea le licenze di TD Booking Suite per Windows.")
    p.add_argument("--cartella",type=Path,default=default_folder(),help="dove stanno chiave privata e registro")
    p.add_argument("--uscita",type=Path,default=None,help="dove salvare i file delle licenze, di serie licenze-emesse nella cartella")
    sub=p.add_subparsers(dest="cmd")
    sub.add_parser("chiave");sub.add_parser("elenco");sub.add_parser("sincronizza",help="registro aggiornato con la repo privata di GitHub")
    g=sub.add_parser("collega",help="collega la repo privata dove tenere il registro delle licenze");g.add_argument("repo",help="proprietario/nome")
    v=sub.add_parser("verifica");v.add_argument("licenza",nargs="?")
    c=sub.add_parser("crea")
    # not given: 1 business, 2 workstations, no expiry; with --sostituisce, the values of the licence replaced
    c.add_argument("--intestatario",default="");c.add_argument("--attivita",type=int,default=None);c.add_argument("--postazioni",type=int,default=None)
    c.add_argument("--installazione",default="");c.add_argument("--scadenza",default=None,help='AAAA-MM-GG; "" per sempre')
    c.add_argument("--tipo",choices=sorted(TIPI),default="",help="pc (Un PC solo), server (Server con postazioni), portale")
    c.add_argument("--sostituisce",default="",help="numero della licenza da sostituire: stessa installazione e intestatario")
    a=p.parse_args(argv)
    a.uscita=a.uscita or a.cartella/"licenze-emesse"
    try:
        if a.cmd=="chiave":cli_make_key(a.cartella)
        elif a.cmd=="crea":cli_create(a.cartella,a.intestatario,a.attivita,a.postazioni,a.installazione,a.scadenza,a.uscita,a.tipo,a.sostituisce)
        elif a.cmd=="collega":cli_connect(a.cartella,a.repo)
        elif a.cmd=="sincronizza":
            if not sync_settings(a.cartella):raise ValueError("Nessuna repo collegata: usa  td_licenze.py collega proprietario/nome")
            cli_sync(a.cartella,password_for(a.cartella))
        elif a.cmd=="elenco":
            for r in registry(a.cartella):
                print(f"{r['id']}  {r['emessa']}  {r['intestatario']}  {TIPI[tipo_of(r)]}, attività {r['attivita']}, postazioni {r['postazioni']}  "
                      f"{r['installazione'] or 'a stock'}  {('scade '+r['scadenza']) if r['scadenza'] else 'per sempre'}")
        elif a.cmd=="verifica":
            text=a.licenza or input("Incolla la licenza: ")
            public=public_key_text(a.cartella) if (a.cartella/"chiave-pubblica.txt").exists() else None
            print(json.dumps(read(text,public),ensure_ascii=False,indent=2)+("\nFirma valida." if public else "\nFirma non controllata: manca la chiave pubblica."))
        else:
            print("Nuova licenza TD Booking Suite (Ctrl+C per uscire)\n")
            tipo=ask("Tipo: pc (Un PC solo), server (Server con postazioni), portale","server")
            cli_create(a.cartella,ask("Intestatario, cioè il nome del cliente"),
                ask("Quante attività","2") if tipo=="portale" else "1",ask("Quante postazioni in rete oltre al PC server","2") if tipo!="pc" else "0",
                ask("Codice installazione del cliente (invio = valida ovunque, a stock)"),ask("Scadenza AAAA-MM-GG (invio = per sempre)"),a.uscita,tipo)
    except ValueError as ex:raise SystemExit(str(ex) if "invalid literal" not in str(ex) else "Attività e postazioni sono numeri.") from None
    except SyncError as ex:raise SystemExit(str(ex)) from None
    except (KeyboardInterrupt,EOFError):print()

if __name__=="__main__":main()
