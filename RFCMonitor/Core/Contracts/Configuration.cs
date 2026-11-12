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
}
public static class EmailTemplateDefaults
{
    public const string SummaryHtml = "<html><head><meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"></head><body style=\"margin:0;padding:0;background:#f4f4f4;font-family:Arial,sans-serif;color:#333;\"><table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"padding:20px 0;background:#f4f4f4;\"><tr><td align=\"center\"><table role=\"presentation\" width=\"600\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"width:600px;max-width:600px;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 2px 5px rgba(0,0,0,.1);\"><tr><td align=\"center\" style=\"background:#0073e6;color:#fff;font-size:20px;padding:16px;\"><strong>Resumen de ejecución · MonitorRFC</strong></td></tr><tr><td style=\"padding:20px;font-size:14px;\"><p style=\"margin-top:0;font-size:16px;\"><strong>Resultado: {{Resultado}}</strong></p><table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"5\" border=\"0\" style=\"background:#f8fafc;border:1px solid #dbe3ec;\"><tr><td><strong>Inicio</strong></td><td>{{Inicio}}</td></tr><tr><td><strong>Fin</strong></td><td>{{Fin}}</td></tr><tr><td><strong>Duración</strong></td><td>{{Duracion}}</td></tr><tr><td><strong>Equipo</strong></td><td>{{Equipo}}</td></tr><tr><td><strong>Registros</strong></td><td>{{Registros}}</td></tr></table><h3 style=\"margin:20px 0 8px;color:#0073e6;\">Fuentes procesadas</h3>{{ResumenHtml}}{{ErroresHtml}}</td></tr><tr><td align=\"center\" style=\"padding:15px;background:#f8fafc;font-size:12px;color:#666;\">Ejecución {{EjecucionId}} · Este es un mensaje automático, por favor no responda este correo.</td></tr></table></td></tr></table></body></html>";
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
