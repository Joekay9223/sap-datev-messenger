namespace NovaNein.Server;

public sealed record WorkItemStages(WorkItemStage Sap, WorkItemStage PdfArchive, WorkItemStage SapAttachment, WorkItemStage Validation, WorkItemStage Package, WorkItemStage Watchfolder, WorkItemStage DatevUpload, WorkItemStage DatevFinalization);
