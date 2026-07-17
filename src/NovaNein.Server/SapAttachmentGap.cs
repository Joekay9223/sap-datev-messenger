using System;

namespace NovaNein.Server;

public sealed record SapAttachmentGap(SapDocumentKind Kind, int DocEntry, int DocNum, DateOnly EntryDate, int? AttachmentEntry);
