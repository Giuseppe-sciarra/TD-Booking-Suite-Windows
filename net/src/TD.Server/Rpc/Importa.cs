using System.Text.Json.Nodes;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Rpc;

/// <summary>portal.contacts_import: clients and treatment history from the old program (CSV or Excel): file, columns,
/// preview, import. Needs what adding clients and treatments by hand needs; the marketing consents also the privacy
/// permission. The window keeps the token of the sealed copy between the steps (the web page kept it in the session).</summary>
public static class Importa
{
    static string CapFirst(string s) => s == "" ? s : char.ToUpper(s[0]) + s[1..];
    static object Lines(ClientImport.Reasons r) => r.Items.Select(x => new
    {
        motivo = CapFirst(x.Reason),
        righe = x.Lines.Count == 1 ? $"riga {x.Lines[0]}" : $"{x.Lines.Count} righe, {string.Join(", ", x.Lines.Take(12))}" + (x.Lines.Count > 12 ? " e altre" : ""),
    }).ToList();

    [Rpc("clienti.importa", Perm = "contacts.read")]
    public static object Clients(Call c)
    {
        foreach (var perm in new[] { "pii.read", "notes.read", "booking.write" }) c.Require(perm);
        var x = c.C; var t = c.Tenant; var imp = new ClientImport(x); var action = c.Str("azione"); var token = c.Str("token"); var error = "";
        var spec = Presets.RecordOf(t.BusinessType, x.S.Scalar<long>("SELECT count(*) FROM core_service WHERE kind='resource'") > 0); var consentOk = c.Can("privacy.manage");
        object Page(object more = null) => new { spec = spec == null ? null : new { titolo = spec.Title, uno = spec.One }, consensi = consentOk, marketing = t.MarketingEnabled, conservazione = t.RetentionDays, errore = error, file = more };
        if (action == "upload")
        {
            byte[] raw; try { raw = Convert.FromBase64String(c.Str("file")); } catch (FormatException) { raw = Array.Empty<byte>(); }
            try
            {
                if (raw.Length == 0) throw new BookingException("Scegli il file da importare.");
                if (raw.Length > Importer.MAX_BYTES) throw new BookingException("File troppo grande: al massimo 20 MB.");
                var (h, r) = Importer.Read(raw); ClientImport.Drop(token);
                token = imp.Stash(c.Str("nome"), h, r); action = "";
            }
            catch (BookingException ex) { error = ex.Message; return Page(); }
        }
        var stash = token != "" ? imp.Unstash(token) : null;
        if (action == "reset" || stash == null) { ClientImport.Drop(token); return Page(); }
        var (name, head, rows) = stash.Value;
        var choices = ClientImport.Fields(spec != null, consentOk); var allowed = choices.Select(f => f.Key).ToHashSet();
        List<string> mapping;
        if (action is "preview" or "import") { var posted = (c.Args["colonne"] as JsonArray)?.Select(v => v?.ToString() ?? "").ToList() ?? new(); mapping = Enumerable.Range(0, head.Count).Select(i => i < posted.Count && allowed.Contains(posted[i]) ? posted[i] : "").ToList(); }
        else mapping = ClientImport.Guess(head, spec != null, consentOk);
        var ask = c.Bool("ask") && consentOk && t.MarketingEnabled && mapping.Contains("consent");
        var check = token + "|" + string.Join(",", mapping) + (ask ? ":ask" : "");  // bound to this very file, not only to its columns
        // the import runs only on the columns of the preview just seen
        if (action == "import" && c.Str("checked") != check) { action = "preview"; error = "Hai cambiato qualche colonna: controlla di nuovo l'anteprima."; }
        ClientImport.Planned plan = null;
        try
        {
            if (action == "import")
            {
                // NO KEY UPDATE: bookings wait as with every writer, while a card saved meanwhile can still write its
                // audit row; the cards themselves are locked by the plan
                x.S.Execute("SELECT 1 FROM core_tenant WHERE id=@t FOR NO KEY UPDATE", new { t = t.Id });
                var done = imp.Apply(imp.Plan(head, rows, mapping, spec != null, consentOk, true), ask, token);
                x.Audit("contacts.imported", token, new { rows = done.GetValueOrDefault("rows"), created = done.GetValueOrDefault("created"), updated = done.GetValueOrDefault("updated"), treatments = done.GetValueOrDefault("treatments"), consents = done.GetValueOrDefault("consents"), requested = done.GetValueOrDefault("requested") });
                x.AfterCommit.Add(() => ClientImport.Drop(token));
                return new { fatto = ClientImport.Summary(done) };
            }
            if (action == "preview") plan = imp.Plan(head, rows, mapping, spec != null, consentOk);
        }
        catch (BookingException ex) { error = ex.Message; }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") { error = "Nel frattempo qualcuno ha aggiunto uno di questi clienti: premi di nuovo Importa, l'anteprima lo terrà in conto."; }
        return Page(new
        {
            token, nome = name, righe = rows.Count, campi = choices.Select(f => new { id = f.Key, nome = f.Label }), ask, verificato = check,
            colonne = head.Select((h, i) => new { titolo = h, esempi = string.Join(" · ", ClientImport.Samples(rows, i)), campo = mapping[i] }),
            piano = plan == null ? null : new
            {
                esempi = plan.Samples.Select(s => new { riga = s.Line, nome = s.Name, stato = s.Status, telefono = s.Phone, email = s.Email, voce = s.Entry }), problemi = Lines(plan.Problems), scarti = Lines(plan.Issues),
                dal = plan.Since.ToString("dd/MM/yyyy"), numeri = plan.Report,
            },
        });
    }
}
