using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MonitorRFC.Application;
using MonitorRFC.Contracts;
using MonitorRFC.Infrastructure.Configuration;

namespace MonitorRFC.Infrastructure.Notifications;

public sealed class EmailOutbox(AgentConfiguration configuration) : IExecutionNotifier
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public sealed class Pending
    {
        public ExecutionReport Report { get; set; } = new();
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
        public string? LastError { get; set; }
    }
    private string DirectoryPath(AgentOptions o) => Path.Combine(o.DataDirectory, "outbox");
    public int PendingCount()
    {
        var dir=DirectoryPath(configuration.Load());
        return Directory.Exists(dir) ? Directory.GetFiles(dir,"*.json").Length : 0;
    }
    public Task EnqueueAsync(ExecutionReport report, CancellationToken ct)
    {
        var o = configuration.Load();
        if (!o.Email.Enabled || !o.Email.NotifyResults.Contains(report.Resultado)) return Task.CompletedTask;
        var dir=DirectoryPath(o);
        Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,report.EjecucionId.ToString("N")+".json");
        if (!File.Exists(path)) AgentConfiguration.AtomicWrite(path,JsonSerializer.Serialize(new Pending{Report=report},AgentConfiguration.Json));
        return Task.CompletedTask;
    }
    public async Task DispatchAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0,ct)) return;
        try
        {
            var o=configuration.Load();
            var dir=DirectoryPath(o);
            if (!o.Email.Enabled || !Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir,"*.json").Order())
            {
                ct.ThrowIfCancellationRequested();
                Pending pending;
                try { pending=JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(file,ct),AgentConfiguration.Json)
                    ?? throw new JsonException("Notificación vacía."); }
                catch (JsonException) { File.Move(file,file+".invalid",true); continue; }
                if (pending.NextAttemptUtc>DateTimeOffset.UtcNow) continue;
                try
                {
                    await SendAsync(o.Email,pending.Report,ct);
                    var sentDir=Path.Combine(o.DataDirectory,"mail-sent");
                    Directory.CreateDirectory(sentDir);
                    File.Move(file,Path.Combine(sentDir,Path.GetFileName(file)),true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    pending.Attempts++;
                    pending.LastError=ex.GetType().Name; // SMTP messages can contain addresses or authentication material.
                    pending.NextAttemptUtc=DateTimeOffset.UtcNow.AddMinutes(Math.Min(360,Math.Pow(2,Math.Min(pending.Attempts,9))));
                    AgentConfiguration.AtomicWrite(file,JsonSerializer.Serialize(pending,AgentConfiguration.Json));
                }
            }
        }
        finally { gate.Release(); }
    }
    public static async Task SendAsync(EmailOptions options, ExecutionReport report, CancellationToken ct)
    {
        if (!options.Enabled) throw new InvalidOperationException("Habilite y configure SMTP.");
        var (subject,html)=EmailTemplates.Render(options,report);
        var message=new MimeMessage {Subject=subject, MessageId=report.EjecucionId.ToString("N")+"@monitorrfc.local"};
        message.From.Add(new MailboxAddress(options.SenderName,options.SenderAddress));
        foreach (var s in options.To) message.To.Add(MailboxAddress.Parse(s));
        foreach (var s in options.Cc) message.Cc.Add(MailboxAddress.Parse(s));
        foreach (var s in options.Bcc) message.Bcc.Add(MailboxAddress.Parse(s));
        message.Body=new BodyBuilder {HtmlBody=html, TextBody=$"MonitorRFC: {report.Resultado}. Ejecución {report.EjecucionId}. Registros: {report.Fuentes.Sum(f=>f.Registros)}."}.ToMessageBody();
        using var client=new SmtpClient {Timeout=30000};
        await client.ConnectAsync(options.Host,options.Port,options.Security=="StartTls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,ct);
        if (!string.IsNullOrWhiteSpace(options.UserName)) await client.AuthenticateAsync(options.UserName,AgentConfiguration.Unprotect(options.ProtectedPassword),ct);
        await client.SendAsync(message,ct);
        await client.DisconnectAsync(true,ct);
    }
}
