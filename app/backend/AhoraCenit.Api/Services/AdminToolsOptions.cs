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
}
