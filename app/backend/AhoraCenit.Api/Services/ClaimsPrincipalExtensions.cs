using System.Security.Claims;
using AhoraCenit.Api.Data;

namespace AhoraCenit.Api.Services;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (value is null || !Guid.TryParse(value, out var id))
        {
            throw new InvalidOperationException("El token no contiene un userId válido.");
        }

        return id;
    }

    public static UserRole GetRole(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.Role);
        if (value is null || !Enum.TryParse<UserRole>(value, out var role))
        {
            throw new InvalidOperationException("El token no contiene un rol válido.");
        }

        return role;
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.GetRole() == UserRole.Admin;

    public static string? GetClientSlug(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(JwtTokenService.ClientSlugClaimType);
}
