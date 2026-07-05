namespace AhoraCenit.Api.Contracts.Auth;

public record RegisterRequest(string Email, string Password, string Name);

/// <summary>Email o nombre de usuario (el slug de cliente), indistintamente.</summary>
public record LoginRequest(string Identifier, string Password);

public record ForgotPasswordRequest(string Identifier);

public record ResetPasswordRequest(string Token, string NewPassword);

public record ConfirmEmailRequest(string Token);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record UpdateProfileRequest(string Name);

public record UserResponse(
    Guid Id,
    string Email,
    string Name,
    string Role,
    string ClientSlug,
    bool EmailConfirmed,
    DateTime CreatedAt);

public record AuthResponse(string Token, UserResponse User);

/// <summary>Devuelto por /register cuando Auth:RequireEmailConfirmation está activo: no hay token todavía.</summary>
public record RegistrationPendingResponse(bool RequiresEmailConfirmation, string Message);

public record MessageResponse(string Message);
