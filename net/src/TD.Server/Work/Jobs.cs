using TD.Core.Domain;
using TD.Server.Rpc;

namespace TD.Server.Work;

/// <summary>The jobs of mail_worker.py with their periods: the outbox and the waiting list every 10 seconds, the site
/// every 2 minutes, reminders, evening list and SMS receipts every 5, turns and retention every hour.</summary>
public static partial class Jobs
{
    static partial void Add(List<Job> list, Server s, ILogger log)
    {
        var w = new WorkerJobs(s.Db, s.Crypto, s.Config.Origin, Outbox.Instance);
        Job Make(string name, int first, int every, Action run) => new(name, TimeSpan.FromSeconds(first), TimeSpan.FromSeconds(every), async _ => { await Task.Run(run); return null; });
        list.Add(Make("coda", 3, 10, () =>
        {
            if (w.Waiting() > 0) log.LogInformation("Avvisi della lista d'attesa accodati.");
            var r = w.Outgoing(); if (r.Values.Any(v => v > 0)) log.LogInformation("Invii: {Esito}", string.Join(", ", r.Select(x => $"{x.Key} {x.Value}")));
        }));
        list.Add(Make("sito", 5, WpSite.EVERY, () => { var n = WpSite.Run(s.Db, s.Crypto, s.Config.Origin, Outbox.Instance); if (n > 0) log.LogInformation("Prenotazioni dal sito: {N}.", n); }));
        list.Add(Make("promemoria", 20, 300, () =>
        {
            if (w.Remind() > 0) log.LogInformation("Messaggi accodati ai clienti.");
            if (w.Evening() > 0) log.LogInformation("Riepiloghi serali accodati.");
        }));
        list.Add(Make("esiti", 40, 300, () => { if (w.Receipts() > 0) log.LogInformation("Esiti degli SMS letti."); }));
        list.Add(Make("ora", 60, 3600, () =>
        {
            w.Turns();
            var done = w.Retention((text, ex) => log.LogWarning(ex, "{Text}", text)); if (done > 0) log.LogInformation("Conservazione dei dati applicata a {N} attività.", done);
        }));
    }
}
