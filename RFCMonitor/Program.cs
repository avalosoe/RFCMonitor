using System.Diagnostics;
using System.IO.Compression;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using MonitorRFC.Application;
using MonitorRFC.Infrastructure.Logging;
using MonitorRFC.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var apiLogDirectory = Path.Combine(AppContext.BaseDirectory, "log");
builder.Logging.AddProvider(new RollingFileLoggerProvider(() => apiLogDirectory, "api"));
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(o =>
{
    // The global RFC export can be large. Compression is negotiated through
    // Accept-Encoding, so clients that do not support it keep receiving JSON.
    o.EnableForHttps = true;
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<SqlRepository>(_ => new SqlRepository(builder.Configuration.GetConnectionString("MonitorRFC") ?? ""));
builder.Services.AddScoped<IRfcQueryService>(s => s.GetRequiredService<SqlRepository>());
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddConcurrencyLimiter("consultas", limiter =>
    {
        limiter.PermitLimit = Math.Max(1, builder.Configuration.GetValue("Api:ConcurrentQueries", 4));
        limiter.QueueLimit = 0;
    });
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "5";
        await Results.Problem(statusCode: 429, title: "Consultas simultáneas excedidas; reintente.").ExecuteAsync(context.HttpContext);
    };
});
var app = builder.Build();
app.Logger.LogInformation("API iniciada. Bitácora: {LogDirectory}", apiLogDirectory);
app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    try { await next(); }
    finally
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        app.Logger.LogInformation("HTTP {Method} {Path} => {StatusCode} en {ElapsedMs:0} ms. TraceId={TraceId}",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsed, context.TraceIdentifier);
    }
});
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError(exception, "Consulta fallida. TraceId={TraceId}", context.TraceIdentifier);
    var status = exception is BadHttpRequestException bad ? bad.StatusCode : 503;
    await Results.Problem(statusCode: status, title: status == 413 ? "Cuerpo demasiado grande." : "No se pudo completar la consulta.",
        extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.UseResponseCompression();
app.UseRateLimiter();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/", () => Results.Ok(new { servicio = "MonitorRFC", estado = "online" }));
app.MapControllers().RequireRateLimiting("consultas");
app.Run();
public partial class Program { }
