using System.Diagnostics;
using System.Security.Claims;
using AhoraCenit.Api.Contracts.AdminSettings;
using AhoraCenit.Api.Services;

namespace AhoraCenit.Api.Features.AdminSettings;

public static class AdminSettingsEndpoints
{
    public static RouteGroupBuilder MapAdminSettingsEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization("AdminOnly");

        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync);

        return group;
    }

    private static async Task<IResult> GetAsync(IPortalSettings settings, CancellationToken ct) =>
        Results.Ok(new PortalSettingsResponse(await settings.IsRegistrationEnabledAsync(ct)));

    private static async Task<IResult> UpdateAsync(
        UpdatePortalSettingsRequest request,
        ClaimsPrincipal principal,
        IPortalSettings settings,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var previous = await settings.IsRegistrationEnabledAsync(ct);

        await settings.SetRegistrationEnabledAsync(request.RegistrationEnabled, ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Settings", "Update", entityId: null,
            parameters: new { request.RegistrationEnabled },
            result: new { Previous = previous, Current = request.RegistrationEnabled },
            sw.Elapsed, success: true);

        return Results.Ok(new PortalSettingsResponse(request.RegistrationEnabled));
    }
}
