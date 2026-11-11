using MonitorRFC.Infrastructure.Configuration;
using MonitorRFC.Infrastructure.Logging;
using MonitorRFC.Infrastructure.Notifications;
using MonitorRFC.Service;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
var serviceRoot = Path.GetDirectoryName(new AgentConfiguration().FilePath)!;
var logDirectory = Path.Combine(Path.GetDirectoryName(serviceRoot)!, "log");
AgentConfiguration.SecureDirectory(logDirectory);
builder.Logging.AddProvider(new RollingFileLoggerProvider(() => logDirectory, "service"));
builder.Services.AddWindowsService(o => o.ServiceName = "MonitorRFC.Service");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.Services.AddSingleton<AgentConfiguration>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService(s => s.GetRequiredService<AgentWorker>());
builder.Services.AddHostedService<AgentPipe>();
var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MonitorRFC.Service.Startup")
    .LogInformation("Iniciando servicio. Bitácora: {LogDirectory}", logDirectory);
await host.RunAsync();
