using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;
namespace MonitorRFC.Agent.Manager;

public static class ServiceAdministration
{
    private const string Name = "MonitorRFC.Service";
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
            if (!File.Exists(binary) || !string.Equals(Path.GetFileName(binary), "MonitorRFC.Service.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Seleccione el ejecutable publicado MonitorRFC.Service.exe.");
            using var scm = OpenSCManager(null, null, 0x0002);
            Check(scm);
            using var created = CreateService(scm, Name, "MonitorRFC · Recolección SAT y DOF", 0xF01FF, 0x10, 2, 1,
                "\"" + binary + "\"", null, IntPtr.Zero, null, @"NT AUTHORITY\NetworkService", null);
            Check(created);
            return "Servicio instalado con NetworkService. Configure los permisos SQL y datos antes de iniciarlo.";
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
