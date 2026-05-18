namespace Pictomancy;

public enum AutoDraw
{
    /// <summary>Pictomancy renders to a texture but does not display it. Caller is responsible for using the texture.</summary>
    None = 0,
    /// <summary>Auto-display the result via ImGui's drawlist (composited the same frame, no display lag).</summary>
    ImGuiOverlay = 1,
    /// <summary>Auto-display the result via the game's native overlay node (lower UI layer; carries a 1-frame display lag).</summary>
    NativeOverlay = 2,
    /// <summary>Experimental: composite into the game backbuffer from a D3D11 hook before native UI/nameplates when the frame timing allows it.</summary>
    SceneComposite = 3,
}
