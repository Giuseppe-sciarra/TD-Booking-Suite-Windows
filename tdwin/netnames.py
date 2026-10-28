"""This PC's names and addresses: the hosts Django accepts, the names in the server certificate and the check
that a request comes from the server PC itself all start from here. No Django: settings import it."""
import socket

def own_addresses():
    found={"127.0.0.1","::1"}
    try:found|={info[4][0].split("%")[0] for info in socket.getaddrinfo(socket.gethostname(),None)}
    except OSError:pass
    return found

def names(extra=()):
    """localhost, the configured names, this PC's name and its addresses, lower case and once each."""
    found=["localhost","127.0.0.1","::1",*extra,socket.gethostname(),socket.getfqdn(),*sorted(own_addresses())]
    seen=[]
    for name in found:
        name=(name or "").strip().lower().split("%")[0]
        if name and name not in seen:seen.append(name)
    return seen
