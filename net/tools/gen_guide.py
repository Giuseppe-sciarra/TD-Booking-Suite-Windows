"""The Guida of the web program, rendered once for the Windows edition (administrator or not, second factor on or
off) and written as a tree of JSON the window draws: net/src/TD.Server/Guide/guida-<staff><mfa>.json.
§r§ and §t§ are filled by the server with the role and the business's name.
Run from suite/: PYTHONPATH=.:.. DJANGO_SETTINGS_MODULE=tdwin.test_settings python ../net/tools/gen_guide.py"""
import json,pathlib,sys
from html.parser import HTMLParser
import django;django.setup()
from django.conf import settings
from django.template import engines
from types import SimpleNamespace

src=(pathlib.Path(settings.BASE_DIR)/"templates"/"guide.html").read_text()
for cut in ("{% extends 'base.html' %}","{% block content %}","{% endblock %}"):src=src.replace(cut,"")
template=engines["django"].from_string(src)

class Tree(HTMLParser):
    def __init__(self):super().__init__(convert_charrefs=True);self.root={"t":"root","c":[]};self.stack=[self.root]
    def handle_starttag(self,tag,attrs):
        node={"t":tag,"c":[]};a=dict(attrs)
        for k in ("id","class","href"):
            if a.get(k):node[k]=a[k]
        self.stack[-1]["c"].append(node);self.stack.append(node)
    def handle_endtag(self,tag):
        while len(self.stack)>1:
            if self.stack.pop()["t"]==tag:break
    def handle_data(self,data):
        if data.strip() or (data and self.stack[-1]["t"] not in ("root","div","section","ol","ul","dl","table","tbody","thead","tr","nav")):self.stack[-1]["c"].append(data)

out=pathlib.Path(__file__).resolve().parents[1]/"src"/"TD.Server"/"Guide";out.mkdir(exist_ok=True)
for staff in (False,True):
    for mfa in (False,True):
        html=template.render({"staff":staff,"role":"" if staff else "§r§","tenant":SimpleNamespace(name="§t§"),"mfa_enabled":mfa,"usernames":settings.LOGIN_USERNAMES,
            "edition":settings.EDITION,"edition_label":settings.EDITION_LABEL,"agency":settings.AGENCY_NAMES})
        t=Tree();t.feed(html);t.close()
        (out/f"guida-{int(staff)}{int(mfa)}.json").write_text(json.dumps(t.root,ensure_ascii=False,separators=(",",":")))
        print(staff,mfa,len(html))
