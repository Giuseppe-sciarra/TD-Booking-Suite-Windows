"""Small FTP, FTPS and SFTP servers on localhost, for the backup destination tests."""
import datetime,os,socket,threading
from pathlib import Path
import paramiko
from cryptography import x509
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID

def self_signed(path):
    key=ec.generate_private_key(ec.SECP256R1());name=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,"localhost")])
    now=datetime.datetime.now(datetime.timezone.utc)
    cert=x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now).not_valid_after(now+datetime.timedelta(days=2)).sign(key,hashes.SHA256())
    path.write_bytes(key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())+cert.public_bytes(serialization.Encoding.PEM))
    return path

def _serve_ftp(root,tls,certfile,ports):
    from pyftpdlib.authorizers import DummyAuthorizer
    from pyftpdlib.handlers import FTPHandler,TLS_FTPHandler
    from pyftpdlib.servers import FTPServer
    auth=DummyAuthorizer();auth.add_user("bk","segreta-ftp",str(root),perm="elradfmwMT")
    handler=type("Handler",(TLS_FTPHandler if tls else FTPHandler,),{"authorizer":auth})
    if tls:handler.certfile=str(certfile);handler.tls_control_required=True;handler.tls_data_required=True
    server=FTPServer(("127.0.0.1",0),handler)
    ports.put(server.socket.getsockname()[1])
    server.serve_forever(timeout=0.2)

class FTPProcess:
    """pyftpdlib in its own process: its asyncore loop shares global state that threads trip over."""
    def __init__(self,root,tls=False,certfile=None):
        import multiprocessing
        context=multiprocessing.get_context("spawn" if os.name=="nt" else "fork");ports=context.Queue()
        self.process=context.Process(target=_serve_ftp,args=(root,tls,certfile,ports),daemon=True);self.process.start()
        self.port=ports.get(timeout=20)
    def close_all(self):self.process.terminate();self.process.join(5)

def ftp(root,tls=False,certfile=None):
    server=FTPProcess(root,tls,certfile)
    return server,server.port

class Auth(paramiko.ServerInterface):
    def check_auth_password(self,username,password):
        return paramiko.AUTH_SUCCESSFUL if (username,password)==("bk","segreta-sftp") else paramiko.AUTH_FAILED
    def get_allowed_auths(self,username):return "password"
    def check_channel_request(self,kind,chanid):return paramiko.OPEN_SUCCEEDED if kind=="session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

class Files(paramiko.SFTPServerInterface):
    root=None
    def _real(self,path):return str(Path(self.root)/self.canonicalize(path).lstrip("/"))
    def list_folder(self,path):
        try:
            out=[]
            for name in os.listdir(self._real(path)):
                attr=paramiko.SFTPAttributes.from_stat(os.stat(os.path.join(self._real(path),name)));attr.filename=name;out.append(attr)
            return out
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    def stat(self,path):
        try:return paramiko.SFTPAttributes.from_stat(os.stat(self._real(path)))
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    lstat=stat
    def open(self,path,flags,attr):
        try:
            fd=os.open(self._real(path),flags|getattr(os,"O_BINARY",0),0o600)
            handle=paramiko.SFTPHandle(flags)
            mode="wb" if flags&os.O_WRONLY else "r+b" if flags&os.O_RDWR else "rb"
            handle.readfile=handle.writefile=os.fdopen(fd,mode)
            return handle
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    def remove(self,path):
        try:os.remove(self._real(path));return paramiko.SFTP_OK
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    def rename(self,old,new):
        try:os.rename(self._real(old),self._real(new));return paramiko.SFTP_OK
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)
    posix_rename=rename
    def mkdir(self,path,attr):
        try:os.mkdir(self._real(path));return paramiko.SFTP_OK
        except OSError as e:return paramiko.SFTPServer.convert_errno(e.errno)

def sftp(root,host_key=None):
    host_key=host_key or paramiko.ECDSAKey.generate()
    Files.root=str(root)
    listener=socket.socket();listener.bind(("127.0.0.1",0));listener.listen(5)
    def serve():
        while True:
            try:conn,_=listener.accept()
            except OSError:return
            t=paramiko.Transport(conn);t.add_server_key(host_key)
            t.set_subsystem_handler("sftp",paramiko.SFTPServer,Files)
            t.start_server(server=Auth())
    threading.Thread(target=serve,daemon=True).start()
    return listener,listener.getsockname()[1]
