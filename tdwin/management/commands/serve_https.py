from django.conf import settings
from django.core.management import call_command
from django.core.management.base import BaseCommand
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
        try:server.start()
        except KeyboardInterrupt:server.stop()
