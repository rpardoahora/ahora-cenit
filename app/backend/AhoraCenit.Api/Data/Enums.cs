namespace AhoraCenit.Api.Data;

public enum UserRole
{
    Admin = 0,
    Cliente = 1
}

public enum ApplicationStatus
{
    Deploying = 0,
    Running = 1,
    Stopped = 2,
    Error = 3,
    Deleted = 4
}
