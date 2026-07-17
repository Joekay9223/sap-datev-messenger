using System;

namespace NovaNein.Server;

public sealed record DatevTransferRequest(
    Guid Id,
    Guid DocumentId,
    string PackageSha256,
    string Status,
    DateTimeOffset RequestedAt,
    string RequestedBy,
    int Attempts,
    string? LastError,
    DateTimeOffset? BridgeStagedAt,
    DateTimeOffset? WatchfolderDeliveredAt);
