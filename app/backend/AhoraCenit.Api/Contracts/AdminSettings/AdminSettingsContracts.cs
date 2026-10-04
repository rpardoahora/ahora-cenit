namespace AhoraCenit.Api.Contracts.AdminSettings;

/// <param name="RegistrationEnabled">
/// true: cualquiera puede crearse una cuenta desde el portal. false: solo un
/// administrador puede dar de alta usuarios (panel o API).
/// </param>
public record PortalSettingsResponse(bool RegistrationEnabled);

public record UpdatePortalSettingsRequest(bool RegistrationEnabled);
