using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds <c>Map_CrumbleShelf</c> as <b>Lid Flip</b> (G4.5, doc 08 §7, the Final; map key, id 3 and scene name unchanged):
/// <list type="bullet">
/// <item><b>Top shelf</b> (<c>cs_lids_top</c>): a 9 × 9 <c>LID_FLIP</c> grid of 1.8 m capsule lids (gap 6 cm, 0.4 m thick,
/// tops at y 6). Waves light a seeded share of the lids yellow for 1 s, then they swing open for 1 s; the waves speed up
/// stage by stage and the outer rings retire every 25 s down to the inner 3 × 3 (≥ 5 of it solid at every tick).</item>
/// <item><b>Sweeper</b> (<c>cs_sweep_top</c>): one low spinner bar (0.35 m, jump it) hanging from the claw over the grid
/// centre, arm 8.4 m (reaches ring 4), sweeping from 20 s (raised until then). Variant B turns the other way.</item>
/// <item><b>Lower shelf</b> (<c>cs_lids_low</c>): a 5 × 5 grid under the top one (tops at y 0), its own slower schedule from
/// 30 s, ring 2 retiring at 75 s. A fall from the top lands here — one more chance; a fall from here passes the kill-Y (−6 m).</item>
/// <item>The claw-machine frame around it (dressing only, outside every gameplay volume). No in-world text.</item>
/// </list>
/// Every lid's state is a pure function of the round's hazard seed (<c>ObstaclePhase.Hash(hazardSeed, id)</c>), so in M6 the
/// VRF seed decides the pattern. The reactive crumble grid (<see cref="HazardCrumbleGrid"/>) stays in the code as the
/// fallback, unused here. Batch: <c>-executeMethod CrumbleShelfBuilder.Run</c>.
/// </summary>
public static class CrumbleShelfBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_CrumbleShelf.unity";
    public const float KillY = -6f;
    public const float TopY = 6f, LowY = 0f;
    public const int TopN = 9, LowN = 5;
    public const string TopId = "cs_lids_top", LowId = "cs_lids_low", SweepId = "cs_sweep_top";
    public const float SweepArm = 8.4f, SweepBar = 0.35f;
    public const int SweepPeriod = 134;            // 4.5 s a turn: tip 11.8 m/s (cap 12)
    public const int SweepStartTicks = 600;        // 20 s
    public const float Frame = 10.6f;              // claw-machine posts (the top grid's half width is 8.37 m)

    /// <summary>
    /// The top shelf, variant A: <see cref="LidGridDef.Default"/> geometry and retire times (rings at 25 / 50 / 75 s), lit 1 s,
    /// open 1 s, waves 90 → 60 ticks, 16 → 50 % of the alive lids. Fairness (G4.5-2): lit + open ≤ the shortest interval,
    /// so a lid never closes already yellow from the next wave (every flip follows a full 1 s of yellow).
    /// </summary>
    public static LidGridDef TopA()
    {
        var d = LidGridDef.Default(0, KitBuilder.Mm(TopY), 0, TopN);
        d.FlippedTicks = 30;
        d.WaveIntervals = new[] { 90, 75, 66, 60 };
        d.FlipPermille = new[] { 160, 240, 380, 500 };
        return d;
    }

    /// <summary>Variant B: a different stage curve (84 → 60 ticks, 18 → 50 %), the same retire times.</summary>
    public static LidGridDef TopB()
    {
        var d = TopA();
        d.WaveIntervals = new[] { 84, 72, 63, 60 };
        d.FlipPermille = new[] { 180, 260, 400, 500 };
        return d;
    }

    /// <summary>The lower shelf, variant A: first wave at 20 s, 66 → 60 ticks, 35 → 50 %, ring 2 retires at 75 s.</summary>
    public static LidGridDef LowA()
    {
        var d = LidGridDef.Default(0, KitBuilder.Mm(LowY), 0, LowN);
        d.FlippedTicks = 30;
        d.FirstWaveTicks = 600;
        d.WaveIntervals = new[] { 66, 60 };
        d.FlipPermille = new[] { 350, 500 };
        d.RingRetireTicks = new[] { 2250 };
        return d;
    }

    /// <summary>Variant B: the lower schedule offset (first wave at 18 s, 63 → 60 ticks, 38 → 50 %).</summary>
    public static LidGridDef LowB()
    {
        var d = LowA();
        d.FirstWaveTicks = 540;
        d.WaveIntervals = new[] { 63, 60 };
        d.FlipPermille = new[] { 380, 500 };
        return d;
    }

    [MenuItem("Fall Rush/Setup/Build Map_CrumbleShelf")]
    public static void Run()
    {
        KitBuilder.Init();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LobbyBuilder.SetupCameraAndLight(new Vector3(0f, 17f, -21f), Quaternion.Euler(36f, 0f, 0f));
        var geo = new GameObject("Geometry").transform;
        var hz = new GameObject("Hazards").transform;
        var frame = LobbyBuilder.Mat("CrumbleFrame", new Color(0.55f, 0.36f, 0.95f));

        // ---------------- the two lid grids (colliders only; the client draws them instanced). Top first: HazardSet order.
        var top = KitBuilder.LidGrid(hz, TopId, new Vector3(0f, TopY, 0f), TopN, def: TopA(), variantB: TopB(), perLidVisuals: false);
        var low = KitBuilder.LidGrid(hz, LowId, new Vector3(0f, LowY, 0f), LowN, def: LowA(), variantB: LowB(), perLidVisuals: false);

        // ---------------- the sweeper: hangs from the claw (no post: the centre lid flips too), raised until 20 s.
        var sweeper = KitBuilder.Spinner(hz, geo, SweepId, new Vector3(0f, TopY, 0f), SweepArm, SweepBar, SweepPeriod, -SweepPeriod);
        var post = geo.Find("SpinnerPost_" + SweepId);
        if (post != null)
            Object.DestroyImmediate(post.gameObject);
        KitBuilder.LocalVisual(sweeper.barVisual, "ClawCable", new Vector3(0f, 6.9f, 0f), new Vector3(0.08f, 13.4f, 0.08f), KitBuilder.Ink);
        KitBuilder.LocalVisual(sweeper.barVisual, "ClawHub", new Vector3(0f, 0.22f, 0f), new Vector3(0.5f, 0.16f, 0.5f), KitBuilder.Grape);

        // ---------------- the claw-machine frame (visual only, outside the shelves) + the claw rail high above.
        const float F = Frame;
        foreach (var (px, pz) in new[] { (-F, -F), (F, -F), (F, F), (-F, F) })
            KitBuilder.Visual(geo, $"FramePost_{px}_{pz}", new Vector3(px, 7f, pz), new Vector3(0.5f, 26f, 0.5f), frame);
        foreach (var y in new[] { 19.8f, -5.8f })
        {
            KitBuilder.Visual(geo, $"FrameN_{y}", new Vector3(0f, y, F), new Vector3(2 * F + 0.5f, 0.4f, 0.4f), frame);
            KitBuilder.Visual(geo, $"FrameS_{y}", new Vector3(0f, y, -F), new Vector3(2 * F + 0.5f, 0.4f, 0.4f), frame);
            KitBuilder.Visual(geo, $"FrameE_{y}", new Vector3(F, y, 0f), new Vector3(0.4f, 0.4f, 2 * F + 0.5f), frame);
            KitBuilder.Visual(geo, $"FrameW_{y}", new Vector3(-F, y, 0f), new Vector3(0.4f, 0.4f, 2 * F + 0.5f), frame);
        }

        KitBuilder.Visual(geo, "ClawRail", new Vector3(0f, 19.8f, 0f), new Vector3(2 * F, 0.3f, 0.6f), KitBuilder.Grape);
        KitBuilder.Visual(geo, "ClawCarriage", new Vector3(0f, 19.5f, 0f), new Vector3(1.2f, 0.5f, 1.2f), KitBuilder.Grape);
        KitBuilder.Visual(geo, "KillPlaneMarker", new Vector3(0f, KillY, 0f), new Vector3(30f, 0.02f, 30f), KitBuilder.Mint).GetComponent<MeshRenderer>().enabled = false;

        // ---------------- spawns: 12 on ring 2 of the top shelf (its 12 edge lids, not the corners), facing the centre.
        // Slot i takes edge lid (4i + i/3) mod 12 in angle order, so slots 0 / 1 / 2 stand 120° apart.
        var edge = new List<(int c, int r)>();
        for (var c = -2; c <= 2; c++)
            for (var r = -2; r <= 2; r++)
                if (Mathf.Max(Mathf.Abs(c), Mathf.Abs(r)) == 2 && !(Mathf.Abs(c) == 2 && Mathf.Abs(r) == 2))
                    edge.Add((c, r));
        edge.Sort((a, b) => Mathf.Atan2(a.c, a.r).CompareTo(Mathf.Atan2(b.c, b.r)));
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        var pitch = top.def.PitchMm * 0.001f;
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var (c, r) = edge[(4 * i + i / 3) % edge.Count];
            var pos = new Vector3(c * pitch, TopY + 0.05f, r * pitch);
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(-pos.x, 0f, -pos.z)));
            spawns[i] = t;
        }

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("CrumbleShelf", MapPoolV1.CrumbleShelf, "Final", KillY, new[] { 0, 1, 2 });
        def.timerCapSeconds = 120;
        def.targetDuration = new Vector2Int(60, 100);
        def.minPlayers = 2;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(0f, TopY, 0f);
        info.botAreaHalfExtents = new Vector2(4f, 4f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.sweepers = new[] { sweeper };
        info.sweeperStartA = SweepStartTicks;
        info.sweeperStartB = SweepStartTicks;
        info.grids = new HazardCrumbleGrid[0];
        info.lidGrids = new[] { top, low };
        LobbyBuilder.Save(scene, ScenePath, info, "Map_CrumbleShelf");
        Debug.Log($"[setup] Lid Flip G4.5: top {TopN}×{TopN} at y {TopY}, low {LowN}×{LowN} at y {LowY}, sweeper arm {SweepArm} m period {SweepPeriod} from {SweepStartTicks / 30f:F0} s");
    }
}
