using System.Text;

namespace NovaNein.Server;

public sealed class BttnextLogMonitor(TransferEvidenceStore store, IConfiguration configuration, ILogger<BttnextLogMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = configuration["Bttnext:LogDirectory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            logger.LogWarning("BTTnext-Logüberwachung ist nicht konfiguriert.");
            return;
        }
        if (!Path.IsPathFullyQualified(directory))
        {
            logger.LogError("BTTnext-Logverzeichnis muss absolut sein.");
            return;
        }

        while (!Directory.Exists(directory) && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("BTTnext-Logverzeichnis ist noch nicht erreichbar: {Directory}", directory);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        if (stoppingToken.IsCancellationRequested) return;
        await store.InitializeLogCursorsAsync(directory, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                    await ReadNewLinesAsync(file, stoppingToken);
                var archiveDirectory = configuration["Bttnext:ArchiveDirectory"];
                if (!string.IsNullOrWhiteSpace(archiveDirectory)) await store.ReconcileArchiveAsync(archiveDirectory, stoppingToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "BTTnext-Logs konnten in diesem Durchlauf nicht gelesen werden.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ReadNewLinesAsync(string file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        var cursor = await store.GetLogCursorAsync(file, cancellationToken) ?? 0L;
        if (cursor > info.Length) cursor = 0;
        if (cursor == info.Length) return;
        if (info.Length - cursor > 16 * 1024 * 1024)
            throw new IOException("Ein einzelner BTTnext-Logzuwachs überschreitet die sichere Lesebegrenzung.");

        byte[] bytes;
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(cursor, SeekOrigin.Begin);
            bytes = new byte[checked((int)(stream.Length - cursor))];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken);
                if (count == 0) break;
                read += count;
            }
            if (read != bytes.Length) Array.Resize(ref bytes, read);
        }
        var lastNewLine = Array.LastIndexOf(bytes, (byte)'\n');
        if (lastNewLine < 0) return;

        var text = Encoding.UTF8.GetString(bytes, 0, lastNewLine + 1);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eventData = BttnextLogParser.Parse(line.TrimEnd('\r'), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            if (eventData is not null) await store.RecordAsync(eventData, cancellationToken);
        }
        await store.SaveLogCursorAsync(file, cursor + lastNewLine + 1L, cancellationToken);
    }
}
