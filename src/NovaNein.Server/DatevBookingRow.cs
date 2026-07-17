using System;

namespace NovaNein.Server;

public sealed record DatevBookingRow(Guid Id, Guid BatchId, int RowNumber, DateOnly DocumentDate, decimal Amount, string DebitCredit, string Currency, string Account, string CounterAccount, string BuCode, string Reference1, string Reference2, string BookingText, string NormalizedReference, string PartnerAccount, string RowSha256, string RawJson);
