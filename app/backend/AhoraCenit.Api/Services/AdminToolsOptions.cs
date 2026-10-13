namespace AhoraCenit.Api.Services;

/// <summary>
/// URLs públicas de las herramientas de infraestructura que se muestran en el
/// panel de administración. Si una URL se deja vacía se deriva de
/// <c>BaseDomain</c> con el esquema y puerto de <c>Auth:PublicBaseUrl</c>
/// (p.ej. https://portainer.DOMAIN), igual que las publica Traefik en infra/.
/// </summary>
public class AdminToolsOptions
{
    public const string SectionName = "AdminTools";

    public string PortainerUrl { get; set; } = string.Empty;

    public string TraefikUrl { get; set; } = string.Empty;

    public string OpenObserveUrl { get; set; } = string.Empty;

    public string NuGetUrl { get; set; } = string.Empty;

    /// <summary>Feed NuGet para los clientes (dotnet, la extensión): NUGET_PUBLIC_URL + v3/index.json.</summary>
    public string NuGetFeedUrl { get; set; } = string.Empty;

    /// <summary>Registry Docker de la plataforma (sin interfaz web: se enlaza su catálogo /v2/_catalog).</summary>
    public string RegistryUrl { get; set; } = string.Empty;
}
