using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ValoAutoPick;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(14, 19, 27);
    public static readonly Color Surface = Color.FromArgb(22, 29, 40);
    public static readonly Color Elevated = Color.FromArgb(31, 40, 53);
    public static readonly Color Border = Color.FromArgb(46, 57, 72);
    public static readonly Color Text = Color.FromArgb(244, 246, 250);
    public static readonly Color Muted = Color.FromArgb(150, 164, 183);
    public static readonly Color Red = Color.FromArgb(255, 70, 85);
    public static readonly Color Green = Color.FromArgb(99, 216, 164);
    public static Color Role(string role) => role switch
    {
        "デュエリスト" => Color.FromArgb(255, 98, 111),
        "イニシエーター" => Color.FromArgb(108, 202, 218),
        "コントローラー" => Color.FromArgb(174, 141, 249),
        "センチネル" => Color.FromArgb(235, 192, 106),
        _ => Red
    };
}

internal static class UiMetrics
{
    public const float CompactScale = .8f;
    public static int Px(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96f * CompactScale);
    // Keep text larger than the compact layout for comfortable reading.
    public static float FontSize(float value) => Math.Max(10f, value);
    public static float DisplayFontSize(float value) => Math.Max(10f, value * .9f);
}

internal static class AgentImages
{
    private static readonly Dictionary<string, Image> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static Image? Get(string id, string kind)
    {
        if (!Guid.TryParse(id, out _)) return null;
        var key = id + "-" + kind;
        if (Cache.TryGetValue(key, out var image)) return image;
        var ext = kind == "background" ? ".jpg" : ".png";
        var name = $"ValoAutoPick.Assets.Agents.{id}-{kind}{ext}";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return null;
            using var source = Image.FromStream(stream);
            image = new Bitmap(source);
            Cache[key] = image;
            return image;
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or IOException) { return null; }
    }
}

internal static class RoundedCorners
{
    public static void Apply(Control control, int radius)
    {
        if (control.Width < 2 || control.Height < 2) return;
        using var path = ThemedButton.Rounded(new Rectangle(0, 0, control.Width, control.Height), radius);
        var previous = control.Region;
        control.Region = new Region(path);
        previous?.Dispose();
        control.Invalidate();
    }
}

public sealed class RoundedCard : Panel
{
    public RoundedCard()
    {
        BackColor = Theme.Background;
        Padding = new Padding(14);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ThemedButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), UiMetrics.Px(this, 9));
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

public class DarkForm : Form
{
    private readonly WindowCaption caption;
    private readonly List<Font> compactFonts = [];

    public DarkForm()
    {
        // Build every form in logical pixels before applying its display DPI.
        AutoScaleMode = AutoScaleMode.None;
        Icon = AppIcon.Create();
        Font = new Font("Yu Gothic UI", 11.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Padding = new Padding(6);
        SizeGripStyle = SizeGripStyle.Hide;
        caption = new WindowCaption(this);
        Controls.Add(caption);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        // Content is added by each derived form after the caption is created.
        // Docking is calculated back-to-front, so reserve the caption's space
        // before a derived form's fill control is laid out.
        if (caption != null && e.Control != caption) Controls.SetChildIndex(caption, Controls.Count - 1);
    }

    protected override void OnLoad(EventArgs e)
    {
        // The former native caption was outside ClientSize. Reserve the same
        // content area for the custom caption and frame.
        var chromeSize = new Size(Padding.Horizontal, Padding.Vertical + caption.Height);
        ClientSize = new Size(ClientSize.Width + chromeSize.Width, ClientSize.Height + chromeSize.Height);
        if (!MinimumSize.IsEmpty)
            MinimumSize = new Size(MinimumSize.Width + chromeSize.Width, MinimumSize.Height + chromeSize.Height);
        caption.UpdateButtons();
        var fonts = Descendants(this).Select(control => (Control: control, Font: control.Font)).ToArray();
        AutoScaleMode = AutoScaleMode.Dpi;
        // Use one uniform compact scale alongside Windows' per-monitor DPI scale.
        AutoScaleDimensions = new SizeF(96 / UiMetrics.CompactScale, 96 / UiMetrics.CompactScale);
        PerformAutoScale();
        var fontCache = new Dictionary<Font, Font>();
        foreach (var entry in fonts)
        {
            if (!fontCache.TryGetValue(entry.Font, out var scaled))
            {
                scaled = new Font(entry.Font.FontFamily, UiMetrics.FontSize(entry.Font.Size), entry.Font.Style,
                    entry.Font.Unit, entry.Font.GdiCharSet, entry.Font.GdiVerticalFont);
                fontCache.Add(entry.Font, scaled);
                compactFonts.Add(scaled);
            }
            entry.Control.Font = scaled;
        }
        base.OnLoad(e);
        var available = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
        Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
        if (StartPosition == FormStartPosition.CenterScreen) CenterToScreen();
        else if (StartPosition == FormStartPosition.CenterParent && Owner != null) CenterToParent();
        Location = new Point(Math.Clamp(Left, available.Left, available.Right - Width), Math.Clamp(Top, available.Top, available.Bottom - Height));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { foreach (var font in compactFonts) font.Dispose(); compactFonts.Clear(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var border = new Pen(Color.FromArgb(62, 75, 91));
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var accent = new Pen(Theme.Red, Math.Max(1, UiMetrics.Px(this, 2)));
        e.Graphics.DrawLine(accent, UiMetrics.Px(this, 6), UiMetrics.Px(this, 2),
            UiMetrics.Px(this, 62), UiMetrics.Px(this, 2));
    }

    protected override void WndProc(ref Message m)
    {
        if (WindowFrame.ProcessMessage(this, caption, ref m)) return;
        base.WndProc(ref m);
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int SetDwmAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
    internal static void ApplyDarkScrollBars(Control control)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try { _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            int enabled = 1; _ = SetDwmAttribute(Handle, 20, ref enabled, sizeof(int));
            int squareCorners = 1; _ = SetDwmAttribute(Handle, 33, ref squareCorners, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

public sealed class SwitchToggle : Control
{
    private bool isChecked;
    private readonly InteractionMotion motion;
    public event EventHandler? CheckedChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => isChecked;
        set { if (isChecked == value) return; isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }
    public SwitchToggle(string text, bool isChecked)
    {
        Text = text; Checked = isChecked; ForeColor = Theme.Text;
        Font = new Font("Yu Gothic UI", 11.5f); Size = new Size(280, 52); Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton; AccessibleName = text; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        motion = new InteractionMotion(this);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int S(int value) => UiMetrics.Px(this, value);
        var track = new Rectangle(Width - S(60), (Height - S(30)) / 2, S(56), S(30));
        using var path = ThemedButton.Rounded(track, S(15));
        var trackColor = !Enabled ? Color.FromArgb(48, 54, 64) : Checked ? Theme.Red : Theme.Border;
        if (Enabled)
        {
            trackColor = InteractionMotion.Blend(trackColor, Color.White, motion.Hover * .18f);
            trackColor = InteractionMotion.Blend(trackColor, Color.Black, motion.Press * .25f);
        }
        using var fill = new SolidBrush(trackColor);
        e.Graphics.FillPath(fill, path);
        var knobX = Checked ? track.Right - S(25) : track.Left + S(5);
        using var knob = new SolidBrush(Enabled ? Color.White : Theme.Muted);
        var inset = (int)Math.Round(S(2) * motion.Press);
        e.Graphics.FillEllipse(knob, knobX + inset, track.Y + S(5) + inset, S(20) - inset * 2, S(20) - inset * 2);
        var label = new Rectangle(0, 0, Math.Max(0, track.Left - S(16)), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, label, Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, label);
    }
    protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

public sealed class ThemedButton : Control, IButtonControl
{
    private bool primary, isDefault, spacePressed;
    private readonly InteractionMotion motion;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary
    {
        get => primary;
        set { if (primary == value) return; primary = value; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact { get; set; }
    [DefaultValue(DialogResult.None)]
    public DialogResult DialogResult { get; set; }
    public ThemedButton()
    {
        BackColor = Theme.Elevated; ForeColor = Theme.Text;
        Font = new Font("Yu Gothic UI", 11.5f, FontStyle.Bold);
        Size = new Size(180, 52);
        Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
        // A native BUTTON can draw its accent border outside OnPaint. Use one
        // owner-drawn surface for the fill, rounded edge and focus indicator.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
            ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false);
        SizeChanged += (_, _) => UpdateRoundedRegion();
        UpdateRoundedRegion();
        motion = new InteractionMotion(this);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var shift = Enabled ? (int)Math.Round(UiMetrics.Px(this, 2) * motion.Press) : 0;
        var bounds = new Rectangle(shift, shift, Math.Max(1, Width - 1 - shift * 2), Math.Max(1, Height - 1 - shift));
        var fill = !Enabled ? Color.FromArgb(39, 47, 59) : Primary ? Theme.Red : Theme.Elevated;
        if (Enabled)
        {
            fill = InteractionMotion.Blend(fill, Primary ? Color.FromArgb(255, 112, 122) : Color.FromArgb(62, 79, 99), motion.Hover);
            fill = InteractionMotion.Blend(fill, Primary ? Color.FromArgb(199, 42, 57) : Color.FromArgb(15, 21, 30), motion.Press * .85f);
        }
        // Clear the outer corner pixels before drawing the rounded surface.
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        using var path = Rounded(bounds, ButtonRadius);
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        if (!Primary)
        {
            var border = Enabled ? InteractionMotion.Blend(Theme.Border, Theme.Muted, motion.Hover * .55f) : Color.FromArgb(55, 64, 78);
            using var pen = new Pen(border); e.Graphics.DrawPath(pen, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(bounds, -UiMetrics.Px(this, 16), -UiMetrics.Px(this, 3)), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        if ((Focused || isDefault) && Enabled && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -UiMetrics.Px(this, 5), -UiMetrics.Px(this, 5)), ForeColor, fill);
        }
    }
    public void NotifyDefault(bool value) { isDefault = value; Invalidate(); }
    public void PerformClick() { if (CanSelect) OnClick(EventArgs.Empty); }
    protected override void OnClick(EventArgs e)
    {
        if (!Enabled) return;
        if (DialogResult != DialogResult.None && FindForm() is { } form) form.DialogResult = DialogResult;
        base.OnClick(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Focus();
        base.OnMouseDown(e);
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { spacePressed = true; e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter) { PerformClick(); e.SuppressKeyPress = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && spacePressed) { spacePressed = false; PerformClick(); e.Handled = true; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { spacePressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override bool ProcessMnemonic(char charCode)
    {
        if (!CanSelect || !IsMnemonic(charCode, Text)) return base.ProcessMnemonic(charCode);
        Focus(); PerformClick(); return true;
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new ThemedButtonAccessibleObject(this);
    private sealed class ThemedButtonAccessibleObject(ThemedButton owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => "押す";
        public override void DoDefaultAction() => owner.PerformClick();
    }
    private int ButtonRadius => Math.Min(UiMetrics.Px(this, 12), Math.Max(4, Height / 3));
    private void UpdateRoundedRegion()
    {
        if (Width < 2 || Height < 2) return;
        using var path = Rounded(new Rectangle(0, 0, Width, Height), ButtonRadius);
        var next = new Region(path);
        var previous = Region;
        Region = next;
        previous?.Dispose();
        Invalidate();
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    internal static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath(); var d = radius * 2;
        if (radius < 2) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure();
        return path;
    }
}

internal sealed class ThemeMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Red;
    public override Color MenuItemSelected => Theme.Elevated;
    public override Color MenuItemSelectedGradientBegin => Theme.Elevated;
    public override Color MenuItemSelectedGradientEnd => Theme.Elevated;
    public override Color MenuItemPressedGradientBegin => Theme.Surface;
    public override Color MenuItemPressedGradientEnd => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
}

internal sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemeMenuRenderer() : base(new ThemeMenuColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        using var background = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(background, bounds);
        var inset = Math.Max(2, (int)Math.Round(3f * (e.ToolStrip?.DeviceDpi ?? 96) / 96));
        if (e.Item.Selected && bounds.Width > inset * 2 + 1 && bounds.Height > inset * 2 + 1)
        {
            var highlight = new Rectangle(inset, inset, bounds.Width - inset * 2 - 1, bounds.Height - inset * 2 - 1);
            using var fill = new SolidBrush(Theme.Elevated);
            using var outline = new Pen(Theme.Red);
            e.Graphics.FillRectangle(fill, highlight);
            e.Graphics.DrawRectangle(outline, highlight);
        }
    }
}

public sealed class ThemedSelect : Control
{
    private readonly string[] items;
    private int selectedIndex = -1;
    private ContextMenuStrip? picker;
    private readonly InteractionMotion motion;
    public event EventHandler? SelectedIndexChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var next = value >= 0 && value < items.Length ? value : -1;
            if (selectedIndex == next) return;
            selectedIndex = next; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public ThemedSelect(IEnumerable<string> items)
    {
        this.items = items.ToArray();
        Height = 48; BackColor = Theme.Elevated; ForeColor = Theme.Text; Font = new Font("Yu Gothic UI", 11.5f);
        Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.ComboBox;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        SizeChanged += (_, _) => RoundedCorners.Apply(this, 6);
        RoundedCorners.Apply(this, 6);
        motion = new InteractionMotion(this);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = ThemedButton.Rounded(bounds, 6);
        var fillColor = Enabled ? InteractionMotion.Blend(Color.FromArgb(26, 34, 46), Color.FromArgb(55, 70, 89), motion.Hover) : Color.FromArgb(31, 36, 45);
        if (Enabled) fillColor = InteractionMotion.Blend(fillColor, Theme.Background, motion.Press * .65f);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(Focused ? Theme.Red : InteractionMotion.Blend(Theme.Border, Theme.Muted, Enabled ? motion.Hover * .5f : 0));
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        var selected = selectedIndex >= 0 ? items[selectedIndex] : "選択";
        TextRenderer.DrawText(e.Graphics, selected, Font, new Rectangle(UiMetrics.Px(this, 16), 0, Math.Max(0, Width - UiMetrics.Px(this, 54)), Height), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var center = new PointF(Width - UiMetrics.Px(this, 22), Height / 2f);
        using var arrow = new Pen(Enabled ? Theme.Muted : Color.FromArgb(86, 96, 108), 1.6f);
        e.Graphics.DrawLines(arrow, [new PointF(center.X - 4, center.Y - 2), center, new PointF(center.X + 4, center.Y - 2)]);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3));
    }
    private void ShowPicker()
    {
        if (!Enabled || picker != null || items.Length == 0) return;
        picker = new ContextMenuStrip
        {
            BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Font,
            Renderer = new ThemeMenuRenderer(),
            ShowImageMargin = false, ShowCheckMargin = false, Padding = new Padding(3)
        };
        for (var i = 0; i < items.Length; i++)
        {
            var index = i;
            var entry = new ToolStripMenuItem(items[i])
            {
                Tag = index, Checked = index == selectedIndex,
                ForeColor = Theme.Text, BackColor = Theme.Surface, Padding = new Padding(9, 6, 9, 6),
                CheckOnClick = false
            };
            entry.Click += (_, _) => SelectedIndex = index;
            picker.Items.Add(entry);
        }
        picker.Opened += (_, _) =>
        {
            // Keep the drop-down and the field the same width.
            if (picker == null) return;
            picker.SuspendLayout();
            picker.AutoSize = false;
            foreach (ToolStripItem item in picker.Items)
            {
                item.AutoSize = false;
                item.Width = Math.Max(1, Width - picker.Padding.Horizontal - 2);
            }
            picker.Width = Width;
            picker.ResumeLayout(false);
            var area = Screen.FromControl(this).WorkingArea;
            if (picker.Right > area.Right) picker.Left = Math.Max(area.Left, area.Right - picker.Width);
        };
        picker.Closed += (_, _) =>
        {
            var closed = picker; picker = null;
            if (closed != null && IsHandleCreated && !IsDisposed) BeginInvoke(closed.Dispose);
            Invalidate();
        };
        picker.Show(this, new Point(0, Height));
    }
    protected override void OnClick(EventArgs e) { base.OnClick(e); ShowPicker(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.F4 || e.Alt && e.KeyCode == Keys.Down) { ShowPicker(); e.Handled = true; }
        else if (e.KeyCode == Keys.Down && selectedIndex < items.Length - 1) { SelectedIndex = Math.Max(0, selectedIndex + 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Up && selectedIndex > 0) { SelectedIndex--; e.Handled = true; }
    }
}

public sealed class HeroPanel : Panel
{
    private Agent? agent;
    private readonly ThemedButton choose = new() { Text = "エージェントを選ぶ", Primary = true, Size = new(212, 52), Anchor = AnchorStyles.Left | AnchorStyles.Bottom, TabIndex = 0 };
    public event EventHandler? ChooseClicked;
    public HeroPanel()
    {
        DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface;
        choose.Location = new(29, Height - 81); choose.Click += (_, _) => ChooseClicked?.Invoke(this, EventArgs.Empty); Controls.Add(choose);
        Resize += (_, _) => { choose.Left = UiMetrics.Px(this, 29); choose.Top = Height - choose.Height - UiMetrics.Px(this, 29); RoundedCorners.Apply(this, UiMetrics.Px(this, 9)); };
        RoundedCorners.Apply(this, UiMetrics.Px(this, 9));
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Agent? Agent
    {
        get => agent;
        set { agent = value; choose.Text = value == null ? "エージェントを選ぶ" : "エージェントを変更"; Invalidate(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.HighQuality;
        int S(int value) => UiMetrics.Px(this, value);
        g.Clear(Theme.Surface);
        using var clip = ThemedButton.Rounded(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), S(9));
        var originalState = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        if (agent == null)
        {
            using var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(36, 45, 60), Color.FromArgb(17, 23, 33), 10f);
            g.FillRectangle(brush, ClientRectangle);
            using var motif = new Pen(Color.FromArgb(30, Theme.Text), 1);
            for (int x = Width - 30; x < Width + 170; x += 32) g.DrawLine(motif, x, 0, x - 180, Height);
        }
        else
        {
            var bg = AgentImages.Get(agent.Id, "background");
            if (bg != null) DrawCover(g, bg, ClientRectangle);
            else g.Clear(Theme.Surface);
            using var shade = new SolidBrush(Color.FromArgb(100, Theme.Background)); g.FillRectangle(shade, ClientRectangle);
            var portrait = AgentImages.Get(agent.Id, "portrait");
            if (portrait != null)
            {
                var ratio = (float)portrait.Width / portrait.Height;
                var targetW = Math.Min(Width * .94f, Height * 1.20f * ratio);
                var targetH = targetW / ratio;
                var target = new RectangleF(Width - targetW + S(34), (Height - targetH) / 2f - S(4), targetW, targetH);
                g.DrawImage(portrait, target);
            }
            using var leftShade = new LinearGradientBrush(ClientRectangle, Color.FromArgb(248, Theme.Background), Color.FromArgb(0, Theme.Background), LinearGradientMode.Horizontal);
            leftShade.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(248, Theme.Background), Color.FromArgb(0, Theme.Background), Color.FromArgb(0, Theme.Background)],
                Positions = [0f, .75f, 1f]
            };
            g.FillRectangle(leftShade, ClientRectangle);
            using var edge = new SolidBrush(Theme.Role(agent.Role)); g.FillRectangle(edge, 0, 0, S(4), Height);
        }
        g.Restore(originalState);
        using var promptFont = new Font("Yu Gothic UI", UiMetrics.DisplayFontSize(26), FontStyle.Bold);
        using var roleFont = new Font("Yu Gothic UI", UiMetrics.FontSize(12), FontStyle.Bold);
        if (agent == null)
        {
            TextRenderer.DrawText(g, "エージェントを選択", promptFont, new Rectangle(S(29), S(74), Width - S(58), S(70)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        else
        {
            var textWidth = Math.Min(Width - S(58), Math.Max(S(162), (int)(Width * .60f)));
            var nameSize = UiMetrics.DisplayFontSize(36f);
            while (nameSize > UiMetrics.DisplayFontSize(21f))
            {
                using var measureFont = new Font("Yu Gothic UI", nameSize, FontStyle.Bold);
                if (TextRenderer.MeasureText(agent.Name, measureFont).Width <= textWidth) break;
                nameSize -= 1.6f;
            }
            using var nameFont = new Font("Yu Gothic UI", nameSize, FontStyle.Bold);
            TextRenderer.DrawText(g, agent.Name, nameFont, new Rectangle(S(29), S(74), textWidth, S(70)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var roleColor = Theme.Role(agent.Role);
            TextRenderer.DrawText(g, agent.Role, roleFont, new Rectangle(S(29), S(151), textWidth, S(29)), roleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        using var border = new Pen(Theme.Border);
        using var outline = ThemedButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(9));
        g.DrawPath(border, outline);
    }
    private static void DrawCover(Graphics g, Image image, Rectangle rect)
    {
        var scale = Math.Max((float)rect.Width / image.Width, (float)rect.Height / image.Height);
        var width = image.Width * scale; var height = image.Height * scale;
        g.DrawImage(image, new RectangleF(rect.X + (rect.Width - width) / 2, rect.Y + (rect.Height - height) / 2, width, height));
    }
}

public sealed class AgentTile : Panel
{
    public Agent Agent { get; }
    public event EventHandler<Agent>? AgentClicked;
    private readonly bool selected;
    private readonly InteractionMotion motion;
    public AgentTile(Agent agent, bool selected = false)
    {
        Agent = agent; this.selected = selected; Width = 164; Height = 180; Margin = new(6); Cursor = Cursors.Hand; TabStop = true; DoubleBuffered = true;
        SizeChanged += (_, _) => RoundedCorners.Apply(this, UiMetrics.Px(this, 9));
        RoundedCorners.Apply(this, UiMetrics.Px(this, 9));
        motion = new InteractionMotion(this);
        MouseClick += (_, _) => AgentClicked?.Invoke(this, agent);
        MouseDown += (_, _) => Focus();
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) AgentClicked?.Invoke(this, agent); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ThemedButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), UiMetrics.Px(this, 9));
        var accent = Theme.Role(Agent.Role);
        var fillColor = selected ? InteractionMotion.Blend(Theme.Surface, accent, .14f) : Theme.Surface;
        fillColor = InteractionMotion.Blend(fillColor, Theme.Elevated, motion.Hover * .85f);
        fillColor = InteractionMotion.Blend(fillColor, Theme.Background, motion.Press * .55f);
        using var fill = new SolidBrush(fillColor); e.Graphics.FillPath(fill, path);
        using var outline = new Pen(InteractionMotion.Blend(Theme.Border, accent, selected ? 1 : motion.Hover), selected ? 2 : 1); e.Graphics.DrawPath(outline, path);
        int S(int value) => UiMetrics.Px(this, value);
        var compact = Height < S(110) || Width < S(130);
        using var nameFont = new Font("Yu Gothic UI", UiMetrics.FontSize(compact ? 10 : 11.5f), FontStyle.Bold);
        using var badgeFont = new Font("Yu Gothic UI", UiMetrics.FontSize(10), FontStyle.Bold);
        var labelHeight = S(compact ? 24 : 30);
        var iconSize = Math.Max(1, Math.Min(Width - S(24), Height - labelHeight - S(12)));
        var icon = AgentImages.Get(Agent.Id, "icon");
        if (icon != null) e.Graphics.DrawImage(icon, new Rectangle((Width - iconSize) / 2, S(6), iconSize, iconSize));
        TextRenderer.DrawText(e.Graphics, Agent.Name, nameFont, new Rectangle(S(4), Height - labelHeight - S(4), Width - S(8), labelHeight), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (selected)
        {
            using var badge = new SolidBrush(accent); e.Graphics.FillEllipse(badge, Width - S(28), S(8), S(20), S(20));
            TextRenderer.DrawText(e.Graphics, "✓", badgeFont, new Rectangle(Width - S(28), S(7), S(20), S(22)), Theme.Background,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 9, Height - 9));
    }
}

// A small, owner-drawn scrollbar for the agent list. The content viewport is
// kept separate so Windows never paints a light native scrollbar over it.
internal sealed class PickerScrollBar : Control
{
    private int maximum, viewport, value, dragOffset;
    private bool dragging, hovering;
    public event EventHandler? ValueChanged;
    public int Value => value;

    public PickerScrollBar()
    {
        BackColor = Theme.Background;
        AccessibleName = "エージェント一覧のスクロール";
        AccessibleRole = AccessibleRole.ScrollBar;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    public void SetRange(int contentHeight, int viewportHeight)
    {
        viewport = Math.Max(0, viewportHeight);
        maximum = Math.Max(0, contentHeight - viewport);
        SetValue(value);
        Invalidate();
    }

    public void SetValue(int next)
    {
        next = Math.Clamp(next, 0, maximum);
        if (value == next) return;
        value = next;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle Thumb()
    {
        var inset = UiMetrics.Px(this, 4);
        var trackHeight = Math.Max(1, Height - inset * 2);
        var thumbHeight = maximum == 0 ? trackHeight : Math.Clamp((int)((long)trackHeight * viewport / (maximum + viewport)), Math.Min(trackHeight, UiMetrics.Px(this, 36)), trackHeight);
        var top = inset + (maximum == 0 ? 0 : (int)((long)(trackHeight - thumbHeight) * value / maximum));
        return new Rectangle(inset, top, Math.Max(1, Width - inset * 2), thumbHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (maximum == 0 || Width < 3 || Height < 3) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(Width / 2 - UiMetrics.Px(this, 2), UiMetrics.Px(this, 4), UiMetrics.Px(this, 4), Math.Max(1, Height - UiMetrics.Px(this, 8)));
        using var trackPath = ThemedButton.Rounded(track, Math.Max(1, track.Width / 2));
        using var trackBrush = new SolidBrush(Theme.Surface);
        e.Graphics.FillPath(trackBrush, trackPath);
        var thumb = Thumb();
        using var thumbPath = ThemedButton.Rounded(thumb, Math.Max(1, thumb.Width / 2));
        using var thumbBrush = new SolidBrush(dragging ? Theme.Red : hovering ? Theme.Muted : Theme.Border);
        e.Graphics.FillPath(thumbBrush, thumbPath);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || maximum == 0) return;
        Focus();
        var thumb = Thumb();
        if (!thumb.Contains(e.Location)) { SetValue(value + (e.Y < thumb.Top ? -viewport : viewport)); return; }
        dragging = true; dragOffset = e.Y - thumb.Top; Capture = true; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        var inset = UiMetrics.Px(this, 4);
        var travel = Math.Max(1, Height - 2 * inset - Thumb().Height);
        SetValue((int)((long)(e.Y - dragOffset - inset) * maximum / travel));
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); dragging = false; Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var next = e.KeyCode switch
        {
            Keys.Up => value - UiMetrics.Px(this, 48), Keys.Down => value + UiMetrics.Px(this, 48),
            Keys.PageUp => value - viewport, Keys.PageDown => value + viewport,
            Keys.Home => 0, Keys.End => maximum, _ => value
        };
        if (next != value) { SetValue(next); e.Handled = true; }
    }
}

public sealed class AgentPickerDialog : DarkForm
{
    private readonly FlowLayoutPanel groupsHost = new()
    {
        BackColor = Theme.Background, Padding = new Padding(12),
        Margin = Padding.Empty, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false
    };
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
    private readonly PickerScrollBar scrollBar = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label footer = new()
    {
        Text = "クリックで選択　·　Escで戻る", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 9.5f), Padding = new Padding(12, 0, 0, 0),
        BackColor = Theme.Surface
    };
    private readonly List<RoleGroup> roleGroups = [];
    private int arrangedWidth = -1;
    private bool layoutReady;

    private sealed record RoleGroup(string Role, Panel Section, Label Heading, TableLayoutPanel Grid, List<AgentTile> Tiles);

    public Agent? SelectedAgent { get; private set; }

    public AgentPickerDialog(IEnumerable<Agent> source, string? selectedId)
    {
        DoubleBuffered = true;
        Text = "エージェントを選択"; StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background; ForeColor = Theme.Text;
        ClientSize = new(1000, 740); MinimumSize = new(760, 560); MaximizeBox = false; MinimizeBox = false;
        var roleOrder = new[] { "デュエリスト", "イニシエーター", "コントローラー", "センチネル" };
        var groups = source.GroupBy(a => string.IsNullOrWhiteSpace(a.Role) ? "その他" : a.Role)
            .OrderBy(group => { var index = Array.IndexOf(roleOrder, group.Key); return index >= 0 ? index : roleOrder.Length; })
            .ThenBy(group => group.Key, StringComparer.CurrentCulture);
        var nextTabIndex = 0;
        foreach (var group in groups)
        {
            var tiles = group.OrderBy(a => a.Name, StringComparer.CurrentCulture).Select(agent => new AgentTile(agent,
                string.Equals(agent.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                Dock = DockStyle.Fill, AccessibleName = $"{agent.Name}、{agent.Role}",
                Margin = new Padding(6), TabIndex = nextTabIndex++
            }).ToList();
            foreach (var tile in tiles)
            {
                tile.AgentClicked += (_, chosen) => { SelectedAgent = chosen; DialogResult = DialogResult.OK; Close(); };
                tile.Enter += (_, _) => EnsureTileVisible(tile);
            }

            var section = new Panel { BackColor = Theme.Background, Margin = new Padding(0, 0, 0, 12) };
            var heading = new Label
            {
                Text = $"{group.Key}　{tiles.Count}人", Location = Point.Empty, Height = 38,
                Font = new Font("Yu Gothic UI", 13f, FontStyle.Bold), ForeColor = Theme.Role(group.Key),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            var grid = new TableLayoutPanel
            {
                BackColor = Theme.Background, Location = new Point(0, heading.Height), Margin = Padding.Empty,
                Padding = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            section.Controls.Add(grid); section.Controls.Add(heading);
            roleGroups.Add(new RoleGroup(group.Key, section, heading, grid, tiles));
            groupsHost.Controls.Add(section);
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        viewport.Controls.Add(groupsHost);
        listLayout.Controls.Add(viewport, 0, 0); listLayout.Controls.Add(scrollBar, 1, 0);
        layout.Controls.Add(listLayout, 0, 0); layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);
        scrollBar.ValueChanged += (_, _) => groupsHost.Top = -scrollBar.Value;
        viewport.SizeChanged += (_, _) => ArrangeTiles();
        void AttachWheel(Control control)
        {
            control.MouseWheel += (_, e) =>
            {
                scrollBar.SetValue(scrollBar.Value - e.Delta * UiMetrics.Px(this, 48) / 120 * Math.Max(1, SystemInformation.MouseWheelScrollLines));
                if (e is HandledMouseEventArgs handled) handled.Handled = true;
            };
        }
        AttachWheel(viewport); AttachWheel(groupsHost); AttachWheel(scrollBar);
        foreach (var group in roleGroups)
        {
            AttachWheel(group.Section); AttachWheel(group.Heading); AttachWheel(group.Grid);
            foreach (var tile in group.Tiles) AttachWheel(tile);
        }
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        layoutReady = true;
        ArrangeTiles();
    }

    protected override CreateParams CreateParams
    {
        get { var result = base.CreateParams; result.ExStyle |= 0x02000000; return result; }
    }

    private void EnsureTileVisible(Control tile)
    {
        var top = groupsHost.PointToClient(tile.PointToScreen(Point.Empty)).Y;
        if (top < scrollBar.Value) scrollBar.SetValue(top);
        else if (top + tile.Height > scrollBar.Value + viewport.ClientSize.Height)
            scrollBar.SetValue(top + tile.Height - viewport.ClientSize.Height);
    }

    private void ArrangeTiles()
    {
        if (!layoutReady || viewport.ClientSize.Width <= 0 || viewport.ClientSize.Height <= 0) return;
        groupsHost.Width = viewport.ClientSize.Width;
        var width = Math.Max(1, groupsHost.ClientSize.Width - groupsHost.Padding.Horizontal - UiMetrics.Px(this, 8));
        if (width == arrangedWidth)
        {
            scrollBar.SetRange(groupsHost.Height, viewport.ClientSize.Height);
            return;
        }
        arrangedWidth = width;
        var columns = Math.Max(1, width / UiMetrics.Px(this, 176));
        var headingHeight = UiMetrics.Px(this, 38);
        var rowHeight = UiMetrics.Px(this, 192);
        groupsHost.SuspendLayout();
        foreach (var group in roleGroups)
        {
            var rows = (int)Math.Ceiling(group.Tiles.Count / (double)columns);
            group.Section.SuspendLayout(); group.Grid.SuspendLayout();
            group.Section.Width = width;
            group.Heading.Width = width;
            group.Heading.Height = headingHeight;
            group.Grid.Location = new Point(0, headingHeight);
            group.Grid.Width = width;
            group.Grid.Height = rows * rowHeight;
            group.Grid.ColumnCount = columns; group.Grid.RowCount = rows;
            group.Grid.ColumnStyles.Clear(); group.Grid.RowStyles.Clear();
            for (var column = 0; column < columns; column++)
                group.Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (var row = 0; row < rows; row++)
                group.Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            group.Grid.Controls.Clear();
            for (var i = 0; i < group.Tiles.Count; i++)
                group.Grid.Controls.Add(group.Tiles[i], i % columns, i / columns);
            group.Section.Height = headingHeight + group.Grid.Height;
            group.Grid.ResumeLayout(true); group.Section.ResumeLayout(true);
        }
        groupsHost.Height = groupsHost.Padding.Vertical + roleGroups.Sum(group => group.Section.Height + group.Section.Margin.Vertical);
        groupsHost.ResumeLayout(true);
        scrollBar.SetRange(groupsHost.Height, viewport.ClientSize.Height);
        groupsHost.Top = -scrollBar.Value;
    }
}
