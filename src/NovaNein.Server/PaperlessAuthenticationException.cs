using System;

namespace NovaNein.Server;

public sealed class PaperlessAuthenticationException : InvalidOperationException
{
	public PaperlessAuthenticationException(string message)
		: base(message)
	{
	}
}
