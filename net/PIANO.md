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
- ◐ Accesso ai dati (Db, Scope, entità generate dallo schema)
- ☐ Migrazioni proprie, controllo dello schema all'avvio
- ☐ Permessi e ruoli (permissions.py)

### Lavoro di ogni giorno
- ☐ Accesso: username/email + password, Authenticator, codici di recupero, accesso senza password dai PC fidati, Esci
- ☐ Dashboard, banner (promemoria WhatsApp, grazie del giorno dopo)
- ☐ Agenda: giorno / settimana / mese / elenco, colonne per operatore, trascina per spostare, blocchi orari, turni
- ☐ Prenotazioni: nuova (ora prima, servizi dopo, lista unica), dettaglio, sposta, stato, visite con più servizi, presenze
- ☐ Clienti: elenco, ricerca, scheda (panoramica, prenotazioni, trattamenti, foto, consensi e privacy), unisci, import
- ☐ Scheda cliente (trattamenti), foto (4 al giorno), storico in PDF
- ☐ Lista d'attesa, richiami
- ☐ Promemoria WhatsApp e grazie con recensione
- ☐ Cassa: conto, operatori, mattonelle, Parcheggio/Stand-by, Da pagare, gift card, non nello scontrino, storico,
      riepilogo PDF, chiusura, annulli, registratore RCH ed Epson (rete e USB), conto che resta
- ☐ Gift card, preventivi (PDF)
- ☐ Report, esportazioni, registro attività

### Attività e impostazioni
- ☐ Configurazione guidata, attività, sedi, orari, festivi, servizi e catalogo, operatori (foto, colori, calendari)
- ☐ Persone e ruoli, testi ed email ai clienti, SMTP, SMS/WhatsApp, notifiche
- ☐ Ristoranti: tavoli, sale, turni, allergeni
- ☐ Strutture ricettive: soggiorni, ospiti, Alloggiati Web
- ☐ Marketing: campagne, iscritti
- ☐ Google Calendar e Outlook sugli operatori
- ☐ API e webhook

### Programma Windows
- ☐ Licenza (TD Licenze), postazioni, rete e certificato, backup e ripristino, stato del programma, aggiornamenti
- ☐ Servizio: sito WordPress, email e promemoria, cassa, backup, conservazione, controllo licenza
- ☐ URL pubbliche: pagina di prenotazione, conferme/annulli, feed, API, collegamento WordPress, ponte cassa
- ☐ Installer MSI (WiX) che aggiorna il 2.22.x tenendo i dati

## Come si prova

- `dotnet test` (Linux o Windows): compatibilità e API su un PostgreSQL vero (`TD_TEST_PG`).
- GitHub Actions sul ramo `net-rewrite`: compila su Windows, prove, finestre WPF con screenshot.
