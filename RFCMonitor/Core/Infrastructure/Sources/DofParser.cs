using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MonitorRFC.Domain;

namespace MonitorRFC.Infrastructure.Sources;

public sealed record DofParseResult(IReadOnlyList<SourceRow> Rows, bool Relevant, bool NeedsReview, string? Reason, IReadOnlyList<string> Attachments);
public static partial class DofParser
{
    [GeneratedRegex(@"(?<!\d)69\s*[-–]?\s*B\s*[-–]?\s*BIS\b",RegexOptions.IgnoreCase)]
    private static partial Regex ArticleBis();
    [GeneratedRegex(@"(?<!\d)69\s*[-–]\s*B\b",RegexOptions.IgnoreCase)]
    private static partial Regex ArticleB();
    [GeneratedRegex(@"\b49\s*[-–]?\s*BIS\b",RegexOptions.IgnoreCase)]
    private static partial Regex Article49();
    [GeneratedRegex(@"ART[IÍ]CULO\s+69\b(?!\s*[-–]?\s*B)",RegexOptions.IgnoreCase)]
    private static partial Regex Article69();
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{2}-\d{2}-\d{2}-\d{4}-\d+\b")]
    private static partial Regex Office();
    public static string? Article(string text)
    {
        if(ArticleBis().IsMatch(text)) return "69-b-bis";
        if(ArticleB().IsMatch(text)) return "69-b";
        if(Article49().IsMatch(text)) return "49-bis";
        return Article69().IsMatch(text) ? "69":null;
    }
    private static string Text(HtmlNode node) => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText),"\\s+"," ").Trim();
    private static string Status(string context, string article)
    {
        var key=TextKey.Normalize(context);
        if(key.Contains("NOSEDESVIRTUO") || key.Contains("NODESVIRTUO") || key.Contains("NODESVIRTUARON"))
            return article == "49-bis" ? "No desvirtuó la presunción de falsedad" : "Definitivo";
        if(key.Contains("SENTENCIAFAVORABLE")||key.Contains("SENTENCIASFAVORABLES")) return "Sentencia favorable";
        if(key.Contains("LISTADOGLOBALDEFINITIVO")||key.Contains("ENFORMADEFINITIVA")||key.Contains("NODESVIRTUARON")) return "Definitivo";
        if(key.Contains("LISTADOGLOBALDEDESVIRTUADOS")||key.Contains("SEDESVIRTUOLAPRESUNCION")) return "Desvirtuado";
        if(key.Contains("LISTADOGLOBALDEPRESUNCION")||key.Contains("LISTADODEPRESUNTOS")) return "Presunto";
        return "Publicado";
    }
    public static DofParseResult Parse(string title,string html,DateOnly date)
    {
        var document=new HtmlDocument(); document.LoadHtml(html);
        var titleArticle=Article(title);
        var listTitle=Regex.IsMatch(title,"listado|nombre.*clave|relaci[oó]n.*contribuyentes",RegexOptions.IgnoreCase);
        var relevant=titleArticle is not null && listTitle;
        var result=new List<SourceRow>();
        var review=false;
        var tables=document.DocumentNode.SelectNodes("//table")??new HtmlNodeCollection(document.DocumentNode);
        var ordinal=0;
        foreach(var table in tables)
        {
            ordinal++;
            // Use the nearest preceding siblings as context; avoid taking unrelated text after the table.
            var contextParts=new List<string>();
            for(var previous=table.PreviousSibling;previous is not null && contextParts.Sum(s=>s.Length)<4000;previous=previous.PreviousSibling)
            {
                if(previous.Name=="table") break;
                contextParts.Insert(0,Text(previous));
            }
            var localContext=string.Join(" ",contextParts);
            var localArticle=Article(localContext);
            var article=(localArticle is not null && Regex.IsMatch(localContext,"listado|relaci[oó]n.*contribuyentes",RegexOptions.IgnoreCase))?localArticle:titleArticle;
            if(article is null) continue;
            var grid=Grid(table);
            string[]? headers=null;
            var rowNumber=0;
            var tableRows=new List<SourceRow>();
            foreach(var cells in grid)
            {
                rowNumber++;
                if(cells.Any(c=>TextKey.Normalize(c) is "RFC" or "REGISTROFEDERALDECONTRIBUYENTES"))
                { headers=cells.ToArray(); continue; }
                var matches=cells.Select((cell,index)=>(Rfc:Regex.Replace(cell,"\\s+","").ToUpperInvariant(),Index:index))
                    .Where(c=>Rfc.IsValid(c.Rfc)).ToArray();
                if(matches.Length==0)
                {
                    var rfcColumn = headers is null ? -1 : Array.FindIndex(headers, h => TextKey.Normalize(h) is "RFC" or "REGISTROFEDERALDECONTRIBUYENTES");
                    if (rfcColumn >= 0 && cells.Length >= headers!.Length && !string.IsNullOrWhiteSpace(cells[rfcColumn])) review = true;
                    continue;
                }
                if(matches.Length>1) {review=true;continue;}
                var match=matches[0];
                var nameIndex=headers is null?-1:Array.FindIndex(headers,h=>TextKey.Normalize(h).Contains("NOMBRE")||TextKey.Normalize(h).Contains("RAZONSOCIAL"));
                if(nameIndex<0) nameIndex=match.Index+1;
                var name=nameIndex<cells.Length ? cells[nameIndex]:"";
                if(string.IsNullOrWhiteSpace(name)) {review=true;continue;}
                var context=localContext+" "+title;
                var status=Status(localContext, article);
                if (status == "Publicado") status = Status(title, article);
                var statusColumn=headers is null?-1:Array.FindIndex(headers,h=>TextKey.Normalize(h).Contains("SITUACION")||TextKey.Normalize(h)=="ESTATUS");
                if(statusColumn>=0&&statusColumn<cells.Length&&!string.IsNullOrWhiteSpace(cells[statusColumn])) status=cells[statusColumn];
                var original=cells.Select((cell,i)=>new OriginalCell(i+1,headers is not null&&i<headers.Length?headers[i]:"Columna "+(i+1),cell)).ToArray();
                tableRows.Add(new(ordinal,rowNumber,match.Rfc,name,article,"publicaciones-dof",status,date,null,
                    new(){["numeroOficio"]=Office().Match(context).Value,["evidenciaClasificacion"]=context,["estadoOriginal"]=status},original));
            }
            // A table must be part of a publication, not a mere reference to legislation.
            var isList=relevant || (Regex.IsMatch(localContext,"listado|relaci[oó]n.*contribuyentes",RegexOptions.IgnoreCase)&&localArticle is not null);
            if(tableRows.Count>0&&isList) {result.AddRange(tableRows);relevant=true;}
        }
        var attachments=(document.DocumentNode.SelectNodes("//a[@href]")??new HtmlNodeCollection(document.DocumentNode))
            .Select(a=>a.GetAttributeValue("href","")).Where(u=>Regex.IsMatch(u,@"\.(pdf|png|jpg|jpeg)(\?|$)",RegexOptions.IgnoreCase)).Distinct().ToArray();
        if(relevant&&result.Count==0) review=true;
        return new(result,relevant,review,review?"Publicación relevante sin extracción completa; revisar tablas/adjuntos.":null,attachments);
    }
    internal static IEnumerable<string[]> Grid(HtmlNode table)
    {
        var pending=new Dictionary<int,(string Text,int Remaining)>();
        foreach(var row in table.SelectNodes(".//tr")??new HtmlNodeCollection(table))
        {
            if(row.Ancestors("table").FirstOrDefault()!=table) continue;
            var cells=row.SelectNodes("./th|./td");
            if(cells is null || cells.Any(c=>c.Descendants("table").Any())) continue;
            var current=new Dictionary<int,string>();
            foreach(var entry in pending.ToArray())
            {
                current[entry.Key]=entry.Value.Text;
                if(entry.Value.Remaining<=1) pending.Remove(entry.Key);
                else pending[entry.Key]=(entry.Value.Text,entry.Value.Remaining-1);
            }
            var column=0;
            foreach(var cell in cells)
            {
                while(current.ContainsKey(column))column++;
                var colspan=Math.Clamp(cell.GetAttributeValue("colspan",1),1,100);
                var rowspan=Math.Clamp(cell.GetAttributeValue("rowspan",1),1,1000);
                var text=Text(cell);
                for(var c=0;c<colspan;c++)
                {
                    current[column]=text;
                    if(rowspan>1)pending[column]=(text,rowspan-1);
                    column++;
                }
            }
            if(current.Count>0)yield return Enumerable.Range(0,current.Keys.Max()+1).Select(i=>current.GetValueOrDefault(i,"")).ToArray();
        }
    }
}
