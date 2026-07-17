using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record PaperlessPage(IReadOnlyList<PaperlessDocument> Results, int Count, string? Next, string? Previous);
