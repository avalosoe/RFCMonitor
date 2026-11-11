using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Configuration;
using MonitorRFC.Infrastructure.Notifications;
using MonitorRFC.Infrastructure.Persistence;
using MonitorRFC.Infrastructure.Sources;
namespace MonitorRFC.Agent.Manager;

public sealed class ManagerViewModel : INotifyPropertyChanged
{
    private readonly AgentConfiguration configuration = new();
    public AgentOptions Options { get; private set; }
    public string SqlServer { get; set; } = "";
    public string SqlDatabase { get; set; } = "";
    public string SqlUser { get; set; } = "";
    public string SqlPassword { get; set; } = "";
    public bool SqlEncrypt { get; set; }
    public bool SqlTrustServerCertificate { get; set; }
    public string NewPassword { get; set; } = "";
    public string ServicePath { get; set; } = @"C:\Servicios\RFCMonitor";
    public string Message { get; private set; } = "Configure SQL y fuentes antes de iniciar. Los secretos guardados no se muestran.";
    public string Diagnostics { get; private set; } = "";
    public string DailyHours { get; set; } = "08:00; 20:00";
    public string[] ScheduleModes { get; } = ["Cada N minutos", "Diaria (horas)", "Diaria (una hora)", "Semanal", "Cron avanzado"];
    private string selectedScheduleMode = "Diaria (horas)";
    public string SelectedScheduleMode
    {
        get => selectedScheduleMode;
        set
        {
            if (selectedScheduleMode == value) return;
            selectedScheduleMode = value;
            foreach (var name in new[] { nameof(SelectedScheduleMode), nameof(IsIntervalSchedule), nameof(IsDailyHoursSchedule), nameof(IsDailySchedule), nameof(IsWeeklySchedule), nameof(IsCronSchedule) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    public bool IsIntervalSchedule => SelectedScheduleMode == "Cada N minutos";
    public bool IsDailyHoursSchedule => SelectedScheduleMode == "Diaria (horas)";
    public bool IsDailySchedule => SelectedScheduleMode == "Diaria (una hora)";
    public bool IsWeeklySchedule => SelectedScheduleMode == "Semanal";
    public bool IsCronSchedule => SelectedScheduleMode == "Cron avanzado";
    public string[] TlsModes { get; } = ["StartTls", "SslOnConnect"];
    public ObservableCollection<SourceSelection> Sources { get; } = [];
    public string[] DofStartModes { get; } = ["Hoy", "Ayer", "Días atrás"];
    public string DofStartMode { get; set; } = "Hoy";
    public int DofDaysBack { get; set; }
    public string WeekDays { get; set; }
    public string To { get; set; }
    public string Cc { get; set; }
    public string Bcc { get; set; }
    public string Results { get; set; }
    public string ServiceStatus { get; private set; } = "Consultando…";
    public bool CanInstall { get; private set; }
    public bool CanUpdate { get; private set; }
    public bool CanUninstall { get; private set; }
    public bool CanStart { get; private set; }
    public bool CanStop { get; private set; }
    public bool CanRestart { get; private set; }
    public string ScheduleHelp => "Predeterminado: cada 12 horas, a las 08:00 y 20:00 (hora de Ciudad de México).";
    public string HtmlPreview { get; private set; } = "<html><body>Seleccione Visualizar HTML para cargar la vista previa.</body></html>";
    public string Variables => string.Join(Environment.NewLine, EmailTemplates.Variables.Select(v => "{{" + v.Key + "}} — " + v.Value));
    public ICommand Action { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ManagerViewModel()
    {
        try { Options = configuration.Load(); }
        catch (Exception ex) { Options = new(); Message = "No se pudo cargar: " + SafeError.Describe(ex); }
        LoadSourceSelection(); LoadDofStart(); LoadDailyHours(); LoadScheduleMode(); RefreshServiceState();
        WeekDays = string.Join(',', Options.Schedule.WeekDays);
        To = string.Join(';', Options.Email.To); Cc = string.Join(';', Options.Email.Cc); Bcc = string.Join(';', Options.Email.Bcc);
        Results = string.Join(';', Options.Email.NotifyResults);
        LoadSqlConnection();
        UpdatePreview();
        Action = new AsyncCommand(ExecuteAsync);
    }
    private void LoadSourceSelection()
    {
        Sources.Clear();
        var enabled = Options.EnabledSatSources.Length == 0 ? null : Options.EnabledSatSources.ToHashSet(StringComparer.Ordinal);
        foreach (var source in SourceCatalog.Sat)
            Sources.Add(new SourceSelection(source.Key, source.Article, source.Name, enabled is null || enabled.Contains(source.Key)));
    }
    private void LoadDofStart()
    {
        DofDaysBack = Math.Max(0, DateOnly.FromDateTime(DateTime.Today).DayNumber - Options.DofStartDate.DayNumber);
        DofStartMode = DofDaysBack switch { 0 => "Hoy", 1 => "Ayer", _ => "Días atrás" };
    }
    private void LoadDailyHours()
    {
        var fields = Options.Schedule.Cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || !int.TryParse(fields[0], out var minute) || minute is < 0 or > 59) return;
        var hours = fields[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var hour) && hour is >= 0 and < 24 ? new TimeOnly(hour, minute).ToString("HH:mm") : null)
            .Where(x => x is not null).ToArray();
        if (hours.Length > 0) DailyHours = string.Join("; ", hours!);
    }
    private void LoadScheduleMode() => SelectedScheduleMode = Options.Schedule.Mode switch
    {
        "interval" => "Cada N minutos", "daily" => "Diaria (una hora)", "weekly" => "Semanal",
        "cron" => "Cron avanzado", _ => "Diaria (horas)"
    };
    private void RefreshServiceState()
    {
        ServiceStatus = ServiceAdministration.Status();
        var installed = ServiceStatus != "No instalado";
        var running = ServiceStatus == "Running";
        CanInstall = !installed;
        CanUpdate = installed;
        CanUninstall = installed;
        CanStart = installed && !running;
        CanStop = installed && running;
        CanRestart = installed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServiceStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstall)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUninstall)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRestart)));
    }
    private void LoadSqlConnection()
    {
        try
        {
            var connection = AgentConfiguration.GetConnection(Options);
            if (string.IsNullOrWhiteSpace(connection)) return;
            var builder = new SqlConnectionStringBuilder(connection);
            SqlServer = builder.DataSource;
            SqlDatabase = builder.InitialCatalog;
            SqlUser = builder.UserID;
            SqlEncrypt = string.Equals(builder["Encrypt"]?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
            SqlTrustServerCertificate = builder.TrustServerCertificate;
        }
        catch { /* La configuración existente se conserva; el usuario puede sustituirla. */ }
    }
    private AgentOptions Edited()
    {
        var copy = JsonSerializer.Deserialize<AgentOptions>(JsonSerializer.Serialize(Options, AgentConfiguration.Json), AgentConfiguration.Json)!;
        copy.EnabledSatSources = Sources.Where(s => s.Enabled).Select(s => s.Key).ToArray();
        var days = DofStartMode == "Hoy" ? 0 : DofStartMode == "Ayer" ? 1 : Math.Max(0, DofDaysBack);
        copy.DofStartDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-days);
        copy.Schedule.WeekDays = Split(WeekDays).Select(int.Parse).ToArray();
        copy.Schedule.Mode = SelectedScheduleMode switch
        {
            "Cada N minutos" => "interval", "Diaria (una hora)" => "daily", "Semanal" => "weekly",
            "Cron avanzado" => "cron", _ => "daily-hours"
        };
        if (copy.Schedule.Mode == "daily-hours") copy.Schedule.Cron = BuildDailyCron(DailyHours);
        copy.Email.To = Split(To); copy.Email.Cc = Split(Cc); copy.Email.Bcc = Split(Bcc); copy.Email.NotifyResults = Split(Results);
        if (NewPassword.Length > 0) copy.Email.ProtectedPassword = AgentConfiguration.Protect(NewPassword);
        if (SqlPassword.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(SqlServer) || string.IsNullOrWhiteSpace(SqlDatabase) || string.IsNullOrWhiteSpace(SqlUser))
                throw new ArgumentException("Servidor, base de datos y usuario SQL son obligatorios al cambiar la contraseña.");
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = SqlServer.Trim(),
                InitialCatalog = SqlDatabase.Trim(),
                UserID = SqlUser.Trim(),
                Password = SqlPassword,
                IntegratedSecurity = false,
                PersistSecurityInfo = false
            };
            builder["Encrypt"] = SqlEncrypt;
            builder["TrustServerCertificate"] = SqlTrustServerCertificate;
            copy.ConnectionString = builder.ConnectionString;
            copy.ProtectedConnectionString = "";
        }
        AgentConfiguration.Validate(copy);
        return copy;
    }
    private void UpdatePreview()
    {
        var rendered = EmailTemplates.Render(Edited().Email, EmailTemplates.Sample());
        HtmlPreview = EmailPreview.SafeHtml(rendered.Html);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HtmlPreview)));
    }
    private static string[] Split(string value) => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string BuildDailyCron(string value)
    {
        var times = Split(value).Select(text => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : throw new ArgumentException("Las horas deben usar el formato HH:mm, por ejemplo 08:00; 20:00.")).Order().ToArray();
        if (times.Length == 0) throw new ArgumentException("Indique al menos una hora para la ejecución diaria.");
        if (times.Select(t => t.Minute).Distinct().Count() != 1) throw new ArgumentException("Todas las horas diarias deben usar los mismos minutos.");
        return $"{times[0].Minute} {string.Join(',', times.Select(t => t.Hour))} * * *";
    }
    public async Task ExecuteAsync(string command)
    {
        try
        {
            switch (command)
            {
                case "restore":
                    if (!File.Exists(configuration.FilePath + ".bak")) throw new InvalidDataException("No hay respaldo de configuración.");
                    var backup = new AgentConfiguration(configuration.FilePath + ".bak").Load();
                    configuration.Save(backup); Options = backup;
                    LoadSourceSelection(); LoadDofStart(); LoadDailyHours(); LoadScheduleMode();
                    WeekDays=string.Join(',',Options.Schedule.WeekDays);To=string.Join(';',Options.Email.To);Cc=string.Join(';',Options.Email.Cc);Bcc=string.Join(';',Options.Email.Bcc);Results=string.Join(';',Options.Email.NotifyResults);
                    SqlPassword="";NewPassword="";LoadSqlConnection();
                    PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(null));
                    Message="Respaldo restaurado. Recargue el servicio para aplicarlo.";break;
                case "browse":
                    var dialog = new OpenFileDialog
                    {
                        Title = "Seleccione el ejecutable publicado del servicio",
                        Filter = "Ejecutables (*.exe)|*.exe",
                        InitialDirectory = Directory.Exists(ServicePath) ? ServicePath : Path.GetDirectoryName(ServicePath)
                    };
                    if (dialog.ShowDialog() == true) ServicePath = dialog.FileName;
                    Message = "Ruta del servicio actualizada.";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServicePath)));
                    break;
                case "install": case "update": case "uninstall": case "start": case "stop": case "restart":
                    Message = await Task.Run(() => ServiceAdministration.Execute(command, ServicePath)); RefreshServiceState(); break;
                case "status":
                    RefreshServiceState();
                    Diagnostics = "SCM: " + ServiceStatus;
                    Message = "Estado del servicio actualizado.";
                    break;
                case "save":
                    var value = Edited(); configuration.Save(value); Options = value;
                    PropertyChanged?.Invoke(this, new(nameof(Options)));
                    try { var applied = await PipeAsync("reload"); Message = "Configuración guardada. " + applied.Message; }
                    catch (TimeoutException) { Message = "Configuración guardada; se aplicará al próximo inicio del servicio."; }
                    break;
                case "sql":
                    await using (var connection = new SqlConnection(AgentConfiguration.GetConnection(Edited())))
                    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await connection.OpenAsync(timeout.Token); }
                    Message = "Conexión SQL correcta (sin modificar datos)."; break;
                case "coverage":
                    var repo = new SqlRepository(AgentConfiguration.GetConnection(Edited()));
                    Diagnostics = JsonSerializer.Serialize(await repo.GetOperationalStateAsync(CancellationToken.None), AgentConfiguration.Json);
                    Message = "Cobertura y pendientes consultados."; break;
                case "preview":
                    UpdatePreview();
                    Message = "Vista previa segura actualizada; no carga imágenes ni recursos externos."; break;
                case "default-email":
                    Options.Email.SubjectTemplate = "MonitorRFC | Resumen {{Resultado}} | {{InicioUtc}}";
                    Options.Email.HtmlTemplate = EmailTemplateDefaults.SummaryHtml;
                    UpdatePreview();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Options)));
                    Message = "Plantilla predeterminada de resumen cargada. Guarde para conservarla."; break;
                case "test-email":
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                        await EmailOutbox.SendAsync(Edited().Email, EmailTemplates.Sample(), timeout.Token);
                    Message = "Correo de prueba enviado."; break;
                default:
                    var reply = await PipeAsync(command);
                    Message = reply.Message;
                    Diagnostics = "SCM: " + ServiceAdministration.Status() + Environment.NewLine + JsonSerializer.Serialize(reply.Status, AgentConfiguration.Json); break;
            }
        }
        catch (Exception ex)
        {
            Message = ex is ArgumentException or FormatException ? ex.Message : SafeError.Describe(ex);
            if (command == "status") Diagnostics = "SCM: " + ServiceAdministration.Status() + ". El canal local no está disponible.";
        }
        if (command == "status") RefreshServiceState();
        PropertyChanged?.Invoke(this, new(nameof(Message))); PropertyChanged?.Invoke(this, new(nameof(Diagnostics)));
    }
    public void NotifyActionStarted(string action)
    {
        Message = action + "…";
        PropertyChanged?.Invoke(this, new(nameof(Message)));
    }
    public sealed class SourceSelection(string key, string article, string name, bool enabled) : INotifyPropertyChanged
    {
        private bool isEnabled = enabled;
        public string Key { get; } = key;
        public string Label { get; } = $"{article.ToUpperInvariant()} · {name}";
        public bool Enabled { get => isEnabled; set { if (isEnabled == value) return; isEnabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static async Task<AgentReply> PipeAsync(string action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", "MonitorRFC.Agent.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, timeout.Token);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AgentCommand(action)) + "\n");
        await pipe.WriteAsync(bytes, timeout.Token); await pipe.FlushAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        return JsonSerializer.Deserialize<AgentReply>(await reader.ReadToEndAsync(timeout.Token), AgentConfiguration.Json)
            ?? throw new InvalidDataException("Respuesta local inválida.");
    }
    private sealed class AsyncCommand(Func<string, Task> execute) : ICommand
    {
        private bool busy;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !busy;
        public async void Execute(object? parameter)
        {
            busy = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await execute(parameter?.ToString() ?? "status"); }
            finally { busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
    }
}
