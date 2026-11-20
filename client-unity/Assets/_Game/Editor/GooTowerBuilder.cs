using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds <c>Map_GooTower</c> (doc 08 §5, the Survival round): a 40 m capsule-machine tower climbed on a square spiral
/// ledge (3 laps × 4 sides, 6 m wide, +3.33 m per side) while purple glitter goo rises from the base (GOO_PLANE, delay 6 s).
/// <list type="bullet">
/// <item>Lap 1, sides 0–1 <b>stair blocks</b>: inner lane small steps, outer lane big blocks to jump (side 1: falling
/// blocks that drop 0.6 s after you land on them and come back after 4 s).</item>
/// <item>Lap 1, sides 2–3 <b>moving lifts</b>: a 3.3 m wall mid-side; outer lane a lift (variant = lift timing), inner
/// lane a steep conveyor ramp running against you.</item>
/// <item>Lap 2 <b>conveyor ramps spiralling up</b>: inner lane belts against you (ramps on the Z sides, belted treads on the
/// X sides), outer lane a faster static climb with a gap to jump (a miss drops you a lap).</item>
/// <item>Lap 3 <b>bumpers on narrow ledges</b>: an open-edged ledge with bumpers (a bonk drops you to lap 2) beside a
/// 1.5 m safe lane; on side 10 the safe lane is a shortcut behind a gate (variant A open, B closed).</item>
/// <item>The <b>summit pad</b> on the last corner (y 40): the first N to reach it escape.</item>
/// </list>
/// The ground ring at y = 0 catches falls from lap 1. Original layout (a square spiral around a capsule machine).
/// Batch: <c>-executeMethod GooTowerBuilder.Run</c>.
/// </summary>
public static class GooTowerBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_GooTower.unity";
    public const float KillY = -10f;
    public const int Sides = 12;
    public const float Height = 40f;
    public const float Rise = Height / Sides;     // 3.333 m per side
    public const float Core = 12f;                // core half-size (24 × 24 m)
    public const float Mid = 15f;                 // ledge centre line (band 12…18)
    public const float HalfLen = 12f;             // straight part of a side: s ∈ [−12, 12] (QA: 18 m sides gave 53 s rounds)

    private static Transform _geo, _hz;
    private static Material _step, _block, _ledge, _rail;
    private static readonly List<HazardConveyor> Conveyors = new List<HazardConveyor>();
    private static readonly List<HazardPlatform> Platforms = new List<HazardPlatform>();
    private static readonly List<HazardFallingBlock> Blocks = new List<HazardFallingBlock>();
    private static readonly List<HazardBumper> Bumpers = new List<HazardBumper>();
    private static readonly List<HazardGateDoor> Doors = new List<HazardGateDoor>();
    private static readonly List<BotRoute> Routes = new List<BotRoute>();

    public static float H(int k) => k * Rise;

    // Side frame: u = along the side (climb direction), v = outward (away from the core); M = middle of the side.
    private static Vector3 U(int k) => (k % 4) switch { 0 => Vector3.right, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.back };
    private static Vector3 Vo(int k) => (k % 4) switch { 0 => Vector3.back, 1 => Vector3.right, 2 => Vector3.forward, _ => Vector3.left };
    private static Vector3 M(int k) => Vo(k) * Mid;
    public static Vector3 P(int k, float s, float w, float y) => M(k) + U(k) * s + Vo(k) * w + Vector3.up * y;
    /// <summary>The corner landing at the start of side k (centre, top height H(k)).</summary>
    public static Vector3 Corner(int k) => P(k, -HalfLen - 3f, 0f, H(k));

    [MenuItem("Fall Rush/Setup/Build Map_GooTower")]
    public static void Run()
    {
        KitBuilder.Init();
        Conveyors.Clear();
        Platforms.Clear();
        Blocks.Clear();
        Bumpers.Clear();
        Doors.Clear();
        Routes.Clear();
        _step = LobbyBuilder.Mat("GooStep", new Color(0.92f, 0.94f, 0.99f));
        _block = LobbyBuilder.Mat("GooBlock", new Color(0.84f, 0.92f, 0.99f));
        _ledge = LobbyBuilder.Mat("GooLedge", new Color(0.95f, 0.93f, 1f));
        _rail = LobbyBuilder.Mat("GooRail", new Color(0.78f, 0.72f, 0.98f));
        var coreMat = LobbyBuilder.Mat("GooCore", new Color(0.62f, 0.48f, 0.98f));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LobbyBuilder.SetupCameraAndLight(new Vector3(0f, 18f, -42f), Quaternion.Euler(18f, 0f, 0f));
        _geo = new GameObject("Geometry").transform;
        _hz = new GameObject("Hazards").transform;

        // Core: the capsule machine (solid; the inner wall of every ledge), a dome on top.
        KitBuilder.Box(_geo, "TowerCore", new Vector3(0f, (Height + 4f) * 0.5f - 1f, 0f), new Vector3(Core * 2f, Height + 4f, Core * 2f), coreMat);
        KitBuilder.Visual(_geo, "TowerDome", new Vector3(0f, Height + 4f, 0f), new Vector3(Core * 1.6f, 2.5f, Core * 1.6f), LobbyBuilder.Mat("GooDome", new Color(0.8f, 0.92f, 1f)));
        for (var i = 1; i <= 3; i++)
            KitBuilder.Visual(_geo, $"TowerBand_{i}", new Vector3(0f, H(i * 4) - 0.6f, 0f), new Vector3(Core * 2f + 0.06f, 0.3f, Core * 2f + 0.06f), KitBuilder.HazardY);
        KitBuilder.Sign(_geo, "GOO TOWER", new Vector3(0f, 6f, -Core - 0.05f), 0.1f);

        // Ground ring at y = 0 (catches falls from lap 1) + the spawn deck south of the start corner.
        KitBuilder.Box(_geo, "GroundS", new Vector3(0f, -0.5f, -Mid), new Vector3(2f * Mid + 6f, 1f, 6f), _ledge);
        KitBuilder.Box(_geo, "GroundN", new Vector3(0f, -0.5f, Mid), new Vector3(2f * Mid + 6f, 1f, 6f), _ledge);
        KitBuilder.Box(_geo, "GroundE", new Vector3(Mid, -0.5f, 0f), new Vector3(6f, 1f, 2f * Mid - 6f), _ledge);
        KitBuilder.Box(_geo, "GroundW", new Vector3(-Mid, -0.5f, 0f), new Vector3(6f, 1f, 2f * Mid - 6f), _ledge);
        KitBuilder.Box(_geo, "SpawnDeck", new Vector3(-Mid - 7.5f, -0.5f, -Mid), new Vector3(9f, 1f, 9f), _ledge); // west of the start corner

        for (var k = 0; k < Sides; k++)
        {
            if (k > 0)
                CornerLanding(k);
            switch (k)
            {
                case 0:
                case 1:
                    StairBlocks(k, falling: k == 1);
                    break;
                case 2:
                case 3:
                    Lift(k);
                    break;
                case 4:
                case 5:
                case 6:
                case 7:
                    ConveyorSide(k);
                    break;
                default:
                    BumperLedge(k, shortcutDoor: k == 10);
                    break;
            }
        }

        // Summit: the corner after side 11 (y 40), with the escape trigger.
        CornerLanding(Sides);
        var top = Corner(Sides);
        KitBuilder.Visual(_geo, "SummitPad", top + new Vector3(0f, 0.01f, 0f), new Vector3(5.6f, 0.02f, 5.6f), KitBuilder.Mint);
        for (var i = 0; i < 8; i++)
            for (var j = 0; j < 8; j++)
                if (((i + j) & 1) == 0)
                    KitBuilder.Visual(_geo, $"SummitCheck_{i}_{j}", top + new Vector3(-2.45f + i * 0.7f, 0.015f, -2.45f + j * 0.7f), new Vector3(0.7f, 0.02f, 0.7f), KitBuilder.Checker);
        KitBuilder.Sign(_geo, "ESCAPE!", top + new Vector3(0f, 3.4f, 0f), 0.07f);
        var summitGo = new GameObject("SummitPadTrigger");
        summitGo.transform.position = top + new Vector3(0f, 1.5f, 0f);
        var summit = summitGo.AddComponent<RaceCheckpoint>();
        summit.index = 1;
        summit.isFinish = true;
        summit.triggerSize = new Vector3(5.6f, 3f, 5.6f);

        // Goo: rises from under the ground ring after 6 s (speed tuned in the map QA: the slowest route keeps its margin).
        var gooRoot = new GameObject("Goo").transform;
        gooRoot.SetParent(_hz, false);
        var goo = gooRoot.gameObject.AddComponent<HazardGoo>();
        goo.obstacleId = "gt_goo";
        goo.phaseMin16 = goo.phaseMax16 = 0;
        goo.def = new GooDef { Y0Mm = -1500, RiseMmPerSec = GooRiseMmPerSec, DelayTicks = 180, MaxYMm = KitBuilder.Mm(Height + 1f), CenterXMm = 0, CenterZMm = 0, HalfXMm = 22000, HalfZMm = 22000, WarnTicks = 90 };
        goo.variantB = goo.def;

        // Spawns: 12 on the spawn deck (3 rows × 4), facing the tower's start corner.
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var pos = new Vector3(-Mid - 10.5f + (i / 4) * 2.2f, 0.05f, -Mid - 3f + (i % 4) * 2f + (i / 4 == 1 ? 1f : 0f));
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.right));
            spawns[i] = t;
            KitBuilder.Visual(_geo, $"SpawnPad_{i:00}", new Vector3(pos.x, 0.008f, pos.z), new Vector3(1.2f, 0.01f, 1.2f), KitBuilder.Mint);
        }

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("GooTower", MapPoolV1.GooTower, "Survival", KillY, new[] { 0 });
        def.timerCapSeconds = 150;
        def.targetDuration = new Vector2Int(90, 120);
        def.checkpointCount = 1;
        def.minPlayers = 2;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(-Mid - 7.5f, 0f, -Mid);
        info.botAreaHalfExtents = new Vector2(3.5f, 3.5f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.conveyors = Conveyors.ToArray();
        info.platforms = Platforms.ToArray();
        info.fallingBlocks = Blocks.ToArray();
        info.bumpers = Bumpers.ToArray();
        info.doors = Doors.ToArray();
        info.goo = new[] { goo };
        info.checkpoints = new[] { summit };
        info.botRoutes = Routes.ToArray();
        LobbyBuilder.Save(scene, ScenePath, info, "Map_GooTower");
    }

    /// <summary>Goo rise speed (mm/s): tuned in the map QA (doc 08 §5 constraint), see docs/proof/m4-map-qa.md.</summary>
    public const int GooRiseMmPerSec = 300; // tuned 400 → 300 with the 24 m sides (QA: 400 left 2–3 of 8 escaping)

    // ------------------------------------------------------------------ geometry helpers (axis-aligned in the side frame)

    /// <summary>A solid box spanning s ∈ [s0, s1], w ∈ [w0, w1] of side k, from y0 to y1.</summary>
    private static GameObject SideBox(int k, string name, float s0, float s1, float w0, float w1, float y0, float y1, Material mat)
    {
        var c = P(k, (s0 + s1) * 0.5f, (w0 + w1) * 0.5f, (y0 + y1) * 0.5f);
        var su = Mathf.Abs(s1 - s0);
        var sv = Mathf.Abs(w1 - w0);
        var along = U(k);
        var size = Mathf.Abs(along.x) > 0.5f ? new Vector3(su, y1 - y0, sv) : new Vector3(sv, y1 - y0, su);
        return KitBuilder.Box(_geo, $"S{k}_{name}", c, size, mat);
    }

    private static Vector3 SideSize(int k, float su, float sv, float h)
    {
        var along = U(k);
        return Mathf.Abs(along.x) > 0.5f ? new Vector3(su, h, sv) : new Vector3(sv, h, su);
    }

    private static void CornerLanding(int k)
    {
        var c = Corner(k);
        KitBuilder.Box(_geo, $"Corner_{k}", c - new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f), _ledge);
        if (k % 4 == 0 && k < Sides)
            KitBuilder.Visual(_geo, $"LapBand_{k}", c + new Vector3(0f, 0.01f, 0f), new Vector3(5.6f, 0.02f, 0.4f), KitBuilder.Mint);
    }

    /// <summary>Small steps across lane w ∈ [w0, w1] over s ∈ [s0, s1], from height y0 to y1 (n steps).</summary>
    private static void Steps(int k, string name, float s0, float s1, float w0, float w1, float y0, float y1, int n, Material mat, float gapAt = float.NaN, float gapLen = 0f)
    {
        var tread = (s1 - s0) / n;
        for (var i = 0; i < n; i++)
        {
            var a = s0 + i * tread;
            var b = a + tread;
            if (!float.IsNaN(gapAt) && b > gapAt - gapLen * 0.5f && a < gapAt + gapLen * 0.5f)
                continue; // the gap to jump
            var top = y0 + (y1 - y0) * (i + 1) / n;
            SideBox(k, $"{name}{i:00}", a, b, w0, w1, k < 4 ? -0.5f : top - 1f, top, mat); // lap 1: solid down to the ground ring
        }
    }

    private static void Rail(int k, float s0, float s1, float y0, float y1, int n)
    {
        var len = (s1 - s0) / n;
        for (var i = 0; i < n; i++)
        {
            var top = y0 + (y1 - y0) * (i + 1) / n;
            SideBox(k, $"Rail{i:00}", s0 + i * len, s0 + (i + 1) * len, 3f, 3.25f, top - 0.2f, top + 1.0f, _rail);
        }
    }

    private static Waypoint W(int k, float s, float w, float y, WaypointAction a = WaypointAction.Run, int arg = 0, float r = 0.7f)
    {
        var p = P(k, s, w, y);
        return new Waypoint { XMm = KitBuilder.Mm(p.x), YMm = KitBuilder.Mm(p.y), ZMm = KitBuilder.Mm(p.z), RadiusMm = KitBuilder.Mm(r), Action = a, Arg = arg };
    }

    private static void Route(int k, string name, params Waypoint[] pts) => Routes.Add(new BotRoute { Section = k, Name = $"S{k}-{name}", Points = pts });

    private static Waypoint EndCorner(int k) => W(k, HalfLen + 3f, 0f, H(k + 1), WaypointAction.Run, 0, 1.0f);
    private static Waypoint StartCorner(int k) => W(k, -HalfLen - 3f, 0f, H(k), WaypointAction.Run, 0, 1.2f);

    // ------------------------------------------------------------------ sections

    private static void StairBlocks(int k, bool falling)
    {
        var y0 = H(k);
        var y1 = H(k + 1);
        // Inner lane: 16 small steps (walk up).
        Steps(k, "Step", -HalfLen, HalfLen, -3f, 0f, y0, y1, 16, _step);
        // Outer lane: four big blocks (+0.83 m each: jump up). G1: was three × 1.11 m, too close to the v2 1.2 m hop.
        const int nb = 4;
        var bl = HalfLen * 2f / nb;
        for (var j = 0; j < nb; j++)
        {
            var a = -HalfLen + j * bl;
            var top = y0 + Rise * (j + 1) / nb;
            if (!falling)
            {
                SideBox(k, $"Block{j}", a, a + bl, 0f, 3f, -0.5f, top, _block);
                continue;
            }

            var c = P(k, a + bl * 0.5f, 1.5f, top - 0.5f);
            var size = SideSize(k, bl, 3f, 1f);
            var root = new GameObject($"FallingBlock_gt_{k}_{j}").transform;
            root.SetParent(_hz, false);
            var fb = root.gameObject.AddComponent<HazardFallingBlock>();
            fb.obstacleId = $"gt_fall_{k}_{j}";
            fb.phaseMin16 = fb.phaseMax16 = 0;
            fb.def = new FallingBlockDef
            {
                CenterXMm = KitBuilder.Mm(c.x), CenterYMm = KitBuilder.Mm(c.y), CenterZMm = KitBuilder.Mm(c.z),
                HalfXMm = KitBuilder.Mm(size.x * 0.5f), HalfYMm = KitBuilder.Mm(size.y * 0.5f), HalfZMm = KitBuilder.Mm(size.z * 0.5f),
                DelayTicks = 18, GoneTicks = 120,
            };
            var col = new GameObject("MotorCollider") { layer = KitBuilder.Layer };
            col.transform.SetParent(root, false);
            col.transform.position = c;
            col.AddComponent<BoxCollider>().size = size;
            fb.motorCollider = col.GetComponent<BoxCollider>();
            var vis = KitBuilder.Visual(root, "Visual", c, size, KitBuilder.HazardY);
            KitBuilder.LocalVisual(vis.transform, "Stripe", new Vector3(0f, 0.51f, 0f), new Vector3(0.96f, 0.02f, 0.3f), KitBuilder.HazardM);
            fb.visual = vis.transform;
            Blocks.Add(fb);
        }

        KitBuilder.Label(_geo, falling ? "FALLING BLOCKS (keep moving!) | STEPS" : "BIG BLOCKS (jump) | STEPS", P(k, -HalfLen, 0f, y0 + 3f), 0.03f);
        Route(k, "steps", StartCorner(k), W(k, -HalfLen + 0.5f, -1.5f, y0), W(k, 0f, -1.5f, (y0 + y1) * 0.5f), W(k, HalfLen - 0.5f, -1.5f, y1), EndCorner(k));
        var blockRoute = new List<Waypoint> { StartCorner(k), W(k, -HalfLen - 1.0f, 1.5f, y0, WaypointAction.Jump) };
        for (var j = 0; j < nb; j++)
        {
            var top = y0 + Rise * (j + 1) / nb;
            blockRoute.Add(W(k, -HalfLen + j * bl + 2.0f, 1.5f, top, WaypointAction.Land, 0, 1.1f));
            if (j < nb - 1)
                blockRoute.Add(W(k, -HalfLen + (j + 1) * bl - 0.7f, 1.5f, top, WaypointAction.Jump));
        }

        blockRoute.Add(EndCorner(k));
        Route(k, falling ? "falling" : "blocks", blockRoute.ToArray());
    }

    private static void Lift(int k)
    {
        var y0 = H(k);
        var y1 = H(k + 1);
        // Floor: low half (s −9…0) except the lift shaft (outer lane, s −3…0); the high half (s 0…9) at y1.
        SideBox(k, "LowFloorIn", -HalfLen, -6f, -3f, 0f, -0.5f, y0, _ledge);
        SideBox(k, "LowFloorOut", -HalfLen, -4.5f, 0f, 3f, -0.5f, y0, _ledge);
        SideBox(k, "HighFloor", 0f, HalfLen, -3f, 3f, -0.5f, y1, _ledge);
        // Inner lane: a 29 degree conveyor ramp up the wall (s -6..0), belt 1.5 m/s against you.
        if (Mathf.Abs(U(k).z) > 0.5f)
        {
            var len = 6f;
            var pitch = Mathf.Atan2(Rise, len) * Mathf.Rad2Deg;
            var sp = U(k).z > 0 ? pitch : -pitch;
            var climb = (U(k) * len + Vector3.up * Rise).normalized;
            var rot = Quaternion.Euler(-sp, 0f, 0f);
            var centre = P(k, -3f, -1.5f, (y0 + y1) * 0.5f) - rot * Vector3.up * 0.25f;
            Conveyors.Add(KitBuilder.Conveyor(_hz, $"gt_lift_ramp_{k}", centre, new Vector3(3f, 0.5f, Mathf.Sqrt(len * len + Rise * Rise)), -climb * 1.5f, sp));
        }
        else
        {
            // X sides cannot pitch a belt (CONVEYOR pitches about X only): steps instead (0.21 m risers).
            Steps(k, "WallStep", -6f, 0f, -3f, 0f, y0, y1, 16, _step);
        }

        // Outer lane: the lift (3 × 3 m, top from y0 to y1 on a sine; variant = timing).
        var c = P(k, -2.25f, 1.5f, (y0 + y1) * 0.5f - 0.2f);
        var travel = Vector3.up * (Rise * 0.5f + 0.05f);
        var lift = StarScrambleBuilder.SinePlatform(_hz, $"gt_lift_{k}", c, SideSize(k, 4.4f, 2.9f, 0.4f), travel, travel, 150, 120);
        Platforms.Add(lift);
        Rail(k, 0f, HalfLen, y1, y1, 1);
        KitBuilder.Label(_geo, "LIFT (wait for it) | BELT RAMP", P(k, -HalfLen, 0f, y0 + 3f), 0.03f);
        var liftIndex = Platforms.Count - 1;
        Route(k, "lift", StartCorner(k), W(k, -5.0f, 1.5f, y0, WaypointAction.JumpToPlatform, liftIndex, 0.6f), W(k, -2.25f, 1.5f, y1, WaypointAction.Platform, liftIndex, 0.9f),
            W(k, 1.5f, 1.5f, y1, WaypointAction.Run, 0, 0.9f), W(k, HalfLen - 0.5f, 0f, y1), EndCorner(k));
        Route(k, "ramp", StartCorner(k), W(k, -6.8f, -1.5f, y0), W(k, 0.8f, -1.5f, y1, WaypointAction.Run, 0, 0.9f), W(k, HalfLen - 0.5f, -1f, y1), EndCorner(k));
    }

    private static void ConveyorSide(int k)
    {
        var y0 = H(k);
        var y1 = H(k + 1);
        var zSide = Mathf.Abs(U(k).z) > 0.5f;
        if (zSide)
        {
            // Inner: a pitched conveyor ramp, belt 2.5 m/s against you. Outer: a static ramp with a 1.6 m gap.
            var len = HalfLen * 2f;
            var pitch = Mathf.Atan2(Rise, len) * Mathf.Rad2Deg;
            var sp = U(k).z > 0 ? pitch : -pitch;
            var rot = Quaternion.Euler(-sp, 0f, 0f);
            var climb = (U(k) * len + Vector3.up * Rise).normalized;
            var slopeLen = Mathf.Sqrt(len * len + Rise * Rise);
            Conveyors.Add(KitBuilder.Conveyor(_hz, $"gt_belt_{k}", P(k, 0f, -1.5f, (y0 + y1) * 0.5f) - rot * Vector3.up * 0.25f, new Vector3(3f, 0.5f, slopeLen), -climb * 2f, sp)); // tuned 2.5 → 3 m/s (QA, v1); G1: 3 → 2 m/s (v2 runs 5 m/s)
            var part = (slopeLen - 1.6f) * 0.5f;
            for (var j = 0; j < 2; j++)
            {
                var sMid = j == 0 ? -HalfLen + part * 0.5f * len / slopeLen : HalfLen - part * 0.5f * len / slopeLen;
                var y = y0 + (sMid + HalfLen) / len * Rise;
                KitBuilder.Box(_geo, $"S{k}_Ramp{j}", P(k, sMid, 1.5f, y) - rot * Vector3.up * 0.25f, new Vector3(3f, 0.5f, part), _step, rot);
            }

            Route(k, "belt", StartCorner(k), W(k, -HalfLen + 0.5f, -1.5f, y0), W(k, HalfLen - 0.5f, -1.5f, y1), EndCorner(k));
            Route(k, "gap", StartCorner(k), W(k, -HalfLen + 0.5f, 1.5f, y0), W(k, -1.15f, 1.5f, y0 + Rise * (HalfLen - 1.15f) / len, WaypointAction.Jump, 0, 0.35f), // G1: jump close to the edge (v2 hop ≈ 3 m reach uphill)
                W(k, 2.6f, 1.5f, y0 + Rise * (HalfLen + 2.6f) / len, WaypointAction.Land, 0, 1.0f), W(k, HalfLen - 0.5f, 1.5f, y1), EndCorner(k));
        }
        else
        {
            // Inner: four belted treads (1.5 m/s against you) with 0.83 m risers. Outer: small steps with a 1.4 m gap.
            var tl = HalfLen * 0.5f;
            for (var j = 0; j < 4; j++)
            {
                var a = -HalfLen + j * tl;
                var top = y0 + Rise * (j + 1) / 4f;
                Conveyors.Add(KitBuilder.Conveyor(_hz, $"gt_tread_{k}_{j}", P(k, a + tl * 0.5f, -1.5f, top - 0.25f), SideSize(k, HalfLen * 0.5f, 3f, 0.5f), -U(k) * 1.5f)); // G1: 2.5 → 1.5 m/s against (v2 runs 5 m/s)
            }

            // G1: the gap removes every step it overlaps; centred on one 1.5 m step it removes just that one (it was centred on a
            // step boundary: two steps gone = a 3 m gap + 0.62 m rise, beyond the v2 hop).
            Steps(k, "Step", -HalfLen, HalfLen, 0f, 3f, y0, y1, 16, _step, 0.75f, 1.4f);
            var tr = new List<Waypoint> { StartCorner(k), W(k, -HalfLen - 0.9f, -1.5f, y0, WaypointAction.Jump) };
            for (var j = 0; j < 4; j++)
            {
                tr.Add(W(k, -HalfLen + j * tl + 2.2f, -1.5f, y0 + Rise * (j + 1) / 4f, WaypointAction.Land, 0, 1.2f));
                if (j < 3)
                    tr.Add(W(k, -HalfLen + (j + 1) * tl - 0.6f, -1.5f, y0 + Rise * (j + 1) / 4f, WaypointAction.Jump));
            }

            tr.Add(EndCorner(k));
            Route(k, "treads", tr.ToArray());
            Route(k, "steps", StartCorner(k), W(k, -HalfLen + 0.5f, 1.5f, y0), W(k, -0.35f, 1.5f, (y0 + y1) * 0.5f, WaypointAction.Jump, 0, 0.4f),
                W(k, 2.2f, 1.5f, (y0 + y1) * 0.5f + 0.42f, WaypointAction.Land, 0, 0.9f), W(k, HalfLen - 0.5f, 1.5f, y1), EndCorner(k));
        }

        Rail(k, -HalfLen, HalfLen, y0, y1, 8);
        KitBuilder.Label(_geo, "BELT (slow, safe) | GAP (fast)", P(k, -HalfLen, 0f, y0 + 3f), 0.03f);
    }

    private static void BumperLedge(int k, bool shortcutDoor)
    {
        var y0 = H(k);
        var y1 = H(k + 1);
        // A 4.5 m stair ledge (w −3…1.5) with a low lip on its open edge: a bonk (6 m/s + 3.5 m/s up) stuns you and costs
        // time, but cannot throw you off the tower (QA: without it, knocked players flew past the lap below into the void).
        Steps(k, "Ledge", -HalfLen, HalfLen, -3f, 1.5f, y0, y1, 16, _step);
        var len = HalfLen * 2f / 16f;
        for (var i = 0; i < 16; i++)
        {
            var top = y0 + Rise * (i + 1) / 16f;
            SideBox(k, $"Lip{i:00}", -HalfLen + i * len, -HalfLen + (i + 1) * len, 1.5f, 1.75f, top - 0.2f, top + 0.7f, _rail);
        }

        KitBuilder.Visual(_geo, $"S{k}_SafeStripe", P(k, 0f, -1.5f, (y0 + y1) * 0.5f + 0.02f), SideSize(k, HalfLen * 2f, 0.06f, 0.02f), KitBuilder.Mint);
        var bumperW = new[] { 0.4f, -0.2f, 0.5f };
        for (var j = 0; j < 3; j++)
        {
            var s = -5f + j * 5f;
            var y = y0 + Rise * Mathf.Ceil((s + HalfLen) / (HalfLen * 2f / 16f)) / 16f;
            var p = P(k, s, bumperW[j], y - 0.02f);
            Bumpers.Add(KitBuilder.Bumper(_hz, $"gt_bumper_{k}_{j}", p, 0.7f, 1.05f));
        }

        if (shortcutDoor)
        {
            // The safe lane's gate: variant A open from RUSH (the shortcut), B open only 0.5 s in every 21 s (closed).
            var c = P(k, -HalfLen + 0.4f, -2.25f, y0 + Rise / 16f + 1.3f);
            var door = KitBuilder.GateDoor(_hz, "gt_shortcut_gate", c, SideSize(k, 0.3f, 1.5f, 2.6f), 0, 10, 0, 0, 0, 0, lift: 2.8f);
            door.def.Mode = 1;
            door.variantB = door.def;
            door.variantB.Mode = 0;
            door.variantB.ClosedTicks = 600; // allowlist max; open 15 of every 639 ticks
            door.variantB.OpeningTicks = 6;
            door.variantB.OpenTicks = 15;
            door.variantB.ClosingTicks = 18;
            door.variantB.TelegraphTicks = 15;
            door.hasVariantB = true;
            Doors.Add(door);
            SideBox(k, "GatePostL", -HalfLen, -HalfLen + 0.8f, -1.5f, -1.2f, y0, y0 + 2.8f, _rail);
        }

        KitBuilder.Label(_geo, shortcutDoor ? "SAFE LANE (gate) | BUMPER LEDGE" : "SAFE LANE | BUMPER LEDGE (open edge)", P(k, -HalfLen, 0f, y0 + 3f), 0.03f);
        var safe = new List<Waypoint> { StartCorner(k) };
        if (shortcutDoor)
            safe.Add(W(k, -HalfLen - 0.6f, -2.25f, y0, WaypointAction.Door, Doors.Count - 1, 0.6f));
        safe.Add(W(k, -HalfLen + 1f, -2.25f, y0));
        safe.Add(W(k, HalfLen - 0.5f, -2.25f, y1));
        safe.Add(EndCorner(k));
        Route(k, shortcutDoor ? "gate" : "safe", safe.ToArray());
        Route(k, "bumpers", StartCorner(k), W(k, -HalfLen + 0.5f, 0.4f, y0), W(k, -5f, -1.3f, y0 + Rise * 0.25f), W(k, 0f, 0.9f, (y0 + y1) * 0.5f),
            W(k, 5f, -1.3f, y0 + Rise * 0.75f), W(k, HalfLen - 0.5f, 0f, y1), EndCorner(k));
    }
}
