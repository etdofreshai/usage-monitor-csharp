using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace UsageMonitor.Services;

internal static class MacWindowPresenter
{
    private static readonly nuint MoveToActiveSpace = (nuint)1 << 1;
    private static readonly nuint FullScreenAuxiliary = (nuint)1 << 8;
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
            behavior &= ~(nuint)1; // CanJoinAllSpaces conflicts with MoveToActiveSpace.
            behavior |= MoveToActiveSpace | FullScreenAuxiliary;

            SendVoidNUInt(nativeWindow, Selector("setCollectionBehavior:"), behavior);
            SendVoidNInt(nativeWindow, Selector("setLevel:"), FloatingWindowLevel);

            var applicationClass = objc_getClass("NSApplication");
            var application = SendIntPtr(applicationClass, Selector("sharedApplication"));
            SendVoidByte(application, Selector("activateIgnoringOtherApps:"), 1);
            SendVoidIntPtr(nativeWindow, Selector("makeKeyAndOrderFront:"), 0);
            SendVoid(nativeWindow, Selector("orderFrontRegardless"));
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"macOS popup activation failed: {ex.Message}");
        }
    }

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendIntPtr(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidByte(nint receiver, nint selector, byte value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidIntPtr(nint receiver, nint selector, nint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNInt(nint receiver, nint selector, nint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNUInt(nint receiver, nint selector, nuint value);
}
