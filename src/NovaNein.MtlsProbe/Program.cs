using System.Net;
using System.Security.Cryptography.X509Certificates;

if (args.Length is not (2 or 3))
{
    Console.Error.WriteLine("Verwendung: NovaNein.MtlsProbe <https-url> <client-zertifikat-thumbprint> [--server-zertifikat-prüfen]");
    return 64;
}

using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
store.Open(OpenFlags.ReadOnly);
using var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, args[1], validOnly: false)
    .OfType<X509Certificate2>()
    .SingleOrDefault() ?? throw new InvalidOperationException("Das Clientzertifikat wurde nicht im CurrentUser-Zertifikatsspeicher gefunden.");
if (!certificate.HasPrivateKey) throw new InvalidOperationException("Das Clientzertifikat besitzt keinen privaten Schlüssel.");
using var handler = new HttpClientHandler();
handler.ClientCertificates.Add(certificate);
if (args.Length == 2) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; // ausschließlich lokaler Staging-Probe
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
using var response = await client.GetAsync(args[0]);
Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
Console.WriteLine(await response.Content.ReadAsStringAsync());
return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
