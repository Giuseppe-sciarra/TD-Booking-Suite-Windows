"""Prints {table: {column: default}} of the Django models (Django sets them in Python, not in the database): run with
PYTHONPATH=.:suite DJANGO_SETTINGS_MODULE=tdwin.test_settings python net/tools/django_defaults.py > net/tools/defaults.json"""
import json,django,uuid,decimal,datetime
django.setup()
from django.apps import apps
from django.db.models.fields import NOT_PROVIDED
out={}
for m in apps.get_models():
    t=m._meta.db_table;d={}
    for f in m._meta.concrete_fields:
        col=f.column
        if getattr(f,"auto_now_add",False) or getattr(f,"auto_now",False):d[col]={"now":True};continue
        if f.default is NOT_PROVIDED:continue
        v=f.default
        if callable(v):
            if v is uuid.uuid4:d[col]={"uuid":True};continue
            if getattr(v,"__name__","")=="now":d[col]={"now":True};continue
            v=v()
        if isinstance(v,(dict,list)):d[col]={"json":json.dumps(v,ensure_ascii=False,separators=(",",":"))}
        elif isinstance(v,bool):d[col]={"bool":v}
        elif isinstance(v,(int,float,decimal.Decimal)):d[col]={"num":str(v)}
        elif isinstance(v,str):d[col]={"str":v}
        elif isinstance(v,datetime.time):d[col]={"time":v.isoformat()}
        elif v is None:pass
        else:d[col]={"str":str(v)}
    out[t]=d
print(json.dumps(out,ensure_ascii=False,indent=0))
