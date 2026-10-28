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
    return tool.default_folder()

def adopt_old_key(folder):
    """A key made by an earlier TD Licenze in %APPDATA%\\TD Licenze moves into the program's folder, once."""
    old=tool.legacy_folder()
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
        self.title("TD Licenze");self.minsize(1000,620)
        if resource("app.ico").exists() and os.name=="nt":self.iconbitmap(resource("app.ico"))
        style=ttk.Style(self)
        if "vista" in style.theme_names():style.theme_use("vista")
        style.configure("Title.TLabel",font=("Segoe UI",14,"bold"));style.configure("Hint.TLabel",foreground="#6b6079")
        self.tabs=ttk.Notebook(self);self.tabs.pack(fill="both",expand=True,padx=12,pady=12)
        self.new_tab=ttk.Frame(self.tabs,padding=16);self.list_tab=ttk.Frame(self.tabs,padding=16);self.key_tab=ttk.Frame(self.tabs,padding=16)
        self.sync_tab=ttk.Frame(self.tabs,padding=16)
        self.tabs.add(self.new_tab,text="Nuova licenza");self.tabs.add(self.list_tab,text="Licenze emesse");self.tabs.add(self.key_tab,text="Chiave")
        self.tabs.add(self.sync_tab,text="Registro su GitHub")
        self.sync_status=tk.StringVar(value="")
        self.build_new();self.build_list();self.build_key();self.build_sync()
        if not tool.has_key(self.folder):self.tabs.select(self.key_tab)
        # with a repo connected, the register of every PC is brought here at once (the password opens the token)
        elif tool.sync_settings(self.folder):self.after(300,lambda:self.sync(ask=True))

    # new licence
    def build_new(self):
        f=self.new_tab;f.columnconfigure(1,weight=1)
        ttk.Label(f,text="Nuova licenza",style="Title.TLabel").grid(row=0,column=0,columnspan=3,sticky="w",pady=(0,4))
        self.replacing=None
        self.replace_info=ttk.Label(f,text="",style="Hint.TLabel",wraplength=640,justify="left");self.replace_info.grid(row=1,column=0,columnspan=2,sticky="w")
        self.replace_reset=ttk.Button(f,text="Nuova da zero",command=self.reset_form)
        self.v={"tipo":tk.StringVar(value=tool.TIPI["server"]),"intestatario":tk.StringVar(),"attivita":tk.StringVar(value="1"),"postazioni":tk.StringVar(value="2"),
                "installazione":tk.StringVar(),"scadenza":tk.StringVar()}
        ttk.Label(f,text="Tipo").grid(row=2,column=0,sticky="w",padx=(0,12),pady=(10,0))
        self.tipo_box=ttk.Combobox(f,textvariable=self.v["tipo"],values=list(tool.TIPI.values()),state="readonly",width=28)
        self.tipo_box.grid(row=2,column=1,sticky="w",pady=(10,0));self.tipo_box.bind("<<ComboboxSelected>>",lambda e:self.tipo_changed())
        self.tipo_hint=ttk.Label(f,text="",style="Hint.TLabel");self.tipo_hint.grid(row=3,column=1,sticky="w")
        rows=[("Intestatario","intestatario","il nome del cliente"),("Attività","attivita","quante attività può gestire"),
              ("Postazioni in rete","postazioni","altri PC, tablet e telefoni oltre al PC server"),("Codice installazione","installazione","dalla pagina Licenza del cliente; vuoto = vale ovunque, a stock"),
              ("Scadenza","scadenza","AAAA-MM-GG; vuota = per sempre")]
        self.fields={}
        for i,(label,name,hint) in enumerate(rows,start=2):
            ttk.Label(f,text=label).grid(row=i*2,column=0,sticky="w",padx=(0,12),pady=(6,0))
            if name in ("attivita","postazioni"):
                field=ttk.Spinbox(f,from_=1 if name=="attivita" else 0,to=999,width=8,textvariable=self.v[name])
                field.grid(row=i*2,column=1,sticky="w",pady=(6,0))
            else:
                field=ttk.Entry(f,textvariable=self.v[name]);field.grid(row=i*2,column=1,sticky="we",pady=(6,0))
            self.fields[name]=field
            ttk.Label(f,text=hint,style="Hint.TLabel").grid(row=i*2+1,column=1,sticky="w")
        self.first=self.fields["intestatario"]
        self.create_button=ttk.Button(f,text="Crea la licenza",command=self.create)
        self.create_button.grid(row=14,column=1,sticky="w",pady=14)
        ttk.Label(f,text="Licenza da mandare al cliente").grid(row=15,column=0,columnspan=2,sticky="w")
        self.result=tk.Text(f,height=5,wrap="char",font=("Consolas",10),state="disabled",relief="solid",borderwidth=1)
        self.result.grid(row=16,column=0,columnspan=3,sticky="nsew",pady=(4,8));f.rowconfigure(16,weight=1)
        self.result_info=ttk.Label(f,text="",style="Hint.TLabel",wraplength=700,justify="left");self.result_info.grid(row=17,column=0,columnspan=3,sticky="w")
        buttons=ttk.Frame(f);buttons.grid(row=18,column=0,columnspan=3,sticky="w",pady=(8,0))
        ttk.Button(buttons,text="Copia la licenza",command=lambda:self.copy(self.result.get("1.0","end").strip())).pack(side="left")
        ttk.Button(buttons,text="Apri la cartella delle licenze",command=self.open_folder).pack(side="left",padx=8)
        self.bind("<Return>",lambda e:self.create() if self.tabs.select()==str(self.new_tab) else None)
        self.tipo_changed()

    def tipo(self):return next(k for k,v in tool.TIPI.items() if v==self.v["tipo"].get())

    def tipo_changed(self):
        """Un PC solo: one business, no workstations. Server: one business and its workstations. Portale: both."""
        tipo=self.tipo()
        hints={"pc":"si usa solo dal PC dove è installato: niente postazioni","server":"una attività, il PC server più le postazioni in rete",
               "portale":"più attività sullo stesso server, con le loro postazioni"}
        self.tipo_hint.configure(text=hints[tipo])
        if tipo=="pc":self.v["attivita"].set("1");self.v["postazioni"].set("0")
        if tipo=="server":
            self.v["attivita"].set("1")
            if self.v["postazioni"].get() in ("","0"):self.v["postazioni"].set("2")
        if tipo=="portale" and self.v["attivita"].get() in ("","0","1"):self.v["attivita"].set("2")
        self.fields["attivita"].configure(state="normal" if tipo=="portale" else "disabled")
        self.fields["postazioni"].configure(state="disabled" if tipo=="pc" else "normal")

    def change(self,row):
        """Cambia postazioni o scadenza: the same client and installation in the form, a new licence that replaces the old."""
        self.replacing=row["id"]
        self.v["tipo"].set(tool.TIPI[tool.tipo_of(row)])
        for k in ("intestatario","attivita","postazioni","installazione","scadenza"):self.v[k].set(row[k])
        self.tipo_changed()
        for k in ("attivita","postazioni"):self.v[k].set(row[k])
        self.replace_info.configure(text=f"Sostituisce la licenza {row['id']}: cambia postazioni, scadenza o tipo e premi Crea la licenza. Il cliente la incolla al posto della vecchia.")
        self.replace_reset.grid(row=1,column=2,sticky="e")
        self.fields["installazione"].configure(state="disabled")
        self.tabs.select(self.new_tab);self.fields["postazioni"].focus_set()

    def reset_form(self):
        self.replacing=None;self.replace_info.configure(text="");self.replace_reset.grid_remove()
        self.fields["installazione"].configure(state="normal")
        for k in ("intestatario","installazione","scadenza"):self.v[k].set("")
        self.v["tipo"].set(tool.TIPI["server"]);self.v["postazioni"].set("2");self.tipo_changed()

    def ask_password(self,why="Password della chiave"):
        if self.password is not None or not tool.key_encrypted(self.folder):return True
        password=simpledialog.askstring("TD Licenze",why,show="*",parent=self)
        if password is None:return False
        try:tool.load_key(self.folder,password)
        except ValueError as ex:messagebox.showerror("TD Licenze",str(ex),parent=self);return False
        self.password=password
        return True

    def create(self):
        if not tool.has_key(self.folder):
            messagebox.showinfo("TD Licenze","Prima crea la chiave delle licenze, nella scheda Chiave.",parent=self);self.tabs.select(self.key_tab);return
        if not self.ask_password():return
        self.sync(quiet=True)  # the latest register first: the new number follows the licences made on other PCs
        try:
            licence,path,data=tool.create(self.folder,self.v["intestatario"].get(),self.v["attivita"].get() or 0,self.v["postazioni"].get() or 0,
                self.v["installazione"].get(),self.v["scadenza"].get(),self.out,self.password,self.tipo(),self.replacing or "")
        except ValueError as ex:
            messagebox.showerror("TD Licenze","Attività e postazioni sono numeri." if "invalid literal" in str(ex) else str(ex),parent=self);return
        self.result.configure(state="normal");self.result.delete("1.0","end");self.result.insert("1.0",licence);self.result.configure(state="disabled")
        self.result_info.configure(text=f"{data['id']} per {data['intestatario']}"+(f", sostituisce {self.replacing}" if self.replacing else "")+f". {tool.describe(data)}. Salvata in {path.name}, e già copiata.")
        self.copy(licence,quiet=True)
        if tool.sync_settings(self.folder):
            sent=self.sync(quiet=True)
            self.result_info.configure(text=self.result_info.cget("text")+(" Registro su GitHub aggiornato." if sent else " Non mandata a GitHub: la mando alla prossima sincronizzazione."))
        if self.replacing:self.replacing=None;self.replace_info.configure(text="");self.replace_reset.grid_remove();self.fields["installazione"].configure(state="normal")
        self.refresh()

    # issued licences
    def build_list(self):
        f=self.list_tab;f.columnconfigure(0,weight=1);f.rowconfigure(1,weight=1)
        ttk.Label(f,text="Licenze emesse",style="Title.TLabel").grid(row=0,column=0,sticky="w",pady=(0,12))
        columns=("id","emessa","intestatario","tipo","attivita","postazioni","installazione","scadenza","stato")
        heads=("Numero","Emessa","Intestatario","Tipo","Attività","Postazioni","Installazione","Scadenza","Stato")
        self.tree=ttk.Treeview(f,columns=columns,show="headings",selectmode="browse")
        for c,h,w in zip(columns,heads,(110,90,140,160,60,80,170,90,190)):self.tree.heading(c,text=h);self.tree.column(c,width=w,minwidth=50,stretch=c=="intestatario")
        self.tree.grid(row=1,column=0,sticky="nsew")
        bar=ttk.Scrollbar(f,orient="vertical",command=self.tree.yview);bar.grid(row=1,column=1,sticky="ns");self.tree.configure(yscrollcommand=bar.set)
        buttons=ttk.Frame(f);buttons.grid(row=2,column=0,sticky="w",pady=(10,0))
        ttk.Button(buttons,text="Copia la licenza selezionata",command=self.copy_selected).pack(side="left")
        ttk.Button(buttons,text="Cambia postazioni o scadenza",command=self.change_selected).pack(side="left",padx=8)
        ttk.Button(buttons,text="Apri la cartella delle licenze",command=self.open_folder).pack(side="left")
        self.sync_button=ttk.Button(buttons,text="Aggiorna da GitHub",command=lambda:self.sync())
        if tool.sync_settings(self.folder):self.sync_button.pack(side="left",padx=8)
        ttk.Label(f,textvariable=self.sync_status,style="Hint.TLabel").grid(row=3,column=0,sticky="w",pady=(8,0))
        self.tree.bind("<Double-1>",lambda e:self.copy_selected())
        self.refresh()

    def refresh(self):
        self.rows={}
        self.tree.delete(*self.tree.get_children())
        rows=tool.registry(self.folder)
        replaced={r.get("sostituisce"):r["id"] for r in rows if r.get("sostituisce")}
        for r in reversed(rows):
            state=f"sostituita da {replaced[r['id']]}" if r["id"] in replaced else "in uso"
            item=self.tree.insert("","end",values=(r["id"],r["emessa"],r["intestatario"],tool.TIPI[tool.tipo_of(r)],r["attivita"],r["postazioni"],
                r["installazione"] or "a stock",r["scadenza"] or "per sempre",state))
            self.rows[item]=r

    def copy_selected(self):
        selected=self.tree.selection()
        if selected:self.copy(self.rows[selected[0]]["licenza"])

    def change_selected(self):
        selected=self.tree.selection()
        if not selected:messagebox.showinfo("TD Licenze","Seleziona la licenza da cambiare.",parent=self);return
        self.change(self.rows[selected[0]])

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
        if tool.key_built_in():
            text=("La chiave privata è dentro questo TD Licenze, cifrata: serve solo la sua password. Ogni TD Licenze scaricato da GitHub "
                  "Actions ha la stessa chiave, su qualunque PC. Qui sotto la chiave pubblica che il programma usa per riconoscere le tue licenze.")
            if tool.key_path(self.folder).exists():text+=" Nella cartella c'è anche una chiave vecchia, che non uso."
        else:
            text=("La chiave privata è in "+str(tool.key_path(self.folder))+" e non va mai condivisa. "
                  "La chiave pubblica qui sotto va nel programma, nel file tdwin/licenza-chiave-pubblica.txt della repo: da quella build in poi accetta le tue licenze.")
        ttk.Label(f,wraplength=680,justify="left",text=text).grid(row=1,column=0,columnspan=2,sticky="w")
        field=ttk.Entry(f,font=("Consolas",10));field.insert(0,public);field.configure(state="readonly")
        field.grid(row=2,column=0,columnspan=2,sticky="we",pady=12)
        buttons=ttk.Frame(f);buttons.grid(row=3,column=0,columnspan=2,sticky="w")
        ttk.Button(buttons,text="Copia la chiave pubblica",command=lambda:self.copy(public)).pack(side="left")
        ttk.Button(buttons,text="Apri la cartella della chiave",command=lambda:self.open_folder(self.folder)).pack(side="left",padx=8)

    # the register in the private repo
    def build_sync(self):
        for child in self.sync_tab.winfo_children():child.destroy()
        f=self.sync_tab;f.columnconfigure(1,weight=1)
        ttk.Label(f,text="Registro su GitHub",style="Title.TLabel").grid(row=0,column=0,columnspan=2,sticky="w",pady=(0,12))
        cfg=tool.sync_settings(self.folder)
        if cfg:
            ttk.Label(f,wraplength=680,justify="left",text=f"Il registro delle licenze va anche nella repo privata {cfg['repo']}: all'apertura e dopo ogni licenza "
                "TD Licenze lo aggiorna da lì e ci manda le licenze nuove. Senza Internet lavora sulla copia della cartella.").grid(row=1,column=0,columnspan=2,sticky="w")
            buttons=ttk.Frame(f);buttons.grid(row=2,column=0,columnspan=2,sticky="w",pady=12)
            ttk.Button(buttons,text="Sincronizza ora",command=lambda:self.sync()).pack(side="left")
            ttk.Button(buttons,text="Scollega",command=self.disconnect).pack(side="left",padx=8)
            ttk.Label(f,textvariable=self.sync_status,style="Hint.TLabel").grid(row=3,column=0,columnspan=2,sticky="w")
            return
        ttk.Label(f,wraplength=680,justify="left",text="Collega una repo privata di GitHub, solo per i dati delle licenze: il registro va lì e lo ritrovi "
            "con TD Licenze su qualunque PC. Serve un token fine-grained con accesso solo a quella repo e il permesso Contents, Read and write. "
            "Il token resta nella cartella, cifrato con la password della chiave.").grid(row=1,column=0,columnspan=2,sticky="w")
        self.repo_var=tk.StringVar(value="Giuseppe-sciarra/TD-Licenze-Dati");self.token_var=tk.StringVar()
        ttk.Label(f,text="Repo").grid(row=2,column=0,sticky="w",pady=(14,0),padx=(0,12))
        ttk.Entry(f,textvariable=self.repo_var).grid(row=2,column=1,sticky="we",pady=(14,0))
        ttk.Label(f,text="Token").grid(row=3,column=0,sticky="w",pady=(6,0),padx=(0,12))
        ttk.Entry(f,textvariable=self.token_var,show="*").grid(row=3,column=1,sticky="we",pady=(6,0))
        ttk.Button(f,text="Collega e sincronizza",command=self.connect).grid(row=4,column=1,sticky="w",pady=14)
        ttk.Label(f,textvariable=self.sync_status,style="Hint.TLabel").grid(row=5,column=0,columnspan=2,sticky="w")

    def connect(self):
        if not tool.has_key(self.folder):messagebox.showinfo("TD Licenze","Prima serve la chiave delle licenze, nella scheda Chiave.",parent=self);return
        if not self.ask_password("Password della chiave: chiude anche il token"):return
        try:repo=tool.save_sync(self.folder,self.repo_var.get(),self.token_var.get(),self.password)
        except ValueError as ex:messagebox.showerror("TD Licenze",str(ex),parent=self);return
        self.token_var.set("")
        if self.sync() is None:
            # the token does not work: nothing stays connected, the person sees why
            (self.folder/tool.SYNC_FILE).unlink(missing_ok=True)
        else:self.sync_button.pack(side="left",padx=8)
        self.build_sync()

    def disconnect(self):
        if not messagebox.askyesno("TD Licenze","Scollego la repo? Il registro resta nella cartella e su GitHub, solo non si aggiorna più da qui.",parent=self):return
        (self.folder/tool.SYNC_FILE).write_text("{}",encoding="utf-8")  # {} also wins over the repo built into the exe
        self.sync_status.set("");self.sync_button.pack_forget();self.build_sync()

    def sync(self,quiet=False,ask=False):
        """The register brought up to date with GitHub: (licences, arrived, sent), or None if it did not work."""
        if not tool.sync_settings(self.folder):return None
        if not self.ask_password("Password della chiave, per aggiornare il registro da GitHub" if ask else "Password della chiave"):return None
        try:result=tool.sync(self.folder,self.password)
        except tool.SyncError as ex:
            self.sync_status.set(str(ex))
            if not quiet:messagebox.showwarning("TD Licenze",str(ex),parent=self)
            return None
        self.sync_status.set("GitHub: "+tool.sync_message(result));self.refresh()
        return result

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
