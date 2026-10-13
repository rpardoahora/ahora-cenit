using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Dónde consulta el portal las versiones publicadas: el registry Docker y el repositorio NuGet (BaGetter)
/// de la plataforma (por la red interna, con el usuario de plataforma) y cualquier registry o feed externo.
/// </summary>
public class FeedsOptions
{
    public const string SectionName = "Feeds";

    public string RegistryInternalUrl { get; set; } = "http://registry:5000";

    /// <summary>Hosts con los que las imágenes nombran al registry de la plataforma (REGISTRY_HOST, localhost:5000...), separados por coma.</summary>
    public string RegistryHosts { get; set; } = "localhost:5000";

    /// <summary>Dirección pública del registry, para los enlaces.</summary>
    public string RegistryPublicUrl { get; set; } = string.Empty;

    /// <summary>Feed del repositorio NuGet de la plataforma por la red interna.</summary>
    public string NugetInternalUrl { get; set; } = "http://nuget:8080/v3/index.json";

    /// <summary>Direcciones públicas del repositorio NuGet (NUGET_PUBLIC_URL, localhost:5100...), separadas por coma; la primera se usa en los enlaces.</summary>
    public string NugetPublicUrls { get; set; } = string.Empty;

    /// <summary>Usuario con lectura en el registry y en el repositorio NuGet (cenit-admin).</summary>
    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

public sealed record FeedVersions(IReadOnlyList<string> Versions, string? Latest, string? RepositoryUrl, string? Error);

public interface IFeedVersionService
{
    /// <summary>Tags de una imagen ("registry/ruta:tag") en su registry.</summary>
    Task<FeedVersions> GetImageTagsAsync(ImageReference image, CancellationToken ct = default);

    /// <summary>Versiones de un paquete NuGet en un feed (null: el repositorio de la plataforma).</summary>
    Task<FeedVersions> GetNugetVersionsAsync(string packageId, string? feedUrl, string? user, string? password, CancellationToken ct = default);
}

/// <summary>Referencia a una imagen Docker: "registry/ruta:tag".</summary>
public sealed record ImageReference(string Registry, string Repository, string? Tag)
{
    private static readonly Regex Reference = new(@"^(?<name>[^@\s]+?)(?::(?<tag>[\w][\w.-]{0,127}))?(?:@sha256:[a-f0-9]+)?$");

    /// <summary>Como se escribe la imagen: sin "docker.io/" ni el "library/" de las imágenes oficiales.</summary>
    public string Display => Registry == "docker.io"
        ? (Repository.StartsWith("library/", StringComparison.Ordinal) ? Repository["library/".Length..] : Repository)
        : $"{Registry}/{Repository}";

    public static ImageReference? Parse(string image)
    {
        var match = Reference.Match(image.Trim());
        if (!match.Success || image.Contains('$'))
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        var tag = match.Groups["tag"].Success ? match.Groups["tag"].Value : null;
        var slash = name.IndexOf('/');
        var first = slash > 0 ? name[..slash] : string.Empty;
        // El primer tramo es un registry si tiene punto o puerto, o es "localhost".
        if (first.Length > 0 && (first.Contains('.') || first.Contains(':') || first == "localhost"))
        {
            return new ImageReference(first.ToLowerInvariant(), name[(slash + 1)..], tag);
        }

        return new ImageReference("docker.io", name.Contains('/') ? name : $"library/{name}", tag);
    }
}

public class FeedVersionService(HttpClient http, IOptions<FeedsOptions> options) : IFeedVersionService
{
    private static readonly Regex BearerParam = new(@"(?<key>realm|service|scope)=""(?<value>[^""]*)""");

    public async Task<FeedVersions> GetImageTagsAsync(ImageReference image, CancellationToken ct = default)
    {
        var feeds = options.Value;
        var isPlatform = IsPlatformRegistry(image.Registry);
        var repositoryUrl = RegistryRepositoryUrl(image, isPlatform);
        var baseUrl = isPlatform
            ? feeds.RegistryInternalUrl.TrimEnd('/')
            : $"https://{(image.Registry == "docker.io" ? "registry-1.docker.io" : image.Registry)}";
        var url = $"{baseUrl}/v2/{image.Repository}/tags/list?n=1000";

        try
        {
            var auth = isPlatform ? Basic(feeds.User, feeds.Password) : null;
            var tags = new List<string>();
            string? next = url;
            // Los registries paginan la lista (Docker Hub, de 100 en 100 y por orden alfabético): hay que leerla entera.
            for (var page = 0; next is not null && page < MaxTagPages; page++)
            {
                using var response = await SendAsync(next, auth, ct);
                HttpResponseMessage? retried = null;
                if (response.StatusCode == HttpStatusCode.Unauthorized && !isPlatform && auth is null
                    && await GetAnonymousTokenAsync(response, ct) is { } token)
                {
                    auth = new AuthenticationHeaderValue("Bearer", token);
                    retried = await SendAsync(next, auth, ct);
                }

                using (retried)
                {
                    var result = retried ?? response;
                    if (result.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new FeedVersions([], null, repositoryUrl, "La imagen no está en el registry.");
                    }

                    if (!result.IsSuccessStatusCode)
                    {
                        return new FeedVersions([], null, repositoryUrl, $"El registry respondió {(int)result.StatusCode}.");
                    }

                    using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
                    if (json.RootElement.TryGetProperty("tags", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        tags.AddRange(list.EnumerateArray().Select(t => t.GetString()).OfType<string>());
                    }

                    next = NextPage(result, baseUrl);
                }
            }

            var sorted = VersionComparer.SortVersions(tags.Distinct());
            return new FeedVersions(sorted, sorted.FirstOrDefault(), repositoryUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new FeedVersions([], null, repositoryUrl, $"No se pudo consultar el registry: {ex.Message}");
        }
    }

    public async Task<FeedVersions> GetNugetVersionsAsync(string packageId, string? feedUrl, string? user, string? password, CancellationToken ct = default)
    {
        var feeds = options.Value;
        string repositoryUrl;
        if (feedUrl is null || IsPlatformNuget(feedUrl))
        {
            // El de la plataforma, por la red interna y con el usuario de plataforma; el enlace, a su web.
            feedUrl = feeds.NugetInternalUrl;
            user = feeds.User;
            password = feeds.Password;
            var publicBase = feeds.NugetPublicUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.TrimEnd('/');
            repositoryUrl = publicBase is null ? feedUrl : $"{publicBase}/packages/{Uri.EscapeDataString(packageId)}";
        }
        else
        {
            repositoryUrl = feedUrl.Contains("nuget.org", StringComparison.OrdinalIgnoreCase)
                ? $"https://www.nuget.org/packages/{packageId}"
                : feedUrl;
        }

        try
        {
            // Protocolo NuGet v3: el índice del feed dice dónde está la lista de versiones (PackageBaseAddress).
            using var index = await GetJsonAsync(feedUrl, user, password, ct);
            var baseAddress = index.RootElement.GetProperty("resources").EnumerateArray()
                .Where(r => r.TryGetProperty("@type", out var type) && type.GetString()?.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal) == true)
                .Select(r => r.GetProperty("@id").GetString())
                .FirstOrDefault();
            if (baseAddress is null)
            {
                return new FeedVersions([], null, repositoryUrl, "El feed no publica la lista de versiones (PackageBaseAddress).");
            }

            using var versions = await GetJsonAsync($"{baseAddress.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json", user, password, ct);
            var list = versions.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()).OfType<string>().ToList();
            var sorted = VersionComparer.SortVersions(list);
            return new FeedVersions(sorted, sorted.FirstOrDefault(), repositoryUrl, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new FeedVersions([], null, repositoryUrl, "El paquete no está en el feed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new FeedVersions([], null, repositoryUrl, $"No se pudo consultar el feed: {ex.Message}");
        }
    }

    /// <summary>¿Apunta la URL al repositorio NuGet de la plataforma (por su dirección pública o la interna)?</summary>
    private bool IsPlatformNuget(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var target))
        {
            return false;
        }

        return options.Value.NugetPublicUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(options.Value.NugetInternalUrl)
            .Append("http://cenit-nuget:8080")
            .Any(h => Uri.TryCreate(h, UriKind.Absolute, out var u)
                && string.Equals(u.Host, target.Host, StringComparison.OrdinalIgnoreCase) && u.Port == target.Port);
    }

    private bool IsPlatformRegistry(string registry) =>
        options.Value.RegistryHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(h => string.Equals(h, registry, StringComparison.OrdinalIgnoreCase));

    private string? RegistryRepositoryUrl(ImageReference image, bool isPlatform)
    {
        if (isPlatform)
        {
            var publicUrl = options.Value.RegistryPublicUrl.TrimEnd('/');
            return publicUrl.Length == 0 ? null : $"{publicUrl}/v2/{image.Repository}/tags/list";
        }

        return image.Registry switch
        {
            "docker.io" => image.Repository.StartsWith("library/", StringComparison.Ordinal)
                ? $"https://hub.docker.com/_/{image.Repository["library/".Length..]}"
                : $"https://hub.docker.com/r/{image.Repository}",
            "mcr.microsoft.com" => $"https://mcr.microsoft.com/artifact/mar/{image.Repository}",
            _ => $"https://{image.Registry}/{image.Repository}"
        };
    }

    private const int MaxTagPages = 50;
    private static readonly Regex NextLink = new(@"<(?<url>[^>]+)>\s*;\s*rel=""?next""?");

    private Task<HttpResponseMessage> SendAsync(string url, AuthenticationHeaderValue? auth, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = auth;
        return http.SendAsync(request, ct);
    }

    /// <summary>Siguiente página según la cabecera Link (rel="next"), que suele ser relativa al registry.</summary>
    private static string? NextPage(HttpResponseMessage response, string baseUrl)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        var match = NextLink.Match(string.Join(',', values));
        if (!match.Success)
        {
            return null;
        }

        var link = match.Groups["url"].Value;
        return link.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link : baseUrl + link;
    }

    /// <summary>Registries públicos (Docker Hub, ghcr...): token anónimo según el desafío WWW-Authenticate.</summary>
    private async Task<string?> GetAnonymousTokenAsync(HttpResponseMessage unauthorized, CancellationToken ct)
    {
        var challenge = unauthorized.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (challenge?.Parameter is null)
        {
            return null;
        }

        var parameters = BearerParam.Matches(challenge.Parameter).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        if (!parameters.TryGetValue("realm", out var realm))
        {
            return null;
        }

        var query = string.Join('&', parameters.Where(p => p.Key != "realm").Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        using var tokenResponse = await http.GetAsync($"{realm}?{query}", ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }

        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        return tokenJson.RootElement.TryGetProperty("token", out var t) ? t.GetString()
            : tokenJson.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string? user, string? password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password))
        {
            request.Headers.Authorization = Basic(user, password);
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{url} respondió {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
}

/// <summary>Ordena versiones de más nueva a más antigua; descarta lo que no parece una versión ("latest", "sha-...").</summary>
public static class VersionComparer
{
    private static readonly Regex VersionLike = new(@"^v?\d+(\.\d+)*([-+][0-9A-Za-z.-]+)?$");

    public static List<string> SortVersions(IEnumerable<string> versions) =>
        versions.Where(v => VersionLike.IsMatch(v)).OrderByDescending(v => v, Comparer<string>.Create(Compare)).ToList();

    public static bool IsNewer(string candidate, string current) =>
        VersionLike.IsMatch(candidate) && VersionLike.IsMatch(current) && Compare(candidate, current) > 0;

    /// <summary>Numérico por tramos (1.10 > 1.9); a igualdad, la versión final gana a la prerelease (1.0 > 1.0-beta).</summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var x = i < coreA.Length ? coreA[i] : 0;
            var y = i < coreB.Length ? coreB[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        if (preA.Length == 0 && preB.Length == 0)
        {
            return 0;
        }

        if (preA.Length == 0 || preB.Length == 0)
        {
            return preA.Length == 0 ? 1 : -1;
        }

        var partsA = preA.Split('.');
        var partsB = preB.Split('.');
        for (var i = 0; i < Math.Max(partsA.Length, partsB.Length); i++)
        {
            if (i >= partsA.Length) return -1;
            if (i >= partsB.Length) return 1;
            var numeric = long.TryParse(partsA[i], out var na) & long.TryParse(partsB[i], out var nb);
            var result = numeric ? na.CompareTo(nb) : string.Compare(partsA[i], partsB[i], StringComparison.OrdinalIgnoreCase);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static (long[] Core, string Pre) Split(string version)
    {
        var value = version.TrimStart('v', 'V');
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var pre = dash >= 0 ? value[(dash + 1)..] : string.Empty;
        return (core.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray(), pre);
    }
}
