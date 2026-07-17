using System.Text.RegularExpressions;

namespace NovaNein.Server;

internal static class RequestActorFormatter
{
    internal static string Format(string? authenticatedCertificateThumbprint, string? claimedSapUser)
    {
        var certificate = string.IsNullOrWhiteSpace(authenticatedCertificateThumbprint)
            ? "unbekannt"
            : WorkstationRegistry.NormalizeThumbprint(authenticatedCertificateThumbprint);
        var claim = Regex.Replace(claimedSapUser ?? string.Empty, @"[\p{C};]+", " ");
        claim = Regex.Replace(claim, @"\s+", " ").Trim();
        if (claim.Length == 0) claim = "nicht-übermittelt";
        if (claim.Length > 80) claim = claim[..80];

        // Nur das mTLS-Arbeitsplatzzertifikat ist authentifiziert. Der SAP-Benutzer wird vom
        // Add-on gemeldet und deshalb im Audit ausdrücklich als nicht verifizierte Angabe benannt.
        return $"arbeitsplatz-zertifikat:{certificate};sap-benutzer-angabe:{claim}";
    }
}
