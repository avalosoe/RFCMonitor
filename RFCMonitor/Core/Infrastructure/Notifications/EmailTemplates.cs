using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;

namespace MonitorRFC.Infrastructure.Notifications;

public static partial class EmailTemplates
{
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EjecucionId"] = "Identificador de ejecución",
        ["Resultado"] = "cambios / sin-cambios / parcial / fallida / cancelada",
        ["ResultadoTexto"] = "Texto descriptivo del resultado (ej. Con Cambios Fiscales, Sin Novedades)",
        ["ResultadoColor"] = "Color hexadecimal del estado (ej. #059669, #2563eb, #dc2626)",
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
        ["ResumenHtml"] = "Tabla HTML ejecutiva de resultados por fuente",
        ["ErroresHtml"] = "Bloque de alerta de incidencias o confirmación",
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

    public static string FormatSourceDisplayName(string sourceKey)
    {
        if (string.Equals(sourceKey, "dof:publicaciones", StringComparison.OrdinalIgnoreCase))
            return "DOF · Diario Oficial de la Federación";
        if (sourceKey.StartsWith("dof:", StringComparison.OrdinalIgnoreCase))
            return $"DOF · Publicación {sourceKey[4..]}";
        if (string.Equals(sourceKey, "sistema", StringComparison.OrdinalIgnoreCase))
            return "Sistema · Proceso MonitorRFC";

        var sat = SourceCatalog.Sat.FirstOrDefault(s => string.Equals(s.Key, sourceKey, StringComparison.OrdinalIgnoreCase));
        if (sat is not null)
        {
            var art = sat.Article switch
            {
                "69" => "Art. 69",
                "69-b" => "Art. 69-B",
                "69-b-bis" => "Art. 69-B Bis",
                _ => sat.Article
            };
            var nameClean = sat.Name.Replace("Artículo " + sat.Article + " - ", "").Trim();
            return $"SAT · {art} ({nameClean})";
        }
        return sourceKey;
    }

    public static (string Subject, string Html) Render(EmailOptions options, ExecutionReport report)
    {
        Validate(options);
        static string E(object? s) => WebUtility.HtmlEncode(s?.ToString() ?? "");

        var (resultadoTexto, resultadoColor) = report.Resultado switch
        {
            "cambios" => ("Con Cambios Fiscales Detectados", "#059669"),
            "sin-cambios" => ("Sin Novedades Fiscales", "#2563eb"),
            "parcial" => ("Ejecución con Incidencias Parciales", "#d97706"),
            "fallida" => ("Ejecución Fallida / Error Crítico", "#dc2626"),
            "cancelada" => ("Ejecución Cancelada", "#64748b"),
            _ => (report.Resultado, "#2563eb")
        };

        // Generar tabla estilizada y ejecutiva de fuentes
        var sbTable = new StringBuilder();
        sbTable.Append("""
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;border-collapse:collapse;margin:12px 0;font-family:'Segoe UI',-apple-system,BlinkMacSystemFont,Roboto,Helvetica,Arial,sans-serif;font-size:13px;border:1px solid #e2e8f0;border-radius:8px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,0.04);">
              <thead>
                <tr style="background:#0f172a;color:#ffffff;text-align:left;">
                  <th style="padding:11px 14px;font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:0.5px;">Fuente de Información</th>
                  <th style="padding:11px 14px;font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:0.5px;text-align:center;">Estado</th>
                  <th style="padding:11px 14px;font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:0.5px;text-align:right;">Registros</th>
                  <th style="padding:11px 14px;font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:0.5px;">Detalle / Observaciones</th>
                </tr>
              </thead>
              <tbody>
            """);

        if (report.Fuentes.Count == 0)
        {
            sbTable.Append("""
                <tr style="background:#ffffff;">
                  <td colspan="4" style="padding:20px;text-align:center;color:#64748b;font-style:italic;">
                    No se procesaron fuentes en este ciclo.
                  </td>
                </tr>
                """);
        }
        else
        {
            var index = 0;
            foreach (var f in report.Fuentes)
            {
                var bg = (index++ % 2 == 0) ? "#ffffff" : "#f8fafc";
                var friendlyName = FormatSourceDisplayName(f.Fuente);
                var (badgeText, badgeBg, badgeColor, badgeBorder) = f.Resultado switch
                {
                    "cambios" => ("CON CAMBIOS", "#dcfce7", "#15803d", "#bbf7d0"),
                    "sin-cambios" => ("SIN NOVEDADES", "#f1f5f9", "#475569", "#cbd5e1"),
                    "fallida" => ("ERROR / FALLÓ", "#fee2e2", "#b91c1c", "#fecaca"),
                    "requiere-revision" => ("REVISIÓN", "#fef3c7", "#b45309", "#fde68a"),
                    "cancelada" => ("CANCELADA", "#f3f4f6", "#4b5563", "#e5e7eb"),
                    _ => (f.Resultado.ToUpperInvariant(), "#f1f5f9", "#475569", "#cbd5e1")
                };

                var detailHtml = !string.IsNullOrWhiteSpace(f.Error)
                    ? $"<span style=\"color:#dc2626;font-weight:600;\">{E(f.Error)}</span>"
                    : f.Resultado == "cambios"
                        ? "<span style=\"color:#16a34a;font-weight:500;\">Actualización integrada</span>"
                        : "<span style=\"color:#94a3b8;\">Sin cambios en el portal</span>";

                var countText = f.Registros > 0 ? f.Registros.ToString("N0") : "0";

                sbTable.Append($"""
                    <tr style="background:{bg};border-bottom:1px solid #e2e8f0;">
                      <td style="padding:10px 14px;color:#0f172a;font-weight:600;">
                        {E(friendlyName)}
                        <div style="font-size:11px;color:#94a3b8;font-weight:400;margin-top:2px;">{E(f.Fuente)}</div>
                      </td>
                      <td style="padding:10px 14px;text-align:center;">
                        <span style="display:inline-block;padding:3px 10px;font-size:10px;font-weight:700;border-radius:12px;background:{badgeBg};color:{badgeColor};border:1px solid {badgeBorder};letter-spacing:0.4px;">
                          {badgeText}
                        </span>
                      </td>
                      <td style="padding:10px 14px;text-align:right;color:#0f172a;font-weight:700;font-variant-numeric:tabular-nums;">
                        {countText}
                      </td>
                      <td style="padding:10px 14px;font-size:12px;line-height:1.4;">
                        {detailHtml}
                      </td>
                    </tr>
                    """);
            }
        }

        sbTable.Append("</tbody></table>");
        var table = sbTable.ToString();

        // Bloque de incidencias y errores destacados
        var errorsList = report.Fuentes.Where(f => !string.IsNullOrWhiteSpace(f.Error)).ToList();
        string errors;
        if (errorsList.Count > 0)
        {
            var sbErrors = new StringBuilder();
            sbErrors.Append($"""
                <div style="margin:14px 0 18px 0;padding:16px 20px;background:#fff1f2;border:1px solid #fecdd3;border-left:5px solid #e11d48;border-radius:8px;">
                  <div style="font-size:14px;font-weight:700;color:#9f1239;margin-bottom:8px;">
                    ⚠️ Incidencias reportadas en el ciclo ({errorsList.Count})
                  </div>
                  <table role="presentation" width="100%" cellspacing="0" cellpadding="4" style="font-size:12px;color:#881337;">
                """);
            foreach (var err in errorsList)
            {
                sbErrors.Append($"""
                    <tr>
                      <td style="font-weight:700;width:180px;vertical-align:top;padding-bottom:4px;">{E(FormatSourceDisplayName(err.Fuente))}:</td>
                      <td style="vertical-align:top;padding-bottom:4px;">{E(err.Error)}</td>
                    </tr>
                    """);
            }
            sbErrors.Append("</table></div>");
            errors = sbErrors.ToString();
        }
        else
        {
            errors = """
                <div style="margin:14px 0 18px 0;padding:12px 18px;background:#f0fdf4;border:1px solid #bbf7d0;border-left:5px solid #16a34a;border-radius:8px;color:#14532d;font-size:13px;font-weight:500;">
                  ✔ Todas las fuentes operaron correctamente. No se registraron fallas ni errores en este ciclo.
                </div>
                """;
        }

        var localInicio = report.Inicio.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        var localFin = report.Fin != default ? report.Fin.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : localInicio;
        var localFecha = report.Inicio.LocalDateTime.ToString("yyyy-MM-dd");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EjecucionId"] = report.EjecucionId.ToString(),
            ["Resultado"] = report.Resultado,
            ["ResultadoTexto"] = resultadoTexto,
            ["ResultadoColor"] = resultadoColor,
            ["Inicio"] = localInicio,
            ["Fin"] = localFin,
            ["Fecha"] = localFecha,
            ["Disparador"] = char.ToUpper(report.Disparador[0]) + report.Disparador[1..],
            ["Duracion"] = (report.Fin - report.Inicio).ToString(@"hh\:mm\:ss"),
            ["Equipo"] = report.Equipo,
            ["FuentesTotal"] = report.Fuentes.Count.ToString(),
            ["FuentesConCambios"] = report.Fuentes.Count(f => f.Resultado == "cambios").ToString(),
            ["FuentesSinCambios"] = report.Fuentes.Count(f => f.Resultado == "sin-cambios").ToString(),
            ["FuentesFallidas"] = report.Fuentes.Count(f => f.Resultado is "fallida" or "requiere-revision").ToString(),
            ["Registros"] = report.Fuentes.Sum(f => f.Registros).ToString("N0"),
            ["ResumenHtml"] = table,
            ["ErroresHtml"] = errors,
            ["Mensaje"] = table,

            // Retrocompatibilidad con plantillas previas
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

    public static (string Subject, string Html) RenderLifecycle(EmailOptions options, string action, string detail)
    {
        Validate(options);
        var isStarted = string.Equals(action, "Iniciado", StringComparison.OrdinalIgnoreCase);
        var subject = $"MonitorRFC | Servicio {action} | {Environment.MachineName} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var headerBg = isStarted ? "#15803d" : "#475569";
        var icon = isStarted ? "&#128994;" : "&#128308;";
        var title = isStarted ? "Servicio Iniciado y en Monitoreo" : "Servicio Detenido";
        var statusText = isStarted ? "Activo / En Monitoreo" : "Detenido / Fuera de Línea";
        var statusColor = isStarted ? "#15803d" : "#b91c1c";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var html = $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"></head>
            <body style="margin:0;padding:0;background-color:#f1f5f9;font-family:'Segoe UI',-apple-system,BlinkMacSystemFont,Roboto,Helvetica,Arial,sans-serif;color:#1e293b;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background-color:#f1f5f9;padding:30px 10px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="600" cellspacing="0" cellpadding="0" border="0" style="width:600px;max-width:600px;background-color:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 6px -1px rgba(0,0,0,0.08);border:1px solid #e2e8f0;">
                      <tr>
                        <td style="background-color:{headerBg};padding:22px 28px;color:#ffffff;">
                          <div style="font-size:12px;text-transform:uppercase;letter-spacing:1.2px;font-weight:700;opacity:0.9;">MonitorRFC · Estado del Servicio</div>
                          <div style="font-size:22px;font-weight:800;margin-top:4px;">{icon} {title}</div>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:26px 28px;">
                          <p style="font-size:15px;line-height:1.6;margin:0 0 18px 0;color:#334155;">{WebUtility.HtmlEncode(detail)}</p>
                          <table role="presentation" width="100%" cellspacing="0" cellpadding="10" style="background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;">
                            <tr><td style="width:140px;color:#64748b;font-weight:600;">Estado:</td><td style="font-weight:700;color:{statusColor};">{statusText}</td></tr>
                            <tr><td style="color:#64748b;font-weight:600;">Fecha y Hora:</td><td style="color:#0f172a;font-weight:600;">{timestamp}</td></tr>
                            <tr><td style="color:#64748b;font-weight:600;">Equipo / Servidor:</td><td>{Environment.MachineName}</td></tr>
                            <tr><td style="color:#64748b;font-weight:600;">Servicio:</td><td>MonitorRFC.Service (Windows Service)</td></tr>
                          </table>
                        </td>
                      </tr>
                      <tr>
                        <td align="center" style="background-color:#f8fafc;border-top:1px solid #e2e8f0;padding:14px;font-size:12px;color:#94a3b8;">
                          Notificación de ciclo de vida del agente · MonitorRFC
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
        return (subject, html);
    }

    public static ExecutionReport Sample() => new()
    {
        Inicio = DateTimeOffset.Now.AddMinutes(-5),
        Fin = DateTimeOffset.Now,
        Resultado = "parcial",
        Disparador = "programada",
        Fuentes =
        [
            new("sat:69-b:definitivos", "cambios", 125),
            new("sat:69:no-localizados", "sin-cambios", 716052),
            new("dof:publicaciones", "fallida", 0, "Error en nota 5803160: Conexión temporalmente indisponible.")
        ]
    };
}
