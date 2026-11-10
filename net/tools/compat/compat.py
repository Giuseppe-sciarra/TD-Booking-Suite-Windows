"""Differential check of the C# port against the Python program on the same PostgreSQL database.
  seed  : a fresh business with every kind of service, hours, exceptions, blocks, bookings
  dump  : what the Python says (free times, bookings as it reads them) -> JSON on stdout
Run from the Windows repo: PYTHONPATH=.:suite DJANGO_SETTINGS_MODULE=tdwin.test_settings DATABASE_URL=... python net/tools/compat/compat.py seed|dump"""
import sys,json,secrets
from datetime import datetime,date,time,timedelta
from zoneinfo import ZoneInfo
import django;django.setup()
from django.utils import timezone
from core.models import *
from core.tenancy import scope
from core.crypto import encrypt
from core.booking import available,create_booking,change_status,visit_slots,BookingError

def seed():
    from django.conf import settings
    t=Tenant.objects.create(slug="cmp",name="Salone Confronto",business_type="parrucchiere")
    tz=ZoneInfo(t.timezone)
    with scope(t):
        loc=Location.objects.create(tenant=t,name="Centro",assign="even",min_gap=15,visit_split="need",visit_wait=30)
        for d in range(6):Schedule.objects.create(tenant=t,weekday=d,start=time(9),end=time(19))
        anna=Resource.objects.create(tenant=t,location=loc,name="Anna")
        bruno=Resource.objects.create(tenant=t,location=loc,name="Bruno",limits={"day_n":3})
        carla=Resource.objects.create(tenant=t,location=loc,name="Carla",priority=2)
        cab=Resource.objects.create(tenant=t,location=loc,name="Cabina 1",secondary=True,group="cabina")
        today=timezone.now().astimezone(tz).date()
        for d in range(1,6):
            Schedule.objects.create(tenant=t,weekday=d,start=time(10),end=time(13),resource=bruno)
            Schedule.objects.create(tenant=t,weekday=d,start=time(14),end=time(18),resource=bruno)
        Schedule.objects.create(tenant=t,weekday=5,start=time(9),end=time(12),resource=carla,every_weeks=2,valid_from=today-timedelta(days=today.weekday()))
        Schedule.objects.create(tenant=t,weekday=2,start=time(15),end=time(19),resource=carla)
        mk=lambda name,minutes,**kw:Service.objects.create(tenant=t,location=loc,name=name,kind="resource",duration=minutes,interval=15,lead_minutes=0,**kw)
        taglio=mk("Taglio",30,price="25.00");colore=mk("Colore",120,pause_start=30,pause_minutes=60,price="60.00");viso=mk("Trattamento viso",45,buffer_after=15)
        for s,rs in ((taglio,(anna,bruno,carla)),(colore,(anna,bruno)),(viso,(carla,cab))):
            for r in rs:ServiceResource.objects.create(tenant=t,service=s,resource=r,**({"duration":40} if s==taglio and r==carla else {}))
        Option.objects.create(tenant=t,service=taglio,name="Lungo",duration=45,price="30.00")
        ExceptionDay.objects.create(tenant=t,date=today+timedelta(days=5),closed=True,label="Chiuso")
        ExceptionDay.objects.create(tenant=t,date=today+timedelta(days=6),start=time(14),end=time(20),resource=anna)
        TimeBlock.objects.create(tenant=t,resource=anna,day=today+timedelta(days=2),start=time(12),end=time(13),reason="break",weekly=True)
        Resource.objects.filter(pk=bruno.pk).update(away_from=today+timedelta(days=8),away_until=today+timedelta(days=9),away_by=carla)
        # restaurant
        rist=Location.objects.create(tenant=t,name="Ristorante",capacity=40,tables="suggest")
        sala=Area.objects.create(tenant=t,location=rist,name="Sala",capacity=30)
        for i in range(1,7):Table.objects.create(tenant=t,area=sala,name=f"T{i}",min_covers=1,max_covers=4 if i<5 else 6,priority=i)
        pranzo=Service.objects.create(tenant=t,location=rist,name="Pranzo",kind="capacity",capacity=30,max_party=8,interval=30,duration=90,lead_minutes=0,turnover=True,party_durations=[[2,60],[6,120]],pace_covers=10,last_arrival=30)
        Schedule.objects.create(tenant=t,weekday=0,start=time(12),end=time(15),service=pranzo)
        for d in range(1,7):Schedule.objects.create(tenant=t,weekday=d,start=time(12),end=time(15),service=pranzo)
        # event
        corso=Service.objects.create(tenant=t,location=loc,name="Corso",kind="event",capacity=5,max_party=3,duration=60,lead_minutes=0)
        for i in (1,3,4):
            a=datetime.combine(today+timedelta(days=i),time(18),tz);Occurrence.objects.create(tenant=t,service=corso,start=a,end=a+timedelta(hours=1),capacity=5)
        # existing bookings
        def at(s,days,h,m=0,**kw):
            p={"service":str(s.pk),"start":datetime.combine(today+timedelta(days=days),time(h,m),tz).isoformat(),"party":kw.pop("party",1),"first_name":kw.pop("fn","Mario"),"last_name":"Rossi","email":kw.pop("email",secrets.token_hex(3)+"@example.invalid"),"phone":"+39333"+str(secrets.randbelow(10**7)).zfill(7),"request_key":secrets.token_urlsafe(24)}
            p.update(kw)
            try:return create_booking(t,p)[0]
            except BookingError as e:print('skip',s.name,days,h,e,file=sys.stderr)
        at(taglio,1,10,resource=str(anna.pk));at(colore,1,11,resource=str(anna.pk));at(taglio,1,10,30)
        at(viso,2,9);at(taglio,2,15,resource=str(bruno.pk));at(taglio,2,16,resource=str(bruno.pk))
        b=at(taglio,3,9,30)
        if b:change_status(t,b,"cancelled",reason="client")
        at(viso,1,15,15);at(taglio,7,10,resource=str(carla.pk))
        at(pranzo,1,12,party=4);at(pranzo,1,12,30,party=6);at(pranzo,2,13,party=2)
        at(corso,1,18,party=3,occurrence="")
    print(json.dumps({"tenant":str(t.pk)}))

def dump():
    t=Tenant.objects.get(slug="cmp");tz=ZoneInfo(t.timezone);today=timezone.now().astimezone(tz).date();out={"slots":{},"bookings":{}}
    with scope(t):
        svc={s.name:s for s in Service.objects.all()};res={r.name:r for r in Resource.objects.all()}
        cases=[]
        for name in ("Taglio","Colore","Trattamento viso","Pranzo","Corso"):
            s=svc[name]
            for desk in (False,True):
                cases.append((name,"",desk,None,1))
                if s.kind=="resource":
                    for r in res.values():
                        if ServiceResource.objects.filter(service=s,resource=r).exists() and not r.secondary:cases.append((name,r.name,desk,None,1))
                    if name=="Taglio":cases.append((name,"",desk,str(Option.objects.get(name="Lungo").pk),1))
                if name=="Pranzo":cases+= [(name,"",desk,None,2),(name,"",desk,None,5),(name,"",desk,None,8)]
        for d in range(0,12):
            day=today+timedelta(days=d)
            for name,rname,desk,opt,party in cases:
                try:r=available(t,svc[name].pk,day,opt,str(res[rname].pk) if rname else None,party,desk=desk)
                except BookingError as e:r="ERR:"+str(e)
                key=f"{day}|{name}|{rname}|{'desk' if desk else 'web'}|{opt or ''}|{party}"
                out["slots"][key]=r if isinstance(r,str) else [[x["start"],x["end"],x["resource"],x["occurrence"]] for x in r]
            v=visit_slots(t,[(svc["Taglio"],None),(svc["Colore"],None)],day)
            out["slots"][f"{day}|VISIT"]=[[x["start"],x["end"],x["resource"],x["resource_name"]] for x in v]
        for b in Booking.objects.all().order_by("created"):
            d=b.data
            out["bookings"][str(b.pk)]={"status":b.status,"start":b.start.isoformat(),"resource":str(b.resource_id or ""),"secondary":str(b.secondary_id or ""),
                "gap":[b.gap_start.isoformat() if b.gap_start else "",b.gap_end.isoformat() if b.gap_end else ""],"tables":b.tables,"area":str(b.area_id or ""),
                "snapshot":{k:v for k,v in b.snapshot.items() if k not in ("undo",)},"pii":{k:v for k,v in d.items() if k!="cancel_token"},
                "contact":(b.contact.data if b.contact else None),"visit":str(b.visit or "")}
    print(json.dumps(out,ensure_ascii=False,sort_keys=True,default=str))

{"seed":seed,"dump":dump}[sys.argv[1]]()
