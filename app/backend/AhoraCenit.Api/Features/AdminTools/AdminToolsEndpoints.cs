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
                "Nexus: paquetes NuGet en los feeds public (lectura anónima) e internal (solo con usuario).",
                Resolve(tools.NuGetUrl, "nuget", baseDomain, publicBaseUrl)),
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
