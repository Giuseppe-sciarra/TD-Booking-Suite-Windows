# TD Booking Suite 3 — il programma Windows in C# .NET

Rifacimento del programma Windows come programma vero: **WPF** per le finestre, **ASP.NET Core** come servizio
Windows, **C# .NET 8** dappertutto, **stessi dati** del programma Python (2.22.x).

## Architettura

```
 Postazione (WPF, TD.App)  ──HTTPS 8443, API JSON──▶  Server (servizio Windows, TD.Server)  ──▶  PostgreSQL
 PC del server (WPF)       ──────────────────────▶      · API privata per le finestre              (lo stesso
 Sito WordPress (plugin)   ──HTTPS, stesse URL──────▶      · URL pubbliche di adesso (prenota,         database
 Cliente (email/WhatsApp)  ──link─────────────────▶        conferme, API, feed, collegamento WP)     di oggi)
                                                         · lavori: sito, promemoria, email, cassa,
                                                           backup, conservazione, licenza
```

- **Le chiavi restano sul server**: le finestre non aprono il database né vedono le chiavi; chiedono al servizio, che
  controlla permessi e sicurezza a livello di riga (`app.tenant_id`) come oggi. Una postazione rubata non porta via niente.
- **TD.Core** (net8.0): dati (Npgsql + Dapper sulle tabelle di oggi), cifratura identica a `core/crypto.py`,
  password Django (argon2 / pbkdf2), regole del lavoro (disponibilità, cassa, privacy...). Si prova anche su Linux.
- **TD.Server** (net8.0, servizio Windows): API privata + URL pubbliche compatibili + lavori in background.
- **TD.App** (net8.0-windows, WPF): le finestre. Postazioni e PC del server usano la stessa app.
- **TD.Tests**: compatibilità con il programma Python (dati cifrati, password, risultati) e prove dell'API su Postgres vero.

## Contratto di compatibilità

1. **Database**: le tabelle e le colonne create dalle migrazioni Django (core 0001–0054, tdwin 0001–0004). Il nuovo
   programma non ne cambia il significato; le sue migrazioni proprie vanno in `td_net_migrations` e sono solo aggiunte.
   Un database del 2.22.x si apre così com'è; il programma Python, se rimesso, continua a leggerlo.
2. **Cifratura**: AES-256-GCM, chiave per attività avvolta dalle chiavi master (`TDBS_KEYS`), etichette (AAD) uguali
   (`contact:<id>`, `booking:<id>`, `sale:<id>`, `photo:<id>`...). JSON compatto senza escape ASCII.
3. **Password**: lette argon2id e pbkdf2_sha256 di Django; scritte argon2id con i parametri di Django 5.2.
4. **Cartella dati** `C:\ProgramData\TD Booking Suite`: `config.json`, `db-admin.json`, `licenza\`, `backup\`, `certs\`,
   `log\` uguali. Licenze di TD Licenze valide così come sono. Backup: stesso formato (pg_dump + segreti).
5. **Sito WordPress**: stessa sincronizzazione (wpsync) e stesse URL, il plugin non cambia.
6. **Postazioni**: `server.txt` e certificato del server come oggi.

## Elenco funzioni (stato)

Legenda: ☐ da fare · ◐ in corso · ☑ fatto e provato

### Fondamenta
- ☑ Cifratura compatibile (prove con dati del Python)
- ☑ Password Django
- ☑ Accesso ai dati (Db, Scope, entità generate dallo schema con i default di Django: tools/gen_entities.py + django_defaults.py)
- ☑ Ctx (unità di lavoro di un'attività: transazione con RLS, chiave, calendario, audit, IOutbox per email/notifiche/webhook)
- ☑ Migrazioni proprie (td_net_migrations) + baseline.sql per un database nuovo (provato: il Python lo apre senza migrazioni in sospeso) + permessi del ruolo runtime
- ☑ Permessi e ruoli (Permissions.cs)
- ☑ Server: accesso (password, Authenticator, recupero, senza password dai PC fidati), sessioni, postazioni (codice a 6 cifre), licenza (Ed25519, prova, orologio, stato online), certificati (autorità interna), RPC `/api/rpc/nome`, archivio del sito (hook su ogni scrittura)
- ☑ Messaggi: email ai clienti (testi, HTML, ICS, promemoria/recensione/richiamo), avvisi allo staff, webhook, traduzioni EN dal django.po

### Lavoro di ogni giorno
- ☐ Accesso: username/email + password, Authenticator, codici di recupero, accesso senza password dai PC fidati, Esci
- ☐ Dashboard, banner (promemoria WhatsApp, grazie del giorno dopo)
- ◐ Agenda WPF: giorno (colonne per operatore, trascina/allunga, quarti liberi cliccabili), settimana, mese, elenco, blocca orario ☑; turni ☐; stampa ☐
- ☑ Motore prenotazioni (Calendar, Availability, BookingWrites, Tables, Waitlist.Holds, Stays.Restricted): identico al Python, vedi «Come si prova»
- ◐ Prenotazioni WPF: nuova (operatore/giorno/ora, più servizi, cliente cercato o nuovo, Forza), dettaglio, stati, annulla con motivo, sposta, ripeti ☑; soggiorni, eventi con iscritti, tavoli, senza appuntamento ☐
- ◐ Clienti: elenco, ricerca, scheda (panoramica, prenotazioni, trattamenti, foto, consensi e privacy), unisci, import — fatto tutto tranne Importa/Esporta
- ☑ Scheda cliente (trattamenti), foto (4 al giorno), storico in PDF
- ☑ Lista d'attesa, richiami
- ☑ Promemoria WhatsApp e grazie con recensione (con l'avviso in cima alle pagine)
- ◐ Cassa: conto, operatori, mattonelle, Parcheggio/Stand-by, Da pagare, gift card, non nello scontrino, storico,
      riepilogo PDF, chiusura, annulli, registratore RCH ed Epson (rete e USB), conto che resta
- ☑ Gift card, preventivi (PDF)
- ◐ Report (fatto, Excel e PDF), registro attività (fatto); esportazioni da fare

### Attività e impostazioni
- ◐ Configurazione guidata, attività, sedi, orari, festivi, servizi e catalogo, operatori (foto, colori, calendari) — fatti: Configura, catalogo completo con moduli, festivi, importa servizi; mancano i calendari Google/Outlook sugli operatori
- ◐ Persone e ruoli, testi ed email ai clienti, SMTP, SMS/WhatsApp, notifiche — fatti: Persone e permessi, Accesso amministratore, Configurazione promemoria, Invio email (SMTP/Google/Microsoft 365, MailKit); manca SMS/WhatsApp e calendari (texts)
- ◐ Ristoranti: tavoli, sale, turni, allergeni — allergeni e turni fatti; mancano Sale e tavoli
- ☐ Strutture ricettive: soggiorni, ospiti, Alloggiati Web
- ☐ Marketing: campagne, iscritti
- ☐ Google Calendar e Outlook sugli operatori
- ☑ API e webhook (pagina; gli indirizzi /api/v1 sono nelle URL pubbliche)

### Programma Windows
- ☐ Licenza (TD Licenze), postazioni, rete e certificato, backup e ripristino, stato del programma, aggiornamenti
- ◐ Servizio: sito WordPress, email e promemoria, cassa, backup, conservazione, controllo licenza — fatto il motore del sito WordPress (WpSite: sync, archivio, ripristina, unisci, provato con un sito finto che firma come il Python: snapshot, agenda e 55 record identici); da agganciare al worker con gli altri lavori
- ☐ URL pubbliche: pagina di prenotazione, conferme/annulli, feed, API, collegamento WordPress, ponte cassa
- ☐ Installer MSI (WiX) che aggiorna il 2.22.x tenendo i dati

## Come si prova

- **Confronto col Python sullo stesso database** (`tools/compat`): `compat.py seed` crea un'attività con ogni tipo di
  servizio, orari, eccezioni, blocchi e prenotazioni; `compat.py dump` e `TD.Compat dump` scrivono le disponibilità
  (12 giorni × servizi × operatori × banco/online × persone, visite) e le prenotazioni come le leggono;
  `diff.py` le confronta. `TD.Compat create` scrive prenotazioni dal C# e il Python le rilegge.
  Ultimo esito: 372 casi, 0 differenze, prima e dopo le scritture del C#.
  ```
  DB=tdcmp; psql ... -c "DROP DATABASE $DB" -c "CREATE DATABASE $DB"
  PYTHONPATH=.:suite DJANGO_SETTINGS_MODULE=tdwin.test_settings DATABASE_URL="postgres://postgres@/$DB?host=/tmp&port=5433" python suite/manage.py migrate && ... grant_runtime
  python net/tools/compat/compat.py seed; python net/tools/compat/compat.py dump > /tmp/py.json
  dotnet run --project net/tools/TD.Compat -- "Host=/tmp;Port=5433;Database=$DB;Username=tdbooking_app;Password=..." dump > /tmp/cs.json
  python3 net/tools/compat/diff.py /tmp/py.json /tmp/cs.json
  ```

- `dotnet test` (Linux o Windows): compatibilità e API su un PostgreSQL vero (`TD_TEST_PG`).
- GitHub Actions sul ramo `net-rewrite`: compila su Windows, prove, finestre WPF con screenshot.

## Dove siamo e come si lavora (per riprendere)

- Ramo `net-rewrite`. SDK con WPF su Linux: `export DOTNET_ROOT=/home/claude/dotnet8 PATH=/home/claude/dotnet8:$PATH` (quello di sistema non ha WindowsDesktop).
- Server di prova: cartella dati `/tmp/tddata` (config.json + db-admin.json sul database `tdcmp`), `TDBS_DATA=/tmp/tddata dotnet run --project src/TD.Server` (porta 8443), utente `titolare` / `Password-di-prova-123`, attività `cmp`.
- Finestre: non girano su Linux. `TDBookingSuite.exe --demo tests/demo --shot cartella` le apre sulle risposte registrate dal server (tests/demo/*.json, rifarle con curl quando cambia una risposta) e salva gli screenshot; lo fa la build `.github/workflows/net.yml`, artefatto `screenshot` (`gh run download`).
- Ordine dei prossimi pezzi: Clienti (elenco, scheda con tab, trattamenti, foto, privacy, unisci) → Cassa (+ registratori RCH/Epson, stand-by, gift card, preventivi) → Lista d'attesa, richiami, promemoria WhatsApp → Catalogo e impostazioni → Report → pagine pubbliche e sito WordPress (wpsync) → lavori del worker (email SMTP, promemoria, sito, backup, licenza) → Licenza/Postazioni/Backup/Rete nelle finestre → installer MSI.
- Le postazioni dovranno rifare l'abbinamento una volta (il token del 2.x è nel cookie del vecchio client, non leggibile): revocare le vecchie da Postazioni.

## Grafica: UGUALE al programma di adesso (richiesta esplicita)

Non «simile»: ogni finestra va messa a confronto con la pagina web corrispondente e portata a coincidere (colori e misure da
`suite/static/app.css`, caratteri Figtree/Poppins in `src/TD.App/Fonts`, barra scura a gruppi, barra in alto, intestazione
con occhiello, schede, tasti con ombra, select/checkbox/barre di scorrimento ridisegnati in `Ui/Theme.xaml`). E **tutte le funzioni**
di ogni pagina (95 template in `suite/templates`), nessuna tolta.
Per il confronto: il programma Python gira su `tdcmp` con
`PYTHONPATH=.:suite DJANGO_SETTINGS_MODULE=tdwin.test_settings DATABASE_URL="postgres://postgres@/tdcmp?host=/tmp&port=5433" python suite/manage.py runserver 127.0.0.1:8000 --noreload`
(utenti `titolare` e `admin`, password `Password-di-prova-123`) e Playwright salva le pagine in `/home/claude/out/web`;
gli screenshot delle finestre arrivano dalla build (`gh run download <id> -n screenshot`).
Build: **una sola, alla fine**, a mano (`gh workflow run net.yml --ref net-rewrite`): niente build a pezzi. In locale si
compila soltanto (`dotnet build src/TD.App`, `src/TD.Server`) e si provano le chiamate con curl sul server di prova.

### Pagine fatte uguali al web (finestra + chiamate del server)

- `base.html` → `MainWindow` · `agenda.html` (giorno, settimana, mese) → `AgendaPage` · `bookings.html` (elenco) → `BookingsPage`
- `booking.html` + `booking.js` → `BookingPage` · `booking_detail.html` → `BookingDetailPage` · blocco orario → `BlockDialog`
- `dashboard.html` → `DashboardPage` (`cruscotto`, `turno.cambia`)
- `contacts.html` → `ClientsPage` (`clienti.elenco`, `clienti.nuovo`) · `contact_detail.html` → `ClientPage` (`cliente.scheda`,
  `cliente.azione`, `cliente.esporta`, `cliente.cancella`) · `contact_merge.html` → `ClientMergePage` · `_photo_grid.html` → `Ui/PhotoGrid`
  (`foto.aggiungi`, `foto.togli`, `foto.immagine`; le due copie JPEG le fa la finestra con WIC, il server controlla che siano JPEG)
- `contact_history.html` → `ClientHistoryPage` (anche finestra «Storico» della Cassa; il «PDF» passa dalla stampa di Windows, `Ui/Printing`)
- `cassa.html` + la parte Cassa di `app.js` → `CassaPage` (`cassa`, `cassa.azione`, `cassa.copia`, `cassa.riepilogo`, `giftcard.cerca`), `cassa_copy.html` →
  `ReceiptCopyPage`. Dominio in `Till.cs`, `TillBill.cs` (protocolli RCH ed Epson confrontati byte per byte con il Python), `Pdf.cs` (= `pdf.py`).
  Il conto lasciato a metà resta sul PC in `%LocalAppData%\TDBookingSuite\cassa-<attività>-<sede>.json` (al posto del localStorage), stesso formato del web
- `standby.html` → `StandbyPage` · `gift_cards.html`, `gift_print.html` → `GiftCardsPage`, `GiftPrintPage` (`giftcard.elenco`, `giftcard.azione`)
- `quotes.html`, `quote_form.html`, `quote_sheet.html` → `QuotesPage`, `QuoteFormPage`, `QuoteSheetPage` (`preventivi.elenco`, `preventivo.dati/salva/elimina/foglio`)
- `waitlist.html` → `WaitlistPage` (`attesa`, `attesa.azione`)
- `confirm.html` («Conferma che sei tu», ogni 15 minuti per le operazioni delicate) → `ConfirmDialog` (`/api/conferma`,
  `[Rpc(Fresh = true)]`, risposta 428)

### Pezzi lasciati con un segnaposto dentro pagine già fatte (da chiudere, nessuno va tolto)

- Agenda: Stampa, Importa prenotazioni. Dettaglio prenotazione: «Iscritti e presenze».
- Clienti: Importa, Esporta; Copia dello scontrino dalla scheda apre la Cassa.
- Cassa: foto degli operatori nella barra (ora l'iniziale colorata); ponte cassa per le sedi non «diretto» (`/book/<slug>/api/cassa/`, con le pagine pubbliche).
- Lista d'attesa: l'iscrizione dalla pagina online (con le pagine pubbliche) e l'avviso a chi aspetta (lavoro del worker).
- Fatti dopo: Richiami, Promemoria WhatsApp, Report, Registratore di cassa (impostazioni; il ponte cassa ha codice e scaricamento, manca il suo
  indirizzo `/book/<slug>/api/cassa/`), Registro attività, Persone e permessi (con `Accounts`: inviti, password, entra senza password), Accesso di
  assistenza (l'amministratore del programma entra nei contenuti solo con la concessione, ogni chiamata registrata), API e webhook (pagina; gli
  indirizzi `/book/<slug>/api/v1/` arrivano con le pagine pubbliche), Configura l'attività (pagina e azioni: `Profile` = profile.py, provato
  uguale a Python su ristorante, parrucchiere, hotel, tour; logo, banco, domande, QR).
- Segnaposto dentro Configura l'attività: `CatalogPage` (elenchi e moduli di sedi, orari, chiusure, servizi, risorse, varianti, turni, date,
  prodotti: catalog.html, form.html, forms.py, con calendar_conflicts, shifts.materialize, Rimuovi/Elimina, festività, prezzi, import dei
  servizi) e `AllergensPage`. È il prossimo pezzo. `Profile.Delete/Retire/Remove` (cancellare come Django: PROTECT, CASCADE, SET_NULL) è già pronto.
- Da fare poi, nell'ordine: Configurazione promemoria, Impostazioni e sito, Invio email, testi SMS/WhatsApp, Sale e tavoli, Alloggi, Guida,
  Account e sicurezza (pagina dell'invito compresa), Iscritti e presenze, stampa e import dell'agenda, import ed export clienti, Marketing; poi
  configurazione del programma (Licenza, Postazioni, Backup, Rete, Stato), pagine pubbliche, sito WordPress, lavori del worker, installer.
- Diverso da Python, apposta: Salva nel Registratore di cassa tiene lo scarto dei documenti imparato (`doc_offset`); Python lo azzera.
- Report: a parità di data, Elenco dei trattamenti e I servizi più fatti seguono l'ora di inserimento (in Python l'ordine a parità di data è casuale).
- Conferma che sei tu: manca la passkey (WebAuthn di Windows).


## Stato al 10/11/2026 (notte)
- Fatte e provate contro il Python: Configurazione promemoria, Impostazioni e sito (modulo attività, pannelli WordPress, scarica plugin), sincronizzazione sito (WpSite), Invio email (SMTP/Google/Microsoft 365), SMS/WhatsApp e calendari (client OAuth), Sale e tavoli, Accesso e sicurezza, Guida, Marketing e campagne, esportazioni CSV (identiche byte per byte), Clienti › Esporta.
- Segnaposto rimasti in MainWindow: alloggi, programma/attività/generali/licenza/postazioni/backup/rete/stato. In ClientsPage: Importa clienti. In AgendaPage: Stampa e Importa (messaggio provvisorio).
- Prossimo pezzo, i lavori del worker (Jobs.Add in TD.Server/Work, oggi vuoto), nell'ordine del mail_worker.py:
  ogni 10 s coda in uscita (mail.worker_once: email con Mail.Send, Telegram, webhook, SMS/WhatsApp con TextMessages.Deliver, limiti giornalieri/orari, esiti) e lista d'attesa (waitlist.run/step);
  ogni 2 min sito (WpSite.Run); ogni 5 min promemoria (ClientMails.Remind + texts.remind), riepilogo serale (notify.evening), esiti SMS (TextMessages.Receipt), calendari esterni (feeds, gcal, mscal);
  ogni ora turni (shifts.extend_rules), conservazione (privacy.run_retention), calendari dei portali (stays.run); poi backup, licenza online, rete, aggiornamenti (tdwin_worker).
- Poi: pagine del programma (Dashboard, Clienti, Impostazioni generali + smtp.sito già pronto, Licenza, Postazioni, Backup, Rete, Stato), Importa clienti e Importa appuntamenti (importer.plan/apply), Stampa del giorno, Iscritti e presenze, Alloggi (stays.py, ospiti, Alloggiati Web), pagine pubbliche (/book, /action, feed, API v1, ponte cassa, invito, /api/wordpress), Google/Outlook sugli operatori, installer MSI, una sola build CI alla fine.
- Aperti da dire a Giuseppe: aggiungere una passkey dalla finestra (serve Windows Hello/webauthn.dll e la verifica sul server: oggi il tasto avvisa, l'elenco e Rimuovi funzionano; la conferma con passkey al login non c'è ancora); la foto degli operatori per l'agenda del sito usa System.Drawing (solo Windows).
- Non provato dal vivo: la sessione SMTP di MailKit, Google OAuth e Graph, Twilio/Skebby/WhatsApp (dalla sandbox non si esce), le finestre WPF (si compilano, non girano su Linux).
