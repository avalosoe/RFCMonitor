using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MonitorRFC.Contracts;
using MonitorRFC.Infrastructure.Configuration;
namespace MonitorRFC.Service;

public sealed class AgentPipe(AgentWorker worker, ILogger<AgentPipe> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var acl = new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create("MonitorRFC.Agent.v1", PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 65536, acl);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var bytes = new List<byte>(); var buffer = new byte[1];
                while (bytes.Count < 4096 && await pipe.ReadAsync(buffer, timeout.Token) > 0 && buffer[0] != '\n') bytes.Add(buffer[0]);
                if (bytes.Count >= 4096) throw new InvalidDataException("Comando demasiado grande.");
                var command = JsonSerializer.Deserialize<AgentCommand>(Encoding.UTF8.GetString(bytes.ToArray()), AgentConfiguration.Json);
                logger.LogInformation("Comando local recibido: {Action}", command?.Action ?? "(vacío)");
                var reply = worker.Command(command?.Action ?? "");
                var response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, AgentConfiguration.Json) + "\n");
                await pipe.WriteAsync(response, timeout.Token); await pipe.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Comando local rechazado."); }
        }
    }
}
