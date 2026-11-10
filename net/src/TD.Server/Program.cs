using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TD.Core;
using TD.Core.Data;
using TD.Core.Win;
using TD.Server;
using TD.Server.Rpc;
using TD.Server.Web;
using TD.Server.Work;

// TDBookingServer.exe            the program in HTTPS (service TDBookingWeb)
// TDBookingServer.exe worker     email, reminders, site, backups, licence (service TDBookingWorker)
// TDBookingServer.exe migrate    tables and rights, with db-admin.json (the worker does it at every start too)
var mode = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "web";
var folder = Environment.GetEnvironmentVariable("TDBS_DATA") ?? AppConfig.DefaultFolder;
var config = AppConfig.Load(folder);
var admin = Admin.Read(folder);

if (mode == "migrate")
{
    Console.WriteLine("Migrazioni: " + string.Join(", ", Migrations.Apply(AppConfig.ToNpgsql(admin.Url), admin.AppPassword)));
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(o => o.ServiceName = mode == "worker" ? "TDBookingWorker" : "TDBookingWeb");
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
var app0 = new Server(config, admin);
builder.Services.AddSingleton(app0);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping; });

if (mode == "worker")
{
    if (admin != null) Migrations.Apply(AppConfig.ToNpgsql(admin.Url), admin.AppPassword);
    builder.Services.AddHostedService<Worker>();
    builder.WebHost.UseUrls("http://127.0.0.1:0");  // nothing served: the host only runs the jobs
    var w = builder.Build();
    await w.RunAsync();
    return;
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    var ip = System.Net.IPAddress.Parse(config.Listen == "0.0.0.0" ? "0.0.0.0" : config.Listen);
    k.Listen(ip, config.Port, l => l.UseHttps(h => h.ServerCertificateSelector = (_, _) => app0.Certificate()));
});
var app = builder.Build();
app.Use(Gate.Handle);
Auth.Map(app);
RpcHost.Map(app);
Public.Map(app);
TD.Server.Rpc.InvioEmail.Map(app);
app.MapGet("/api/info", (Server s, HttpContext h) => Results.Json(s.Info(h)));
await app.RunAsync();
