using System;

namespace NovaNein.Server;

public sealed record WorkItemQuery(
    DateOnly? FromEntryDate = null,
    DateOnly? ToEntryDate = null,
    string? Direction = null,
    string? Status = null,
    bool? PdfPresent = null,
    string? DatevStatus = null,
    bool? ErrorStatus = null,
    int Page = 1,
    int PageSize = 50,
    string? SortBy = null,
    string? SortDirection = null,
    string? Search = null);
