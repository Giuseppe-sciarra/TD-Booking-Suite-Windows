"""The two PDF manuals, from the HTML in this folder:

    python docs/manuali/build.py

docs/Istruzioni-TD-Booking-Suite-Windows.pdf  for whoever installs and uses the program
docs/Guida-TD-Booking-Suite-per-il-negozio.pdf the same without installing and configuring, for the shop (cliente.html, made here)
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

# 2.20.5: the guide for whoever only uses the program in the shop: no installing, configuring, licence or technician
CLIENT=[("account","Gli account e l'accesso","entrare, uscire, chi vede cosa",["Il secondo fattore"]),
        ("lavoro","Il lavoro di ogni giorno","agenda, clienti, storico, promemoria",["Promemoria impostati dall'amministratore","Importare il listino dal programma di prima","Importare i clienti dal programma di prima","Aspetto"]),
        ("cassa","La cassa","conto, scontrini, chiusura, annulli",["Collegare la cassa"]),
        ("problemi","Se qualcosa non va","",[])]
CLIENT_ROWS=("Una persona ha dimenticato la password","Il programma è lento","Il server non risponde e serve l'agenda","Uno scontrino è")

def client():
    """cliente.html, made from windows.html: its chapters for the shop, numbered again, with the subsections above left out."""
    import re
    html=(HERE/"windows.html").read_text("utf-8")
    head,body=html.split('<section class="toc">',1)
    head=head.replace('<div class="kicker">Istruzioni · ','<div class="kicker">Guida per il negozio · ')
    head=re.sub(r'<p class="sub">.*?</p>','<p class="sub">Come si usa ogni giorno: entrare, l\'agenda, i clienti e il loro storico, la cassa e gli scontrini. Per l\'installazione e le impostazioni c\'è l\'assistenza.</p>',head,flags=re.S)
    chapters=dict(re.findall(r'(<h2 id="([a-z]+)">.*?)(?=<h2 id=|</body>)',body,flags=re.S)[i][::-1] for i in range(len(re.findall(r'<h2 id="',body))))
    toc,out=[],[]
    for n,(key,title,small,drop) in enumerate(CLIENT,1):
        text=chapters[key]
        text=re.sub(r'<h2 id="[a-z]+"><span class="num">\d+</span>[^<]*</h2>',f'<h2 id="{key}"><span class="num">{n}</span>{title}</h2>',text,count=1)
        for h in drop:text=re.sub(r'<h3>'+re.escape(h)+r'</h3>.*?(?=<h3>|$)','',text,flags=re.S)
        if key=="problemi":text=re.sub(r'<tr><td>(?!Cosa succede)(.*?)</td>.*?</tr>\s*',lambda m:m.group(0) if m.group(1).startswith(CLIENT_ROWS) else "",text,flags=re.S)
        out.append(text)
        toc.append(f'    <li><span>{n}</span><a href="#{key}">{title}</a>'+(f'<small>{small}</small>' if small else '')+'</li>')
    numbers={key:n for n,(key,*_) in enumerate(CLIENT,1)}
    first={int(n):k for k,n in re.findall(r'<h2 id="([a-z]+)"><span class="num">(\d+)</span>',body)}
    out=[re.sub(r' ?\(capitolo (\d+)\)',lambda m:f" (capitolo {numbers[first[int(m.group(1))]]})" if first.get(int(m.group(1))) in numbers else "",t) for t in out]
    page=head+'<section class="toc">\n  <h2>Indice</h2>\n  <ol>\n'+"\n".join(toc)+'\n  </ol>\n</section>\n\n'+"".join(out)
    page+='<div class="note"><strong>Assistenza</strong>Per installare, aggiornare, collegare la cassa o il sito, i backup e tutto quello che qui non c\'è, chiama Tastiere Digitali: tastieredigitali.tech</div>\n</body>\n</html>\n'
    (HERE/"cliente.html").write_text(page,"utf-8")

if __name__=="__main__":
    client()
    images()
    pdf("windows.html","Istruzioni-TD-Booking-Suite-Windows.pdf","TD Booking Suite per Windows · Istruzioni")
    pdf("cliente.html","Guida-TD-Booking-Suite-per-il-negozio.pdf","TD Booking Suite · Guida per il negozio")
    pdf("licenze.html","Istruzioni-TD-Licenze.pdf","TD Licenze · Istruzioni · riservato")
