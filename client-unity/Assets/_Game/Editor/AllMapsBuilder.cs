using UnityEditor;
using UnityEngine;

/// <summary>
/// G1: rebuilds every code-built map scene in one batch run (each builder records its map hash):
/// <c>-executeMethod AllMapsBuilder.Run</c>. Menu: Fall Rush/Setup/Build all maps.
/// </summary>
public static class AllMapsBuilder
{
    [MenuItem("Fall Rush/Setup/Build all maps")]
    public static void Run()
    {
        SandboxBuilder.Run();
        LobbyBuilder.Run();
        BlockPartyDashBuilder.Run();
        GooTowerBuilder.Run();
        StarScrambleBuilder.Run();
        CrumbleShelfBuilder.Run();
        AssetDatabase.SaveAssets();
        Debug.Log("[setup] all maps rebuilt");
    }
}
