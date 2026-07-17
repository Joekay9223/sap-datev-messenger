using System;

namespace NovaNein.Server;

public sealed class PdfInboxDuplicateException : Exception
{
	public PdfInboxDuplicateException(string message)
		: base(message)
	{
	}
}
