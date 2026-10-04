using AhoraCenit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

public interface IPortalSettings
{
    /// <summary>¿Puede registrarse cualquier persona desde el portal (POST /api/auth/register)?</summary>
    Task<bool> IsRegistrationEnabledAsync(CancellationToken ct = default);

    Task SetRegistrationEnabledAsync(bool enabled, CancellationToken ct = default);
}

public class PortalSettings(AppDbContext db, IOptions<AuthOptions> authOptions) : IPortalSettings
{
    private const string RegistrationEnabledKey = "Auth.RegistrationEnabled";

    public async Task<bool> IsRegistrationEnabledAsync(CancellationToken ct = default)
    {
        var stored = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == RegistrationEnabledKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // Sin valor guardado: el inicial de la configuración (Auth__RegistrationEnabled, por defecto true).
        return stored is null ? authOptions.Value.RegistrationEnabled : bool.TryParse(stored, out var enabled) && enabled;
    }

    public async Task SetRegistrationEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == RegistrationEnabledKey, ct);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = RegistrationEnabledKey, Value = enabled.ToString() });
        }
        else
        {
            setting.Value = enabled.ToString();
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
