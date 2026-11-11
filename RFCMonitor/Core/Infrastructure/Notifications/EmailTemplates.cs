using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using MonitorRFC.Contracts;
namespace MonitorRFC.Infrastructure.Notifications;

public static partial class EmailTemplates
{
    public static IReadOnlyDictionary<string,string> Variables { get; } = new Dictionary<string,string>
    {
        ["EjecucionId"]="Identificador de ejecución", ["Resultado"]="cambios / sin-cambios / parcial / fallida / cancelada",
        ["InicioUtc"]="Inicio UTC", ["FinUtc"]="Fin UTC", ["Duracion"]="Duración", ["Equipo"]="Equipo del servicio",
        ["FuentesTotal"]="Fuentes procesadas", ["FuentesConCambios"]="Fuentes actualizadas",
        ["FuentesSinCambios"]="Fuentes sin novedades", ["FuentesFallidas"]="Fuentes fallidas o pendientes de revisión",
        ["Registros"]="Registros procesados", ["ResumenHtml"]="Tabla HTML de resultados por fuente",
        ["ErroresHtml"]="Errores codificados HTML", ["Mensaje"]="Alias de ResumenHtml para plantilla anterior"
    };
    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    [GeneratedRegex(@"<\s*(script|iframe|object|embed|form)\b|\bon\w+\s*=|javascript\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveContent();
    public static void Validate(EmailOptions options)
    {
        if (options.SubjectTemplate.IndexOfAny(['\r','\n']) >= 0 || options.SenderName.IndexOfAny(['\r','\n']) >= 0)
            throw new ArgumentException("El asunto/remitente no acepta saltos de línea.");
        if (ActiveContent().IsMatch(options.HtmlTemplate)) throw new ArgumentException("La plantilla no admite contenido activo.");
        foreach (var text in new[] {options.SubjectTemplate, options.HtmlTemplate})
        {
            foreach (Match match in Tokens().Matches(text))
                if (!Variables.ContainsKey(match.Groups[1].Value)) throw new ArgumentException("Variable desconocida: " + match.Groups[1].Value);
            var remainder = Tokens().Replace(text, "");
            if (remainder.Contains("{{") || remainder.Contains("}}")) throw new ArgumentException("Variable de plantilla mal formada.");
        }
        if (Tokens().Matches(options.SubjectTemplate).Any(m => m.Groups[1].Value is "ResumenHtml" or "ErroresHtml" or "Mensaje"))
            throw new ArgumentException("Las variables HTML solo se permiten en el cuerpo.");
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535)
            throw new ArgumentException("Servidor o puerto SMTP inválido.");
        if (options.Security is not ("StartTls" or "SslOnConnect")) throw new ArgumentException("Seleccione StartTls o SslOnConnect.");
        ValidateAddress(options.SenderAddress);
        if (options.To.Length + options.Cc.Length + options.Bcc.Length == 0) throw new ArgumentException("Agregue un destinatario.");
        foreach (var address in options.To.Concat(options.Cc).Concat(options.Bcc)) ValidateAddress(address);
        if (options.NotifyResults.Any(r => r is not ("cambios" or "sin-cambios" or "parcial" or "fallida" or "cancelada")))
            throw new ArgumentException("Resultado de notificación desconocido.");
    }
    private static void ValidateAddress(string text)
    {
        if (text.IndexOfAny(['\r','\n']) >= 0 || !MailboxAddress.TryParse(text, out var parsed) || parsed.Address != text)
            throw new ArgumentException("Dirección de correo inválida.");
    }
    public static (string Subject, string Html) Render(EmailOptions options, ExecutionReport report)
    {
        Validate(options);
        static string E(object? s) => WebUtility.HtmlEncode(s?.ToString() ?? "");
        var table = "<table border=\"1\" cellpadding=\"6\"><tr><th>Fuente</th><th>Resultado</th><th>Leídos</th><th>Aceptados</th><th>Rechazados</th><th>Vigentes certificados</th></tr>" +
            string.Concat(report.Fuentes.Select(f => $"<tr><td>{E(f.Fuente)}</td><td>{E(f.Resultado)}</td><td>{f.Leidos ?? f.Registros}</td><td>{f.Registros}</td><td>{f.Rechazados ?? 0}</td><td>{f.RegistrosCertificados?.ToString() ?? "No disponible"}</td></tr>")) + "</table>";
        var errors = string.Concat(report.Fuentes.Where(f => f.Error is not null).Select(f => $"<p>{E(f.Fuente)}: {E(f.Error)}</p>"));
        var values = new Dictionary<string,string>
        {
            ["EjecucionId"]=report.EjecucionId.ToString(), ["Resultado"]=report.Resultado,
            ["InicioUtc"]=report.InicioUtc.ToString("O"), ["FinUtc"]=report.FinUtc.ToString("O"),
            ["Duracion"]=(report.FinUtc-report.InicioUtc).ToString("g"), ["Equipo"]=report.Equipo,
            ["FuentesTotal"]=report.Fuentes.Count.ToString(), ["FuentesConCambios"]=report.Fuentes.Count(f=>f.Resultado=="cambios").ToString(),
            ["FuentesSinCambios"]=report.Fuentes.Count(f=>f.Resultado=="sin-cambios").ToString(),
            ["FuentesFallidas"]=report.Fuentes.Count(f=>f.Resultado is "fallida" or "requiere-revision").ToString(),
            ["Registros"]=report.Fuentes.Sum(f=>f.Registros).ToString(),
            ["ResumenHtml"]=table, ["ErroresHtml"]=errors, ["Mensaje"]=table
        };
        string RenderText(string text, bool html) => Tokens().Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            return html && key is not ("ResumenHtml" or "ErroresHtml" or "Mensaje") ? E(values[key]) : values[key];
        });
        var subject = RenderText(options.SubjectTemplate, false);
        if (subject.IndexOfAny(['\r','\n']) >= 0) throw new ArgumentException("Asunto renderizado inválido.");
        return (subject, RenderText(options.HtmlTemplate, true));
    }
    public static ExecutionReport Sample() => new()
    {
        InicioUtc=DateTimeOffset.UtcNow.AddMinutes(-2), FinUtc=DateTimeOffset.UtcNow, Resultado="parcial",
        Fuentes=[new("SAT · 69-B","cambios",125),new("SAT · No localizados","sin-cambios",0),new("DOF","fallida",0,"Ejemplo de conexión temporalmente indisponible.")]
    };
}
