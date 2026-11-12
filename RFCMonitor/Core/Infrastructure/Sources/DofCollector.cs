using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MonitorRFC.Application;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Persistence;

namespace MonitorRFC.Infrastructure.Sources;

public sealed class DofCollector(SafeDownloader downloader,SqlRepository repository, ILogger? logger = null)
{
    private const string BaseUrl="https://sidof.segob.gob.mx/dof/sidof";
    public async Task<IReadOnlyList<SourceResult>> RunAsync(AgentOptions options,CancellationToken ct)
    {
        var results=new List<SourceResult>();
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Now,Scheduling.ScheduleCalculator.Zone(options.Schedule.TimeZone)).DateTime);
        var days=await repository.PendingDofDaysAsync(options.DofStartDate,today,options.DofLookbackDays,options.DofDaysPerCycle,ct);
        foreach(var date in days)
        {
            ct.ThrowIfCancellationRequested();
            var complete=true; var changes=0; long count=0; var errors=new List<string>();
            try
            {
                var content=await downloader.TextAsync($"{BaseUrl}/notas/{date:dd-MM-yyyy}",options,ct);
                using var response=JsonDocument.Parse(content);
                if(!Success(response.RootElement))throw new InvalidDataException("SIDOF no confirmó una consulta exitosa del día.");
                var seen=new HashSet<long>();
                var foundGroups=0;
                foreach(var group in new[]{"NotasMatutinas","NotasVespertinas","NotasExtraordinarias"})
                {
                    if(!response.RootElement.TryGetProperty(group,out var notes)||notes.ValueKind!=JsonValueKind.Array)continue;
                    foundGroups++;
                    foreach(var note in notes.EnumerateArray())
                    {
                        var agency=String(note,"codOrgaDos");
                        if(!TextKey.Normalize(agency).Contains("HACIENDA"))continue;
                        if(!long.TryParse(String(note,"codNota"),out var code)||!seen.Add(code))continue;
                        try
                        {
                            var download=await downloader.FetchAsync($"{BaseUrl}/notas/nota/{code}",options,ct);
                            using var detail=JsonDocument.Parse(await File.ReadAllTextAsync(download.Path,ct));
                            if(!Success(detail.RootElement)||!detail.RootElement.TryGetProperty("Nota",out var value))
                                throw new InvalidDataException("SIDOF no devolvió el detalle de la nota.");
                            var title=String(value,"titulo");
                            var html=String(value,"cadenaContenido");
                            var dateText=String(value,"fecha");
                            if(!DateOnly.TryParseExact(dateText,"dd-MM-yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var published))
                                throw new InvalidDataException("Nota DOF con fecha inválida.");
                            var parsed=DofParser.Parse(title,html,published);
                            foreach (var attachment in parsed.Attachments.Where(_ => parsed.Relevant))
                            {
                                var url = new Uri(new Uri("https://www.dof.gob.mx/"), attachment).ToString();
                                try
                                {
                                    var annex = await downloader.FetchAsync(url, options, ct);
                                    await repository.RecordAdjunctAsync(code, url, "pendiente", annex.Path, "Anexo PDF/imagen conservado; requiere revisión de extracción.", ct);
                                }
                                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                                catch (Exception ex) { await repository.RecordAdjunctAsync(code, url, "pendiente", null, SafeError.Describe(ex), ct); }
                            }
                            if (parsed.Relevant && parsed.Rows.Count > 0)
                            {
                                var source=new SourceDocument("dof:publicaciones",code.ToString(CultureInfo.InvariantCulture),"DOF",
                                    parsed.Rows.Select(r=>r.Article).Distinct().Count()==1?parsed.Rows[0].Article:"69",
                                    "publicaciones-dof",download.Url,download.Hash,download.Path,published,"nota",DateTimeOffset.Now,"dof-v1",
                                    code,title,String(value,"codEdicion"),agency);
                                var result=await repository.ImportAsync(source,AsAsync(parsed.Rows,ct),ct);
                                if(result.Changed)changes++;
                                count+=result.Rows;
                                await repository.ClearDofPendingAsync(code,ct);
                            }
                            else if(parsed.NeedsReview)
                            {
                                complete=false;
                                await repository.SaveDofPendingAsync(code,date,parsed.Reason??"Extracción pendiente.",ct);
                                errors.Add($"Nota {code}: extracción pendiente.");
                                continue;
                            }
                            else if(!parsed.Relevant)
                            {
                                await repository.ClearDofPendingAsync(code,ct);
                                continue;
                            }
                        }
                        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
                        catch(Exception ex)
                        {
                            complete=false;
                            logger?.LogWarning(ex, "Fallo al procesar nota DOF {Code}: {Message}", code, ex.Message);
                            var error=SafeError.Describe(ex);
                            await repository.SaveDofPendingAsync(code,date,error,ct);
                            errors.Add($"Nota {code}: {error}");
                        }
                    }
                }
                if(foundGroups!=3)throw new InvalidDataException("SIDOF no devolvió las tres ediciones; día pendiente.");
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
            catch(Exception ex) {complete=false;errors.Add(SafeError.Describe(ex));}
            var description=errors.Count==0?null:string.Join(" ",errors.Take(10));
            await repository.CompleteDofDayAsync(date,complete,description,ct);
            results.Add(new("dof:"+date.ToString("yyyy-MM-dd"),!complete?"fallida":changes>0?"cambios":"sin-cambios",count,description));
        }
        if (results.Count == 0) return [];
        var failures = results.Where(r => r.Resultado == "fallida").ToArray();
        return [new("dof:publicaciones", failures.Length > 0 ? "fallida" : results.Any(r => r.Resultado == "cambios") ? "cambios" : "sin-cambios",
            results.Sum(r => r.Registros), failures.Length == 0 ? null : string.Join(" ", failures.Take(5).Select(r => r.Fuente + ": " + r.Error)))];
    }
    public static string String(JsonElement value,string key)=>value.TryGetProperty(key,out var found)&&found.ValueKind!=JsonValueKind.Null?found.ToString():"";
    private static bool Success(JsonElement value)=>String(value,"messageCode")=="200";
    private static async IAsyncEnumerable<SourceRow> AsAsync(IEnumerable<SourceRow> rows,[EnumeratorCancellation] CancellationToken ct)
    {
        foreach(var row in rows){ct.ThrowIfCancellationRequested();yield return row;}
        await Task.CompletedTask;
    }
}
