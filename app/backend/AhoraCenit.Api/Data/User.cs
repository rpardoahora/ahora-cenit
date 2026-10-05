namespace AhoraCenit.Api.Data;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Email { get; set; }

    public required string PasswordHash { get; set; }

    public required string Name { get; set; }

    public UserRole Role { get; set; } = UserRole.Cliente;

    /// <summary>
    /// Unique slug identifying this client, used e.g. as part of the default
    /// subdomain for the applications they deploy. Generated from the name.
    /// </summary>
    public required string ClientSlug { get; set; }

    public bool EmailConfirmed { get; set; }

    public string? EmailConfirmationToken { get; set; }

    public DateTime? EmailConfirmationTokenExpiresAt { get; set; }

    public string? PasswordResetToken { get; set; }

    public DateTime? PasswordResetTokenExpiresAt { get; set; }

    /// <summary>
    /// Login del cliente en el SQL Server común (modo AppsSql:Mode = Shared). Lo
    /// comparten todas sus instancias; null si nunca se ha necesitado.
    /// </summary>
    public string? SqlLogin { get; set; }

    public string? SqlPassword { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
