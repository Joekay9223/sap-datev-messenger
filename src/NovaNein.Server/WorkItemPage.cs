using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record WorkItemPage(
    IReadOnlyList<WorkItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int? NextPage,
    IReadOnlyList<WorkItem> UploadTargets);
