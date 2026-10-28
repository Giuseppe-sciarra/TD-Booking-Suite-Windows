"""TD Licenze: la finestra per creare le licenze di TD Booking Suite per Windows.

GitHub Actions la costruisce come TD Licenze.exe (artefatto TD-Licenze), solo per chi vende il programma.
È portatile: chiave, registro e licenze emesse stanno nella cartella del programma, che si copia intera
su una chiavetta. Lanciata come script usa la cartella di td_licenze.py da riga di comando."""
import os,shutil,subprocess,sys,tkinter as tk
from pathlib import Path
from tkinter import ttk,messagebox,simpledialog
import td_licenze as tool

def resource(name):
    base=Path(getattr(sys,"_MEIPASS",Path(__file__).resolve().parent))
    return base/name

FILES=("chiave-privata.pem","chiave-pubblica.txt","registro-licenze.csv")

def portable_folder():
    """TD Licenze.exe keeps everything next to itself; the script keeps td_licenze.py's folder."""
    return Path(sys.executable).resolve().parent if getattr(sys,"frozen",False) else tool.default_folder()

def adopt_old_key(folder):
    """A key made by an earlier TD Licenze in %APPDATA%\\TD Licenze moves into the program's folder, once."""
    old=tool.default_folder()
    if Path(folder).resolve()==old.resolve() or tool.has_key(folder) or not tool.has_key(old):return None
    folder=Path(folder)
    for name in FILES:
        if (old/name).exists():shutil.copy2(old/name,folder/name)
    if (old/"licenze-emesse").is_dir():shutil.copytree(old/"licenze-emesse",folder/"licenze-emesse",dirs_exist_ok=True)
    # the old copy goes only when the new one is identical: a single place holds the key
    if all((folder/n).read_bytes()==(old/n).read_bytes() for n in FILES if (old/n).exists()):shutil.rmtree(old,ignore_errors=True)
    return old

class App(tk.Tk):
    def __init__(self,folder=None):
        super().__init__()
        self.folder=Path(folder or tool.default_folder());self.out=self.folder/"licenze-emesse";self.password=None
        self.title("TD Licenze");self.minsize(760,560)
        if resource("app.ico").exists() and os.name=="nt":self.iconbitmap(resource("app.ico"))
        style=ttk.Style(self)
        if "vista" in style.theme_names():style.theme_use("vista")
        style.configure("Title.TLabel",font=("Segoe UI",14,"bold"));style.configure("Hint.TLabel",foreground="#6b6079")
        self.tabs=ttk.Notebook(self);self.tabs.pack(fill="both",expand=True,padx=12,pady=12)
        self.new_tab=ttk.Frame(self.tabs,padding=16);self.list_tab=ttk.Frame(self.tabs,padding=16);self.key_tab=ttk.Frame(self.tabs,padding=16)
        self.tabs.add(self.new_tab,text="Nuova licenza");self.tabs.add(self.list_tab,text="Licenze emesse");self.tabs.add(self.key_tab,text="Chiave")
        self.build_new();self.build_list();self.build_key()
        if not tool.has_key(self.folder):self.tabs.select(self.key_tab)

    # new licence
    def build_new(self):
        f=self.new_tab;f.columnconfigure(1,weight=1)
        ttk.Label(f,text="Nuova licenza",style="Title.TLabel").grid(row=0,column=0,columnspan=3,sticky="w",pady=(0,12))
        self.v={"intestatario":tk.StringVar(),"attivita":tk.StringVar(value="1"),"postazioni":tk.StringVar(value="2"),"installazione":tk.StringVar(),"scadenza":tk.StringVar()}
        rows=[("Intestatario","intestatario","il nome del cliente"),("Attività","attivita","1 = versione singola, di più = portale"),
              ("Postazioni in rete","postazioni","oltre al PC server"),("Codice installazione","installazione","dalla pagina Licenza del cliente; vuoto = vale ovunque, a stock"),
              ("Scadenza","scadenza","AAAA-MM-GG; vuota = per sempre")]
        for i,(label,name,hint) in enumerate(rows,start=1):
            ttk.Label(f,text=label).grid(row=i*2-1,column=0,sticky="w",padx=(0,12),pady=(6,0))
            if name in ("attivita","postazioni"):
                field=ttk.Spinbox(f,from_=1 if name=="attivita" else 0,to=999,width=8,textvariable=self.v[name])
                field.grid(row=i*2-1,column=1,sticky="w",pady=(6,0))
            else:
                field=ttk.Entry(f,textvariable=self.v[name]);field.grid(row=i*2-1,column=1,sticky="we",pady=(6,0))
            if name=="intestatario":self.first=field
            ttk.Label(f,text=hint,style="Hint.TLabel").grid(row=i*2,column=1,sticky="w")
        self.create_button=ttk.Button(f,text="Crea la licenza",command=self.create)
        self.create_button.grid(row=11,column=1,sticky="w",pady=14)
        ttk.Label(f,text="Licenza da mandare al cliente").grid(row=12,column=0,columnspan=2,sticky="w")
        self.result=tk.Text(f,height=5,wrap="char",font=("Consolas",10),state="disabled",relief="solid",borderwidth=1)
        self.result.grid(row=13,column=0,columnspan=3,sticky="nsew",pady=(4,8));f.rowconfigure(13,weight=1)
        self.result_info=ttk.Label(f,text="",style="Hint.TLabel");self.result_info.grid(row=14,column=0,columnspan=3,sticky="w")
        buttons=ttk.Frame(f);buttons.grid(row=15,column=0,columnspan=3,sticky="w",pady=(8,0))
        ttk.Button(buttons,text="Copia la licenza",command=lambda:self.copy(self.result.get("1.0","end").strip())).pack(side="left")
        ttk.Button(buttons,text="Apri la cartella delle licenze",command=self.open_folder).pack(side="left",padx=8)
        self.bind("<Return>",lambda e:self.create() if self.tabs.select()==str(self.new_tab) else None)

    def ask_password(self):
        if self.password is not None or not tool.key_encrypted(self.folder):return True
        password=simpledialog.askstring("TD Licenze","Password della chiave",show="*",parent=self)
        if password is None:return False
        try:tool.load_key(self.folder,password)
        except ValueError as ex:messagebox.showerror("TD Licenze",str(ex),parent=self);return False
        self.password=password
        return True

    def create(self):
        if not tool.has_key(self.folder):
            messagebox.showinfo("TD Licenze","Prima crea la chiave delle licenze, nella scheda Chiave.",parent=self);self.tabs.select(self.key_tab);return
        if not self.ask_password():return
        try:
            licence,path,data=tool.create(self.folder,self.v["intestatario"].get(),self.v["attivita"].get() or 0,self.v["postazioni"].get() or 0,
                self.v["installazione"].get(),self.v["scadenza"].get(),self.out,self.password)
        except ValueError as ex:
            messagebox.showerror("TD Licenze","Attività e postazioni sono numeri." if "invalid literal" in str(ex) else str(ex),parent=self);return
        self.result.configure(state="normal");self.result.delete("1.0","end");self.result.insert("1.0",licence);self.result.configure(state="disabled")
        self.result_info.configure(text=f"{data['id']} per {data['intestatario']}. {tool.describe(data)}. Salvata in {path.name}")
        self.copy(licence,quiet=True);self.refresh()

    # issued licences
    def build_list(self):
        f=self.list_tab;f.columnconfigure(0,weight=1);f.rowconfigure(1,weight=1)
        ttk.Label(f,text="Licenze emesse",style="Title.TLabel").grid(row=0,column=0,sticky="w",pady=(0,12))
        columns=("id","emessa","intestatario","attivita","postazioni","installazione","scadenza")
        heads=("Numero","Emessa","Intestatario","Attività","Postazioni","Installazione","Scadenza")
        self.tree=ttk.Treeview(f,columns=columns,show="headings",selectmode="browse")
        for c,h,w in zip(columns,heads,(110,90,200,70,80,170,90)):self.tree.heading(c,text=h);self.tree.column(c,width=w,stretch=c=="intestatario")
        self.tree.grid(row=1,column=0,sticky="nsew")
        bar=ttk.Scrollbar(f,orient="vertical",command=self.tree.yview);bar.grid(row=1,column=1,sticky="ns");self.tree.configure(yscrollcommand=bar.set)
        buttons=ttk.Frame(f);buttons.grid(row=2,column=0,sticky="w",pady=(10,0))
        ttk.Button(buttons,text="Copia la licenza selezionata",command=self.copy_selected).pack(side="left")
        ttk.Button(buttons,text="Apri la cartella delle licenze",command=self.open_folder).pack(side="left",padx=8)
        self.tree.bind("<Double-1>",lambda e:self.copy_selected())
        self.refresh()

    def refresh(self):
        self.rows={}
        self.tree.delete(*self.tree.get_children())
        for r in reversed(tool.registry(self.folder)):
            item=self.tree.insert("","end",values=(r["id"],r["emessa"],r["intestatario"],r["attivita"],r["postazioni"],r["installazione"] or "a stock",r["scadenza"] or "per sempre"))
            self.rows[item]=r["licenza"]

    def copy_selected(self):
        selected=self.tree.selection()
        if selected:self.copy(self.rows[selected[0]])

    # the key
    def build_key(self):
        for child in self.key_tab.winfo_children():child.destroy()
        f=self.key_tab;f.columnconfigure(1,weight=1)
        ttk.Label(f,text="Chiave delle licenze",style="Title.TLabel").grid(row=0,column=0,columnspan=2,sticky="w",pady=(0,12))
        if not tool.has_key(self.folder):
            ttk.Label(f,wraplength=680,justify="left",text="La prima volta crea la chiave che firma le licenze. Resta nella cartella del programma, protetta dalla password: "
                +str(self.folder)+". Copia l'intera cartella su una chiavetta e tienine una copia: senza la chiave non puoi più fare licenze per le installazioni che esistono già.").grid(row=1,column=0,columnspan=2,sticky="w")
            self.p1=tk.StringVar();self.p2=tk.StringVar()
            ttk.Label(f,text="Password della chiave").grid(row=2,column=0,sticky="w",pady=(14,0),padx=(0,12))
            ttk.Entry(f,textvariable=self.p1,show="*").grid(row=2,column=1,sticky="we",pady=(14,0))
            ttk.Label(f,text="Ripeti la password").grid(row=3,column=0,sticky="w",pady=(6,0),padx=(0,12))
            ttk.Entry(f,textvariable=self.p2,show="*").grid(row=3,column=1,sticky="we",pady=(6,0))
            ttk.Button(f,text="Crea la chiave",command=self.make_key).grid(row=4,column=1,sticky="w",pady=14)
            return
        public=tool.public_key_text(self.folder)
        ttk.Label(f,wraplength=680,justify="left",text="La chiave privata è in "+str(tool.key_path(self.folder))+" e non va mai condivisa. "
            "La chiave pubblica qui sotto va nel programma, nel file tdwin/licenza-chiave-pubblica.txt della repo: da quella build in poi accetta le tue licenze.").grid(row=1,column=0,columnspan=2,sticky="w")
        field=ttk.Entry(f,font=("Consolas",10));field.insert(0,public);field.configure(state="readonly")
        field.grid(row=2,column=0,columnspan=2,sticky="we",pady=12)
        buttons=ttk.Frame(f);buttons.grid(row=3,column=0,columnspan=2,sticky="w")
        ttk.Button(buttons,text="Copia la chiave pubblica",command=lambda:self.copy(public)).pack(side="left")
        ttk.Button(buttons,text="Apri la cartella della chiave",command=lambda:self.open_folder(self.folder)).pack(side="left",padx=8)

    def make_key(self):
        if len(self.p1.get())<8:messagebox.showerror("TD Licenze","Usa una password di almeno 8 caratteri.",parent=self);return
        if self.p1.get()!=self.p2.get():messagebox.showerror("TD Licenze","Le due password non coincidono.",parent=self);return
        try:tool.make_key(self.folder,self.p1.get())
        except ValueError as ex:messagebox.showerror("TD Licenze",str(ex),parent=self);return
        self.password=self.p1.get();self.build_key()
        messagebox.showinfo("TD Licenze","Chiave creata. Copia la chiave pubblica nella repo, e la cartella della chiave su una chiavetta.",parent=self)

    # helpers
    def copy(self,text,quiet=False):
        if not text:return
        self.clipboard_clear();self.clipboard_append(text);self.update()
        if not quiet:messagebox.showinfo("TD Licenze","Copiata negli appunti.",parent=self)

    def open_folder(self,folder=None):
        folder=Path(folder or self.out);folder.mkdir(parents=True,exist_ok=True)
        if os.name=="nt":os.startfile(folder)
        else:subprocess.Popen(["xdg-open",str(folder)])

def start():
    folder=portable_folder()
    try:
        folder.mkdir(parents=True,exist_ok=True)
        probe=folder/".scrivibile";probe.write_text("ok");probe.unlink()
    except OSError:
        root=tk.Tk();root.withdraw()
        messagebox.showerror("TD Licenze",f"Non posso scrivere in {folder}. Sposta la cartella di TD Licenze sul Desktop, in Documenti o su una chiavetta.")
        raise SystemExit(1)
    moved=adopt_old_key(folder)
    app=App(folder)
    if moved:app.after(200,lambda:messagebox.showinfo("TD Licenze",f"Ho spostato chiave e registro da {moved} nella cartella del programma: ora è tutto in {folder}.",parent=app))
    return app

if __name__=="__main__":
    app=start()
    if "--prova" in sys.argv:app.update();app.destroy()  # build check: the window opens and closes
    else:app.mainloop()
