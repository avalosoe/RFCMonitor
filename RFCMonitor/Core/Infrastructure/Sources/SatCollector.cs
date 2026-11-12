using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using MonitorRFC.Application;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;
namespace MonitorRFC.Infrastructure.Sources;

public sealed class SatCollector(SafeDownloader downloader,IPublicationRepository repository, ILogger logger)
{
    public const string CatalogUrl="https://www.sat.gob.mx/minisitio/DatosAbiertos/contribuyentes_publicados.html";
    public async Task<IReadOnlyList<SourceResult>> RunAsync(AgentOptions options,CancellationToken ct)
    {
        var results=new List<SourceResult>();
        var html=await downloader.TextAsync(CatalogUrl,options,ct);
        var page=new HtmlDocument(); page.LoadHtml(html);
        var listings=SourceCatalog.Sat.Where(s=>options.EnabledSatSources.Length==0||options.EnabledSatSources.Contains(s.Key)).ToList();
        var position=0;
        logger.LogInformation("[SAT] Catálogo del portal descargado; {Total} listado(s) por evaluar.", listings.Count);
        foreach(var listing in listings)
        {
            ct.ThrowIfCancellationRequested();
            position++;
            try
            {
                logger.LogInformation("[SAT {N}/{Total}] Procesando listado {Key} ({Name})...", position, listings.Count, listing.Key, listing.Name);
                var anchors=page.DocumentNode.SelectNodes("//a[@href]")??new HtmlNodeCollection(page.DocumentNode);
                var file=Path.GetFileName(listing.RelativeUrl);
                var anchor=anchors.FirstOrDefault(a=>Uri.TryCreate(new Uri(CatalogUrl),a.GetAttributeValue("href",""),out var u)&&
                    string.Equals(Path.GetFileName(u.AbsolutePath),file,StringComparison.OrdinalIgnoreCase));
                var targetUrl = anchor is not null ? new Uri(new Uri(CatalogUrl), anchor.GetAttributeValue("href", "")).ToString() : listing.Url;

                DateOnly? date=null;
                if(anchor is not null)
                {
                    for(var parent=anchor.ParentNode;parent is not null;parent=parent.ParentNode)
                    {
                        if(parent.Name=="body"||parent.Name=="html") break;
                        var text=HtmlEntity.DeEntitize(parent.InnerText);
                        var match=Regex.Match(text,@"Información\s+actualizada\s+al\s+(\d{1,2}\s+de\s+\w+\s+de\s+\d{4})",RegexOptions.IgnoreCase);
                        if(match.Success) {date=SatParser.ParseDate(match.Groups[1].Value);break;}
                    }
                }
                date ??= DateOnly.FromDateTime(DateTime.Today);

                // Si la fecha de la página oficial no es superior a la registrada en BD (o si no hay fecha/1900),
                // evitamos descargar el archivo masivo y confirmamos sin cambios de inmediato.
                var unchanged = await repository.CheckUnchangedByDateAsync(listing.Key, listing.Key, date, targetUrl, ct);
                if (unchanged is not null)
                {
                    logger.LogInformation("Listado SAT {Key} sin cambios según fecha oficial ({Date}). Registros activos: {Rows:N0}.", listing.Key, date, unchanged.Rows);
                    results.Add(new(listing.Key, "sin-cambios", unchanged.Rows, null, unchanged.VersionId, unchanged.ReadRows, unchanged.RejectedRows, unchanged.CertifiedRows));
                    continue;
                }

                logger.LogInformation("Descargando archivo desde {Url}...", targetUrl);
                var download=await downloader.FetchAsync(targetUrl,options,ct);
                logger.LogInformation("Descarga completa ({Bytes:N0} bytes). Analizando e importando registros...", download.Bytes);
                var document=new SourceDocument(listing.Key,listing.Key,"SAT",listing.Article,listing.Slug,download.Url,
                    download.Hash,download.Path,date,"seccion",DateTimeOffset.Now,"sat-v2-rfc-normalizado");
                var rows=listing.Document ? SatParser.ReadWorkbookAsync(download.Path,ct) : SatParser.ReadAsync(listing,download.Path,ct);
                var result=await repository.ImportAsync(document,rows,ct);
                var status = result.Changed ? "cambios" : "sin-cambios";
                var issue = result.RejectedRows > 0
                    ? $"{result.RejectedRows} fila(s) rechazadas. {result.ReviewDetail ?? ""}".Trim()
                    : null;
                logger.LogInformation("Listado SAT {Key} finalizado exitosamente: estado={Status}, filas={Rows:N0}.", listing.Key, status, result.Rows);
                results.Add(new(listing.Key,status,result.Rows,issue,result.VersionId,result.ReadRows,result.RejectedRows,result.CertifiedRows));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex)
            {
                // Persist only the safe summary, but retain the exception and
                // stack trace in the protected local service log.
                logger.LogWarning(ex, "La fuente SAT {Source} falló.", listing.Key);
                results.Add(new(listing.Key,"fallida",0,SafeError.Describe(ex)));
            }
        }
        return results;
    }
}
public static class SafeError
{
    public static string Describe(Exception ex)=>ex switch
    {
        InvalidDataException => ex.Message,
        HttpRequestException h => $"Fuente no disponible (HTTP {(int?)h.StatusCode}): {h.Message}",
        OperationCanceledException => "La operación excedió el tiempo permitido.",
        Microsoft.Data.SqlClient.SqlException s => $"Error SQL ({s.Number}): {s.Message}",
        _ => $"{ex.Message} ({ex.GetType().Name})"
    };
}
