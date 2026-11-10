using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TD.Core.Data;

namespace TD.Core.Domain;

/// <summary>core/booking.py, the half that writes: the client's data, where and when, create, move, status, series, visits.</summary>
public sealed partial class Bookings
{
    static JsonObject With(JsonObject d, params (string K, JsonNode V)[] kv)
    {
        var o = (JsonObject)d.DeepClone();
        foreach (var (k, v) in kv) o[k] = v?.DeepClone();
        return o;
    }
    static bool IsTrue(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static bool IsFalse(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && !b;

    // ---------- the client's data
    public static string ShownValue(JsonNode answer) =>
        answer is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? "sì" : "no") : answer?.ToString() ?? "";

    public JsonObject Personal(JsonObject data, Service service, bool staff = false, bool walkin = false)
    {
        JsonObject result;
        if (staff)
        {
            var src = walkin ? With(data, ("first_name", Json.Truthy(data["first_name"]) ? data["first_name"] : "Cliente di passaggio")) : data;
            result = Clients.Clean(src, requireContact: !walkin);
        }
        else
        {
            result = new JsonObject();
            foreach (var key in new[] { "first_name", "last_name", "email", "phone" })
            {
                var node = data[key];
                if (node != null && !(node is JsonValue jv && jv.TryGetValue<string>(out _))) throw new BookingException("Recapito non valido: " + key);
                var val = (node?.GetValue<string>() ?? "").Trim();
                if (val == "" || val.Length > (key == "email" ? 254 : 100) || val.Any(ch => ch < 32)) throw new BookingException("Compila correttamente nome, cognome, email e telefono.");
                result[key] = val;
            }
            if (!Clients.ValidEmail(result.Str("email"))) throw new BookingException("Email non valida.");
            result["email"] = Crypto.CaseFold(result.Str("email"));
            result["phone"] = Clients.NormalizePhone(result.Str("phone"));
            if (result.Str("phone") == "") throw new BookingException("Numero di telefono non valido, per esempio 333 123 4567.");
        }
        foreach (var (k, v) in Extras(data, service)) result[k] = v?.DeepClone();
        return result;
    }

    /// <summary>Notes and the service's own questions, the same for online and staff bookings.</summary>
    public JsonObject Extras(JsonObject data, Service service)
    {
        var result = new JsonObject();
        if (data["notes"] != null && !(data["notes"] is JsonValue nv && nv.TryGetValue<string>(out _))) throw new BookingException("Note non valide.");
        var notes = (data["notes"]?.GetValue<string>() ?? "").Trim();
        if (notes.Length > 4000) throw new BookingException("Note troppo lunghe.");
        result["notes"] = notes;
        var asked = c.Tenant.AskedAllergens;
        var picked = asked.Count > 0 ? data["allergies"] ?? new JsonArray() : new JsonArray();
        if (!Json.Truthy(picked)) picked = new JsonArray();
        if (picked is not JsonArray pa || pa.Any(a => !(a is JsonValue av && av.TryGetValue<string>(out var s) && asked.Contains(s))))
            throw new BookingException("Intolleranze non valide: ricarica la pagina e riprova.");
        if (pa.Count > 0) result["allergies"] = new JsonArray(asked.Where(a => pa.Any(x => x.GetValue<string>() == a)).Select(x => (JsonNode)x).ToArray());
        var answers = data["answers"] ?? new JsonObject();
        if (answers is not JsonObject ans) throw new BookingException("Campi aggiuntivi non validi.");
        var outAns = new JsonObject(); result["answers"] = outAns;
        foreach (var field in service.FieldList.OfType<JsonObject>())
        {
            if (field["show_if"] is JsonObject cond && ShownValue(outAns[cond.Str("key")]) != cond.Str("value")) continue;
            var key = field.Str("key"); var type = field.Str("type"); var label = field.Str("label"); var required = Json.Truthy(field["required"]);
            JsonNode val = ans[key]?.DeepClone() ?? "";
            var empty = val is JsonValue ev && ev.TryGetValue<string>(out var es) && es == "";
            if (type == "checkbox")
            {
                if (empty || ans[key] == null && ans.ContainsKey(key)) val = false;
                if (!(val is JsonValue bv && bv.TryGetValue<bool>(out var bb))) throw new BookingException("Valore sì/no non valido: " + label);
                if (required && !bb) throw new BookingException("Compila: " + label);
            }
            else if (required && (empty || ans.ContainsKey(key) && ans[key] == null)) throw new BookingException("Compila: " + label);
            if (type == "checkbox") { }
            else if (type == "select")
            {
                if (Json.Truthy(val) && !(field["choices"] as JsonArray ?? new JsonArray()).Any(x => x?.ToString() == val.ToString())) throw new BookingException("Scelta non valida: " + label);
            }
            else if (type == "number")
            {
                if (!empty && val != null) val = Integer(val, label, 0, 100000);
            }
            else
            {
                if (!(val is JsonValue tv && tv.TryGetValue<string>(out var ts))) throw new BookingException("Testo non valido: " + label);
                if (ts.Length > 500) throw new BookingException("Campo troppo lungo.");
            }
            outAns[key] = val;
        }
        return result;
    }

    // ---------- where and when
    public sealed record Where(DateTime Start, DateTime End, DateTime Bs, DateTime Be, int Party, Option Option, Resource Resource, Occurrence Occurrence, List<string> Issues);

    public Where Bounds(Service service, JsonObject data, IEnumerable<Guid> exclude = null, bool desk = false, bool force = false, Guid? prefer = null)
    {
        var (option, resource, _) = Details(service, data["option"], data["resource"]);
        var party = Integer(data["party"] ?? 1, "Quantità", 1, desk ? 1000 : service.MaxParty);
        var now = c.Now;
        var parsed = Times.ParseAware(data["start"]?.ToString(), out var naive) ?? throw new BookingException("Data o ora non valida.");
        DateTime start;
        if (naive)
        {
            if (!force) throw new BookingException("Fuso orario mancante.");
            start = c.Cal.Local(DateOnly.FromDateTime(parsed), parsed.TimeOfDay) ?? throw new BookingException("Data o ora non valida.");
        }
        else start = parsed;
        var day = c.Cal.DayOf(start); Occurrence occurrence = null; var issues = new List<string>();
        var before = M(service.BufferBefore); var after = M(service.BufferAfter);
        int? minutes = desk && service.Kind == "resource" && Json.Truthy(data["minutes"]) ? Integer(data["minutes"], "Durata", 5, 1440) : null;
        DateTime end, bs, be;
        if (service.Kind == "day")
        {
            if (!DateOnly.TryParseExact(data["end_date"]?.ToString() ?? "", "yyyy-MM-dd", out var endDay)) throw new BookingException("Seleziona la data di partenza.");
            var nights = endDay.DayNumber - day.DayNumber;
            if (0 <= nights && nights < service.MinDays) throw new BookingException($"Durata minima: {service.MinDays} giorni.");
            if (nights > service.MaxDays) throw new BookingException($"Durata massima: {service.MaxDays} giorni.");
            var why = Stays.Restricted(service, day, endDay);
            if (why != "") throw new BookingException(why);
            var expected = c.Cal.Local(day, service.Checkin); var e = c.Cal.Local(endDay, service.Checkout);
            if (expected == null || e == null || start != expected) throw new BookingException("Ora di arrivo non valida.");
            end = e.Value;
            if (end <= start) throw new BookingException("La partenza viene prima dell'arrivo: scegli un'altra data di partenza.");
            if (desk ? day < c.Today : !(now + M(service.LeadMinutes) <= start && start <= now.AddDays(service.HorizonDays))) throw new BookingException("Data fuori dall'intervallo prenotabile.");
            var tick = TimeSpan.FromTicks(10);
            if (!c.Cal.Fits(start, start + tick, service, purpose: "arrival") || !c.Cal.Fits(end, end + tick, service, purpose: "departure") || c.Cal.BlockedStay(start, end, service))
                throw new BookingException("Arrivo, partenza o soggiorno non consentiti dal calendario.");
            bs = start - before; be = end + after;
            if (HasUnits(service))
            {
                var free = FreeUnits(service, bs, be, party, resource, exclude);
                if (free.Count == 0) throw new BookingException(resource == null ? "Nessuna unità disponibile per le date scelte." : "L'unità scelta non è disponibile per queste date.");
                resource = free[0];
            }
            else if (!CapacityAvailable(service, bs, be, party, exclude: exclude)) throw new BookingException("Disponibilità esaurita.");
        }
        else if (force || minutes.HasValue)
        {
            if (service.Kind is "event" or "shift")
            {
                var oid = AsId(data["occurrence"]);
                occurrence = oid.HasValue ? c.S.One<Occurrence>("SELECT * FROM core_occurrence WHERE id=@id AND service_id=@s AND active", new { id = oid, s = service.Id }) : null;
                if (occurrence == null) throw new BookingException(INVALID);
                start = occurrence.Start;
            }
            else if (service.Kind == "resource" && resource == null)
            {
                foreach (var r in Ranked(service, Linked(service), day, prefer))
                {
                    var e = start + M(Length(service, option, r));
                    if (c.Cal.Fits(start - before, e + after, service, r) && CapacityAvailable(service, start - before, e + after, party, r, exclude: exclude, gap: Posa(service, start, e, r))) { resource = r; break; }
                }
                if (resource == null) throw new BookingException("Nessuno è libero a quell'ora: scegli con chi.");
            }
            if (occurrence != null) end = occurrence.End;
            else if (service.Kind == "capacity" && !service.Turnover)
            {
                var w = c.Cal.Windows(day, service).FirstOrDefault(x => x.A <= start && start < x.B);
                end = w.B != default ? w.B - after : start + M(service.Duration);
            }
            else end = start + M(minutes ?? Length(service, option, resource, party));
            bs = start - before; be = end + after;
            if (start < now - M(1)) issues.Add("nel passato");
            if (!c.Cal.Fits(bs, be, service, resource)) issues.Add("fuori dall'orario di apertura");
            var block = c.Cal.Blocked(bs, be, resource?.Id, service.Id);
            if (block != null) issues.Add(block.ResourceId.HasValue ? $"nell'orario bloccato di {resource?.Name} ({ReasonLabel(block.Reason).ToLowerInvariant()})" : "in un turno chiuso");
            if (!CapacityAvailable(service, bs, be, party, resource, occurrence, exclude, blocks: false, rooms: false, gap: Posa(service, start, end, resource)))
                issues.Add(resource != null ? $"sovrapposta a un altro appuntamento di {resource.Name}" : $"oltre i posti disponibili ({(occurrence != null ? occurrence.Capacity : service.Capacity)})");
            if (Away(resource, day)) issues.Add($"{resource.Name} è fuori ufficio");
            var mins = (int)Math.Floor((end - start).TotalMinutes);
            foreach (var x in new[] { Limited(service, day, mins, exclude), resource != null ? Limited(resource, day, mins, exclude) : "" }) if (x != "") issues.Add(x);
            if (resource == null)
            {
                var t = new Tables(c, this);
                foreach (var x in new[] { Shared(service, bs, be, party, exclude), service.PaceDesk ? Paced(service, start, party, exclude) : "", t.AreaOver(t.PickArea(service, data["area"]), bs, be, party, exclude) })
                    if (x != "") issues.Add(x);
            }
        }
        else
        {
            var slots = Available(service.Id.ToString(), day, data["option"], data["resource"], party, exclude, desk: desk, prefer: prefer, area: data["area"]);
            var occ = data["occurrence"]?.ToString() ?? "";
            var match = slots.FirstOrDefault(s => s.StartUtc == start && (!Json.Truthy(data["occurrence"]) || s.Occurrence == occ))
                ?? throw new BookingException("Orario non disponibile. Aggiorna le disponibilità.");
            resource = match.Resource != "" ? c.Row<Resource>(Guid.Parse(match.Resource)) : null;
            occurrence = match.Occurrence != "" ? c.Row<Occurrence>(Guid.Parse(match.Occurrence)) : null;
            end = match.EndUtc; bs = start - before; be = end + after;
        }
        return new Where(start, end, bs, be, party, option, resource, occurrence, issues);
    }

    public static readonly Dictionary<string, string> BLOCK_REASONS = new()
    { ["break"] = "Pausa", ["training"] = "Formazione", ["meeting"] = "Riunione", ["other"] = "Altro", ["closed"] = "Turno chiuso", ["busy"] = "Occupato", ["google"] = "Occupato su Google", ["outlook"] = "Occupato su Outlook" };
    public static string ReasonLabel(string r) => BLOCK_REASONS.TryGetValue(r ?? "", out var l) ? l : r ?? "";

    /// <summary>What a forced booking or a walk-in goes beyond: without Forza a refusal, with Forza a warning until confirmed.</summary>
    public static void Overrun(List<string> issues, JsonObject data)
    {
        if (issues.Count == 0) return;
        if (!IsTrue(data["force"])) throw new BookingException("Adesso non si può: " + string.Join(", ", issues) + ". Scegli con chi o chiedi a chi può forzare.");
        if (!IsTrue(data["force_ok"])) throw new ForceWarning("Stai forzando: " + string.Join(", ", issues) + ". Se va bene, conferma.");
    }

    public bool NeedsRequest(Service service, int party, JsonObject pii, Contact contact)
    {
        var n = c.Tenant.AbsencesRequest;
        return service.RequestAll || 0 < service.RequestAbove && service.RequestAbove < party || service.RequestAllergies && Json.Truthy(pii["allergies"])
            || n > 0 && contact != null && c.S.Scalar<long>("SELECT count(*) FROM core_booking WHERE contact_id=@c AND status='no_show'", new { c = contact.Id }) >= n;
    }

    public BookingException Refused(Service service)
    {
        var phone = Location(service).Phone;
        return new BookingException("Per prenotare contatta l'attività" + (string.IsNullOrEmpty(phone) ? "." : $": {phone}."));
    }

    public static string TokenUrlSafe(int bytes) => Crypto.B64(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=');

    public string ActionToken(string kind, Guid? contact = null, Guid? booking = null, int days = 30)
    {
        var token = TokenUrlSafe(32);
        c.S.Insert(new Actiontoken { TenantId = c.Tenant.Id, Digest = Crypto.TokenHash(token), Kind = kind, ContactId = contact, BookingId = booking, Expires = c.Now.AddDays(days) });
        return token;
    }

    public Delivery Queue(JsonObject payload, Guid? contact = null, Guid? campaign = null)
    {
        var d = new Delivery { TenantId = c.Tenant.Id, ContactId = contact, CampaignId = campaign, NextAttempt = c.Now, Created = c.Now };
        d.Payload = c.Encrypt(payload, "delivery:" + d.Id);
        c.S.Insert(d);
        return d;
    }

    public JsonObject Data(Booking b) => c.Decrypt(b.Pii, "booking:" + b.Id);

    // ---------- create
    public sealed record Placed(Guid Visit, Piece Piece, Service First);

    /// <summary>create_booking(): the booking, and whether it is new (false: the same request key seen before).</summary>
    public (Booking, bool) Create(JsonObject data, string origin = "public", string source = "", Guid? wait = null, Guid? series = null, Placed visit = null, ICollection<string> allowed = null, bool late = false)
    {
        var actor = c.Actor;
        c.Lock(); Fresh();
        if (!c.Operational()) throw new BookingException("Prenotazioni non disponibili.");
        if (actor == null && !c.Desk && !late && c.Tenant.ReceiveSchedule && !c.Cal.Fits(c.Now, c.Now.AddSeconds(1), purpose: "receive"))
            throw new BookingException("Le richieste online sono accettate soltanto negli orari indicati dall'attività.");
        var key = data["request_key"]?.ToString() ?? "";
        if (!Regex.IsMatch(key, "^[A-Za-z0-9_-]{20,64}$")) throw new BookingException("Identificativo richiesta mancante.");
        var fingerprint = c.Index(Json.PyDumps(data), "booking-request");
        var existing = c.S.One<Booking>("SELECT * FROM core_booking WHERE request_key=@k LIMIT 1", new { k = key });
        if (existing != null)
        {
            if (existing.RequestFingerprint != fingerprint) throw new BookingException("Richiesta già usata con dati differenti.");
            return (existing, false);
        }
        if (Json.Truthy(data["extra"]) && visit == null) return CreateVisit(data, origin, source, wait, allowed);
        var service = visit != null ? visit.Piece.Service : ActiveService(data["service"]);
        var staff = actor != null || c.Desk;
        var walkin = staff && IsTrue(data["walkin"]);
        var pii = Personal(data, visit != null ? visit.First : service, staff, walkin);
        var wl = new Waitlist(c);
        if (wait.HasValue) wl.Claim(wait.Value);
        var when = walkin ? With(data, ("start", Iso(new DateTime(c.Now.Ticks - c.Now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc)))) : data;
        Option option; Resource resource; DateTime start, end, bs, be; int party; Occurrence ev; List<string> issues;
        (DateTime?, DateTime?) gap; List<Resource> second;
        if (visit != null)
        {
            var p = visit.Piece;
            (option, resource, start, end, bs, be, gap, second, party, ev, issues) = (p.Option, p.R, p.Start, p.End, p.Bs, p.Be, p.Gap, p.Second, 1, null, new List<string>());
        }
        else
        {
            var w = Bounds(service, when, desk: staff, force: late || staff && (walkin || IsTrue(data["force"])), prefer: LastOperator(service, pii, data, staff));
            (start, end, bs, be, party, option, resource, ev, issues) = (w.Start, w.End, w.Bs, w.Be, w.Party, w.Option, w.Resource, w.Occurrence, w.Issues);
            (gap, second) = Kit(service, start, end, bs, be, resource, issues);
            if (!late) Overrun(issues, data);
        }
        var tables = new Tables(c, this);
        var (area, tableIds) = tables.Seat(service, party, bs, be, tables.PickArea(service, data["area"]), staff ? data["tables"] : null, online: !staff);
        Contact knownByPhone = null; Contact contact; bool newContact;
        var clients = new Clients(c);
        if (staff)
        {
            if (Json.Truthy(data["contact"]))
            {
                var cid = AsId(data["contact"]);
                contact = cid.HasValue ? c.S.One<Contact>("SELECT * FROM core_contact WHERE id=@id AND email_index NOT LIKE 'erased:%'", new { id = cid }) : null;
                if (contact == null) throw new BookingException(INVALID);
                newContact = false;
            }
            else if (walkin && pii.Str("email") == "" && pii.Str("phone") == "") { contact = null; newContact = false; }
            else (contact, newContact) = clients.FindOrCreate(pii, named: origin == "agenda");
            if (pii.Str("email") == "") data = With(data, ("marketing", false));
        }
        else
        {
            var idx = c.Index(pii.Str("email"), "email");
            contact = clients.ByIndex(idx);
            if (contact == null) knownByPhone = clients.ByPhone(pii.Str("phone"), withoutEmail: true);
            if (contact == null && knownByPhone == null && origin == "wordpress") knownByPhone = clients.ByName(pii);
            contact ??= knownByPhone;
            if (contact is { OnlineBlocked: true } || c.S.Scalar<long>("SELECT count(*) FROM core_contact WHERE phone_index=@p AND online_blocked", new { p = clients.PhoneIndex(pii.Str("phone")) }) > 0)
                throw Refused(service);
            var why = clients.CategoryRefusal(contact, start, c.Now);
            if (why != null) throw new BookingException(why);
            newContact = contact == null;
            if (newContact)
            {
                contact = new Contact { TenantId = c.Tenant.Id, EmailIndex = idx, Created = c.Now };
                clients.SealCard(contact, new JsonObject { ["first_name"] = pii.Str("first_name"), ["last_name"] = pii.Str("last_name"), ["email"] = pii.Str("email"), ["phone"] = pii.Str("phone") });
            }
        }
        var consentRequested = contact != null && IsTrue(data["marketing"]) && contact.Consent != "yes" && !contact.Suppressed && knownByPhone == null
            && c.S.Scalar<long>("SELECT count(*) FROM core_actiontoken WHERE contact_id=@c AND kind='consent' AND NOT used AND expires>@n", new { c = contact.Id, n = c.Now }) == 0;
        if (consentRequested && newContact) contact.Consent = "pending";
        var notices = !IsFalse(data["notices"]);
        if (newContact) contact.Notices = notices;
        var ch = data["channel"]?.ToString() is "email" or "sms" or "whatsapp" && Json.Truthy(data["phone"]) ? data["channel"].ToString() : "";
        if (newContact && ch != "") contact.Channel = ch;
        if (contact != null)
        {
            if (newContact && !staff) c.S.Insert(contact);
            else if (newContact) c.S.Update(contact);  // made by FindOrCreate, now with its notices and channel
            else c.S.Update(contact);
        }
        var status = walkin ? "in_progress" : late || !staff && NeedsRequest(service, party, pii, contact) ? "pending" : "confirmed";
        var loc = Location(service);
        var fieldsOf = visit != null ? visit.First : service;
        var booking = new Booking
        {
            TenantId = c.Tenant.Id, ServiceId = service.Id, LocationId = service.LocationId, ResourceId = resource?.Id, OptionId = option?.Id, OccurrenceId = ev?.Id,
            ContactId = contact?.Id, Start = start, End = end, BusyStart = bs, BusyEnd = be, Party = party, RequestKey = key, RequestFingerprint = fingerprint, Status = status,
            Origin = staff ? (walkin ? "walkin" : origin is "import" or "agenda" ? origin : "desk") : origin, Source = staff ? "" : source, Series = series, Visit = visit?.Visit,
            Created = c.Now,
        };
        booking.Updated = c.Now;
        var snap = booking.Snap;
        snap["service"] = service.Name; snap["location"] = loc.Name; snap["resource"] = resource?.Name ?? ""; snap["option"] = option?.Name ?? "";
        snap["field_labels"] = new JsonObject(fieldsOf.FieldList.OfType<JsonObject>().Select(x => KeyValuePair.Create(x.Str("key"), (JsonNode)x.Str("label"))));
        snap["policy"] = new JsonObject { ["cancel_cutoff_minutes"] = service.CancelCutoffMinutes, ["min_days"] = service.MinDays };
        if (!notices) snap["notices"] = false;
        if (ch != "") snap["channel"] = ch;
        if (!staff && c.Language != "it") snap["lang"] = c.Language;
        if (issues.Count > 0 && !late) snap["forced"] = new JsonArray(issues.Select(x => (JsonNode)x).ToArray());
        (booking.GapStart, booking.GapEnd) = gap; Equip(booking, second);
        booking.AreaId = area?.Id; booking.TableIds = tableIds;
        if (resource != null && !Json.Truthy(data["resource"])) snap["any_resource"] = true;
        Prices.Stamp(c.S, snap, service, option, contact, resource);
        booking.Pii = c.Encrypt(pii, "booking:" + booking.Id);
        c.S.Insert(booking);
        var token = ActionToken("cancel", contact?.Id, booking.Id, Math.Max(30, service.HorizonDays + service.MaxDays + 30));
        pii["cancel_token"] = token;
        booking.Pii = c.Encrypt(pii, "booking:" + booking.Id);
        c.S.Update(booking, "pii");
        if (!walkin && series == null && visit == null && booking.Origin != "import" && !(booking.Origin == "agenda" && status == "pending"))
            c.Out.ClientMail(c, booking, status == "pending" ? "requested" : "confirmed");
        if (consentRequested)
        {
            var t2 = ActionToken("consent", contact.Id, booking.Id, 7);
            var shown = c.Language == "en" && !string.IsNullOrEmpty(c.Tenant.ConsentTextEn) ? c.Tenant.ConsentTextEn : c.Tenant.ConsentText;
            booking.Snap["consent_request"] = new JsonObject { ["text"] = shown, ["requested"] = Iso(c.Now), ["source"] = "booking" };
            c.S.Update(booking, "snapshot");
            new Privacy(c).Record(contact.Id, "requested", "booking", shown, booking.Id.ToString());
            Queue(new JsonObject
            {
                ["to"] = pii.Str("email"), ["subject"] = "Conferma iscrizione · " + c.Tenant.Name,
                ["body"] = $"Conferma la richiesta di iscrizione alle comunicazioni di {c.Tenant.Name}:\n{c.PublicOrigin}/action/{c.Tenant.Slug}/{t2}/\nSe non hai richiesto l'iscrizione, ignora il messaggio.",
            }, contact.Id);
        }
        if (series == null && visit == null && booking.Origin != "import") c.Out.NotifyStaff(c, booking, status == "pending" ? "requested" : "created");
        wl.Booked(booking, wait);
        c.Out.Emit(c, booking, "created");
        c.Audit("booking.created", booking.Id, issues.Count > 0 ? new { origin = booking.Origin, forced = issues } : new { origin = booking.Origin });
        Fresh();
        return (booking, true);
    }

    // ---------- visits of several services
    public List<Service> VisitServices(Service first, JsonNode extra)
    {
        var ids = (extra switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',').Select(x => x.Trim()),
            JsonArray a => a.Select(x => x?.ToString() ?? ""),
            _ => Array.Empty<string>(),
        }).Where(x => x != "").ToList();
        if (ids.Count > VISIT_MAX - 1) throw new BookingException($"Al massimo {VISIT_MAX} servizi in una visita.");
        var rest = ids.Select(x => { var s = ActiveService(x); if (s.LocationId != first.LocationId) throw new BookingException(INVALID); return s; }).ToList();
        var all = new List<Service> { first }; all.AddRange(rest);
        if (all.Any(x => x.Kind != "resource")) throw new BookingException("Più servizi nella stessa visita solo per gli appuntamenti con un operatore.");
        return all;
    }

    Piece PieceAt(Service service, Option option, Resource r, DateTime start, List<Piece> taken, Guid[] exclude, bool desk, DateOnly day, Dictionary<object, object> cache)
    {
        var end = start + M(Length(service, option, r));
        var bs = start - M(service.BufferBefore); var be = end + M(service.BufferAfter);
        bool Clash(Resource x) => taken.Any(p => (p.R.Id == x.Id || (p.Second ?? new()).Any(y => y.Id == x.Id)) && bs < p.Be && p.Bs < be);
        if (Clash(r)) return null;
        var key = (service.Id, r.Id, start);
        if (!cache.TryGetValue(key, out var ok))
        {
            var wk = ("w", service.Id, r.Id);
            if (!cache.TryGetValue(wk, out var w)) cache[wk] = w = c.Cal.Windows(day, service, r);
            cache[key] = ok = ((List<Span>)w).Any(x => x.A <= bs && be <= x.B) && CapacityAvailable(service, bs, be, 1, r, exclude: exclude, online: !desk, gap: Posa(service, start, end, r));
        }
        if (!(bool)ok) return null;
        var nk = ("need", service.Id);
        if (!cache.TryGetValue(nk, out var need)) cache[nk] = need = Linked(service, true);
        var needList = (List<Resource>)need;
        List<Resource> second = needList.Count > 0 ? Groups(needList).Select(g => g.FirstOrDefault(x => !Clash(x) && CapacityAvailable(service, bs, be, 1, x, exclude: exclude))).ToList() : null;
        if (second != null && second.Contains(null)) return null;
        var p = Posa(service, start, end, r);
        return new Piece(service, option, r, start, end, bs, be, p.HasValue ? (p.Value.A, p.Value.B) : (null, null), second);
    }

    List<Piece> Chain(List<(Service, Option)> items, Resource first, DateTime start, bool split, DateOnly day, TimeSpan wait, Guid[] exclude, bool desk, ICollection<string> allowed, Dictionary<object, object> cache)
    {
        var p0 = PieceAt(items[0].Item1, items[0].Item2, first, start, new(), exclude, desk, day, cache);
        if (p0 == null) return null;
        var plan = new List<Piece> { p0 };
        foreach (var (service, option) in items.Skip(1))
        {
            var ok = ("ops", service.Id);
            if (!cache.TryGetValue(ok, out var mine0)) cache[ok] = mine0 = Ranked(service, Linked(service).Where(r => allowed == null || allowed.Contains(r.Id.ToString())), day);
            var mine = (List<Resource>)mine0;
            var ops = new List<Resource>();
            if (mine.Any(r => r.Id == first.Id)) ops.Add(first);
            if (split) ops.AddRange(mine.Where(r => r.Id != first.Id));
            var t = plan[^1].End; Piece found = null;
            while (t <= plan[^1].End + wait && found == null)
            {
                foreach (var r in ops) { found = PieceAt(service, option, r, t, plan, exclude, desk, day, cache); if (found != null) break; }
                t += M(5);
            }
            if (found == null) return null;
            plan.Add(found);
        }
        return plan;
    }

    public List<Slot> VisitSlots(List<(Service, Option)> items, DateOnly day, Resource resource = null, IEnumerable<Guid> exclude = null, bool desk = false, ICollection<string> allowed = null)
    {
        var (service, option) = items[0]; var place = Location(service); var ex = Ex(exclude);
        var ops = resource != null ? new List<Resource> { resource } : Ranked(service, Linked(service).Where(r => allowed == null || allowed.Contains(r.Id.ToString())), day);
        var firsts = ops.ToDictionary(r => r.Id, r => Available(service.Id.ToString(), day, option?.Id.ToString(), r.Id.ToString(), 1, ex, desk: desk));
        var wait = M(place.VisitWait); var outp = new SortedDictionary<string, List<Piece>>(StringComparer.Ordinal);
        var cache = new Dictionary<object, object>();
        foreach (var r in ops) foreach (var x in firsts[r.Id]) cache[(service.Id, r.Id, x.StartUtc)] = true;
        var splits = place.VisitSplit switch { "split" => new[] { true }, "need" => new[] { false, true }, _ => new[] { false } };
        foreach (var split in splits)
            foreach (var r in ops)
                foreach (var slot in firsts[r.Id])
                    if (!outp.ContainsKey(slot.Start) && Chain(items, r, slot.StartUtc, split, day, wait, ex, desk, allowed, cache) is { } plan) outp[slot.Start] = plan;
        return outp.Select(kv => new Slot
        {
            Start = kv.Key, StartUtc = kv.Value[0].Start, End = Iso(kv.Value[^1].End), EndUtc = kv.Value[^1].End, Resource = kv.Value[0].R.Id.ToString(),
            ResourceName = string.Join(" + ", kv.Value.Select(p => p.R.Name).Distinct()), Occurrence = "", Plan = kv.Value,
        }).ToList();
    }

    public List<Booking> VisitRows(Booking b)
    {
        if (b.Visit == null) return new() { b };
        var rows = c.S.Query<Booking>("SELECT * FROM core_booking WHERE visit=@v" + (b.Status == "cancelled" ? "" : " AND status<>'cancelled'") + " ORDER BY start", new { v = b.Visit }).ToList();
        return rows.Count > 0 ? rows : new() { b };
    }

    public List<Piece> VisitPlan(Service first, Option option, JsonNode extra, JsonObject data, IEnumerable<Guid> exclude = null, bool desk = false, ICollection<string> allowed = null)
    {
        var items = VisitServices(first, extra).Select(x => (x, (Option)null)).ToList(); items[0] = (first, option);
        var start = Times.ParseAware(data["start"]?.ToString(), out var naive) ?? throw new BookingException("Data o ora non valida.");
        if (naive) throw new BookingException("Fuso orario mancante.");
        var resource = Details(first, null, Json.Truthy(data["resource"]) ? data["resource"] : null).Item2;
        var slots = VisitSlots(items, c.Cal.DayOf(start), resource, exclude, desk, allowed);
        return slots.FirstOrDefault(x => x.StartUtc == start)?.Plan ?? throw new BookingException("Orario non disponibile. Aggiorna le disponibilità.");
    }

    (Booking, bool) CreateVisit(JsonObject data, string origin, string source, Guid? wait, ICollection<string> allowed)
    {
        if (IsTrue(data["walkin"]) || IsTrue(data["force"])) throw new BookingException("Più servizi insieme si prenotano su un orario libero, senza forzare.");
        var first = ActiveService(data["service"]);
        var plan = VisitPlan(first, Details(first, data["option"]).Item1, data["extra"], data, desk: c.Actor != null || c.Desk, allowed: allowed);
        var vid = Guid.NewGuid(); var key = data["request_key"].ToString(); var made = new List<Booking>();
        for (int i = 0; i < plan.Count; i++)
        {
            var d = i == 0 ? data : With(data, ("request_key", $"{(key.Length > 58 ? key[..58] : key)}-v{i}"));
            made.Add(Create(d, origin, source, i == 0 ? wait : null, visit: new Placed(vid, plan[i], first)).Item1);
        }
        if (made.Any(b => b.Status == "pending"))
        {
            c.S.Execute("UPDATE core_booking SET status='pending' WHERE visit=@v AND status='confirmed'", new { v = vid });
            made[0].Status = "pending";
        }
        var kind = made[0].Status == "pending" ? "requested" : "confirmed";
        c.Out.ClientMail(c, made[0], kind);
        c.Out.NotifyStaff(c, made[0], made[0].Status == "pending" ? "requested" : "created");
        return (made[0], true);
    }

    // ---------- move
    public Booking Move(Guid bookingId, JsonObject data, bool desk = true, bool quiet = false)
    {
        c.Lock(); Fresh();
        var booking = c.S.One<Booking>("SELECT * FROM core_booking WHERE id=@id AND status = ANY(@m)", new { id = bookingId, m = MOVABLE }) ?? throw new BookingException(INVALID);
        if (booking.Visit != null && IsTrue(data["visit"])) return MoveVisit(booking, data, desk);
        var service = ActiveService(data["service"] ?? booking.ServiceId.ToString());
        var w = Bounds(service, data, new[] { booking.Id }, desk, desk && IsTrue(data["force"]));
        var issues = w.Issues;
        var (gap, second) = Kit(service, w.Start, w.End, w.Bs, w.Be, w.Resource, issues, new[] { booking.Id });
        Overrun(issues, data);
        var clients = new Clients(c);
        if (!desk && clients.CategoryRefusal(c.Row<Contact>(booking.ContactId), w.Start, c.Now, new[] { booking.Id }) is string why) throw new BookingException(why);
        object Key(Booking b) => (b.Start, b.Party, b.OptionId, b.ResourceId, b.OccurrenceId, b.ServiceId);
        var before = Key(booking); var left = (booking.Start, booking.End);
        (booking.Start, booking.End, booking.BusyStart, booking.BusyEnd, booking.Party) = (w.Start, w.End, w.Bs, w.Be, w.Party);
        booking.OptionId = w.Option?.Id; booking.ResourceId = w.Resource?.Id; booking.OccurrenceId = w.Occurrence?.Id; booking.ServiceId = service.Id;
        (booking.GapStart, booking.GapEnd) = gap; Equip(booking, second);
        var moved = !Key(booking).Equals(before);
        var snap = booking.Snap; snap.Remove("forced");
        if (issues.Count > 0) snap["forced"] = new JsonArray(issues.Select(x => (JsonNode)x).ToArray());
        booking.LocationId = service.LocationId;
        snap["service"] = service.Name; snap["location"] = Location(service).Name; snap["resource"] = w.Resource?.Name ?? ""; snap["option"] = w.Option?.Name ?? "";
        Prices.Stamp(c.S, snap, service, w.Option, c.Row<Contact>(booking.ContactId), w.Resource);
        if (moved)
        {
            foreach (var n in REARM) snap.Remove(n);
            var t = new Tables(c, this);
            if (!Tables.KINDS.Contains(service.Kind)) booking.TableIds = new();
            else if (booking.TableIds.Count == 0 || booking.TableIds.Intersect(t.Taken(Location(service), booking.BusyStart, booking.BusyEnd, booking.Id)).Any())
            {
                var (area, ids) = t.Seat(service, booking.Party, booking.BusyStart, booking.BusyEnd, c.Row<Area>(booking.AreaId), exclude: booking.Id);
                booking.AreaId = area?.Id; booking.TableIds = ids;
            }
        }
        booking.Updated = c.Now;
        c.S.Update(booking);
        c.S.Execute("UPDATE core_actiontoken SET expires=@e WHERE booking_id=@b AND kind='cancel' AND NOT used", new { e = booking.End.AddDays(30), b = booking.Id });
        if (!moved)
        {
            c.Audit("booking.resized", booking.Id, issues.Count > 0 ? new { minutes = (int)(booking.End - booking.Start).TotalMinutes, forced = issues } : new { minutes = (int)(booking.End - booking.Start).TotalMinutes });
            Fresh();
            return booking;
        }
        c.S.Execute("UPDATE core_actiontoken SET expires=@n WHERE booking_id=@b AND kind IN ('confirm','decline') AND NOT used", new { n = c.Now, b = booking.Id });
        new Waitlist(c).Freed(left.Start, left.End);
        if (!quiet) { c.Out.ClientMail(c, booking, "moved"); c.Out.NotifyStaff(c, booking, "moved"); }
        c.Out.Emit(c, booking, "moved");
        c.Audit("booking.moved", booking.Id, issues.Count > 0 ? new { forced = issues } : null);
        Fresh();
        return booking;
    }

    Booking MoveVisit(Booking booking, JsonObject data, bool desk)
    {
        var rows = VisitRows(booking).Where(b => MOVABLE.Contains(b.Status)).ToList();
        var first = c.Row<Service>(rows[0].ServiceId);
        var plan = VisitPlan(first, c.Row<Option>(rows[0].OptionId), new JsonArray(rows.Skip(1).Select(b => (JsonNode)b.ServiceId.ToString()).ToArray()), data, rows.Select(b => b.Id), desk);
        if (!desk && new Clients(c).CategoryRefusal(c.Row<Contact>(booking.ContactId), plan[0].Start, c.Now, rows.Select(b => b.Id)) is string why) throw new BookingException(why);
        var wl = new Waitlist(c);
        foreach (var (b, p) in rows.Zip(plan))
        {
            var left = (b.Start, b.End);
            (b.Start, b.End, b.BusyStart, b.BusyEnd, b.ResourceId) = (p.Start, p.End, p.Bs, p.Be, p.R.Id);
            (b.GapStart, b.GapEnd) = p.Gap; Equip(b, p.Second);
            b.Snap["resource"] = p.R.Name; b.Snap.Remove("forced");
            foreach (var n in REARM) b.Snap.Remove(n);
            Prices.Stamp(c.S, b.Snap, c.Row<Service>(b.ServiceId), c.Row<Option>(b.OptionId), c.Row<Contact>(b.ContactId), p.R);
            b.Updated = c.Now; c.S.Update(b);
            c.S.Execute("UPDATE core_actiontoken SET expires=@e WHERE booking_id=@b AND kind='cancel' AND NOT used", new { e = b.End.AddDays(30), b = b.Id });
            c.S.Execute("UPDATE core_actiontoken SET expires=@n WHERE booking_id=@b AND kind IN ('confirm','decline') AND NOT used", new { n = c.Now, b = b.Id });
            wl.Freed(left.Start, left.End); c.Out.Emit(c, b, "moved"); c.Audit("booking.moved", b.Id, new { visit = b.Visit.ToString() });
        }
        c.Out.ClientMail(c, rows[0], "moved"); c.Out.NotifyStaff(c, rows[0], "moved");
        Fresh();
        return c.S.Get<Booking>(booking.Id);
    }

    // ---------- status
    public bool CanUndo(Booking b, DateTime? now = null)
    {
        var last = b.Snap["undo"] as JsonObject;
        return last != null && last.Str("to") == b.Status && (now ?? c.Now) <= Times.ParseAware(last.Str("at"), out _)!.Value.AddMinutes(UNDO_MINUTES);
    }

    public Booking ChangeStatus(Booking bk, string status, string reason = "", string note = "", bool undo = false, bool quiet = false)
    {
        c.Lock(); Fresh();
        var booking = c.S.One<Booking>("SELECT * FROM core_booking WHERE id=@id FOR UPDATE", new { id = bk.Id });
        var old = booking.Status; var now = c.Now;
        if (undo)
        {
            if (!CanUndo(booking, now)) throw new BookingException($"Si torna indietro solo entro {UNDO_MINUTES} minuti: cambia lo stato da qui sotto.");
            status = booking.Snap["undo"].Str("from");
        }
        else if (booking.Status == status) return booking;
        else if (!MOVES.TryGetValue(old, out var to) || !to.Contains(status)) throw new BookingException("Cambio di stato non consentito.");
        else if (status is "completed" or "no_show" && booking.Start > now && old is not ("arrived" or "in_progress")) throw new BookingException("L'appuntamento non è ancora iniziato.");
        else if (status is "arrived" or "in_progress" && c.Cal.DayOf(booking.Start) > c.Today) throw new BookingException("La prenotazione non è di oggi.");
        if (old == "cancelled")
        {
            var service = c.Row<Service>(booking.ServiceId);
            var res = service.Kind == "resource" ? c.Row<Resource>(booking.ResourceId) : null;
            var others = c.S.Query<Resource>("SELECT * FROM core_resource WHERE id=@s OR id::text = ANY(@m)", new { s = booking.SecondaryId ?? Guid.Empty, m = booking.MoreIds.ToArray() }).ToList();
            if (!CapacityAvailable(service, booking.BusyStart, booking.BusyEnd, booking.Party, res, c.Row<Occurrence>(booking.OccurrenceId), new[] { booking.Id },
                    gap: booking.GapStart.HasValue ? (booking.GapStart.Value, booking.GapEnd.Value) : null)
                || others.Any(x => !CapacityAvailable(service, booking.BusyStart, booking.BusyEnd, 1, x, exclude: new[] { booking.Id })))
                throw new BookingException("Il posto nel frattempo è stato preso: sposta la prenotazione su un altro orario.");
        }
        booking.Status = status;
        if (undo) booking.Snap.Remove("undo");
        else booking.Snap["undo"] = new JsonObject { ["from"] = old, ["to"] = status, ["at"] = Iso(now) };
        if (status == "cancelled" && !undo)
        {
            booking.Snap["cancel_reason"] = REASON_LABELS.ContainsKey(reason ?? "") ? reason : "other";
            note = string.Join(" ", (note ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); if (note.Length > 300) note = note[..300];
            var pii = Data(booking); pii.Remove("cancel_note");
            if (note != "") pii["cancel_note"] = note;
            booking.Pii = c.Encrypt(pii, "booking:" + booking.Id);
        }
        if (old == "cancelled") c.S.Execute("UPDATE core_actiontoken SET used=false WHERE booking_id=@b AND kind='cancel'", new { b = booking.Id });
        booking.Updated = c.Now;
        c.S.Update(booking, "status", "snapshot", "pii", "updated");
        if (booking.Visit != null && !quiet && (status is "cancelled" or "confirmed" || undo))
            foreach (var other in c.S.Query<Booking>("SELECT * FROM core_booking WHERE visit=@v AND status=@o AND id<>@id", new { v = booking.Visit, o = old, id = booking.Id }).ToList())
                try { ChangeStatus(other, status, reason, note, undo, quiet: true); } catch (BookingException) { }
        if (HOLDING.Contains(old) && !HOLDING.Contains(status)) new Waitlist(c).Freed(booking.Start, booking.End);
        if (!undo && !quiet)
        {
            var mail = status switch { "cancelled" => reason == "rejected" ? "rejected" : "cancelled", "confirmed" => old == "pending" ? "accepted" : "", _ => null };
            if (!string.IsNullOrEmpty(mail)) c.Out.ClientMail(c, booking, mail);
            if (status == "cancelled") c.Out.NotifyStaff(c, booking, "cancelled");
        }
        if (status == "cancelled") c.Out.Emit(c, booking, "cancelled");
        var detail = new Dictionary<string, object> { ["from"] = old };
        if (status == "cancelled") detail["reason"] = booking.Snap.Str("cancel_reason");
        c.Audit(undo ? "booking.undo" : "booking." + status, booking.Id, detail);
        Fresh();
        return booking;
    }

    // ---------- series
    public JsonObject SeriesBase(Booking b)
    {
        var d = Data(b);
        var o = new JsonObject
        {
            ["service"] = b.ServiceId.ToString(), ["resource"] = b.ResourceId?.ToString() ?? "", ["option"] = b.OptionId?.ToString() ?? "", ["party"] = b.Party,
            ["contact"] = b.ContactId?.ToString(),
        };
        foreach (var k in new[] { "first_name", "last_name", "email", "phone", "notes" }) o[k] = d.Str(k);
        o["answers"] = d["answers"]?.DeepClone() ?? new JsonObject();
        o["allergies"] = d["allergies"]?.DeepClone() ?? new JsonArray();
        return o;
    }
    static string FirstSentence(string message) => message.Split('.')[0].ToLowerInvariant();

    public List<(DateTime Start, string Why)> SeriesPlan(Booking b, int every, DateOnly? until)
    {
        var service = c.Row<Service>(b.ServiceId);
        if (service.Kind is not ("resource" or "capacity") || b.ContactId == null) throw new BookingException("Si ripetono gli appuntamenti e le fasce orarie di un cliente con la scheda.");
        var first = c.Cal.ToLocal(b.Start); var firstDay = DateOnly.FromDateTime(first);
        if (every is < 1 or > 4 || until == null || !(firstDay < until && until <= firstDay.AddDays(366))) throw new BookingException("Scegli ogni quante settimane e fino a quando, al massimo un anno.");
        var outp = new List<(DateTime, string)>(); var baseData = SeriesBase(b);
        for (var d = firstDay.AddDays(7 * every); d <= until; d = d.AddDays(7 * every))
        {
            var start = c.Cal.Local(d, first.TimeOfDay); string why = null;
            if (start == null) why = "ora che non esiste per il cambio dell'ora";
            else try { Bounds(service, With(baseData, ("start", Iso(start.Value))), desk: true); } catch (BookingException ex) { why = FirstSentence(ex.Message); }
            outp.Add((start ?? DateTime.SpecifyKind(d.ToDateTime(TimeOnly.FromTimeSpan(first.TimeOfDay)) - c.Zone.BaseUtcOffset, DateTimeKind.Utc), why));
        }
        return outp;
    }

    public int SeriesCreate(Booking b, int every, DateOnly until)
    {
        var sid = b.Series ?? Guid.NewGuid(); var made = 0;
        if (b.Series == null) { b.Series = sid; b.Updated = c.Now; c.S.Update(b, "series", "updated"); }
        foreach (var (start, why) in SeriesPlan(b, every, until))
        {
            if (why != null) continue;
            try { Create(With(SeriesBase(b), ("start", Iso(start)), ("request_key", TokenUrlSafe(24))), series: sid); made++; } catch (BookingException) { }
        }
        c.Audit("booking.series", b.Id, new { made, every });
        return made;
    }

    public List<Booking> SeriesRows(Booking b, bool following) =>
        !following || b.Series == null ? new() { b } :
        c.S.Query<Booking>("SELECT * FROM core_booking WHERE series=@s AND status = ANY(@m) AND start>=@st ORDER BY start", new { s = b.Series, m = MOVABLE, st = b.Start }).ToList();

    public List<(Booking B, DateTime At, string Why)> SeriesMovePlan(Booking b, DateTime start, Resource resource, bool following)
    {
        var rows = SeriesRows(b, following); var nw = c.Cal.ToLocal(start); var shift = DateOnly.FromDateTime(nw).DayNumber - c.Cal.DayOf(b.Start).DayNumber;
        var outp = new List<(Booking, DateTime, string)>();
        foreach (var x in rows)
        {
            var d = c.Cal.DayOf(x.Start).AddDays(shift);
            var at = c.Cal.Local(d, nw.TimeOfDay); string why = null;
            if (at == null) why = "ora che non esiste per il cambio dell'ora";
            else try
                {
                    Bounds(c.Row<Service>(x.ServiceId), new JsonObject
                    {
                        ["option"] = x.OptionId?.ToString() ?? "", ["resource"] = (resource?.Id ?? x.ResourceId)?.ToString() ?? "", ["party"] = x.Party, ["start"] = Iso(at.Value),
                    }, new[] { x.Id }, desk: true);
                }
                catch (BookingException ex) { why = FirstSentence(ex.Message); }
            outp.Add((x, at ?? DateTime.SpecifyKind(d.ToDateTime(TimeOnly.FromTimeSpan(nw.TimeOfDay)) - c.Zone.BaseUtcOffset, DateTimeKind.Utc), why));
        }
        return outp;
    }

    public int SeriesMove(Booking b, DateTime start, Resource resource, bool following)
    {
        var made = 0;
        foreach (var (x, at, why) in SeriesMovePlan(b, start, resource, following))
        {
            if (why != null) continue;
            try
            {
                Move(x.Id, new JsonObject
                {
                    ["service"] = x.ServiceId.ToString(), ["option"] = x.OptionId?.ToString() ?? "", ["resource"] = (resource?.Id ?? x.ResourceId)?.ToString() ?? "", ["party"] = x.Party, ["start"] = Iso(at),
                }, quiet: made > 0);
                made++;
            }
            catch (BookingException) { }
        }
        c.Audit("booking.series_moved", b.Id, new { moved = made });
        return made;
    }
}
