using System.Text;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Features.AdminSettings;
using AhoraCenit.Api.Features.AdminTools;
using AhoraCenit.Api.Features.Trash;
using AhoraCenit.Api.Features.DockerHost;
using AhoraCenit.Api.Features.SqlAdmin;
using AhoraCenit.Api.Features.Applications;
using AhoraCenit.Api.Features.Auth;
using AhoraCenit.Api.Features.Products;
using AhoraCenit.Api.Features.Users;
using AhoraCenit.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Options ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<PortainerOptions>(builder.Configuration.GetSection(PortainerOptions.SectionName));
builder.Services.Configure<AdminSeedOptions>(builder.Configuration.GetSection(AdminSeedOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<AdminToolsOptions>(builder.Configuration.GetSection(AdminToolsOptions.SectionName));
builder.Services.Configure<AppsSqlOptions>(builder.Configuration.GetSection(AppsSqlOptions.SectionName));
builder.Services.Configure<FeedsOptions>(builder.Configuration.GetSection(FeedsOptions.SectionName));
builder.Services.AddHttpClient<IFeedVersionService, FeedVersionService>(client => client.Timeout = TimeSpan.FromSeconds(15));

// --- Database ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// --- Services ---
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IPortalSettings, PortalSettings>();
builder.Services.AddScoped<ISharedSqlProvisioner, SharedSqlProvisioner>();
builder.Services.Configure<TrashOptions>(builder.Configuration.GetSection(TrashOptions.SectionName));
builder.Services.AddScoped<ITrashService, TrashService>();
builder.Services.AddHostedService<TrashCleanupService>();
builder.Services.Configure<SqlAdminOptions>(builder.Configuration.GetSection(SqlAdminOptions.SectionName));
builder.Services.AddSingleton<SqlAdminService>();
builder.Services.AddHostedService<SqlAdminIdleService>();
// La primera vez descarga la imagen de CloudBeaver (~400 MB) a través de Portainer.
builder.Services.AddHttpClient("sqladmin-docker", client => client.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();
builder.Services.AddHttpClient<IPortainerClient, PortainerClient>(client =>
{
    // Crear un stack implica que Portainer ejecute "docker compose up", incluyendo el pull
    // de imágenes: para productos con imágenes grandes (p.ej. SQL Server) puede tardar bastante
    // más que el timeout por defecto de HttpClient (100s). Si expira antes de que Portainer
    // responda, el despliegue se marca como error aunque el stack se acabe creando igualmente.
    client.Timeout = TimeSpan.FromMinutes(15);
});

// --- OpenTelemetry (opcional): solo se activa si se define OTEL_EXPORTER_OTLP_ENDPOINT.
// Pensado para exportar a OpenObserve (traces + metrics + logs unificados) vía OTLP/HTTP.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    var otlpUser = builder.Configuration["OTEL_EXPORTER_OTLP_USER"];
    var otlpPassword = builder.Configuration["OTEL_EXPORTER_OTLP_PASSWORD"];
    var otlpHeaders = string.IsNullOrWhiteSpace(otlpUser)
        ? null
        : $"Authorization=Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{otlpUser}:{otlpPassword}"))}";
    var otlpBaseEndpoint = otlpEndpoint.TrimEnd('/');

    // OpenObserve espera una URL de ingesta por señal (.../v1/traces, .../v1/metrics,
    // .../v1/logs), así que la componemos explícitamente en vez de confiar en el
    // autosufijado del SDK.
    void ConfigureOtlpExporter(OtlpExporterOptions otlp, string signal)
    {
        otlp.Endpoint = new Uri($"{otlpBaseEndpoint}/v1/{signal}");
        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
        if (otlpHeaders is not null)
        {
            otlp.Headers = otlpHeaders;
        }
    }

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("ahora-cenit-api"))
        .WithTracing(tracing => tracing
            .AddSource(AuditLogger.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(otlp => ConfigureOtlpExporter(otlp, "traces")))
        .WithMetrics(metrics => metrics
            .AddMeter(AuditLogger.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(otlp => ConfigureOtlpExporter(otlp, "metrics")));

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("ahora-cenit-api"));
        logging.AddOtlpExporter(otlp => ConfigureOtlpExporter(otlp, "logs"));
    });
}

if (string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]))
{
    // Sin SMTP configurado: los emails se escriben en el log (modo dev).
    builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
}
else
{
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
}

// --- Auth ---
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtSecret = jwtSection["Secret"] ?? string.Empty;
var jwtIssuer = jwtSection["Issuer"] ?? "AhoraCenit";

builder.Services.Configure<ApiAuthOptions>(builder.Configuration.GetSection(ApiAuthOptions.SectionName));

// Dos formas de autenticarse: el JWT de sesión del portal y el token de API
// (automatizaciones). El esquema por defecto elige uno u otro en cada petición.
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = ApiTokenDefaults.SelectorScheme;
    options.DefaultAuthenticateScheme = ApiTokenDefaults.SelectorScheme;
    options.DefaultChallengeScheme = ApiTokenDefaults.SelectorScheme;
})
.AddPolicyScheme(ApiTokenDefaults.SelectorScheme, "JWT o token de API", options =>
{
    options.ForwardDefaultSelector = ApiTokenDefaults.SelectScheme;
})
.AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenDefaults.Scheme, null)
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtIssuer,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(jwtSecret) ? "DEV-ONLY-INSECURE-DEFAULT-SECRET-CHANGE-ME-1234567890" : jwtSecret)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));

// --- API / Swagger ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "AhoraCenit API",
        Version = "v1",
        Description = "API del marketplace de aplicaciones dockerizadas AhoraCenit."
    });

    var jwtSecurityScheme = new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Description = "Introduce el JWT con el prefijo 'Bearer '."
    };

    options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, jwtSecurityScheme);
    options.AddSecurityDefinition(ApiTokenDefaults.Scheme, new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = ApiTokenDefaults.ApiKeyHeader,
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
        Description = "Token de API de la plataforma (CENIT_TOKEN). También vale como 'Authorization: Bearer <token>'."
    });
    options.AddSecurityRequirement(_ => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, null),
            new List<string>()
        },
        {
            new Microsoft.OpenApi.OpenApiSecuritySchemeReference(ApiTokenDefaults.Scheme, null),
            new List<string>()
        }
    });
});

var app = builder.Build();

// --- Apply EF Core migrations automatically on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    await SeedAdminAsync(db, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminSeedOptions>>().Value);
    await SeedProductsAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api/auth").MapAuthEndpoints();
app.MapGroup("/api/products").MapProductsEndpoints();
app.MapGroup("/api/applications").MapApplicationsEndpoints();
app.MapGroup("/api/users").MapUsersEndpoints();
app.MapGroup("/api/admin/tools").MapAdminToolsEndpoints();
app.MapGroup("/api/admin/settings").MapAdminSettingsEndpoints();
app.MapGroup("/api/admin/trash").MapTrashEndpoints();
app.MapGroup("/api/admin/host").MapDockerHostEndpoints();
app.MapSqlAdminEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// Configuración pública que necesita el frontend antes de iniciar sesión.
app.MapGet("/api/config", async (IConfiguration configuration, IPortalSettings settings, ITrashService trash, CancellationToken ct) =>
{
    var appsSqlMode = await settings.GetAppsSqlModeAsync(ct);
    return Results.Ok(new
    {
        baseDomain = configuration["BaseDomain"] ?? "ahoracenit.localhost",
        registrationEnabled = await settings.IsRegistrationEnabledAsync(ct),
        // Modo SQL por defecto al desplegar; en "Shared" el formulario pide la contraseña del administrador de la app.
        appsSqlMode = appsSqlMode.ToString(),
        sharedSql = appsSqlMode == AppsSqlMode.Shared,
        // Días que pasa en la papelera lo que se borra (para avisarlo al borrar).
        trashRetentionDays = trash.RetentionDays
    });
})
    .AllowAnonymous();

// Frontend (Vite build) se sirve desde wwwroot, en el mismo servicio/puerto que la API.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

static async Task SeedAdminAsync(AppDbContext db, AdminSeedOptions adminSeed)
{
    var hasAdmin = await db.Users.AnyAsync(u => u.Role == UserRole.Admin);
    if (hasAdmin)
    {
        return;
    }

    var email = string.IsNullOrWhiteSpace(adminSeed.Email) ? "admin@ahoracenit.com" : adminSeed.Email.Trim().ToLowerInvariant();
    var password = string.IsNullOrWhiteSpace(adminSeed.Password) ? "ChangeMe123!" : adminSeed.Password;

    var existingByEmail = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (existingByEmail is not null)
    {
        existingByEmail.Role = UserRole.Admin;
        await db.SaveChangesAsync();
        return;
    }

    var admin = new User
    {
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        Name = "Admin",
        Role = UserRole.Admin,
        ClientSlug = await AhoraCenit.Api.Services.SlugGenerator.ResolveUniqueAsync(
            "admin",
            slug => db.Users.AnyAsync(u => u.ClientSlug == slug)),
        EmailConfirmed = true
    };

    db.Users.Add(admin);
    await db.SaveChangesAsync();
}

/// <summary>
/// Catálogo inicial de una instalación nueva: los productos de Data/Seed/products.json (los de Ahora que se
/// pueden desplegar con imágenes publicadas, con los logos en /products/). Solo si aún no hay ninguno.
/// </summary>
static async Task SeedProductsAsync(AppDbContext db)
{
    if (await db.Products.AnyAsync())
    {
        return;
    }

    var file = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "products.json");
    if (!File.Exists(file))
    {
        return;
    }

    var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var seeds = System.Text.Json.JsonSerializer.Deserialize<List<SeedProduct>>(await File.ReadAllTextAsync(file), jsonOptions) ?? [];

    db.Products.AddRange(seeds.Select(seed => new Product
    {
        Name = seed.Name,
        Description = seed.Description,
        ImageUrl = seed.ImageUrl,
        WebsiteUrl = seed.WebsiteUrl,
        ComposeTemplate = seed.ComposeTemplate,
        EnvVarsSchemaJson = System.Text.Json.JsonSerializer.Serialize(seed.EnvVarsSchema, jsonOptions)
    }));
    await db.SaveChangesAsync();
}

record SeedProduct(string Name, string Description, string ImageUrl, string WebsiteUrl, string ComposeTemplate, List<EnvVarDefinition> EnvVarsSchema);

// Exposed for WebApplicationFactory-based integration testing, if ever needed.
public partial class Program;
