using System.Drawing.Drawing2D;

namespace PeripheralPower;

internal sealed class BatteryPanel : Form
{
    private static readonly Color White = Color.White;
    private readonly Font titleFont = new("Microsoft YaHei UI", 16.5f, FontStyle.Bold);
    private readonly Font bodyFont = new("Microsoft YaHei UI", 9f);
    private readonly Font smallFont = new("Microsoft YaHei UI", 8.2f);
    private readonly Font strongFont = new("Microsoft YaHei UI", 9.2f, FontStyle.Bold);

    private readonly Label title = new() { Text = "外设电量" };
    private readonly Label status = new() { Text = "正在检测设备…" };
    private readonly Button refresh = new() { Text = "立即刷新" };
    private readonly Panel list = new() { AutoScroll = true };
    private readonly Label settingsLabel = new() { Text = "设置" };
    private readonly Label intervalLabel = new() { Text = "刷新间隔" };
    private readonly ComboBox interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "刷新间隔" };
    private readonly CheckBox startup = new() { Text = "登录时启动", AutoSize = true };
    private readonly Button exit = new() { Text = "退出应用" };
    private readonly Label empty = new() { Text = "暂无可显示电量的外设\r\n唤醒无线设备后，可点「立即刷新」。" };
    private IReadOnlyList<BatteryReading> readings = [];
    private DateTime? updated;
    private bool busy;
    private bool updating;
    private int settingsTop;
    private Point? anchor;

    public event Action? RefreshRequested;
    public event Action<int>? IntervalChanged;
    public event Action<bool>? StartupChanged;
    public event Action? ExitRequested;
    public int IntervalMinutes => interval.SelectedIndex + 5;

    public BatteryPanel()
    {
        Name = "PeripheralPowerPanel";
        Text = "外设电量";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = TrayArt.Surface;
        ForeColor = TrayArt.Ink;
        Font = bodyFont;
        AccessibleName = "外设电量与设置";

        title.Font = titleFont;
        status.Font = smallFont;
        status.ForeColor = TrayArt.Muted;
        settingsLabel.Font = strongFont;
        settingsLabel.BackColor = White;
        intervalLabel.ForeColor = TrayArt.Muted;
        intervalLabel.BackColor = White;
        startup.BackColor = White;
        empty.ForeColor = TrayArt.Muted;
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.BackColor = White;
        ConfigureButton(refresh, TrayArt.Accent, White);
        ConfigureButton(exit, TrayArt.Muted, White);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();
        interval.Items.AddRange(Enumerable.Range(5, 11).Select(n => (object)$"{n} 分钟").ToArray());
        interval.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && interval.SelectedIndex >= 0) IntervalChanged?.Invoke(IntervalMinutes);
        };
        startup.CheckedChanged += (_, _) => { if (!updating) StartupChanged?.Invoke(startup.Checked); };

        Controls.AddRange([title, status, refresh, list, settingsLabel, intervalLabel, interval, startup, exit]);
        list.Controls.Add(empty);
        SetInterval(10);
        LayoutPanel();
        DpiChanged += (_, _) => BeginInvoke(() => { LayoutPanel(); PlaceWindow(); });
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var style = base.CreateParams;
            style.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            style.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return style;
        }
    }

    private static void ConfigureButton(Button button, Color foreground, Color background)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = TrayArt.Border;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
    }

    public void SetInterval(int minutes)
    {
        updating = true;
        interval.SelectedIndex = Math.Clamp(minutes, 5, 15) - 5;
        updating = false;
    }

    public void SetStartup(bool enabled)
    {
        updating = true;
        startup.Checked = enabled;
        updating = false;
    }

    public void Report(string message) => status.Text = message;

    public void SetData(IReadOnlyList<BatteryReading> data, DateTime? lastUpdate, bool busy)
    {
        this.busy = busy;
        bool changed = !readings.SequenceEqual(data);
        readings = data;
        var beforeUpdate = updated;
        updated = lastUpdate;
        refresh.Enabled = !busy;
        if (busy) status.Text = "正在读取电量…";
        else if (beforeUpdate != lastUpdate || status.Text == "正在读取电量…")
            status.Text = lastUpdate is { } stamp
                ? $"{readings.Count} 台设备 · {stamp:HH:mm:ss} 更新" : "等待设备响应";

        if (!changed) return;
        list.SuspendLayout();
        foreach (var card in list.Controls.OfType<DeviceCard>().ToArray()) card.Dispose();
        empty.Visible = readings.Count == 0;
        foreach (var reading in readings) list.Controls.Add(new DeviceCard(reading));
        LayoutPanel();
        if (Visible) PlaceWindow();
        list.ResumeLayout();
    }

    public void Open(Point? near)
    {
        anchor = near;
        PlaceWindow();
        if (!Visible) Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!interval.DroppedDown) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keyData)
    {
        if (keyData == Keys.Escape && !interval.DroppedDown)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref m, keyData);
    }

    private int S(int x) => (int)Math.Round(x * DeviceDpi / 96.0);

    private void LayoutPanel()
    {
        int rows = Math.Clamp(readings.Count, 1, 4);
        int listHeight = readings.Count == 0 ? 72 : rows * 75 - 8;
        settingsTop = 88 + listHeight + 12;
        ClientSize = new Size(S(416), S(settingsTop + 113));
        title.SetBounds(S(21), S(16), S(252), S(33));
        status.SetBounds(S(23), S(54), S(264), S(19));
        refresh.SetBounds(S(302), S(26), S(92), S(32));
        list.SetBounds(S(21), S(87), S(374), S(listHeight));
        list.AutoScrollPosition = new Point(0, 0);
        settingsLabel.SetBounds(S(36), S(settingsTop + 12), S(105), S(20));
        intervalLabel.SetBounds(S(36), S(settingsTop + 42), S(63), S(21));
        interval.SetBounds(S(102), S(settingsTop + 39), S(95), S(31));
        startup.Location = new Point(S(224), S(settingsTop + 43));
        exit.SetBounds(S(307), S(settingsTop + 73), S(74), S(25));
        empty.Bounds = new Rectangle(0, 0, list.Width, list.Height);
        empty.Visible = readings.Count == 0;
        int cardWidth = list.Width - (readings.Count > rows ? SystemInformation.VerticalScrollBarWidth : 0);
        int index = 0;
        foreach (var card in list.Controls.OfType<DeviceCard>())
            card.Bounds = new Rectangle(0, S(index++ * 75), cardWidth, S(67));

        using var outline = TrayArt.Rounded(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), S(15));
        var old = Region;
        Region = new Region(outline);
        old?.Dispose();
        Invalidate();
    }

    private void PlaceWindow()
    {
        var monitor = anchor is Point pointer ? Screen.FromPoint(pointer) : Screen.FromPoint(Cursor.Position);
        var area = monitor.WorkingArea;
        int margin = S(9);
        int left = area.Right - Width - margin;
        int top = area.Bottom - Height - margin;
        if (anchor is Point point)
        {
            left = Math.Clamp(point.X - Width + S(36), area.Left + margin, Math.Max(area.Left + margin, left));
            if (point.Y < area.Top) top = area.Top + margin;
            else if (point.Y < area.Bottom) top = Math.Clamp(point.Y - Height - margin, area.Top + margin,
                Math.Max(area.Top + margin, top));
        }
        Location = new Point(Math.Max(area.Left, left), Math.Max(area.Top, top));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TrayArt.Outline(g, new RectangleF(.7f, .7f, ClientSize.Width - 1.4f, ClientSize.Height - 1.4f), S(15), TrayArt.Border);
        var settingsBox = new RectangleF(S(21), S(settingsTop), S(374), S(104));
        TrayArt.Fill(g, settingsBox, S(12), White);
        TrayArt.Outline(g, settingsBox, S(12), TrayArt.Border);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            titleFont.Dispose();
            bodyFont.Dispose();
            smallFont.Dispose();
            strongFont.Dispose();
        }
    }
}

internal sealed class DeviceCard : Control
{
    private readonly BatteryReading battery;

    public DeviceCard(BatteryReading battery)
    {
        this.battery = battery;
        AccessibleName = $"{battery.Name}，{battery.Percent}%";
        AccessibleRole = AccessibleRole.StaticText;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float width = Width / scale;
        var bounds = new RectangleF(.5f, .5f, width - 1, Height / scale - 1);
        TrayArt.Fill(g, bounds, 12, Color.White);
        TrayArt.Outline(g, bounds, 12, TrayArt.Border);

        TrayArt.Fill(g, new RectangleF(12, 10, 47, 47), 10, TrayArt.DeviceBackground(battery.Kind));
        g.TranslateTransform(18, 16);
        g.ScaleTransform(1.18f, 1.18f);
        TrayArt.DeviceGlyph(g, battery.Kind, 0, 0);
        g.ResetTransform();
        g.ScaleTransform(scale, scale);

        using var mainFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var pctFont = new Font("Segoe UI", 24, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(TrayArt.Ink);
        using var muted = new SolidBrush(TrayArt.Muted);
        using var textLeft = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var textRight = new StringFormat { Alignment = StringAlignment.Far };
        var palette = TrayArt.BatteryColors(battery.Percent, battery.Charging);
        using var batteryInk = new SolidBrush(palette.Foreground);
        var source = $"{TrayArt.DeviceLabel(battery.Kind)}  ·  {battery.Connection}{(battery.Charging ? "  ·  充电中" : "")}";
        g.DrawString(battery.Name, mainFont, ink, new RectangleF(70, 10, width - 166, 20), textLeft);
        g.DrawString(source, captionFont, muted, new RectangleF(70, 32, width - 150, 17), textLeft);
        g.DrawString($"{battery.Percent}%", pctFont, batteryInk, new RectangleF(width - 97, 12, 82, 33), textRight);
        float barWidth = width - 86;
        TrayArt.Fill(g, new RectangleF(71, 54, barWidth, 4), 2, palette.Background);
        TrayArt.Fill(g, new RectangleF(71, 54, barWidth * battery.Percent / 100, 4), 2, palette.Foreground);
    }
}
