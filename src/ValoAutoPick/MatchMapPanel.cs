using System.Drawing.Drawing2D;
using System.Reflection;

namespace ValoAutoPick;

// Map artwork is embedded so showing a match never depends on a network request.
public sealed class MatchMapPanel : Panel
{
    private readonly Dictionary<string, Image?> images = new(StringComparer.OrdinalIgnoreCase);
    private string mapId = "", agentName = "";
    public MatchMapPanel()
    {
        DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "現在のマップ: マッチを待機中";
        SizeChanged += (_, _) => RoundedCorners.Apply(this, UiMetrics.Px(this, 10));
    }
    public void SetMatch(string? id, string? agent)
    {
        id ??= ""; agent ??= "";
        if (id == mapId && agent == agentName) return;
        mapId = id; agentName = agent;
        AccessibleName = id.Length == 0 ? "現在のマップ: マッチを待機中" : $"現在のマップ: {MapCatalog.Name(id)}";
        Invalidate();
    }
    private Image? Artwork(string id)
    {
        var key = MapCatalog.Key(id);
        // Only look up known embedded names, never paths supplied by the game.
        var known = MapCatalog.Maps.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key;
        if (string.IsNullOrEmpty(known)) return null;
        if (images.TryGetValue(known, out var cached)) return cached;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ValoAutoPick.Assets.Maps.{known}.jpg");
        if (stream == null) return images[known] = null;
        using var original = Image.FromStream(stream);
        return images[known] = new Bitmap(original);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int S(int value) => UiMetrics.Px(this, value);
        var rect = new Rectangle(0, 0, Width, Height);
        g.Clear(Color.FromArgb(22, 33, 46));
        var image = mapId.Length == 0 ? null : Artwork(mapId);
        if (image != null)
        {
            float scale = Math.Max((float)Width / image.Width, (float)Height / image.Height);
            var width = image.Width * scale; var height = image.Height * scale;
            g.DrawImage(image, (Width - width) / 2, (Height - height) / 2, width, height);
        }
        else
        {
            using var fallback = new LinearGradientBrush(rect, Color.FromArgb(32, 52, 68), mapId.Length == 0 ? Color.FromArgb(27, 41, 55) : Theme.Background, 20f);
            g.FillRectangle(fallback, rect);
            using var grid = new Pen(Color.FromArgb(22, 133, 212, 218), Math.Max(1, S(1)));
            for (var x = -Height; x < Width + Height; x += S(45)) g.DrawLine(grid, x, Height, x + Height, 0);
        }
        using (var shade = new LinearGradientBrush(rect, Color.FromArgb(50, 7, 12, 19), Color.FromArgb(mapId.Length == 0 ? 95 : 235, 7, 12, 19), 90f)) g.FillRectangle(shade, rect);
        using var topFont = new Font("Yu Gothic UI", UiMetrics.FontSize(10), FontStyle.Bold);
        using var nameFont = new Font("Yu Gothic UI", UiMetrics.DisplayFontSize(32), FontStyle.Bold);
        using var infoFont = new Font("Yu Gothic UI", UiMetrics.FontSize(11), FontStyle.Regular);
        var padding = S(24);
        using var accent = new SolidBrush(Color.FromArgb(99, 216, 210));
        g.FillRectangle(accent, padding, S(23), S(5), S(18));
        TextRenderer.DrawText(g, "現在のマップ", topFont, new Rectangle(padding + S(13), S(18), Width - padding * 2, S(28)), Color.FromArgb(195, 228, 228), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var name = mapId.Length == 0 ? "マッチを待機中" : MapCatalog.Name(mapId);
        TextRenderer.DrawText(g, name, nameFont, new Rectangle(padding, Height - S(91), Width - padding * 2, S(49)), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        var info = mapId.Length == 0 ? "" : string.IsNullOrEmpty(agentName) ? "エージェント未設定" : $"設定エージェント  ·  {agentName}";
        TextRenderer.DrawText(g, info, infoFont, new Rectangle(padding, Height - S(43), Width - padding * 2, S(25)), Color.FromArgb(211, 224, 229), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var image in images.Values) image?.Dispose();
        base.Dispose(disposing);
    }
}
