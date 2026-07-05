using System.Text;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Features.Applications;
using AhoraCenit.Api.Features.Auth;
using AhoraCenit.Api.Features.Products;
using AhoraCenit.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- Options ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<PortainerOptions>(builder.Configuration.GetSection(PortainerOptions.SectionName));
builder.Services.Configure<AdminSeedOptions>(builder.Configuration.GetSection(AdminSeedOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));

// --- Database ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// --- Services ---
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddHttpClient<IPortainerClient, PortainerClient>();

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

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
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
    options.AddSecurityRequirement(_ => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, null),
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

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

static async Task SeedProductsAsync(AppDbContext db)
{
    if (await db.Products.AnyAsync())
    {
        return;
    }

    var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

    string SerializeSchema(List<EnvVarDefinition> schema) =>
        System.Text.Json.JsonSerializer.Serialize(schema, jsonOptions);

    var nextcloud = new Product
    {
        Name = "Nextcloud",
        Description = "Almacenamiento en la nube y colaboración de archivos, autoalojado.",
        ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/6/60/Nextcloud_Logo.svg",
        ComposeTemplate = """
            services:
              app:
                image: nextcloud:latest
                environment:
                  NEXTCLOUD_ADMIN_USER: ${NEXTCLOUD_ADMIN_USER}
                  NEXTCLOUD_ADMIN_PASSWORD: ${NEXTCLOUD_ADMIN_PASSWORD}
                networks:
                  - proxy
                labels:
                  - traefik.enable=true
                  - traefik.docker.network=proxy
                  - traefik.http.routers.${APP_SUBDOMAIN}.rule=Host(`${APP_SUBDOMAIN}.${BASE_DOMAIN}`)
            networks:
              proxy:
                external: true
            """,
        EnvVarsSchemaJson = SerializeSchema(
        [
            new EnvVarDefinition { Key = "NEXTCLOUD_ADMIN_USER", Label = "Usuario admin", DefaultValue = "admin", IsSecret = false },
            new EnvVarDefinition { Key = "NEXTCLOUD_ADMIN_PASSWORD", Label = "Contraseña admin", DefaultValue = "changeme", IsSecret = true }
        ])
    };

    var whoami = new Product
    {
        Name = "whoami",
        Description = "Servidor HTTP mínimo que devuelve información de la petición y del contenedor. Útil para probar despliegues y enrutado.",
        ImageUrl = "https://upload.wikimedia.org/wikipedia/commons/1/1e/Traefik_Logo.svg",
        ComposeTemplate = """
            services:
              app:
                image: traefik/whoami:latest
                environment:
                  WHOAMI_NAME: ${WHOAMI_NAME}
                networks:
                  - proxy
                labels:
                  - traefik.enable=true
                  - traefik.docker.network=proxy
                  - traefik.http.routers.${APP_SUBDOMAIN}.rule=Host(`${APP_SUBDOMAIN}.${BASE_DOMAIN}`)
                  - traefik.http.services.${APP_SUBDOMAIN}.loadbalancer.server.port=80
            networks:
              proxy:
                external: true
            """,
        EnvVarsSchemaJson = SerializeSchema(
        [
            new EnvVarDefinition { Key = "WHOAMI_NAME", Label = "Nombre mostrado", DefaultValue = "ahora-cenit-demo", IsSecret = false }
        ])
    };

    db.Products.AddRange(nextcloud, whoami);
    await db.SaveChangesAsync();
}

// Exposed for WebApplicationFactory-based integration testing, if ever needed.
public partial class Program;
