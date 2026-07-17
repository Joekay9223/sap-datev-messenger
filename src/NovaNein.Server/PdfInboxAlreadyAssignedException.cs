using System;

namespace NovaNein.Server;

public sealed class PdfInboxAlreadyAssignedException : Exception
{
	public PdfInboxAlreadyAssignedException(string message)
		: base(message)
	{
	}
}
