using System;
using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record ReconciliationPage(IReadOnlyList<ReconciliationItem> Items, Guid BatchId, int Page, int PageSize, int TotalCount, int? NextPage, IReadOnlyDictionary<string, int> Counts);
