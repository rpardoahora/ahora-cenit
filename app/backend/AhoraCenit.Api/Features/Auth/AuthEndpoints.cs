using AhoraCenit.Api.Contracts.Auth;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Features.Auth;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/confirm-email", ConfirmEmailAsync);
        group.MapPost("/forgot-password", ForgotPasswordAsync);
        group.MapPost("/reset-password", ResetPasswordAsync);
        group.MapGet("/me", GetMeAsync).RequireAuthorization();
        group.MapPatch("/me", UpdateMeAsync).RequireAuthorization();
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();

        return group;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        IJwtTokenService jwtTokenService,
        IEmailSender emailSender,
        IOptions<AuthOptions> authOptions,
        IPortalSettings portalSettings,
        CancellationToken ct)
    {
        if (!await portalSettings.IsRegistrationEnabledAsync(ct))
        {
            return Results.Json(
                new { message = "El registro de nuevos usuarios está deshabilitado. Contacta con el administrador." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { message = "Email, password y name son obligatorios." });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var emailTaken = await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct);
        if (emailTaken)
        {
            return Results.Conflict(new { message = "Ya existe un usuario con ese email." });
        }

        var baseSlug = SlugGenerator.Slugify(request.Name);
        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = "cliente";
        }

        var clientSlug = await SlugGenerator.ResolveUniqueAsync(
            baseSlug,
            slug => db.Users.AnyAsync(u => u.ClientSlug == slug, ct));

        var options = authOptions.Value;
        var requiresConfirmation = options.RequireEmailConfirmation;

        var user = new User
        {
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Name = request.Name.Trim(),
            Role = UserRole.Cliente,
            ClientSlug = clientSlug,
            EmailConfirmed = !requiresConfirmation
        };

        if (requiresConfirmation)
        {
            user.EmailConfirmationToken = TokenGenerator.Create();
            user.EmailConfirmationTokenExpiresAt = DateTime.UtcNow.AddMinutes(options.EmailConfirmationTokenExpiresMinutes);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        if (requiresConfirmation)
        {
            var confirmUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/confirmar-email?token={user.EmailConfirmationToken}";
            await emailSender.SendAsync(
                user.Email,
                "Confirma tu cuenta en ahora-cenit",
                $"<p>Hola {user.Name},</p><p>Confirma tu cuenta pulsando este enlace:</p><p><a href=\"{confirmUrl}\">{confirmUrl}</a></p>",
                ct);

            return Results.Ok(new RegistrationPendingResponse(
                true,
                "Te hemos enviado un email para confirmar tu cuenta. Revisa tu bandeja de entrada."));
        }

        var token = jwtTokenService.GenerateToken(user);
        return Results.Ok(new AuthResponse(token, ToResponse(user)));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        IJwtTokenService jwtTokenService,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var identifier = request.Identifier.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == identifier || u.ClientSlug == identifier, ct);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Results.Json(
                new { message = "Usuario o contraseña incorrectos." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (authOptions.Value.RequireEmailConfirmation && !user.EmailConfirmed)
        {
            return Results.Json(
                new { message = "Confirma tu email antes de iniciar sesión." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var token = jwtTokenService.GenerateToken(user);
        return Results.Ok(new AuthResponse(token, ToResponse(user)));
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailRequest request,
        AppDbContext db,
        IJwtTokenService jwtTokenService,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailConfirmationToken == request.Token, ct);

        if (user is null || user.EmailConfirmationTokenExpiresAt is null ||
            user.EmailConfirmationTokenExpiresAt < DateTime.UtcNow)
        {
            return Results.BadRequest(new { message = "El enlace de confirmación no es válido o ha caducado." });
        }

        user.EmailConfirmed = true;
        user.EmailConfirmationToken = null;
        user.EmailConfirmationTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);

        var token = jwtTokenService.GenerateToken(user);
        return Results.Ok(new AuthResponse(token, ToResponse(user)));
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        AppDbContext db,
        IEmailSender emailSender,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var identifier = request.Identifier.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == identifier || u.ClientSlug == identifier, ct);

        // Siempre devolvemos el mismo mensaje exista o no el usuario, para no
        // filtrar qué emails/usuarios están registrados.
        const string genericMessage = "Si el usuario existe, te hemos enviado un email con instrucciones.";

        if (user is null)
        {
            return Results.Ok(new MessageResponse(genericMessage));
        }

        var options = authOptions.Value;
        user.PasswordResetToken = TokenGenerator.Create();
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(options.PasswordResetTokenExpiresMinutes);
        await db.SaveChangesAsync(ct);

        var resetUrl = $"{options.PublicBaseUrl.TrimEnd('/')}/restablecer-password?token={user.PasswordResetToken}";
        await emailSender.SendAsync(
            user.Email,
            "Restablece tu contraseña en ahora-cenit",
            $"<p>Hola {user.Name},</p><p>Restablece tu contraseña pulsando este enlace (caduca en {options.PasswordResetTokenExpiresMinutes} minutos):</p><p><a href=\"{resetUrl}\">{resetUrl}</a></p>",
            ct);

        return Results.Ok(new MessageResponse(genericMessage));
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return Results.BadRequest(new { message = "La nueva contraseña debe tener al menos 8 caracteres." });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetToken == request.Token, ct);

        if (user is null || user.PasswordResetTokenExpiresAt is null ||
            user.PasswordResetTokenExpiresAt < DateTime.UtcNow)
        {
            return Results.BadRequest(new { message = "El enlace para restablecer la contraseña no es válido o ha caducado." });
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new MessageResponse("Contraseña actualizada. Ya puedes iniciar sesión."));
    }

    private static async Task<IResult> GetMeAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.FindAsync([userId], ct);
        return user is null ? Results.NotFound() : Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> UpdateMeAsync(
        UpdateProfileRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { message = "El nombre no puede estar vacío." });
        }

        var userId = principal.GetUserId();
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        user.Name = request.Name.Trim();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        System.Security.Claims.ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return Results.BadRequest(new { message = "La nueva contraseña debe tener al menos 8 caracteres." });
        }

        var userId = principal.GetUserId();
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return Results.BadRequest(new { message = "La contraseña actual no es correcta." });
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new MessageResponse("Contraseña actualizada."));
    }

    private static UserResponse ToResponse(User user) =>
        new(user.Id, user.Email, user.Name, user.Role.ToString(), user.ClientSlug, user.EmailConfirmed, user.CreatedAt);
}
