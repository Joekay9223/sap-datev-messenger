using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class RequestActorFormatterTests
{
    [Fact]
    public void Labels_the_sap_user_as_an_unverified_claim_and_normalizes_control_text()
    {
        var actor = RequestActorFormatter.Format("AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA", " manager;\r\ngefälscht ");

        Assert.Equal("arbeitsplatz-zertifikat:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA;sap-benutzer-angabe:manager gefälscht", actor);
        Assert.DoesNotContain(";sap:", actor, StringComparison.Ordinal);
    }
}
