using AhoraCenit.Api.Contracts.AdminTools;
using AhoraCenit.Api.Services;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Features.AdminTools;

public static class AdminToolsEndpoints
{
    public static RouteGroupBuilder MapAdminToolsEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization("AdminOnly");

        group.MapGet("/", List);

        return group;
    }

    private static IResult List(
        IOptions<AdminToolsOptions> toolsOptions,
        SqlAdminService sqlAdmin,
        IOptions<AuthOptions> authOptions,
        IConfiguration configuration)
    {
        var tools = toolsOptions.Value;
        var baseDomain = configuration["BaseDomain"] ?? "ahoracenit.localhost";
        var publicBaseUrl = authOptions.Value.PublicBaseUrl;

        return Results.Ok(new[]
        {
            new AdminToolResponse(
                "portainer",
                "Portainer",
                "Gestión de contenedores, stacks, volúmenes y redes de Docker.",
                Resolve(tools.PortainerUrl, "portainer", baseDomain, publicBaseUrl)),
            new AdminToolResponse(
                "traefik",
                "Traefik",
                "Dashboard del proxy inverso: routers, servicios, middlewares y certificados.",
                Resolve(tools.TraefikUrl, "traefik", baseDomain, publicBaseUrl)),
            new AdminToolResponse(
                "openobserve",
                "OpenObserve",
                "Trazas, métricas y logs de auditoría del portal.",
                Resolve(tools.OpenObserveUrl, "telemetry", baseDomain, publicBaseUrl)),
            new AdminToolResponse(
                "nuget",
                "Repositorio NuGet",
                "BaGetter: feed NuGet privado. Leer con usuario y contraseña (cenit-admin, cenit-apps o admin); subir con el token de plataforma como API key, desde un origen registrado con usuario (sin él el feed responde 401). La web pide el login de administración.",
                Resolve(tools.NuGetUrl, "nuget", baseDomain, publicBaseUrl),
                string.IsNullOrWhiteSpace(tools.NuGetFeedUrl)
                    ? Resolve(tools.NuGetUrl, "nuget", baseDomain, publicBaseUrl) + "/v3/index.json"
                    : tools.NuGetFeedUrl.Trim()),
            new AdminToolResponse(
                "registry",
                "Repositorio Docker",
                "Registry privado de imágenes. No tiene interfaz web: el enlace abre su catálogo de imágenes (usuario admin o cenit-admin). Para subir: docker login con el usuario cenit-admin y el token de plataforma.",
                Resolve(tools.RegistryUrl, "registry", baseDomain, publicBaseUrl) + "/v2/_catalog"),
            new AdminToolResponse(
                "sqladmin",
                "Explorador de BBDD",
                "CloudBeaver sobre el SQL Server de la plataforma: bases, tablas y consultas. Está apagado: se arranca al pulsar Explorar BBDD (también desde cada instancia, conectado a su base) y se para solo tras unos minutos sin uso.",
                sqlAdmin.PublicUrl),
        });
    }

    /// <summary>
    /// Devuelve la URL configurada o, si está vacía, la deriva como
    /// {esquema}://{subdominio}.{BaseDomain}{:puerto}, tomando esquema y puerto
    /// de la URL pública del portal.
    /// </summary>
    private static string Resolve(string configured, string subdomain, string baseDomain, string publicBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().TrimEnd('/');
        }

        var scheme = "http";
        var portSuffix = string.Empty;
        if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUri))
        {
            scheme = publicUri.Scheme;
            portSuffix = publicUri.IsDefaultPort ? string.Empty : $":{publicUri.Port}";
        }

        return $"{scheme}://{subdomain}.{baseDomain}{portSuffix}";
    }
}
