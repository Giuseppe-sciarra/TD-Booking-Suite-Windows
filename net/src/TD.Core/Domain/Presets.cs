using System.Text.Json.Nodes;

namespace TD.Core.Domain;

/// <summary>Activity presets (core/presets.py): defaults of a new service, booking questions, record names, units, words.</summary>
public static class Presets
{
    public sealed record Preset(string Label, string Kind, string ResourceLabel, string Hint, Dictionary<string, object> Defaults);
    static Dictionary<string, object> D(params (string, object)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    public static readonly Dictionary<string, Preset> All = new()
    {
        ["ristorante"] = new("Ristorante / Pizzeria", "capacity", "", "Posti per fascia oraria, il tavolo occupa una durata.", D(("duration", 90), ("interval", 15), ("capacity", 30), ("max_party", 20), ("request_above", 8), ("turnover", false), ("last_arrival", 60))),
        ["parrucchiere"] = new("Parrucchiere / Estetista", "resource", "Operatore", "Operatori con agenda propria, prestazioni con durata.", D(("interval", 15), ("max_party", 1))),
        ["medico"] = new("Studio medico / Fisioterapia", "resource", "Professionista", "Professionisti con agenda propria, visite con durata.", D(("interval", 15), ("max_party", 1))),
        ["consulenza"] = new("Consulenze professionali", "resource", "Consulente", "Appuntamenti con professionisti, in studio o in videochiamata.", D(("interval", 30), ("max_party", 1))),
        ["centro_estetico"] = new("Centro estetico / Nails", "resource", "Operatrice", "Trattamenti con operatrici e prestazioni con durata.", D(("interval", 15), ("max_party", 1))),
        ["spa"] = new("SPA / Massaggi", "resource", "Operatore o cabina", "Massaggi e trattamenti: operatori o cabine con agenda propria.", D(("interval", 15), ("max_party", 2))),
        ["tattoo"] = new("Tattoo / Piercing", "resource", "Artista", "Artisti con agenda propria, sedute anche lunghe.", D(("interval", 30), ("max_party", 1))),
        ["autoscuola"] = new("Autoscuola", "resource", "Istruttore", "Lezioni di guida con istruttori e tipi di lezione.", D(("interval", 30), ("max_party", 1))),
        ["campo"] = new("Campi sportivi / Sale", "resource", "Campo o sala", "Padel, tennis, calcetto, sale riunioni con calendario proprio.", D(("interval", 30), ("max_party", 10))),
        ["corso"] = new("Corsi / Lezioni", "capacity", "", "Lezioni a orari fissi con capienza, per esempio yoga massimo 12.", D(("duration", 60), ("interval", 60), ("capacity", 12), ("max_party", 4))),
        ["officina"] = new("Officina / Gommista", "resource", "Ponte", "Ponti o postazioni con agenda propria: un veicolo alla volta su ognuno, qualunque sia l'intervento.", D(("interval", 30), ("max_party", 1), ("client_picks", false))),
        ["hotel"] = new("Hotel / B&B", "day", "Camera", "Camere con ospiti massimi, notti, check-in e check-out.", D(("min_days", 1), ("max_days", 30), ("checkin", new TimeSpan(15, 0, 0)), ("checkout", new TimeSpan(10, 0, 0)), ("max_party", 6))),
        ["casa_vacanze"] = new("Casa vacanze / Appartamenti", "day", "Alloggio", "Alloggi con ospiti massimi e soggiorno minimo.", D(("min_days", 2), ("max_days", 30), ("checkin", new TimeSpan(16, 0, 0)), ("checkout", new TimeSpan(10, 0, 0)), ("max_party", 8))),
        ["noleggio"] = new("Noleggio bici, mezzi, attrezzatura", "day", "Mezzo", "Mezzi noleggiabili a giorni con ritiro e riconsegna, anche in giornata.", D(("min_days", 0), ("max_days", 366), ("checkin", new TimeSpan(9, 0, 0)), ("checkout", new TimeSpan(19, 0, 0)), ("max_party", 1))),
        ["evento"] = new("Evento / Serata", "event", "", "Posti per ogni data dell'evento.", D(("capacity", 100), ("max_party", 10))),
        ["tour"] = new("Tour / Esperienze", "event", "", "Partenze a orari fissi con posti per ogni data.", D(("capacity", 20), ("max_party", 10))),
    };
    public static IEnumerable<(string Key, string Label)> Choices => new[] { ("", "Personalizzato") }.Concat(All.Select(kv => (kv.Key, kv.Value.Label)));

    static JsonObject Q(string key, string label, string type = "checkbox", string[] choices = null, bool required = false)
    {
        var o = new JsonObject { ["key"] = key, ["label"] = label, ["type"] = type, ["required"] = required };
        if (choices != null) o["choices"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
        return o;
    }
    static JsonObject First => Q("prima_volta", "È la prima volta da noi");
    static JsonObject Arrival(bool req = false) => Q("arrivo", "Orario di arrivo previsto", "select", new[] { "Nel pomeriggio, entro le 18", "In serata, entro le 21", "Dopo le 21" }, req);
    static JsonObject Access => Q("accessibilita", "Esigenze di accessibilità", "text");
    static readonly string[] Driving = { "Guida obbligatoria: veicolo e manovre", "Guida obbligatoria: urbana", "Guida obbligatoria: extraurbana e autostrada", "Guida obbligatoria: notturna", "Guida di esercitazione", "Simulazione d'esame" };

    /// <summary>The questions a new service of that type asks when someone books (a fresh copy every time).</summary>
    public static JsonArray Questions(string key) => new(key switch
    {
        "ristorante" => new JsonNode[] { Q("occasione", "È un'occasione speciale?", "select", new[] { "Compleanno", "Anniversario", "Cena di lavoro", "Altro" }), Q("seggioloni", "Seggioloni per bambini, quanti", "number"), Q("spazio", "Ci serve spazio per un passeggino o una sedia a rotelle"), Q("cane", "Veniamo con un cane") },
        "parrucchiere" or "centro_estetico" => new JsonNode[] { First },
        "spa" => new JsonNode[] { First, Q("pressione", "Pressione del massaggio", "select", new[] { "Leggera", "Media", "Forte" }) },
        "tattoo" => new JsonNode[] { Q("zona", "Zona del corpo", "text"), Q("dimensione", "Dimensione indicativa", "select", new[] { "Piccolo, fino a 5 cm", "Medio, fino a 15 cm", "Grande, oltre 15 cm", "Copertura di un tatuaggio esistente" }), Q("stile", "Colore o bianco e nero", "select", new[] { "Bianco e nero", "Colore", "Da decidere insieme" }), Q("maggiorenne", "Sono maggiorenne. Se hai meno di 18 anni chiamaci: serve il genitore", required: true) },
        "medico" => new JsonNode[] { Q("per_chi", "Per chi è la visita", "select", new[] { "Per me", "Per un familiare" }), Q("prima_visita", "È la prima visita da questo professionista") },
        "consulenza" => new JsonNode[] { Q("modalita", "Come preferisci incontrarci", "select", new[] { "In studio", "Videochiamata", "Telefono" }), Q("argomento", "Di cosa vuoi parlare", "text") },
        "autoscuola" => new JsonNode[] { Q("tipo_guida", "Tipo di guida", "select", Driving), Q("foglio_rosa", "Ho il foglio rosa") },
        "campo" => new JsonNode[] { Q("attrezzatura", "Mi serve attrezzatura a noleggio, per esempio racchette o pallone") },
        "corso" => new JsonNode[] { Q("livello", "Il tuo livello", "select", new[] { "Principiante", "Intermedio", "Avanzato" }), Q("prova", "È una lezione di prova") },
        "officina" => new JsonNode[] { Q("targa", "Targa", "text", required: true), Q("veicolo", "Marca e modello", "text"), Q("km", "Chilometri attuali", "number"), Q("attesa", "Aspetti o lasci il veicolo", "select", new[] { "Aspetto in officina", "Lascio il veicolo" }), Q("auto_cortesia", "Mi serve l'auto di cortesia") },
        "hotel" => new JsonNode[] { Arrival(), Q("letto", "Preferenza per il letto", "select", new[] { "Matrimoniale", "Due letti singoli", "Indifferente" }), Q("culla", "Mi serve una culla"), Q("animali", "Viaggio con un animale") },
        "casa_vacanze" => new JsonNode[] { Arrival(true), Q("culla", "Mi serve una culla"), Q("animali", "Viaggio con un animale") },
        "noleggio" => new JsonNode[] { Q("altezza", "Altezza di chi pedala in cm, una per persona: per esempio 175, 160", "text"), Q("casco", "Mi serve il casco"), Q("seggiolino", "Mi serve il seggiolino per bambini") },
        "evento" => new JsonNode[] { Access },
        "tour" => new JsonNode[] { Q("lingua", "Lingua della visita", "select", new[] { "Italiano", "Inglese", "Francese", "Tedesco", "Spagnolo" }), Q("bambini", "Età dei bambini, se ce ne sono", "text"), Access },
        _ => Array.Empty<JsonNode>(),
    });

    public static readonly string[] Allergens = { "Glutine", "Lattosio", "Latte e derivati", "Uova", "Frutta a guscio", "Arachidi", "Pesce", "Crostacei", "Molluschi", "Soia", "Sedano", "Senape", "Sesamo", "Solfiti", "Lupini" };
    static readonly HashSet<string> FoodTypes = new() { "ristorante", "hotel", "casa_vacanze", "evento", "tour" };
    public static bool ServesFood(string businessType) => FoodTypes.Contains(businessType ?? "");

    public sealed record Record(string Title, string One, string What, string Hint, string[] Suggest);
    static readonly Dictionary<string, Record> Records = new()
    {
        ["parrucchiere"] = new("Trattamenti", "trattamento", "Trattamento", "Per esempio: colore 6.3 con ossigeno 20 vol, posa 35 minuti, shampoo antigiallo", new[] { "Test di tollerabilità" }),
        ["centro_estetico"] = new("Trattamenti", "trattamento", "Trattamento", "Per esempio: semipermanente rosso 112 su base rinforzante, pelle sensibile: usato il lenitivo", new[] { "Test di tollerabilità" }),
        ["spa"] = new("Trattamenti", "trattamento", "Trattamento", "Per esempio: olio di mandorle, pressione media, insistere sulla cervicale", Array.Empty<string>()),
        ["tattoo"] = new("Sedute", "seduta", "Lavoro", "Per esempio: avambraccio sinistro, linee e ombre, aghi 7RL, inchiostri e lotti usati, prossima seduta tra 4 settimane", new[] { "Consulenza", "Seduta", "Ritocco" }),
        ["medico"] = new("Visite e sedute", "visita o seduta", "Prestazione", "Per esempio: seduta 3 di 10, esercizi consegnati, prossimo controllo tra un mese", new[] { "Prima visita", "Visita di controllo" }),
        ["consulenza"] = new("Incontri", "incontro", "Argomento", "Per esempio: cosa avete visto, documenti da preparare per la prossima volta", Array.Empty<string>()),
        ["autoscuola"] = new("Lezioni svolte", "lezione", "Tipo di guida", "Per esempio: rotatorie e parcheggio in parallelo, da rivedere le precedenze", Driving),
        ["officina"] = new("Interventi", "intervento", "Intervento", "Per esempio: tagliando a 60.000 km, olio 5W30 e filtri, pastiglie anteriori al 30%", new[] { "Tagliando", "Cambio gomme", "Revisione" }),
    };
    static readonly Record Generic = new("Servizi svolti", "servizio svolto", "Servizio", "Cosa hai fatto, i prodotti usati, cosa ricordare per la prossima volta", Array.Empty<string>());
    /// <summary>The business's client record, or null where there is none; a custom business by appointment gets the generic one.</summary>
    public static Record RecordOf(string businessType, bool hasResourceServices) =>
        Records.TryGetValue(businessType ?? "", out var r) ? r : string.IsNullOrEmpty(businessType) && hasResourceServices ? Generic : null;

    static readonly Dictionary<string, (string One, string Many, string Help)> Units = new()
    {
        ["parrucchiere"] = ("Operatore", "Operatori", "Chi lavora nel salone: ogni operatore ha la sua agenda e due clienti non finiscono mai sulla stessa persona alla stessa ora"),
        ["medico"] = ("Professionista", "Professionisti", "Medici e terapisti: ognuno ha la sua agenda e il paziente può scegliere da chi andare"),
        ["consulenza"] = ("Consulente", "Consulenti", "Chi riceve gli appuntamenti: ognuno ha la sua agenda"),
        ["centro_estetico"] = ("Operatrice", "Operatrici", "Chi fa i trattamenti: ognuna ha la sua agenda e la cliente può sceglierla"),
        ["spa"] = ("Cabina o operatore", "Cabine e operatori", "Le cabine e chi fa i massaggi: ognuno ha la sua agenda"),
        ["tattoo"] = ("Artista", "Artisti", "Chi tatua: ognuno ha la sua agenda e il cliente può sceglierlo"),
        ["autoscuola"] = ("Istruttore", "Istruttori", "Chi fa lezione: ognuno ha la sua agenda"),
        ["campo"] = ("Campo o sala", "Campi e sale", "I campi o le sale che si prenotano: ognuno ha il suo calendario"),
        ["hotel"] = ("Camera", "Camere", "Le camere che si prenotano, con quanti ospiti ci stanno, descrizione e foto"),
        ["casa_vacanze"] = ("Alloggio", "Alloggi", "Gli appartamenti o le case che si prenotano, con quanti ospiti ci stanno"),
        ["noleggio"] = ("Mezzo", "Mezzi", "Bici, auto o attrezzature che si noleggiano, una per una"),
        ["officina"] = ("Ponte", "Ponti", "I ponti o le postazioni dell'officina: ognuno tiene un veicolo alla volta, qualunque sia l'intervento"),
    };
    public static (string One, string Many, string Help) UnitsOf(string businessType) =>
        Units.TryGetValue(businessType ?? "", out var u) ? u : ("Operatore o postazione", "Operatori e postazioni", "Chi o cosa si prenota uno alla volta, con la sua agenda: un operatore, una cabina, una camera, un campo");
    public static string ServiceWord(string businessType) => businessType switch { "parrucchiere" or "centro_estetico" or "spa" => "Trattamento", "medico" => "Visita", "tattoo" => "Seduta", _ => "Servizio" };
    static readonly Dictionary<string, string> NoteHints = new()
    {
        ["ristorante"] = "tavolo vicino alla finestra, seggiolone, una torta", ["parrucchiere"] = "colore come l'ultima volta, capelli molto lunghi", ["centro_estetico"] = "pelle sensibile, come l'ultima volta",
        ["spa"] = "preferisco un'operatrice, pelle sensibile", ["medico"] = "prima visita, porto gli esami", ["tattoo"] = "disegno già pronto, copertura", ["autoscuola"] = "prima guida, cambio automatico",
        ["campo"] = "serve il noleggio delle racchette", ["corso"] = "principiante, prima lezione di prova", ["officina"] = "rumore in frenata, aspetto in sede", ["hotel"] = "arrivo dopo le 22, culla in camera",
        ["casa_vacanze"] = "arrivo in serata, con un cane", ["noleggio"] = "ritiro la mattina presto", ["evento"] = "posti vicini, uno con carrozzina", ["tour"] = "una persona con mobilità ridotta",
    };
    public static string NoteHint(string businessType) => NoteHints.TryGetValue(businessType ?? "", out var h) ? h : "un'esigenza particolare, un orario da evitare";
}
