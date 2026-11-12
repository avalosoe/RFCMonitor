using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using MonitorRFC.Contracts;

namespace MonitorRFC.Infrastructure.Notifications;

public static partial class EmailTemplates
{
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EjecucionId"] = "Identificador de ejecución",
        ["Resultado"] = "cambios / sin-cambios / parcial / fallida / cancelada",
        ["Inicio"] = "Fecha y hora local de inicio (yyyy-MM-dd HH:mm:ss)",
        ["Fin"] = "Fecha y hora local de fin (yyyy-MM-dd HH:mm:ss)",
        ["Fecha"] = "Fecha local de inicio (yyyy-MM-dd)",
        ["Disparador"] = "Tipo de disparador (manual, programada, arranque)",
        ["Duracion"] = "Duración",
        ["Equipo"] = "Equipo del servicio",
        ["FuentesTotal"] = "Fuentes procesadas",
        ["FuentesConCambios"] = "Fuentes actualizadas",
        ["FuentesSinCambios"] = "Fuentes sin novedades",
        ["FuentesFallidas"] = "Fuentes fallidas o pendientes de revisión",
        ["Registros"] = "Registros procesados",
        ["ResumenHtml"] = "Tabla HTML de resultados por fuente",
        ["ErroresHtml"] = "Errores codificados HTML",
        ["Mensaje"] = "Alias de ResumenHtml para compatibilidad"
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"<\s*(script|iframe|object|embed|form)\b|\bon\w+\s*=|javascript\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveContent();

    public static void Validate(EmailOptions options)
    {
        if (options.SubjectTemplate.IndexOfAny(['\r', '\n']) >= 0 || options.SenderName.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("El asunto/remitente no acepta saltos de línea.");
        if (ActiveContent().IsMatch(options.HtmlTemplate))
            throw new ArgumentException("La plantilla no admite contenido activo.");

        // Si el envío de correo no está habilitado, no bloqueamos la inicialización del servicio
        if (!options.Enabled) return;

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535)
            throw new ArgumentException("Servidor o puerto SMTP inválido.");
        if (options.Security is not ("StartTls" or "SslOnConnect"))
            throw new ArgumentException("Seleccione StartTls o SslOnConnect.");

        ValidateAddress(options.SenderAddress);
        if (options.To.Length + options.Cc.Length + options.Bcc.Length == 0)
            throw new ArgumentException("Agregue al menos un destinatario.");

        foreach (var address in options.To.Concat(options.Cc).Concat(options.Bcc))
            ValidateAddress(address);

        if (options.NotifyResults.Any(r => r is not ("cambios" or "sin-cambios" or "parcial" or "fallida" or "cancelada")))
            throw new ArgumentException("Resultado de notificación desconocido.");
    }

    private static void ValidateAddress(string text)
    {
        if (text.IndexOfAny(['\r', '\n']) >= 0 || !MailboxAddress.TryParse(text, out var parsed) || parsed.Address != text)
            throw new ArgumentException("Dirección de correo inválida.");
    }

    public static (string Subject, string Html) Render(EmailOptions options, ExecutionReport report)
    {
        Validate(options);
        static string E(object? s) => WebUtility.HtmlEncode(s?.ToString() ?? "");

        var table = "<table border=\"1\" cellpadding=\"6\"><tr><th>Fuente</th><th>Resultado</th><th>Leídos</th><th>Aceptados</th><th>Rechazados</th><th>Vigentes certificados</th></tr>" +
            string.Concat(report.Fuentes.Select(f => $"<tr><td>{E(f.Fuente)}</td><td>{E(f.Resultado)}</td><td>{f.Leidos ?? f.Registros}</td><td>{f.Registros}</td><td>{f.Rechazados ?? 0}</td><td>{f.RegistrosCertificados?.ToString() ?? "No disponible"}</td></tr>")) + "</table>";
        var errors = string.Concat(report.Fuentes.Where(f => f.Error is not null).Select(f => $"<p>{E(f.Fuente)}: {E(f.Error)}</p>"));

        var localInicio = report.Inicio.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        var localFin = report.Fin != default ? report.Fin.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : localInicio;
        var localFecha = report.Inicio.LocalDateTime.ToString("yyyy-MM-dd");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EjecucionId"] = report.EjecucionId.ToString(),
            ["Resultado"] = report.Resultado,
            ["Inicio"] = localInicio,
            ["Fin"] = localFin,
            ["Fecha"] = localFecha,
            ["Disparador"] = report.Disparador,
            ["Duracion"] = (report.Fin - report.Inicio).ToString("g"),
            ["Equipo"] = report.Equipo,
            ["FuentesTotal"] = report.Fuentes.Count.ToString(),
            ["FuentesConCambios"] = report.Fuentes.Count(f => f.Resultado == "cambios").ToString(),
            ["FuentesSinCambios"] = report.Fuentes.Count(f => f.Resultado == "sin-cambios").ToString(),
            ["FuentesFallidas"] = report.Fuentes.Count(f => f.Resultado is "fallida" or "requiere-revision").ToString(),
            ["Registros"] = report.Fuentes.Sum(f => f.Registros).ToString(),
            ["ResumenHtml"] = table,
            ["ErroresHtml"] = errors,
            ["Mensaje"] = table,

            // Si por error una plantilla previa contiene referencias a UTC, se resuelven a la fecha/hora local sin romper la aplicación
            ["InicioUtc"] = localInicio,
            ["FinUtc"] = localFin,
            ["FechaUtc"] = localFecha
        };

        string RenderText(string text, bool html) => Tokens().Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            if (!values.TryGetValue(key, out var val)) val = "";
            var isHtmlVar = key.Equals("ResumenHtml", StringComparison.OrdinalIgnoreCase)
                         || key.Equals("ErroresHtml", StringComparison.OrdinalIgnoreCase)
                         || key.Equals("Mensaje", StringComparison.OrdinalIgnoreCase);
            return html && !isHtmlVar ? E(val) : val;
        });

        var subject = RenderText(options.SubjectTemplate, false);
        if (subject.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Asunto renderizado inválido.");

        return (subject, RenderText(options.HtmlTemplate, true));
    }

    public static ExecutionReport Sample() => new()
    {
        Inicio = DateTimeOffset.Now.AddMinutes(-2),
        Fin = DateTimeOffset.Now,
        Resultado = "parcial",
        Fuentes =
        [
            new("SAT · 69-B", "cambios", 125),
            new("SAT · No localizados", "sin-cambios", 0),
            new("DOF", "fallida", 0, "Ejemplo de conexión temporalmente indisponible.")
        ]
    };
}
