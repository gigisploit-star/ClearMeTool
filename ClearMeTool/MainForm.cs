namespace ClearMeTool;

internal sealed class MainForm : Form
{
    private readonly TextBox _sourcePath = new() { ReadOnly = true };
    private readonly TextBox _cleanPath = new() { ReadOnly = true };
    private readonly ListView _regions = new();
    private readonly ListView _partitions = new();
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f),
        WordWrap = false
    };
    private readonly RadioButton _uninit = new()
    {
        Text = "Uninitialize ME in this dump (no second file)",
        AutoSize = true,
        Checked = true
    };
    private readonly RadioButton _replaceAll = new()
    {
        Text = "Replace the whole ME region from a clean file",
        AutoSize = true
    };
    private readonly RadioButton _stateOnly = new()
    {
        Text = "Copy state partitions from a clean file, keep my code",
        AutoSize = true
    };
    private readonly CheckBox _clearPassword = new()
    {
        Text = "Clear setup password (AMI: AMITSESetup, AdminPw, UserPw — serial variables stay)",
        AutoSize = true,
        Margin = new Padding(0, 6, 0, 0)
    };
    private readonly Button _build = new() { Text = "Build image…", AutoSize = true, Enabled = false };
    private readonly Label _sourceSummary = new() { AutoSize = true, MaximumSize = new Size(880, 0) };
    private readonly Label _cleanSummary = new() { AutoSize = true, MaximumSize = new Size(880, 0) };

    private SpiImage? _source;
    private byte[]? _cleanMe;
    private string? _cleanFile;

    // NOTE: Full file continues - this was truncated in error. See artifacts zip.
}
