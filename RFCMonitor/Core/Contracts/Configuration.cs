namespace MonitorRFC.Contracts;

public sealed class AgentOptions
{
    public int Version { get; set; } = 1;
    public string ConnectionString { get; set; } = "";
    public string ProtectedConnectionString { get; set; } = "";
    public string DataDirectory { get; set; } = @"C:\Servicios\MonitorRFC\data";
    public bool SatEnabled { get; set; } = true;
    public string[] EnabledSatSources { get; set; } = [];
    public bool DofEnabled { get; set; } = true;
    public DateOnly DofStartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
    public int DofLookbackDays { get; set; } = 7;
    public int DofDaysPerCycle { get; set; } = 31;
    public int HttpTimeoutSeconds { get; set; } = 180;
    public long MaxDownloadBytes { get; set; } = 512L * 1024 * 1024;
    public ScheduleOptions Schedule { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
}
public sealed class ScheduleOptions
{
    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public string Mode { get; set; } = "daily-hours";
    public int IntervalMinutes { get; set; } = 720;
    public string LocalTime { get; set; } = "08:00";
    public int[] WeekDays { get; set; } = [1,2,3,4,5];
    public string Cron { get; set; } = "0 8,20 * * *";
    public string TimeZone { get; set; } = "America/Mexico_City";
}
public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string UserName { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string SenderAddress { get; set; } = "";
    public string SenderName { get; set; } = "MonitorRFC";
    public string[] To { get; set; } = [];
    public string[] Cc { get; set; } = [];
    public string[] Bcc { get; set; } = [];
    public string[] NotifyResults { get; set; } = ["cambios", "sin-cambios", "parcial", "fallida", "cancelada"];
    public string SubjectTemplate { get; set; } = "MonitorRFC | Resumen {{Resultado}} | {{Inicio}}";
    public string HtmlTemplate { get; set; } = EmailTemplateDefaults.SummaryHtml;
    public bool NotifyServiceLifecycle { get; set; } = true;
}
public static class EmailTemplateDefaults
{
    public const string SummaryHtml = """
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="UTF-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
        </head>
        <body style="margin:0;padding:0;background-color:#f1f5f9;font-family:'Segoe UI',-apple-system,BlinkMacSystemFont,Roboto,Helvetica,Arial,sans-serif;color:#1e293b;">
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background-color:#f1f5f9;padding:25px 10px;">
            <tr>
              <td align="center">
                <table role="presentation" width="680" cellspacing="0" cellpadding="0" border="0" style="width:680px;max-width:680px;background-color:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 6px -1px rgba(0,0,0,0.08),0 2px 4px -2px rgba(0,0,0,0.06);border:1px solid #e2e8f0;">
                  <tr>
                    <td style="background-color:{{ResultadoColor}};padding:22px 30px;color:#ffffff;">
                      <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0">
                        <tr>
                          <td>
                            <div style="font-size:12px;text-transform:uppercase;letter-spacing:1.2px;font-weight:700;opacity:0.9;">MonitorRFC · Monitoreo Fiscal SAT & DOF</div>
                            <div style="font-size:22px;font-weight:800;margin-top:4px;letter-spacing:-0.3px;">{{ResultadoTexto}}</div>
                          </td>
                          <td align="right" style="vertical-align:middle;">
                            <span style="background:rgba(255,255,255,0.22);padding:6px 14px;border-radius:20px;font-size:12px;font-weight:700;letter-spacing:0.5px;">{{Disparador}}</span>
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:22px 30px 10px 30px;">
                      <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0">
                        <tr>
                          <td width="23%" style="background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:12px 10px;text-align:center;">
                            <div style="font-size:11px;color:#64748b;font-weight:600;text-transform:uppercase;">Fuentes</div>
                            <div style="font-size:22px;font-weight:800;color:#0f172a;margin-top:2px;">{{FuentesTotal}}</div>
                            <div style="font-size:10px;color:#64748b;">procesadas</div>
                          </td>
                          <td width="2%"></td>
                          <td width="23%" style="background:#ecfdf5;border:1px solid #bbf7d0;border-radius:8px;padding:12px 10px;text-align:center;">
                            <div style="font-size:11px;color:#15803d;font-weight:600;text-transform:uppercase;">Con Cambios</div>
                            <div style="font-size:22px;font-weight:800;color:#15803d;margin-top:2px;">{{FuentesConCambios}}</div>
                            <div style="font-size:10px;color:#15803d;">actualizadas</div>
                          </td>
                          <td width="2%"></td>
                          <td width="23%" style="background:#f1f5f9;border:1px solid #cbd5e1;border-radius:8px;padding:12px 10px;text-align:center;">
                            <div style="font-size:11px;color:#475569;font-weight:600;text-transform:uppercase;">Sin Novedad</div>
                            <div style="font-size:22px;font-weight:800;color:#334155;margin-top:2px;">{{FuentesSinCambios}}</div>
                            <div style="font-size:10px;color:#475569;">sin cambios</div>
                          </td>
                          <td width="2%"></td>
                          <td width="23%" style="background:#fef2f2;border:1px solid #fecaca;border-radius:8px;padding:12px 10px;text-align:center;">
                            <div style="font-size:11px;color:#b91c1c;font-weight:600;text-transform:uppercase;">Fallidas</div>
                            <div style="font-size:22px;font-weight:800;color:#b91c1c;margin-top:2px;">{{FuentesFallidas}}</div>
                            <div style="font-size:10px;color:#b91c1c;">con error</div>
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:10px 30px;">
                      <table role="presentation" width="100%" cellspacing="0" cellpadding="8" style="background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;color:#475569;">
                        <tr>
                          <td><strong>Inicio:</strong> {{Inicio}}</td>
                          <td><strong>Fin:</strong> {{Fin}}</td>
                          <td><strong>Duración:</strong> {{Duracion}}</td>
                          <td><strong>Equipo:</strong> {{Equipo}}</td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:12px 30px 20px 30px;">
                      {{ErroresHtml}}
                      <div style="margin-top:16px;">
                        <div style="font-size:15px;font-weight:700;color:#0f172a;margin-bottom:8px;">Estado por Fuente de Información</div>
                        {{ResumenHtml}}
                      </div>
                    </td>
                  </tr>
                  <tr>
                    <td align="center" style="background-color:#f8fafc;border-top:1px solid #e2e8f0;padding:16px 30px;font-size:12px;color:#64748b;line-height:1.5;">
                      <div><strong>MonitorRFC</strong> · Servicio Automatizado de Vigilancia Fiscal</div>
                      <div style="font-size:11px;color:#94a3b8;margin-top:3px;">ID Ejecución: {{EjecucionId}} · Notificación generada automáticamente</div>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
}
public sealed record AgentCommand(string Action);
public sealed record AgentReply(bool Success, string Message, AgentStatus? Status = null);
public sealed record AgentStatus(bool Running, DateTimeOffset? NextRun, ExecutionReport? LastExecution, int PendingEmails, string? ConfigurationError);
public sealed record SourceResult(string Fuente, string Resultado, long Registros, string? Error = null, long? VersionId = null,
    long? Leidos = null, long? Rechazados = null, long? RegistrosCertificados = null);
public sealed class ExecutionReport
{
    public Guid EjecucionId { get; set; } = Guid.NewGuid();
    public string Resultado { get; set; } = "sin-cambios";
    public string Disparador { get; set; } = "manual";
    public DateTimeOffset Inicio { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Fin { get; set; }
    public string Equipo { get; set; } = Environment.MachineName;
    public List<SourceResult> Fuentes { get; set; } = [];
}
