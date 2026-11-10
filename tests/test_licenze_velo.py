"""TD Licenze per TD Velo: licenze TDVL1. con numerazione propria, sostituzioni, stato online separato. Senza Django."""
import importlib.util,json
from pathlib import Path

ROOT=Path(__file__).resolve().parent.parent
CODE="ABCD-EFGH-IJKL-MNOP"

def load():
    spec=importlib.util.spec_from_file_location("td_licenze_velo",ROOT/"licenze"/"td_licenze.py")
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    return module

def body(tool,signed):
    return json.loads(tool.unb64(signed.split(".",1)[1].split(".")[0]))

def test_velo_licences_state_and_register(tmp_path):
    tool=load();out=tmp_path/"emesse"
    tool.make_key(tmp_path,"")
    public=tool.public_key_text(tmp_path)
    lic,path,data=tool.create(tmp_path,"Studio Prova",None,None,CODE,"",out,None,"","","velo")
    assert lic.startswith("TDVL1.") and data["id"].endswith("-0001") and data["id"].startswith("TDV-") and data["prodotto"]=="TD Velo"
    assert tool.read(lic,public)["installazione"]==CODE and "Apri TD Velo" in path.read_text(encoding="utf-8")
    # TD Booking Suite: numerazione e prefisso suoi, invariati
    lic_b,_,data_b=tool.create(tmp_path,"Bar Roma",1,2,"","",out,None,"server")
    assert lic_b.startswith("TDBS1.") and data_b["id"].startswith("TD-") and data_b["id"].endswith("-0001") and "prodotto" not in data_b
    rows=tool.registry(tmp_path)
    assert [tool.prodotto_of(r) for r in rows]==["velo","booking"] and rows[0]["prodotto"]=="velo"
    assert "TD Velo" in tool.describe(data) and tool.tipo_of(rows[0])=="pc"
    # la sostituzione resta di TD Velo, con il codice della licenza sostituita
    lic2,_,data2=tool.create(tmp_path,"",None,None,"","2030-12-31",out,None,"",data["id"])
    assert lic2.startswith("TDVL1.") and data2["sostituisce"]==data["id"] and data2["installazione"]==CODE and data2["id"].endswith("-0002")
    try:tool.create(tmp_path,"",None,None,"","",out,None,"",data_b["id"],"velo");raise AssertionError("una licenza Velo non sostituisce una Booking")
    except ValueError:pass
    # stato online: un file per programma, chiavi e revoche ricavate con il proprio prefisso
    key=tool.load_key(tmp_path)
    velo=body(tool,tool.state(tool.registry(tmp_path),key,"2026-11-10T10:00:00+00:00","velo"))
    assert velo["prodotto"]=="TD Velo" and velo["revocate"]==[tool.revoked_id(data["id"],"velo")]
    assert list(velo["installazioni"])==[tool.code_digest(CODE,"installazione","velo").hex()]
    booking=body(tool,tool.state(tool.registry(tmp_path),key,"2026-11-10T10:00:00+00:00"))
    assert "prodotto" not in booking and booking["revocate"]==[] and booking["installazioni"]=={}
    # la riga Velo nel registro letto senza la colonna (registro di una versione precedente): riconosciuta dal numero
    assert tool.prodotto_of({"id":data["id"],"licenza":lic})=="velo" and tool.prodotto_of({"id":data_b["id"],"licenza":lic_b})=="booking"
