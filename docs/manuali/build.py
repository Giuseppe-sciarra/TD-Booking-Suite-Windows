"""The two PDF manuals, from the HTML in this folder:

    python docs/manuali/build.py

docs/Istruzioni-TD-Booking-Suite-Windows.pdf  for whoever installs and uses the program
docs/Istruzioni-TD-Licenze.pdf                internal, for the seller only

The screenshots come from docs/screenshots (also used by the READMEs) and become light JPEGs in img/; the ones of
TD Licenze stay in img-licenze/, never in docs/screenshots, which is copied to the public showcase.
Needs Pillow and Playwright for Python with Chromium."""
from datetime import date
from pathlib import Path
from PIL import Image

HERE=Path(__file__).resolve().parent
DOCS=HERE.parent
ROOT=DOCS.parent
VERSION=(ROOT/"suite"/"VERSION").read_text().strip()
MONTHS=["gennaio","febbraio","marzo","aprile","maggio","giugno","luglio","agosto","settembre","ottobre","novembre","dicembre"]
TODAY=date.today()
STAMP=f"{TODAY.day} {MONTHS[TODAY.month-1]} {TODAY.year}"

# how tall a screenshot may be in the manual, in pixels of the 2000 px wide copy: the top is what matters
CROP={"backup":1500,"postazioni":1500,"rete":1500,"guida":1800,"bloccato":1400,"licenza":1500,"primo-avvio":1700}

def images():
    out=HERE/"img";out.mkdir(exist_ok=True)
    for png in sorted((DOCS/"screenshots").glob("*.png")):
        im=Image.open(png).convert("RGB")
        if im.width>2000:im=im.resize((2000,round(im.height*2000/im.width)),Image.LANCZOS)
        limit=CROP.get(png.stem)
        if limit and im.height>limit:im=im.crop((0,0,im.width,limit))
        im=im.resize((1400,round(im.height*1400/im.width)),Image.LANCZOS)
        im.save(out/(png.stem+".jpg"),quality=82,optimize=True,progressive=True)

def pdf(source,target,title):
    from playwright.sync_api import sync_playwright
    html=(HERE/source).read_text("utf-8").replace("{{VERSION}}",VERSION).replace("{{DATE}}",STAMP)
    page_file=HERE/("_"+source)
    page_file.write_text(html,"utf-8")
    footer=('<div style="width:100%;font:8px Helvetica,Arial,sans-serif;color:#6b6079;padding:0 17mm;display:flex;justify-content:space-between">'
            f'<span>{title} · versione {VERSION}</span><span><span class="pageNumber"></span> / <span class="totalPages"></span></span></div>')
    try:
        with sync_playwright() as p:
            browser=p.chromium.launch()
            page=browser.new_page()
            page.goto(page_file.as_uri(),wait_until="networkidle")
            page.evaluate("document.fonts.ready")
            page.pdf(path=str(DOCS/target),format="A4",print_background=True,prefer_css_page_size=True,
                     display_header_footer=True,header_template="<span></span>",footer_template=footer)
            browser.close()
    finally:page_file.unlink(missing_ok=True)
    print(DOCS/target)

if __name__=="__main__":
    images()
    pdf("windows.html","Istruzioni-TD-Booking-Suite-Windows.pdf","TD Booking Suite per Windows · Istruzioni")
    pdf("licenze.html","Istruzioni-TD-Licenze.pdf","TD Licenze · Istruzioni · riservato")
