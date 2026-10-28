"""TD Licenze: crea le licenze di TD Booking Suite per Windows.

Gira sul PC di chi vende il programma, non va mai installato dai clienti. La finestra è TD Licenze.exe,
costruito da GitHub Actions (td_licenze_gui.py); da riga di comando serve Python con il pacchetto cryptography:
    pip install cryptography

    python td_licenze.py            crea una licenza facendo le domande
    python td_licenze.py chiave     la prima volta: crea la chiave privata e stampa quella pubblica
    python td_licenze.py crea --intestatario "Bar Roma" --attivita 1 --postazioni 3 --installazione ABCD-EFGH-IJKL-MNOP
    python td_licenze.py verifica   controlla una licenza incollata e ne mostra i dati
    python td_licenze.py elenco     le licenze emesse, dal registro

La chiave privata firma le licenze: chi la ha può crearne quante vuole. Resta solo in questa cartella
(--cartella, di serie %APPDATA%\\TD Licenze o ~/.td-licenze), protetta da una password, con una copia su una chiavetta.
Il programma contiene solo la chiave pubblica: verifica le licenze ma non può crearle."""
import argparse,base64,csv,getpass,json,os,re
from datetime import date
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey,Ed25519PublicKey

PREFIX="TDBS1."
FIELDS=["id","emessa","intestatario","attivita","postazioni","installazione","scadenza","licenza"]

def b64(raw):return base64.urlsafe_b64encode(raw).decode().rstrip("=")
def unb64(text):return base64.urlsafe_b64decode(text+"="*(-len(text)%4))
def default_folder():
    base=os.environ.get("APPDATA")
    return Path(base)/"TD Licenze" if base else Path.home()/".td-licenze"
def normalize_code(code):
    code="".join(ch for ch in code.upper() if ch.isalnum())
    if code and not re.fullmatch(r"[A-Z2-7]{16}",code):raise ValueError("Il codice installazione ha 16 lettere e cifre, come ABCD-EFGH-IJKL-MNOP.")
    return "-".join(code[i:i+4] for i in range(0,len(code),4))

# the key: made once, kept with a password
def key_path(folder):return Path(folder)/"chiave-privata.pem"
def has_key(folder):return key_path(folder).exists()
def key_encrypted(folder):return b"ENCRYPTED" in key_path(folder).read_bytes()
def public_text(key):return b64(key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw))
def public_key_text(folder):return (Path(folder)/"chiave-pubblica.txt").read_text().strip()

def make_key(folder,password):
    """The key pair, once: the private key protected by the password, the public one for the program."""
    folder=Path(folder);folder.mkdir(parents=True,exist_ok=True)
    if has_key(folder):raise ValueError(f"La chiave esiste già in {key_path(folder)}: non la sovrascrivo, ne perderesti le licenze.")
    key=Ed25519PrivateKey.generate()
    crypt=serialization.BestAvailableEncryption(password.encode()) if password else serialization.NoEncryption()
    pem=key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,crypt)
    with os.fdopen(os.open(key_path(folder),os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600),"wb") as f:f.write(pem)
    (folder/"chiave-pubblica.txt").write_text(public_text(key)+"\n")
    return public_text(key)

def load_key(folder,password=None):
    if not has_key(folder):raise ValueError(f"Manca la chiave privata in {key_path(folder)}: la prima volta creala con  python td_licenze.py chiave")
    try:return serialization.load_pem_private_key(key_path(folder).read_bytes(),password.encode() if password else None)
    except (ValueError,TypeError):raise ValueError("Password della chiave errata.") from None

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
    with path.open("a",newline="",encoding="utf-8") as f:
        w=csv.DictWriter(f,fieldnames=FIELDS)
        if new:w.writeheader()
        w.writerow(row)

def describe(data):
    return (f"Attività: {data['attivita']}, postazioni in rete: {data['postazioni']}, "
            +(f"solo per l'installazione {data['installazione']}" if data["installazione"] else "valida su qualunque installazione")
            +(f", fino al {date.fromisoformat(data['scadenza']):%d/%m/%Y}" if data["scadenza"] else ", senza scadenza"))

def create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,password=None):
    """A signed licence, written to its file and to the register: (licence, file, data)."""
    intestatario=" ".join((intestatario or "").split())
    if not intestatario:raise ValueError("Serve l'intestatario.")
    attivita,postazioni=int(attivita),int(postazioni)
    if attivita<1 or postazioni<0:raise ValueError("Attività almeno 1, postazioni 0 o più.")
    installazione=normalize_code(installazione or "")
    scadenza=(scadenza or "").strip()
    if scadenza:
        try:date.fromisoformat(scadenza)
        except ValueError:raise ValueError("Scadenza nel formato AAAA-MM-GG, per esempio 2027-12-31.") from None
    key=load_key(folder,password)
    today=date.today()
    number=1+sum(1 for r in registry(folder) if r["id"].startswith(f"TD-{today.year}-"))
    data={"id":f"TD-{today.year}-{number:04d}","emessa":today.isoformat(),"intestatario":intestatario,"attivita":attivita,
          "postazioni":postazioni,"installazione":installazione,"scadenza":scadenza}
    licence=sign(key,data)
    record(folder,{**data,"licenza":licence})
    out=Path(out);out.mkdir(parents=True,exist_ok=True)
    slug=re.sub(r"[^a-z0-9]+","-",intestatario.lower()).strip("-")[:40] or "cliente"
    path=out/f"licenza-{data['id']}-{slug}.txt"
    path.write_text(f"Licenza TD Booking Suite per {intestatario}\n{describe(data)}"
        "\n\nApri TD Booking Suite, Area agenzia, Licenza, e incolla tutta la riga qui sotto:\n\n"+licence+"\n",encoding="utf-8")
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

def cli_create(folder,intestatario,attivita,postazioni,installazione,scadenza,out):
    licence,path,data=create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,password_for(folder))
    print(f"\nLicenza {data['id']} per {data['intestatario']}:\n\n{licence}\n\nSalvata anche in {path}")

def ask(prompt,default=""):
    value=input(f"{prompt}{f' [{default}]' if default else ''}: ").strip()
    return value or default

def main(argv=None):
    p=argparse.ArgumentParser(description="Crea le licenze di TD Booking Suite per Windows.")
    p.add_argument("--cartella",type=Path,default=default_folder(),help="dove stanno chiave privata e registro")
    p.add_argument("--uscita",type=Path,default=Path("licenze-emesse"),help="dove salvare i file delle licenze")
    sub=p.add_subparsers(dest="cmd")
    sub.add_parser("chiave");sub.add_parser("elenco")
    v=sub.add_parser("verifica");v.add_argument("licenza",nargs="?")
    c=sub.add_parser("crea")
    c.add_argument("--intestatario",required=True);c.add_argument("--attivita",type=int,default=1);c.add_argument("--postazioni",type=int,default=2)
    c.add_argument("--installazione",default="");c.add_argument("--scadenza",default="")
    a=p.parse_args(argv)
    try:
        if a.cmd=="chiave":cli_make_key(a.cartella)
        elif a.cmd=="crea":cli_create(a.cartella,a.intestatario,a.attivita,a.postazioni,a.installazione,a.scadenza,a.uscita)
        elif a.cmd=="elenco":
            for r in registry(a.cartella):
                print(f"{r['id']}  {r['emessa']}  {r['intestatario']}  attività {r['attivita']}, postazioni {r['postazioni']}  "
                      f"{r['installazione'] or 'a stock'}  {('scade '+r['scadenza']) if r['scadenza'] else 'per sempre'}")
        elif a.cmd=="verifica":
            text=a.licenza or input("Incolla la licenza: ")
            public=public_key_text(a.cartella) if (a.cartella/"chiave-pubblica.txt").exists() else None
            print(json.dumps(read(text,public),ensure_ascii=False,indent=2)+("\nFirma valida." if public else "\nFirma non controllata: manca la chiave pubblica."))
        else:
            print("Nuova licenza TD Booking Suite (Ctrl+C per uscire)\n")
            cli_create(a.cartella,ask("Intestatario, cioè il nome del cliente"),
                ask("Quante attività (1 = versione singola, di più = portale)","1"),ask("Quante postazioni in rete oltre al PC server","2"),
                ask("Codice installazione del cliente (invio = valida ovunque, a stock)"),ask("Scadenza AAAA-MM-GG (invio = per sempre)"),a.uscita)
    except ValueError as ex:raise SystemExit(str(ex) if "invalid literal" not in str(ex) else "Attività e postazioni sono numeri.") from None
    except (KeyboardInterrupt,EOFError):print()

if __name__=="__main__":main()
