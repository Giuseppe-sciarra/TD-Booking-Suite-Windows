namespace TD.Server;

/// <summary>What the services say, in the data folder's log (TDBookingWeb.log, TDBookingWorker.log): a Windows service
/// has no console, and Stato del programma sends these files to who helps. At 10 MB the file becomes .1.log.</summary>
public sealed class FileLog(string path) : ILoggerProvider
{
    readonly object gate = new();
    public ILogger CreateLogger(string category) => new Writer(this, category);
    public void Dispose() { }
    void Write(string line)
    {
        lock (gate)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 10_000_000) File.Move(path, Path.ChangeExtension(path, ".1.log"), true);  // shortcut: one old file kept
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }  // a log that cannot be written never stops the program
    }
    sealed class Writer(FileLog log, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> format)
        {
            if (!IsEnabled(level)) return;
            log.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level.ToString().ToLowerInvariant()[..4]} {category}: {format(state, exception)}" + (exception != null ? Environment.NewLine + exception : ""));
        }
    }
}
