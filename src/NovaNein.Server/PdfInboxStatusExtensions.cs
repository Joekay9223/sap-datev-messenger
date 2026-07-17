namespace NovaNein.Server;

internal static class PdfInboxStatusExtensions
{
	public static string ToWireValue(this PdfInboxStatus status)
	{
		return status switch
		{
			PdfInboxStatus.Unassigned => "unassigned",
			PdfInboxStatus.Assigned => "assigned",
			PdfInboxStatus.Rejected => "rejected",
			_ => "unknown",
		};
	}
}
