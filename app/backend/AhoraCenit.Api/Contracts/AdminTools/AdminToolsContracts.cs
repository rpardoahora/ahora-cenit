namespace AhoraCenit.Api.Contracts.AdminTools;

/// <param name="Url">Su web.</param>
/// <param name="FeedUrl">Solo el repositorio NuGet: el feed para dotnet/NuGet (en local, localhost:5100, porque dotnet no resuelve *.localhost).</param>
public record AdminToolResponse(string Key, string Name, string Description, string Url, string? FeedUrl = null);
