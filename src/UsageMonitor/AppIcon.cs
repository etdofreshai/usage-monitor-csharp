using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UsageMonitor;

public static class AppIcon
{
    private const string IconUri = "avares://UsageMonitor/Assets/usage-monitor.ico";

    public static WindowIcon Create()
    {
        return new WindowIcon(AssetLoader.Open(new Uri(IconUri)));
    }

    public static Bitmap CreateBitmap()
    {
        return new Bitmap(AssetLoader.Open(new Uri(IconUri)));
    }
}
