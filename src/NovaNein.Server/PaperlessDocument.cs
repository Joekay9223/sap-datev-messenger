using System;

namespace NovaNein.Server;

public sealed record PaperlessDocument(int Id, string Title, string Correspondent, string[] Tags, DateOnly? Created, DateOnly? DocumentDate, string? ArchiveSerialNumber, string? OriginalFileName, decimal? Amount, string? Currency);
