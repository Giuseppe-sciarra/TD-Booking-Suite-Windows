"""First configuration files of a server installation: run by windows/setup-server.ps1, without Django.

    python -m tdwin.setup_files --data "C:\\ProgramData\\TD Booking Suite" --origine https://ufficio-pc:8443 --pg-bin "C:\\Program Files\\TD Booking Suite\\pgsql\\bin"

Existing files are never replaced: keys and passwords made at the first installation stay for its whole life.
An update only changes address, port and paths when they are given again."""
import argparse,base64,json,os,secrets,sys
from pathlib import Path
from urllib.parse import urlparse

def key():return base64.urlsafe_b64encode(secrets.token_bytes(32)).decode()

def replace_json(path,data):
    """All or nothing: config.json holds the only copy of the keys. The new file inherits the folder's rights."""
    temporary=Path(str(path)+".tmp")
    with open(temporary,"w",encoding="utf-8") as f:json.dump(data,f,ensure_ascii=False,indent=1);f.flush();os.fsync(f.fileno())
    os.replace(temporary,path)

def write_new(path,data):
    with os.fdopen(os.open(path,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600),"w",encoding="utf-8") as f:json.dump(data,f,ensure_ascii=False,indent=1)

def default_origin(config):
    """The address the program gets when nobody gives one: this PC by name for a server, localhost for Un PC solo."""
    import socket
    host="localhost" if config.get("modalita")=="standalone" else socket.gethostname().lower()
    return f"https://{host}"+("" if config["porta"]==443 else f":{config['porta']}")

def prepare(data,origine=None,porta=None,indirizzi=None,pg_bin=None,pg_port=55432,modalita=None):
    data=Path(data);data.mkdir(parents=True,exist_ok=True)
    for folder in ("licenza","backup","certs","log","tmp"):(data/folder).mkdir(exist_ok=True)
    admin_path=data/"db-admin.json";config_path=data/"config.json"
    if not admin_path.exists():
        write_new(admin_path,{"url":f"postgresql://postgres:{secrets.token_urlsafe(24)}@127.0.0.1:{pg_port}/tdbooking","app_password":secrets.token_urlsafe(24)})
    admin=json.loads(admin_path.read_text("utf-8"))
    if config_path.exists():
        config=json.loads(config_path.read_text("utf-8"))
    else:
        config={"origine":"","indirizzi":[],"porta":8443,"install_id":secrets.token_hex(16),"pg_bin":"",
                "env":{"DJANGO_SECRET_KEY":secrets.token_urlsafe(64),"TDBS_KEYS":json.dumps({"v1":key(),"index":key()},separators=(",",":")),"TDBS_ACTIVE_KEY":"v1",
                       "DATABASE_URL":f"postgresql://tdbooking_app:{admin['app_password']}@127.0.0.1:{pg_port}/tdbooking","TDBS_DEV":"0","TRUST_PROXY":"0"}}
    automatic=not config["origine"] or config["origine"]==default_origin(config)
    if porta:config["porta"]=int(porta)
    # Un PC solo listens on this PC only; a server on the network. Not given: as before (a 2.8 server is a server)
    if modalita:
        if modalita not in ("standalone","server"):raise SystemExit("Modalità non valida: standalone o server.")
        config["modalita"]=modalita
    config.setdefault("modalita","server")
    config["ascolto"]="127.0.0.1" if config["modalita"]=="standalone" else "0.0.0.0"
    if automatic and not origine:config["origine"]=""
    if origine:
        origine=origine.rstrip("/")
        parsed=urlparse(origine)
        if parsed.scheme!="https" or not parsed.hostname or parsed.path:raise SystemExit("L'indirizzo deve essere come https://nome-del-pc:8443")
        config["origine"]=origine
    if not config["origine"]:config["origine"]=default_origin(config)
    if indirizzi is not None:config["indirizzi"]=[h.strip() for h in indirizzi.split(",") if h.strip()]
    if pg_bin:config["pg_bin"]=str(pg_bin)
    if config_path.exists():replace_json(config_path,config)
    else:write_new(config_path,config)
    return config,admin

def main(argv=None):
    p=argparse.ArgumentParser()
    p.add_argument("--data",required=True);p.add_argument("--origine");p.add_argument("--porta",type=int)
    p.add_argument("--indirizzi",help="altri nomi o indirizzi, separati da virgola");p.add_argument("--pg-bin");p.add_argument("--pg-port",type=int,default=55432)
    p.add_argument("--modalita",help="standalone (un PC solo) o server (con postazioni)")
    a=p.parse_args(argv)
    config,admin=prepare(a.data,a.origine,a.porta,a.indirizzi,a.pg_bin,a.pg_port,a.modalita)
    print(json.dumps({"origine":config["origine"],"porta":config["porta"],"modalita":config["modalita"]}))

if __name__=="__main__":main(sys.argv[1:])
