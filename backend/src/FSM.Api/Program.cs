using System.Security.Claims;
using System.Text;
using FSM.Api;
using FSM.Api.Authorization;
using FSM.Api.Auth;
using FSM.Api.Services;
using FSM.Api.Synchronization;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "Fleet Safety Manager";
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var configuredConnection = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Default database connection is missing.");
var resolvedConnection = DatabaseConnectionResolver.Resolve(
    configuredConnection, AppContext.BaseDirectory);
ApplyPendingDatabaseRestore(resolvedConnection);
builder.Services.AddDbContext<FsmDbContext>(options =>
    options.UseSqlite(resolvedConnection));

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<DocumentStorageService>();
builder.Services.AddSingleton<ServiceRequestExcelService>();
builder.Services.AddSingleton<InspectionListService>();
builder.Services.Configure<NotificationSettings>(
    builder.Configuration.GetSection("Notifications"));
builder.Services.Configure<SmtpSettings>(
    builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<SynchronizationOptions>(
    builder.Configuration.GetSection(SynchronizationOptions.SectionName));
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<AdminPasswordRecoveryService>();
builder.Services.AddHostedService<DailyNotificationWorker>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.GetFullPath(
            builder.Configuration["DataProtection:KeyPath"] ??
            Path.Combine("Data", "Keys"),
            AppContext.BaseDirectory)));
builder.Services.AddHttpClient("Synchronization", client =>
{
    var timeoutMinutes = builder.Configuration.GetValue<int?>(
        "Synchronization:RequestTimeoutMinutes") ?? 10;
    client.Timeout = TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes));
});
builder.Services.AddScoped<SynchronizationTokenService>();
builder.Services.AddScoped<SynchronizationFileService>();
builder.Services.AddScoped<SynchronizationExchangeService>();
builder.Services.AddScoped<SynchronizationStatusService>();
builder.Services.AddScoped<SynchronizationClient>();
builder.Services.AddScoped<SynchronizationProvisioningService>();
builder.Services.AddScoped<OfficeDiscoveryService>();
builder.Services.AddSingleton<ShipConfigurationProtectionService>();
builder.Services.AddScoped<VesselAccessService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<RecoveryKeyService>();
builder.Services.AddHostedService<SynchronizationWorker>();
builder.Services.AddHostedService<SynchronizationDiscoveryWorker>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
});

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT key is missing.");
var applicationEdition = builder.Configuration["Application:Edition"] ?? "Office";

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
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userIdValue = context.Principal?
                    .FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(userIdValue, out var userId))
                {
                    context.Fail("The authenticated user identifier is invalid.");
                    return;
                }

                var db = context.HttpContext.RequestServices
                    .GetRequiredService<FsmDbContext>();
                var currentUser = await db.Users
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == userId);
                if (currentUser is null ||
                    !currentUser.IsActive ||
                    currentUser.IsLocked)
                {
                    context.Fail("The authenticated account is no longer active.");
                    return;
                }

                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    foreach (var claim in identity
                        .FindAll(identity.RoleClaimType)
                        .ToList())
                    {
                        identity.RemoveClaim(claim);
                    }

                    identity.AddClaim(new Claim(
                        identity.RoleClaimType,
                        currentUser.Role.ToString()));
                }

                var isShipEdition = applicationEdition.Equals(
                    "Ship",
                    StringComparison.OrdinalIgnoreCase);
                var roleAllowed = isShipEdition
                    ? currentUser.Role.ToString() == "Crew"
                    : currentUser.Role.ToString() is
                        "OfficeAdmin" or "Superintendent";

                if (!roleAllowed)
                {
                    context.Fail("The token role is not permitted for this application edition.");
                }
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FsmDbContext>();
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrade.ApplyAsync(db);
    await SeedData.InitializeAsync(db);
    await scope.ServiceProvider
        .GetRequiredService<SynchronizationProvisioningService>()
        .EnsureExistingInstallationsAsync();
    if (scope.ServiceProvider.GetRequiredService<RecoveryKeyService>().EnsureCreated())
    {
        scope.ServiceProvider.GetRequiredService<AuditService>()
            .Add("Recovery key generated", "System", null);
        await db.SaveChangesAsync();
    }
}

app.Run();

static void ApplyPendingDatabaseRestore(string connectionString)
{
    var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:") return;
    var databasePath = Path.GetFullPath(builder.DataSource);
    var pendingPath = databasePath + ".restore-pending";
    if (!File.Exists(pendingPath)) return;

    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    if (File.Exists(databasePath))
    {
        var safetyBackup = databasePath + $".before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
        File.Copy(databasePath, safetyBackup, overwrite: false);
    }
    File.Move(pendingPath, databasePath, overwrite: true);
    Console.WriteLine("The staged Fleet Safety Manager database restore was applied.");
}
