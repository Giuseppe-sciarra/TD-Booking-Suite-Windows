from django.conf import settings
from django.core.management import call_command
from django.core.management.base import BaseCommand
def warm():
    """2.20.7: every module and template the pages may load, read once at the start. On a shop's PC the first time of
    each action (connect the register, its test, a report) waited, even for minutes, while Windows looked at the files
    it opened for the first time; then it never did again. Now that wait happens here, before anyone uses the program."""
    import importlib,os,pkgutil
    for pkg in ("core","tdwin"):
        for m in pkgutil.walk_packages(importlib.import_module(pkg).__path__,pkg+"."):
            if "test" in m.name or ".migrations" in m.name:continue
            try:importlib.import_module(m.name)
            except Exception:pass  # a module that does not load here fails the same on its page, with its error
    for name in ("serial","serial.tools.list_ports"):
        try:importlib.import_module(name)
        except Exception:pass
    try:
        from core.cassa import ponte
        ponte()
        from django.template import engines
        from django.template.loader import get_template
        for folder in engines["django"].template_dirs:
            for root,_,files in os.walk(folder):
                for f in files:
                    if f.endswith(".html"):
                        try:get_template(os.path.relpath(os.path.join(root,f),folder).replace(os.sep,"/"))
                        except Exception:pass
    except Exception:pass

class Command(BaseCommand):
    help="Avvia il programma in HTTPS sulla porta di config.json: è il servizio TD Booking Suite Web."
    def handle(self,*a,**kw):
        from cheroot import wsgi
        from cheroot.ssl.builtin import BuiltinSSLAdapter
        from django.core.wsgi import get_wsgi_application
        from tdwin import licensing,rete
        # first the edition from the licence: the core's check refuses Private with more than one business
        licensing.apply(force=True)
        rete.apply(force=True)
        cert,key=rete.paths()
        call_command("check_runtime")
        address=(settings.TDWIN_CONFIG.get("ascolto","0.0.0.0"),int(settings.TDWIN_CONFIG.get("porta",8443)))
        server=wsgi.Server(address,get_wsgi_application(),numthreads=int(settings.TDWIN_CONFIG.get("thread",16)),server_name="TD Booking Suite")
        server.ssl_adapter=BuiltinSSLAdapter(str(cert),str(key))
        rete.ADAPTERS.append(server.ssl_adapter);rete.served(cert,key)
        self.stdout.write(f"In ascolto su https://{address[0]}:{address[1]}, indirizzo pubblico {settings.PUBLIC_ORIGIN}")
        import threading
        threading.Thread(target=warm,daemon=True,name="warm").start()
        try:server.start()
        except KeyboardInterrupt:server.stop()
