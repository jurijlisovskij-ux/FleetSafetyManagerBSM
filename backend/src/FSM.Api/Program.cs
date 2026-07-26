using System.Text;
using System.Text.Json.Serialization;
using FSM.Api;
using FSM.Api.Auth;
using FSM.Api.Services;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.IdentityModel.Tokens;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);
var externalConfig = Environment.GetEnvironmentVariable("FSM_CONFIG_PATH");
if (!string.IsNullOrWhiteSpace(externalConfig))
    builder.Configuration.AddJsonFile(
        externalConfig, optional: false, reloadOnChange: true);

if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "Fleet Safety Manager";
    });
}

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors();

builder.Services.AddDbContext<FsmDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<DocumentStorageService>();
builder.Services.AddScoped<EquipmentDocumentAccessService>();
builder.Services.AddSingleton<ServiceRequestExcelService>();
builder.Services.AddSingleton<ServiceRequestStorageService>();
builder.Services.AddSingleton<ServiceRequestPdfService>();
builder.Services.AddSingleton<ActiveRequestTracker>();
builder.Services.AddScoped<SystemAuditService>();
builder.Services.AddSingleton<SystemBackupService>();
builder.Services.AddSingleton<DatabaseLifecycleStatus>();
builder.Services.AddScoped<DatabaseLifecycleService>();
builder.Services.AddSingleton<InspectionListService>();
builder.Services.AddSingleton<InspectionStorageService>();
builder.Services.AddSingleton<InspectionPdfService>();
builder.Services.Configure<NotificationSettings>(
    builder.Configuration.GetSection("Notifications"));
builder.Services.Configure<SmtpSettings>(
    builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddHostedService<DailyNotificationWorker>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey =
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                    builder.Configuration["Jwt:Key"]!)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

var jwtKey = app.Configuration["Jwt:Key"];

if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException(
        "JWT signing key is missing. Configure Jwt:Key or Jwt__Key.");
}

if (jwtKey.Length < 32)
{
    throw new InvalidOperationException(
        "JWT signing key must contain at least 32 characters.");
}

if (IsPlaceholderSigningKey(jwtKey))
{
    throw new InvalidOperationException(
        "JWT signing key must not use a placeholder or default value.");
}

var allowedOrigins = app.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray()
    ?? [];

foreach (var origin in allowedOrigins)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp &&
         uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException(
            $"CORS allowed origin '{origin}' must be an absolute HTTP or HTTPS origin.");
    }
}

var swaggerEnabled =
    app.Environment.IsDevelopment() ||
    app.Configuration.GetValue<bool>("Swagger:Enabled");

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

if (allowedOrigins.Length > 0)
{
    app.UseCors(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
}
app.UseAuthentication();
app.UseMiddleware<ActiveRequestTrackingMiddleware>();
app.UseAuthorization();

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapFallbackToFile("index.html");

await app.Services.GetRequiredService<SystemBackupService>()
    .ApplyPendingRestoreAsync(CancellationToken.None);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
    await scope.ServiceProvider.GetRequiredService<DatabaseLifecycleService>()
        .InitializeAsync(CancellationToken.None);
    await SeedData.InitializeAsync(db, app.Configuration);
}

static bool IsPlaceholderSigningKey(string value) =>
    value.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase) ||
    value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
    value.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase);

app.Run();

public partial class Program;
