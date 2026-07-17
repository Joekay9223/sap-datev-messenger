using System.Diagnostics;

namespace NovaNein.SapAddon;

public static class ArchivedPdfLauncher
{
    public static async Task<bool> OpenAsync(
        SapDocumentContext context,
        Func<SapDocumentContext, CancellationToken, Task<NovaNeinArchivedPdf?>> download,
        CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (download is null) throw new ArgumentNullException(nameof(download));

        var archived = await download(context, cancellationToken);
        if (archived is null) return false;

        var directory = Path.Combine(Path.GetTempPath(), "NovaNein", "Archiv");
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(archived.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            fileName = $"NovaNein-{context.DocNum}.pdf";
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}-{fileName}");
        using (var output = File.Create(path))
            await output.WriteAsync(archived.Content, 0, archived.Content.Length, cancellationToken);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return true;
    }
}
