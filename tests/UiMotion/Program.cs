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
        var select = new ThemedSelect(["自動", "アジア太平洋（東京など）とても長い選択肢"]) { Size = new Size(220, 48) };
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
                var assembly = typeof(MatchMapPanel).Assembly;
                foreach (var (key, _) in MapCatalog.Maps)
                {
                    using var asset = assembly.GetManifestResourceStream($"ValoAutoPick.Assets.Maps.{key}.jpg");
                    Assert(asset != null, $"map artwork embedded: {key}");
                    using var decoded = Image.FromStream(asset!);
                    Assert(decoded.Width > 0 && decoded.Height > 0, $"map artwork readable: {key}");
                }
                using (var map = new MatchMapPanel { Size = new Size(500, 250) })
                {
                    map.SetMatch("/Game/Maps/Ascent/Ascent", "ジェット");
                    Assert(map.AccessibleName?.Contains("アセント") == true, "matched map is presented");
                    using var rendered = new Bitmap(map.Width, map.Height);
                    map.DrawToBitmap(rendered, map.ClientRectangle);
                    map.SetMatch("", "");
                    Assert(map.AccessibleName?.Contains("待機中") == true, "map resets to waiting");
                }
                using (var iconResource = assembly.GetManifestResourceStream("ValoAutoPick.Assets.QuickPickerPro-transparent.ico"))
                using (var expected = new Icon(iconResource!))
                using (var expectedBitmap = expected.ToBitmap())
                using (var actualBitmap = form.Icon!.ToBitmap())
                using (var picker = new AgentPickerDialog(Array.Empty<Agent>(), null))
                using (var pickerBitmap = picker.Icon!.ToBitmap())
                {
                    Assert(expectedBitmap.Size == pickerBitmap.Size && expectedBitmap.Size == actualBitmap.Size, "dialog icon size matches app icon");
                    for (int y = 0; y < expectedBitmap.Height; y++)
                        for (int x = 0; x < expectedBitmap.Width; x++)
                            if (expectedBitmap.GetPixel(x,y).ToArgb() != pickerBitmap.GetPixel(x,y).ToArgb() || expectedBitmap.GetPixel(x,y).ToArgb() != actualBitmap.GetPixel(x,y).ToArgb())
                                throw new InvalidOperationException("dialog icon differs from app icon");
                    Console.WriteLine("PASS agent picker uses app icon");
                }
                using (var main = new MainWindow())
                {
                    var apply = typeof(MainWindow).GetMethod("ApplyAccountLabels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var loginButton = (ThemedButton)typeof(MainWindow).GetField("login", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var accountLabel = (Label)typeof(MainWindow).GetField("accountInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    apply.Invoke(main, [true, "Player#1234"]);
                    Assert(loginButton.Text == "Player#1234" && accountLabel.Text == "接続済み", "connected account shows user name and connected status");
                    apply.Invoke(main, [false, ""]);
                    Assert(loginButton.Text == "Riotにログイン" && accountLabel.Text == "Riotアカウント: 未接続", "signed-out labels remain available");
                    ((NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Dispose();
                }
                var caption = form.Controls.Cast<Control>().Single(c => c.GetType().Name == "WindowCaption");
                var captionButtons = caption.Controls.Cast<Control>().ToArray();
                await CheckMotion("primary button", primary, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("secondary button", secondary, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("switch", toggle, c => new Point(c.Width - Px(c, 11), c.Height / 2));
                await CheckMotion("selection control", select, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                typeof(ThemedSelect).GetMethod("ShowPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(select, null);
                await Task.Delay(100);
                var menu = (ContextMenuStrip)typeof(ThemedSelect).GetField("picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(select)!;
                Assert(menu.Width == select.Width, $"selection menu matches control width ({menu.Width} == {select.Width})");
                menu.Items[1].PerformClick();
                Assert(select.SelectedIndex == 1, "selection menu still selects an item");
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
