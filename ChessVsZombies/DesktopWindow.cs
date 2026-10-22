using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace ChessVsZombies;

internal static class DesktopWindow
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MaximizeWindow(IntPtr window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetKeyboardFocus();

    private sealed class SdlWindows
    {
        // Keep one reference for the process lifetime. Input checks run for
        // every update/warp and must not load and unload SDL on each call.
        private readonly IntPtr library;
        public readonly MaximizeWindow Maximize;
        public readonly GetKeyboardFocus KeyboardFocus;

        public SdlWindows()
        {
            var libraryName = OperatingSystem.IsWindows() ? "SDL2.dll"
                : OperatingSystem.IsMacOS() ? "libSDL2-2.0.0.dylib" : "libSDL2-2.0.so.0";
            library = NativeLibrary.Load(libraryName, typeof(Game).Assembly, null);
            Maximize = Marshal.GetDelegateForFunctionPointer<MaximizeWindow>(NativeLibrary.GetExport(library, "SDL_MaximizeWindow"));
            KeyboardFocus = Marshal.GetDelegateForFunctionPointer<GetKeyboardFocus>(NativeLibrary.GetExport(library, "SDL_GetKeyboardFocus"));
        }
    }

    private static readonly Lazy<SdlWindows> Sdl = new(() => new SdlWindows());

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static bool HasInputFocus(GameWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            // The SDL/MonoGame focus event can trail an OS focus change. Check
            // the foreground process before touching the global mouse cursor.
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out uint processId);
            return processId == Environment.ProcessId;
        }
        return window.Handle != IntPtr.Zero && Sdl.Value.KeyboardFocus() == window.Handle;
    }

    public static void Maximize(GameWindow window)
    {
        // DesktopGL exposes an SDL_Window pointer, not a platform window handle.
        Sdl.Value.Maximize(window.Handle);
    }
}
