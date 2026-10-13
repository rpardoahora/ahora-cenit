namespace AhoraCenit.Api.Contracts.AdminSettings;

/// <param name="RegistrationEnabled">
/// true: cualquiera puede crearse una cuenta desde el portal. false: solo un
/// administrador puede dar de alta usuarios (panel o API).
/// </param>
/// <param name="AppsSqlMode">
/// SQL Server de las aplicaciones: "Dedicated" (cada una con la imagen SQL de su compose) o "Shared"
/// (todas en el SQL Server de la plataforma, un login por cliente). Un cliente siempre despliega con
/// este modo; un administrador puede elegir otro al desplegar.
/// </param>
public record PortalSettingsResponse(bool RegistrationEnabled, string AppsSqlMode);

/// <summary>Solo se cambia lo que se envía.</summary>
public record UpdatePortalSettingsRequest(bool? RegistrationEnabled = null, string? AppsSqlMode = null);
