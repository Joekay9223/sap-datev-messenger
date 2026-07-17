using System;

namespace NovaNein.Server;

public sealed record ReconciliationDecisionRequest(string ExpectedHash, string Decision, Guid? DatevRowId, string Reason);
