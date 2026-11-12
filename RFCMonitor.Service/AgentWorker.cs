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
                        state.Next = ScheduleCalculator.Next(options.Schedule, DateTimeOffset.Now); Persist(); break;
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
        logger.LogInformation("Inicializando Worker. Configuración={ConfigPath}", configuration.FilePath);
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
                state.Next ??= ScheduleCalculator.Next(options.Schedule, DateTimeOffset.Now);
                if (options.Schedule.RunOnStartup) { state.Queued = true; state.Trigger = "arranque"; }
                logger.LogInformation("Configuración cargada. SAT={SatEnabled}; DOF={DofEnabled}; programación={Mode}; próxima={NextRun}; datos={DataDirectory}",
                    options.SatEnabled, options.DofEnabled, options.Schedule.Mode, state.Next, options.DataDirectory);
            }
            catch (Exception ex) { error = SafeError.Describe(ex); logger.LogError(ex, "No se pudo cargar la configuración del Worker."); }
            Persist();
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            AgentOptions? runOptions = null; string trigger = "manual";
            lock (gate)
            {
                if (options is not null)
                {
                    if (options.Schedule.Enabled && state.Next <= DateTimeOffset.Now)
                    { state.Queued = true; state.Trigger = "programada"; logger.LogInformation("Ejecución programada encolada."); }
                    if (state.Queued)
                    {
                        runOptions = options; trigger = state.Trigger;
                        state.Queued = false; state.Running = true;
                        state.Next = ScheduleCalculator.Next(options.Schedule, DateTimeOffset.Now);
                        Persist();
                    }
                }
            }
            if (runOptions is not null) await RunCycleAsync(runOptions, trigger, stoppingToken);
            try { await outbox.DispatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo despachar un correo pendiente."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task RunCycleAsync(AgentOptions settings, string trigger, CancellationToken ct)
    {
        var report = new ExecutionReport { Disparador = trigger };
        using var scope = logger.BeginScope("EjecucionId={EjecucionId}", report.EjecucionId);
        var repo = new SqlRepository(AgentConfiguration.GetConnection(settings), logger);
        try
        {
            logger.LogInformation("Inicio de ejecución. Disparador={Trigger}; SAT={SatEnabled}; DOF={DofEnabled}", trigger, settings.SatEnabled, settings.DofEnabled);
            await using var lease = await repo.AcquireLeaseAsync(ct);
            if (lease is null) throw new InvalidDataException("Otra instancia mantiene el bloqueo de ingesta; reintente.");
            logger.LogInformation("Bloqueo de ingesta adquirido; validando catálogos SQL.");
            await repo.EnsureCatalogAsync(ct, settings);
            logger.LogInformation("Catálogos SQL listos.");
            using var downloader = new SafeDownloader();
            if (settings.SatEnabled)
            {
                try
                {
                    logger.LogInformation("Iniciando recolección SAT.");
                    var results = await new SatCollector(downloader, repo, logger).RunAsync(settings, ct);
                    report.Fuentes.AddRange(results);
                    foreach (var source in results.Where(x => x.Resultado is "fallida" or "requiere-revision" || (x.Rechazados ?? 0) > 0))
                        logger.LogWarning("Fuente {Source} terminó como {Result}: {Detail}", source.Fuente, source.Resultado, source.Error);
                    logger.LogInformation("SAT finalizado. Fuentes={Sources}; registros={Records}; fallidas={Failed}", results.Count, results.Sum(x => x.Registros), results.Count(x => x.Resultado == "fallida"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "La recolección SAT terminó con error.");
                    foreach (var listing in MonitorRFC.Domain.SourceCatalog.Sat.Where(l => settings.EnabledSatSources.Length == 0 || settings.EnabledSatSources.Contains(l.Key)))
                        report.Fuentes.Add(new(listing.Key, "fallida", 0, SafeError.Describe(ex)));
                }
            }
            if (settings.DofEnabled)
            {
                try
                {
                    logger.LogInformation("Iniciando recolección DOF desde {DofStartDate}.", settings.DofStartDate);
                    var results = await new DofCollector(downloader, repo, logger).RunAsync(settings, ct);
                    report.Fuentes.AddRange(results);
                    logger.LogInformation("DOF finalizado. Fuentes={Sources}; registros={Records}; fallidas={Failed}", results.Count, results.Sum(x => x.Registros), results.Count(x => x.Resultado == "fallida"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "La recolección DOF terminó con error."); report.Fuentes.Add(new("dof:publicaciones", "fallida", 0, SafeError.Describe(ex))); }
            }
            var hasActiveGeneration = (await repo.GetHeaderAsync(ct)) is not null;
            var anyChanges = report.Fuentes.Any(r => r.Resultado == "cambios");
            if (report.Fuentes.Any(r => r.Resultado is "cambios" or "sin-cambios") && (anyChanges || !hasActiveGeneration))
            {
                logger.LogInformation("Publicando nueva generación de datos.");
                await repo.PublishGenerationAsync(ct);
            }
            report.Resultado = Outcome(report.Fuentes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Resultado = "cancelada"; logger.LogWarning("Ejecución cancelada por detención del servicio."); }
        catch (Exception ex)
        {
            report.Fuentes.Add(new("sistema", "fallida", 0, SafeError.Describe(ex)));
            report.Resultado = Outcome(report.Fuentes);
            logger.LogError(ex, "Ciclo fallido.");
        }
        finally
        {
            report.Fin = DateTimeOffset.Now;
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var history = Path.Combine(settings.DataDirectory, "executions", report.EjecucionId.ToString("N") + ".json");
            AgentConfiguration.AtomicWrite(history, JsonSerializer.Serialize(report, AgentConfiguration.Json));
            try { await repo.SaveExecutionAsync(report, finish.Token); }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo guardar el resumen en SQL; se conservó localmente en {HistoryPath}.", history); }
            try { await outbox.EnqueueAsync(report, finish.Token); }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo encolar el correo de resumen."); }
            lock (gate)
            {
                state.Last = report; state.Running = false;
                if (report.Resultado == "cancelada") { state.Queued = true; state.Trigger = "recuperacion"; }
                Persist();
            }
            logger.LogInformation("Resultado {Resultado}; fuentes {Fuentes}; registros {Registros}", report.Resultado, report.Fuentes.Count, report.Fuentes.Sum(f => f.Registros));
        }
    }
    public static string Outcome(IReadOnlyList<SourceResult> rows) => rows.Any(r => r.Resultado is "fallida" or "requiere-revision")
        ? rows.Any(r => r.Resultado is "cambios" or "sin-cambios") ? "parcial" : "fallida"
        : rows.Any(r => r.Resultado == "cambios") ? "cambios" : "sin-cambios";
}
