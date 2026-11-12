# TD Booking Suite per Windows

TD Booking Suite diventa un programma per Windows, che si installa con un file MSI. All'installazione si sceglie come usarlo:

- **Un PC solo.** Programma e dati sullo stesso PC, che si usa da solo. Di serie risponde solo a quel PC (127.0.0.1): niente altri computer o telefoni, niente pagina prenotazioni per i clienti né modulo nel sito, niente porta aperta nel firewall. Si entra con username e password. La pagina prenotazioni e il sito l'amministratore li accende da **Online e sito** (vedi sotto).
- **Server con postazioni.** Il PC che resta acceso tiene il database PostgreSQL, il programma in HTTPS e i backup, come servizi di Windows. Gli altri PC con la postazione, i telefoni dell'ufficio e, se lo metti online, il sito si collegano a lui. Sul server si usa anche il programma.
- **Solo postazione.** Solo la finestra di TD Booking Suite, per un PC che si collega a un server già installato o alla versione web su Docker.

La **licenza** dice il tipo (Un PC solo, Server con postazioni, Portale con più attività), quante postazioni in rete si usano e se scade. Senza licenza si attiva una **prova di 7 giorni**: finita la prova, il programma chiede la licenza.

**Per le attività ad appuntamento** (parrucchieri, estetica, studi, campi) l'agenda si apre sul giorno, con **una colonna per operatore**: un clic su uno spazio libero e la prenotazione parte con operatore, giorno e ora già messi; il cliente si cerca in rubrica, si sceglie il trattamento e l'orario è già selezionato. Ogni operatore ha la sua **foto**, i trattamenti che fa, ognuno con la sua durata e il suo prezzo se diversi, e, se serve, i suoi giorni e orari e le sue ferie. L'agenda si guarda per tutti o per un operatore solo. Oltre al giorno ci sono **Settimana** (i sette giorni ora per ora, per tutti o per un operatore) e **Mese** (quanti appuntamenti ha ogni giorno); ogni operatore ha il suo colore. Il titolare tiene il listino con i **prezzi** e dà a un cliente uno **sconto** in percentuale o fisso, che vale da subito sulle sue prenotazioni.

**Con la 2.14.0** il programma fa quello che fanno i gestionali di settore: stati *Arrivato* e *In corso* con *Torna indietro*, servizi **su richiesta** con *Richieste da confermare* nella Dashboard, **regole del banco** separate da quelle online con **Forza** e **Senza appuntamento**, l'origine di ogni prenotazione con link per canale, QR e codice per i siti senza WordPress. Il **promemoria** è acceso di serie con i tasti **Confermo** e **Non vengo**, *Da chiamare* per chi non risponde, un secondo promemoria, *Prima dell'arrivo*, *Dopo la visita* con il link della recensione, il **riepilogo serale**, email con il logo dell'attività e il file `.ics`; il cliente **sposta** la prenotazione dal link *Gestisci*. Chiusure **dal giorno al giorno**, **festività italiane** in un clic, **Blocca orario**, due fasce al giorno e settimane alterne; **turni e date degli eventi** li gestisce il titolare. L'agenda si **trascina**, si allunga, si aggiorna da sola, si **stampa** e ha *Nascondi nomi*; c'è la **lista d'attesa** con *Avvisami se si libera*, e la scheda cliente ha etichette, VIP, compleanno, assenze, *Non prenotabile online* e *Unisci due schede*. WhatsApp a un clic, ultimo arrivo al ristorante, officina con i ponti, noleggio in giornata. La pulizia dei dati scaduti la fa il servizio *TD Booking Suite Worker* ogni giorno. I link aperti dal cliente (Confermo, Non vengo, Sposta, calendario, lista d'attesa) funzionano solo con il programma raggiungibile da Internet: Server con postazioni messo online o *Online e sito* acceso. Le postazioni tengono una **copia dell'agenda** di oggi e domani per quando il server non risponde (vedi [Le postazioni](#le-postazioni)). Il dettaglio è nel [CHANGELOG del nucleo](suite/CHANGELOG.md).

Il nucleo web è [TD Booking Suite](https://github.com/Giuseppe-sciarra/TD-Booking-Suite), incluso qui come sottomodulo in `suite/`.

**Istruzioni in PDF**, con gli screenshot:

- [`docs/Istruzioni-TD-Booking-Suite-Windows.pdf`](docs/Istruzioni-TD-Booking-Suite-Windows.pdf): installare, configurare, lavorare, postazioni, backup, licenza, problemi. Si può dare ai clienti.
- [`docs/Istruzioni-TD-Licenze.pdf`](docs/Istruzioni-TD-Licenze.pdf): TD Licenze, riservato a chi vende.

| | |
|---|---|
| ![Configurazione guidata](docs/screenshots/guida.png) | ![Chi lavora nell'attività](docs/screenshots/guida-io.png) |
| **Configurazione guidata**: licenza, attività, backup e postazioni, passo per passo | **Chi lavora nell'attività**: lo stesso account dell'amministratore, oppure un titolare con il suo |
| ![Agenda per operatore](docs/screenshots/agenda.png) | ![Operatore](docs/screenshots/operatore.png) |
| **Agenda del giorno**: una colonna per operatore con la sua foto, un clic su uno spazio libero per prenotare con quell'operatore a quell'ora | **Operatore**: la foto, i trattamenti che fa con durata e prezzo suoi, i suoi giorni e orari, il link per le ferie |
| ![Settimana](docs/screenshots/settimana.png) | ![Mese](docs/screenshots/mese.png) |
| **Settimana**: i sette giorni ora per ora, un colore per operatore, gli appuntamenti affiancati quando si sovrappongono | **Mese**: quanti appuntamenti ha ogni giorno e i primi, un clic apre il giorno |
| ![Scheda cliente](docs/screenshots/cliente.png) | ![Al lavoro](docs/screenshots/al-lavoro.png) |
| **Scheda cliente**: chi è sempre in vista, con lo sconto; speso, appuntamenti in programma e ultimo trattamento | **Al lavoro**: il menu a gruppi con un'icona per voce, il lavoro di ogni giorno in alto |
| ![Accesso](docs/screenshots/accesso.png) | ![Licenza](docs/screenshots/licenza.png) |
| **Accesso** con username o email, *Entra come …* senza password per i titolari dal PC del programma e dalle postazioni, e il promemoria dei due account | **Licenza**: tipo, cosa consente, il codice installazione da mandare a chi vende, il campo per incollarla |
| ![Postazioni](docs/screenshots/postazioni.png) | ![Tema scuro](docs/screenshots/dashboard-scuro.png) |
| **Postazioni**: il codice di 6 cifre per registrare un PC, l'elenco con ultimo accesso e revoca | **Tema scuro**, e il colore principale a scelta |
| ![Backup](docs/screenshots/backup.png) | ![Rete e certificato](docs/screenshots/rete.png) |
| **Backup** cifrati, ogni notte, su disco esterno, NAS, FTP, FTPS o SFTP, con lo storico | **Rete e certificato**: indirizzo pubblico, proxy fidati e certificato, solo per l'amministratore |
| ![Importa clienti](docs/screenshots/import.png) | ![Anteprima dell'import](docs/screenshots/import-anteprima.png) |
| **Importa clienti** (Clienti › Importa): anagrafica e storico da Excel o CSV, colonna per colonna | **Anteprima**: le prime righe, cosa succede, le righe che non entrano e perché |
| ![Prossimi 7 giorni](docs/screenshots/dashboard-7giorni.png) | ![Logo e aspetto](docs/screenshots/logo-aspetto.png) |
| **Dashboard**: *Oggi* e *Prossimi 7 giorni* mostrano chi viene e a che ora, giorno per giorno; una colonna del grafico porta a quel giorno | **Logo e aspetto** (Configura l'attività): il titolare carica il logo, che va in alto nel menu al posto di "TD", sceglie colore e frase, con l'anteprima |
| ![Email ai clienti](docs/screenshots/notifiche.png) | ![Email delle prenotazioni del cliente](docs/screenshots/cliente-avvisi.png) |
| **Email ai clienti** (Notifiche, se l'amministratore le attiva): quali partono, il promemoria, i testi con i campi e l'anteprima | **Il cliente sceglie**: dalla sua scheda le email delle prenotazioni si accendono o si spengono |
| ![Aspetto](docs/screenshots/aspetto.png) | ![Titolare](docs/screenshots/titolare.png) |
| **Impostazioni generali**: nome, loghi chiaro e scuro, icona, colore e tema di partenza | **Il titolare** vede il lavoro dell'attività: niente Persone e permessi, marketing solo se attivato |
| ![Persone e permessi](docs/screenshots/persone.png) | |
| **Persone e permessi**, solo per l'amministratore del programma: ruoli, password, *Entra senza password* per un titolare | |

## Installare

### Windows segnala l'MSI: si installa lo stesso

L'MSI non ha ancora una firma digitale, quindi Windows non riconosce chi lo pubblica e lo segnala, anche se il file è a posto:

1. **Edge o Chrome**, appena scaricato, dicono che il file *non viene scaricato comunemente*. Apri il menu **…** del download e scegli **Mantieni**, poi **Mostra altro** e **Mantieni comunque**.
2. **Windows ha protetto il PC**, la finestra blu di SmartScreen, compare al doppio clic. Premi **Ulteriori informazioni** e poi **Esegui comunque**.
3. Se il file arriva da una chiavetta o da una email e non parte: tasto destro, **Proprietà**, spunta **Sblocca** in basso, **OK**.

Per togliere gli avvisi serve un certificato di firma del codice, che costa qualche centinaio di euro l'anno: la build lo usa da sola, vedi [Firma del codice](#firma-del-codice).

### Un PC solo, o il PC server

1. Doppio clic sull'MSI, poi **Avanti**. Accetta la licenza d'uso.
2. Alla domanda **Come userai TD Booking Suite su questo PC?** scegli **Un PC solo** oppure **Server con postazioni**, poi **Installa** e dai il permesso di amministratore. La prima volta servono un paio di minuti: l'installer crea il database e i servizi. La scelta si cambia dopo, aprendo di nuovo l'installazione: i dati restano.
3. Alla fine lascia spuntato **Apri TD Booking Suite**, oppure usa l'icona sul desktop. Sul PC con il server la finestra apre da sola il programma installato lì, già verificato: niente indirizzo né impronta da controllare.
4. Si apre **Primo avvio**. Il **codice del primo avvio** lo mostra il menu Start, **TD Booking Suite**, **Codice del primo avvio**, che chiede il permesso di amministratore: così un altro utente di Windows dello stesso PC non può creare l'amministratore al posto tuo. Poi scegli lo **username** dell'amministratore del programma, l'email se vuoi, e la password. **Resta collegato su questo PC** è già spuntato.
5. Si apre la **configurazione guidata**:
   - **Licenza**: incolla la licenza oppure **Attiva la prova di 7 giorni**;
   - **La tua attività e chi ci lavora**: nome e tipo dell'attività, poi **Io, con questo account** (un solo account configura e lavora, proposto con Un PC solo) oppure **Il titolare, con un suo account** (username, email facoltativa e password del titolare, proposto con Server con postazioni e quando installi per un cliente);
   - **Backup**: password dei backup, backup automatico e almeno una destinazione fuori dal PC;
   - **Postazioni**, solo con Server con postazioni.
6. **Al lavoro**: con *Io, con questo account* **Vai all'agenda**; con il titolare **Esci ed entra come …** apre l'accesso con il suo username già scritto.

| ![Primo avvio](docs/screenshots/primo-avvio.png) | ![Senza licenza](docs/screenshots/bloccato.png) |
|---|---|
| **Primo avvio**: solo dal PC server, solo finché l'amministratore non c'è, con il codice che vede solo un amministratore di Windows | **Dopo la prova**: restano aperte solo Licenza, Backup e la configurazione guidata, e i dati sono al sicuro |

### Gli account

| Account | Cosa fa |
|---|---|
| **Amministratore del programma** | Configura tutto: licenza, backup, configurazione guidata, impostazioni generali; tipo di attività, sedi, tipo di prenotazione dei servizi, domande del modulo e sito; sul server postazioni e rete, su Un PC solo *Online e sito*. Agenda e clienti solo se lavora nell'attività (*Io, con questo account*) |
| **Titolare** | Lavora: agenda, prenotazioni, clienti, notifiche ed email ai clienti, marketing se l'amministratore lo attiva. Tiene aggiornati orari, chiusure e ferie, gli operatori, il listino (servizi, varianti, prezzi e sconti dei clienti), i turni del ristorante e le date degli eventi, e il suo logo. Tipo di attività, sedi, tipo di prenotazione dei servizi e persone con accesso non li cambia: *Persone e permessi* non la vede |
| **Persone dell'attività** | Quello che il loro ruolo consente, le aggiunge l'amministratore del programma da *Persone e permessi* |

- Ogni account nuovo ha uno **username**; l'email è facoltativa e, se c'è, si entra anche con quella. Uno username già usato da chi lavora in un'altra attività non aggancia il suo account: con l'email giusta sì.
- **Email ai clienti**: conferma, modifica e annullamento partono da sole, con il logo dell'attività. Il **promemoria** è acceso di serie per le attività nuove e il titolare lo regola in *Notifiche* anche senza l'amministratore. Se l'amministratore lo attiva in *Impostazioni e sito*, il titolare sceglie anche quali email standard partono e scrive i testi, con l'anteprima. Avvisi e riepilogo serale per un operatore partono solo se la persona ha l'email. Il cliente sceglie nel modulo se riceverle; dalla sua scheda si cambiano.
- **Marketing** lo accende l'amministratore in *Impostazioni e sito*: le campagne email, il CSV degli iscritti o tutti e due. Spento, la voce non compare nel menu. Il CSV si scarica una volta sola, senza che la finestra lo ripeta.
- **Logo e aspetto**: in *Configura l'attività* il titolare carica il logo, che compare in alto nel menu al posto di "TD", e sceglie colore e frase sotto il nome.
- **Entra senza password**: in *Persone e permessi* l'amministratore del programma lo accende per un titolare. Dal PC del programma e dalle postazioni registrate la pagina di accesso mostra *Entra come …*: un clic e si è dentro, collegati finché non si preme *Esci*. Da un dispositivo non registrato serve la password.
- **Resta collegato** (spuntato di serie): il programma si riapre già collegato, sempre, finché non premi **Esci**. **Esci** in alto a destra cambia account.
- **Il menu** è a gruppi, ognuno nella sua cornice, con un'icona per voce: il lavoro di ogni giorno in alto, la configurazione del programma richiudibile sotto. Finita la configurazione guidata (licenza o prova e almeno un'attività) la sua voce sparisce; la pagina resta all'indirizzo `/platform/inizia/`.
- **La finestra** si apre al 90% dello schermo, ingrandita sugli schermi Full HD al 125% e più piccoli; le pagine sono compatte per stare in un Full HD. Con **Un PC solo** apre sempre il programma di quel PC (`https://localhost:porta`), anche se prima il PC era un server o una postazione: niente *Cambia server*. Mentre il programma si avvia mostra *Avvio di TD Booking Suite…* e riprova da sola; non mostra mai le pagine di errore del browser, e il tasto destro offre solo taglia, copia e incolla.
- **Secondo fattore**: con Un PC solo non c'è, si entra con username e password. Con Server con postazioni Authenticator e passkey sono facoltativi, e l'avviso *Proteggi il tuo account* lo vede solo l'amministratore del programma.

### Installazione da riga di comando

```bat
rem Un PC solo
msiexec /i TD-Booking-Suite-2.22.13.msi MODALITA=standalone /qn
rem Server con postazioni, con porta e indirizzi
msiexec /i TD-Booking-Suite-2.22.13.msi MODALITA=server PORTA=8443 INDIRIZZO=https://ufficio-pc:8443 ALTRI_INDIRIZZI=192.168.1.10 /qn
rem Solo postazione
msiexec /i TD-Booking-Suite-2.22.13.msi ADDLOCAL=Postazione /qn
```

Senza `MODALITA` l'installazione tiene il tipo che il PC aveva: solo la finestra se c'è solo la postazione, altrimenti quello ricordato nel registro. Un server della 2.8, o i dati di un server rimasti dopo averlo disinstallato, o un `INDIRIZZO` dato, fanno Server con postazioni; un PC nuovo parte come Un PC solo. `MODALITA=postazione` da sola non si usa: ferma l'installazione e dice di usare `ADDLOCAL=Postazione`.

L'indirizzo del programma è `https://NOME-DEL-PC:8443` (con Un PC solo `https://localhost:8443`), e lo trovi nella pagina **Postazioni**.

### Le postazioni

1. Sugli altri PC installa lo stesso MSI scegliendo **Solo postazione**.
2. Al primo avvio il programma chiede l'**indirizzo del server** e mostra l'**impronta del certificato**. Se è uguale a quella della pagina Postazioni del server, premi **Continua**.
3. Sul server, in **Postazioni**, scrivi un nome (per esempio *Reception*) e premi **Genera il codice**. Sulla postazione scrivi le 6 cifre entro 15 minuti. La registrazione si fa una volta sola. Dopo 10 tentativi da uno stesso indirizzo, quell'indirizzo aspetta 15 minuti.

Da **browser o telefono** basta aprire l'indirizzo del server e inserire il codice. Se il browser avvisa che la connessione non è sicura, scarica il certificato dal link nella pagina e installalo tra le autorità attendibili.

Le postazioni contano per la licenza, il PC server no. Una postazione persa si **revoca** dalla pagina Postazioni. Il programma per la postazione segue il tema chiaro o scuro di Windows; per cambiare server: menu Start, **Cambia server della postazione**, oppure clic sull'icona in alto a sinistra della finestra e poi **Cambia server**.

La finestra usa da sola il server installato sullo stesso PC solo se lo ha installato l'MSI (lo dice il suo valore nel registro, che scrivono solo gli amministratori): su una postazione un utente normale non può farle aprire un server suo.

![Registra la postazione](docs/screenshots/registra-postazione.png)

**Copia dell'agenda per quando il server non risponde.** Mentre è collegata, la finestra (sulle postazioni, ma anche sul PC server e con Un PC solo) salva da sola una copia di sola lettura del **foglio del giorno di oggi e di domani** dell'ultima attività aperta: alla prima pagina dell'attività e poi ogni 15 minuti, con solo quello che può vedere chi è collegato. Sta in `%LOCALAPPDATA%\TD Booking Suite\Copia agenda`, cifrata per quell'utente di Windows; quelle di prima di ieri si cancellano. Se il server non risponde, la pagina *Il server non risponde* ha **Apri la copia dell'agenda (sola lettura)**: si apre con la striscia gialla con data e ora della copia, e **Riprova** torna al programma. Sul PC server il pulsante compare dopo i primi due minuti dall'avvio. La copia non tiene aperta la sessione (dopo 30 minuti senza nessuno si esce come sempre) e serve un server 2.14.0 o più nuovo. Se non si decifra (profilo di Windows copiato su un altro PC, file rovinato) si rifà appena il server risponde.

### Un PC solo: Online e sito

Con **Un PC solo** la pagina prenotazioni per i clienti e il modulo nel sito WordPress sono **spenti** di serie: il programma risponde solo a quel PC e i loro indirizzi dicono che non sono attivi. L'amministratore del programma li accende da **Online e sito** nel menu:

1. nella finestra di TD Booking Suite premi **Accendi** e dai il permesso a Windows (Controllo account utente);
2. il PC risponde anche dalla rete sulla porta del programma, come un server, e apre quella porta nel firewall (reti private e di dominio); i servizi ripartono e la pagina si aggiorna da sola;
3. per Internet il router inoltra la porta a questo PC e nella stessa pagina scrivi l'indirizzo pubblico e, se c'è, il proxy davanti.

Da fuori si raggiungono **solo** la pagina prenotazioni e il collegamento del sito: agenda, clienti e configurazione restano su quel PC. **Spegni** torna come prima. Da un browser il pulsante mostra il comando da eseguire come amministratore: `setup-server.ps1 -Online on` (o `off`), nella cartella `server\script` del programma.

### Da fuori dall'ufficio: proxy, certificato, server in cloud

Le sedi si collegano tutte allo **stesso server**, come postazioni: non c'è niente da sincronizzare. Il server può essere un PC dell'ufficio raggiungibile da fuori oppure un Windows in cloud. Tutto si imposta dopo l'installazione in **Rete e certificato**, che vede e modifica solo l'amministratore del programma, con la conferma della password o del secondo fattore:

- **Indirizzo pubblico**, per esempio `https://booking.tuosito.it`: è quello dei link nelle email (conferme, consensi, inviti) e da usare fuori dall'ufficio. Vale da subito, senza riavviare.
- **Proxy fidati**: gli IP dei proxy davanti al server, uno per riga o come rete (`10.0.0.0/24`). Da loro il programma legge l'indirizzo vero di chi si collega, per i limiti di accesso e l'elenco delle postazioni. Chi passa dal proxy non è mai il PC server: registra la postazione con il codice.
- **Certificato**: quello interno, che il programma crea e rinnova da solo, oppure uno tuo caricato come `.pfx`/`.p12` o `.crt`/`.pem` con la sua `.key`, con la catena completa. Il programma controlla che chiave e certificato vadano insieme e che non sia scaduto, lo usa dalla connessione successiva e un mese prima della scadenza lo ricorda in cima alle pagine. Se scade torna da solo al certificato interno.

**Con un proxy** (NPMplus, Nginx Proxy Manager, Caddy, IIS) che ha il suo certificato, per esempio Let's Encrypt:

1. nel proxy crea l'host con il nome pubblico, destinazione `https://IP-DEL-SERVER:8443`, schema **https**, senza controllare il certificato del server;
2. lascia l'intestazione Host com'è e aggiungi X-Forwarded-For (NPMplus lo fa già);
3. in Rete e certificato scrivi l'indirizzo pubblico e l'IP del proxy.

**Senza proxy**: carica un certificato per il nome pubblico, punta il DNS sull'IP della macchina e apri la porta anche alla rete pubblica:

```powershell
Set-NetFirewallRule -DisplayName "TD Booking Suite" -Profile Any
```

**Certificati autofirmati.** Il programma per la postazione si fida da solo di un certificato riconosciuto da Windows. Con il certificato interno o con uno autofirmato mostra l'impronta al primo collegamento: se è uguale a quella delle pagine Postazioni e Rete e certificato, premi **Continua** e da lì in avanti la ricorda. Se un giorno il certificato cambia, il programma avvisa e chiede se vuoi vedere quello nuovo e accettarlo: non si blocca, ma non accetta niente da solo.

**La versione web.** Il programma per la postazione si collega anche a TD Booking Suite installato con Docker su un server Linux: installa **Solo postazione** e scrivi l'indirizzo, per esempio `https://booking.tuosito.it`.

Le **passkey** valgono per l'indirizzo con cui sono state create: chi entra sia dal nome dell'ufficio sia da quello pubblico ne registra una per ciascuno. Funzionano dove il browser si fida del certificato: il certificato interno sul server ce lo mette l'installer, sulle postazioni lo puoi installare dalla pagina di registrazione. Altrove si usa **Authenticator** per il secondo fattore.

## Licenze

Le licenze le crei tu con **TD Licenze.exe**. Lo costruisce GitHub a ogni push e, da solo, il 1° di ogni mese: l'artefatto resta 90 giorni, quindi ce n'è sempre uno da scaricare.

1. apri **Actions**, poi l'ultima esecuzione verde;
2. in fondo, in *Artifacts*, scarica **TD-Licenze**;
3. scompattalo in una cartella, di solito su una chiavetta, e apri `TD Licenze.exe`. Windows lo segnala come l'MSI e si apre allo stesso modo.

È **portatile**: registro, collegamento a GitHub e licenze emesse si salvano solo nella sua cartella, accanto all'exe, e da nessun'altra parte. Dalla chiavetta lo apri sul PC del cliente, copi il codice installazione dalla sua pagina Licenza, crei la licenza e la incolli. Per il backup basta copiare la cartella intera. Se una versione precedente aveva messo la chiave in `%APPDATA%\TD Licenze`, al primo avvio la porta nella sua cartella. Per aggiornare TD Licenze sostituisci solo `TD Licenze.exe` e la cartella `_internal`: i file della chiave non si toccano.

L'artefatto è privato come la repo e non va mai dato ai clienti.

| Scheda | A cosa serve |
|---|---|
| **Chiave** | La prima volta crea la chiave delle licenze, con una password, e mostra la chiave pubblica da copiare |
| **Nuova licenza** | Tipo, intestatario, attività, postazioni, codice installazione e scadenza: **Crea la licenza**, e la licenza è già pronta da copiare |
| **Licenze emesse** | Tutte quelle fatte, con lo stato (in uso, sostituita o revocata): doppio clic per copiarne di nuovo una, **Cambia postazioni o scadenza** per farne una che la sostituisce, **Rendi permanente**, **Metti una scadenza** e **Revoca**, **Aggiorna da GitHub** per le licenze fatte sugli altri PC. Il programma del cliente lo sa da solo entro un giorno, quando è online: la licenza nuova arriva senza incollarla, la vecchia o la revocata smette di valere |
| **Credenziali** | Gli accessi creati per ogni cliente (amministratore del programma, titolari, persone, password dei backup, codici di recupero e del primo avvio, email, Telegram, sito, rete), cifrati con la password della chiave in `credenziali.tdc` e, con il registro collegato, anche su GitHub: se un cliente perde una password la ritrovi. Cerca, copia (negli appunti 30 secondi), genera, esporta e importa CSV; da **Licenze emesse**, **Credenziali del cliente** |
| **Registro su GitHub** | Collega la repo privata dove tenere il registro: lo ritrovi con TD Licenze su qualunque PC. Con la repo pubblica `TD-Licenze-Stato` (anche lei nel token) pubblica lo stato delle licenze, firmato |

| Tipo | Attività | Postazioni in rete |
|---|---|---|
| **Un PC solo** | 1 | nessuna |
| **Server con postazioni** | 1 | quante ne scegli, oltre al PC server |
| **Portale, più attività** | 2 o più | quante ne scegli |

**La chiave, una volta sola.** La build mette la chiave privata **dentro TD Licenze.exe**, cifrata con la sua password: ogni TD Licenze scaricato da Actions, su qualunque PC, chiede solo la password e decifra la chiave in memoria.

1. Apri TD Licenze in una cartella vuota e crea la chiave dalla scheda **Chiave**, con una password lunga. Nella cartella compaiono `chiave-privata.pem` e `chiave-pubblica.txt`.
2. Il contenuto di `chiave-pubblica.txt` va in `tdwin/licenza-chiave-pubblica.txt`. La build lo scrive **dentro il codice compilato** del programma: nell'MSI non c'è un file con la chiave da sostituire.
3. **Settings → Secrets and variables → Actions → New repository secret**: `TD_LICENZE_CHIAVE`, con tutto il contenuto di `chiave-privata.pem` (righe `BEGIN` ed `END` comprese). È cifrata: senza password non firma.
4. Commit e push. Le licenze fatte con un'altra chiave non valgono nelle build nuove.
5. Tieni `chiave-privata.pem` e la password fuori dal PC: GitHub non fa rileggere un secret, e senza la chiave non puoi più fare licenze per le installazioni che esistono già.

La chiave privata non va mai nella repo come file (`.gitignore` blocca i `.pem`, il registro, le licenze emesse e `licenze/chiave_incorporata.py`, che la build scrive dal secret). Senza il secret TD Licenze tiene la chiave nella sua cartella, come prima.

**Il registro su GitHub.** Il registro può stare anche in una repo privata solo per i dati, per esempio `TD-Licenze-Dati`. TD Licenze lo aggiorna da lì all'apertura e dopo ogni licenza; senza Internet lavora sulla copia della cartella e manda le licenze nuove dopo. Se due PC scrivono insieme, rilegge e unisce.

1. Crea la repo privata e vuota.
2. Crea un token **fine-grained** solo su quella repo, con **Contents: Read and write**.
3. Scheda **Registro su GitHub**: repo, token, **Collega e sincronizza**. Il token resta in `registro-github.json`, cifrato con la password della chiave.
4. Facoltativo: il secret `TD_LICENZE_REGISTRO` con tutto il contenuto di `registro-github.json`. Così anche un TD Licenze appena scaricato trova la repo da solo.

Su GitHub va solo `registro-licenze.csv`: la chiave privata e il token restano fuori.

Ogni licenza esce come riga `TDBS1.…` e come file `licenze-emesse/licenza-TD-2026-0001-bar-roma.txt`, da mandare al cliente. Resta anche nel `registro-licenze.csv`.

Chi preferisce la riga di comando ha `licenze/td_licenze.py` (Python 3 e `pip install cryptography`). Di serie lavora nella sua cartella; con `--cartella` usa quella di TD Licenze.exe:

```bash
python licenze/td_licenze.py chiave       # la prima volta
python licenze/td_licenze.py              # fa le domande
python licenze/td_licenze.py crea --intestatario "Bar Roma" --tipo server --postazioni 3 --installazione ABCD-EFGH-IJKL-MNOP --scadenza 2027-12-31
python licenze/td_licenze.py crea --sostituisce TD-2026-0007 --postazioni 5   # il resto resta come nella 0007
python licenze/td_licenze.py elenco       # quelle emesse, dal registro
python licenze/td_licenze.py verifica     # incolli una licenza e vedi cosa contiene
python licenze/td_licenze.py collega Giuseppe-sciarra/TD-Licenze-Dati   # la repo del registro, chiede il token
python licenze/td_licenze.py sincronizza  # registro aggiornato con GitHub
```

| Cosa succede se il cliente… | |
|---|---|
| reinstalla il programma sullo stesso PC e ripristina un backup | il codice installazione torna quello di prima e la licenza vale ancora |
| reinstalla Windows o cambia PC | nuovo codice: gli fai una licenza nuova con il codice nuovo |
| copia la cartella dei dati su un altro PC | il codice cambia (contiene l'ID del PC) e la licenza non vale lì |
| ha più attività di quelle della licenza nuova | il programma rifiuta la licenza e lo spiega |
| ha più attività di quelle della licenza installata | le ultime create restano chiuse; i dati restano e sono nei backup |
| ha più postazioni registrate | restano attive quelle registrate per prime, finché non ne revoca |
| arriva alla scadenza | il programma si ferma come a fine prova: restano aperte Licenza e Backup |
| sposta indietro la data del PC | prova e scadenza si contano dall'ultimo uso: non si allungano |
| cancella la prova dal database | la prova resta usata: il segno sta anche nella cartella dei dati |

### Come il programma protegge la licenza

- Le licenze sono firmate con **Ed25519**: il programma contiene solo la chiave pubblica, quindi le verifica ma non le può creare, e funziona offline.
- `windows/compile_tdwin.py`, nella build, scrive la chiave pubblica **dentro la verifica** e compila con **Cython** tutto il codice di `tdwin` in moduli nativi (`.pyd`): nell'MSI non ci sono i sorgenti dell'edizione Windows, né un file con la chiave, né le impostazioni dei test. La build si ferma se resta un `.py` di tdwin (a parte `__init__` e migrazioni).
- Prima di fidarsi di una licenza la verifica prova una **firma falsa**: se viene accettata, qualcuno ha sostituito il verificatore e il programma si ferma.
- All'avvio (`tdwin/apps.py`) il programma controlla che il controllo della licenza sia nelle impostazioni e che le sue funzioni siano quelle compilate; il middleware lo ricontrolla a ogni pagina. Altrimenti non parte: *TD Booking Suite è stato modificato*.
- L'**orologio**: l'ultimo momento in cui il programma ha lavorato sta nel database e nella cartella dei dati, e prova e scadenza si contano da lì.
- La **prova** è segnata nel database e nella cartella dei dati: cancellarne uno non la ridà.
- Le **attività oltre la licenza** restano chiuse anche se qualcuno toglie il limite dal core.

Su un PC dove il cliente è amministratore nessuna protezione è assoluta: questa serve a rendere necessario un reverse engineering vero, non la modifica di un file. Resta un limite noto: un port forward sul PC server (netsh portproxy, un proxy senza X-Forwarded-For) fa sembrare locali gli altri PC, che così non contano come postazioni.

## Vetrina pubblica

Il codice resta in questa repo privata. Quello che vedono tutti sta in **[TD-Booking-Suite-per-Windows](https://github.com/Giuseppe-sciarra/TD-Booking-Suite-per-Windows)**, che è la pagina da collegare al sito:

- presentazione;
- screenshot;
- istruzioni;
- gli MSI da scaricare, con la prova di 7 giorni.

Per pubblicare una versione apri **Actions → Windows → Run workflow**, spunta **Pubblica il rilascio** e premi **Run workflow**. Va bene anche un tag, per esempio `git tag v2.22.0 && git push origin v2.22.0`. La build fa i test, l'MSI e la prova di installazione. Poi copia nella vetrina `vetrina/README.md` e `docs/screenshots/`, e pubblica lì una Release con l'MSI. Per scrivere nella vetrina usa il secret `VETRINA_TOKEN`: un token fine-grained con *Contents: Read and write* solo su quella repo. Il testo della vetrina si cambia in `vetrina/README.md`. Le immagini di TD Licenze stanno in `docs/manuali/img-licenze`, mai in `docs/screenshots`, che va in pubblico.

## Backup e ripristino

Dalla pagina **Backup** imposti:

- la **password dei backup**: senza, il file non si apre, nemmeno da chi ha installato il programma;
- l'**ora del backup**, una volta al giorno. Se il PC è spento a quell'ora, il backup parte appena si riaccende;
- quante **copie tenere**.

Ogni backup contiene il database, le chiavi che lo aprono, la licenza e il codice installazione, tutto cifrato con AES-256. Resta sul PC server in `C:\ProgramData\TD Booking Suite\backup` e viene copiato su ogni **destinazione**:

- **Disco esterno o chiavetta**: percorso come `E:\Backup TD Booking`. Se il disco è staccato, la copia sul server si fa lo stesso e nello storico vedi la destinazione non riuscita. Con due dischi alternati ogni settimana hai sempre una copia fuori dall'ufficio.
- **Cartella di rete o NAS**: `\\nome-del-nas\cartella`, con l'utente e la password del NAS. Non serve collegare l'unità in Windows.
- **FTP, FTPS, SFTP**: server, utente, password e cartella. Meglio SFTP o FTPS. Al primo collegamento il programma memorizza la chiave o il certificato del server, e poi controlla che non cambi. Un server FTPS con un certificato riconosciuto da Windows resta legato a quello: se un giorno il certificato non è più riconosciuto, il programma non manda la password e lo segnala.

Ogni destinazione ha il pulsante **Prova**, e lo **storico** dice com'è andato ogni backup. Se un backup non riesce, o i backup sono spenti, l'amministratore lo vede in cima alle pagine.

**Ripristino**: sul PC server apri menu Start, **TD Booking Suite**, **Ripristina backup**. Scegli il file `.tdbk` e scrivi la password. Il programma si ferma un minuto, rimette i dati e riparte. Il database di prima non viene cancellato: resta da parte con la data nel nome. Su un PC nuovo installa prima l'MSI, poi ripristina, poi chiedi la licenza con il nuovo codice installazione.

**Password dimenticata**, dell'amministratore del programma, del titolare o di chiunque: menu Start, **TD Booking Suite**, **Recupera accesso**, con username o email. Serve il PC server e un account amministratore di Windows. Azzera anche Authenticator e passkey, apre la pagina per la password nuova (valida 24 ore) e resta scritto nel Registro attività di ogni attività dell'account, come *Console del server*.

## Aggiornare, cambiare tipo, disinstallare

Per aggiornare, apri l'MSI nuovo sopra quello vecchio: tipo, servizi, porta, database e dati restano. Prima di applicare la versione nuova, l'installer salva una copia del database in `backup\prima-dell-aggiornamento.dump`. Se l'aggiornamento non riesce, Windows rimette la versione di prima e la fa ripartire.

Per **cambiare tipo** apri di nuovo l'MSI (o Impostazioni › App › TD Booking Suite › Modifica), scegli di cambiare l'installazione e il tipo nuovo. Da riga di comando: `msiexec /i TD-Booking-Suite-2.22.13.msi MODALITA=standalone /qn`. I dati restano.

La disinstallazione toglie programma e servizi ma lascia i dati in `C:\ProgramData\TD Booking Suite`. Per cancellarli davvero, elimina quella cartella.

**Reinstallare dopo averlo tolto.** Se l'installer trova i dati di un'installazione di prima, dopo il tipo chiede cosa farne:

- **Usa i dati di prima**, già scelto: il programma riparte da dove era, con le stesse attività, clienti e prenotazioni, e si entra con l'amministratore di allora;
- **Ricomincia da zero**: un programma vuoto e al primo avvio si crea l'amministratore. I dati di prima non si cancellano: vanno, interi, in `C:\ProgramData\TD Booking Suite (dati del <giorno e ora>)`. Restano licenza, prova, codice installazione, indirizzi, porta e certificato, così la licenza vale ancora e le postazioni aprono il server come prima.

Da riga di comando: `msiexec /i TD-Booking-Suite-2.22.13.msi MODALITA=standalone RICOMINCIA=1 /qn`. Senza `RICOMINCIA` usa i dati di prima. Un aggiornamento non lo chiede: i dati restano sempre.

Da zero anche senza disinstallare, da PowerShell come amministratore:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\TD Booking Suite\server\script\setup-server.ps1" -Ricomincia 1
```

Se un da zero si ferma a metà (dati già messi da parte, database non ancora creato), basta rilanciarlo o riaprire l'MSI: finisce il lavoro e tiene codice installazione, indirizzi e porta.

## Cosa c'è sul PC server

| | |
|---|---|
| `C:\Program Files\TD Booking Suite\server` | Python, il programma (tdwin compilato in `.pyd`), PostgreSQL 17, gli script |
| `C:\Program Files\TD Booking Suite\postazione` | il programma per la postazione (WebView2) |
| `C:\ProgramData\TD Booking Suite` | configurazione, database, licenza, certificati, backup, log |
| `C:\ProgramData\TD Booking Suite Postazione\server.txt` | indirizzo e impronta del server di questo PC, per la finestra |
| servizio **TDBookingDB** | PostgreSQL, solo su 127.0.0.1, porta 55432 |
| servizio **TDBookingWeb** | il programma in HTTPS sulla porta 8443 (con Un PC solo solo su 127.0.0.1, finché *Online e sito* è spento) |
| servizio **TDBookingWorker** | email, promemoria, avvisi della lista d'attesa, riepilogo serale, pulizia dei dati scaduti ogni giorno e backup |

Ogni servizio gira con un suo account virtuale di Windows (`NT SERVICE\…`):

- il servizio web **non può leggere** la password del proprietario del database e ha solo i diritti sui dati, con l'isolamento tra attività del core;
- solo il worker, che fa i backup, la legge;
- la cartella dei dati la vedono solo Windows, gli amministratori e i servizi.

Una cartella dati, o la cartella condivisa della postazione, creata prima dell'installazione da un utente normale (magari con una sua autorità di certificazione o un `config.json` suo dentro) non viene usata: l'installazione la mette da parte come `….non-fidata-<data>` e ne crea una nuova. Una giunzione al loro posto ferma l'installazione.

Il firewall apre la porta solo quando il programma risponde dalla rete: Server con postazioni, o Un PC solo con *Online e sito* acceso. Solo sulle reti private e di dominio.

## Costruire l'MSI

GitHub Actions (`.github/workflows/windows.yml`) gira a ogni push su `main`:

1. esegue i test;
2. costruisce l'MSI con `windows/build.ps1`, che compila tdwin con Cython (`windows/compile_tdwin.py`);
3. lo installa davvero su Windows (`windows/smoke-test.ps1`): un server con i servizi e i loro account, HTTPS con il certificato, un backup fatto dal worker; poi l'aggiornamento di un server come quelli della 2.8, senza il tipo nel registro, che resta server con porta, dati e backup; poi la modifica verso Un PC solo (solo su 127.0.0.1, niente firewall); infine la disinstallazione che lascia i dati;
4. lo mette tra gli *Artifacts* dell'esecuzione, per 30 giorni.

Con un tag `v2.22.0` crea anche una **Release** con l'MSI allegato e, dalla 2.22.0, lo zip di **TD Cassa di prova** (`windows/cassa-prova/`): una cassa finta che risponde in rete come una RCH Print!F o una Epson FP RT, non stampa niente e mostra ogni documento ricevuto, per provare la Cassa senza il registratore. Si avvia su un PC della rete (o sullo stesso), si scrive il suo indirizzo in Registratore di cassa e si prova tutto: collegamento, scontrino di prova, conti, annulli, chiusura, anche carta finita e cassa che non risponde.

### Firma del codice

La build firma il programma della postazione, i moduli `.pyd` di tdwin, l'MSI e TD Licenze.exe (SHA-256, con marca temporale) solo se trova un certificato nei secret della repo (**Settings → Secrets and variables → Actions**, `windows/firma.ps1`). Senza, tutto resta come prima, non firmato, e il log lo dice in una riga.

- **Certificato OV o EV in un file `.pfx`**: `WINDOWS_CERT_PFX`, il file in base64 (in PowerShell `[Convert]::ToBase64String([IO.File]::ReadAllBytes("firma.pfx"))`), e `WINDOWS_CERT_PASSWORD`, la sua password.
- **Azure Artifact Signing** (ex Trusted Signing, da circa 10 $ al mese, anche per le aziende in UE): `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` e `AZURE_CLIENT_SECRET` di un'app registrata con il ruolo *Certificate Profile Signer* sull'account di firma, più `ARTIFACT_SIGNING_ENDPOINT` (per esempio `https://weu.codesigning.azure.net/`), `ARTIFACT_SIGNING_ACCOUNT` e `ARTIFACT_SIGNING_PROFILE`.

Dal 2023 i certificati OV ed EV delle autorità hanno la chiave su un token USB o in un HSM in cloud e di norma non si esportano in `.pfx`: per firmare su GitHub Actions va bene Azure Artifact Signing, oppure il servizio di firma in cloud dell'autorità, da aggiungere in `windows/firma.ps1`. Anche firmato, un certificato nuovo (EV compreso) toglie gli avvisi di SmartScreen dopo un po' di installazioni.

Per costruirlo a mano su Windows servono Python 3.14, .NET SDK 8, PowerShell 7 e i Visual Studio Build Tools con il compilatore C++ (per Cython):

```powershell
git clone --recurse-submodules https://github.com/Giuseppe-sciarra/TD-Booking-Suite-Windows
cd TD-Booking-Suite-Windows
pwsh windows/build.ps1
```

Per prendere l'ultima versione del core:

```bash
git submodule update --remote suite && git commit -am "Core aggiornato"
```

Le istruzioni in PDF si rifanno con `python docs/manuali/build.py` (Pillow e Playwright con Chromium), dagli HTML di `docs/manuali/` e dagli screenshot di `docs/screenshots/`.

## Struttura

| | |
|---|---|
| `suite/` | il core web, sottomodulo del repository pubblico |
| `tdwin/` | l'app Django dell'edizione Windows: licenza, postazioni, primo avvio, configurazione guidata, HTTPS, certificati, rete, backup e ripristino |
| `licenze/` | TD Licenze, il programma per creare le licenze, solo per chi vende |
| `windows/client/` | il programma per la postazione, C# .NET Framework 4.8 con WebView2 |
| `windows/cassa-prova/` | TD Cassa di prova, la cassa finta per i test, C# .NET Framework 4.8 |
| `windows/wix/` | l'MSI, WiX 5, con la finestra della scelta del tipo (`Modalita.wxs`) |
| `windows/script/` | configurazione del server, rimozione, ripristino, recupero di un accesso, codice del primo avvio |
| `windows/servizi/` | i servizi Windows, WinSW |
| `windows/compile_tdwin.py` | la compilazione di tdwin con Cython e la chiave della licenza, nella build |
| `windows/firma.ps1` | la firma del codice nella build, solo con il certificato nei secret |
| `docs/` | le istruzioni in PDF, gli screenshot e le loro sorgenti in `docs/manuali/` |
| `tests/` | test, anche con PostgreSQL vero: `TDWIN_PG_ADMIN=postgresql://postgres:…@127.0.0.1:5432/postgres pytest` |

## Licenza

Tutti i diritti riservati, vedi [LICENSE](LICENSE). Il core in `suite/` è AGPL-3.0.
