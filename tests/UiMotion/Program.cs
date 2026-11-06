using System.Diagnostics;
using System.Reflection;
using ValoAutoPick;

internal sealed class TestWindow : DarkForm
{
    protected override bool ShowWithoutActivation => true;
}

internal static class Program
{
    private static readonly MethodInfo MouseEnter = typeof(Control).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseLeave = typeof(Control).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseDown = typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseUp = typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo KeyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo KeyUp = typeof(Control).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new TestWindow { Text = "UI animation checks", ClientSize = new Size(850, 720), Opacity = 0, ShowInTaskbar = false };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.TopDown,
            BackColor = Color.FromArgb(22, 29, 40) };
        var primary = new ThemedButton { Text = "実行", Primary = true };
        var secondary = new ThemedButton { Text = "設定" };
        var toggle = new SwitchToggle("自動で監視", false);
        var select = new ThemedSelect(["自動", "地域"]) { Size = new Size(220, 48) };
        var tile = new AgentTile(new Agent("1dbf2edd-4729-0984-3115-daa5eed44993", "ジェット", "デュエリスト"));
        var nativeType = typeof(DarkForm).Assembly.GetType("ValoAutoPick.AnimatedNativeButton")!;
        var native = (Button)Activator.CreateInstance(nativeType, true)!;
        native.Text = "設定を解除"; native.Size = new Size(180, 40);
        panel.Controls.AddRange([primary, secondary, toggle, select, tile, native]);
        form.Controls.Add(panel);
        form.Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(100);
                var caption = form.Controls.Cast<Control>().Single(c => c.GetType().Name == "WindowCaption");
                var captionButtons = caption.Controls.Cast<Control>().ToArray();
                await CheckMotion("primary button", primary, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("secondary button", secondary, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("switch", toggle, c => new Point(c.Width - Px(c, 11), c.Height / 2));
                await CheckMotion("selection control", select, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("agent tile", tile, c => new Point(Px(c, 8), c.Height / 2));
                await CheckMotion("relay button", native, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                foreach (var button in captionButtons)
                    await CheckMotion("caption " + button.AccessibleName, button, _ => new Point(4, 4));

                primary.Enabled = false;
                var disabled = Pixel(primary, new Point(primary.Width / 2, primary.Height / 6));
                MouseEnter.Invoke(primary, [EventArgs.Empty]);
                MouseDown.Invoke(primary, [new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)]);
                await Task.Delay(200);
                Assert(Distance(disabled, Pixel(primary, new Point(primary.Width / 2, primary.Height / 6))) == 0,
                    "disabled button does not animate");
                var clickCount = 0;
                secondary.Click += (_, _) => clickCount++;
                var keySample = new Point(secondary.Width / 2, secondary.Height / 6);
                var keyIdle = Pixel(secondary, keySample);
                KeyDown.Invoke(secondary, [new KeyEventArgs(Keys.Space)]);
                await Task.Delay(120);
                Assert(Distance(keyIdle, Pixel(secondary, keySample)) > 5, "keyboard press has feedback");
                KeyUp.Invoke(secondary, [new KeyEventArgs(Keys.Space)]);
                Assert(clickCount == 1, "keyboard activation still fires once");
                Console.WriteLine("UI motion checks passed");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private static int Px(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96f * .7f);
    private static Color Pixel(Control control, Point point)
    {
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, control.ClientRectangle);
        return image.GetPixel(point.X, point.Y);
    }
    private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static void Assert(bool condition, string text)
    {
        if (!condition) throw new InvalidOperationException(text);
        Console.WriteLine("PASS " + text);
    }

    private static async Task CheckMotion(string name, Control control, Func<Control, Point> sample)
    {
        var point = sample(control);
        var idle = Pixel(control, point);
        var clock = Stopwatch.StartNew();
        MouseEnter.Invoke(control, [EventArgs.Empty]);
        await Task.Delay(55);
        var intermediate = Pixel(control, point);
        var observedAt = clock.ElapsedMilliseconds;
        await Task.Delay(200);
        var hovered = Pixel(control, point);
        Assert(Distance(idle, hovered) > 5, name + " changes on hover");
        if (observedAt < 130)
            Assert(Distance(idle, intermediate) > 0 && Distance(intermediate, hovered) > 0, name + " transitions through intermediate frames");
        MouseDown.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
        await Task.Delay(150);
        var pressed = Pixel(control, point);
        Assert(Distance(hovered, pressed) > 5, name + $" changes while pressed (hover={hovered}, pressed={pressed})");
        MouseUp.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
        await Task.Delay(200);
        Assert(Distance(hovered, Pixel(control, point)) <= 3, name + " returns to hover after release");
        if (name == "primary button")
        {
            MouseDown.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
            MouseUp.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
            await Task.Delay(25);
            Assert(Distance(hovered, Pixel(control, point)) > 2, "a quick click remains visible after release");
            await Task.Delay(200);
        }
        MouseLeave.Invoke(control, [EventArgs.Empty]);
        await Task.Delay(240);
        Assert(Distance(idle, Pixel(control, point)) <= 3, name + " returns to idle after leaving");
    }
}
