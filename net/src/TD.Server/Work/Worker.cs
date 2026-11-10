using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Work;

/// <summary>The background work, one loop per job as the Python worker's threads: email and messages out, reminders,
/// the WordPress site, the waiting list, backups, the licence online, retention.</summary>
public sealed class Worker(Server s, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        WpSync.Install();
        WpSite.Face = Faces.Small;
        var jobs = Jobs.All(s, log).Select(j => Loop(j, stop)).ToList();
        await Task.WhenAll(jobs);
    }

    async Task Loop(Job job, CancellationToken stop)
    {
        try { await Task.Delay(job.First, stop); } catch (TaskCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var wait = job.Every;
            try { wait = await job.Run(stop) ?? job.Every; }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "{Job}", job.Name); }
            try { await Task.Delay(wait, stop); } catch (TaskCanceledException) { return; }
        }
    }
}

/// <summary>A job of the worker: its first run after the start, how often, what it does (null keeps the period).</summary>
public sealed record Job(string Name, TimeSpan First, TimeSpan Every, Func<CancellationToken, Task<TimeSpan?>> Run);

public static partial class Jobs
{
    public static IEnumerable<Job> All(Server s, ILogger log)
    {
        var list = new List<Job>();
        Add(list, s, log);
        return list;
    }
    static partial void Add(List<Job> list, Server s, ILogger log);
}

/// <summary>wpsync.face: the operator's photo as a 128 px JPEG data address for the site's agenda, or empty.</summary>
public static class Faces
{
    public static string Small(byte[] data)
    {
        return OperatingSystem.IsWindows() ? Resize(data) : "";  // GDI+ is the program's own platform; elsewhere (the tests) no photo
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string Resize(byte[] data)
    {
        try
        {
            using var src = System.Drawing.Image.FromStream(new MemoryStream(data));
            var k = Math.Min(1, 128.0 / Math.Max(src.Width, src.Height));
            using var small = new System.Drawing.Bitmap(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var quality = new System.Drawing.Imaging.EncoderParameters(1); quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            using var ms = new MemoryStream(); small.Save(ms, jpeg, quality);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException) { return ""; }
    }
}
