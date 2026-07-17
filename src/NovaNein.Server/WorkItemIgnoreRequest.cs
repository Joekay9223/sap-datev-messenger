namespace NovaNein.Server;

public sealed record WorkItemIgnoreRequest(
    string AdminUserName,
    string AdminPassword,
    string Reason,
    int DocNum = 0);
