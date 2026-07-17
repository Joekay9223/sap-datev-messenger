using System.Configuration;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using NovaNein.SapAddon;

namespace NovaNein.SapAddonHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // WinForms must be initialized before SAP COM, MessageBox or any form can
        // create the first native window handle. Calling this later causes the
        // add-on to fail at startup inside an already running SAP client.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var companionMode = args.Length == 1 && string.Equals(args[0], "--companion", StringComparison.OrdinalIgnoreCase);
        if (!companionMode && args.Length != 1) { MessageBox.Show("Das NovaNein-Add-on muss von SAP Business One gestartet werden.", "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        using var instanceMutex = new Mutex(initiallyOwned: true, name: "Global\\NovaNein-SapAddonHost", createdNew: out var createdNew);
        if (!createdNew) return;
        try
        {
            var serverUrl = RequireSetting("NovaNeinServerUrl");
            var thumbprint = RequireSetting("NovaNeinCertificateThumbprint");
            dynamic application = ConnectToSap(args, companionMode);
            var clientVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
            using (var client = new NovaNeinServerClient(new Uri(serverUrl, UriKind.Absolute), thumbprint))
            using (var watcher = new System.Windows.Forms.Timer { Interval = 750 })
            {
                using var coresuiteMenuItem = new ToolStripMenuItem("Coresuite-PDF erzeugen");
                using var coresuiteBatchMenuItem = new ToolStripMenuItem("Fehlende Ausgangsbelege einmalig drucken");
                NovaNeinCockpitForm? cockpit = null;
                DocumentDropForm? dropForm = null;
                var coresuiteBatchRunning = false;
                void OpenCockpit()
                {
                    if (cockpit is not null && !cockpit.IsDisposed)
                    {
                        cockpit.BringToFront();
                        return;
                    }
                    cockpit = new NovaNeinCockpitForm(
                        client.GetStatisticsAsync,
                        client.ScanMissingPdfAttachmentsAsync,
                        client.GetWeeklyReminderAsync,
                        client.SetWeeklyReminderAsync,
                        ct => client.CheckHealthAsync(clientVersion, "NovaNein-Cockpit geöffnet", ct));
                    cockpit.FormClosed += (_, __) => cockpit = null;
                    cockpit.Show(dropForm);
                }
                async Task<bool> OpenArchivedPdfAsync(SapDocumentContext context, CancellationToken cancellationToken = default) =>
                    await ArchivedPdfLauncher.OpenAsync(context, (document, ct) => client.DownloadArchivedPdfAsync(document, ct), cancellationToken);
                async Task ExportCurrentCoresuitePdfAsync()
                {
                    var context = TryReadDocument(application);
                    if (context is null || context.Direction != SapDocumentDirection.Outgoing)
                    {
                        MessageBox.Show("Bitte zuerst eine SAP-Ausgangsrechnung öffnen.", "Coresuite-PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    try
                    {
                        var export = ExportCoresuitePdf(application, context);
                        var progress = await client.SubmitOutgoingPdfAsync(context.DocEntry, context.DocNum, export.PdfPath, context.SapUser);
                        MessageBox.Show($"Coresuite-PDF für SAP {context.DocNum} wurde erzeugt und an NovaNein übergeben.\n\nStatus: {progress.Status}", "Coresuite-PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Coresuite-PDF konnte nicht erzeugt oder übergeben werden: " + ex.Message, "Coresuite-PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                async Task PrintMissingOutgoingCoresuitePdfsAsync()
                {
                    if (coresuiteBatchRunning) return;
                    coresuiteBatchRunning = true;
                    coresuiteBatchMenuItem.Enabled = false;
                    try
                    {
                        var missing = await client.GetMissingOutgoingItemsAsync();
                        if (missing.Count == 0)
                        {
                            MessageBox.Show("NovaNein meldet derzeit keine offenen Ausgangsbelege ohne PDF.", "Coresuite-Sammeldruck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }

                        var confirmation = MessageBox.Show(
                            $"NovaNein meldet {missing.Count} offene Ausgangsbelege ohne PDF.\n\nDiese Belege werden jetzt jeweils genau einmal über Coresuite T0000009 exportiert und an NovaNein übergeben. Fortfahren?",
                            "Coresuite-Sammeldruck",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);
                        if (confirmation != DialogResult.Yes) return;

                        var sapUser = Convert.ToString(application.Company.UserName, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        var succeeded = 0;
                        var failures = new List<string>();
                        foreach (var item in missing)
                        {
                            try
                            {
                                var context = new SapDocumentContext(SapDocumentDirection.Outgoing, item.DocEntry, item.DocNum, sapUser);
                                var export = ExportCoresuitePdf(application, context);
                                await client.SubmitOutgoingPdfAsync(item.DocEntry, item.DocNum, export.PdfPath, sapUser);
                                succeeded++;
                            }
                            catch (Exception ex)
                            {
                                failures.Add($"SAP {item.DocNum}: {ex.Message}");
                            }
                        }

                        var summary = $"Coresuite-Sammeldruck abgeschlossen.\n\nErfolgreich: {succeeded}\nFehlgeschlagen: {failures.Count}";
                        if (failures.Count > 0)
                            summary += "\n\n" + string.Join("\n", failures.Take(10));
                        MessageBox.Show(summary, "Coresuite-Sammeldruck", MessageBoxButtons.OK, failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Die Liste der fehlenden Ausgangsbelege konnte nicht verarbeitet werden: " + ex.Message, "Coresuite-Sammeldruck", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        coresuiteBatchRunning = false;
                        coresuiteBatchMenuItem.Enabled = true;
                    }
                }
                using var form = new DocumentDropForm(
                    client.SubmitPdfAsync,
                    client.EnableWeeklyReminderAsync,
                    client.ScanMissingPdfAttachmentsAsync,
                    () => TryReadDocument(application),
                    client.GetDocumentForSapAsync,
                    client.GetValidationReasonsAsync,
                    client.GetDatevStatusAsync,
                    client.ReviewDocumentAsync,
                    OpenArchivedPdfAsync,
                        client.GetNotificationsAsync,
                        client.MarkNotificationReadAsync,
                        client.CheckHealthAsync,
                    clientVersion,
                    OpenCockpit);
                using var archiveMenuItem = new ToolStripMenuItem("Archivierten Beleg öffnen") { Enabled = false };
                using var cockpitMenuItem = new ToolStripMenuItem("NovaNein Cockpit");
                using var trayMenu = new ContextMenuStrip();
                trayMenu.Items.AddRange(new ToolStripItem[] { archiveMenuItem, coresuiteMenuItem, coresuiteBatchMenuItem, cockpitMenuItem });
                using var trayIcon = new NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = "NovaNein Belegarchiv",
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };
                async Task OpenArchiveFromTrayAsync()
                {
                    SapDocumentContext? context = TryReadDocument(application);
                    if (context is null)
                    {
                        MessageBox.Show("Bitte zuerst eine gebuchte SAP-Eingangs- oder Ausgangsrechnung öffnen.", "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    try
                    {
                        if (!await OpenArchivedPdfAsync(context))
                            MessageBox.Show("Für diesen SAP-Beleg ist noch keine NovaNein-PDF archiviert.", "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Archivierte PDF konnte nicht geöffnet werden: " + ex.Message, "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                archiveMenuItem.Click += async (_, __) => await OpenArchiveFromTrayAsync();
                coresuiteMenuItem.Click += async (_, __) => await ExportCurrentCoresuitePdfAsync();
                coresuiteBatchMenuItem.Click += async (_, __) => await PrintMissingOutgoingCoresuitePdfsAsync();
                cockpitMenuItem.Click += (_, __) => OpenCockpit();
                trayIcon.DoubleClick += async (_, __) => await OpenArchiveFromTrayAsync();
                dropForm = form;
                form.FormClosed += (_, __) => cockpit?.Close();
                string? current = null;
                watcher.Tick += (_, __) =>
                {
                    var context = TryReadDocument(application);
                    var key = context is null ? null : $"{context.Direction}:{context.DocEntry}:{context.DocNum}:{context.SapUser}";
                    if (!string.Equals(key, current, StringComparison.Ordinal))
                    {
                        current = key;
                        archiveMenuItem.Enabled = context is not null;
                        if (context is null) form.ClearDocument();
                        else form.SetDocument(context);
                    }
                };
                watcher.Start();
                Application.Run(form);
            }
        }
        catch (Exception ex)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NovaNein");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "sap-addon-host.log"), $"{DateTimeOffset.Now:O} {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
            }
            catch { /* Ein Startfehler darf nicht durch die Diagnoseprotokollierung verdeckt werden. */ }
            if (!companionMode)
                MessageBox.Show(ex.Message, "NovaNein konnte nicht gestartet werden", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static dynamic ConnectToSap(string[] args, bool companionMode)
    {
        if (!companionMode)
        {
            dynamic gui = Activator.CreateInstance(Type.GetTypeFromProgID("SAPbouiCOM.SboGuiApi") ?? throw new InvalidOperationException("SAP Business One UI API ist nicht installiert."))!;
            gui.Connect(args[0]);
            return gui.GetApplication(-1);
        }

        // Fallback for installations where SAP's Extension Manager cannot start
        // the add-on (for example while its server certificate is expired). The
        // companion is launched in the same Windows session and attaches to the
        // registered SAP UI API object instead of inventing a connection token.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try { return Marshal.GetActiveObject("SAPbouiCOM.Application"); }
            catch (COMException) when (attempt < 59) { Thread.Sleep(TimeSpan.FromSeconds(5)); }
        }

        throw new InvalidOperationException("SAP Business One wurde in dieser Windows-Sitzung nicht gefunden.");
    }

    private static SapDocumentContext? TryReadDocument(dynamic application)
    {
        try
        {
            dynamic form = application.Forms.ActiveForm;
            var type = (string)form.TypeEx;
            var direction = type == "141" ? SapDocumentDirection.Incoming : type == "133" ? SapDocumentDirection.Outgoing : (SapDocumentDirection?)null;
            if (direction is null) return null;
            dynamic source = form.DataSources.DBDataSources.Item(direction == SapDocumentDirection.Incoming ? "OPCH" : "OINV");
            var docEntry = ParseSapNumber((string)source.GetValue("DocEntry", 0));
            var docNum = ParseSapNumber((string)source.GetValue("DocNum", 0));
            if (docEntry <= 0 || docNum <= 0) return null;
            return new SapDocumentContext(direction.Value, docEntry, docNum, (string)application.Company.UserName);
        }
        catch { return null; }
    }

    private static int ParseSapNumber(string value) => int.TryParse((value ?? string.Empty).Trim(), out var number) ? number : 0;

    private static CoresuiteExportResult ExportCoresuitePdf(dynamic application, SapDocumentContext context)
    {
        var runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Coresystems", "coresuite", "EXAMPLE");
        if (!Directory.Exists(runtimeDirectory))
        {
            runtimeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "SAP", "SAP Business One", "AddOns", "COR", "coresuite", "x64");
        }

        dynamic company = application.Company;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        AddCompanyValue(values, "CompanyDatabase", company, "CompanyDB");
        AddCompanyValue(values, "DbServer", company, "DbServer");
        AddCompanyValue(values, "DbUser", company, "DbUserName");
        AddCompanyValue(values, "LicenseServer", company, "LicenseServer");
        AddCompanyValue(values, "SAPUser", company, "UserName");
        AddCompanyValue(values, "DbServerType", company, "DbServerType");

        return new CoresuitePdfExporter().Export(new CoresuiteExportRequest(
            runtimeDirectory,
            values,
            context.DocEntry.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "T0000009"));
    }

    private static void AddCompanyValue(IDictionary<string, string> values, string configName, dynamic company, string propertyName)
    {
        try
        {
            object? raw = propertyName switch
            {
                "CompanyDB" => company.CompanyDB,
                "DbServer" => company.DbServer,
                "DbUserName" => company.DbUserName,
                "LicenseServer" => company.LicenseServer,
                "UserName" => company.UserName,
                "DbServerType" => company.DbServerType,
                _ => null
            };
            var value = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(value)) values[configName] = value.Trim();
        }
        catch { /* Optional SAP UI properties vary slightly by SAP B1 patch level. */ }
    }

    private static string RequireSetting(string key)
    {
        var machineConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NovaNein", "client.config");
        if (File.Exists(machineConfig))
        {
            var value = XDocument.Load(machineConfig).Descendants("add").FirstOrDefault(x => (string?)x.Attribute("key") == key)?.Attribute("value")?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value!;
        }
        return ConfigurationManager.AppSettings[key] is { Length: > 0 } fallback ? fallback! : throw new InvalidOperationException($"Die Add-on-Einstellung {key} fehlt.");
    }
}
