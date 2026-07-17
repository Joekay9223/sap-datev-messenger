using System.Drawing;
using System.Windows.Forms;

namespace NovaNein.SapAddon;

/// <summary>Explicit, reason-bound manual decision for a yellow or red document. Closing the dialog never decides.</summary>
public sealed class DocumentReviewDialog : Form
{
    private readonly TextBox _reason = new()
    {
        Location = new Point(16, 205),
        Size = new Size(480, 72),
        Multiline = true,
        MaxLength = 1000,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly Button _approve = new() { Text = "Manuell freigeben", Location = new Point(16, 292), Size = new Size(150, 30), Enabled = false };
    private readonly Button _reject = new() { Text = "Beleg ablehnen", Location = new Point(176, 292), Size = new Size(150, 30), Enabled = false, ForeColor = Color.Firebrick };

    public DocumentReviewDialog(SapDocumentContext context, IReadOnlyList<string> validationReasons, bool isRed = false)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (validationReasons is null || validationReasons.Count == 0) throw new ArgumentException("Mindestens ein Prüfgrund ist erforderlich.", nameof(validationReasons));
        Text = $"NovaNein – {(isRed ? "rote" : "gelbe")} Prüfung für SAP-Beleg {context.DocNum}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(512, 340);

        var heading = new Label
        {
            Text = "NovaNein benötigt eine manuelle Entscheidung.",
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Location = new Point(16, 15)
        };
        var reasons = new TextBox
        {
            Text = string.Join(Environment.NewLine, validationReasons.Select(item => "• " + item.Trim())),
            Location = new Point(16, 44),
            Size = new Size(480, 112),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            TabStop = false
        };
        var explanation = new Label
        {
            Text = $"Ihre Begründung (Pflichtfeld, protokolliert als SAP-Benutzer {context.SapUser}):",
            AutoSize = true,
            Location = new Point(16, 178)
        };
        var cancel = new Button { Text = "Später entscheiden", Location = new Point(346, 292), Size = new Size(150, 30), DialogResult = DialogResult.Cancel };

        _reason.TextChanged += (_, __) => _approve.Enabled = _reject.Enabled = !string.IsNullOrWhiteSpace(_reason.Text);
        _approve.Click += (_, __) => Complete(approve: true);
        _reject.Click += (_, __) => Complete(approve: false);
        CancelButton = cancel;
        Controls.AddRange(new Control[] { heading, reasons, explanation, _reason, _approve, _reject, cancel });
    }

    public bool Approve { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    private void Complete(bool approve)
    {
        var reason = _reason.Text.Trim();
        if (reason.Length == 0)
        {
            MessageBox.Show(this, "Bitte tragen Sie zuerst eine konkrete Begründung ein.", "Begründung erforderlich", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var action = approve ? "manuell freigeben" : "ablehnen";
        var answer = MessageBox.Show(this, $"Beleg wirklich {action}?\n\nBegründung: {reason}", "Entscheidung bestätigen", MessageBoxButtons.YesNo, approve ? MessageBoxIcon.Question : MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        Approve = approve;
        Reason = reason;
        DialogResult = DialogResult.OK;
        Close();
    }
}
