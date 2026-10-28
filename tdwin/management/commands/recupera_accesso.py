from django.core.management.base import BaseCommand,CommandError
from django.db import transaction
from core.models import User,Membership,audit
from core.tenancy import scope
from core.platform import invitation
class Command(BaseCommand):
    help=("Password dimenticata, dal PC server come amministratore di Windows: per l'amministratore del programma o per "
          "chiunque abbia un account (il titolare, una persona dell'attività). Revoca sessioni, Authenticator e passkey e "
          "stampa il link, valido 24 ore, per scegliere la nuova password.")
    def add_arguments(self,p):
        p.add_argument("--email",required=True,help="username o email dell'account");p.add_argument("--conferma",action="store_true",help="dopo aver controllato chi lo chiede")
    def handle(self,*a,**kw):
        if not kw["conferma"]:raise CommandError("Serve --conferma, dopo aver controllato chi chiede il recupero.")
        with transaction.atomic():
            u=User.objects.by_login(kw["email"]).select_for_update().filter(is_active=True).first()
            if not u:raise CommandError("Nessun account con questo username o email.")
            u.auth_version+=1;u.totp="";u.recovery=[];u.set_unusable_password();u.save();u.passkeys.all().delete()
            # in the Registro attività of each business: whoever picks the new password is not the one who had it
            for m in Membership.objects.filter(user=u).select_related("tenant"):
                with scope(m.tenant):audit(m.tenant,None,"member.password_reset",u.pk,via="console")
            self.stdout.write(("amministratore " if u.is_staff else "utente ")+invitation(u))
