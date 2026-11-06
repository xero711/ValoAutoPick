namespace ValoAutoPick;

// One short-lived timer per active interaction keeps transitions smooth without
// repainting idle controls. The owner disposes the timer when it is disposed.
internal sealed class InteractionMotion : IDisposable
{
    private readonly Control owner;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private bool hovering, mouseDown, keyDown;
    private float hover, press;
    private long lastTick;

    internal float Hover => Ease(hover);
    internal float Press => Ease(press);

    internal InteractionMotion(Control owner)
    {
        this.owner = owner;
        timer.Tick += Tick;
        owner.MouseEnter += MouseEnter;
        owner.MouseLeave += MouseLeave;
        owner.MouseDown += MouseDown;
        owner.MouseUp += MouseUp;
        owner.MouseCaptureChanged += CaptureChanged;
        owner.KeyDown += KeyDown;
        owner.KeyUp += KeyUp;
        owner.LostFocus += LostFocus;
        owner.EnabledChanged += EnabledChanged;
        owner.VisibleChanged += VisibleChanged;
        owner.Disposed += OwnerDisposed;
    }

    private static float Ease(float value) => value * value * (3f - 2f * value);
    private static float Approach(float value, float target, float step) =>
        value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    private void Begin()
    {
        if (owner.IsDisposed || !owner.Visible) { Reset(); return; }
        if (!timer.Enabled) { lastTick = Environment.TickCount64; timer.Start(); }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (owner.IsDisposed || !owner.Visible) { Reset(); return; }
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - lastTick, 1, 250);
        lastTick = now;
        var hoverTarget = owner.Enabled && hovering ? 1f : 0f;
        var pressTarget = owner.Enabled && ((mouseDown && hovering) || keyDown) ? 1f : 0f;
        hover = Approach(hover, hoverTarget, elapsed / (hoverTarget > hover ? 140f : 180f));
        press = Approach(press, pressTarget, elapsed / (pressTarget > press ? 70f : 130f));
        owner.Invalidate();
        if (hover == hoverTarget && press == pressTarget) timer.Stop();
    }

    private void MouseEnter(object? sender, EventArgs e) { hovering = true; Begin(); }
    private void MouseLeave(object? sender, EventArgs e) { hovering = false; Begin(); }
    private void MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !owner.Enabled) return;
        mouseDown = true;
        press = Math.Max(press, .45f); // Keep even a very quick click visible for a few frames.
        owner.Invalidate();
        Begin();
    }
    private void MouseUp(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { mouseDown = false; Begin(); } }
    private void CaptureChanged(object? sender, EventArgs e) { if (!owner.Capture && mouseDown) { mouseDown = false; Begin(); } }
    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (!owner.Enabled || e.KeyCode is not (Keys.Space or Keys.Enter)) return;
        keyDown = true;
        press = Math.Max(press, .45f);
        owner.Invalidate();
        Begin();
    }
    private void KeyUp(object? sender, KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { keyDown = false; Begin(); } }
    private void LostFocus(object? sender, EventArgs e) { if (keyDown) { keyDown = false; Begin(); } }
    private void EnabledChanged(object? sender, EventArgs e)
    {
        if (!owner.Enabled) { mouseDown = false; keyDown = false; }
        Begin();
    }
    private void VisibleChanged(object? sender, EventArgs e) { if (!owner.Visible) Reset(); }
    private void OwnerDisposed(object? sender, EventArgs e) => Dispose();
    private void Reset() { timer.Stop(); hovering = mouseDown = keyDown = false; hover = press = 0; }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= Tick;
        timer.Dispose();
        owner.MouseEnter -= MouseEnter;
        owner.MouseLeave -= MouseLeave;
        owner.MouseDown -= MouseDown;
        owner.MouseUp -= MouseUp;
        owner.MouseCaptureChanged -= CaptureChanged;
        owner.KeyDown -= KeyDown;
        owner.KeyUp -= KeyUp;
        owner.LostFocus -= LostFocus;
        owner.EnabledChanged -= EnabledChanged;
        owner.VisibleChanged -= VisibleChanged;
        owner.Disposed -= OwnerDisposed;
    }

    internal static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static int Channel(int a, int b, float t) => (int)Math.Round(a + (b - a) * t);
        return Color.FromArgb(Channel(from.A, to.A, amount), Channel(from.R, to.R, amount),
            Channel(from.G, to.G, amount), Channel(from.B, to.B, amount));
    }
}

// Relay-dialog buttons retain native Button sizing and keyboard behavior while
// using the same animated appearance as the rest of the application.
internal sealed class AnimatedNativeButton : Button
{
    private readonly InteractionMotion motion;

    internal AnimatedNativeButton()
    {
        motion = new InteractionMotion(this);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var normal = Enabled ? Theme.Elevated : Color.FromArgb(39, 47, 59);
        var fill = Enabled ? InteractionMotion.Blend(normal, Color.FromArgb(62, 79, 99), motion.Hover) : normal;
        if (Enabled) fill = InteractionMotion.Blend(fill, Color.FromArgb(15, 21, 30), motion.Press * .8f);
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        using var brush = new SolidBrush(fill);
        using var border = new Pen(Enabled ? InteractionMotion.Blend(Theme.Border, Theme.Muted, motion.Hover * .55f) : Theme.Border);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        e.Graphics.FillRectangle(brush, bounds);
        e.Graphics.DrawRectangle(border, bounds);
        var offset = (int)Math.Round(UiMetrics.Px(this, 2) * motion.Press);
        bounds.Offset(0, offset);
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}
