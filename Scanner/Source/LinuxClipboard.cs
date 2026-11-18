using System.Runtime.InteropServices;

namespace WrongWay;

static class LinuxClipboard
{
    // Avalonia looks up these atoms at startup. A bare X11 session may not have
    // them yet, so create them before Avalonia opens its display connection.
    public static void Initialize()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return;

        nint display = XOpenDisplay(0);
        if (display == 0)
            return;
        try
        {
            foreach (string atom in new[] { "CLIPBOARD", "UTF8_STRING", "TARGETS", "MULTIPLE", "ATOM_PAIR", "INCR" })
                XInternAtom(display, atom, 0);
        }
        finally { XCloseDisplay(display); }
    }

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(nint name);

    [DllImport("libX11.so.6")]
    private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPStr)] string name, int onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);
}
