"""Compares the Python and C# dumps of compat.py / TD.Compat: free times exactly, bookings by their readable data."""
import json,sys
a=json.load(open(sys.argv[1]));b=json.load(open(sys.argv[2]));bad=0
for k in sorted(set(a["slots"])|set(b["slots"])):
    x,y=a["slots"].get(k),b["slots"].get(k)
    if x!=y:
        bad+=1
        if bad<=15:
            if isinstance(x,list) and isinstance(y,list):
                sx={tuple(i) for i in x};sy={tuple(i) for i in y}
                print("DIFF",k,"py-only",sorted(sx-sy)[:4],"cs-only",sorted(sy-sx)[:4],"order" if sx==sy else "")
            else:print("DIFF",k,str(x)[:120],"|",str(y)[:120])
print("slots",len(a["slots"]),"different",bad)
for k in a["bookings"]:
    if k not in b["bookings"]:print("missing booking",k);continue
    x,y=a["bookings"][k],b["bookings"][k]
    for f in ("status","resource","secondary","tables","area","pii","contact","visit"):
        if x[f]!=y[f]:print("BOOKING",k[:8],f,x[f],"|",y[f])
    sx,sy=x["snapshot"],y["snapshot"]
    if sx!=sy:print("SNAP",k[:8],{kk:(sx.get(kk),sy.get(kk)) for kk in set(sx)|set(sy) if sx.get(kk)!=sy.get(kk)})
