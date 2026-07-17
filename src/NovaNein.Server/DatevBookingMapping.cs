using System;

namespace NovaNein.Server;

public sealed record DatevBookingMapping(string SapTaxCode, string DatevBuCode, string DatevAccount, DateOnly ValidFrom, DateOnly? ValidTo, string ApprovedBy, string MappingHash);
