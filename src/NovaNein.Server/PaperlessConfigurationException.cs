using System;

namespace NovaNein.Server;

public sealed class PaperlessConfigurationException : InvalidOperationException
{
	public PaperlessConfigurationException(string message)
		: base(message)
	{
	}
}
