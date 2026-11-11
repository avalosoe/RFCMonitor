namespace MonitorRFC.Contracts;

public sealed class AgentOptions
{
    public int Version { get; set; } = 1;
    public string ConnectionString { get; set; } = "";
    public string ProtectedConnectionString { get; set; } = "";
    public string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MonitorRFC");
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
    public string SubjectTemplate { get; set; } = "Actualización de EDOS y EFOS | {{Resultado}}";
    public string HtmlTemplate { get; set; } = "<html><head><meta charset=\"UTF-8\"></head><body style=\"margin:0;padding:0;background-color:#f4f4f4;font-family:Arial,sans-serif;text-align:center;\"><table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"background-color:#f4f4f4;padding:20px 0;\"><tr><td align=\"center\"><table role=\"presentation\" width=\"600\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"background:#ffffff;padding:20px;border-radius:10px;\"><tr><td align=\"center\" style=\"background:#0073e6;color:#ffffff;font-size:20px;padding:15px;border-radius:10px 10px 0 0;\"><strong>Actualización de EDOS y EFOS</strong></td></tr><tr><td align=\"center\" style=\"padding:20px;font-size:16px;color:#333333;\">{{Mensaje}}</td></tr><tr><td align=\"center\" style=\"padding:15px;font-size:12px;color:#666666;\">Este es un mensaje automático, por favor no responda este correo.</td></tr></table></td></tr></table></body></html>";
}
public sealed record AgentCommand(string Action);
public sealed record AgentReply(bool Success, string Message, AgentStatus? Status = null);
public sealed record AgentStatus(bool Running, DateTimeOffset? NextRunUtc, ExecutionReport? LastExecution, int PendingEmails, string? ConfigurationError);
public sealed record SourceResult(string Fuente, string Resultado, long Registros, string? Error = null, long? VersionId = null);
public sealed class ExecutionReport
{
    public Guid EjecucionId { get; set; } = Guid.NewGuid();
    public string Resultado { get; set; } = "sin-cambios";
    public string Disparador { get; set; } = "manual";
    public DateTimeOffset InicioUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinUtc { get; set; }
    public string Equipo { get; set; } = Environment.MachineName;
    public List<SourceResult> Fuentes { get; set; } = [];
}
