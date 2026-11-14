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
    private static bool LooksLikeRfc(string value) =>
        value.Length is >= 8 and <= 20 && value.All(char.IsLetterOrDigit) && value.Any(char.IsLetter) && value.Any(char.IsDigit);
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
        var listTitle=Regex.IsMatch(title,"listado|nombre.*clave|relaci[oó]n.*contribuyentes|oficio|notificaci|anexo",RegexOptions.IgnoreCase);
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
            var article=localArticle ?? titleArticle ?? Article(title + " " + localContext);
            if(article is null) continue;
            var grid=Grid(table);
            string[]? headers=null;
            var rowNumber=0;
            var tableRows=new List<SourceRow>();
            foreach(var cells in grid)
            {
                rowNumber++;
                if(cells.Any(c=>TextKey.Normalize(c) is "RFC" or "REGISTROFEDERALDECONTRIBUYENTES" || TextKey.Normalize(c).Contains("RFC")))
                { headers=cells.ToArray(); continue; }

                var rfcColumn = headers is null ? -1 : Array.FindIndex(headers, h =>
                {
                    var k = TextKey.Normalize(h);
                    return k is "RFC" or "REGISTROFEDERALDECONTRIBUYENTES" || k.Contains("RFC") || k.Contains("REGISTROFEDERAL");
                });

                string rfcVal = "";
                int rfcIdx = -1;

                if (rfcColumn >= 0 && rfcColumn < cells.Length && !string.IsNullOrWhiteSpace(cells[rfcColumn]))
                {
                    var candidate = Rfc.Normalize(cells[rfcColumn]);
                    // Con encabezado RFC se admiten identificadores atípicos, pero nunca fechas ni textos con separadores.
                    if (LooksLikeRfc(candidate))
                    {
                        rfcVal = candidate;
                        rfcIdx = rfcColumn;
                    }
                }
                else
                {
                    // Sin encabezado RFC solo se acepta un RFC con formato estricto; de lo contrario la tabla
                    // (p. ej. proveedores extranjeros con fecha y razón social) no corresponde a un listado de RFC.
                    var m = cells.Select((cell, index) => (Rfc: Regex.Replace(cell, "\\s+", "").ToUpperInvariant(), Index: index))
                        .FirstOrDefault(c => Rfc.IsStrictRfc(c.Rfc));
                    if (!string.IsNullOrEmpty(m.Rfc))
                    {
                        rfcVal = Rfc.Normalize(m.Rfc);
                        rfcIdx = m.Index;
                    }
                }

                if (string.IsNullOrWhiteSpace(rfcVal)) continue;

                var nameIndex = headers is null ? -1 : Array.FindIndex(headers, h => TextKey.Normalize(h).Contains("NOMBRE") || TextKey.Normalize(h).Contains("RAZON") || TextKey.Normalize(h).Contains("CONTRIBUYENTE"));
                if (nameIndex < 0) nameIndex = rfcIdx + 1;
                var name = nameIndex >= 0 && nameIndex < cells.Length ? cells[nameIndex].Trim() : "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = cells.FirstOrDefault(c => rfcIdx >= 0 && rfcIdx < cells.Length && c != cells[rfcIdx] && !string.IsNullOrWhiteSpace(c))?.Trim() ?? rfcVal;
                }
                var context = localContext + " " + title;
                var status = Status(localContext, article);
                if (status == "Publicado") status = Status(title, article);
                var statusColumn = headers is null ? -1 : Array.FindIndex(headers, h => TextKey.Normalize(h).Contains("SITUACION") || TextKey.Normalize(h) == "ESTATUS");
                if (statusColumn >= 0 && statusColumn < cells.Length && !string.IsNullOrWhiteSpace(cells[statusColumn])) status = cells[statusColumn];
                var original = cells.Select((cell, i) => new OriginalCell(i + 1, headers is not null && i < headers.Length ? headers[i] : "Columna " + (i + 1), cell)).ToArray();
                var officeNum = Office().Match(context).Value;
                var datos = new ContribuyenteDatos(
                    Rfc: rfcVal,
                    RazonSocial: name,
                    Articulo: article,
                    Listado: "publicaciones-dof",
                    Situacion: status,
                    FechaEfectiva: date,
                    NumeroOficioDOF: string.IsNullOrWhiteSpace(officeNum) ? null : officeNum,
                    FechaOficioDOF: date
                );
                tableRows.Add(new(ordinal, rowNumber, rfcVal, name, article, "publicaciones-dof", status, date, null,
                    new() { ["numeroOficio"] = officeNum, ["evidenciaClasificacion"] = context, ["estadoOriginal"] = status }, original, 0, datos));
            }
            // A table must be part of a publication, not a mere reference to legislation.
            var isList = relevant 
                || (Regex.IsMatch(localContext, "listado|relaci[oó]n.*contribuyentes|oficio|notificaci[oó]n|acuerdo", RegexOptions.IgnoreCase) && article is not null)
                || (article is not null && tableRows.Count > 0);
            if (tableRows.Count > 0 && (isList || relevant)) { result.AddRange(tableRows); relevant = true; }
        }
        var attachments=(document.DocumentNode.SelectNodes("//a[@href]")??new HtmlNodeCollection(document.DocumentNode))
            .Select(a=>a.GetAttributeValue("href","")).Where(u=>Regex.IsMatch(u,@"\.(pdf|png|jpg|jpeg)(\?|$)",RegexOptions.IgnoreCase)).Distinct().ToArray();
        if(relevant&&result.Count==0) review=true;
        return new(result, relevant || result.Count > 0, review, review ? "Publicación relevante sin extracción completa; revisar tablas/adjuntos." : null, attachments);
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
