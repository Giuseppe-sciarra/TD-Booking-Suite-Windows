import uuid
from datetime import time,timedelta
from django.db import models

class Prova(models.Model):
    """The 7-day trial: one row, activated once and never again."""
    attivata=models.DateTimeField()
    da=models.CharField(max_length=254,blank=True)
    @property
    def fine(self):
        from .licensing import TRIAL_DAYS
        return self.attivata+timedelta(days=TRIAL_DAYS)

class Postazione(models.Model):
    """A PC with the client program, or a browser, allowed to open the program. The server PC itself needs none."""
    id=models.UUIDField(primary_key=True,default=uuid.uuid4,editable=False)
    nome=models.CharField(max_length=80)
    codice=models.CharField(max_length=64,blank=True,db_index=True)  # sha256 of the 6-digit pairing code, empty once paired
    scadenza_codice=models.DateTimeField(null=True)
    tentativi=models.PositiveIntegerField(default=0)
    token=models.CharField(max_length=64,null=True,unique=True)  # sha256 of the workstation cookie
    creata=models.DateTimeField(auto_now_add=True)
    abbinata=models.DateTimeField(null=True)
    ultimo_accesso=models.DateTimeField(null=True)
    ultimo_ip=models.GenericIPAddressField(null=True)

class ImpostazioniBackup(models.Model):
    """One row: when the nightly backup runs and the key that encrypts it."""
    attivo=models.BooleanField(default=False)
    ora=models.TimeField(default=time(1,30))
    conserva_locali=models.PositiveIntegerField(default=7)
    chiave=models.TextField(blank=True)  # derived from the backup password, sealed with the master key
    sale=models.CharField(max_length=32,blank=True)
    password_impostata=models.DateTimeField(null=True)
    richiesto=models.DateTimeField(null=True)  # "Esegui ora" from the page; the worker runs it
    ultimo_giorno=models.DateField(null=True)  # day of the last scheduled run
    @classmethod
    def get(cls):return cls.objects.get_or_create(pk=1)[0]

class Destinazione(models.Model):
    TIPI=[("cartella","Cartella o disco esterno"),("rete","Cartella di rete, NAS"),("ftp","FTP"),("ftps","FTPS, FTP con TLS"),("sftp","SFTP")]
    id=models.UUIDField(primary_key=True,default=uuid.uuid4,editable=False)
    nome=models.CharField(max_length=80)
    tipo=models.CharField(max_length=10,choices=TIPI)
    attiva=models.BooleanField(default=True)
    percorso=models.CharField(max_length=400)
    host=models.CharField(max_length=255,blank=True)
    porta=models.PositiveIntegerField(null=True,blank=True)
    utente=models.CharField(max_length=255,blank=True)
    password=models.TextField(blank=True)  # sealed with the master key
    impronta=models.CharField(max_length=128,blank=True)  # server key or certificate seen the first time, then required
    conserva=models.PositiveIntegerField(default=14)
    prova_richiesta=models.DateTimeField(null=True)
    ultimo_esito=models.TextField(blank=True)
    ultimo_controllo=models.DateTimeField(null=True)
    creata=models.DateTimeField(auto_now_add=True)

class Esecuzione(models.Model):
    inizio=models.DateTimeField()
    fine=models.DateTimeField(null=True)
    esito=models.CharField(max_length=10,default="in corso")  # in corso, ok, parziale, errore
    file=models.CharField(max_length=80,blank=True)
    dimensione=models.BigIntegerField(default=0)
    dettagli=models.TextField(blank=True)
    manuale=models.BooleanField(default=False)
    class Meta:ordering=["-inizio"]
