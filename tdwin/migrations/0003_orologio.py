from django.db import migrations,models
class Migration(migrations.Migration):
    dependencies=[("tdwin","0002_rete")]
    operations=[migrations.CreateModel(name="Orologio",fields=[("id",models.BigAutoField(auto_created=True,primary_key=True,serialize=False,verbose_name="ID")),("visto",models.DateTimeField())])]
