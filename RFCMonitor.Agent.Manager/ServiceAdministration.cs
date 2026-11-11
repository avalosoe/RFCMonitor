using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;
namespace MonitorRFC.Agent.Manager;

public static class ServiceAdministration
{
    private const string Name = "MonitorRFC.Service";
    public static readonly string InstallDirectory = @"C:\Servicios\MonitorRFC";
    public static string Status()
    {
        try { using var service = new ServiceController(Name); return service.Status.ToString(); }
        catch (InvalidOperationException) { return "No instalado"; }
    }
    public static string Execute(string action, string binary)
    {
        if (action == "install")
        {
            if (Status() != "No instalado") return "El servicio ya está instalado.";
            binary = Path.GetFullPath(binary);
            if (!File.Exists(binary) || !string.Equals(Path.GetExtension(binary), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Seleccione el archivo .exe publicado que ejecuta el servicio.");
            var sourceDirectory = Path.GetDirectoryName(binary)!;
            Directory.CreateDirectory(InstallDirectory);
            if (!string.Equals(Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar), InstallDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                CopyPublication(sourceDirectory, InstallDirectory);
            binary = Path.Combine(InstallDirectory, Path.GetFileName(binary));
            if (!File.Exists(binary)) throw new InvalidDataException("No se pudo copiar el ejecutable del servicio a " + InstallDirectory + ".");
            using var scm = OpenSCManager(null, null, 0x0002);
            Check(scm);
            using var created = CreateService(scm, Name, "MonitorRFC · Recolección SAT y DOF", 0xF01FF, 0x10, 2, 1,
                "\"" + binary + "\"", null, IntPtr.Zero, null, @"NT AUTHORITY\NetworkService", null);
            Check(created);
            return "Servicio instalado en " + InstallDirectory + " con inicio automático. Configure SQL antes de iniciarlo.";
        }
        if (action == "uninstall")
        {
            if (Status() == "No instalado") return "El servicio ya está desinstalado.";
            Execute("stop", binary);
            using var scm = OpenSCManager(null, null, 0x0001); Check(scm);
            using var service = OpenService(scm, Name, 0x10000); Check(service);
            if (!DeleteService(service)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return "Servicio desinstalado; datos, evidencia y configuración conservados.";
        }
        if (action == "update")
        {
            if (Status() == "No instalado") return "El servicio no está instalado.";
            binary = Path.GetFullPath(binary);
            if (!File.Exists(binary) || !string.Equals(Path.GetExtension(binary), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Seleccione el archivo .exe publicado que ejecuta el servicio.");
            var sourceDirectory = Path.GetDirectoryName(binary)!;
            if (string.Equals(Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar), InstallDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Seleccione una publicación externa; el ejecutable instalado no puede actualizarse a sí mismo.");
            Execute("stop", binary);
            CopyPublication(sourceDirectory, InstallDirectory);
            Execute("start", binary);
            return "Publicación actualizada y servicio reiniciado.";
        }
        using var controller = new ServiceController(Name);
        if (action is "stop" or "restart")
        {
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                if (controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
        }
        if (action is "start" or "restart")
        {
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Running)
            {
                if (controller.Status != ServiceControllerStatus.StartPending) controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        return "Estado: " + Status();
    }
    private static void CopyPublication(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
    private static void Check(ServiceHandle handle) { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateService(ServiceHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
