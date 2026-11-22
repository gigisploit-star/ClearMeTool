namespace ClearMeTool;

internal sealed class CompareForm : Form
{
    private readonly TextBox _pathA = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox _pathB = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox _report = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 9f),
        WordWrap = false,
        Dock = DockStyle.Fill
    };

    private string? _fileA;
    private string? _fileB;

    public static void Open(IWin32Window owner)
    {
        using var form = new CompareForm();
        form.ShowDialog(owner);
    }

    private CompareForm()
    {
        Text = "Compare two dumps";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(860, 620);
        Font = new Font("Segoe UI", 9f);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(12, 12, 12, 0)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        top.Controls.Add(new Label { Text = "A", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        top.Controls.Add(_pathA, 1, 0);
        var browseA = new Button { Text = "Open…", Dock = DockStyle.Fill };
        browseA.Click += (_, _) => Browse(isA: true);
        top.Controls.Add(browseA, 2, 0);

        top.Controls.Add(new Label { Text = "B", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        top.Controls.Add(_pathB, 1, 1);
        var browseB = new Button { Text = "Open…", Dock = DockStyle.Fill };
        browseB.Click += (_, _) => Browse(isA: false);
        top.Controls.Add(browseB, 2, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 4)
        };
        var compare = new Button { Text = "Compare", AutoSize = true };
        compare.Click += (_, _) => RunCompare();
        actions.Controls.Add(compare);
        actions.Controls.Add(new Label
               {
            Text = ".bin  .rom  .fd  .rgn  .bio",
            AutoSize = true,
            Padding = new Padding(8, 6, 0, 0)
        });

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        host.Controls.Add(_report);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(top);
    }

    private void Browse(bool isA)
    {
        using var dialog = new OpenFileDialog
        {
            Title = isA ? "Open file A" : "Open file B",
            Filter = Firmware.OpenFilter
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (isA)
        {
            _fileA = dialog.FileName;
            _pathA.Text = dialog.FileName;
        }
        else
        {
            _fileB = dialog.FileName;
            _pathB.Text = dialog.FileName;
        }
    }

    private void RunCompare()
    {
        if (string.IsNullOrEmpty(_fileA) || string.IsNullOrEmpty(_fileB))
        {
            MessageBox.Show(this, "Open both files first.", "Compare");
            return;
        }

        try
        {
            _report.Text = Firmware.Compare(_fileA, _fileB);
        }
        catch (Exception ex)
        {
            _report.Text = ex.Message;
        }
    }
}
