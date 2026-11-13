using System.Text.Json;
using System.Threading.Channels;
using MonitorRFC.Infrastructure.Persistence;
using MonitorRFC.Infrastructure.Sources;

namespace MonitorRFC.Infrastructure.Jobs;

public sealed record DofJobRequest(Guid Id, DateOnly Inicio, DateOnly Fin, string? Articulo);

/// <summary>Cola en memoria de trabajos de sincronización DOF (un solo consumidor: se procesan de uno en uno).</summary>
public sealed class DofJobQueue
{
    private readonly Channel<DofJobRequest> channel = Channel.CreateUnbounded<DofJobRequest>(new UnboundedChannelOptions { SingleReader = true });
    public ValueTask EnqueueAsync(DofJobRequest request, CancellationToken ct = default) => channel.Writer.WriteAsync(request, ct);
    public IAsyncEnumerable<DofJobRequest> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Ejecuta en segundo plano, dentro de la propia API, las sincronizaciones DOF solicitadas. Es independiente del Worker.
/// El estado y el avance se guardan en dbo.TrabajoDOF para poder consultarlos incluso tras un reinicio.
/// </summary>
public sealed class DofJobService(DofJobQueue queue, IServiceScopeFactory scopes, SafeDownloader downloader,
    IConfiguration configuration, ILogger<DofJobService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<SqlRepository>();
            await repo.EnsureJobTableAsync(stoppingToken);
            var interrupted = await repo.MarkInterruptedJobsAsync(stoppingToken);
            if (interrupted > 0) logger.LogWarning("[DOF-Job] {Count} trabajo(s) quedaron interrumpidos por el reinicio anterior y se marcaron como 'interrumpido'.", interrupted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex) { logger.LogError(ex, "[DOF-Job] No se pudo preparar la tabla de trabajos DOF; las solicitudes asíncronas fallarán hasta corregirlo."); }

        try
        {
            await foreach (var request in queue.ReadAllAsync(stoppingToken))
                await RunAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunAsync(DofJobRequest request, CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<SqlRepository>();
        var total = request.Fin.DayNumber - request.Inicio.DayNumber + 1;
        logger.LogInformation("[DOF-Job {Id}] Iniciando {Inicio:yyyy-MM-dd}..{Fin:yyyy-MM-dd}; artículo={Articulo}", request.Id, request.Inicio, request.Fin, request.Articulo ?? "todos");
        try
        {
            await repo.UpdateJobAsync(request.Id, "en_curso", 0, total, "Iniciando.", null, null, stoppingToken);
            var options = new Contracts.AgentOptions { DataDirectory = ResolveWritableDataDirectory(configuration["Api:DataDirectory"]) };
            var response = await repo.SincronizarDofPeriodoAsync(request.Inicio, request.Fin, request.Articulo, downloader, options, stoppingToken,
                (dia, totalDias, mensaje) => repo.UpdateJobAsync(request.Id, "en_curso", dia, totalDias, mensaje, null, null, stoppingToken));
            await repo.UpdateJobAsync(request.Id, "completado", total, total, response.Mensaje, JsonSerializer.Serialize(response, Json), null, CancellationToken.None);
            logger.LogInformation("[DOF-Job {Id}] Completado: estado={Estado}, filas={Filas}.", request.Id, response.Estado, response.TotalRegistrosProcesados);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("[DOF-Job {Id}] Interrumpido por el apagado de la API.", request.Id);
            try { await repo.UpdateJobAsync(request.Id, "interrumpido", 0, total, "La API se detuvo antes de terminar; vuelva a lanzar el mismo rango.", null, null, CancellationToken.None); } catch { /* se marcará al arrancar */ }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[DOF-Job {Id}] Falló.", request.Id);
            try { await repo.UpdateJobAsync(request.Id, "error", 0, total, "La sincronización falló.", null, SafeError.Describe(ex), CancellationToken.None); }
            catch (Exception inner) { logger.LogError(inner, "[DOF-Job {Id}] No se pudo registrar el error.", request.Id); }
        }
    }

    /// <summary>
    /// La API es independiente del Worker: nunca usa carpetas del servicio. Guarda su evidencia DOF en "Api:DataDirectory"
    /// (si se configura) o en "data" junto a la API, con respaldo en la carpeta temporal. Se prueba escritura real.
    /// </summary>
    public static string ResolveWritableDataDirectory(string? configured)
    {
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "data") : configured,
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Path.GetTempPath(), "MonitorRFC")
        };
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "evidence"));
                var probe = Path.Combine(dir, "evidence", Guid.NewGuid().ToString("N") + ".probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return dir;
            }
            catch { /* probar la siguiente carpeta */ }
        }
        throw new InvalidOperationException("No hay una carpeta con permiso de escritura para guardar la evidencia de DOF.");
    }
}
