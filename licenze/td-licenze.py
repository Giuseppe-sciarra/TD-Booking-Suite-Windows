"""TD Licenze: crea le licenze di TD Booking Suite per Windows.

Gira sul PC di chi vende il programma, non va mai installato dai clienti. Serve solo il pacchetto cryptography:
    pip install cryptography

    python td-licenze.py            crea una licenza facendo le domande
    python td-licenze.py chiave     la prima volta: crea la chiave privata e stampa quella pubblica
    python td-licenze.py crea --intestatario "Bar Roma" --attivita 1 --postazioni 3 --installazione ABCD-EFGH-IJKL-MNOP
    python td-licenze.py verifica   controlla una licenza incollata e ne mostra i dati
    python td-licenze.py elenco     le licenze emesse, dal registro

La chiave privata firma le licenze: chi la ha può crearne quante vuole. Resta solo in questa cartella
(--cartella, di serie %APPDATA%\\TD Licenze o ~/.td-licenze), protetta da una password, con una copia su una chiavetta.
Il programma contiene solo la chiave pubblica: verifica le licenze ma non può crearle."""
import argparse,base64,csv,getpass,json,os,re,sys
from datetime import date
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

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

def make_key(folder):
    folder.mkdir(parents=True,exist_ok=True)
    path=folder/"chiave-privata.pem"
    if path.exists():raise SystemExit(f"La chiave esiste già in {path}: non la sovrascrivo, ne perderesti le licenze.")
    password=getpass.getpass("Password per proteggere la chiave (consigliata, invio per nessuna): ")
    if password and password!=getpass.getpass("Ripeti la password: "):raise SystemExit("Le password non coincidono.")
    key=Ed25519PrivateKey.generate()
    crypt=serialization.BestAvailableEncryption(password.encode()) if password else serialization.NoEncryption()
    pem=key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,crypt)
    with os.fdopen(os.open(path,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600),"wb") as f:f.write(pem)
    public=public_text(key)
    (folder/"chiave-pubblica.txt").write_text(public+"\n")
    print(f"Chiave privata creata: {path}\nFanne subito una copia su una chiavetta: senza, non puoi più emettere licenze.\n")
    print("Chiave pubblica, da mettere nel programma (file tdwin/licenza-chiave-pubblica.txt della repo):\n"+public)

def public_text(key):return b64(key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw))
def load_key(folder):
    path=folder/"chiave-privata.pem"
    if not path.exists():raise SystemExit(f"Manca la chiave privata in {path}: la prima volta lancia  python td-licenze.py chiave")
    pem=path.read_bytes()
    password=None
    if b"ENCRYPTED" in pem:password=getpass.getpass("Password della chiave: ").encode()
    try:return serialization.load_pem_private_key(pem,password)
    except (ValueError,TypeError):raise SystemExit("Password della chiave errata.") from None

def sign(key,data):
    body=b64(json.dumps(data,ensure_ascii=False,separators=(",",":"),sort_keys=True).encode())
    return PREFIX+body+"."+b64(key.sign((PREFIX+body).encode()))

def read(text,public=None):
    """The data of a licence, checked with the public key when given."""
    text="".join(text.split())
    if not text.startswith(PREFIX):raise ValueError("Non è una licenza TDBS1.")
    body,signature=text[len(PREFIX):].split(".")
    if public:
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
        Ed25519PublicKey.from_public_bytes(unb64(public)).verify(unb64(signature),(PREFIX+body).encode())
    return json.loads(unb64(body))

def registry(folder):
    path=folder/"registro-licenze.csv"
    if not path.exists():return []
    with path.open(newline="",encoding="utf-8") as f:return list(csv.DictReader(f))
def record(folder,row):
    path=folder/"registro-licenze.csv";new=not path.exists()
    with path.open("a",newline="",encoding="utf-8") as f:
        w=csv.DictWriter(f,fieldnames=FIELDS)
        if new:w.writeheader()
        w.writerow(row)

def create(folder,intestatario,attivita,postazioni,installazione,scadenza,out):
    intestatario=" ".join(intestatario.split())
    if not intestatario:raise SystemExit("Serve l'intestatario.")
    if attivita<1 or postazioni<0:raise SystemExit("Attività almeno 1, postazioni 0 o più.")
    try:installazione=normalize_code(installazione or "")
    except ValueError as ex:raise SystemExit(str(ex)) from None
    if scadenza:
        try:date.fromisoformat(scadenza)
        except ValueError:raise SystemExit("Scadenza nel formato AAAA-MM-GG, per esempio 2027-12-31.") from None
    key=load_key(folder)
    today=date.today()
    number=1+sum(1 for r in registry(folder) if r["id"].startswith(f"TD-{today.year}-"))
    data={"id":f"TD-{today.year}-{number:04d}","emessa":today.isoformat(),"intestatario":intestatario,"attivita":attivita,
          "postazioni":postazioni,"installazione":installazione,"scadenza":scadenza or ""}
    licence=sign(key,data)
    record(folder,{**data,"licenza":licence})
    out.mkdir(parents=True,exist_ok=True)
    slug=re.sub(r"[^a-z0-9]+","-",intestatario.lower()).strip("-")[:40] or "cliente"
    path=out/f"licenza-{data['id']}-{slug}.txt"
    path.write_text(f"Licenza TD Booking Suite per {intestatario}\n"
        f"Attività: {attivita}, postazioni in rete: {postazioni}, "+(f"solo per l'installazione {installazione}" if installazione else "valida su qualunque installazione")+
        (f", fino al {date.fromisoformat(scadenza):%d/%m/%Y}" if scadenza else ", senza scadenza")+
        "\n\nApri TD Booking Suite, Area agenzia, Licenza, e incolla tutta la riga qui sotto:\n\n"+licence+"\n",encoding="utf-8")
    print(f"\nLicenza {data['id']} per {intestatario}:\n\n{licence}\n\nSalvata anche in {path}")
    return licence

def ask(prompt,default=""):
    value=input(f"{prompt}{f' [{default}]' if default else ''}: ").strip()
    return value or default
def interactive(folder,out):
    print("Nuova licenza TD Booking Suite (Ctrl+C per uscire)\n")
    intestatario=ask("Intestatario, cioè il nome del cliente")
    attivita=int(ask("Quante attività (1 = versione singola, di più = portale)","1"))
    postazioni=int(ask("Quante postazioni in rete oltre al PC server","2"))
    installazione=ask("Codice installazione del cliente (invio = valida ovunque, a stock)")
    scadenza=ask("Scadenza AAAA-MM-GG (invio = per sempre)")
    create(folder,intestatario,attivita,postazioni,installazione,scadenza,out)

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
    if a.cmd=="chiave":make_key(a.cartella)
    elif a.cmd=="crea":create(a.cartella,a.intestatario,a.attivita,a.postazioni,a.installazione,a.scadenza,a.uscita)
    elif a.cmd=="elenco":
        for r in registry(a.cartella):
            print(f"{r['id']}  {r['emessa']}  {r['intestatario']}  attività {r['attivita']}, postazioni {r['postazioni']}  "
                  f"{r['installazione'] or 'a stock'}  {('scade '+r['scadenza']) if r['scadenza'] else 'per sempre'}")
    elif a.cmd=="verifica":
        text=a.licenza or input("Incolla la licenza: ")
        public=(a.cartella/"chiave-pubblica.txt").read_text().strip() if (a.cartella/"chiave-pubblica.txt").exists() else None
        try:print(json.dumps(read(text,public),ensure_ascii=False,indent=2)+("\nFirma valida." if public else "\nFirma non controllata: manca la chiave pubblica."))
        except Exception:raise SystemExit("Licenza non valida o modificata.") from None
    else:
        try:interactive(a.cartella,a.uscita)
        except (KeyboardInterrupt,EOFError):print()
        except ValueError:raise SystemExit("Attività e postazioni sono numeri.") from None

if __name__=="__main__":
    try:main()
    except KeyboardInterrupt:sys.exit(1)
