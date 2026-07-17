using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaNein.Server;

public static class WorkItemUploadTargets
{
    public static IEnumerable<WorkItem> Select(IEnumerable<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return WorkItemOrdering.Apply(
            items.Where(item => !item.Ignored && item.DocEntry > 0 &&
                (string.Equals(item.PdfState, "missing", StringComparison.OrdinalIgnoreCase) || !item.DocumentId.HasValue)),
            "docNum",
            "asc");
    }
}
