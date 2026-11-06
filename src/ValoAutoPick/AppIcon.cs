using System.Reflection;

namespace ValoAutoPick;

internal static class AppIcon
{
    private static readonly byte[] Data = Load();
    private static byte[] Load()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValoAutoPick.Assets.QuickPickerPro-transparent.ico");
        if (resource == null) return [];
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }
    public static Icon Create()
    {
        if (Data.Length == 0) return (Icon)SystemIcons.Application.Clone();
        using var stream = new MemoryStream(Data, writable: false);
        using var source = new Icon(stream);
        return new Icon(source, source.Size);
    }
}
