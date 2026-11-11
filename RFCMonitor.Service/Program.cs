using MonitorRFC.Infrastructure.Configuration;
using MonitorRFC.Infrastructure.Notifications;
using MonitorRFC.Service;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "MonitorRFC.Service");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.Services.AddSingleton<AgentConfiguration>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService(s => s.GetRequiredService<AgentWorker>());
builder.Services.AddHostedService<AgentPipe>();
await builder.Build().RunAsync();
