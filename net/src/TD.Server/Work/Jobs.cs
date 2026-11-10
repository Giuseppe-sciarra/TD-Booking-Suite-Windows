using TD.Core.Domain;
using TD.Server.Rpc;

namespace TD.Server.Work;

/// <summary>The jobs of mail_worker.py with their periods: the outbox and the waiting list every 10 seconds, the site
/// every 2 minutes, reminders, evening list and SMS receipts every 5, turns and retention every hour.</summary>
public static partial class Jobs
{
    static partial void Add(List<Job> list, Server s, ILogger log)
    {
        // the public address follows Rete e certificato within a minute
        WorkerJobs W() => new(s.Db, s.Crypto, s.PublicOrigin, Outbox.Instance);
        Job Make(string name, int first, int every, Action run) => new(name, TimeSpan.FromSeconds(first), TimeSpan.FromSeconds(every), async _ => { await Task.Run(run); return null; });
        list.Add(Make("coda", 3, 10, () =>
        {
            _ = s.Edition;  // read again every minute: the contexts of every job follow the licence
            if (W().Waiting() > 0) log.LogInformation("Avvisi della lista d'attesa accodati.");
            var r = W().Outgoing(); if (r.Values.Any(v => v > 0)) log.LogInformation("Invii: {Esito}", string.Join(", ", r.Select(x => $"{x.Key} {x.Value}")));
        }));
        list.Add(Make("sito", 5, WpSite.EVERY, () => { var n = WpSite.Run(s.Db, s.Crypto, s.PublicOrigin, Outbox.Instance); if (n > 0) log.LogInformation("Prenotazioni dal sito: {N}.", n); }));
        list.Add(Make("promemoria", 20, 300, () =>
        {
            if (W().Remind() > 0) log.LogInformation("Messaggi accodati ai clienti.");
            if (W().Evening() > 0) log.LogInformation("Riepiloghi serali accodati.");
        }));
        list.Add(Make("richieste", 30, 60, () => { if (Portale.Notify(s) > 0) log.LogInformation("Richieste di prova inviate all'amministratore."); }));
        // feeds.refresh, gcal.refresh, mscal.refresh: the operators' outside calendars, each when it is due
        list.Add(Make("calendari", 50, 300, () => { var n = OutsideCalendar.Refresh(s.Db, s.Crypto, s.PublicOrigin, Outbox.Instance, s.Edition); if (n > 0) log.LogInformation("Calendari degli operatori allineati: {N}.", n); }));
        list.Add(Make("esiti", 40, 300, () => { if (W().Receipts() > 0) log.LogInformation("Esiti degli SMS letti."); }));
        // tdwin_worker: the licence's state online once a day (every hour while offline), the new versions the same
        list.Add(new Job("licenza", TimeSpan.FromSeconds(90), TimeSpan.FromDays(1), async _ =>
        {
            try { var said = await s.Licensing.CheckOnline(); if (said != "") log.LogInformation("Licenza: {Esito}", said); s.Licensing.Apply(true); return null; }
            catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException) { return TimeSpan.FromHours(1); }
        }));
        list.Add(new Job("aggiornamenti", TimeSpan.FromSeconds(120), TimeSpan.FromDays(1), async _ =>
        {
            try { if (Updates.Get(s).Modo != "spento") await Updates.Check(s); return null; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) { return TimeSpan.FromHours(1); }
        }));
        // tdwin backup.loop: destination tests, "Esegui ora" and the nightly backup
        var backup = new Backup(s); backup.CleanWork();
        list.Add(Make("backup", 15, 15, () => backup.Tick(log)));
        list.Add(Make("ora", 60, 3600, () =>
        {
            W().Turns(); ClientImport.Purge();  // copies left by an import never finished
            // stays.run: the portals' calendars read again, Alloggiati Web sent and its receipts
            var read = Stays.Run(s.Db, s.Crypto, s.PublicOrigin, Outbox.Instance, s.Edition); if (read > 0) log.LogInformation("Calendari dei portali letti: {N}.", read);
            var done = W().Retention((text, ex) => log.LogWarning(ex, "{Text}", text)); if (done > 0) log.LogInformation("Conservazione dei dati applicata a {N} attività.", done);
        }));
    }
}
