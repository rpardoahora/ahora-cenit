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
        Results.Ok(await ReadAsync(settings, ct));

    private static async Task<PortalSettingsResponse> ReadAsync(IPortalSettings settings, CancellationToken ct) =>
        new(await settings.IsRegistrationEnabledAsync(ct), (await settings.GetAppsSqlModeAsync(ct)).ToString());

    private static async Task<IResult> UpdateAsync(
        UpdatePortalSettingsRequest request,
        ClaimsPrincipal principal,
        IPortalSettings settings,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        AppsSqlMode? appsSqlMode = null;
        if (request.AppsSqlMode is not null)
        {
            if (!Enum.TryParse<AppsSqlMode>(request.AppsSqlMode, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Results.BadRequest(new { message = "appsSqlMode debe ser \"Dedicated\" o \"Shared\"." });
            }

            appsSqlMode = parsed;
        }

        var previous = await ReadAsync(settings, ct);
        if (request.RegistrationEnabled is { } registrationEnabled)
        {
            await settings.SetRegistrationEnabledAsync(registrationEnabled, ct);
        }

        if (appsSqlMode is { } mode)
        {
            await settings.SetAppsSqlModeAsync(mode, ct);
        }

        var current = await ReadAsync(settings, ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Settings", "Update", entityId: null,
            parameters: request,
            result: new { Previous = previous, Current = current },
            sw.Elapsed, success: true);

        return Results.Ok(current);
    }
}
