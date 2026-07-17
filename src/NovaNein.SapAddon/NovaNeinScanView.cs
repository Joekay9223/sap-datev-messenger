using System.Drawing;
using System.Windows.Forms;

namespace NovaNein.SapAddon;

/// <summary>
/// Reusable, user-facing rendering of the read-only SAP attachment scan.
/// Raw server JSON never reaches the SAP user interface.
/// </summary>
public sealed class NovaNeinScanView : UserControl
{
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8), Height = 34 };
    private readonly DataGridView _grid = new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        ReadOnly = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    public NovaNeinScanView()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Art", FillWeight = 145 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SAP-Nr.", FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Eingabedatum", FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "DocEntry", FillWeight = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PDF-Anhang", FillWeight = 95 });
        Controls.Add(_grid);
        Controls.Add(_summary);
    }

    public void SetResults(IReadOnlyList<NovaNeinAttachmentGap> gaps)
    {
        if (gaps is null) throw new ArgumentNullException(nameof(gaps));
        _grid.Rows.Clear();
        foreach (var gap in gaps.OrderBy(item => item.EntryDate).ThenBy(item => item.DocNum))
        {
            _grid.Rows.Add(
                gap.FriendlyKind,
                gap.DocNum,
                gap.EntryDate.ToString("dd.MM.yyyy"),
                gap.DocEntry,
                gap.AttachmentEntry is null ? "kein PDF" : $"Eintrag {gap.AttachmentEntry}");
        }
        _summary.ForeColor = gaps.Count == 0 ? Color.ForestGreen : Color.DarkGoldenrod;
        _summary.Text = gaps.Count == 0
            ? "Keine SAP-Buchungen ohne PDF-Anhang im geprüften Zeitraum."
            : $"{gaps.Count} SAP-Buchung(en) ohne nachgewiesenen PDF-Anhang – der Scan ist lesend.";
    }
}

public sealed class NovaNeinScanDialog : Form
{
    public NovaNeinScanDialog(IReadOnlyList<NovaNeinAttachmentGap> gaps)
    {
        if (gaps is null) throw new ArgumentNullException(nameof(gaps));
        Text = "NovaNein PDF-Scan";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 360);
        ClientSize = new Size(860, 480);
        var view = new NovaNeinScanView { Dock = DockStyle.Fill };
        view.SetResults(gaps);
        Controls.Add(view);
    }
}
