namespace NovaNein.Server;

public interface IPaperlessTokenProvider
{
	bool TryGetToken(out string token);
}
