using System.Drawing;
using System.Windows.Forms;

namespace NovaNein.SapAddon;

/// <summary>Small floating SAP add-on window for dropping one PDF onto the currently open SAP invoice.</summary>
public sealed class DocumentDropForm : Form
{
    private readonly Label _health = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(448, 20) };
    private readonly Label _document = new Label { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(448, 36) };
    private readonly Label _archiveInfo = new Label { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _datevInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(448, 34) };
    private readonly Button _selectPdfButton = new Button { Text = "PDF auswählen", Size = new Size(143, 26) };
    private readonly Button _openArchiveButton = new Button { Text = "Beleg anzeigen", Size = new Size(143, 26) };
    private readonly Button _reviewButton = new Button { Text = "Prüfung öffnen", Location = new Point(300, 112), Size = new Size(143, 26), Visible = false };
    private readonly Button _cockpitButton = new Button { Text = "NovaNein Cockpit", Size = new Size(143, 26) };
    private readonly Func<SapDocumentContext, string, CancellationToken, Task<NovaNeinDocumentProgress>> _submit;
    private readonly Func<CancellationToken, Task> _enableReminder;
    private readonly Func<CancellationToken, Task<IReadOnlyList<NovaNeinAttachmentGap>>> _scanMissingPdfs;
    private readonly Func<SapDocumentContext?> _readActiveDocument;
    private readonly Func<SapDocumentContext, CancellationToken, Task<NovaNeinDocumentProgress?>> _getExistingDocument;
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<string>>> _getValidationReasons;
    private readonly Func<Guid, CancellationToken, Task<NovaNeinDatevStatus>> _getDatevStatus;
    private readonly Func<Guid, bool, string, string, CancellationToken, Task<NovaNeinDocumentProgress>> _reviewDocument;
    private readonly Func<SapDocumentContext, CancellationToken, Task<bool>> _openArchive;
    private readonly Func<CancellationToken, Task<IReadOnlyList<NovaNeinUserNotification>>> _getNotifications;
    private readonly Func<long, CancellationToken, Task> _markNotificationRead;
    private readonly Func<string, string, CancellationToken, Task<NovaNeinHealthResponse>> _checkHealth;
    private readonly Action _openCockpit;
    private readonly string _clientVersion;
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = NovaNeinHealthRetryPolicy.HealthyIntervalMilliseconds };
    private readonly HashSet<long> _seenNotificationIds = new();
    private SapDocumentContext? _context;
    private PendingReview? _pendingReview;
    private bool _serverAvailable;
    private bool _healthRefreshRunning;
    private bool _closing;
    private bool _statusBlockedByHealth;
    private bool _notificationRefreshRunning;
    private DateTime _lastNotificationPollUtc = DateTime.MinValue;
    private int _consecutiveHealthFailures;

    public DocumentDropForm(
        Func<SapDocumentContext, string, CancellationToken, Task<NovaNeinDocumentProgress>> submit,
        Func<CancellationToken, Task> enableReminder,
        Func<CancellationToken, Task<IReadOnlyList<NovaNeinAttachmentGap>>> scanMissingPdfs,
        Func<SapDocumentContext?> readActiveDocument,
        Func<SapDocumentContext, CancellationToken, Task<NovaNeinDocumentProgress?>> getExistingDocument,
        Func<Guid, CancellationToken, Task<IReadOnlyList<string>>> getValidationReasons,
        Func<Guid, CancellationToken, Task<NovaNeinDatevStatus>> getDatevStatus,
        Func<Guid, bool, string, string, CancellationToken, Task<NovaNeinDocumentProgress>> reviewDocument,
        Func<SapDocumentContext, CancellationToken, Task<bool>> openArchive,
        Func<CancellationToken, Task<IReadOnlyList<NovaNeinUserNotification>>> getNotifications,
        Func<long, CancellationToken, Task> markNotificationRead,
        Func<string, string, CancellationToken, Task<NovaNeinHealthResponse>> checkHealth,
        string clientVersion,
        Action openCockpit)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        _enableReminder = enableReminder ?? throw new ArgumentNullException(nameof(enableReminder));
        _scanMissingPdfs = scanMissingPdfs ?? throw new ArgumentNullException(nameof(scanMissingPdfs));
        _readActiveDocument = readActiveDocument ?? throw new ArgumentNullException(nameof(readActiveDocument));
        _getExistingDocument = getExistingDocument ?? throw new ArgumentNullException(nameof(getExistingDocument));
        _getValidationReasons = getValidationReasons ?? throw new ArgumentNullException(nameof(getValidationReasons));
        _getDatevStatus = getDatevStatus ?? throw new ArgumentNullException(nameof(getDatevStatus));
        _reviewDocument = reviewDocument ?? throw new ArgumentNullException(nameof(reviewDocument));
        _openArchive = openArchive ?? throw new ArgumentNullException(nameof(openArchive));
        _getNotifications = getNotifications ?? throw new ArgumentNullException(nameof(getNotifications));
        _markNotificationRead = markNotificationRead ?? throw new ArgumentNullException(nameof(markNotificationRead));
        _checkHealth = checkHealth ?? throw new ArgumentNullException(nameof(checkHealth));
        _openCockpit = openCockpit ?? throw new ArgumentNullException(nameof(openCockpit));
        _clientVersion = string.IsNullOrWhiteSpace(clientVersion)
            ? throw new ArgumentException("Die Clientversion ist erforderlich.", nameof(clientVersion))
            : clientVersion.Trim();
        Text = "NovaNein – Belegarchiv";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AllowDrop = false;
        ClientSize = new Size(476, 250);
        var hint = new Label { Text = "PDF zum geöffneten SAP-Beleg hier ablegen", AutoSize = true, Location = new Point(16, 72) };
        _health.Location = new Point(16, 12);
        _health.Text = "○ NovaNein-Selbstprüfung wird gestartet …";
        _document.Location = new Point(16, 40);
        _status.Location = new Point(16, 100);
        _archiveInfo.Location = new Point(16, 130);
        _datevInfo.Location = new Point(16, 148);
        var reminder = new Button { Text = "Wochen-Reminder", Location = new Point(16, 210), Size = new Size(143, 26) };
        var scan = new Button { Text = "PDF-Scan", Location = new Point(167, 210), Size = new Size(125, 26) };
        _selectPdfButton.Location = new Point(16, 178);
        _openArchiveButton.Location = new Point(167, 178);
        _cockpitButton.Location = new Point(318, 178);
        _reviewButton.Location = new Point(318, 210);
        _selectPdfButton.Click += async (_, __) => await SelectPdfAsync();
        _openArchiveButton.Click += async (_, __) => await OpenArchiveAsync();
        reminder.Click += async (_, __) => await EnableReminderAsync();
        scan.Click += async (_, __) => await ScanAsync();
        _reviewButton.Click += async (_, __) => await ReviewPendingAsync();
        _cockpitButton.Click += (_, __) => _openCockpit();
        Controls.AddRange(new Control[] { _health, _document, hint, _status, _archiveInfo, _datevInfo, _selectPdfButton, _openArchiveButton, reminder, scan, _reviewButton, _cockpitButton });
        _selectPdfButton.Enabled = false;
        _openArchiveButton.Enabled = false;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _healthTimer.Tick += async (_, __) => await RefreshHealthAsync();
        Shown += async (_, __) =>
        {
            await RefreshHealthAsync();
            _healthTimer.Start();
            if (_serverAvailable) await LoadNotificationsAtStartupAsync();
        };
        FormClosed += (_, __) => { _closing = true; _healthTimer.Stop(); _healthTimer.Dispose(); };
        ClearDocument();
    }

    public void SetDocument(SapDocumentContext context)
    {
        ClearPendingReview();
        _context = context ?? throw new ArgumentNullException(nameof(context));
        AllowDrop = false;
        _openArchiveButton.Enabled = false;
        _selectPdfButton.Enabled = _serverAvailable;
        var label = context.Direction == SapDocumentDirection.Incoming ? "SAP-Eingangsrechnung" : "SAP-Ausgangsrechnung";
        _document.Text = $"{label} {context.DocNum}";
        _statusBlockedByHealth = !_serverAvailable;
        _status.Text = _serverAvailable ? "Vorhandener NovaNein-Status wird geprüft …" : "Warte auf eine erfolgreiche NovaNein-Selbstprüfung.";
        _status.ForeColor = Color.DimGray;
        _ = RestoreExistingDocumentAsync(context);
    }

    public void ClearDocument()
    {
        ClearPendingReview();
        _context = null;
        AllowDrop = false;
        _openArchiveButton.Enabled = false;
        _document.Text = "Kein unterstützter SAP-Beleg geöffnet";
        _status.Text = "PDF-Ablage ist deaktiviert.";
        _status.ForeColor = Color.DimGray;
        _archiveInfo.Text = "Beleg hinterlegt: –";
        _datevInfo.Text = "DATEV-ZIP vorbereitet: – | Übertragung: –";
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!_serverAvailable)
        {
            e.Effect = DragDropEffects.None;
            _status.ForeColor = Color.Firebrick;
            _status.Text = "PDF-Ablage ist bis zur erfolgreichen Selbstprüfung gesperrt.";
            return;
        }
        var active = ReadActiveDocument();
        if (active is null)
        {
            ClearDocument();
            e.Effect = DragDropEffects.None;
            return;
        }
        if (_context is null || !_context.IsSameDocument(active)) SetDocument(active);
        var paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
        e.Effect = _context is not null && paths is { Length: 1 } && paths[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDragDrop(object sender, DragEventArgs e)
    {
        var paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
        if (paths is not { Length: 1 } || !paths[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Bitte genau eine PDF ablegen.");
            return;
        }
        await SubmitPdfAsync(paths[0]);
    }

    private async Task SelectPdfAsync()
    {
        if (_context is null)
        {
            ShowError("Bitte zuerst eine SAP-Eingangs- oder Ausgangsrechnung öffnen.");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "PDF zum geöffneten SAP-Beleg auswählen",
            Filter = "PDF-Dateien (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await SubmitPdfAsync(dialog.FileName);
    }

    private async Task SubmitPdfAsync(string path)
    {
        var selected = _context;
        var active = ReadActiveDocument();
        switch (DocumentContextGuard.Compare(selected, active))
        {
            case DocumentContextMatch.Missing:
                ClearDocument();
                ShowError("Kein unterstützter SAP-Beleg ausgewählt.");
                return;
            case DocumentContextMatch.Changed:
                SetDocument(active!);
                ShowError("Der SAP-Beleg wurde gewechselt. Bitte die PDF erneut auswählen.");
                return;
        }
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Bitte eine PDF-Datei auswählen.");
            return;
        }
        try
        {
            Enabled = false;
            _status.ForeColor = Color.DimGray;
            _status.Text = "PDF wird sicher übergeben …";
            var progress = await _submit(selected!, path, CancellationToken.None);
            if (_context?.IsSameDocument(selected) == true)
            {
                SetProgress(progress);
                if (IsManualReviewStatus(progress.Status))
                {
                    try
                    {
                        var reasons = await _getValidationReasons(progress.Id, CancellationToken.None);
                        if (_context?.IsSameDocument(selected) == true) SetPendingReview(progress.Id, selected!, reasons, progress.Status);
                    }
                    catch (Exception ex)
                    {
                        if (_context?.IsSameDocument(selected) == true)
                        {
                            SetPendingReview(progress.Id, selected!, Array.Empty<string>(), progress.Status);
                            ShowError("Prüfbeleg: Prüfgründe konnten nicht geladen werden. Bitte erneut versuchen. " + ex.Message);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (_context?.IsSameDocument(selected) == true) ShowError(ex.Message);
        }
        finally { Enabled = true; }
    }

    private void ShowError(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = message.Length > 90 ? message.Substring(0, 90) + "…" : message;
    }

    private void SetProgress(NovaNeinDocumentProgress progress)
    {
        AllowDrop = false;
        _statusBlockedByHealth = false;
        if (!IsManualReviewStatus(progress.Status)) ClearPendingReview();
        var presentation = DocumentProgressPresentation.For(progress.Status);
        _status.ForeColor = presentation.Tone switch
        {
            DocumentProgressTone.Green => Color.ForestGreen,
            DocumentProgressTone.Yellow => Color.Goldenrod,
            DocumentProgressTone.Red => Color.Firebrick,
            _ => Color.DimGray
        };
        _status.Text = presentation.Message;
        if (_context is not null)
            _ = RefreshDatevStatusAsync(progress.Id, _context, progress.Status is NovaNeinDocumentStatus.Approved or NovaNeinDocumentStatus.AttachedToSap or NovaNeinDocumentStatus.Packaged or NovaNeinDocumentStatus.Transferred);
    }

    private async Task RefreshDatevStatusAsync(Guid documentId, SapDocumentContext context, bool waitForPackage)
    {
        try
        {
            for (var attempt = 0; attempt < (waitForPackage ? 30 : 1); attempt++)
            {
                var status = await _getDatevStatus(documentId, CancellationToken.None);
                if (_context?.IsSameDocument(context) != true) return;
                _archiveInfo.Text = $"Beleg hinterlegt: {(status.PdfArchived ? "Ja" : "Nein")}";
                _openArchiveButton.Enabled = status.PdfArchived;
                var prepared = status.PackagePreparedAt is { } preparedAt ? $"Ja, {preparedAt.ToLocalTime():dd.MM.yyyy HH:mm}" : "Nein";
                var transferred = status.Transferred && status.JobFinalizedAt is { } finalizedAt ? $"Ja, {finalizedAt.ToLocalTime():dd.MM.yyyy HH:mm}" : "Nein";
                _datevInfo.Text = $"DATEV-ZIP vorbereitet: {prepared} | Übertragung: {transferred}";
                if (!waitForPackage || status.PackagePreparedAt is not null || attempt == 29) return;
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
        catch
        {
            if (_context?.IsSameDocument(context) == true)
            {
                _openArchiveButton.Enabled = false;
                _archiveInfo.Text = "Beleg hinterlegt: Status nicht verfügbar";
                _datevInfo.Text = "DATEV-Status: noch nicht verfügbar";
            }
        }
    }

    private async Task OpenArchiveAsync()
    {
        var selected = _context;
        if (selected is null)
        {
            ShowError("Bitte zuerst einen SAP-Beleg öffnen.");
            return;
        }
        var active = ReadActiveDocument();
        if (DocumentContextGuard.Compare(selected, active) != DocumentContextMatch.Current)
        {
            if (active is null) ClearDocument();
            else SetDocument(active);
            ShowError("Der SAP-Beleg wurde gewechselt. Bitte erneut versuchen.");
            return;
        }
        try
        {
            Enabled = false;
            var opened = await _openArchive(selected, CancellationToken.None);
            if (!opened) ShowError("Für diesen SAP-Beleg ist noch kein archivierter PDF-Beleg hinterlegt.");
        }
        catch (Exception ex) { ShowError("Archivierte PDF konnte nicht geöffnet werden: " + ex.Message); }
        finally { Enabled = true; }
    }

    private async Task RestoreExistingDocumentAsync(SapDocumentContext context)
    {
        try
        {
            var progress = await _getExistingDocument(context, CancellationToken.None);
        if (_context?.IsSameDocument(context) != true) return;
            if (progress is null)
            {
                AllowDrop = _serverAvailable;
                _statusBlockedByHealth = !_serverAvailable;
                _status.ForeColor = _serverAvailable ? Color.DimGray : Color.Firebrick;
                _status.Text = _serverAvailable ? "PDF für Prüfung bereit." : "PDF-Ablage ist bis zur Wiederverbindung gesperrt.";
                return;
            }
            SetProgress(progress);
            if (!IsManualReviewStatus(progress.Status)) return;
            try
            {
                var reasons = await _getValidationReasons(progress.Id, CancellationToken.None);
                if (_context?.IsSameDocument(context) == true) SetPendingReview(progress.Id, context, reasons, progress.Status);
            }
            catch (Exception ex)
            {
                if (_context?.IsSameDocument(context) == true)
                {
                    SetPendingReview(progress.Id, context, Array.Empty<string>(), progress.Status);
                    ShowError("Prüfbeleg: Prüfgründe konnten nicht geladen werden. Bitte erneut versuchen. " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            if (_context?.IsSameDocument(context) == true)
            {
                AllowDrop = false;
                ShowError("Vorhandener NovaNein-Status konnte nicht geladen werden: " + ex.Message);
            }
        }
    }

    private void SetPendingReview(Guid documentId, SapDocumentContext context, IReadOnlyList<string> reasons, NovaNeinDocumentStatus status)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Eine Dokument-ID ist erforderlich.", nameof(documentId));
        if (reasons is null) throw new ArgumentNullException(nameof(reasons));
        _pendingReview = new PendingReview(documentId, context, reasons.ToArray(), status);
        AllowDrop = false;
        _reviewButton.Visible = true;
        _reviewButton.Enabled = true;
        if (reasons.Count == 0)
        {
            _reviewButton.Text = "Prüfgründe erneut laden";
            return;
        }
        var red = status == NovaNeinDocumentStatus.Rejected;
        _reviewButton.Text = red ? "Rote Prüfung öffnen" : "Gelbe Prüfung öffnen";
        _status.ForeColor = red ? Color.Firebrick : Color.Goldenrod;
        var summary = string.Join(" ", reasons.Select(item => item.Trim()));
        _status.Text = (red ? "Prüfung rot – " : "Prüfung gelb – ") + (summary.Length > 105 ? summary.Substring(0, 105) + "…" : summary);
    }

    private void ClearPendingReview()
    {
        _pendingReview = null;
        _reviewButton.Text = "Prüfung öffnen";
        _reviewButton.Visible = false;
        _reviewButton.Enabled = false;
    }

    private async Task ReviewPendingAsync()
    {
        var pending = _pendingReview;
        if (pending is null) return;
        var active = ReadActiveDocument();
        switch (DocumentContextGuard.Compare(pending.Context, active))
        {
            case DocumentContextMatch.Missing:
                ClearDocument();
                ShowError("Der SAP-Beleg ist nicht mehr geöffnet. Es wurde nichts entschieden.");
                return;
            case DocumentContextMatch.Changed:
                SetDocument(active!);
                ShowError("Der SAP-Beleg wurde gewechselt. Es wurde nichts entschieden.");
                return;
        }

        if (pending.Reasons.Count == 0)
        {
            try
            {
                Enabled = false;
                _status.ForeColor = Color.DimGray;
                _status.Text = "Prüfgründe werden erneut geladen …";
                var reasons = await _getValidationReasons(pending.DocumentId, CancellationToken.None);
                if (_context?.IsSameDocument(pending.Context) != true) return;
                SetPendingReview(pending.DocumentId, pending.Context, reasons, pending.Status);
                pending = _pendingReview!;
            }
            catch (Exception ex)
            {
                if (_context?.IsSameDocument(pending.Context) == true) ShowError("Prüfgründe konnten nicht geladen werden: " + ex.Message);
                return;
            }
            finally { Enabled = true; }
        }

        using var dialog = new DocumentReviewDialog(pending.Context, pending.Reasons, pending.Status == NovaNeinDocumentStatus.Rejected);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        active = ReadActiveDocument();
        if (_pendingReview?.DocumentId != pending.DocumentId || DocumentContextGuard.Compare(pending.Context, active) != DocumentContextMatch.Current)
        {
            if (active is null) ClearDocument();
            else SetDocument(active);
            ShowError("Der SAP-Beleg wurde während der Prüfung gewechselt. Es wurde nichts entschieden.");
            return;
        }

        try
        {
            Enabled = false;
            _status.ForeColor = Color.DimGray;
            _status.Text = dialog.Approve ? "Manuelle Freigabe wird protokolliert …" : "Ablehnung wird protokolliert …";
            var progress = await _reviewDocument(pending.DocumentId, dialog.Approve, dialog.Reason, pending.Context.SapUser, CancellationToken.None);
            if (_context?.IsSameDocument(pending.Context) == true && _pendingReview?.DocumentId == pending.DocumentId)
            {
                ClearPendingReview();
                SetProgress(progress);
            }
        }
        catch (Exception ex)
        {
            if (_context?.IsSameDocument(pending.Context) == true) ShowError(ex.Message);
        }
        finally { Enabled = true; }
    }

    private SapDocumentContext? ReadActiveDocument()
    {
        try { return _readActiveDocument(); }
        catch { return null; }
    }

    private async Task LoadNotificationsAtStartupAsync()
    {
        try { await RefreshNotificationsAsync(showWhenEmpty: false); }
        catch (Exception ex) { ShowError("NovaNein-Hinweise konnten nicht geladen werden: " + ex.Message); }
    }

    private async Task MaybeRefreshNotificationsAsync()
    {
        if (_notificationRefreshRunning || _closing || IsDisposed) return;
        if ((DateTime.UtcNow - _lastNotificationPollUtc) < TimeSpan.FromSeconds(60)) return;
        _notificationRefreshRunning = true;
        _lastNotificationPollUtc = DateTime.UtcNow;
        try { await RefreshNotificationsAsync(showWhenEmpty: false); }
        catch { /* Ein späterer Poll versucht es bei einer kurzen Netzstörung erneut. */ }
        finally { _notificationRefreshRunning = false; }
    }

    private async Task RefreshHealthAsync()
    {
        if (_healthRefreshRunning || _closing || IsDisposed) return;
        _healthRefreshRunning = true;
        try
        {
            _health.ForeColor = Color.DimGray;
            _health.Text = "○ NovaNein-Verbindung wird geprüft …";
            var health = await _checkHealth(_clientVersion, "SAP UI verbunden", CancellationToken.None);
            if (_closing || IsDisposed) return;
            if (!health.Compatible)
            {
                _serverAvailable = false;
                _consecutiveHealthFailures++;
                AllowDrop = false;
                _selectPdfButton.Enabled = false;
                _statusBlockedByHealth = true;
                _health.ForeColor = Color.Firebrick;
                _health.Text = $"✕ Update erforderlich: Client {_clientVersion}, Server {health.ServerVersion}";
                _status.ForeColor = Color.Firebrick;
                _status.Text = "PDF-Ablage ist wegen einer nicht kompatiblen Version gesperrt.";
                return;
            }

            _serverAvailable = true;
            _selectPdfButton.Enabled = true;
            _consecutiveHealthFailures = 0;
            _health.ForeColor = Color.ForestGreen;
            _health.Text = $"✓ Verbunden – Selbstprüfung {health.CheckedAt.ToLocalTime():HH:mm:ss}";
            await MaybeRefreshNotificationsAsync();
            if (_context is not null && _statusBlockedByHealth)
            {
                _status.ForeColor = Color.DimGray;
                _status.Text = "Verbindung wiederhergestellt; Belegstatus wird geladen …";
                _statusBlockedByHealth = false;
                _ = RestoreExistingDocumentAsync(_context);
            }
        }
        catch (Exception ex)
        {
            if (_closing || IsDisposed) return;
            _serverAvailable = false;
            _consecutiveHealthFailures++;
            AllowDrop = false;
            _selectPdfButton.Enabled = false;
            _statusBlockedByHealth = true;
            _health.ForeColor = Color.Firebrick;
            var detail = (ex.Message ?? string.Empty).Replace(Environment.NewLine, " ").Trim();
            _health.Text = "✕ Verbindung unterbrochen" + (detail.Length == 0
                ? string.Empty
                : ": " + (detail.Length > 65 ? detail.Substring(0, 65) + "…" : detail));
            if (_context is not null)
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = "PDF-Ablage ist bis zur Wiederverbindung gesperrt.";
            }
        }
        finally
        {
            if (!_closing && !IsDisposed)
                _healthTimer.Interval = NovaNeinHealthRetryPolicy.NextDelayMilliseconds(_consecutiveHealthFailures);
            _healthRefreshRunning = false;
        }
    }

    private async Task RefreshNotificationsAsync(bool showWhenEmpty)
    {
        var notifications = await _getNotifications(CancellationToken.None);
        foreach (var item in notifications.Where(item => item.IsRead)) _seenNotificationIds.Add(item.Id);
        var fresh = notifications.Where(item => !item.IsRead && !_seenNotificationIds.Contains(item.Id)).ToArray();
        if (fresh.Length > 0)
        {
            MessageBox.Show(this, NovaNeinNotificationPresentation.Format(fresh), "NovaNein-Hinweise (keine E-Mail)", MessageBoxButtons.OK, MessageBoxIcon.Information);
            foreach (var item in fresh)
            {
                try
                {
                    await _markNotificationRead(item.Id, CancellationToken.None);
                    _seenNotificationIds.Add(item.Id);
                }
                catch
                {
                    // Beim nächsten Poll erneut anbieten, wenn der Server die Quittierung
                    // wegen einer unterbrochenen Verbindung nicht annehmen konnte.
                }
            }
        }
        else if (showWhenEmpty)
        {
            MessageBox.Show(this, "Es liegt noch keine neue Montagsnotiz vor. Der Reminder wird im NovaNein-SAP-Fenster angezeigt; es wurde keine E-Mail versandt.", "NovaNein Wochen-Reminder", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task EnableReminderAsync()
    {
        try
        {
            Enabled = false;
            await _enableReminder(CancellationToken.None);
            _status.ForeColor = Color.ForestGreen;
            _status.Text = "Wochen-Reminder ist aktiviert; Montagsnotizen erscheinen hier im SAP-Fenster.";
            Enabled = true;
            try { await RefreshNotificationsAsync(showWhenEmpty: true); }
            catch (Exception ex) { ShowError("Reminder ist aktiviert, Hinweise konnten aber nicht geladen werden: " + ex.Message); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { Enabled = true; }
    }

    private async Task ScanAsync()
    {
        try
        {
            Enabled = false;
            _status.Text = "PDF-Anhänge werden geprüft …";
            var result = await _scanMissingPdfs(CancellationToken.None);
            using var dialog = new NovaNeinScanDialog(result);
            dialog.ShowDialog(this);
            _status.ForeColor = Color.DimGray;
            _status.Text = "PDF-Scan abgeschlossen.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { Enabled = true; }
    }

    private sealed class PendingReview
    {
        public PendingReview(Guid documentId, SapDocumentContext context, IReadOnlyList<string> reasons, NovaNeinDocumentStatus status)
        {
            DocumentId = documentId;
            Context = context;
            Reasons = reasons;
            Status = status;
        }

        public Guid DocumentId { get; }
        public SapDocumentContext Context { get; }
        public IReadOnlyList<string> Reasons { get; }
        public NovaNeinDocumentStatus Status { get; }
    }

    private static bool IsManualReviewStatus(NovaNeinDocumentStatus status) =>
        status is NovaNeinDocumentStatus.NeedsReview or NovaNeinDocumentStatus.Rejected;
}
