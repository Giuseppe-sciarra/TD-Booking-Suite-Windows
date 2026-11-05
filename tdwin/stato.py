"""2.20.8: Stato del programma: the slow pages and the stuck window, written by themselves, and the logs in one zip for
support, without the terminal. The web service writes log/lente.log (its folder is the one it may write); the window
of the program reports a page it waited for more than 5 seconds (a first time, the antivirus, the window itself)."""
import io,json,zipfile
from datetime import timedelta
from django.conf import settings
from django.http import HttpResponse,JsonResponse
from django.shortcuts import render
from django.utils import timezone
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_POST
from core.site import staff_only

def folder():return settings.TDWIN_DATA/"log"
def path():return folder()/"lente.log"

def note(where,what,seconds):
    """One line, newest at the end: when, who saw it (programma or finestra), the page without its query, how long."""
    try:
        folder().mkdir(parents=True,exist_ok=True)
        if path().exists() and path().stat().st_size>2_000_000:path().replace(folder()/"lente.1.log")  # shortcut: one old file kept, enough for weeks
        with open(path(),"a",encoding="utf-8") as f:f.write(f"{timezone.localtime():%d/%m/%Y %H:%M:%S}\t{where}\t{what[:200]}\t{seconds:.1f}\n")
    except OSError:pass

def lines(limit=200):
    rows=[]
    for p in (folder()/"lente.1.log",path()):
        try:rows+=p.read_text("utf-8",errors="replace").splitlines()
        except OSError:pass
    out=[]
    for r in rows[-limit:]:
        parts=r.split("\t")
        if len(parts)==4:out.append({"at":parts[0],"where":parts[1],"page":parts[2],"seconds":parts[3]})
    return list(reversed(out))

def recent(hours=24):
    since=timezone.localtime()-timedelta(hours=hours)
    n=0
    for r in lines(500):
        try:at=timezone.make_aware(timezone.datetime.strptime(r["at"],"%d/%m/%Y %H:%M:%S"))
        except ValueError:continue
        if at>=since:n+=1
    return n

def page(request):
    staff_only(request)
    if request.GET.get("scarica"):
        buf=io.BytesIO()
        with zipfile.ZipFile(buf,"w",zipfile.ZIP_DEFLATED) as z:
            for p in sorted(folder().glob("*.log")):
                try:z.writestr(p.name,p.read_bytes()[-5_000_000:])
                except OSError:pass
        response=HttpResponse(buf.getvalue(),content_type="application/zip")
        response["Content-Disposition"]=f'attachment; filename="log-td-booking-suite-{timezone.localdate():%Y-%m-%d}.zip"'
        return response
    return render(request,"tdwin/stato.html",{"rows":lines(),"day":recent(),"version":settings.VERSION})

@csrf_exempt
@require_POST
def slow(request):
    """The window's own report: only from who is signed in, only a path and a number, nothing else is kept."""
    if not request.user.is_authenticated:return JsonResponse({},status=403)
    try:data=json.loads(request.body[:2000]);page=str(data.get("p",""))[:200];ms=float(data.get("ms",0))
    except (ValueError,TypeError,AttributeError):return JsonResponse({},status=400)
    if page.startswith("/") and 0<ms<3_600_000:note("finestra",page,ms/1000)
    return JsonResponse({})
