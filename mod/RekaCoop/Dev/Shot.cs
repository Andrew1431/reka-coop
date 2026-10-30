#if DEVBUILD
using System.IO;
using UnityEngine;

namespace RekaCoop.Dev;

// Drop a file named `shot.request` in the game folder and the game saves `shot.png` next to it on the
// next frame. Reads the game's own back buffer, so it works even when the window is covered.
internal static class Shot
{
    private static float _nextCheck;

    public static void Tick()
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 0.25f;
        var request = Path.Combine(BepInEx.Paths.GameRootPath, "shot.request");
        if (!File.Exists(request)) return;
        File.Delete(request);
        ScreenCapture.CaptureScreenshot(Path.Combine(BepInEx.Paths.GameRootPath, "shot.png"));
    }
}
#endif
