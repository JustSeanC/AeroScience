namespace AeroScience.Engine;

public sealed class FileLogProvider(string directory) : ILoggerProvider
{
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLog(this, categoryName);
    public void Dispose() { }

    private void Write(string text)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"engine-{DateTime.UtcNow:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) return;
                File.AppendAllText(path, text + Environment.NewLine);
                foreach (var old in Directory.GetFiles(directory, "engine-*.log").OrderDescending().Skip(7)) File.Delete(old);
            }
            catch (IOException) { /* Console logging remains available when a log file is locked/full. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLog(FileLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) provider.Write($"{DateTimeOffset.UtcNow:O} {level} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
