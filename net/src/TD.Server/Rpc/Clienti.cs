using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using TD.Core;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>Clienti (portal.contacts, contact_detail): the list with search and filters, a new client, the card with
/// every action of it, the access request and the erasure.</summary>
public static class Clienti
{
    const int PAGE = 50, BOOKINGS = 100, ENTRIES = 30;
    static readonly Dictionary<string, string> SALE_STATUS = new() { ["queued"] = "In coda", ["sending"] = "In corso", ["done"] = "Emesso", ["failed"] = "Non emesso", ["unknown"] = "Da verificare", ["void"] = "Annullato" };
    static readonly Dictionary<string, string> EVENTS = new() { ["requested"] = "Richiesto", ["confirmed"] = "Confermato", ["revoked"] = "Revocato", ["suppressed"] = "Escluso dagli invii", ["erased"] = "Dati cancellati", ["imported"] = "Importato", ["merged"] = "Schede unite" };
    public static readonly (string Id, string Label)[] CHANNELS = { ("", "Come imposta l'attività"), ("email", "Solo email"), ("sms", "SMS"), ("whatsapp", "WhatsApp") };

    [Rpc("clienti.elenco", Perm = "contacts.read")]
    public static object List(Call c)
    {
        var x = c.C; var cl = new Clients(x);
        var q = c.Str("q").Trim(); var tag = c.Str("tag").Trim(); var vip = c.Bool("vip"); var page = Math.Max(1, c.Int("pagina", 1));
        var where = new List<string>(); var p = new DynamicParameters();
        if (tag != "") { where.Add("tag_index LIKE @tag"); p.Add("tag", cl.TagLike(tag)); }
        if (vip) where.Add("vip");
        List<(Contact Contact, JsonObject Data)> rows; long total;
        if (q != "")
        {
            var found = cl.Search(q, 200, where.Count > 0 ? string.Join(" AND ", where) : null, p);
            total = found.Count; rows = found.Skip((page - 1) * PAGE).Take(PAGE).Select(f => (f.Contact, f.Data)).ToList();
        }
        else
        {
            var w = string.Join(" AND ", where.Prepend("email_index NOT LIKE 'erased:%'"));
            total = x.S.Scalar<long>($"SELECT count(*) FROM core_contact WHERE {w}", p);
            p.Add("lim", PAGE); p.Add("off", (page - 1) * PAGE);
            rows = x.S.Query<Contact>($"SELECT * FROM core_contact WHERE {w} ORDER BY created DESC LIMIT @lim OFFSET @off", p).Select(ct => (ct, cl.Data(ct))).ToList();
        }
        var counts = cl.VisitCounts(rows.Select(r => r.Contact.Id));
        return new
        {
            righe = rows.Select(r =>
            {
                var d = Permissions.Shown(c.Perms, r.Data); var n = counts.GetValueOrDefault(r.Contact.Id);
                return new { id = r.Contact.Id, nome = d.Str("first_name"), cognome = d.Str("last_name"), telefono = d.Str("phone"), email = d.Str("email"), vip = r.Contact.Vip, visite = n?.Visits ?? 0, ultima = n?.Last is DateTime l ? x.Cal.DayOf(l) : (DateOnly?)null, inArrivo = (n?.Next ?? 0) > 0 };
            }),
            pagina = page, pagine = Math.Max(1, (int)Math.Ceiling(total / (double)PAGE)), etichette = cl.KnownTags(), filtrato = q != "" || tag != "" || vip,
        };
    }

    [Rpc("clienti.nuovo", Perm = "contacts.read")]
    public static object New(Call c)
    {
        c.Require("booking.write");
        var cl = new Clients(c.C); var (ct, created) = cl.FindOrCreate(Clients.Clean(c.Args));
        c.C.Audit(created ? "contact.created" : "contact.found", ct.Id);
        return new { id = ct.Id, nuovo = created };
    }

    static Contact Card(Call c) => c.C.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = c.NeedId("id") }) ?? throw new KeyNotFoundException();
    static Presets.Record Spec(Call c) => Presets.RecordOf(c.Tenant.BusinessType, c.C.S.Scalar<long>("SELECT count(*) FROM core_service WHERE kind='resource'") > 0);

    [Rpc("cliente.scheda", Perm = "contacts.read")]
    public static object Detail(Call c)
    {
        var x = c.C; var cl = new Clients(x); var bk = new Bookings(x); var contact = Card(c); var now = x.Now;
        var (rows, stats) = cl.History(contact, c.Member, c.Perms);
        var raw = cl.Data(contact); var data = Permissions.Shown(c.Perms, raw); var pii = c.Can("pii.read"); var notesOk = c.Can("notes.read"); var write = c.Can("booking.write");
        var spec = notesOk ? Spec(c) : null;
        var page = Math.Max(1, c.Int("pagina", 1)); var pages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)BOOKINGS));
        var entries = spec != null ? cl.Treatments(contact) : new();
        var recorded = entries.Where(t => t.Row.BookingId.HasValue).Select(t => t.Row.BookingId.Value).ToHashSet();
        string ServiceName(Booking b) => b.Snap.Str("service") is var s && s != "" ? s : x.Row<Service>(b.ServiceId).Name;
        int Nights(Booking b) => x.Cal.DayOf(b.End).DayNumber - x.Cal.DayOf(b.Start).DayNumber;
        object Row(Booking b)
        {
            var d = Permissions.Shown(c.Perms, bk.Data(b)); var service = x.Row<Service>(b.ServiceId);
            return new
            {
                id = b.Id, inizio = bk.Iso(b.Start), fine = bk.Iso(b.End), stato = b.Status, servizio = ServiceName(b), variante = b.Snap.Str("option"), tipo = service.Kind, notti = Nights(b),
                operatore = b.Snap.Str("resource"), persone = b.Party, maxPersone = service.MaxParty, sede = b.Snap.Str("location"), allergie = d["allergies"]?.DeepClone(), note = d.Str("notes"), noteInterne = d.Str("internal_notes"),
                prezzo = b.Snap.Str("price"), totale = b.Snap.Str("total"), sconto = b.Snap.Str("discount"), registrata = recorded.Contains(b.Id), registrabile = b.Start <= now && Bookings.ACCEPTED.Contains(b.Status),
            };
        }
        var upcoming = rows.Where(b => b.Start > now && Bookings.MOVABLE.Contains(b.Status)).OrderBy(b => b.Start).Take(3).ToList();
        var late = 0;
        foreach (var (status, start, snap) in x.S.Query<(string, DateTime, string)>("SELECT status, start, snapshot::text FROM core_booking WHERE contact_id=@c AND status='cancelled'", new { c = contact.Id }))
            if (Agenda.Late(status, start, Json.Obj(snap))) late++;
        var kinds = x.S.Query<string>("SELECT DISTINCT kind FROM core_service WHERE active").ToList();
        var tp = Math.Max(1, c.Int("tp", 1)); var tpages = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)ENTRIES));
        JsonObject prefill = null;
        if (spec != null && c.Id("da") is Guid from && rows.FirstOrDefault(b => b.Id == from) is Booking fb) prefill = cl.FromBooking(fb);
        var today = x.Today;
        var owed = x.S.Query<Sale>("SELECT * FROM core_sale WHERE contact_id=@c AND kind='sale' AND status='done' AND due>0 AND settled IS NULL ORDER BY created", new { c = contact.Id }).ToList();
        var fiscalKinds = Clients.FISCAL_KINDS.ToDictionary(k => k.Id, k => k.Label);
        return new
        {
            id = contact.Id, dati = data, grezzi = pii ? raw : new JsonObject(), creato = bk.Iso(contact.Created), vip = contact.Vip, bloccato = contact.OnlineBlocked, consenso = contact.Consent,
            avvisi = contact.Notices, canale = contact.Channel, canali = CHANNELS.Select(k => new { id = k.Id, nome = k.Label }), testi = Json.Obj(c.Tenant.Texts).Count > 0,
            scontoTipo = contact.DiscountKind, scontoValore = Prices.Normal(contact.Discount), sconto = Prices.DiscountLabel(contact),
            numeri = new
            {
                visite = stats.Visits, ultima = stats.Last.HasValue ? bk.Iso(stats.Last.Value) : null, prossima = stats.Next == null ? null : new { inizio = bk.Iso(stats.Next.Start), servizio = ServiceName(stats.Next) },
                disdette = stats.Cancelled, assenze = stats.NoShow, tardive = late, servizio = stats.Service?.Name, servizioN = stats.Service?.N ?? 0, operatore = stats.Staff?.Name, totale = stats.Total, speso = stats.Spent.HasValue ? Prices.Str(stats.Spent.Value) : null,
            },
            prezzi = x.S.Scalar<long>("SELECT count(*) FROM core_service WHERE kind='resource'") > 0, parolaVisite = kinds.Count == 1 && kinds[0] == "day" ? "Soggiorni" : "Visite",
            whatsapp = ClientMails.WhatsApp(x, data.Str("phone"), upcoming.FirstOrDefault()),
            note = notesOk ? raw.Str("staff_notes") : null, allergie = notesOk ? rows.SelectMany(b => (bk.Data(b)["allergies"] as JsonArray)?.Select(a => a?.ToString() ?? "") ?? Enumerable.Empty<string>()).Distinct().ToList() : new List<string>(),
            inProgramma = upcoming.Select(Row), prenotazioni = rows.Skip((page - 1) * BOOKINGS).Take(BOOKINGS).Select(Row), pagina = page, pagine = pages,
            scheda = spec == null ? null : new
            {
                titolo = spec.Title, uno = spec.One, cosa = spec.What, suggerimento = spec.Hint, voci = entries.Count, pagina = tp, pagine = tpages, precompilata = prefill, oggi = today,
                cose = x.S.Query<string>("SELECT name FROM core_service WHERE active ORDER BY name").Concat(spec.Suggest).Distinct(), persone = x.S.Query<string>("SELECT name FROM core_resource WHERE active ORDER BY name"),
                righe = entries.Skip((tp - 1) * ENTRIES).Take(ENTRIES).Select(t => new { id = t.Row.Id, data = DateOnly.FromDateTime(t.Row.Date), servizio = t.Info.Str("service"), operatore = t.Info.Str("staff"), dettagli = t.Info.Str("details"), tipo = t.Info.Str("kind"), prenotazione = t.Row.BookingId }),
            },
            attese = x.S.Query<Waitentry>("SELECT * FROM core_waitentry WHERE contact_id=@c AND status='waiting' ORDER BY first", new { c = contact.Id }).Select(w => new { servizio = x.Row<Service>(w.ServiceId)?.Name, dal = DateOnly.FromDateTime(w.First), al = DateOnly.FromDateTime(w.Last) }),
            unisci = c.Can("privacy.manage") && pii, test = x.S.Query<string>("SELECT name FROM core_service WHERE active AND test_days>0"), titoliConsenso = Clients.CONSENT_TITLES,
            categorie = c.Tenant.Categories.Select(k => k.Str("name")),
            tipiFiscali = Clients.FISCAL_KINDS.Select(k => new { id = k.Id, nome = k.Label }),
            fiscali = Clients.FISCAL.Where(f => data.Str(f.Key) != "").Select(f => new { nome = f.Label, valore = f.Key == "fiscal_kind" ? fiscalKinds.GetValueOrDefault(data.Str(f.Key), data.Str(f.Key)) : data.Str(f.Key) }),
            scontrini = x.S.Query<Sale>("SELECT * FROM core_sale WHERE contact_id=@c AND kind='sale' ORDER BY created DESC LIMIT 20", new { c = contact.Id }).Select(s => new
            {
                id = s.Id, totale = Prices.Str(s.Total), stato = s.Status, statoNome = SALE_STATUS.GetValueOrDefault(s.Status, s.Status), creato = bk.Iso(s.Created), numero = s.Number, documento = int.TryParse(s.Z, out var zz) && int.TryParse(s.Number, out var nn) ? $"{zz:0000}-{nn:0000}" : s.Z != "" && s.Number != "" ? $"{s.Z}-{s.Number}" : s.Number, matricola = s.Serial, email = s.Mailed.HasValue,
                daPagare = Prices.Str(s.Due), pagato = s.Settled.HasValue ? bk.Iso(s.Settled.Value) : null, sede = s.LocationId,
            }),
            deve = owed.Select(s => new { importo = Prices.Str(s.Due), stampato = s.Printed.HasValue ? bk.Iso(s.Printed.Value) : null, sede = s.LocationId }), deveTotale = Prices.Str(owed.Sum(s => s.Due)),
            giftcard = x.S.Query<Giftcard>("SELECT * FROM core_giftcard WHERE contact_id=@c AND status='active' ORDER BY created DESC", new { c = contact.Id }).Select(g => new
            { codice = g.Code, credito = Prices.Str(g.Balance), scade = g.Expires.HasValue ? DateOnly.FromDateTime(g.Expires.Value) : (DateOnly?)null, scaduta = g.Expires.HasValue && DateOnly.FromDateTime(g.Expires.Value) < today }),
            preventivi = write ? x.S.Query<Quote>("SELECT * FROM core_quote WHERE contact_id=@c ORDER BY year DESC, number DESC LIMIT 10", new { c = contact.Id }).Select(q => (object)new { id = q.Id, numero = $"{q.Year}-{q.Number:0000}", totale = Prices.Str(q.Total) }) : Array.Empty<object>(),
            eventi = new Privacy(x).History(contact).Select(e => new { tipo = EVENTS.GetValueOrDefault(e.Kind, e.Kind), quando = bk.Iso(e.At), canale = e.Channel, testo = e.TextHash.Length > 10 ? e.TextHash[..10] : e.TextHash }),
        };
    }

    /// <summary>The POST actions of the card, each with its own permission as the web has them. Returns the line to show.</summary>
    [Rpc("cliente.azione", Perm = "contacts.read")]
    public static object Action(Call c)
    {
        var x = c.C; var cl = new Clients(x); var contact = Card(c); var a = c.Str("azione"); string text;
        switch (a)
        {
            case "treatment" or "treatment_remove":
                if (Spec(c) == null) throw new KeyNotFoundException();
                c.Require("booking.write"); c.Require("notes.read");
                Treatment item = null;
                if (c.Id("treatment") is Guid tid) item = x.S.One<Treatment>("SELECT * FROM core_treatment WHERE contact_id=@c AND id=@id", new { c = contact.Id, id = tid }) ?? throw new BookingException("Voce non trovata: forse è già stata eliminata.");
                if (a == "treatment_remove")
                {
                    if (item == null) throw new BookingException("Voce non trovata.");
                    x.S.Delete<Treatment>(item.Id); x.Audit("treatment.removed", contact.Id); text = "Voce eliminata dalla scheda."; break;
                }
                var d = Clients.CleanTreatment(c.Args, x.Today); Guid? booking = null;
                if (item == null && c.Id("booking") is Guid bid)
                {
                    // the booking it comes from: only one of this client, and only one this user may see
                    var (vis, vargs) = Permissions.VisibleBookings(c.Member, c.Perms, "b"); var p = new DynamicParameters(vargs); p.Add("id", bid); p.Add("cid", contact.Id);
                    booking = x.S.One<Guid?>($"SELECT b.id FROM core_booking b WHERE b.id=@id AND b.contact_id=@cid AND ({vis})", p);
                }
                var t = cl.SaveTreatment(contact, d, item, item == null ? booking : null);
                x.Audit("treatment.saved", contact.Id, new { entry = t.Id.ToString(), edited = item != null });
                text = item != null ? "Modifica salvata." : "Salvato nella scheda del cliente."; break;
            case "discount":
                c.Require("catalog.write");
                var changed = cl.SetDiscount(contact, c.Str("discount_kind"), c.Str("discount"));
                x.Audit("contact.discount", contact.Id, new { kind = contact.DiscountKind, value = contact.Discount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), bookings = changed });
                text = (contact.DiscountKind != "" ? "Sconto salvato." : "Sconto tolto.") + (changed == 1 ? " Aggiornata 1 prenotazione in programma." : changed > 0 ? $" Aggiornate {changed} prenotazioni in programma." : ""); break;
            case "notices":
                c.Require("booking.write");
                contact.Notices = c.Bool("notices"); x.S.Update(contact, "notices"); x.Audit("contact.notices", contact.Id, new { on = contact.Notices });
                text = contact.Notices ? "Il cliente riceve le email delle sue prenotazioni." : "Il cliente non riceve più le email delle sue prenotazioni."; break;
            case "channel":
                c.Require("booking.write");
                if (!CHANNELS.Any(k => k.Id == c.Str("channel"))) return new { testo = "" };
                contact.Channel = c.Str("channel"); x.S.Update(contact, "channel"); x.Audit("contact.channel", contact.Id, new { channel = contact.Channel });
                text = "Canale dei promemoria salvato."; break;
            case "sign":
                c.Require("booking.write"); c.Require("pii.read");
                cl.SignConsent(contact, c.Args); x.Audit("contact.signed", contact.Id); text = "Consenso firmato e salvato nella scheda."; break;
            case "profile":
                c.Require("booking.write"); c.Require("pii.read");
                var pr = Clients.CleanProfile(c.Args, x.Today, c.Tenant.Categories); cl.SaveProfile(contact, pr);
                x.Audit("contact.profile", contact.Id, new { vip = pr.Vip, online_blocked = pr.OnlineBlocked, tags = pr.Tags.Count, birthday = pr.Birthday != "" });
                text = "Scheda aggiornata."; break;
            case "fiscal":
                c.Require("booking.write"); c.Require("pii.read");
                cl.SaveFiscal(contact, Clients.CleanFiscal(c.Args)); x.Audit("contact.fiscal", contact.Id); text = "Dati fiscali salvati."; break;
            case "notes":
                c.Require("booking.write"); c.Require("notes.read");
                cl.Update(contact, notes: c.Str("staff_notes")); x.Audit("contact.notes", contact.Id); text = "Note salvate."; break;
            case "card":
                c.Require("booking.write"); c.Require("pii.read");
                cl.Update(contact, c.Args); x.Audit("contact.card", contact.Id); text = "Dati del cliente aggiornati."; break;
            default: throw new KeyNotFoundException();
        }
        return new { testo = text };
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The access request: everything kept about the client, as a file to save.</summary>
    [Rpc("cliente.esporta", Perm = "privacy.manage")]
    public static object Export(Call c)
    {
        var contact = Card(c); c.C.Audit("privacy.exported", contact.Id);
        return new { nome = $"dati-cliente-{contact.Id.ToString()[..8]}.json", testo = new Privacy(c.C).Export(contact).ToJsonString(Pretty) };
    }

    [Rpc("cliente.cancella", Perm = "privacy.manage", Fresh = true)]
    public static object Erase(Call c)
    {
        var contact = Card(c);
        if (c.Str("conferma") != "CANCELLA") throw new BookingException("Scrivi CANCELLA per confermare.");
        new Privacy(c.C).Erase(contact);
        return new { testo = "Dati del cliente cancellati. Lo storico dei consensi resta, senza dati personali." };
    }

    // ---------- Unisci due schede (portal.contact_merge)
    static void MergeRights(Call c) { foreach (var p in new[] { "pii.read", "booking.write", "privacy.manage" }) c.Require(p); }

    /// <summary>The page before the merge: the other card searched, or the two cards side by side, field by field.</summary>
    [Rpc("cliente.unisci.dati", Perm = "contacts.read")]
    public static object MergeForm(Call c)
    {
        MergeRights(c);
        var x = c.C; var cl = new Clients(x); var contact = Card(c); var a = cl.Data(contact);
        var other = c.Id("con") is Guid oid && oid != contact.Id ? x.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = oid }) : null;
        long Count(Contact k) => x.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE contact_id=@c", new { c = k.Id });
        object Side(Contact k) => new { id = k.Id, creato = x.Cal.DayOf(k.Created), prenotazioni = Count(k), consenso = k.Consent };
        if (other == null)
            return new
            {
                nome = a.Str("first_name"), cognome = a.Str("last_name"),
                trovati = c.Str("q") == "" ? null : cl.Search(c.Str("q"), 10).Where(f => f.Contact.Id != contact.Id).Select(f => new { id = f.Contact.Id, nome = f.Data.Str("first_name"), cognome = f.Data.Str("last_name"), telefono = f.Data.Str("phone"), email = f.Data.Str("email"), creato = x.Cal.DayOf(f.Contact.Created) }),
            };
        var b = cl.Data(other);
        return new
        {
            nome = a.Str("first_name"), cognome = a.Str("last_name"), questa = Side(contact), altra = Side(other), scheda = Spec(c) != null,
            righe = Clients.MERGE_FIELDS.Where(f => f.Key is "first_name" or "last_name" or "email" or "phone" or "birthday" || a.Str(f.Key) != "" || b.Str(f.Key) != "")  // fiscal data only when a card has them
                .Select(f => new { chiave = f.Key, nome = f.Label, mio = a.Str(f.Key), suo = b.Str(f.Key), scelta = a.Str(f.Key) != "" || b.Str(f.Key) == "" ? "this" : "other" }),
        };
    }

    [Rpc("cliente.unisci", Perm = "contacts.read", Fresh = true)]
    public static object Merge(Call c)
    {
        MergeRights(c);
        var contact = Card(c); var other = c.NeedId("con");
        if (c.Str("conferma") != "UNISCI") throw new BookingException("Scrivi UNISCI per confermare.");
        var mainIsOther = c.Str("main") == "other"; var keepArgs = c.Obj("keep");
        // the choices are made on the page as "this card" and "the other one": turned to main and second here
        var keep = Clients.MERGE_FIELDS.ToDictionary(f => f.Key, f => (keepArgs.Str(f.Key) == "other") == !mainIsOther ? "other" : "main");
        var main = new Clients(c.C).Merge(mainIsOther ? other : contact.Id, mainIsOther ? contact.Id : other, keep);
        return new { id = main.Id, testo = "Schede unite: prenotazioni, scheda, lista d'attesa e consensi sono ora qui." };
    }
}
