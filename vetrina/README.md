# TD Booking Suite per Windows

**Prenotazioni, agenda e clienti in un programma per Windows: i dati restano sul tuo PC, in ufficio.**

TD Booking Suite gestisce appuntamenti, tavoli, camere, lezioni ed eventi. Si installa su un PC solo, oppure su un PC dell'ufficio che fa da server per tutte le postazioni della rete, il browser e il telefono. Lo fa [Tastiere Digitali](https://tastieredigitali.tech).

**[Scarica l'ultima versione](../../releases/latest)**: un solo file MSI, per un PC solo, per il server e per le postazioni. I primi 7 giorni sono di prova gratuita, con tutte le funzioni. Le **[istruzioni complete in PDF](Istruzioni-TD-Booking-Suite-Windows.pdf)** spiegano tutto, passo per passo.

| | |
|---|---|
| ![Agenda per operatore](screenshots/agenda.png) | ![Operatore](screenshots/operatore.png) |
| **Agenda del giorno**: una colonna per operatore con la sua foto, per tutti o per uno solo; un clic su uno spazio libero e la prenotazione parte con operatore, giorno e ora | **Operatori**: la foto, i trattamenti che fanno con durata e prezzo loro, i giorni e orari, le ferie |
| ![Settimana](screenshots/settimana.png) | ![Mese](screenshots/mese.png) |
| **Settimana**: i sette giorni ora per ora, un colore per operatore | **Mese**: quanti appuntamenti ha ogni giorno, un clic apre il giorno |
| ![Scheda cliente](screenshots/cliente.png) | ![Al lavoro](screenshots/al-lavoro.png) |
| **Scheda cliente**: recapiti, sconto, speso, appuntamenti in programma e ultimo trattamento | **Dashboard**: prenotazioni di oggi e dei prossimi giorni, agenda e clienti a portata di clic |
| ![Configurazione guidata](screenshots/guida.png) | ![Chi lavora nell'attività](screenshots/guida-io.png) |
| **Configurazione guidata**: licenza, attività, backup e postazioni, un passo alla volta | **Uno o due account**: lo usi tu con un account solo, oppure lo installi per un cliente con il suo |
| ![Accesso](screenshots/accesso.png) | ![Tema scuro](screenshots/dashboard-scuro.png) |
| **Accesso** con username o email, o con un clic per il titolare dal PC del programma: resta collegato, e **Esci** per cambiare account | **Tema chiaro o scuro**, con il colore che preferisci |
| ![Postazioni](screenshots/postazioni.png) | ![Backup](screenshots/backup.png) |
| **Postazioni**: ogni PC si registra una volta con un codice di 6 cifre | **Backup automatici**, cifrati, su disco esterno, NAS o server remoto |
| ![Importa clienti](screenshots/import.png) | ![Anteprima dell'import](screenshots/import-anteprima.png) |
| **Importa i clienti** dal programma di prima, da Excel o CSV | **Anteprima** prima di importare: cosa entra e cosa no |
| ![Prossimi 7 giorni](screenshots/dashboard-7giorni.png) | ![Logo e aspetto](screenshots/logo-aspetto.png) |
| **Oggi e prossimi 7 giorni**: un clic sulla dashboard e vedi chi viene e a che ora, giorno per giorno | **Logo e aspetto**: il logo dell'attività in alto nel menu, il colore e la frase sotto il nome, con l'anteprima |
| ![Email ai clienti](screenshots/notifiche.png) | ![Rete e certificato](screenshots/rete.png) |
| **Email ai clienti**: conferma, modifica, annullamento e promemoria, con il tuo testo e l'anteprima | **Rete e certificato**: indirizzo pubblico, proxy e certificato, solo per l'amministratore |

## Cosa fa

- **Agenda e prenotazioni** per parrucchieri, centri estetici, SPA, ristoranti, studi, autoscuole, officine, hotel, case vacanza, noleggi, tour ed eventi. Ci sono gli orari, le risorse e gli operatori, e le domande giuste per ogni tipo di attività.
- **Agenda del giorno, della settimana e del mese** per parrucchieri, estetica, studi e campi: nel giorno una colonna per ogni operatore che lavora, nella settimana i sette giorni ora per ora, nel mese quanti appuntamenti ha ogni giorno. Ogni operatore ha il suo colore. Un clic su uno spazio libero e la prenotazione parte con operatore, giorno e ora già messi.
- **Operatori con foto, trattamenti, orari e ferie**: ognuno fa i servizi che spunti, ognuno con la sua durata e il suo prezzo se diversi, e, se serve, ha i suoi giorni di lavoro. Il cliente sceglie con chi prenotare dalla foto.
- **Prenotazioni online** dalla pagina dell'attività, quando il server è raggiungibile da Internet, con conferme e promemoria per email. Con Un PC solo si accendono da *Online e sito*.
- **Email ai clienti con promemoria**: conferma, modifica e annullamento partono da sole; quando l'amministratore lo attiva, il titolare sceglie quali partono, aggiunge il promemoria da 2 ore a 3 giorni prima e scrive i testi, con l'anteprima. Il cliente sceglie se riceverle.
- **Clienti**: scheda con i recapiti sempre in vista, quanto ha speso, cosa ha in programma, lo storico con i prezzi, le note, i trattamenti, le intolleranze e i consensi marketing.
- **Listino e sconti**: il titolare tiene servizi, varianti e prezzi; a un cliente dai uno sconto in percentuale o fisso, che vale da subito sulle sue prenotazioni.
- **Importa dal programma di prima**: anagrafica e storico dei trattamenti da Excel o CSV. Per ogni colonna scegli cosa contiene e guardi l'anteprima prima di importare. Chi c'è già non viene sovrascritto né raddoppiato. I consensi marketing restano da confermare dal cliente.
- **Persone e permessi**: ognuno entra con il suo username, l'email è facoltativa, e vede solo quello che il suo ruolo consente. Chi installa configura tipo di attività, sedi, persone con accesso e parte tecnica; il titolare lavora e tiene aggiornati orari, operatori, listino e il suo logo.
- **Una o più attività**: le licenze Un PC solo e Server sono per un'attività, la licenza Portale per chi ne gestisce diverse.
- **Più postazioni**: i PC della rete usano il programma per la postazione; browser e telefono vanno bene lo stesso. Le sedi lontane si collegano allo stesso server.
- **Da fuori dall'ufficio**: dietro un proxy con il suo certificato (NPMplus, Caddy, IIS) o con un tuo certificato caricato nel programma. Anche i certificati autofirmati vanno bene: le postazioni li riconoscono dall'impronta. Il programma per le postazioni si collega anche alla versione web su Docker.
- **Sicurezza**:
  - HTTPS anche in rete locale;
  - con le postazioni, secondo fattore facoltativo con Authenticator o passkey;
  - dati di ogni attività cifrati e separati;
  - ogni parte del programma con i suoi permessi di Windows;
  - l'amministratore del programma lo crea solo un amministratore di Windows del PC.
- **Backup automatici ogni notte**, cifrati con la tua password, su disco esterno, NAS, FTP, FTPS o SFTP. Il ripristino si fa dal menu Start.
- **Il tuo marchio**: nome, loghi, icona, colore principale e tema di partenza si cambiano dalle impostazioni; ogni attività mette il suo logo, che compare in alto nel menu.

## Installare

### Windows ti avvisa: si installa lo stesso

Il file non ha ancora una firma digitale, quindi Windows non sa chi lo pubblica e lo segnala:

1. **Edge o Chrome** dicono che il file *non viene scaricato comunemente*. Apri il menu **…** del download, poi **Mantieni**, **Mostra altro** e **Mantieni comunque**.
2. **Windows ha protetto il PC**, la finestra blu di SmartScreen: premi **Ulteriori informazioni** e poi **Esegui comunque**.
3. Se il file arriva da una chiavetta o da una email e non parte: tasto destro, **Proprietà**, spunta **Sblocca**, **OK**.

### Scegli come usarlo

Durante l'installazione il programma chiede **Come userai TD Booking Suite su questo PC?**

| | |
|---|---|
| **Un PC solo** | Programma e dati su questo PC, che si usa da solo: niente altri PC o telefoni. Per un'attività con un solo computer. Si entra con username e password. La pagina prenotazioni e il sito si accendono quando servono, da *Online e sito* |
| **Server con postazioni** | Questo PC tiene i dati e resta acceso. Gli altri PC con la postazione, tablet e telefoni dell'ufficio e, se lo metti online, il sito si collegano a lui. Qui si usa anche il programma |
| **Solo postazione** | Solo la finestra del programma, per un PC che si collega a un server già installato in ufficio o a TD Booking Suite su Internet |

La scelta si cambia dopo, aprendo di nuovo l'installazione: i dati restano.

Se lo reinstalli dopo averlo tolto, l'installer trova i dati di prima e chiede: **Usa i dati di prima**, e riparte da dove era, oppure **Ricomincia da zero**, con un programma vuoto e un nuovo primo avvio. I dati di prima non si cancellano: vanno da parte, interi, in `C:\ProgramData`. Licenza e prova restano.

### Il primo avvio e la configurazione guidata

1. Alla fine dell'installazione si apre TD Booking Suite con il **Primo avvio**. Il codice che chiede lo trovi nel menu Start, **TD Booking Suite**, **Codice del primo avvio** (serve il permesso di amministratore di Windows). Scegli lo **username** e la password dell'**amministratore del programma**; l'email è facoltativa.
2. La **configurazione guidata** ti porta passo per passo:
   - **Licenza**: attiva la **prova di 7 giorni**, oppure incolla la licenza;
   - **La tua attività e chi ci lavora**: nome e tipo dell'attività, poi **Io, con questo account** se il programma lo usi tu, oppure **Il titolare, con un suo account** se lo installi per qualcun altro;
   - **Backup**: password dei backup, backup automatico e una destinazione fuori dal PC;
   - **Postazioni**, se hai scelto Server con postazioni.
3. **Al lavoro**: con un account solo vai direttamente all'agenda; con il titolare, **Esci ed entra come** apre il suo accesso.

| ![Primo avvio](screenshots/primo-avvio.png) | ![Licenza](screenshots/licenza.png) |
|---|---|
| **Primo avvio**: l'amministratore si crea solo dal PC del programma | **Licenza**: cosa consente e il codice installazione da mandarci |

**Resta collegato** è spuntato di serie: il programma si riapre già collegato, sempre. Il titolare può anche entrare senza password, con un clic, dal PC del programma e dalle postazioni registrate. Per entrare con un altro account usa **Esci**, in alto a destra.

### Sulle altre postazioni

1. Installa lo stesso MSI scegliendo **Solo postazione**.
2. Al primo avvio scrivi l'indirizzo del server, per esempio `https://ufficio-pc:8443`, e controlla che l'impronta del certificato sia la stessa della pagina Postazioni del server.
3. Sul server, in **Postazioni**, genera il codice e scrivilo sulla postazione.

Da browser o telefono basta aprire l'indirizzo del server e inserire il codice.

## La licenza

Dopo i 7 giorni di prova il programma chiede la licenza. Prenotazioni e clienti restano salvati, e i backup continuano. Ci sono tre tipi di licenza:

| | Attività | Postazioni in rete |
|---|---|---|
| **Un PC solo** | 1 | nessuna |
| **Server con postazioni** | 1 | quante ti servono, oltre al PC server |
| **Portale** | 2 o più | quante ti servono |

La licenza può avere una **scadenza** e vale per il PC dove l'hai attivata. Per avere più postazioni, più attività o un rinnovo ti mandiamo una licenza nuova: la incolli al posto della vecchia e non perdi niente.

### Come acquistare

Prezzi e formule sono su richiesta. **Contattaci da [tastieredigitali.tech](https://tastieredigitali.tech)** e mandaci il **codice installazione** che trovi nella pagina Licenza del programma: ti spieghiamo le formule e ti mandiamo la licenza per il tuo PC. La incolli nella stessa pagina e il programma riparte subito, con tutti i dati.

## Requisiti

- Windows 10 o 11 a 64 bit. Sul PC con i dati bastano 4 GB di RAM e qualche GB libero.
- Sulle postazioni serve Microsoft Edge WebView2, già presente su Windows 11 e sui Windows 10 aggiornati. Se manca, il programma lo dice e porta alla pagina per scaricarlo.
- Per le postazioni fuori dall'ufficio, il server deve essere raggiungibile da Internet, oppure si usa un server Windows in cloud.

## Domande

**Dove sono i miei dati?** Sul PC con il programma, in `C:\ProgramData\TD Booking Suite`, e nei backup che scegli tu. Non passano da nostri server.

**Posso aggiornare senza perdere niente?** Sì: apri l'MSI nuovo sopra quello vecchio. Prima di aggiornare il programma salva da solo una copia del database.

**Ho dimenticato la password.** Sul PC con il programma, menu Start, **TD Booking Suite**, **Recupera accesso**: vale per l'amministratore e per chiunque abbia un account. Serve un account amministratore di Windows.

**E se cambio PC?** Installa il programma sul PC nuovo e ripristina un backup dal menu Start. Poi chiedici la licenza con il nuovo codice installazione.

**Esiste una versione web, senza Windows?** Sì: il nucleo web di TD Booking Suite è open source, su [github.com/Giuseppe-sciarra/TD-Booking-Suite](https://github.com/Giuseppe-sciarra/TD-Booking-Suite), e si installa con Docker su un server Linux.

---

TD Booking Suite per Windows © Tastiere Digitali. Il programma si usa con una licenza di Tastiere Digitali, o nei 7 giorni di prova. I componenti di terze parti inclusi restano sotto le loro licenze, elencate nell'installer.
