using System.Drawing.Drawing2D;

namespace ValoAutoPick;

// Keep the standard Windows window styles for keyboard shortcuts and the
// system menu, but use our own client area instead of the native caption.
internal static class WindowFrame
{
    private const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmSysCommand = 0x0112;
    private const int HtCaption = 2, ScSize = 0xf000, ScMaximize = 0xf030;

    internal static bool ProcessMessage(Form form, Control? caption, ref Message message)
    {
        if (message.Msg == WmNcCalcSize)
        {
            message.Result = IntPtr.Zero;
            return true;
        }

        if (message.Msg == WmSysCommand && (message.WParam.ToInt64() & 0xfff0) is ScSize or ScMaximize)
        {
            message.Result = IntPtr.Zero;
            return true;
        }

        if (message.Msg != WmNcHitTest || caption == null) return false;
        long packedPoint = message.LParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(packedPoint & 0xffff)), unchecked((short)((packedPoint >> 16) & 0xffff)));
        var point = form.PointToClient(screenPoint);
        var client = form.ClientRectangle;
        if (!client.Contains(point)) return false;

        if (caption.Bounds.Contains(point))
        {
            message.Result = (IntPtr)HtCaption;
            return true;
        }
        return false;
    }
}

internal sealed class WindowCaption : Panel
{
    private const int WmNcHitTest = 0x0084, HtTransparent = -1;
    private readonly Form owner;
    private readonly CaptionButton minimize, close;
    private readonly Font brandFont = new("Segoe UI", UiMetrics.FontSize(9f), FontStyle.Bold);
    private readonly Font detailFont = new("Yu Gothic UI", UiMetrics.FontSize(8.5f), FontStyle.Bold);

    internal WindowCaption(Form owner)
    {
        this.owner = owner;
        Dock = DockStyle.Top; Height = 40; Margin = Padding.Empty;
        BackColor = Color.FromArgb(20, 27, 37);
        DoubleBuffered = true;
        minimize = new CaptionButton(CaptionAction.Minimize, owner);
        close = new CaptionButton(CaptionAction.Close, owner);
        Controls.Add(minimize); Controls.Add(close);
        owner.TextChanged += (_, _) => Invalidate();
    }

    internal void UpdateButtons()
    {
        minimize.Visible = owner.ControlBox && owner.MinimizeBox;
        close.Visible = owner.ControlBox;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int S(int value) => UiMetrics.Px(this, value);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var middle = Height / 2;
        using (var red = new SolidBrush(Theme.Red))
        {
            g.FillPolygon(red, [new Point(S(15), middle - S(7)), new Point(S(20), middle - S(7)),
                new Point(S(14), middle + S(7)), new Point(S(9), middle + S(7))]);
            g.FillPolygon(red, [new Point(S(22), middle - S(7)), new Point(S(27), middle - S(7)),
                new Point(S(21), middle + S(7)), new Point(S(16), middle + S(7))]);
        }
        TextRenderer.DrawText(g, "VALBUDDY", brandFont, new Rectangle(S(35), 0, S(104), Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using (var divider = new Pen(Theme.Border)) g.DrawLine(divider, S(140), S(12), S(140), Height - S(12));
        var detail = owner.Text.Equals("ValBuddy", StringComparison.OrdinalIgnoreCase) ? "AUTO PICK" : owner.Text;
        var available = Math.Max(0, Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Left).DefaultIfEmpty(Width).Min() - S(160));
        TextRenderer.DrawText(g, detail, detailFont, new Rectangle(S(153), 0, available, Height), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using var bottom = new Pen(Theme.Border);
        g.DrawLine(bottom, 0, Height - 1, Width, Height - 1);
    }

    protected override void WndProc(ref Message m)
    {
        // Forward empty caption space to the form's native hit test so Windows
        // owns dragging and the system menu.
        if (m.Msg == WmNcHitTest) { m.Result = (IntPtr)HtTransparent; return; }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { brandFont.Dispose(); detailFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal enum CaptionAction { Minimize, Close }

internal sealed class CaptionButton : Control
{
    private readonly CaptionAction action;
    private readonly Form owner;
    private readonly InteractionMotion motion;
    private bool spacePressed;

    internal CaptionButton(CaptionAction action, Form owner)
    {
        this.action = action; this.owner = owner;
        Dock = DockStyle.Right; Width = 46; TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        UpdateState();
        motion = new InteractionMotion(this);
    }

    internal void UpdateState()
    {
        AccessibleName = action switch
        {
            CaptionAction.Minimize => "最小化",
            _ => "閉じる"
        };
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int value) => UiMetrics.Px(this, value);
        var normal = Parent?.BackColor ?? Theme.Surface;
        var background = InteractionMotion.Blend(normal, action == CaptionAction.Close ? Theme.Red : Color.FromArgb(54, 68, 85), motion.Hover);
        background = InteractionMotion.Blend(background, action == CaptionAction.Close ? Color.FromArgb(179, 36, 51) : Color.FromArgb(13, 19, 27), motion.Press * .85f);
        e.Graphics.Clear(background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Text, Math.Max(1f, S(1))) { StartCap = LineCap.Square, EndCap = LineCap.Square };
        var x = Width / 2; var y = Height / 2 + (int)Math.Round(S(2) * motion.Press);
        if (action == CaptionAction.Minimize)
            e.Graphics.DrawLine(pen, x - S(5), y + S(3), x + S(5), y + S(3));
        else if (action == CaptionAction.Close)
        {
            e.Graphics.DrawLine(pen, x - S(5), y - S(5), x + S(5), y + S(5));
            e.Graphics.DrawLine(pen, x + S(5), y - S(5), x - S(5), y + S(5));
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -S(4), -S(4)));
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        switch (action)
        {
            case CaptionAction.Minimize: owner.WindowState = FormWindowState.Minimized; break;
            case CaptionAction.Close: owner.Close(); break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { spacePressed = true; e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.SuppressKeyPress = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && spacePressed) { spacePressed = false; OnClick(EventArgs.Empty); e.Handled = true; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); spacePressed = false; Invalidate(); }
    protected override AccessibleObject CreateAccessibilityInstance() => new CaptionButtonAccessibleObject(this);

    private sealed class CaptionButtonAccessibleObject(CaptionButton owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => "押す";
        public override void DoDefaultAction() { if (owner.CanSelect) owner.OnClick(EventArgs.Empty); }
    }
}
