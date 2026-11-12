using MonitorRFC.Contracts;
using MonitorRFC.Domain;
namespace MonitorRFC.Application;

public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset Now => DateTimeOffset.Now; }
public interface IPublicationRepository
{
    Task<ImportResult?> CheckUnchangedByDateAsync(string sourceKey, string externalKey, DateOnly? sourceDate, string url, CancellationToken ct);
    Task<ImportResult> ImportAsync(SourceDocument document, IAsyncEnumerable<SourceRow> rows, CancellationToken ct);
    Task<long> PublishGenerationAsync(CancellationToken ct);
    Task SaveExecutionAsync(ExecutionReport report, CancellationToken ct);
    Task<DateOnly?> LastCompleteDofDateAsync(CancellationToken ct);
    Task CompleteDofDayAsync(DateOnly date, bool complete, string? detail, CancellationToken ct);
}
public interface IRfcQueryService
{
    Task<QueryHeader?> GetHeaderAsync(CancellationToken ct);
    IAsyncEnumerable<Appearance> ReadAsync(long generation, QueryFilter filter, string[]? rfcs, CancellationToken ct);
}
public interface IExecutionNotifier
{
    Task EnqueueAsync(ExecutionReport report, CancellationToken ct);
    Task DispatchAsync(CancellationToken ct);
}
