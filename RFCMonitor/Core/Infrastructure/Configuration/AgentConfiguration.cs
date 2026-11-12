using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;

namespace MonitorRFC.Infrastructure.Configuration;

public sealed class AgentConfiguration
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath { get; }
    public AgentConfiguration(string? path = null) => FilePath = Path.GetFullPath(path ??
        Environment.GetEnvironmentVariable("MONITORRFC_CONFIG") ??
        @"C:\Servicios\MonitorRFC\config\agentsettings.json");
    public AgentOptions Load()
    {
        var value = File.Exists(FilePath) ? JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(FilePath), Json)
            ?? throw new InvalidDataException("Configuración vacía.") : new AgentOptions();

        // Normalizar de inmediato cualquier referencia previa a variables UTC para usar la fecha/hora local
        if (value.Email is not null)
        {
            if (value.Email.SubjectTemplate?.Contains("Utc", StringComparison.OrdinalIgnoreCase) == true)
            {
                value.Email.SubjectTemplate = System.Text.RegularExpressions.Regex.Replace(value.Email.SubjectTemplate, @"\{\{\s*InicioUtc\s*\}\}", "{{Inicio}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                value.Email.SubjectTemplate = System.Text.RegularExpressions.Regex.Replace(value.Email.SubjectTemplate, @"\{\{\s*FinUtc\s*\}\}", "{{Fin}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                value.Email.SubjectTemplate = System.Text.RegularExpressions.Regex.Replace(value.Email.SubjectTemplate, @"\{\{\s*FechaUtc\s*\}\}", "{{Fecha}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            if (value.Email.HtmlTemplate?.Contains("Utc", StringComparison.OrdinalIgnoreCase) == true)
            {
                value.Email.HtmlTemplate = System.Text.RegularExpressions.Regex.Replace(value.Email.HtmlTemplate, @"\{\{\s*InicioUtc\s*\}\}", "{{Inicio}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                value.Email.HtmlTemplate = System.Text.RegularExpressions.Regex.Replace(value.Email.HtmlTemplate, @"\{\{\s*FinUtc\s*\}\}", "{{Fin}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                value.Email.HtmlTemplate = System.Text.RegularExpressions.Regex.Replace(value.Email.HtmlTemplate, @"\{\{\s*FechaUtc\s*\}\}", "{{Fecha}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        Validate(value);
        return value;
    }
    public void Save(AgentOptions value)
    {
        Validate(value);
        if (!string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            value.ProtectedConnectionString = Protect(value.ConnectionString);
            value.ConnectionString = "";
        }
        value.Version++;
        SecureDirectory(Path.GetDirectoryName(FilePath)!);
        SecureDirectory(value.DataDirectory);
        if (File.Exists(FilePath))
        {
            try { _ = Load(); File.Copy(FilePath, FilePath + ".bak", true); }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidDataException) { /* Conservar el respaldo válido anterior. */ }
        }
        AtomicWrite(FilePath, JsonSerializer.Serialize(value, Json));
    }
    public static string GetConnection(AgentOptions value) =>
        Environment.GetEnvironmentVariable("MONITORRFC_SQL") ?? (string.IsNullOrWhiteSpace(value.ProtectedConnectionString)
        ? value.ConnectionString : Unprotect(value.ProtectedConnectionString));
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("El almacén protegido requiere Windows.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine));
    }
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("El almacén protegido requiere Windows.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine));
    }
    public static void Validate(AgentOptions value)
    {
        if (value.Schedule is null || value.Email is null || value.EnabledSatSources is null)
            throw new ArgumentException("Configuración incompleta.");
        if (!Path.IsPathFullyQualified(value.DataDirectory)) throw new ArgumentException("El directorio de datos debe ser absoluto.");
        var fullPath = Path.GetFullPath(value.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (fullPath == Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar) ||
            new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.CommonApplicationData }
            .Any(f => string.Equals(fullPath, Environment.GetFolderPath(f).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use una subcarpeta exclusiva de MonitorRFC para los datos.");
        if (value.HttpTimeoutSeconds is < 5 or > 3600 || value.MaxDownloadBytes < 1024)
            throw new ArgumentException("Límites de descarga inválidos.");
        if (value.DofLookbackDays is < 1 or > 3650 || value.DofDaysPerCycle is < 1 or > 3660)
            throw new ArgumentException("Rangos DOF inválidos.");
        if (value.DofStartDate < new DateOnly(1900, 1, 1) || value.DofStartDate > DateOnly.FromDateTime(DateTime.Now))
            throw new ArgumentException("Fecha inicial DOF fuera del rango permitido.");
        if (!string.IsNullOrWhiteSpace(value.ConnectionString)) _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(value.ConnectionString);
        if (value.EnabledSatSources.Any(k => !SourceCatalog.Sat.Any(s => s.Key == k)))
            throw new ArgumentException("Fuente SAT desconocida.");
        Scheduling.ScheduleCalculator.Validate(value.Schedule);
        Notifications.EmailTemplates.Validate(value.Email);
    }
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) return;
        // Keep the owner, inheritance and existing access rules. The service used
        // to replace the complete ACL on every startup, which removed permissions
        // assigned by the administrator and could make NetworkService the owner.
        var directory = new DirectoryInfo(path);
        var acl = directory.GetAccessControl();
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null) })
            acl.SetAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
}
