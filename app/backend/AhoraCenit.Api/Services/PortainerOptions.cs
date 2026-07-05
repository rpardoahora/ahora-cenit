namespace AhoraCenit.Api.Services;

public class PortainerOptions
{
    public const string SectionName = "Portainer";

    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public int EndpointId { get; set; } = 1;
}
