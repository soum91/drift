using System.Diagnostics;
using System.Net;

namespace Drift.Windows;

internal sealed class MainForm : Form, IDriftUi
{
    private static readonly Color Back = Color.FromArgb(15, 20, 31);
    private static readonly Color Panel = Color.FromArgb(27, 36, 50);
    private static readonly Color Ink = Color.FromArgb(225, 239, 244);
    private static readonly Color Mint = Color.FromArgb(111, 232, 185);
    private readonly DriftService _service;
    private readonly DriftSender _sender = new();
    private readonly ComboBox _interface = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _code = new() { AutoSize = true, ForeColor = Mint, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
    private readonly Button _listen = new() { Text = "Start receiving", Width = 154 };
    private readonly TextBox _peerIp = new() { Width = 175, PlaceholderText = "192.168.1.20" };
    private readonly TextBox _peerCode = new() { Width = 104, MaxLength = 6, PlaceholderText = "6-digit code" };
    private readonly Button _pair = new() { Text = "Pair", Width = 94 };
    private readonly Button _send = new() { Text = "Send file to receiver", Width = 210 };
    private readonly Button _queue = new() { Text = "Share file with iPhone", Width = 210 };
    private readonly CheckBox _shareClipboard = new() { Text = "Let paired iPhone read Windows clipboard", AutoSize = true };
    private readonly CheckBox _receiveClipboard = new() { Text = "Accept clipboard copied from paired iPhone", AutoSize = true };
    private readonly ProgressBar _progress = new() { Width = 520, Height = 18, Minimum = 0, Maximum = 100 };
    private readonly RichTextBox _log = new() { ReadOnly = true, BackColor = Back, ForeColor = Ink,
                                                 BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
                                                 Font = new Font("Consolas", 9.5f), DetectUrls = false };
    private readonly NotifyIcon _tray;
    private bool _allowClipboardRead;
    private bool _allowClipboardWrite;
    private bool _reallyExit;

    public bool ClipboardSharingEnabled => _allowClipboardRead;
    public bool ClipboardReceivingEnabled => _allowClipboardWrite;

    public MainForm()
    {
        _service = new DriftService(this);
        Text = "Drift | Windows Companion";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(690, 700);
        Size = new Size(800, 820);
        BackColor = Back;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                                          RowCount = 2, Padding = new Padding(20, 16, 20, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        Controls.Add(root);

        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                                            WrapContents = false, AutoScroll = true };
        root.Controls.Add(content, 0, 0);
        content.Resize += (_, _) =>
        {
            foreach (Control child in content.Controls) child.Width = Math.Max(580, content.ClientSize.Width - 26);
        };

        var heading = new Panel { Height = 80 };
        heading.Controls.Add(new Label { Text = "◈  DRIFT", Font = new Font("Segoe UI", 27, FontStyle.Bold),
                                          ForeColor = Mint, Location = new Point(10, 3), AutoSize = true });
        heading.Controls.Add(new Label { Text = "Windows ↔ iPhone ↔ Android TV  •  local transfer, no cloud",
                                          ForeColor = Ink, Location = new Point(12, 53), AutoSize = true });
        content.Controls.Add(heading);

        var receive = Group("Receive from iPhone", 135);
        var receiveRow = NewRow(20, 38);
        receiveRow.Controls.Add(Styled(new Label { Text = "Windows LAN IP:", Width = 142, TextAlign = ContentAlignment.MiddleLeft }));
        receiveRow.Controls.Add(_interface);
        var refresh = Button("Refresh IPs", 110);
        refresh.Click += (_, _) => RefreshAddresses();
        receiveRow.Controls.Add(refresh);
        receive.Controls.Add(receiveRow);
        var receiverRow = NewRow(20, 82);
        Style(_listen); _listen.Click += async (_, _) => await ToggleReceiving();
        receiverRow.Controls.Add(_listen);
        receiverRow.Controls.Add(Styled(new Label { Text = "Pairing code:", Width = 112, TextAlign = ContentAlignment.MiddleLeft }));
        receiverRow.Controls.Add(_code);
        receive.Controls.Add(receiverRow);
        content.Controls.Add(receive);

        var share = Group("Share files both ways", 170);
        var peerRow = NewRow(20, 30);
        peerRow.Controls.Add(Styled(new Label { Text = "TV / Drift IP:", Width = 118, TextAlign = ContentAlignment.MiddleLeft }));
        peerRow.Controls.Add(_peerIp);
        peerRow.Controls.Add(_peerCode);
        Style(_pair); _pair.Click += async (_, _) => await PairToReceiver();
        peerRow.Controls.Add(_pair);
        share.Controls.Add(peerRow);

        var fileRow = NewRow(20, 80);
        Style(_send); _send.Click += async (_, _) => await SendFile();
        Style(_queue); _queue.Click += (_, _) => QueueFile();
        fileRow.Controls.Add(_send);
        fileRow.Controls.Add(_queue);
        share.Controls.Add(fileRow);
        var progressRow = NewRow(20, 126);
        progressRow.Controls.Add(_progress);
        share.Controls.Add(progressRow);
        content.Controls.Add(share);

        var clip = Group("Pasteboard sync (opt-in)", 132);
        var clipRow = NewRow(15, 32);
        _shareClipboard.ForeColor = Ink;
        _receiveClipboard.ForeColor = Ink;
        _shareClipboard.CheckedChanged += (_, _) => _allowClipboardRead = _shareClipboard.Checked;
        _receiveClipboard.CheckedChanged += (_, _) => _allowClipboardWrite = _receiveClipboard.Checked;
        clipRow.Controls.Add(_shareClipboard);
        var clipRow2 = NewRow(15, 63);
        clipRow2.Controls.Add(_receiveClipboard);
        var note = Styled(new Label { Text = "Sync works with Drift on iPhone while the app is open. Windows clipboard is never published unless enabled.",
                                      Width = 610, Height = 28, ForeColor = Mint, AutoEllipsis = true });
        clipRow2.Controls.Add(note);
        clip.Controls.Add(clipRow);
        clip.Controls.Add(clipRow2);
        content.Controls.Add(clip);

        var air = Group("Native AirDrop (experimental, separate engine)", 116);
        var airRow = NewRow(16, 30);
        var pulse = Button("PulseIt setup / research", 230);
        pulse.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/gsl0001/windows-airdrop") { UseShellExecute = true });
        airRow.Controls.Add(pulse);
        air.Controls.Add(airRow);
        var caution = Styled(new Label { Text = "Needs a specific TP-Link Archer T3U (2357:012d) USB adapter, admin driver setup, and separate PulseIt build.",
                                          Left = 18, Top = 77, Width = 650, Height = 26, AutoEllipsis = true });
        air.Controls.Add(caution);
        content.Controls.Add(air);

        var logPanel = Group("Activity", 0);
        logPanel.Dock = DockStyle.Fill;
        logPanel.Padding = new Padding(12, 28, 12, 12);
        logPanel.Controls.Add(_log);
        root.Controls.Add(logPanel, 0, 1);

        _tray = new NotifyIcon { Icon = Icon, Text = "Drift Windows Companion", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Drift", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Exit Drift", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            { e.Cancel = true; Hide(); _tray.ShowBalloonTip(1700, "Drift still running", "Use the tray menu to quit.", ToolTipIcon.Info); }
        };
        FormClosed += (_, _) =>
        {
            _tray.Visible = false;
            _tray.Dispose();
            _sender.Dispose();
            _service.StopAsync().GetAwaiter().GetResult();
        };
        RefreshAddresses();
        Report("Choose your private LAN address and Start receiving, then enter the six-digit code on iPhone.");
    }

    private static GroupBox Group(string title, int height) => new()
    {
        Text = title, Height = height, Width = 710, BackColor = Panel, ForeColor = Mint,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 13),
        Padding = new Padding(14)
    };

    private static FlowLayoutPanel NewRow(int left, int top) => new()
    {
        Location = new Point(left, top), Width = 660, Height = 42, FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false, BackColor = Color.Transparent
    };

    private static Control Styled(Control control)
    {
        control.ForeColor = Ink;
        control.Margin = new Padding(3, 6, 7, 3);
        return control;
    }

    private static Button Button(string text, int width)
    {
        var button = new Button { Text = text, Width = width };
        Style(button);
        return button;
    }

    private static void Style(Button button)
    {
        button.Height = 34;
        button.BackColor = Color.FromArgb(49, 74, 72);
        button.ForeColor = Mint;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Mint;
        button.Margin = new Padding(3, 3, 8, 3);
    }

    private void RefreshAddresses()
    {
        _interface.Items.Clear();
        foreach (var addr in DriftService.LocalAddresses()) _interface.Items.Add(addr.ToString());
        if (_interface.Items.Count > 0) _interface.SelectedIndex = 0;
        else Report("No private IPv4 network found. Connect to Wi-Fi or a Windows Wi-Fi Direct group.");
    }

    private async Task ToggleReceiving()
    {
        _listen.Enabled = false;
        try
        {
            if (_service.Running)
            {
                await _service.StopAsync();
                _listen.Text = "Start receiving";
                _code.Text = "";
            }
            else
            {
                if (!IPAddress.TryParse(_interface.SelectedItem?.ToString(), out var addr))
                    throw new InvalidOperationException("Select a LAN IPv4 address first");
                await _service.StartAsync(addr);
                _listen.Text = "Stop receiving";
                _code.Text = _service.PairingCode;
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { _listen.Enabled = true; }
    }

    private async Task PairToReceiver()
    {
        _pair.Enabled = false;
        try { await _sender.PairAsync(_peerIp.Text, _peerCode.Text); Report("Paired with receiver " + _peerIp.Text); }
        catch (Exception ex) { Error(ex); }
        finally { _pair.Enabled = true; }
    }

    private async Task SendFile()
    {
        using var dialog = new OpenFileDialog { Title = "Choose file to send to Drift receiver" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _send.Enabled = false;
        _progress.Value = 0;
        try
        {
            Report("Waiting for receiver approval: " + Path.GetFileName(dialog.FileName));
            var progress = new Progress<double>(value => _progress.Value = Math.Clamp((int)(value * 100), 0, 100));
            await _sender.SendFileAsync(dialog.FileName, progress);
            Report("Sent successfully: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { Error(ex); }
        finally { _send.Enabled = true; }
    }

    private void QueueFile()
    {
        if (!_service.Running) { Report("Start receiving before sharing a file with iPhone"); return; }
        using var dialog = new OpenFileDialog { Title = "Choose file for iPhone to download" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _service.QueueForIPhone(dialog.FileName); }
        catch (Exception ex) { Error(ex); }
    }

    private void Error(Exception ex)
    {
        Report("ERROR: " + ex.Message);
        MessageBox.Show(this, ex.Message, "Drift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public void Report(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => Report(message))); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    public Task<bool> ApproveFileAsync(string name, long bytes) => OnUi(() =>
        MessageBox.Show(this, $"Accept {name} ({bytes / 1024.0 / 1024.0:N2} MiB) from a paired Drift device?",
                        "Incoming Drift file", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes);

    public Task SetClipboardAsync(string text) => OnUi(() =>
    { if (!ClipboardReceivingEnabled) throw new InvalidOperationException("Clipboard receive was disabled");
      if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text);
      return true; });

    public Task<string> GetClipboardAsync() => OnUi(() =>
    { if (!ClipboardSharingEnabled) throw new InvalidOperationException("Clipboard sharing was disabled"); return Clipboard.ContainsText() ? Clipboard.GetText() : ""; });

    private Task<T> OnUi<T>(Func<T> callback)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated) { result.SetException(new InvalidOperationException("Drift window closed")); return result.Task; }
        BeginInvoke((Action)(() =>
        {
            try { result.TrySetResult(callback()); }
            catch (Exception ex) { result.TrySetException(ex); }
        }));
        return result.Task;
    }
}
