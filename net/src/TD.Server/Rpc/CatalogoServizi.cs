using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>forms.ServiceForm: what the client books. The administrator of the program chooses the kind of booking and
/// sees every field with the kinds that use it; the business sees only the fields of its kind.</summary>
public static partial class Catalogo
{
    static readonly string[] SERVICE_FIELDS = { "name", "name_en", "location", "kind", "description", "description_en", "price", "capacity", "online_capacity", "max_party", "turnover", "last_arrival", "duration", "party_durations", "interval",
        "pace_covers", "pace_bookings", "pace_desk", "checkin", "checkout", "min_days", "max_days", "buffer_before", "buffer_after", "lead_minutes", "horizon_days", "cancel_cutoff_minutes", "request_all", "request_above", "request_allergies",
        "pause_start", "pause_minutes", "client_picks", "instructions", "remote_url", "recall_days", "test_days", "resource_label", "thanks_url", "active", "resources" };
    static readonly Dictionary<string, string> SERVICE_LABELS = new()
    {
        ["name_en"] = "Nome in inglese (facoltativo)", ["description_en"] = "Descrizione in inglese (facoltativa)", ["thanks_url"] = "Pagina di ringraziamento dopo la prenotazione", ["pause_start"] = "Lavoro prima della posa (minuti)",
        ["pause_minutes"] = "Tempo di posa (minuti)", ["client_picks"] = "Il cliente sceglie con chi",
    };
    static readonly Dictionary<string, string> SERVICE_HELP = new()
    {
        ["name"] = "Per esempio Cena, Taglio donna, Camera doppia", ["thanks_url"] = "Facoltativa, per esempio https://www.ilvostrosito.it/grazie: dopo la prenotazione online il cliente va lì. Vuota: quella dell'attività, se c'è",
        ["pause_start"] = "Per esempio 30 per l'applicazione del colore. Dopo la posa viene la finitura, fino alla fine della durata",
        ["pause_minutes"] = "0 = nessuna posa. Durante la posa il cliente resta, l'operatore è libero e si può prenotare con lui un servizio breve",
        ["client_picks"] = "Spento: online il cliente sceglie solo l'orario e il programma assegna la persona o il posto libero, per esempio il ponte dell'officina",
    };
    static readonly Dictionary<string, (int, int)> BOUNDS = new()
    {
        ["pause_start"] = (0, 1440), ["pause_minutes"] = (0, 1440), ["recall_days"] = (0, 1100), ["test_days"] = (0, 1100), ["duration"] = (5, 1440), ["interval"] = (5, 240), ["capacity"] = (1, 10000), ["max_party"] = (1, 1000), ["buffer_before"] = (0, 1440),
        ["buffer_after"] = (0, 1440), ["lead_minutes"] = (0, 525600), ["horizon_days"] = (1, 730), ["max_days"] = (1, 366), ["min_days"] = (0, 366), ["last_arrival"] = (0, 1440), ["cancel_cutoff_minutes"] = (0, 525600), ["request_above"] = (0, 1000),
        ["online_capacity"] = (0, 10000), ["pace_covers"] = (0, 10000), ["pace_bookings"] = (0, 1000),
    };
    static readonly string[] OPTIONAL_INTS = { "request_above", "online_capacity", "pace_covers", "pace_bookings", "recall_days", "test_days", "pause_start", "pause_minutes" };

    static int GetInt(Service s, string k) => k switch
    {
        "capacity" => s.Capacity, "online_capacity" => s.OnlineCapacity, "max_party" => s.MaxParty, "last_arrival" => s.LastArrival, "duration" => s.Duration, "interval" => s.Interval, "pace_covers" => s.PaceCovers, "pace_bookings" => s.PaceBookings,
        "min_days" => s.MinDays, "max_days" => s.MaxDays, "buffer_before" => s.BufferBefore, "buffer_after" => s.BufferAfter, "lead_minutes" => s.LeadMinutes, "horizon_days" => s.HorizonDays, "cancel_cutoff_minutes" => s.CancelCutoffMinutes,
        "request_above" => s.RequestAbove, "pause_start" => s.PauseStart, "pause_minutes" => s.PauseMinutes, "recall_days" => s.RecallDays, "test_days" => s.TestDays, _ => throw new ArgumentException(k),
    };
    static void SetInt(Service s, string k, int v)
    {
        switch (k)
        {
            case "capacity": s.Capacity = v; break; case "online_capacity": s.OnlineCapacity = v; break; case "max_party": s.MaxParty = v; break; case "last_arrival": s.LastArrival = v; break; case "duration": s.Duration = v; break; case "interval": s.Interval = v; break;
            case "pace_covers": s.PaceCovers = v; break; case "pace_bookings": s.PaceBookings = v; break; case "min_days": s.MinDays = v; break; case "max_days": s.MaxDays = v; break; case "buffer_before": s.BufferBefore = v; break; case "buffer_after": s.BufferAfter = v; break;
            case "lead_minutes": s.LeadMinutes = v; break; case "horizon_days": s.HorizonDays = v; break; case "cancel_cutoff_minutes": s.CancelCutoffMinutes = v; break; case "request_above": s.RequestAbove = v; break; case "pause_start": s.PauseStart = v; break;
            case "pause_minutes": s.PauseMinutes = v; break; case "recall_days": s.RecallDays = v; break; case "test_days": s.TestDays = v; break; default: throw new ArgumentException(k);
        }
    }
    static bool GetBool(Service s, string k) => k switch { "turnover" => s.Turnover, "pace_desk" => s.PaceDesk, "request_all" => s.RequestAll, "request_allergies" => s.RequestAllergies, "client_picks" => s.ClientPicks, "active" => s.Active, _ => throw new ArgumentException(k) };
    static void SetBool(Service s, string k, bool v)
    {
        switch (k) { case "turnover": s.Turnover = v; break; case "pace_desk": s.PaceDesk = v; break; case "request_all": s.RequestAll = v; break; case "request_allergies": s.RequestAllergies = v; break; case "client_picks": s.ClientPicks = v; break; case "active": s.Active = v; break; default: throw new ArgumentException(k); }
    }
    static readonly string[] INTS = { "capacity", "online_capacity", "max_party", "last_arrival", "duration", "interval", "pace_covers", "pace_bookings", "min_days", "max_days", "buffer_before", "buffer_after", "lead_minutes", "horizon_days", "cancel_cutoff_minutes", "request_above", "pause_start", "pause_minutes", "recall_days", "test_days" };
    static readonly string[] BOOLS = { "turnover", "pace_desk", "request_all", "request_allergies", "client_picks", "active" };

    /// <summary>The service as the form starts: the one saved, or a new one from the type's template (presets.initial).</summary>
    static (Service S, string Preset, bool Agency) Start(Call c, Guid? id)
    {
        var x = c.C; var t = c.Tenant; var agency = c.User.IsStaff;
        if (id.HasValue) return (x.S.Get<Service>(id), "", agency);
        // the administrator may start from any template; the business always starts from its own type
        var preset = agency && Presets.All.ContainsKey(c.Str("preset")) ? c.Str("preset") : t.BusinessType ?? "";
        var s = new Service { TenantId = t.Id, Kind = Presets.All.TryGetValue(t.BusinessType ?? "", out var own) ? own.Kind : "resource" };
        if (Presets.All.TryGetValue(preset, out var p))
        {
            s.Kind = p.Kind; s.ResourceLabel = p.ResourceLabel; s.Fields = Json.Dump(Presets.Questions(preset));
            foreach (var (k, v) in p.Defaults) { if (v is int n) SetInt(s, k, n); else if (v is bool b) SetBool(s, k, b); else if (k == "checkin") s.Checkin = (TimeSpan)v; else if (k == "checkout") s.Checkout = (TimeSpan)v; }
            s.Preset = preset;
        }
        // a business whose type changed engine keeps adding services with the engine its catalog already uses
        var kinds = x.S.Query<string>("SELECT kind FROM core_service WHERE active ORDER BY name").ToList();
        if (!agency && kinds.Count > 0 && !kinds.Contains(s.Kind)) s.Kind = kinds[0];
        return (s, preset, agency);
    }

    static Special ServiceForm(Call c, Guid? id)
    {
        var x = c.C; var t = c.Tenant; var (s, _, agency) = Start(c, id); var isNew = !id.HasValue; var kind = s.Kind; var f = new List<Fld>();
        var (_, many, _) = Presets.UnitsOf(t.BusinessType); var restaurant = !agency && t.BusinessType == "ristorante" && kind is "capacity" or "shift";
        string Label(string n) => FormTexts.KIND_LABELS.TryGetValue(n, out var kl) && kl.TryGetValue(kind, out var l) ? l : SERVICE_LABELS.TryGetValue(n, out var sl) && !FormTexts.LABELS.ContainsKey(n) ? sl : FormTexts.LABELS.GetValueOrDefault(n, n);
        string Help(string n) => FormTexts.KIND_HELP.TryGetValue(n, out var kh) && kh.TryGetValue(kind, out var h) ? h : SERVICE_HELP.TryGetValue(n, out var sh) ? sh : FormTexts.HELP.GetValueOrDefault(n, "");
        foreach (var n in SERVICE_FIELDS)
        {
            var kinds = FormTexts.FIELD_KINDS.GetValueOrDefault(n);
            if (!agency && (n == "kind" || kinds != null && !kinds.Contains(kind) || restaurant && n == "turnover")) continue;
            if (n == "request_allergies" && t.AskedAllergens.Count == 0) continue;
            Fld fld = n switch
            {
                "name" => new Fld { Nome = n, Valore = s.Name, Max = 120 }, "name_en" => new Fld { Nome = n, Valore = s.NameEn, Max = 120 },
                "location" => One(x, n, "Sede", "SELECT id, name FROM core_location WHERE active ORDER BY name", isNew ? null : s.LocationId),
                "kind" => new Fld { Nome = n, Tipo = "select", Valore = kind, Scelte = Opts(FormTexts.SERVICE_KINDS), Largo = true, Aiuto = "Lo decide l'amministratore del programma: l'attività non lo vede e non lo cambia" },
                "description" => new Fld { Nome = n, Tipo = "area", Valore = s.Description, Largo = true }, "description_en" => new Fld { Nome = n, Tipo = "area", Valore = s.DescriptionEn },
                "price" => new Fld { Nome = n, Tipo = "decimal", Valore = s.Price?.ToString("0.00", It) ?? "" },
                "party_durations" => new Fld { Nome = n, Valore = string.Join(", ", Json.Arr(s.PartyDurations).OfType<JsonArray>().Select(a => $"{a[0]}:{a[1]}")), Max = 200 },
                "checkin" => new Fld { Nome = n, Tipo = "time", Valore = Hm(s.Checkin) }, "checkout" => new Fld { Nome = n, Tipo = "time", Valore = Hm(s.Checkout) },
                "instructions" => new Fld { Nome = n, Tipo = "area", Valore = s.Instructions, Max = 2000 }, "remote_url" => new Fld { Nome = n, Valore = s.RemoteUrl, Max = 500 }, "resource_label" => new Fld { Nome = n, Valore = s.ResourceLabel, Max = 60 },
                "thanks_url" => new Fld { Nome = n, Valore = s.ThanksUrl, Max = 500 },
                "resources" => new Fld
                {
                    Nome = n, Tipo = "multi", Largo = true, Etichetta = many, Scelte = x.S.Query<(Guid, string)>("SELECT id, name FROM core_resource WHERE active ORDER BY name").Select(r => (object)new { id = r.Item1.ToString(), nome = r.Item2 }),
                    Aiuto = $"Quali {many.ToLower(It)} si possono prenotare con questo servizio" + (x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_resource WHERE active AND secondary)") ? ": le voci usate insieme all'operatore (cabine, macchinari) sono richieste, ne serve una libera per ogni gruppo" : ""),
                    // with one location a new service starts with every operator ticked, instead of an error on saving
                    Valore = (isNew ? x.S.Scalar<long>("SELECT count(*) FROM core_location WHERE active") == 1 ? x.S.Query<Guid>("SELECT id FROM core_resource WHERE active") : Enumerable.Empty<Guid>() : x.S.Query<Guid>("SELECT resource_id FROM core_serviceresource WHERE service_id=@s", new { s = s.Id })).Select(g => g.ToString()).ToList(),
                },
                _ when BOOLS.Contains(n) => new Fld { Nome = n, Tipo = "check", Valore = GetBool(s, n) },
                _ => new Fld { Nome = n, Tipo = "number", Valore = GetInt(s, n).ToString() },
            };
            if (fld.Nome == "location" && fld.Tipo == "select") fld.Scelte = Rows(x, "SELECT id, name FROM core_location ORDER BY name");
            fld.Etichetta ??= Label(n); if (fld.Aiuto == "") fld.Aiuto = Help(n);
            if (agency && kinds != null)
            {
                fld.Tipi = kinds; if (FormTexts.KIND_LABELS.TryGetValue(n, out var kl)) fld.Etichette = kl; if (FormTexts.KIND_HELP.TryGetValue(n, out var kh)) fld.Aiuti = kh;
            }
            f.Add(fld);
        }
        // a restaurant chooses how its covers turn, and changes it when it wants
        if (restaurant) f.Add(new Fld { Nome = "mode", Etichetta = "Come girano i tavoli", Tipo = "select", Valore = kind == "shift" ? "fixed" : s.Turnover ? "rotation" : "service", Scelte = Opts(FormTexts.MODES),
            Aiuto = "Un turno per servizio: i coperti valgono per tutto il pranzo o tutta la cena. Turni fissi: per esempio 19:30 e 21:30, i turni si scrivono in Turni del ristorante. Rotazione: il tavolo si libera dopo la durata e si riprenota" });
        f.Add(new Fld { Nome = "private_on", Etichetta = "Riservato: si prenota solo con il suo link", Tipo = "check", Valore = s.Secret != "",
            Aiuto = s.Secret != "" ? $"Il link da mandare: {c.PublicOrigin.TrimEnd('/')}/book/{t.Slug}/?riservato={s.Secret}" : "Non compare nella pagina prenotazioni: lo prenota solo chi riceve il link" });
        if (s.Secret != "") f.Add(new Fld { Nome = "private_new", Etichetta = "Cambia il link riservato", Tipo = "check", Valore = false, Aiuto = "Il link di prima smette di funzionare" });
        f.AddRange(Limits(s.Limits, "questo servizio"));
        return new(f, new { servizio = true, domande = Json.Arr(s.Fields) });
    }

    /// <summary>clean_fields: the questions of the booking form, 12 at most.</summary>
    static JsonArray CleanQuestions(JsonNode raw, Posted p)
    {
        var schema = raw as JsonArray ?? new JsonArray(); var keys = new HashSet<string>(); var outp = new JsonArray();
        void Bad(string text) => p.Error("fields", text);
        if (schema.Count > 12) { Bad("Massimo 12 campi aggiuntivi."); return outp; }
        foreach (var node in schema)
        {
            if (node is not JsonObject item || !Regex.IsMatch(item.Str("key"), @"^[a-z][a-z0-9_]{0,30}\z") || !keys.Add(item.Str("key"))) { Bad("Identificativo campo non valido."); return outp; }
            var label = item["label"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : null;
            if (item.Str("type") is not ("text" or "number" or "checkbox" or "select") || label == null || label.Length is < 1 or > 80) { Bad("Tipo o etichetta non validi."); return outp; }
            if (item["required"] != null && !(item["required"] is JsonValue rv && rv.TryGetValue<bool>(out _))) { Bad("Obbligatorietà non valida."); return outp; }
            // shown only when an earlier question has that answer (sì / no for a box)
            if (item["show_if"] != null && !(item["show_if"] is JsonObject cond && cond.Str("key") != item.Str("key") && keys.Contains(cond.Str("key")) && cond["value"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs.Length is >= 1 and <= 80))
            { Bad("Condizione non valida: scegli una domanda che viene prima e la risposta."); return outp; }
            if (item.Str("type") == "select" && !(item["choices"] is JsonArray ch && ch.Count is >= 1 and <= 30 && ch.All(v => v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length is >= 1 and <= 80))) { Bad("Inserisci le opzioni del campo."); return outp; }
            outp.Add(item.DeepClone());
        }
        return outp;
    }

    static Guid SaveService(Call c, Guid? id, Posted p)
    {
        var x = c.C; var t = c.Tenant; var (s, _, agency) = Start(c, id); var isNew = !id.HasValue; var d = new Dictionary<string, int?>();
        var formKind = s.Kind; var restaurant = !agency && t.BusinessType == "ristorante" && formKind is "capacity" or "shift";
        // the fields this form has (the business does not see those of the other kinds: they stay as they are)
        bool In(string n) => agency ? p.Has(n) || BOOLS.Contains(n) && (FormTexts.FIELD_KINDS.GetValueOrDefault(n)?.Contains(p.Raw("kind")) ?? true)
            : n != "kind" && (FormTexts.FIELD_KINDS.GetValueOrDefault(n)?.Contains(formKind) ?? true) && !(restaurant && n == "turnover") && !(n == "request_allergies" && t.AskedAllergens.Count == 0);
        var name = p.Text("name", true, 120); if (name.Length > 120) p.Whole("Nome troppo lungo."); var nameEn = p.Text("name_en", false, 120);
        var loc = p.Ref("location", "core_location", true); var kind = agency ? p.Choice("kind", FormTexts.SERVICE_KINDS.Select(k => k.Item1), true, formKind) : formKind;
        var description = p.Text("description"); var descriptionEn = p.Text("description_en");
        var price = In("price") ? p.Dec("price") : s.Price; if (price is < 0 or > 100000) p.Error("price", "Prezzo tra 0 e 100.000 €.");
        foreach (var n in INTS)
        {
            if (!In(n)) continue;
            var v = p.Int(n, !OPTIONAL_INTS.Contains(n)); if (v == null && OPTIONAL_INTS.Contains(n) && !p.Errors.ContainsKey(n)) v = 0;
            if (v.HasValue && BOUNDS.TryGetValue(n, out var b) && (v < b.Item1 || v > b.Item2)) { p.Error(n, $"Valore tra {b.Item1} e {b.Item2}."); }
            d[n] = v;
        }
        int? V(string n) => d.TryGetValue(n, out var v) ? v : In(n) ? null : GetInt(s, n);
        var partyDurations = s.PartyDurations;
        if (In("party_durations"))
        {
            var outp = new List<(int, int)>(); var bad = "";
            foreach (var part in Regex.Split(p.Text("party_durations", false, 200), @"[,;\n]").Select(v => v.Trim()).Where(v => v != ""))
            {
                var m = Regex.Match(part, @"^(\d{1,4})\s*[:=]\s*(\d{1,4})\z");
                if (!m.Success) { bad = "Scrivi persone e minuti così: 2:90, 4:105."; break; }
                int n = int.Parse(m.Groups[1].Value), mins = int.Parse(m.Groups[2].Value);
                if (!(n is >= 1 and <= 1000 && mins is >= 5 and <= 1440)) { bad = "Persone da 1 a 1000, minuti da 5 a 1440."; break; }
                outp.Add((n, mins));
            }
            if (bad == "" && outp.Select(o => o.Item1).Distinct().Count() < outp.Count) bad = "Ogni numero di persone una volta sola.";
            if (bad != "") p.Error("party_durations", bad);
            else partyDurations = Json.Dump(new JsonArray(outp.OrderBy(o => o).Take(8).Select(o => (JsonNode)new JsonArray(o.Item1, o.Item2)).ToArray()));
        }
        var checkin = In("checkin") ? p.Time("checkin", true) : s.Checkin; var checkout = In("checkout") ? p.Time("checkout", true) : s.Checkout;
        string Https(string n, int max, string now)
        {
            if (!In(n)) return now;
            var v = p.Text(n, false, max);
            if (v != "" && !Uri.TryCreate(v, UriKind.Absolute, out _)) p.Error(n, "Inserisci un URL valido."); else if (v != "" && !v.StartsWith("https://")) p.Error(n, "Usa un indirizzo HTTPS.");
            return v;
        }
        var instructions = In("instructions") ? p.Text("instructions", false, 2000) : s.Instructions; var remote = Https("remote_url", 500, s.RemoteUrl); var thanks = Https("thanks_url", 500, s.ThanksUrl);
        var resourceLabel = In("resource_label") ? p.Text("resource_label", false, 60) : s.ResourceLabel;
        var questions = p.Has("fields") ? CleanQuestions(c.Obj("valori")["fields"], p) : Json.Arr(s.Fields);
        var withResources = In("resources"); var resources = withResources ? p.Refs("resources", "core_resource", "active").Select(r => x.Row<Resource>(r)).ToList() : new();
        var mode = restaurant ? p.Choice("mode", FormTexts.MODES.Select(m => m.Item1), true) : "";
        var limits = ReadLimits(p);
        // clean()
        if (V("online_capacity") > 0 && V("capacity") > 0 && V("online_capacity") > V("capacity")) p.Error("online_capacity", "Non può superare i posti.");
        // the groups above the threshold are requests, up to the most people online: above it no request can start
        if (V("request_above") > 0 && V("max_party") > 0 && V("request_above") >= V("max_party")) p.Error("request_above", "Deve essere meno delle persone massime per prenotazione: sopra quel numero online non si prenota.");
        if (kind == "resource" && withResources && resources.Count == 0) p.Error("resources", "Seleziona almeno una voce: se l'elenco è vuoto, creala prima nella pagina accanto a Servizi.");
        else if (kind == "resource" && resources.Count > 0 && resources.All(r => r.Secondary)) p.Error("resources", "Serve almeno una persona: cabine e macchinari si prenotano insieme a lei.");
        if (kind == "resource" && V("pause_minutes") > 0 && !(0 < (V("pause_start") ?? 0) && V("pause_start") + V("pause_minutes") < (V("duration") ?? 0))) p.Error("pause_minutes", "La posa sta dentro la durata: dopo un po' di lavoro e prima della finitura.");
        if (kind == "day" && V("min_days") > 0 && V("max_days") > 0 && V("min_days") > V("max_days")) p.Error("min_days", "Le notti minime superano le massime.");
        if (kind == "day" && V("min_days") == 0 && checkin.HasValue && checkout.HasValue && checkout <= checkin) p.Error("min_days", "Con 0 notti la riconsegna è lo stesso giorno: l'ora di partenza deve venire dopo quella di arrivo.");
        if (resources.Any(r => r.LocationId != loc)) p.Error("resources", "Le risorse devono appartenere alla sede del servizio.");
        p.Check();
        // save()
        (s.Name, s.NameEn, s.LocationId, s.Kind, s.Description, s.DescriptionEn, s.Price, s.PartyDurations, s.Checkin, s.Checkout, s.Instructions, s.RemoteUrl, s.ThanksUrl, s.ResourceLabel, s.Fields, s.Limits) =
            (name, nameEn, loc.Value, kind, description, descriptionEn, price, partyDurations, checkin.Value, checkout.Value, instructions, remote, thanks, resourceLabel, Json.Dump(questions), limits);
        foreach (var (n, v) in d) if (v.HasValue) SetInt(s, n, v.Value);
        foreach (var n in BOOLS) if (In(n)) SetBool(s, n, p.Bool(n));
        if (mode != "") (s.Kind, s.Turnover) = mode switch { "fixed" => ("shift", false), "rotation" => ("capacity", true), _ => ("capacity", false) };
        if (!p.Bool("private_on")) s.Secret = ""; else if (s.Secret == "" || p.Bool("private_new")) s.Secret = Bookings.TokenUrlSafe(18);
        if (isNew) x.S.Insert(s); else { x.S.Update(s); x.Forget<Service>(s.Id); }
        if (withResources)
        {
            // an operator ticked again keeps his own duration and price for this service; day: the named units
            var chosen = s.Kind is "resource" or "day" ? resources.Select(r => r.Id).ToArray() : Array.Empty<Guid>();
            x.S.Execute("DELETE FROM core_serviceresource WHERE service_id=@s AND NOT (resource_id = ANY(@r))", new { s = s.Id, r = chosen });
            foreach (var r in chosen)
                if (!x.S.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM core_serviceresource WHERE service_id=@s AND resource_id=@r)", new { s = s.Id, r }))
                    x.S.Insert(new Serviceresource { TenantId = t.Id, ServiceId = s.Id, ResourceId = r });
        }
        return s.Id;
    }
}
