from django.db import migrations,models
class Migration(migrations.Migration):
    dependencies=[("tdwin","0003_orologio")]
    operations=[migrations.CreateModel(name="Aggiornamenti",fields=[("id",models.BigAutoField(auto_created=True,primary_key=True,serialize=False,verbose_name="ID")),("modo",models.CharField(choices=[("avvisa","Avvisa quando c'è una versione nuova"),("spento","Spento")],default="avvisa",max_length=10)),("ultimo_controllo",models.DateTimeField(null=True)),("esito",models.TextField(blank=True)),("disponibile",models.CharField(blank=True,max_length=20))])]
