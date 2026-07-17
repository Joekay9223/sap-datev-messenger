using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NovaNein.SapAddon;

/// <summary>In-SAP cockpit for settings, read-only scans and archive statistics.</summary>
public sealed class NovaNeinCockpitForm : Form
{
    private readonly Func<CancellationToken, Task<NovaNeinStatisticsResponse>> _getStatistics;
    private readonly Func<CancellationToken, Task<IReadOnlyList<NovaNeinAttachmentGap>>> _runScan;
    private readonly Func<CancellationToken, Task<bool>> _getReminder;
    private readonly Func<bool, CancellationToken, Task> _setReminder;
    private readonly Func<CancellationToken, Task<NovaNeinHealthResponse>> _checkHealth;
    private readonly Label _health = new() { AutoSize = true, MaximumSize = new Size(560, 36) };
    private readonly Label _statistics = new() { AutoSize = true, MaximumSize = new Size(560, 180) };
    private readonly NovaNeinScanView _scanView = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _reminder = new() { Text = "Montags-Reminder aktiv (Standard: aktiviert)", AutoSize = true, Checked = true };
    private readonly Button _refresh = new() { Text = "Aktualisieren", AutoSize = true };
    private readonly Button _scan = new() { Text = "PDF-Scan starten", AutoSize = true };
    private readonly Button _saveSettings = new() { Text = "Einstellungen speichern", AutoSize = true };
    private bool _busy;

    public NovaNeinCockpitForm(
        Func<CancellationToken, Task<NovaNeinStatisticsResponse>> getStatistics,
        Func<CancellationToken, Task<IReadOnlyList<NovaNeinAttachmentGap>>> runScan,
        Func<CancellationToken, Task<bool>> getReminder,
        Func<bool, CancellationToken, Task> setReminder,
        Func<CancellationToken, Task<NovaNeinHealthResponse>> checkHealth)
    {
        _getStatistics = getStatistics ?? throw new ArgumentNullException(nameof(getStatistics));
        _runScan = runScan ?? throw new ArgumentNullException(nameof(runScan));
        _getReminder = getReminder ?? throw new ArgumentNullException(nameof(getReminder));
        _setReminder = setReminder ?? throw new ArgumentNullException(nameof(setReminder));
        _checkHealth = checkHealth ?? throw new ArgumentNullException(nameof(checkHealth));
        Text = "NovaNein Cockpit";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 420);
        Size = new Size(680, 480);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var overview = new TabPage("Übersicht");
        var scanPage = new TabPage("Scans");
        var settingsPage = new TabPage("Einstellungen");
        tabs.TabPages.AddRange(new[] { overview, scanPage, settingsPage });
        Controls.Add(tabs);

        _health.Location = new Point(20, 20);
        _statistics.Location = new Point(20, 74);
        _refresh.Location = new Point(20, 300);
        _refresh.Click += async (_, __) => await RefreshAsync();
        overview.Controls.AddRange(new Control[] { _health, _statistics, _refresh });

        _scan.Dock = DockStyle.Bottom;
        _scan.Height = 34;
        _scan.Click += async (_, __) => await RunScanAsync();
        scanPage.Controls.Add(_scanView);
        scanPage.Controls.Add(_scan);

        _reminder.Location = new Point(20, 24);
        _saveSettings.Location = new Point(20, 62);
        _saveSettings.Click += async (_, __) => await SaveSettingsAsync();
        settingsPage.Controls.AddRange(new Control[] { _reminder, _saveSettings });

        Shown += async (_, __) =>
        {
            await RefreshAsync();
            await LoadSettingsAsync();
        };
    }

    private async Task RefreshAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        _refresh.Enabled = false;
        try
        {
            var health = await _checkHealth(CancellationToken.None);
            var statistics = await _getStatistics(CancellationToken.None);
            _health.ForeColor = health.Compatible ? Color.ForestGreen : Color.Firebrick;
            _health.Text = health.Compatible
                ? $"✓ Verbunden – Selbstprüfung {health.CheckedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}"
                : $"✕ Versionsprüfung fehlgeschlagen: Server {health.ServerVersion}";
            _statistics.Text =
                $"Belege gesamt: {statistics.Total}\r\n" +
                $"Neu / Prüfung offen / freigegeben: {statistics.Received} / {statistics.NeedsReview} / {statistics.Approved}\r\n" +
                $"Abgelehnt / fehlgeschlagen / an SAP angehängt: {statistics.Rejected} / {statistics.Failed} / {statistics.AttachedToSap}\r\n" +
                $"Eingänge der letzten 7 Tage: {statistics.CreatedLast7Days}\r\n" +
                $"Eingänge der letzten 30 Tage: {statistics.CreatedLast30Days}";
        }
        catch (Exception ex)
        {
            _health.ForeColor = Color.Firebrick;
            _health.Text = "✕ Cockpit konnte nicht aktualisiert werden: " + Shorten(ex.Message);
        }
        finally
        {
            _refresh.Enabled = true;
            _busy = false;
        }
    }

    private async Task RunScanAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        _scan.Enabled = false;
        try { _scanView.SetResults(await _runScan(CancellationToken.None)); }
        catch (Exception ex)
        {
            _scanView.SetResults(Array.Empty<NovaNeinAttachmentGap>());
            MessageBox.Show(this, "Scan fehlgeschlagen: " + ex.Message, "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _scan.Enabled = true; _busy = false; }
    }

    private async Task SaveSettingsAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        _saveSettings.Enabled = false;
        try
        {
            await _setReminder(_reminder.Checked, CancellationToken.None);
            MessageBox.Show(this, "Die Reminder-Einstellung wurde gespeichert.", "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Die Einstellung konnte nicht gespeichert werden: " + ex.Message, "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _saveSettings.Enabled = true; _busy = false; }
    }

    private async Task LoadSettingsAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        try { _reminder.Checked = await _getReminder(CancellationToken.None); }
        catch (Exception ex) { MessageBox.Show(this, "Die Reminder-Einstellung konnte nicht geladen werden: " + ex.Message, "NovaNein", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _busy = false; }
    }

    private static string Shorten(string? value)
    {
        var text = (value ?? string.Empty).Replace(Environment.NewLine, " ").Trim();
        return text.Length > 100 ? text.Substring(0, 100) + "…" : text;
    }
}
