using System.Runtime.InteropServices;

namespace PoVoiceHud.App.Interop;

/// <summary>
/// Keeps a window out of screen captures and screen sharing: while the screen is watched, the HUD must not read its own
/// verdicts back as if they were on the screen.
/// </summary>
internal static partial class CaptureExclusion
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;

    /// <summary>False when Windows refused (before Windows 10 2004).</summary>
    public static bool Set(nint window, bool excluded) => SetWindowDisplayAffinity(window, excluded ? WdaExcludeFromCapture : WdaNone);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);
}
