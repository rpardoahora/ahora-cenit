using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AhoraCenit.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Tokens de API para automatizaciones (CI, scripts, otros servicios). El
/// instalador configura aquí el mismo token de plataforma que sirve para
/// <c>docker login</c> en el registry y como contraseña del usuario de NuGet en Nexus.
/// </summary>
public class ApiAuthOptions
{
    public const string SectionName = "ApiAuth";

    /// <summary>Tokens válidos, separados por coma o punto y coma. Vacío = API por token deshabilitada.</summary>
    public string Tokens { get; set; } = string.Empty;

    /// <summary>Email del usuario admin en cuyo nombre actúan las peticiones con token (por defecto, el de AdminSeed).</summary>
    public string UserEmail { get; set; } = string.Empty;

    public IEnumerable<string> GetTokens() =>
        Tokens.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class ApiTokenDefaults
{
    /// <summary>Esquema que valida el token de API.</summary>
    public const string Scheme = "ApiToken";

    /// <summary>Esquema por defecto: decide por petición si es un JWT de sesión o un token de API.</summary>
    public const string SelectorScheme = "JwtOrApiToken";

    public const string ApiKeyHeader = "X-Api-Key";

    public const string AuthMethodClaimType = "auth_method";

    /// <summary>
    /// Un JWT tiene siempre tres segmentos separados por puntos; cualquier otro
    /// valor de Bearer (o la cabecera X-Api-Key) se trata como token de API.
    /// </summary>
    public static string SelectScheme(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey(ApiKeyHeader))
        {
            return Scheme;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = authorization["Bearer ".Length..].Trim();
            return value.Count(c => c == '.') == 2 ? JwtBearerDefaults.AuthenticationScheme : Scheme;
        }

        return authorization.StartsWith("token ", StringComparison.OrdinalIgnoreCase)
            ? Scheme
            : JwtBearerDefaults.AuthenticationScheme;
    }
}

public class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiAuthOptions> apiAuthOptions,
    IOptions<AdminSeedOptions> adminSeedOptions,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ExtractToken(Request);
        if (string.IsNullOrEmpty(presented))
        {
            return AuthenticateResult.NoResult();
        }

        var validTokens = apiAuthOptions.Value.GetTokens().ToList();
        if (validTokens.Count == 0 || !validTokens.Any(t => FixedTimeEquals(t, presented)))
        {
            return AuthenticateResult.Fail("Token de API no válido.");
        }

        var user = await ResolveServiceUserAsync();
        if (user is null)
        {
            return AuthenticateResult.Fail("No hay ningún usuario administrador en cuyo nombre actuar.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtTokenService.ClientSlugClaimType, user.ClientSlug),
            new(ApiTokenDefaults.AuthMethodClaimType, "api-token")
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private static string? ExtractToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ApiTokenDefaults.ApiKeyHeader, out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey.ToString().Trim();
        }

        var authorization = request.Headers.Authorization.ToString();
        foreach (var prefix in new[] { "Bearer ", "token " })
        {
            if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return authorization[prefix.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>Compara en tiempo constante (sobre el hash, para no filtrar tampoco la longitud).</summary>
    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private async Task<User?> ResolveServiceUserAsync()
    {
        var email = apiAuthOptions.Value.UserEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = adminSeedOptions.Value.Email;
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.Role == UserRole.Admin);

        return user ?? await db.Users
            .Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync();
    }
}
