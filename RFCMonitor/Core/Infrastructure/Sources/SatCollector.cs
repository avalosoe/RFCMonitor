using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MonitorRFC.Application;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;
namespace MonitorRFC.Infrastructure.Sources;

public sealed class SatCollector(SafeDownloader downloader,IPublicationRepository repository)
{
    public const string CatalogUrl="https://www.sat.gob.mx/minisitio/DatosAbiertos/contribuyentes_publicados.html";
    public async Task<IReadOnlyList<SourceResult>> RunAsync(AgentOptions options,CancellationToken ct)
    {
        var results=new List<SourceResult>();
        var html=await downloader.TextAsync(CatalogUrl,options,ct);
        var page=new HtmlDocument(); page.LoadHtml(html);
        foreach(var listing in SourceCatalog.Sat.Where(s=>options.EnabledSatSources.Length==0||options.EnabledSatSources.Contains(s.Key)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var anchors=page.DocumentNode.SelectNodes("//a[@href]")??new HtmlNodeCollection(page.DocumentNode);
                var file=Path.GetFileName(listing.RelativeUrl);
                var anchor=anchors.FirstOrDefault(a=>Uri.TryCreate(new Uri(CatalogUrl),a.GetAttributeValue("href",""),out var u)&&
                    string.Equals(Path.GetFileName(u.AbsolutePath),file,StringComparison.OrdinalIgnoreCase));
                if(anchor is null) throw new InvalidDataException("El enlace oficial no está presente; requiere revisión.");
                var section = anchor.Ancestors().FirstOrDefault(n => n.GetAttributeValue("class", "").Split(' ').Contains("panel-group"));
                var sectionTitle = section?.SelectSingleNode(".//div[contains(@class,'panel-heading')]");
                var sectionArticle = DofParser.Article(HtmlEntity.DeEntitize(sectionTitle?.InnerText ?? ""));
                if (sectionArticle != listing.Article) throw new InvalidDataException("El enlace cambió de sección; requiere revisión del catálogo.");
                var uri=new Uri(new Uri(CatalogUrl),anchor.GetAttributeValue("href",""));
                DateOnly? date=null;
                for(var parent=anchor.ParentNode;parent is not null;parent=parent.ParentNode)
                {
                    if(parent.Name=="body"||parent.Name=="html") break;
                    var text=HtmlEntity.DeEntitize(parent.InnerText);
                    var match=Regex.Match(text,@"Información\s+actualizada\s+al\s+(\d{1,2}\s+de\s+\w+\s+de\s+\d{4})",RegexOptions.IgnoreCase);
                    if(match.Success) {date=SatParser.ParseDate(match.Groups[1].Value);break;}
                }
                if(date is null)
                    throw new InvalidDataException("No se encontró una fecha de actualización válida junto al listado oficial.");
                var download=await downloader.FetchAsync(uri.ToString(),options,ct);
                var document=new SourceDocument(listing.Key,listing.Key,"SAT",listing.Article,listing.Slug,download.Url,
                    download.Hash,download.Path,date,"seccion",DateTimeOffset.UtcNow,"sat-v2-rfc-normalizado");
                var rows=listing.Document ? SatParser.ReadWorkbookAsync(download.Path,ct) : SatParser.ReadAsync(listing,download.Path,ct);
                var result=await repository.ImportAsync(document,rows,ct);
                var status = result.Changed ? "cambios" : "sin-cambios";
                var issue = result.RejectedRows > 0
                    ? $"{result.RejectedRows} fila(s) no consultable(s) por RFC; se conservaron en FilaFuente. {result.ReviewDetail ?? "Revise la cuarentena."}"
                    : null;
                results.Add(new(listing.Key,status,result.Rows,issue,result.VersionId,result.ReadRows,result.RejectedRows,result.CertifiedRows));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex) {results.Add(new(listing.Key,"fallida",0,SafeError.Describe(ex)));}
        }
        return results;
    }
}
public static class SafeError
{
    public static string Describe(Exception ex)=>ex switch
    {
        InvalidDataException => ex.Message,
        HttpRequestException h => $"Fuente no disponible (HTTP {(int?)h.StatusCode}).",
        OperationCanceledException => "La operación excedió el tiempo permitido.",
        Microsoft.Data.SqlClient.SqlException => "No se pudo completar la operación SQL.",
        _ => "No se pudo completar la operación ("+ex.GetType().Name+")."
    };
}
