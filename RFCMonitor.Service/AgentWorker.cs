using System.Text.Json;
using MonitorRFC.Contracts;
using MonitorRFC.Infrastructure.Configuration;
using MonitorRFC.Infrastructure.Notifications;
using MonitorRFC.Infrastructure.Persistence;
using MonitorRFC.Infrastructure.Scheduling;
using MonitorRFC.Infrastructure.Sources;
namespace MonitorRFC.Service;

public sealed class AgentWorker(AgentConfiguration configuration, EmailOutbox outbox, ILogger<AgentWorker> logger) : BackgroundService
{
    public sealed class State
    {
        public bool Queued { get; set; }
        public bool Running { get; set; }
        public string Trigger { get; set; } = "manual";
        public DateTimeOffset? Next { get; set; }
        public ExecutionReport? Last { get; set; }
    }
    private readonly object gate = new();
    private State state = new();
    private AgentOptions? options;
    private string? error;
    private string StatePath => Path.Combine(Path.GetDirectoryName(configuration.FilePath)!, "agent-state.json");
    private void Persist() => AgentConfiguration.AtomicWrite(StatePath, JsonSerializer.Serialize(state, AgentConfiguration.Json));
    public AgentReply Command(string action)
    {
        lock (gate)
        {
            try
            {
                switch (action)
                {
                    case "status": break;
                    case "run": state.Queued = true; state.Trigger = "manual"; Persist(); break;
                    case "reload":
                        var updated = configuration.Load();
                        if (state.Running) return new(false, "Hay una ejecución activa; guarde y recargue al finalizar.");
                        options = updated; error = null;
                        state.Next = ScheduleCalculator.Next(options.Schedule, DateTimeOffset.UtcNow); Persist(); break;
                    default: return new(false, "Comando desconocido.");
                }
                int pending;
                try { pending = outbox.PendingCount(); } catch { pending = -1; }
                return new(true, action == "run" ? "Ejecución encolada." : "Estado consultado.", new(state.Running, state.Next, state.Last, pending, error));
            }
            catch (Exception ex) { error = SafeError.Describe(ex); return new(false, error); }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.GetDirectoryName(configuration.FilePath)!;
        AgentConfiguration.SecureDirectory(root);
        // FileShare.None persists exclusive ownership for the lifetime of this process and releases on crash.
        using var localLock = new FileStream(Path.Combine(root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        lock (gate)
        {
            if (File.Exists(StatePath)) state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), AgentConfiguration.Json) ?? new();
            if (state.Running) { state.Queued = true; state.Trigger = "recuperacion"; state.Running = false; }
            try
            {
                options = configuration.Load();
                AgentConfiguration.SecureDirectory(options.DataDirectory);
                state.Next ??= ScheduleCalculator.Next(options.Schedule, DateTimeOffset.UtcNow);
                if (options.Schedule.RunOnStartup) { state.Queued = true; state.Trigger = "arranque"; }
            }
            catch (Exception ex) { error = SafeError.Describe(ex); }
            Persist();
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            AgentOptions? runOptions = null; string trigger = "manual";
            lock (gate)
            {
                if (options is not null)
                {
                    if (options.Schedule.Enabled && state.Next <= DateTimeOffset.UtcNow)
                    { state.Queued = true; state.Trigger = "programada"; }
                    if (state.Queued)
                    {
                        runOptions = options; trigger = state.Trigger;
                        state.Queued = false; state.Running = true;
                        state.Next = ScheduleCalculator.Next(options.Schedule, DateTimeOffset.UtcNow);
                        Persist();
                    }
                }
            }
            if (runOptions is not null) await RunCycleAsync(runOptions, trigger, stoppingToken);
            try { await outbox.DispatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Correo pendiente: {Tipo}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task RunCycleAsync(AgentOptions settings, string trigger, CancellationToken ct)
    {
        var report = new ExecutionReport { Disparador = trigger };
        using var scope = logger.BeginScope("EjecucionId={EjecucionId}", report.EjecucionId);
        var repo = new SqlRepository(AgentConfiguration.GetConnection(settings));
        try
        {
            await using var lease = await repo.AcquireLeaseAsync(ct);
            if (lease is null) throw new InvalidDataException("Otra instancia mantiene el bloqueo de ingesta; reintente.");
            await repo.EnsureCatalogAsync(ct, settings);
            using var downloader = new SafeDownloader();
            if (settings.SatEnabled)
            {
                try { report.Fuentes.AddRange(await new SatCollector(downloader, repo).RunAsync(settings, ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    foreach (var listing in MonitorRFC.Domain.SourceCatalog.Sat.Where(l => settings.EnabledSatSources.Length == 0 || settings.EnabledSatSources.Contains(l.Key)))
                        report.Fuentes.Add(new(listing.Key, "fallida", 0, SafeError.Describe(ex)));
                }
            }
            if (settings.DofEnabled)
            {
                try { report.Fuentes.AddRange(await new DofCollector(downloader, repo).RunAsync(settings, ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { report.Fuentes.Add(new("dof:publicaciones", "fallida", 0, SafeError.Describe(ex))); }
            }
            if (report.Fuentes.Any(r => r.Resultado is "cambios" or "sin-cambios")) await repo.PublishGenerationAsync(ct);
            report.Resultado = Outcome(report.Fuentes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Resultado = "cancelada"; }
        catch (Exception ex)
        {
            report.Fuentes.Add(new("sistema", "fallida", 0, SafeError.Describe(ex)));
            report.Resultado = Outcome(report.Fuentes);
            logger.LogError("Ciclo fallido: {Tipo}", ex.GetType().Name);
        }
        finally
        {
            report.FinUtc = DateTimeOffset.UtcNow;
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var history = Path.Combine(settings.DataDirectory, "executions", report.EjecucionId.ToString("N") + ".json");
            AgentConfiguration.AtomicWrite(history, JsonSerializer.Serialize(report, AgentConfiguration.Json));
            try { await repo.SaveExecutionAsync(report, finish.Token); }
            catch (Exception ex) { logger.LogWarning("Resumen conservado localmente: {Tipo}", ex.GetType().Name); }
            try { await outbox.EnqueueAsync(report, finish.Token); }
            catch (Exception ex) { logger.LogWarning("No se pudo encolar correo: {Tipo}", ex.GetType().Name); }
            lock (gate)
            {
                state.Last = report; state.Running = false;
                if (report.Resultado == "cancelada") { state.Queued = true; state.Trigger = "recuperacion"; }
                Persist();
            }
            logger.LogInformation("Resultado {Resultado}; fuentes {Fuentes}; registros {Registros}", report.Resultado, report.Fuentes.Count, report.Fuentes.Sum(f => f.Registros));
        }
    }
    public static string Outcome(IReadOnlyList<SourceResult> rows) => rows.Any(r => r.Resultado == "fallida")
        ? rows.Any(r => r.Resultado is "cambios" or "sin-cambios") ? "parcial" : "fallida"
        : rows.Any(r => r.Resultado == "cambios") ? "cambios" : "sin-cambios";
}
