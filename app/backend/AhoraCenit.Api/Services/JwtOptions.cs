namespace AhoraCenit.Api.Services;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Secret { get; set; } = string.Empty;

    public string Issuer { get; set; } = "AhoraCenit";

    public int ExpiresMinutes { get; set; } = 60 * 24;
}
