import threading
from django.core.management import call_command
from django.core.management.base import BaseCommand
class Command(BaseCommand):
    help="Il lavoro in background: email e promemoria del core, più i backup. È il servizio TD Booking Suite Worker."
    def handle(self,*a,**kw):
        from tdwin import backup,licensing,rete
        backup.clean_work()
        licensing.apply(force=True)
        rete.apply(force=True)
        threading.Thread(target=rete.keep,daemon=True,name="rete").start()
        threading.Thread(target=backup.loop,daemon=True,name="backup").start()
        threading.Thread(target=licensing.keep_online,daemon=True,name="licenze").start()  # 2.13.0
        call_command("mail_worker")
