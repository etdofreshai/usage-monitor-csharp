using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace UsageMonitor.Services;

internal static class MacWindowPresenter
{
    private static readonly nuint CanJoinAllSpaces = (nuint)1;
    private static readonly nuint MoveToActiveSpace = (nuint)1 << 1;
    private static readonly nuint Managed = (nuint)1 << 2;
    private static readonly nuint Transient = (nuint)1 << 3;
    private static readonly nuint Stationary = (nuint)1 << 4;
    private static readonly nuint ParticipatesInCycle = (nuint)1 << 5;
    private static readonly nuint IgnoresCycle = (nuint)1 << 6;
    private static readonly nuint FullScreenAuxiliary = (nuint)1 << 8;
    private static readonly nuint Primary = (nuint)1 << 16;
    private static readonly nuint Auxiliary = (nuint)1 << 17;
    private static readonly nuint CanJoinAllApplications = (nuint)1 << 18;
    private const nint FloatingWindowLevel = 3;

    public static void BringToFront(Window window)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle is null || platformHandle.HandleDescriptor != "NSWindow")
            return;

        try
        {
            var nativeWindow = platformHandle.Handle;
            var behavior = SendNUInt(nativeWindow, Selector("collectionBehavior"));
            behavior &= ~(MoveToActiveSpace | Managed | Stationary | ParticipatesInCycle | Primary | Auxiliary);
            behavior |= CanJoinAllSpaces | CanJoinAllApplications | Transient | IgnoresCycle | FullScreenAuxiliary;

            SendVoidNUInt(nativeWindow, Selector("setCollectionBehavior:"), behavior);
            SendVoidNInt(nativeWindow, Selector("setLevel:"), FloatingWindowLevel);
            SendVoid(nativeWindow, Selector("orderFrontRegardless"));
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"macOS popup activation failed: {ex.Message}");
        }
    }

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNInt(nint receiver, nint selector, nint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNUInt(nint receiver, nint selector, nuint value);
}
