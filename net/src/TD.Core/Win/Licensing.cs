using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Core.Win;

/// <summary>A licence's data, as tdwin/licensing.parse reads it.</summary>
public sealed record Licence(string Id, string Intestatario, int Attivita, int Postazioni, string Installazione, string Emessa, DateOnly? Scadenza, string Tipo, string Sostituisce);

/// <summary>What this installation may do now (status()).</summary>
public sealed class LicenceState
{
    public string Kind { get; init; } = "bloccato";  // licenza, prova, bloccato
    public int Attivita { get; init; } = 1;
    public int Postazioni { get; init; } = 2;
    public int? Giorni { get; init; }
    public DateTime? Fine { get; init; }
    public string Problema { get; init; } = "";
    public bool ProvaUsata { get; init; }
    public Licence Licence { get; init; }
}

/// <summary>tdwin/licensing.py: licences signed offline by TD Licenze with Ed25519, the 7-day trial, the clock that
/// never goes back, the signed online state (new licence for this installation, revoked ones).</summary>
public sealed class Licensing(string dataFolder, string installId, Db db)
{
    public const string PREFIX = "TDBS1.", STATE_PREFIX = "TDST1.";
    public const int TRIAL_DAYS = 7;
    public static readonly Dictionary<string, string> TIPI = new() { ["pc"] = "Un PC solo", ["server"] = "Server con postazioni", ["portale"] = "Portale, più attività" };
    public string StateUrl { get; set; } = "https://raw.githubusercontent.com/Giuseppe-sciarra/TD-Licenze-Stato/main/stato-licenze.json";
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    static readonly TimeZoneInfo Rome = Zones.Find("Europe/Rome");

    /// <summary>The seller's public key, built into the program (the private one never leaves TD Licenze).</summary>
    public static string PublicKey { get; set; } = ReadKey();
    static string ReadKey()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TD.Core.Win.licenza-chiave-pubblica.txt");
        return s == null ? "" : new StreamReader(s).ReadToEnd().Trim();
    }

    public static byte[] UnB64(string t) => Convert.FromBase64String(t.Replace('-', '+').Replace('_', '/') + new string('=', (4 - t.Length % 4) % 4));
    public static string Normalize(string code) => new((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string MachineId()
    {
        if (OperatingSystem.IsWindows())
        {
            using var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return k?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        try { return File.ReadAllText("/etc/machine-id").Trim(); } catch { return ""; }
    }

    public string InstallCode(string id = null)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"tdbs-installazione\n{id ?? installId}\n{MachineId()}"));
        var code = Base32(digest)[..16];
        return string.Join("-", Enumerable.Range(0, 4).Select(i => code.Substring(i * 4, 4)));
    }
    static string Base32(byte[] data)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var sb = new StringBuilder(); int buf = 0, bits = 0;
        foreach (var b in data) { buf = buf << 8 | b; bits += 8; while (bits >= 5) { sb.Append(A[(buf >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) sb.Append(A[(buf << (5 - bits)) & 31]);
        return sb.ToString();
    }

    sealed class Tampered(string m) : Exception(m);

    static Ed25519PublicKeyParameters Verifier(string key)
    {
        var pub = new Ed25519PublicKeyParameters(UnB64(key));
        // a verifier patched to accept anything stops the program instead
        if (Verify(pub, new byte[64], Encoding.ASCII.GetBytes("tdbs-controllo"))) throw new Tampered("La verifica delle licenze di questo programma è stata modificata: reinstalla TD Booking Suite.");
        return pub;
    }
    static bool Verify(Ed25519PublicKeyParameters pub, byte[] sig, byte[] msg)
    {
        var v = new Ed25519Signer(); v.Init(false, pub); v.BlockUpdate(msg, 0, msg.Length); return v.VerifySignature(sig);
    }

    /// <summary>The JSON a text carries when the seller signed it (TDBS1. licence, TDST1. state).</summary>
    static JsonObject Signed(string text, string prefix)
    {
        if (string.IsNullOrEmpty(PublicKey)) throw new KeyNotFoundException();
        var pub = Verifier(PublicKey);
        var parts = text[prefix.Length..].Split('.');
        if (parts.Length != 2) throw new FormatException("cut");
        var sig = UnB64(parts[1]);
        if (sig.Length != 64) throw new FormatException("cut");
        if (!Verify(pub, sig, Encoding.UTF8.GetBytes(prefix + parts[0]))) throw new CryptographicException("firma");
        return JsonNode.Parse(UnB64(parts[0])) as JsonObject ?? throw new FormatException();
    }

    public static Licence Parse(string text)
    {
        text = string.Concat((text ?? "").Where(ch => !char.IsWhiteSpace(ch)));
        if (!text.StartsWith(PREFIX)) throw new ArgumentException("Non è una licenza di TD Booking Suite: deve iniziare con TDBS1.");
        JsonObject raw; Licence d;
        try
        {
            raw = Signed(text, PREFIX);
            d = new Licence(raw["id"]!.ToString(), raw["intestatario"]!.ToString(), (int)raw["attivita"]!, (int)raw["postazioni"]!, raw.Str("installazione"), raw.Str("emessa"),
                raw.Str("scadenza") != "" ? DateOnly.ParseExact(raw.Str("scadenza"), "yyyy-MM-dd") : null, "", "");
        }
        catch (KeyNotFoundException) { throw new ArgumentException("Questa installazione non ha la chiave per verificare le licenze: aggiorna il programma."); }
        catch (Tampered) { throw; }
        catch (CryptographicException) { throw new ArgumentException("Licenza non valida: la firma non corrisponde, forse è stata modificata."); }
        catch (Exception) { throw new ArgumentException("Licenza incompleta: copiala di nuovo per intero."); }
        if (d.Attivita < 1 || d.Postazioni < 0) throw new ArgumentException("Licenza non valida.");
        var kind = raw.Str("tipo");
        kind = TIPI.ContainsKey(kind) ? kind : d.Attivita > 1 ? "portale" : d.Postazioni == 0 ? "pc" : "server";
        return d with { Tipo = kind, Sostituisce = raw.Str("sostituisce") };
    }

    public string Problem(Licence d, DateOnly today, string code = null)
    {
        code ??= InstallCode();
        if (d.Installazione != "" && Normalize(d.Installazione) != Normalize(code)) return $"Questa licenza è per l'installazione {d.Installazione}, non per questa ({code}).";
        if (d.Scadenza.HasValue && d.Scadenza < today) return $"La licenza è scaduta il {d.Scadenza:dd/MM/yyyy}.";
        return "";
    }

    string Folder => Path.Combine(dataFolder, "licenza");
    public string LicencePath => Path.Combine(Folder, "licenza.txt");
    string Marker(string name) => Path.Combine(Folder, name);
    DateTime? ReadTime(string name)
    {
        try { return DateTimeOffset.Parse(File.ReadAllText(Marker(name)).Trim(), CultureInfo.InvariantCulture).UtcDateTime; } catch { return null; }
    }
    void WriteTime(string name, DateTime value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = Marker(name) + ".tmp";
            File.WriteAllText(tmp, Times.Iso(value, TimeZoneInfo.Utc) + "\n");
            File.Move(tmp, Marker(name), true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    (DateTime, long)? stamp; (Licence, string) cached;
    public (Licence, string) Installed()
    {
        var fi = new FileInfo(LicencePath);
        if (!fi.Exists) return (null, "");
        var st = (fi.LastWriteTimeUtc, fi.Length);
        if (stamp != st)
        {
            try { cached = (Parse(File.ReadAllText(LicencePath)), ""); } catch (ArgumentException ex) { cached = (null, ex.Message); }
            stamp = st;
        }
        return cached;
    }

    /// <summary>Now, never earlier than the last time the program ran.</summary>
    public DateTime Clock()
    {
        var real = Now();
        using var s = db.Global();
        var seen = s.Query<DateTime>("SELECT visto FROM tdwin_orologio WHERE id=1").ToList();
        if (ReadTime("visto.txt") is DateTime f) seen.Add(f);
        DateTime? last = seen.Count > 0 ? seen.Max() : null;
        if (last == null || real > last.Value.AddMinutes(10))
        {
            s.Execute("INSERT INTO tdwin_orologio (id, visto) VALUES (1, @v) ON CONFLICT (id) DO UPDATE SET visto=@v", new { v = real });
            s.Commit(); WriteTime("visto.txt", real);
            return real;
        }
        return real > last.Value ? real : last.Value;
    }

    public DateTime? TrialStart()
    {
        using var s = db.Global();
        var found = s.Query<DateTime>("SELECT attivata FROM tdwin_prova WHERE id=1").ToList();
        if (ReadTime("prova.txt") is DateTime f) found.Add(f);
        return found.Count > 0 ? found.Min() : null;
    }

    public (DateTime, bool) StartTrial(string who)
    {
        var first = TrialStart(); var start = first ?? Now();
        using var s = db.Global();
        var created = s.Execute("INSERT INTO tdwin_prova (id, attivata, da) VALUES (1, @a, @d) ON CONFLICT (id) DO NOTHING", new { a = start, d = who ?? "" }) > 0;
        s.Commit();
        if (ReadTime("prova.txt") == null) WriteTime("prova.txt", start);
        return (start, first == null && created);
    }

    public static string RevokedId(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"tdbs-revoca\n{id}"))).ToLowerInvariant();
    public HashSet<string> Revoked()
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Marker("revocate.json"))).ToHashSet(); } catch { return new(); }
    }

    public LicenceState Status(DateTime? at = null)
    {
        var real = at ?? Now(); var now = at ?? Clock();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, Rome));
        var late = now - real > TimeSpan.FromDays(1);
        var (data, reason) = Installed();
        if (data != null)
        {
            reason = Problem(data, today);
            if (reason == "" && Revoked().Contains(RevokedId(data.Id))) reason = $"La licenza {data.Id} non vale più: è stata revocata o sostituita da chi te l'ha venduta. Chiedigli quella nuova.";
            if (reason == "") return new LicenceState { Kind = "licenza", Attivita = data.Attivita, Postazioni = data.Postazioni, Licence = data, Giorni = data.Scadenza.HasValue ? data.Scadenza.Value.DayNumber - today.DayNumber : null };
        }
        var start = TrialStart();
        var end = start?.AddDays(TRIAL_DAYS);
        if (end.HasValue && now < end)
            return new LicenceState { Kind = "prova", Attivita = 1, Postazioni = 2, Fine = end, Giorni = (int)Math.Ceiling((end.Value - now).TotalDays), Problema = reason };
        if (reason == "") reason = start.HasValue ? $"La prova di {TRIAL_DAYS} giorni è finita: per continuare serve la licenza." : "Per usare il programma inserisci la licenza o attiva la prova.";
        if (late) reason += $" La data di questo PC è indietro: il programma conta dall'ultimo uso, il {TimeZoneInfo.ConvertTimeFromUtc(now, Rome):dd/MM/yyyy}.";
        return new LicenceState { Kind = "bloccato", Attivita = data?.Attivita ?? 1, Postazioni = data?.Postazioni ?? 2, Problema = reason, ProvaUsata = start.HasValue };
    }

    LicenceState applied; DateTime appliedAt;
    /// <summary>The state, checked again once a minute and after every change.</summary>
    public LicenceState Apply(bool force = false)
    {
        if (!force && applied != null && DateTime.UtcNow - appliedAt < TimeSpan.FromMinutes(1)) return applied;
        applied = Status(); appliedAt = DateTime.UtcNow;
        return applied;
    }

    /// <summary>Check a pasted licence against this installation and make it the active one: (data, workstations over).</summary>
    public (Licence, int) Install(string text)
    {
        var d = Parse(text);
        var reason = Problem(d, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Clock(), Rome)));
        if (reason != "") throw new ArgumentException(reason);
        using var s = db.Global();
        var businesses = s.Scalar<long>("SELECT count(*) FROM core_tenant");
        if (businesses > d.Attivita) throw new ArgumentException($"Qui ci sono {businesses} attività e questa licenza ne consente {d.Attivita}: serve una licenza con più attività.");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(LicencePath + ".tmp", string.Concat(text.Where(ch => !char.IsWhiteSpace(ch))) + "\n");
        File.Move(LicencePath + ".tmp", LicencePath, true);
        Apply(true);
        var paired = s.Scalar<long>("SELECT count(*) FROM tdwin_postazione WHERE token IS NOT NULL");
        return (d, paired > d.Postazioni ? (int)(paired - d.Postazioni) : 0);
    }

    static byte[] CodeDigest(string code, string purpose) => SHA256.HashData(Encoding.UTF8.GetBytes($"tdbs-{purpose}\n{Normalize(code)}"));

    /// <summary>The published state applied here: what happened, in words, or "" when nothing changed.</summary>
    public async Task<string> CheckOnline(Func<string, Task<string>> fetch = null)
    {
        if (string.IsNullOrEmpty(StateUrl)) return "";
        fetch ??= async url => { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; h.DefaultRequestHeaders.UserAgent.ParseAdd("TD-Booking-Suite"); return await h.GetStringAsync(url); };
        var text = string.Concat((await fetch(StateUrl)).Where(ch => !char.IsWhiteSpace(ch)));
        if (!text.StartsWith(STATE_PREFIX)) throw new FormatException("Lo stato delle licenze online non è leggibile.");
        JsonObject raw;
        try { raw = Signed(text, STATE_PREFIX); }
        catch (KeyNotFoundException) { return ""; }
        catch (Tampered) { throw; }
        catch (Exception) { throw new FormatException("Lo stato delle licenze online non è firmato da chi vende il programma: lo ignoro."); }
        if (!DateTimeOffset.TryParse(raw.Str("emesso"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issuedO)) throw new FormatException("Lo stato delle licenze online non è leggibile.");
        var issued = issuedO.UtcDateTime;
        var last = ReadTime("stato-licenze.txt");
        if (last.HasValue && issued < last) return "";
        var said = ""; var code = InstallCode(); var (data, _) = Installed();
        var entry = raw["installazioni"]?[Convert.ToHexString(CodeDigest(code, "installazione")).ToLowerInvariant()] as JsonObject;
        if (entry != null && entry.Str("licenza") != "")
        {
            Licence nw = null; string licence = null;
            try
            {
                var ct = UnB64(entry.Str("licenza")); var nonce = UnB64(entry.Str("nonce"));
                var plain = new byte[ct.Length - 16];
                using var gcm = new AesGcm(CodeDigest(code, "licenza"), 16);
                gcm.Decrypt(nonce, ct[..^16], ct[^16..], plain, Encoding.ASCII.GetBytes("tdbs-stato"));
                licence = Encoding.UTF8.GetString(plain); nw = Parse(licence);
            }
            catch { nw = null; }
            if (nw != null && Normalize(nw.Installazione) == Normalize(code) && (data == null || nw.Id != data.Id))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(LicencePath + ".tmp", licence + "\n"); File.Move(LicencePath + ".tmp", LicencePath, true);
                said = $"Arrivata da chi vende il programma la licenza {nw.Id}" + (nw.Scadenza.HasValue ? $", fino al {nw.Scadenza:dd/MM/yyyy}" : ", senza scadenza") + ".";
            }
        }
        var ids = (raw["revocate"] as JsonArray ?? new()).Select(x => x?.ToString() ?? "").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var before = Revoked();
        try { Directory.CreateDirectory(Folder); File.WriteAllText(Marker("revocate.tmp"), JsonSerializer.Serialize(ids)); File.Move(Marker("revocate.tmp"), Marker("revocate.json"), true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        (data, _) = Installed();
        if (data != null && ids.Except(before).Contains(RevokedId(data.Id)) && said == "") said = $"La licenza {data.Id} è stata revocata da chi vende il programma.";
        WriteTime("stato-licenze.txt", issued); WriteTime("controllo-online.txt", Now());
        Apply(true);
        return said;
    }
    public DateTime? LastCheck() => ReadTime("controllo-online.txt");
}
