using System.Runtime.InteropServices;

namespace NovaNein.SapAddonInstaller;

internal static class Program
{
    private const string InstallPathFileName = "sap-addon-install-path.txt";
    private static readonly (string Resource, string FileName)[] Payload =
    {
        ("NovaNein.Payload.NovaNein.SapAddon.dll", "NovaNein.SapAddon.dll"),
        ("NovaNein.Payload.NovaNein.SapAddonHost.exe", "NovaNein.SapAddonHost.exe"),
        ("NovaNein.Payload.NovaNein.SapAddonHost.exe.config", "NovaNein.SapAddonHost.exe.config")
    };

    [STAThread]
    private static int Main(string[] args)
    {
        var uninstall = args.Any(IsUninstallArgument);
        var sapContext = args.FirstOrDefault(value => value.IndexOf('|') >= 0);
        try
        {
            var context = SapInstallContext.Parse(sapContext, requireInstallPath: !uninstall);
            SapInstallApi.Prepare(context.ApiPath);
            if (uninstall)
            {
                Uninstall();
                SapInstallApi.EndUninstall(string.Empty, true);
            }
            else
            {
                Install(context.InstallPath!);
                SapInstallApi.EndInstallEx(string.Empty, true);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log("FEHLER: " + ex);
            try
            {
                if (uninstall) SapInstallApi.EndUninstall(ex.Message, false);
                else SapInstallApi.EndInstallEx(ex.Message, false);
            }
            catch (Exception callbackError) { Log("SAP-Rückmeldung fehlgeschlagen: " + callbackError); }
            return 1;
        }
    }

    private static bool IsUninstallArgument(string value) =>
        string.Equals(value, "/u", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "uninstall", StringComparison.OrdinalIgnoreCase);

    private static void Install(string installPath)
    {
        Directory.CreateDirectory(installPath);
        var assembly = typeof(Program).Assembly;
        var transactionRoot = Path.Combine(installPath, ".novanein-install-" + Guid.NewGuid().ToString("N"));
        var stagingDirectory = Path.Combine(transactionRoot, "staging");
        var backupDirectory = Path.Combine(transactionRoot, "backup");
        var changes = new List<(string Target, string? Backup, bool Created)>();
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);
        try
        {
            foreach (var item in Payload)
            {
                using var source = assembly.GetManifestResourceStream(item.Resource)
                    ?? throw new InvalidOperationException($"Eingebettete Datei fehlt: {item.Resource}");
                var staged = Path.Combine(stagingDirectory, item.FileName);
                using var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            foreach (var item in Payload)
            {
                var staged = Path.Combine(stagingDirectory, item.FileName);
                var target = Path.Combine(installPath, item.FileName);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupDirectory, item.FileName);
                    File.Replace(staged, target, backup, ignoreMetadataErrors: true);
                    changes.Add((target, backup, false));
                }
                else
                {
                    File.Move(staged, target);
                    changes.Add((target, null, true));
                }
            }

            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(Path.Combine(StateDirectory, InstallPathFileName), installPath);
        }
        catch (Exception installError)
        {
            try { RollBack(changes); }
            catch (Exception rollbackError) { throw new AggregateException("Installation und Wiederherstellung des vorherigen Standes sind fehlgeschlagen.", installError, rollbackError); }
            throw;
        }
        finally { TryDeleteDirectory(transactionRoot); }
        Log("Installation erfolgreich: " + installPath);
    }

    private static void Uninstall()
    {
        var pathFile = new[] { Path.Combine(StateDirectory, InstallPathFileName), Path.Combine(LegacyStateDirectory, InstallPathFileName) }
            .FirstOrDefault(File.Exists);
        if (pathFile is null) throw new InvalidOperationException("Der gespeicherte NovaNein-Installationspfad fehlt.");
        var installPath = File.ReadAllText(pathFile).Trim();
        foreach (var item in Payload)
        {
            var target = Path.Combine(installPath, item.FileName);
            if (File.Exists(target)) File.Delete(target);
        }
        File.Delete(pathFile);
        if (Directory.Exists(installPath) && !Directory.EnumerateFileSystemEntries(installPath).Any()) Directory.Delete(installPath);
        Log("Deinstallation erfolgreich: " + installPath);
    }

    private static void RollBack(IEnumerable<(string Target, string? Backup, bool Created)> changes)
    {
        foreach (var change in changes.Reverse())
        {
            if (change.Created)
            {
                if (File.Exists(change.Target)) File.Delete(change.Target);
                continue;
            }
            if (string.IsNullOrWhiteSpace(change.Backup) || !File.Exists(change.Backup))
                throw new InvalidOperationException($"Sicherungsdatei für {change.Target} fehlt.");
            if (File.Exists(change.Target)) File.Delete(change.Target);
            File.Move(change.Backup, change.Target);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static string LegacyStateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NovaNein");
    private static string StateDirectory => Path.Combine(LegacyStateDirectory, "SapAddonInstaller");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.AppendAllText(Path.Combine(StateDirectory, "sap-addon-installer.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed class SapInstallContext
    {
        private SapInstallContext(string? installPath, string apiPath) { InstallPath = installPath; ApiPath = apiPath; }
        public string? InstallPath { get; }
        public string ApiPath { get; }

        public static SapInstallContext Parse(string? value, bool requireInstallPath)
        {
            var elements = (value ?? string.Empty).Split(new[] { '|' }, 2);
            var installPath = elements.Length == 2 ? elements[0].Trim().Trim('"') : null;
            var suppliedApi = elements.Length == 2 ? elements[1].Trim().Trim('"') : null;
            if (requireInstallPath && string.IsNullOrWhiteSpace(installPath))
                throw new InvalidOperationException("Dieses Installationsprogramm muss von SAP Business One gestartet werden.");

            var candidates = new[]
            {
                ReplaceApiFileName(suppliedApi),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SAP", "SAP Business One", "AddOnInstallAPI_x64.dll")
            };
            var apiPath = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                ?? throw new FileNotFoundException("AddOnInstallAPI_x64.dll wurde nicht gefunden.");
            return new SapInstallContext(installPath, apiPath);
        }

        private static string? ReplaceApiFileName(string? suppliedApi)
        {
            if (string.IsNullOrWhiteSpace(suppliedApi)) return null;
            var fileName = Path.GetFileName(suppliedApi);
            if (string.Equals(fileName, "AddOnInstallAPI.dll", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Path.GetDirectoryName(suppliedApi) ?? string.Empty, "AddOnInstallAPI_x64.dll");
            return string.Equals(fileName, "AddOnInstallAPI_x64.dll", StringComparison.OrdinalIgnoreCase)
                ? suppliedApi
                : null;
        }
    }

    private static class SapInstallApi
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string pathName);

        [DllImport("AddOnInstallAPI_x64.dll", EntryPoint = "EndInstallEx", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi)]
        internal static extern int EndInstallEx(string message, [MarshalAs(UnmanagedType.Bool)] bool success);

        [DllImport("AddOnInstallAPI_x64.dll", EntryPoint = "EndUninstall", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi)]
        internal static extern int EndUninstall(string message, [MarshalAs(UnmanagedType.Bool)] bool success);

        internal static void Prepare(string apiPath)
        {
            var directory = Path.GetDirectoryName(apiPath) ?? throw new InvalidOperationException("Ungültiger AddOnInstallAPI-Pfad.");
            if (!SetDllDirectory(directory)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Der SAP-API-Pfad konnte nicht gesetzt werden.");
            Environment.CurrentDirectory = directory;
        }
    }
}
