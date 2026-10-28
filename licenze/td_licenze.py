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

La chiave privata firma le licenze: chi la ha può crearne quante vuole. Resta solo nella cartella del programma
(--cartella, di serie quella di TD Licenze.exe o di questo file), protetta da una password: chiave, registro e
licenze emesse si salvano lì e da nessun'altra parte, così la cartella intera va su una chiavetta.
Il programma contiene solo la chiave pubblica: verifica le licenze ma non può crearle."""
import argparse,base64,csv,getpass,json,os,re
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

def cli_create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,tipo="",sostituisce=""):
    licence,path,data=create(folder,intestatario,attivita,postazioni,installazione,scadenza,out,password_for(folder),tipo,sostituisce)
    print(f"\nLicenza {data['id']} per {data['intestatario']}:\n\n{licence}\n\nSalvata anche in {path}")

def ask(prompt,default=""):
    value=input(f"{prompt}{f' [{default}]' if default else ''}: ").strip()
    return value or default

def main(argv=None):
    p=argparse.ArgumentParser(description="Crea le licenze di TD Booking Suite per Windows.")
    p.add_argument("--cartella",type=Path,default=default_folder(),help="dove stanno chiave privata e registro")
    p.add_argument("--uscita",type=Path,default=None,help="dove salvare i file delle licenze, di serie licenze-emesse nella cartella")
    sub=p.add_subparsers(dest="cmd")
    sub.add_parser("chiave");sub.add_parser("elenco")
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
    except (KeyboardInterrupt,EOFError):print()

if __name__=="__main__":main()
