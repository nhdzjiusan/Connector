using System.Drawing.Drawing2D;

namespace PeripheralPower;

internal sealed class BatteryPanel : Form
{
    private readonly bool? themeOverride;
    internal bool Dark { get; private set; }
    internal Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(241, 248, 255) : Color.FromArgb(20, 41, 65);
    internal Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(190, 208, 227) : Color.FromArgb(61, 83, 107);
    private Bitmap? material;
    private readonly System.Windows.Forms.Timer lightTimer = new() { Interval = 40 };
    private Point? light;
    private int cardWidth, listHeight, renderedScroll;
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
    private readonly CheckBox notifications = new() { Text = "低电量提醒", AutoSize = true, BackColor = Color.Transparent };
    public event Action<bool>? NotificationsChanged;
    public void SetNotifications(bool enabled) { updating = true; notifications.Checked = enabled; updating = false; }
    private readonly Button exit = new() { Text = "退出应用" };
    private readonly Label empty = new() { Text = "暂无可显示电量的外设\r\n唤醒无线设备后，可点「立即刷新」。" };
    private IReadOnlyList<BatteryReading> readings = [];
    private DateTime? updated;
    private bool updating;
    private int settingsTop;
    private Point? anchor;

    public event Action? RefreshRequested;
    public event Action<int>? IntervalChanged;
    public event Action<bool>? StartupChanged;
    public event Action? ExitRequested;
    public int IntervalMinutes => interval.SelectedIndex + 5;

    public BatteryPanel(bool? darkOverride = null)
    {
        themeOverride = darkOverride;
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
        settingsLabel.Font = strongFont;
        empty.TextAlign = ContentAlignment.MiddleCenter;
        foreach (var control in new Control[] { title, status, list, settingsLabel, intervalLabel, startup, empty })
            control.BackColor = Color.Transparent;
        ConfigureButton(refresh);
        ConfigureButton(exit);
        interval.FlatStyle = FlatStyle.Flat;
        interval.DrawMode = DrawMode.OwnerDrawFixed;
        interval.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool selected = e.State.HasFlag(DrawItemState.Selected) && interval.DroppedDown;
            var background = selected ? SystemColors.Highlight : interval.BackColor;
            var foreground = selected ? SystemColors.HighlightText : Ink;
            using var fill = new SolidBrush(background);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, interval.Items[e.Index]?.ToString(), interval.Font, e.Bounds,
                foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
        ApplyTheme();
        lightTimer.Tick += (_, _) =>
        {
            Point cursor = PointToClient(Cursor.Position);
            Point? next = ClientRectangle.Contains(cursor) ? cursor : null;
            if (next == light) return;
            if (light is Point old) Invalidate(GlowBounds(old), true);
            light = next;
            if (next is Point current) Invalidate(GlowBounds(current), true);
        };
        list.Scroll += (_, _) => Invalidate(true);
        list.MouseWheel += (_, _) => Invalidate(true);
        VisibleChanged += (_, _) =>
        {
            lightTimer.Enabled = Visible && MotionAllowed;
            if (!Visible) light = null;
        };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();
        interval.Items.AddRange(Enumerable.Range(5, 11).Select(n => (object)$"{n} 分钟").ToArray());
        interval.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && interval.SelectedIndex >= 0) IntervalChanged?.Invoke(IntervalMinutes);
        };
        startup.CheckedChanged += (_, _) => { if (!updating) StartupChanged?.Invoke(startup.Checked); };
        notifications.CheckedChanged += (_,_) => { if (!updating) NotificationsChanged?.Invoke(notifications.Checked); };
        Controls.Add(notifications);

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

    private static void ConfigureButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Color.Transparent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(25, 140, 195, 215);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(52, 105, 155, 190);
        button.Cursor = Cursors.Hand;
    }

    private void ApplyTheme()
    {
        bool nextDark = themeOverride ?? (Settings.Theme switch { "light" => false, "dark" => true, _ => (Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int value && value == 0) });
        lightTimer.Enabled = Visible && MotionAllowed;
        if (!MotionAllowed) light = null;
        if (SystemInformation.HighContrast)
        {
            BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
            status.ForeColor = intervalLabel.ForeColor = empty.ForeColor = exit.ForeColor = refresh.ForeColor = SystemColors.WindowText;
            interval.BackColor = SystemColors.Window; interval.ForeColor = SystemColors.WindowText;
            ResetMaterial(); return;
        }
        if (material != null && nextDark == Dark) return;
        Dark = nextDark;
        BackColor = Dark ? Color.FromArgb(27, 45, 67) : Color.FromArgb(213, 233, 244);
        ForeColor = Ink;
        status.ForeColor = intervalLabel.ForeColor = empty.ForeColor = exit.ForeColor = Muted;
        refresh.ForeColor = Dark ? Color.FromArgb(176, 234, 251) : Color.FromArgb(16, 79, 107);
        interval.BackColor = Dark ? Color.FromArgb(45, 65, 87) : Color.FromArgb(238, 247, 252);
        interval.ForeColor = Ink;
        ResetMaterial();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if ((m.Msg == 0x001A || m.Msg == 0x031A) && IsHandleCreated && !IsDisposed)
            ApplyTheme();
    }

    private void ResetMaterial()
    {
        material?.Dispose();
        material = null;
        Invalidate(true);
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
        var scroll = list.AutoScrollPosition;
        var cards = list.Controls.OfType<DeviceCard>().ToDictionary(c => c.Id);
        bool layout = cards.Count != data.Count || !cards.Keys.SequenceEqual(data.Select(r => r.Id));
        empty.Visible = readings.Count == 0;
        int index = 0;
        foreach (var reading in data)
        {
            if (cards.Remove(reading.Id,out var card)) card.UpdateReading(reading);
            else list.Controls.Add(card = new DeviceCard(reading));
            if (layout) list.Controls.SetChildIndex(card,index);
            index++;
        }
        foreach (var old in cards.Values) old.Dispose();
        if (layout) { LayoutPanel(); list.AutoScrollPosition = new Point(-scroll.X,-scroll.Y); if (Visible) PlaceWindow(); }
        list.ResumeLayout();
    }

    public void Open(Point? near)
    {
        ApplyTheme();
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
    private bool MotionAllowed => !SystemInformation.HighContrast && System.Windows.SystemParameters.ClientAreaAnimation;
    private Rectangle GlowBounds(Point p) => new(p.X-S(92),p.Y-S(66),S(184),S(132));

    private void LayoutPanel()
    {
        int rows = Math.Clamp(readings.Count, 1, 4);
        interval.ItemHeight = Math.Max(S(19), interval.Font.Height + S(2));
        listHeight = readings.Count == 0 ? 70 : rows * 78 - 8;
        settingsTop = 86 + listHeight + 12;
        ClientSize = new Size(S(442), S(settingsTop + 102));
        title.SetBounds(S(79), S(19), S(214), S(32));
        status.SetBounds(S(81), S(51), S(216), S(20));
        refresh.SetBounds(S(320), S(29), S(101), S(29));
        list.SetBounds(S(16), S(86), S(410), S(listHeight));
        list.AutoScrollPosition = Point.Empty;
        settingsLabel.SetBounds(S(32), S(settingsTop + 12), S(120), S(22));
        intervalLabel.SetBounds(S(32), S(settingsTop + 49), S(67), S(23));
        interval.SetBounds(S(105), S(settingsTop + 46), S(97), S(28));
        startup.Location = new Point(S(241), S(settingsTop + 48));
        notifications.Location = new Point(S(151), S(settingsTop + 12));
        exit.SetBounds(S(336), S(settingsTop + 9), S(80), S(27));
        empty.Bounds = new Rectangle(0, 0, list.Width, list.Height);
        empty.Visible = readings.Count == 0;
        int pixels = list.Width - (readings.Count > rows ? SystemInformation.VerticalScrollBarWidth : 0);
        cardWidth = (int)Math.Round(pixels * 96.0 / DeviceDpi);
        int index = 0;
        foreach (var card in list.Controls.OfType<DeviceCard>())
            card.Bounds = new Rectangle(0, S(index++ * 78), pixels, S(70));

        using var outline = TrayArt.Rounded(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), S(23));
        var old = Region;
        Region = new Region(outline);
        old?.Dispose();
        ResetMaterial();
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

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { e.Graphics.Clear(SystemColors.Window); return; }
        if (listHeight == 0) { base.OnPaintBackground(e); return; }
        float scale = DeviceDpi / 96f;
        int scroll = -list.AutoScrollPosition.Y;
        if (scroll != renderedScroll)
        {
            renderedScroll = scroll;
            material?.Dispose();
            material = null;
        }
        material ??= GlassMaterial.Render(442, settingsTop + 102, listHeight, cardWidth, readings.Count,
            scroll / scale, settingsTop, scale, Dark);
        var g = e.Graphics;
        var saved = g.Save();
        g.DrawImage(material, ClientRectangle);
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (light is Point cursor)
        {
            float x = cursor.X / scale, y = cursor.Y / scale;
            using var path = new GraphicsPath();
            path.AddEllipse(x - 90, y - 64, 180, 128);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(Dark ? 20 : 32, 225, 249, 255),
                SurroundColors = [Color.Transparent]
            };
            g.FillPath(glow, path);
        }
        TrayArt.Fill(g, new RectangleF(27, 23, 43, 42), 16,
            Dark ? Color.FromArgb(188, 239, 237) : Color.FromArgb(30, 71, 89));
        var mark = Dark ? Color.FromArgb(21, 60, 77) : Color.FromArgb(217, 255, 246);
        TrayArt.Outline(g, new RectangleF(37, 37, 20, 13), 3, mark);
        TrayArt.Fill(g, new RectangleF(40, 40, 12, 7), 1.5f, mark);
        TrayArt.Fill(g, new RectangleF(59, 41, 2, 5), 1, mark);
        g.Restore(saved);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            lightTimer.Dispose();
            material?.Dispose();
            titleFont.Dispose();
            bodyFont.Dispose();
            smallFont.Dispose();
            strongFont.Dispose();
        }
    }
}

internal sealed class DeviceCard : Control
{
    private BatteryReading battery;
    public string Id => battery.Id;
    public void UpdateReading(BatteryReading next)
    {
        if (battery == next) return;
        battery = next; AccessibleName = $"{next.Name}，{next.Percent}% {next.PowerStatus}".TrimEnd();
        Invalidate();
    }

    public DeviceCard(BatteryReading battery)
    {
        this.battery = battery;
        AccessibleName = $"{battery.Name}，{battery.Percent}% {battery.PowerStatus}".TrimEnd();
        AccessibleRole = AccessibleRole.StaticText;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float width = Width / scale;
        var panel = (BatteryPanel)FindForm()!;
        TrayArt.Fill(g, new RectangleF(12, 11, 47, 47), 15, TrayArt.DeviceBackground(battery.Kind));
        g.TranslateTransform(18, 16);
        g.ScaleTransform(1.18f, 1.18f);
        TrayArt.DeviceGlyph(g, battery.Kind, 0, 0);
        g.ResetTransform();
        g.ScaleTransform(scale, scale);

        using var mainFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var pctFont = new Font("Segoe UI", 24, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(panel.Ink);
        using var muted = new SolidBrush(panel.Muted);
        using var textLeft = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var textRight = new StringFormat { Alignment = StringAlignment.Far };
        var palette = TrayArt.BatteryColors(battery.Percent, battery.Charging || battery.IsReference);
        if (panel.Dark)
        {
            var color = battery.Charging || battery.IsReference ? Color.FromArgb(161, 209, 255) :
                battery.Percent <= 20 ? Color.FromArgb(255, 172, 183) :
                battery.Percent <= 40 ? Color.FromArgb(255, 212, 148) : Color.FromArgb(149, 238, 209);
            palette = (color, Color.FromArgb(78, 106, 129));
        }
        if (SystemInformation.HighContrast) palette = (SystemColors.WindowText,SystemColors.ControlDark);
        using var batteryInk = new SolidBrush(palette.Foreground);
        var source = $"{TrayArt.DeviceLabel(battery.Kind)}  ·  {battery.Connection}";
        if (battery.PowerStatus.Length > 0) source += $"  ·  {battery.PowerStatus}";
        g.DrawString(battery.Name, mainFont, ink, new RectangleF(70, 10, width - 166, 20), textLeft);
        g.DrawString(source, captionFont, muted, new RectangleF(70, 33, width - 99, 17), textLeft);
        g.DrawString($"{battery.Percent}%", pctFont, batteryInk, new RectangleF(width - 97, 12, 82, 33), textRight);
        float barWidth = width - 86;
        TrayArt.Fill(g, new RectangleF(71, 56, barWidth, 4), 2, palette.Background);
        TrayArt.Fill(g, new RectangleF(71, 56, barWidth * battery.Percent / 100, 4), 2, palette.Foreground);
    }
}
