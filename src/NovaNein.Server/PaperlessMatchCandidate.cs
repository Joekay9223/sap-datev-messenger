using System.Collections.Generic;

namespace NovaNein.Server;

public sealed record PaperlessMatchCandidate(PaperlessDocument Document, int Score, IReadOnlyList<string> Reasons);
