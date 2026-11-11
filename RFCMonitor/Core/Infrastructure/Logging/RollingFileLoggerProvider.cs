using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Globalization;

namespace MonitorRFC.Infrastructure.Logging;

/// <summary>Proveedor local sin dependencias externas. Un archivo por día, en UTF-8.</summary>
public sealed class RollingFileLoggerProvider(Func<string> directory, string prefix) : ILoggerProvider
{
    private readonly object sync = new();
    private DateOnly? maintainedDay;
    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(categoryName, Write);
    public void Dispose() { }

    private void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        try
        {
            lock (sync)
            {
                var directoryPath = directory();
                Directory.CreateDirectory(directoryPath);
                var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
                if (maintainedDay != utcToday)
                {
                    Maintain(directoryPath, utcToday);
                    maintainedDay = utcToday;
                }
                var path = Path.Combine(directoryPath, $"{prefix}-{utcToday:yyyyMMdd}.log");
                var detail = exception is null ? "" : Environment.NewLine + exception;
                var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category} ({eventId.Id}) {message}{detail}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // El log nunca debe detener al API ni al servicio.
        }
    }

    private void Maintain(string directoryPath, DateOnly today)
    {
        // Se invoca dentro del candado de escritura: el archivo del día anterior ya no recibirá líneas.
        foreach (var logPath in Directory.EnumerateFiles(directoryPath, $"{prefix}-*.log"))
        {
            if (!TryGetDate(logPath, out var day) || day >= today) continue;
            var zipPath = Path.ChangeExtension(logPath, ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                archive.CreateEntryFromFile(logPath, Path.GetFileName(logPath), CompressionLevel.Optimal);
            File.Delete(logPath);
        }
        var oldest = today.AddDays(-14); // hoy y los 14 días anteriores: máximo 15 días.
        foreach (var path in Directory.EnumerateFiles(directoryPath, $"{prefix}-*.*"))
            if (TryGetDate(path, out var day) && day < oldest) File.Delete(path);
    }

    private static bool TryGetDate(string path, out DateOnly day)
    {
        day = default;
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash >= 0 && DateOnly.TryParseExact(name[(dash + 1)..], "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);
    }

    private sealed class RollingFileLogger(string category, Action<LogLevel, string, EventId, string, Exception?> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => write(logLevel, category, eventId, formatter(state, exception), exception);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
