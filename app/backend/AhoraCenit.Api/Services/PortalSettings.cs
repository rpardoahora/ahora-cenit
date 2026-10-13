using AhoraCenit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

public interface IPortalSettings
{
    /// <summary>¿Puede registrarse cualquier persona desde el portal (POST /api/auth/register)?</summary>
    Task<bool> IsRegistrationEnabledAsync(CancellationToken ct = default);

    Task SetRegistrationEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>
    /// SQL Server de las aplicaciones que se despliegan (un cliente siempre usa este; un administrador
    /// puede elegir otro al desplegar). Sin valor guardado: el de la configuración (APPS_SQL_MODE).
    /// </summary>
    Task<AppsSqlMode> GetAppsSqlModeAsync(CancellationToken ct = default);

    Task SetAppsSqlModeAsync(AppsSqlMode mode, CancellationToken ct = default);
}

public class PortalSettings(AppDbContext db, IOptions<AuthOptions> authOptions, IOptions<AppsSqlOptions> appsSqlOptions) : IPortalSettings
{
    private const string RegistrationEnabledKey = "Auth.RegistrationEnabled";
    private const string AppsSqlModeKey = "AppsSql.Mode";

    public async Task<bool> IsRegistrationEnabledAsync(CancellationToken ct = default)
    {
        var stored = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == RegistrationEnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // Sin valor guardado: el inicial de la configuración (Auth__RegistrationEnabled, por defecto true).
        return stored is null ? authOptions.Value.RegistrationEnabled : bool.TryParse(stored, out var enabled) && enabled;
    }

    public Task SetRegistrationEnabledAsync(bool enabled, CancellationToken ct = default) =>
        SaveAsync(RegistrationEnabledKey, enabled.ToString(), ct);

    public async Task<AppsSqlMode> GetAppsSqlModeAsync(CancellationToken ct = default)
    {
        var stored = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppsSqlModeKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        return Enum.TryParse<AppsSqlMode>(stored, ignoreCase: true, out var mode) ? mode : appsSqlOptions.Value.Mode;
    }

    public Task SetAppsSqlModeAsync(AppsSqlMode mode, CancellationToken ct = default) =>
        SaveAsync(AppsSqlModeKey, mode.ToString(), ct);

    private async Task SaveAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
