using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using HtmlAgilityPack;
namespace MonitorRFC.Agent.Manager;

// Render a safe structural subset in WPF: no scripts, URLs, embedded content or network requests.
public static class EmailPreview
{
    public static string SafeHtml(string html)
    {
        var document = new HtmlDocument(); document.LoadHtml(html);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//iframe|//object|//embed|//form|//img|//link") ?? Enumerable.Empty<HtmlNode>()) node.Remove();
        foreach (var node in document.DocumentNode.Descendants().ToArray())
            foreach (var attribute in node.Attributes.Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || a.Name is "src" or "href").ToArray()) attribute.Remove();
        return "<!DOCTYPE html>" + document.DocumentNode.OuterHtml;
    }
    public static FlowDocument Render(string html)
    {
        var source = new HtmlDocument(); source.LoadHtml(html);
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(20) };
        var body = source.DocumentNode.SelectSingleNode("//body") ?? source.DocumentNode;
        AddBlocks(document.Blocks, body);
        return document;
    }
    private static void AddBlocks(BlockCollection target, HtmlNode node)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.Name is "script" or "style" or "head" or "img" or "iframe" or "object") continue;
            if (child.Name == "table")
            {
                var table = new Table { CellSpacing = 6 };
                var group = new TableRowGroup(); table.RowGroups.Add(group);
                foreach (var row in child.SelectNodes(".//tr") ?? new HtmlNodeCollection(child))
                {
                    var output = new TableRow(); group.Rows.Add(output);
                    foreach (var cell in row.SelectNodes("./td|./th") ?? new HtmlNodeCollection(row))
                        output.Cells.Add(new TableCell(new Paragraph(new Run(HtmlEntity.DeEntitize(cell.InnerText))))
                        { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(6), FontWeight = cell.Name == "th" ? FontWeights.Bold : FontWeights.Normal });
                }
                target.Add(table);
            }
            else if (child.Name is "div" or "section" or "html" or "body") AddBlocks(target, child);
            else
            {
                var text = HtmlEntity.DeEntitize(child.InnerText).Trim();
                if (text.Length == 0) continue;
                target.Add(new Paragraph(new Run(text)) { FontSize = child.Name == "h1" ? 26 : child.Name == "h2" ? 20 : 14,
                    FontWeight = child.Name is "h1" or "h2" or "h3" ? FontWeights.Bold : FontWeights.Normal });
            }
        }
    }
}
