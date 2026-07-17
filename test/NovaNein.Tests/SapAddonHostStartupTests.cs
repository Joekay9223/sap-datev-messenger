namespace NovaNein.Tests;

public sealed class SapAddonHostStartupTests
{
    [Fact]
    public void WinFormsInitialization_PrecedesAnyNativeWindowCreation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programPath = Path.Combine(repositoryRoot, "src", "NovaNein.SapAddonHost", "Program.cs");
        var source = File.ReadAllText(programPath);

        var enableVisualStyles = source.IndexOf("Application.EnableVisualStyles();", StringComparison.Ordinal);
        var setTextRendering = source.IndexOf("Application.SetCompatibleTextRenderingDefault(false);", StringComparison.Ordinal);
        var firstMessageBox = source.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        var sapComActivation = source.IndexOf("Activator.CreateInstance(", StringComparison.Ordinal);
        var addOnFormCreation = source.IndexOf("new DocumentDropForm(", StringComparison.Ordinal);

        Assert.True(enableVisualStyles >= 0, "Die WinForms-Visual-Styles-Initialisierung fehlt.");
        Assert.True(setTextRendering > enableVisualStyles, "Die Textdarstellung muss nach EnableVisualStyles initialisiert werden.");
        Assert.True(setTextRendering < firstMessageBox, "WinForms muss vor dem ersten MessageBox-Fenster initialisiert werden.");
        Assert.True(setTextRendering < sapComActivation, "WinForms muss vor der SAP-COM-Aktivierung initialisiert werden.");
        Assert.True(setTextRendering < addOnFormCreation, "WinForms muss vor dem NovaNein-Formular initialisiert werden.");
    }

    [Fact]
    public void CompanionMode_AttachesToTheInteractiveSapUiApiWithoutAnAddOnToken()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programPath = Path.Combine(repositoryRoot, "src", "NovaNein.SapAddonHost", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("--companion", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.GetActiveObject(\"SAPbouiCOM.Application\")", source, StringComparison.Ordinal);
        Assert.Contains("Global\\\\NovaNein-SapAddonHost", source, StringComparison.Ordinal);
        Assert.Contains("SAP Business One wurde in dieser Windows-Sitzung nicht gefunden.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentDropForm_ProvidesExplicitPdfSelectionFallback()
    {
        var repositoryRoot = FindRepositoryRoot();
        var formPath = Path.Combine(repositoryRoot, "src", "NovaNein.SapAddon", "OutgoingInvoiceDropForm.cs");
        var source = File.ReadAllText(formPath);

        Assert.Contains("PDF auswählen", source, StringComparison.Ordinal);
        Assert.Contains("OpenFileDialog", source, StringComparison.Ordinal);
        Assert.Contains("SubmitPdfAsync(dialog.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("Bitte zuerst eine SAP-Eingangs- oder Ausgangsrechnung öffnen.", source, StringComparison.Ordinal);
        Assert.Contains("Text = \"Beleg anzeigen\"", source, StringComparison.Ordinal);
        Assert.Contains("Beleg hinterlegt: {(status.PdfArchived ? \"Ja\" : \"Nein\")}", source, StringComparison.Ordinal);
        Assert.Contains("DATEV-ZIP vorbereitet: {prepared} | Übertragung: {transferred}", source, StringComparison.Ordinal);
        Assert.Contains("status.PackagePreparedAt is { } preparedAt", source, StringComparison.Ordinal);
        Assert.Contains("status.JobFinalizedAt is { } finalizedAt", source, StringComparison.Ordinal);
        Assert.Contains("Text = \"Wochen-Reminder\", Location = new Point(16, 210)", source, StringComparison.Ordinal);
        Assert.Contains("Text = \"PDF-Scan\", Location = new Point(167, 210)", source, StringComparison.Ordinal);
        Assert.Contains("_selectPdfButton.Enabled = false", source, StringComparison.Ordinal);
        Assert.Contains("_selectPdfButton.Enabled = _serverAvailable", source, StringComparison.Ordinal);
        Assert.Contains("_openArchiveButton.Enabled = status.PdfArchived", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SapAddonHost_ProvidesArchiveAccessThroughNotificationAreaIcon()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programPath = Path.Combine(repositoryRoot, "src", "NovaNein.SapAddonHost", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("new NotifyIcon", source, StringComparison.Ordinal);
        Assert.Contains("Archivierten Beleg öffnen", source, StringComparison.Ordinal);
        Assert.Contains("ArchivedPdfLauncher.OpenAsync", source, StringComparison.Ordinal);
        Assert.Contains("trayIcon.DoubleClick", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_exposes_the_archived_pdf_route_with_root_bounded_storage_lookup()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programPath = Path.Combine(repositoryRoot, "src", "NovaNein.Server", "Program.cs");
        var source = File.ReadAllText(programPath);

        Assert.Contains("/api/v1/documents/by-sap/{direction}/{docEntry:int}/pdf", source, StringComparison.Ordinal);
        Assert.Contains("item.PdfSha256 + \".pdf\"", source, StringComparison.Ordinal);
        Assert.Contains("StartsWith(rootPrefix", source, StringComparison.Ordinal);
        Assert.Contains("Results.File(path, \"application/pdf\"", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NovaNein.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Das NovaNein-Repository wurde ausgehend vom Testausgabeordner nicht gefunden.");
    }
}
