"""Build step, after the program is copied into the MSI's folder: the Windows edition's own code (tdwin) becomes
native modules (.pyd on Windows) with Cython, and the seller's public licence key is written inside the compiled
licence check. The .py sources, the key file and the test settings do not go in the MSI: what is shipped cannot
be read or edited as text, and no file or setting can swap the key.

    python windows/compile_tdwin.py <app folder that holds tdwin/> [<public key file>]

Kept as .py: the packages' __init__ files and the migrations (Django reads them, there is no licence logic in them).
Needs Cython, setuptools and a C compiler (MSVC on Windows, the same Python as the embedded one)."""
import base64,os,shutil,sys,tempfile
from pathlib import Path

MARK="    key=public_key()  # compile_tdwin.py: the seller's key\n"

def write_key(tdwin,key):
    source=tdwin/"licensing.py"
    text=source.read_text("utf-8")
    if text.count(MARK)!=1:raise SystemExit("licensing.py: manca il punto dove scrivere la chiave, la build si ferma")
    if key and len(base64.urlsafe_b64decode(key+"="*(-len(key)%4)))!=32:raise SystemExit("Chiave pubblica non valida: 32 byte in base64")
    source.write_text(text.replace(MARK,f"    key={key!r}  # the seller's key, part of the compiled code\n"),"utf-8")

def sources(tdwin):
    return sorted(p for p in tdwin.rglob("*.py") if p.name not in ("__init__.py","test_settings.py") and "migrations" not in p.parts)

def compile_all(app,key):
    from Cython.Build import cythonize
    from setuptools import Extension,setup
    tdwin=app/"tdwin"
    write_key(tdwin,key)
    for name in ("test_settings.py","licenza-chiave-pubblica.txt"):(tdwin/name).unlink(missing_ok=True)
    found=sources(tdwin)
    extensions=[Extension(".".join(p.relative_to(app).with_suffix("").parts),[str(p.relative_to(app))]) for p in found]
    temp=Path(tempfile.mkdtemp(prefix="tdwin-build-"))
    here=os.getcwd();os.chdir(app)
    try:
        # annotation_typing off: annotations stay hints, as in the sources; binding on: functions keep their Python behaviour
        setup(name="tdwin",script_args=["build_ext","--inplace","--build-temp",str(temp),"-q"],
              ext_modules=cythonize(extensions,quiet=True,compiler_directives={"language_level":3,"binding":True,"annotation_typing":False}))
    finally:os.chdir(here);shutil.rmtree(temp,ignore_errors=True)
    missing=[str(p) for p in found if not any(q.suffix in (".pyd",".so") for q in p.parent.glob(p.stem+".*"))]
    if missing:raise SystemExit("Moduli non compilati: "+", ".join(missing))
    for p in found:p.unlink();p.with_suffix(".c").unlink(missing_ok=True)
    for cache in tdwin.rglob("__pycache__"):shutil.rmtree(cache,ignore_errors=True)
    print(f"tdwin: {len(found)} moduli compilati, chiave della licenza {'scritta' if key else 'assente: solo la prova'}")

if __name__=="__main__":
    app=Path(sys.argv[1]).resolve()
    key_file=Path(sys.argv[2]) if len(sys.argv)>2 else None
    compile_all(app,key_file.read_text().strip() if key_file and key_file.exists() else "")
