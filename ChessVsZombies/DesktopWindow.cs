using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace ChessVsZombies;

internal static class DesktopWindow
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MaximizeWindow(IntPtr window);

    public static void Maximize(GameWindow window)
    {
        // DesktopGL exposes an SDL_Window pointer, not a platform window handle.
        var libraryName = OperatingSystem.IsWindows() ? "SDL2.dll"
            : OperatingSystem.IsMacOS() ? "libSDL2-2.0.0.dylib" : "libSDL2-2.0.so.0";
        var library = NativeLibrary.Load(libraryName, typeof(Game).Assembly, null);
        try
        {
            var maximize = Marshal.GetDelegateForFunctionPointer<MaximizeWindow>(
                NativeLibrary.GetExport(library, "SDL_MaximizeWindow"));
            maximize(window.Handle);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
