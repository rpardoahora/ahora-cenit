namespace AhoraCenit.Api.Contracts.Users;

public record CreateUserRequest(string Email, string Password, string Name, string Role);

public record UpdateUserRequest(string Email, string Name, string Role);

public record ResetUserPasswordRequest(string NewPassword);

public record AdminUserResponse(
    Guid Id,
    string Email,
    string Name,
    string Role,
    string ClientSlug,
    bool EmailConfirmed,
    DateTime CreatedAt,
    int ApplicationsCount);
