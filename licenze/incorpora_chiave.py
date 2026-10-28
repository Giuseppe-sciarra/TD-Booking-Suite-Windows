"""Build step of TD Licenze.exe (.github/workflows/windows.yml, job licenze): the seller's private key, from the
repo's secret TD_LICENZE_CHIAVE, and the program's public key, from tdwin/licenza-chiave-pubblica.txt, go into
licenze/chiave_incorporata.py, which PyInstaller packs into the exe. Every TD Licenze downloaded from Actions then
has the key: on a new PC it asks only its password. The secret TD_LICENZE_REGISTRO, optional, is the content of
registro-github.json: the private repo of the register, its token sealed with the same password. With it a new
TD Licenze also finds the register at once.

The key must be encrypted with a password (as TD Licenze makes it): a key without one is refused, because the exe
travels on USB sticks. Without the secret nothing is written and TD Licenze keeps the key in its folder, as before.
licenze/chiave_incorporata.py is in .gitignore: it never goes in the repo."""
import json,os,sys
from pathlib import Path

HERE=Path(__file__).resolve().parent
TARGET=HERE/"chiave_incorporata.py"

def main():
    pem=os.environ.get("TD_LICENZE_CHIAVE","").strip()
    TARGET.unlink(missing_ok=True)
    if not pem:
        print("::notice::Manca il secret TD_LICENZE_CHIAVE: TD Licenze tiene la chiave nella sua cartella");return
    if "BEGIN ENCRYPTED PRIVATE KEY" not in pem:
        raise SystemExit("TD_LICENZE_CHIAVE deve essere la chiave privata cifrata con la password (chiave-privata.pem di TD Licenze)")
    public=(HERE.parent/"tdwin"/"licenza-chiave-pubblica.txt").read_text().strip()
    registro=os.environ.get("TD_LICENZE_REGISTRO","").strip()
    if registro:
        try:cfg=json.loads(registro)
        except ValueError:cfg=None
        if not isinstance(cfg,dict) or not {"repo","sale","nonce","token"}<=cfg.keys():
            raise SystemExit("TD_LICENZE_REGISTRO deve essere il contenuto di registro-github.json, dalla cartella di TD Licenze collegata alla repo")
        registro=json.dumps(cfg)
    TARGET.write_text(f"# written by incorpora_chiave.py at build time, never in the repo\nCHIAVE={pem+chr(10)!r}\nPUBBLICA={public!r}\nREGISTRO={registro!r}\n",encoding="utf-8")
    print("Chiave delle licenze scritta dentro TD Licenze"+(", con la repo del registro" if registro else ""))

if __name__=="__main__":
    main();sys.exit(0)
