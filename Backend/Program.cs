using DiaErpIntegration.API.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using DiaErpIntegration.API.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DiaOptions>()
    .Bind(builder.Configuration.GetSection("DiaSettings"))
    .ValidateOnStart();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();

// Mock & State Storage
builder.Services.AddSingleton<MockDataRepository>();

// DİA client registration based on mode
var mode = builder.Configuration["DiaSettings:Mode"] ?? "Real";
if (mode.Equals("Mock", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IDiaWsClient, MockDiaWsClient>();
}
else
{
    var dia = builder.Configuration.GetSection("DiaSettings").Get<DiaOptions>() ?? new DiaOptions();

    string? GetEnv(params string[] names)
    {
        foreach (var name in names)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    // Secrets appsettings'te durmasın: env/user-secrets üzerinden gelmeli.
    dia.BaseUrl = string.IsNullOrWhiteSpace(dia.BaseUrl)
        ? GetEnv("DIA_BASEURL", "DiaSettings__BaseUrl")
        : dia.BaseUrl;
    dia.Username = string.IsNullOrWhiteSpace(dia.Username)
        ? GetEnv("DIA_USERNAME", "DiaSettings__Username")
        : dia.Username;
    dia.Password = string.IsNullOrWhiteSpace(dia.Password)
        ? GetEnv("DIA_PASSWORD", "DiaSettings__Password")
        : dia.Password;
    dia.ApiKey = string.IsNullOrWhiteSpace(dia.ApiKey)
        ? GetEnv("DIA_APIKEY", "DiaSettings__ApiKey")
        : dia.ApiKey;

    if (string.IsNullOrWhiteSpace(dia.BaseUrl)) throw new InvalidOperationException("DiaSettings/BaseUrl is required in Real mode (env: DIA_BASEURL).");
    if (string.IsNullOrWhiteSpace(dia.Username)) throw new InvalidOperationException("DiaSettings/Username is required in Real mode (env: DIA_USERNAME).");
    if (string.IsNullOrWhiteSpace(dia.Password)) throw new InvalidOperationException("DiaSettings/Password is required in Real mode (env: DIA_PASSWORD).");
    if (string.IsNullOrWhiteSpace(dia.ApiKey)) throw new InvalidOperationException("DiaSettings/ApiKey is required in Real mode (env: DIA_APIKEY).");
    if (dia.PoolFirmaKodu <= 0) throw new InvalidOperationException("DiaSettings:PoolFirmaKodu is required in Real mode.");

    var baseUrl = dia.BaseUrl.EndsWith("/") ? dia.BaseUrl : (dia.BaseUrl + "/");

    // IMPORTANT:
    // DiaSessionManager typed-client olarak kayıt edilirse "transient" instance üretir ve her istek yeni session açabilir.
    // Bu da UI'da 502/504 dalgalanmasını çok artırıyor (çoklu login fırtınası).
    // Bu yüzden session manager'ı SINGLETON yapıp, HttpClient'ını factory'den named client olarak veriyoruz.
    builder.Services.AddHttpClient("DiaSession", c => c.BaseAddress = new Uri(baseUrl));
    builder.Services.AddSingleton<DiaSessionManager>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("DiaSession");
        var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DiaOptions>>();
        var logger = sp.GetRequiredService<ILogger<DiaSessionManager>>();
        return new DiaSessionManager(http, opt, logger);
    });

    builder.Services.AddHttpClient<IDiaWsClient, DiaWsClient>(c => c.BaseAddress = new Uri(baseUrl));
}

builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<InvoiceTransferService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        b => b.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowAll");

// DiaSettings: JSON section name is "DiaSettings" (not "DiaOptions"). Env override: DiaSettings__TransferRequireSnapshot
try
{
    var diaStartup = app.Services.GetRequiredService<IOptions<DiaOptions>>().Value;
    app.Logger.LogWarning(
        "DiaSettings runtime: TransferRequireSnapshot={TransferRequireSnapshot} | override env: DiaSettings__TransferRequireSnapshot (not DiaOptions__…)",
        diaStartup.TransferRequireSnapshot);
}
catch
{
    // ignore
}

// DIAG startup stamp (stale backend detection)
try
{
    var asm = typeof(Program).Assembly;
    var name = asm.GetName();
    var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "";
    app.Logger.LogInformation("API Startup: utc={Utc} assembly={Assembly} version={Version} info={Info}",
        DateTimeOffset.UtcNow.ToString("O"),
        name.Name ?? "DiaErpIntegration.API",
        name.Version?.ToString() ?? "",
        info);
}
catch
{
    // ignore
}

// Frontend (Vite build) statik servis
// Not: Bu, dev server çalışmadığında bile UI'ı açabilmek için dist klasörünü servis eder.
var frontendDist = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "Frontend", "dist"));
if (Directory.Exists(frontendDist))
{
    app.Logger.LogInformation("Frontend dist serving enabled: {FrontendDist}", frontendDist);
    var distProvider = new PhysicalFileProvider(frontendDist);
    var defaultFiles = new DefaultFilesOptions
    {
        FileProvider = distProvider,
        RequestPath = ""
    };
    defaultFiles.DefaultFileNames.Clear();
    defaultFiles.DefaultFileNames.Add("index.html");
    app.UseDefaultFiles(defaultFiles);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = distProvider, RequestPath = "" });
}
else
{
    app.Logger.LogWarning("Frontend dist not found, UI won't be served: {FrontendDist}", frontendDist);
}

app.UseAuthorization();
app.MapControllers();

if (Directory.Exists(frontendDist))
{
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(frontendDist, "index.html"));
    });
}

app.Run();

