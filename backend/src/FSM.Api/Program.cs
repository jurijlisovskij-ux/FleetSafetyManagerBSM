using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using FSM.Api;
using FSM.Api.Authorization;
using FSM.Api.Auth;
using FSM.Api.Services;
using FSM.Api.Synchronization;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using FSM.Infrastructure.Persistence;
using FSM.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var resetAdministratorPassword = args.Contains(
    "--reset-administrator-password",
    StringComparer.OrdinalIgnoreCase);
var hostArguments = resetAdministratorPassword
    ? args.Where(value => !value.Equals(
        "--reset-administrator-password",
        StringComparison.OrdinalIgnoreCase)).ToArray()
    : args;
var builder = WebApplication.CreateBuilder(hostArguments);

if (resetAdministratorPassword)
{
    Environment.ExitCode = await ResetAdministratorPasswordAsync(builder.Configuration);
    return;
}

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
builder.Services.AddHostedService<DailyNotificationWorker>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.GetFullPath(
            builder.Configuration["DataProtection:KeyPath"] ??
            Path.Combine("Data", "Keys"),
            AppContext.BaseDirectory)));
builder.Services.AddSingleton<EmailRecoverySettingsStore>();
builder.Services.AddSingleton<EmailSender>();
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

static async Task<int> ResetAdministratorPasswordAsync(IConfiguration configuration)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("Administrator password reset is available only on Windows.");
        return 2;
    }

    var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
    if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("Run the reset tool as a Windows administrator.");
        return 3;
    }

    var edition = configuration["Application:Edition"] ?? "Office";
    if (!edition.Equals("Office", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Administrator password reset is available only in Office Edition.");
        return 4;
    }

    var newPassword = Console.In.ReadLine() ?? string.Empty;
    var repeatedPassword = Console.In.ReadLine() ?? string.Empty;
    if (newPassword.Length < 8)
    {
        Console.Error.WriteLine("The new password must contain at least 8 characters.");
        return 5;
    }
    if (!string.Equals(newPassword, repeatedPassword, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("The passwords do not match.");
        return 6;
    }

    var configuredConnection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Default database connection is missing.");
    var connection = DatabaseConnectionResolver.Resolve(configuredConnection, AppContext.BaseDirectory);
    var options = new DbContextOptionsBuilder<FsmDbContext>()
        .UseSqlite(connection)
        .Options;
    await using var db = new FsmDbContext(options);
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrade.ApplyAsync(db);
    await SeedData.InitializeAsync(db);

    var administrator = await db.Users.SingleAsync(x => x.Role == UserRole.OfficeAdmin);
    administrator.PasswordHash = PasswordHasher.Hash(newPassword);
    administrator.IsActive = true;
    administrator.IsLocked = false;
    administrator.FailedLoginAttempts = 0;
    administrator.ForcePasswordChange = false;
    db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(),
        DateUtc = DateTime.UtcNow,
        Action = "Administrator password reset locally",
        Operator = WindowsIdentity.GetCurrent().Name,
        TargetUser = administrator.Username,
        ComputerName = Environment.MachineName,
        Result = "Success",
        Summary = "Password reset with the local elevated recovery tool."
    });
    await db.SaveChangesAsync();
    Console.WriteLine("Administrator password changed successfully.");
    return 0;
}
