using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds <c>Map_GooTower</c> (key <c>GooTower</c>, map id 1, the Survival round; G4.3, 2026-11-20, doc 08 §5): a
/// 22 m capsule-machine tower climbed on a square spiral ledge, <b>2 laps × 4 sides</b> round a solid 14 × 14 m core,
/// while purple glitter goo rises from the base (GOO_PLANE). Code-built and re-runnable. No in-world text.
/// <list type="bullet">
/// <item>Ledge 5 m wide (band 7…12 m from the centre), sides 14 m straight between 5 × 5 m corner landings, +2.75 m
/// per side (y 0 → 22). Lap 1 (k 0–3) is solid down to the ground and has a 0.4 m outer rail; lap 2 (k 4–7) is 1 m
/// slabs 11 m above lap 1 with an open outer edge (a fall lands on the same side one lap down).</item>
/// <item>The spiral runs counter-clockwise seen from above and starts on the <b>east</b> face going north (k0), so the
/// two belt sides (k2, k6) run along Z: <c>CONVEYOR</c> pitches about X only.</item>
/// <item>k0 Stair Steps: inner 12 × 0.23 m steps, outer 4 falling blocks over a trench; a mallet swinging along each
/// lane. k1 Lift Pair: a 2.75 m wall, two lifts half a period apart, a gumball crossing the approach toward the rail.
/// k2 Belt Ramp vs Spring: belt ramp against you / a spring onto a shelf, a bumper at the top of each. k3 Bumper
/// Ledge: a ramp with 3 staggered bumpers. k4 Narrow Steps: two 1.6 m columns of pitched blocks over 2.2 m gaps under a
/// mallet swinging along them. k5 Sweeper: a low bar on a 2.4 m arm. Corner 6: a spring shortcut onto k6. k6 Belt
/// Against You: a full-width belt with 2 bumpers. k7 Gate: 3 doors lifting in a travelling wave, then the summit pad
/// (corner 8, y 22): the first 5 to reach it escape.</item>
/// </list>
/// Hazard ids <c>gt_k{side}_{kind}_{n}</c>. Spawn deck south of corner 0 (behind the start, −U(0)); the ground ring at
/// y 0 catches lap-1 falls (the goo covers it from ≈ 12 s). Batch: <c>-executeMethod GooTowerBuilder.Run</c>.
/// </summary>
public static class GooTowerBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_GooTower.unity";
    public const float KillY = -10f;
    public const int Sides = 8;
    public const float Height = 22f;
    public const float Rise = Height / Sides;     // 2.75 m per side
    public const float Core = 7f;                 // core half-size (14 × 14 m)
    public const float HalfW = 2.5f;              // ledge half-width (band 7…12)
    public const float Mid = Core + HalfW;        // ledge centre line 9.5 m
    public const float HalfLen = 7f;              // straight part of a side: s ∈ [−7, 7]
    public const float CornerHalf = 2.5f;         // 5 × 5 m corner landings
    public const float RailW = 0.25f;             // lap-1 outer rail (w 2.25…2.5), 0.4 m above the walk surface
    public const float RailH = 0.4f;
    public const float Ground = 18f;              // ground ring outer half-size

    /// <summary>Goo rise (mm/s) from −1.5 m after the delay: covers y 0 at ≈ 11.8 s and the summit at ≈ 141 s.</summary>
    public const int GooRiseMmPerSec = 170; // G4.3c: 235 → 200 → 170 (normal bots: 4.3, then 6 of 8 gooed)
    public const int GooDelayTicks = 90;    // G4.3c: 6 → 4 → 3 s (keeps the reference start margin ≈ 9 s)

    // ------------------------------------------------------------------ tuning (G4.3c log in docs/proof/G4.3-goo-tower.md)
    private static readonly int[] K0MalletA = { 150, 170 }, K0MalletB = { 170, 150 }; // inner / outer lane
    private const int K1LiftA = 130, K1LiftB = 180; // G4.3c: A 150 → 130 (lift 1 was 1.30× lift 0)
    private const int K1BallA = 54, K1BallB = 66; // G4.3c: 75 / 96 → 54 / 66 (the inner lift lane was a free route)
    private const float K2BeltMs = 0.6f; // G4.3c: 1.5 → 0.7 → 0.3 → 0.6 (ramp 1.58×, 1.34× the spring; then the spring 1.29×)
    private const int K4MalletA = 240, K4MalletB = 270; // G4.3c: 160/190 → 200/230 → 240/270
    private const int K5SweepA = 80; // G4.3c: 84 → 70 → 80 (70 + a 0.4 m bar: reference bots were swept off lap 2)               // B turns the other way
    private const float K6BeltMs = 2.0f;
    private const int DoorClosed = 102, DoorOpening = 6, DoorOpen = 60, DoorClosing = 12; // cycle 180, offsets 60

    private static Transform _geo, _hz;
    private static Material _step, _block, _ledge, _rail, _coreMat;
    private static readonly List<HazardConveyor> Conveyors = new List<HazardConveyor>();
    private static readonly List<HazardPlatform> Platforms = new List<HazardPlatform>();
    private static readonly List<HazardFallingBlock> Blocks = new List<HazardFallingBlock>();
    private static readonly List<HazardBumper> Bumpers = new List<HazardBumper>();
    private static readonly List<HazardGateDoor> Doors = new List<HazardGateDoor>();
    private static readonly List<HazardMallet> Mallets = new List<HazardMallet>();
    private static readonly List<HazardSweeper> Sweepers = new List<HazardSweeper>();
    private static readonly List<HazardSpringPad> Pads = new List<HazardSpringPad>();
    private static readonly List<HazardRollingBall> Balls = new List<HazardRollingBall>();
    private static readonly List<BotRoute> Routes = new List<BotRoute>();

    public static float H(int k) => k * Rise;

    // Side frame: u = along the side (climb direction), v = outward (away from the core); M = middle of the side.
    // k0 is the east face going north (+Z), then north (−X), west (−Z), south (+X): counter-clockwise from above.
    public static Vector3 U(int k) => (k % 4) switch { 0 => Vector3.forward, 1 => Vector3.left, 2 => Vector3.back, _ => Vector3.right };
    public static Vector3 Vo(int k) => (k % 4) switch { 0 => Vector3.right, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.back };
    private static Vector3 M(int k) => Vo(k) * Mid;
    public static Vector3 P(int k, float s, float w, float y) => M(k) + U(k) * s + Vo(k) * w + Vector3.up * y;
    /// <summary>The corner landing at the start of side k (centre, top height H(k)).</summary>
    public static Vector3 Corner(int k) => P(k, -HalfLen - CornerHalf, 0f, H(k));
    public static bool Lap1(int k) => k < 4;

    [MenuItem("Fall Rush/Setup/Build Map_GooTower")]
    public static void Run()
    {
        KitBuilder.Init();
        Conveyors.Clear();
        Platforms.Clear();
        Blocks.Clear();
        Bumpers.Clear();
        Doors.Clear();
        Mallets.Clear();
        Sweepers.Clear();
        Pads.Clear();
        Balls.Clear();
        Routes.Clear();
        _step = LobbyBuilder.Mat("GooStep", new Color(0.92f, 0.94f, 0.99f));
        _block = LobbyBuilder.Mat("GooBlock", new Color(0.84f, 0.92f, 0.99f));
        _ledge = LobbyBuilder.Mat("GooLedge", new Color(0.95f, 0.93f, 1f));
        _rail = LobbyBuilder.Mat("GooRail", new Color(0.78f, 0.72f, 0.98f));
        _coreMat = LobbyBuilder.Mat("GooCore", new Color(0.62f, 0.48f, 0.98f));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        LobbyBuilder.SetupCameraAndLight(new Vector3(9.5f, 6f, -30f), Quaternion.Euler(12f, 0f, 0f));
        _geo = new GameObject("Geometry").transform;
        _hz = new GameObject("Hazards").transform;

        // Core: the capsule machine (solid; the inner wall of every ledge), y −1…23, a dome on top.
        KitBuilder.Box(_geo, "TowerCore", new Vector3(0f, (Height + 1f) * 0.5f - 0.5f, 0f), new Vector3(Core * 2f, Height + 2f, Core * 2f), _coreMat);
        KitBuilder.Box(_geo, "TowerDome", new Vector3(0f, Height + 1.6f, 0f), new Vector3(Core * 1.6f, 1.2f, Core * 1.6f), LobbyBuilder.Mat("GooDome", new Color(0.8f, 0.92f, 1f)));

        // Ground ring (y 0, band 12…18) + the trench floor under k0's falling blocks + the spawn deck south of corner 0.
        KitBuilder.Box(_geo, "GroundN", new Vector3(0f, -0.5f, (Ground + Core + 2f * HalfW) * 0.5f), new Vector3(2f * Ground, 1f, Ground - Core - 2f * HalfW), _ledge);
        KitBuilder.Box(_geo, "GroundS", new Vector3(0f, -0.5f, -(Ground + Core + 2f * HalfW) * 0.5f), new Vector3(2f * Ground, 1f, Ground - Core - 2f * HalfW), _ledge);
        KitBuilder.Box(_geo, "GroundE", new Vector3((Ground + Core + 2f * HalfW) * 0.5f, -0.5f, 0f), new Vector3(Ground - Core - 2f * HalfW, 1f, 2f * (Core + 2f * HalfW)), _ledge);
        KitBuilder.Box(_geo, "GroundW", new Vector3(-(Ground + Core + 2f * HalfW) * 0.5f, -0.5f, 0f), new Vector3(Ground - Core - 2f * HalfW, 1f, 2f * (Core + 2f * HalfW)), _ledge);
        KitBuilder.Box(_geo, "SpawnDeck", new Vector3(Mid, -0.5f, -Ground - 3.5f), new Vector3(9f, 1f, 7f), _ledge);

        for (var k = 0; k < Sides; k++)
        {
            CornerLanding(k);
            switch (k)
            {
                case 0: StairSteps(); break;
                case 1: LiftPair(); break;
                case 2: BeltRampVsSpring(); break;
                case 3: BumperLedge(); break;
                case 4: NarrowSteps(); break;
                case 5: Sweeper(); break;
                case 6: BeltAgainstYou(); break;
                default: GateToSummit(); break;
            }
        }

        // Summit: corner 8 (above corner 0, y 22), the escape trigger, a beacon on its outer corner.
        CornerLanding(Sides);
        var top = Corner(Sides);
        KitBuilder.Visual(_geo, "SummitPad", top + new Vector3(0f, 0.01f, 0f), new Vector3(4.8f, 0.02f, 4.8f), KitBuilder.Mint);
        for (var i = 0; i < 6; i++)
            for (var j = 0; j < 6; j++)
                if (((i + j) & 1) == 0)
                    KitBuilder.Visual(_geo, $"SummitCheck_{i}_{j}", top + new Vector3(-2f + i * 0.8f, 0.015f, -2f + j * 0.8f), new Vector3(0.8f, 0.02f, 0.8f), KitBuilder.Checker);
        var beacon = top + Vo(Sides) * 2.15f - U(Sides) * 2.15f;
        KitBuilder.Box(_geo, "SummitBeaconPole", beacon + Vector3.up * 4f, new Vector3(0.5f, 8f, 0.5f), KitBuilder.Mint);
        KitBuilder.Box(_geo, "SummitBeaconFlag", beacon + Vector3.up * 7f + Vo(Sides) * 1.25f, new Vector3(2.0f, 1.6f, 0.12f), KitBuilder.Checker, Quaternion.LookRotation(U(Sides)));
        var summitGo = new GameObject("SummitPadTrigger");
        summitGo.transform.position = top + new Vector3(0f, 1.5f, 0f);
        var summit = summitGo.AddComponent<RaceCheckpoint>();
        summit.index = 1;
        summit.isFinish = true;
        summit.triggerSize = new Vector3(4.6f, 3f, 4.6f);

        // Goo: rises from under the ground ring after 6 s.
        var gooRoot = new GameObject("Goo").transform;
        gooRoot.SetParent(_hz, false);
        var goo = gooRoot.gameObject.AddComponent<HazardGoo>();
        goo.obstacleId = "gt_goo";
        goo.phaseMin16 = goo.phaseMax16 = 0;
        goo.def = new GooDef { Y0Mm = -1500, RiseMmPerSec = GooRiseMmPerSec, DelayTicks = GooDelayTicks, MaxYMm = KitBuilder.Mm(Height + 1f), CenterXMm = 0, CenterZMm = 0, HalfXMm = 22000, HalfZMm = 22000, WarnTicks = 90 };
        goo.variantB = goo.def;

        // Spawns: 12 on the spawn deck (3 rows × 4), facing north toward corner 0.
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var row = i / 4;
            var col = i % 4;
            var pos = new Vector3(Mid - 3f + col * 2f + (row == 1 ? 0.8f : 0f), 0.05f, -Ground - 1.2f - row * 2.1f);
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.forward));
            spawns[i] = t;
            KitBuilder.Visual(_geo, $"SpawnPad_{i:00}", new Vector3(pos.x, 0.008f, pos.z), new Vector3(1.2f, 0.01f, 1.2f), KitBuilder.Mint);
        }

        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("GooTower", MapPoolV1.GooTower, "Survival", KillY, new[] { 0 });
        def.timerCapSeconds = 150;
        def.targetDuration = new Vector2Int(90, 110);
        def.checkpointCount = 1;
        def.minPlayers = 2;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(Mid, 0f, -Ground - 3.5f);
        info.botAreaHalfExtents = new Vector2(3.5f, 2.5f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.conveyors = Conveyors.ToArray();
        info.platforms = Platforms.ToArray();
        info.fallingBlocks = Blocks.ToArray();
        info.bumpers = Bumpers.ToArray();
        info.doors = Doors.ToArray();
        info.mallets = Mallets.ToArray();
        info.sweepers = Sweepers.ToArray();
        info.pads = Pads.ToArray();
        info.rollingBalls = Balls.ToArray();
        info.turnTables = new HazardTurnTable[0];
        info.goo = new[] { goo };
        info.checkpoints = new[] { summit };
        info.botRoutes = Routes.ToArray();
        LobbyBuilder.Save(scene, ScenePath, info, "Map_GooTower");
        var problems = new List<string>();
        for (byte v = 0; v < 2; v++)
            foreach (var m in def.allowedModifiers)
                problems.AddRange(info.Validate(new RoundParams { MapId = MapPoolV1.GooTower, Variant = v, Modifier = (byte)m }));
        Debug.Log($"[setup] Goo Tower G4.3: {Sides} sides, core {Core * 2f} m, summit y {Height}; hazards: {Mallets.Count} mallet, {Platforms.Count} lift, {Balls.Count} ball lane, {Conveyors.Count} belt, " +
                  $"{Pads.Count} spring, {Bumpers.Count} bumper, {Blocks.Count} falling block, {Sweepers.Count} sweeper, {Doors.Count} door; {Routes.Count} routes; allowlist problems: {problems.Count} {string.Join("; ", problems)}");
    }

    // ------------------------------------------------------------------ geometry helpers (in the side frame)

    /// <summary>A solid box spanning s ∈ [s0, s1], w ∈ [w0, w1] of side k, from y0 to y1.</summary>
    private static GameObject SideBox(int k, string name, float s0, float s1, float w0, float w1, float y0, float y1, Material mat)
    {
        var c = P(k, (s0 + s1) * 0.5f, (w0 + w1) * 0.5f, (y0 + y1) * 0.5f);
        return KitBuilder.Box(_geo, $"S{k}_{name}", c, SideSize(k, Mathf.Abs(s1 - s0), Mathf.Abs(w1 - w0), y1 - y0), mat);
    }

    private static GameObject Box3(int k, string name, float s0, float s1, float w0, float w1, float y0, float y1, Material mat) => SideBox(k, name, s0, s1, w0, w1, y0, y1, mat);

    private static Vector3 SideSize(int k, float su, float sv, float h) => Mathf.Abs(U(k).x) > 0.5f ? new Vector3(su, h, sv) : new Vector3(sv, h, su);

    /// <summary>Walk slab: lap 1 solid down to the ground, lap 2 a 1 m slab.</summary>
    private static GameObject Floor(int k, string name, float s0, float s1, float w0, float w1, float top, Material mat = null) =>
        SideBox(k, name, s0, s1, w0, w1, Lap1(k) ? -1f : top - 1f, top, mat ?? _ledge);

    /// <summary>Rotation of a box whose local z climbs from (s0, ya) to (s1, yb) on side k (local x across the side).</summary>
    private static Quaternion SlopeRot(int k, float s0, float ya, float s1, float yb, out Vector3 normal)
    {
        var d = (U(k) * (s1 - s0) + Vector3.up * (yb - ya)).normalized;
        normal = Vector3.Cross(d, Vo(k)).normalized;
        return Quaternion.LookRotation(d, normal);
    }

    /// <summary>A pitched slab whose top runs from (s0, ya) to (s1, yb) across w0…w1, <paramref name="thick"/> deep.</summary>
    private static GameObject Slope(int k, string name, float s0, float ya, float s1, float yb, float w0, float w1, float thick, Material mat)
    {
        var rot = SlopeRot(k, s0, ya, s1, yb, out var n);
        var len = Mathf.Sqrt((s1 - s0) * (s1 - s0) + (yb - ya) * (yb - ya));
        var topMid = P(k, (s0 + s1) * 0.5f, (w0 + w1) * 0.5f, (ya + yb) * 0.5f);
        return KitBuilder.Box(_geo, $"S{k}_{name}", topMid - n * (thick * 0.5f), new Vector3(Mathf.Abs(w1 - w0), thick, len), mat, rot);
    }

    /// <summary>
    /// Lap-1 fill under a ramp: stepped solid boxes up to just under the ramp (no hollow wedge seen from outside). Lap 2:
    /// a 1 m pitched slab under the walk surface.
    /// </summary>
    private static void RampBody(int k, string name, float s0, float ya, float s1, float yb, float w0, float w1, float surfaceThick)
    {
        if (!Lap1(k))
        {
            Slope(k, $"{name}Body", s0, ya - surfaceThick, s1, yb - surfaceThick, w0, w1, 1f, _ledge);
            return;
        }

        const int n = 8;
        for (var i = 0; i < n; i++)
        {
            var a = s0 + (s1 - s0) * i / n;
            var b = s0 + (s1 - s0) * (i + 1) / n;
            var yLow = ya + (yb - ya) * i / n - surfaceThick - 0.02f; // under the ramp's lower edge of this strip
            SideBox(k, $"{name}Fill{i}", a, b, w0, w1, -1f, yLow, _ledge);
        }
    }

    private static void CornerLanding(int k)
    {
        var c = Corner(k);
        var lap1 = k > 0 && k < 4;
        var y0 = k == 0 ? -1f : lap1 ? -1f : c.y - 1f;
        KitBuilder.Box(_geo, $"Corner_{k}", new Vector3(c.x, (y0 + c.y) * 0.5f, c.z), new Vector3(2f * CornerHalf, c.y - y0, 2f * CornerHalf), _ledge);
        if (lap1)
        {
            // Outer rail on the corner's two outer edges (0.4 m): side k−1's outer edge continues as side k's back edge.
            var outA = Vo(k);
            var outB = -U(k);
            foreach (var (o, along, name) in new[] { (outA, U(k), "A"), (outB, Vo(k), "B") })
            {
                var centre = c + o * (CornerHalf - RailW * 0.5f) + Vector3.up * (RailH * 0.5f);
                var size = Mathf.Abs(along.x) > 0.5f ? new Vector3(2f * CornerHalf, RailH, RailW) : new Vector3(RailW, RailH, 2f * CornerHalf);
                KitBuilder.Box(_geo, $"CornerRail_{k}{name}", centre, size, _rail);
            }
        }

        if (k == 4)
            KitBuilder.Visual(_geo, "LapBand_4", c + new Vector3(0f, 0.01f, 0f), SideSize(4, 0.4f, 4.6f, 0.02f), KitBuilder.Mint);
    }

    /// <summary>Lap-1 outer rail beside a walk surface rising from ya to yb over s0…s1 (pitched, 0.4 m above it).</summary>
    private static void Rail(int k, string name, float s0, float ya, float s1, float yb)
    {
        if (Mathf.Abs(yb - ya) < 0.01f)
        {
            SideBox(k, $"Rail{name}", s0, s1, HalfW - RailW, HalfW, ya - 0.3f, ya + RailH, _rail);
            return;
        }

        Slope(k, $"Rail{name}", s0, ya + RailH, s1, yb + RailH, HalfW - RailW, HalfW, RailH + 0.3f, _rail);
    }

    // ------------------------------------------------------------------ hazards

    private static HazardMallet AlongMallet(int k, string id, float s, float w, float surfaceY, float halfWidth, int periodA, int periodB, float arm = 3f, float amp = 50f, bool across = false)
    {
        // Swing along the side (u): the head pushes you back down the path (or on through a gap), never sideways off the
        // tower (a 9 m/s push across lap 2 carries ≈ 10 m: past the lap-1 ledge into the goo).
        var pivot = P(k, s, w, surfaceY + 0.55f + arm);
        var alongZ = Mathf.Abs(U(k).z) > 0.5f;
        var axis = alongZ != across ? 2 : 0;
        var ml = KitBuilder.Mallet(_hz, _geo, id, pivot, axis, arm, amp, periodA, periodB);
        ml.def.HeadHalfWidthMm = KitBuilder.Mm(halfWidth);
        ml.variantB.HeadHalfWidthMm = ml.def.HeadHalfWidthMm;
        var head = ml.visual.Find("Head");
        if (head != null)
            head.localScale = axis == 2 ? new Vector3(halfWidth / 0.75f, 1f, 1f) : new Vector3(1f, 1f, halfWidth / 0.75f);
        // The arm out of the core wall sits above the axle, in the swing plane only when swinging along the side.
        foreach (var n in new[] { $"MalletPostA_{id}", $"MalletPostB_{id}", $"MalletBeam_{id}" })
        {
            var t = _geo.Find(n);
            if (t != null)
                Object.DestroyImmediate(t.gameObject);
        }

        if (Lap1(k))
        {
            // G4.3c: lap 1 mallets knock soft (6 m/s, 3.5 up): the 0.4 m rail then bounces you; the hard knock (9 + 5 up)
            // cleared it and dropped bots onto the ground ring in the goo.
            foreach (var v in new[] { 0, 1 })
            {
                var d = v == 0 ? ml.def : ml.variantB;
                d.KnockbackCms = KnockProfile.SoftPushCms;
                d.KnockUpCms = KnockProfile.SoftUpCms;
                d.StunTicks = KnockProfile.SoftStunTicks;
                d.ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks);
                if (v == 0) ml.def = d; else ml.variantB = d;
            }
        }

        // A beam out of the core wall carries the axle (no posts on the ledge).
        var beamEnd = across ? w + 0.3f : w + halfWidth * 0.5f + 0.3f;
        SideBox(k, $"MalletArm_{id}", s - 0.25f, s + 0.25f, -HalfW - 0.3f, Mathf.Min(HalfW, beamEnd), pivot.y + 0.05f, pivot.y + 0.55f, KitBuilder.Grape);
        Mallets.Add(ml);
        return ml;
    }

    private static HazardBumper Bumper(int k, string id, float s, float w, float surfaceY, float r = 0.9f, float h = 1.2f)
    {
        var b = KitBuilder.Bumper(_hz, id, P(k, s, w, surfaceY - 0.3f), r, h + 0.3f);
        Bumpers.Add(b);
        return b;
    }

    // ------------------------------------------------------------------ bot routes

    private static Waypoint W(int k, float s, float w, float y, WaypointAction a = WaypointAction.Run, int arg = 0, float r = 0.7f)
    {
        var p = P(k, s, w, y);
        return new Waypoint { XMm = KitBuilder.Mm(p.x), YMm = KitBuilder.Mm(p.y), ZMm = KitBuilder.Mm(p.z), RadiusMm = KitBuilder.Mm(r), Action = a, Arg = arg };
    }

    private static void Route(int k, string name, params Waypoint[] pts) => Routes.Add(new BotRoute { Section = k, Name = $"k{k}-{name}", Points = pts });

    private static Waypoint StartCorner(int k, float w = 0f) => W(k, -HalfLen - CornerHalf, w, H(k), WaypointAction.Run, 0, 1.2f);
    private static Waypoint EndCorner(int k, float w = 0f) => W(k, HalfLen + 1.2f, w, H(k + 1), WaypointAction.Run, 0, 1.0f);

    // ------------------------------------------------------------------ sides

    /// <summary>k0 (lap 1, y 0 → 2.75): inner 12 walkable steps, outer 4 falling blocks over a trench; a mallet along each lane.</summary>
    private static void StairSteps()
    {
        const int k = 0;
        var y0 = H(k);
        var y1 = H(k + 1);
        const int n = 12;
        var tread = 2f * HalfLen / n;
        for (var i = 0; i < n; i++)
            SideBox(k, $"Step{i:00}", -HalfLen + i * tread, -HalfLen + (i + 1) * tread, -HalfW, 0f, -1f, y0 + Rise * (i + 1) / n, _step);
        float StepTop(float s) => y0 + Rise * Mathf.Clamp(Mathf.Floor((s + HalfLen) / tread) + 1f, 1f, n) / n;

        // Outer lane: 4 falling blocks (2.4 m, 1.1 m gaps before each, +0.69 m each), drop 0.8 s after landing, back after
        // 4 s. Under them the trench floor (y 0) between the steps and the rail.
        // G4.3c: the trench floor sits 0.7 m under each block (gap + block; a hop gets you back on). At y 0 the goo (y 0 at
        // ≈ 12 s) took every bot that a crowd dropped in (QA: most knock-outs were here at 11.9 s).
        for (var j = 0; j < 4; j++)
            SideBox(k, $"Trench{j}", -HalfLen + j * 3.5f, -HalfLen + (j + 1) * 3.5f, 0f, HalfW - RailW, -1f, Mathf.Max(0f, y0 + Rise * (j + 1) / 4f - 0.7f), _ledge);
        const int nb = 4;
        const float bl = 2.4f, gap = 1.1f;
        var blockS = new float[nb];
        for (var j = 0; j < nb; j++)
        {
            var a = -HalfLen + gap + j * (bl + gap);
            blockS[j] = a;
            var topY = y0 + Rise * (j + 1) / nb;
            var c = P(k, a + bl * 0.5f, (HalfW - RailW) * 0.5f, topY - 0.3f);
            var size = SideSize(k, bl, HalfW - RailW, 0.6f); // 0.6 m: no capsule fits between a block and the trench floor
            if (j == 1)
            {
                // G4.3c: block 1 stays (a rest step before the mallet): bots waiting on the corner at y 0 for the mallet
                // were caught by the goo at 12 s in a crowd. Static, solid down to the trench floor.
                SideBox(k, "RestBlock1", a, a + bl, 0f, HalfW - RailW, Mathf.Max(0f, topY - 0.7f), topY, _block);
                Rail(k, $"B{j}", a - gap, topY - Rise / nb, a + bl, topY);
                continue;
            }

            var root = new GameObject($"FallingBlock_gt_k0_fall_{j}").transform;
            root.SetParent(_hz, false);
            var fb = root.gameObject.AddComponent<HazardFallingBlock>();
            fb.obstacleId = $"gt_k0_fall_{j}";
            fb.phaseMin16 = fb.phaseMax16 = 0;
            fb.def = new FallingBlockDef
            {
                CenterXMm = KitBuilder.Mm(c.x), CenterYMm = KitBuilder.Mm(c.y), CenterZMm = KitBuilder.Mm(c.z),
                HalfXMm = KitBuilder.Mm(size.x * 0.5f), HalfYMm = KitBuilder.Mm(size.y * 0.5f), HalfZMm = KitBuilder.Mm(size.z * 0.5f),
                DelayTicks = 24, GoneTicks = 120, // G4.3c: 0.6 → 0.8 s (normal bots fell 1.0× per traversal)
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
            Rail(k, $"B{j}", a - gap, topY - Rise / nb, a + bl, topY);
        }

        // Mallets swinging along the side: inner lane at s −2.5, outer lane at s +2.5.
        // G4.3c: the inner mallet moved −2.5 → +2.5 (beside the outer one): its queue then forms on the steps above the
        // early goo, not on corner 0.
        AlongMallet(k, "gt_k0_mallet_0", 2.5f, -HalfW * 0.5f, StepTop(2.5f), 1.2f, K0MalletA[0], K0MalletB[0]);
        // G4.3c: the outer one swings across its lane over block 2 (along a lane of jumps it left no window: bots timed out
        // on the rest block and were hit).
        AlongMallet(k, "gt_k0_mallet_1", 2.3f, (HalfW - RailW) * 0.5f, y0 + Rise * 3 / nb, 1.0f, K0MalletA[1], K0MalletB[1], 3f, 40f, across: true);

        // Routes. Steps: wait before the inner mallet until the run past it is clear.
        Route(k, "steps", StartCorner(k, -1.25f), W(k, -6.3f, -1.25f, StepTop(-6.3f)), W(k, -0.4f, -1.25f, StepTop(-0.4f), WaypointAction.WaitClear, 1, 0.5f),
            W(k, 5.6f, -1.25f, StepTop(5.6f), WaypointAction.Run, 0, 0.8f), W(k, 6.4f, -1.25f, y1), EndCorner(k, -1.0f));
        // Falling blocks: no waiting on a block (it drops 0.6 s after you land): wait on the corner until the whole run
        // past the outer mallet is clear, then hop block to block.
        var w0 = (HalfW - RailW) * 0.5f;
        var fr = new List<Waypoint> { StartCorner(k, w0), W(k, -HalfLen - 1.6f, w0, y0, WaypointAction.Run, 0, 0.4f), W(k, -HalfLen - 0.25f, w0, y0, WaypointAction.Jump, 0, 0.3f) };
        for (var j = 0; j < nb; j++)
        {
            var topY = y0 + Rise * (j + 1) / nb;
            fr.Add(W(k, blockS[j] + 1.0f, w0, topY, WaypointAction.Land, 0, 0.8f));
            if (j == 1)
                fr.Add(W(k, blockS[j] + 0.5f, w0, topY, WaypointAction.WaitClear, 3, 0.4f)); // on the rest block: until block 2 is clear of the mallet
            if (j < nb - 1)
                fr.Add(W(k, blockS[j] + bl - 0.3f, w0, topY, WaypointAction.Jump, 0, 0.3f));
        }

        fr.Add(EndCorner(k, 1.0f));
        Route(k, "falling", fr.ToArray());
    }

    /// <summary>k1 (lap 1, y 2.75 → 5.5): a 2.75 m wall at s +2, two lifts half a period apart, a gumball crossing at s −3.</summary>
    private static void LiftPair()
    {
        const int k = 1;
        var y0 = H(k);
        var y1 = H(k + 1);
        const float wallS = 2f, liftS0 = -1f, liftT = 0.24f;
        Floor(k, "LowFloor", -HalfLen, liftS0, -HalfW, HalfW, y0);
        Floor(k, "LiftBay", liftS0, wallS, -HalfW, HalfW, y0);
        Floor(k, "HighFloor", wallS, HalfLen, -HalfW, HalfW, y1);
        Rail(k, "Low", -HalfLen, y0, wallS, y0);
        Rail(k, "High", wallS, y1, HalfLen, y1);

        // Lifts: one per lane, 2.4 × 3 m, resting on the bay floor (top +0.24 m) and rising flush with the high floor.
        var lanes = new[] { (-HalfW + 0.05f, -0.05f), (0.05f, HalfW - RailW - 0.05f) };
        var bottomTop = y0 + liftT;
        var travel = y1 - bottomTop;
        for (var i = 0; i < 2; i++)
        {
            var (a, b) = lanes[i];
            var c = P(k, (liftS0 + wallS - 0.05f) * 0.5f, (a + b) * 0.5f, (bottomTop + y1) * 0.5f - liftT * 0.5f);
            var pl = KitBuilder.LoopPlatform(_hz, $"gt_k1_lift_{i}", c, SideSize(k, wallS - 0.05f - liftS0, b - a, liftT),
                new[] { new Vector3(0f, -travel * 0.5f, 0f), new Vector3(0f, travel * 0.5f, 0f) }, null, K1LiftA);
            pl.variantB.PeriodTicks = K1LiftB;
            pl.hasVariantB = true;
            pl.phaseMin16 = pl.phaseMax16 = i * 32768; // half a period apart: one is always near the bottom
            Platforms.Add(pl);
        }

        // The gumball crossing the approach, out of a chute in the core wall toward the rail (soft knock: the 0.4 m rail
        // bounces you, a hard knock would clear it).
        const float r = 0.8f;
        const float ballS = -2.3f; // G4.3c: −3 → −2.3: the ball now reaches bots queueing at the lift edge (the inner lane had no contact)
        // G4.3c: rolls inward, out of a dispenser on the rail (rolling outward, a knock cleared the 0.4 m rail and dropped
        // bots onto the ground ring in the goo).
        SideBox(k, "BallDispenser", ballS - 1.0f, ballS + 1.0f, HalfW - 0.4f, HalfW, y0, y0 + 2.0f, KitBuilder.Grape);
        var start = P(k, ballS, HalfW - 0.4f - r - 0.05f, y0 + r + 0.01f);
        var end = P(k, ballS, -HalfW + r + 0.1f, y0 + r + 0.01f);
        var ball = KitBuilder.RollingBall(_hz, _geo, "gt_k1_ball_0", start, end, r, 4500, K1BallA, K1BallB);
        ball.def.KnockbackCms = KnockProfile.SoftPushCms;
        ball.def.KnockUpCms = KnockProfile.SoftUpCms;
        ball.def.StunTicks = KnockProfile.SoftStunTicks;
        ball.def.ImmunityTicks = KnockProfile.ImmunityFor(KnockProfile.SoftStunTicks);
        var vb = ball.def;
        vb.PeriodTicks = K1BallB;
        ball.variantB = vb;
        if (ball.chute != null)
        {
            var t = ball.chute.transform;
            t.SetPositionAndRotation(P(k, ballS, HalfW - 0.42f, y0 + 1.0f), Quaternion.LookRotation(-Vo(k)));
            t.localScale = new Vector3(1.8f, 1.8f, 0.04f);
        }

        Balls.Add(ball);

        // Routes: one per lift. Wait before the ball line until the run to the lift edge is clear, board the lift, ride it
        // (pushing toward the wall), step off at the top.
        for (var i = 0; i < 2; i++)
        {
            var w = i == 0 ? -1.25f : 1.05f;
            Route(k, $"lift{i}", StartCorner(k, w * 0.5f), W(k, -5.6f, w, y0, WaypointAction.Run, 0, 0.7f), W(k, -4.6f, w, y0, WaypointAction.WaitClear, 1, 0.5f),
                W(k, liftS0 - 0.35f, w, y0, WaypointAction.JumpToPlatform, i, 0.4f), W(k, 0.5f, w, y1, WaypointAction.Platform, i, 0.9f),
                W(k, wallS + 1.2f, w, y1, WaypointAction.Run, 0, 0.9f), EndCorner(k, w * 0.5f));
        }
    }

    /// <summary>k2 (lap 1, y 5.5 → 8.25): inner a belt ramp against you, outer a spring onto a top shelf; a bumper at each top.</summary>
    private static void BeltRampVsSpring()
    {
        const int k = 2;
        var y0 = H(k);
        var y1 = H(k + 1);
        const float shelfS = 3f, padS = -3f;
        // Inner lane: the belt ramp (U(2) = −Z, so it pitches about X).
        var len = 2f * HalfLen;
        var pitch = Mathf.Atan2(Rise, len) * Mathf.Rad2Deg;
        var sp = U(k).z > 0 ? pitch : -pitch;
        var rot = Quaternion.Euler(-sp, 0f, 0f);
        var climb = (U(k) * len + Vector3.up * Rise).normalized;
        var slopeLen = Mathf.Sqrt(len * len + Rise * Rise);
        Conveyors.Add(KitBuilder.Conveyor(_hz, "gt_k2_belt_0", P(k, 0f, -HalfW * 0.5f, (y0 + y1) * 0.5f) - rot * Vector3.up * 0.25f, new Vector3(HalfW, 0.5f, slopeLen), -climb * K2BeltMs, sp));
        RampBody(k, "Ramp", -HalfLen, y0, HalfLen, y1, -HalfW, 0f, 0.5f);
        // Outer lane: low floor up to the shelf, the shelf (y1) from s +3, the spring at s −3.
        Floor(k, "LowOuter", -HalfLen, shelfS, 0f, HalfW, y0);
        // G4.3c: the shelf sits 0.55 m under corner 3 with a short ramp up (at corner height a landing already counted as
        // k3, so the shelf bumper was never part of the spring lane).
        const float shelfY = 7.7f, shelfRampS = 6.0f;
        Floor(k, "Shelf", shelfS, shelfRampS, 0f, HalfW, shelfY);
        Slope(k, "ShelfRamp", shelfRampS, shelfY, HalfLen, y1, 0f, HalfW - RailW, 0.5f, _ledge);
        RampBody(k, "ShelfRamp", shelfRampS, shelfY, HalfLen, y1, 0f, HalfW, 0.5f);
        Rail(k, "Low", -HalfLen, y0, shelfS, y0);
        Rail(k, "Shelf", shelfS, shelfY, shelfRampS, shelfY);
        Rail(k, "ShelfRamp", shelfRampS, shelfY, HalfLen, y1);
        // Launch (u 8.2 m/s, up 12 m/s): apex ≈ 3.6 m at 0.6 s, clears the shelf edge (6 m on) by ≈ 0.6 m, lands ≈ s +4.
        var padTop = P(k, padS, 1.15f, y0 + 0.1f);
        var launch = U(k) * 9.5f + Vector3.up * 12.5f;
        Pads.Add(KitBuilder.SpringPad(_hz, "gt_k2_spring_0", padTop, k % 2 == 0 ? new Vector2(1.9f, 2f) : new Vector2(2f, 1.9f), launch));
        // Each leaves one passable line (≈ 1 m) beside it: a bumper is solid, so a fully covered lane is a wall.
        Bumper(k, "gt_k2_bumper_0", 5.4f, -1.75f, y0 + Rise * (5.4f + HalfLen) / len, 0.65f);
        Bumper(k, "gt_k2_bumper_1", 5.2f, 1.6f, shelfY, 0.65f); // past the landing, passed on its inner side (a bonk pushes you onto the ramp); on the landing it knocked every lander off // G4.3c: at 5.0 the spring landed bots into it and knocked them off the shelf, every time

        float RampY(float s) => y0 + Rise * (s + HalfLen) / len;
        Route(k, "ramp", StartCorner(k, -1.25f), W(k, -6.4f, -1.25f, RampY(-6.4f)), W(k, 3.6f, -1.0f, RampY(3.6f)), W(k, 5.4f, -0.74f, RampY(5.4f), WaypointAction.Run, 0, 0.4f), // brushing the bumper
            W(k, 6.6f, -1.0f, y1), EndCorner(k, -1.0f));
        Route(k, "spring", StartCorner(k, 1.15f), W(k, -5.4f, 1.15f, y0, WaypointAction.Run, 0, 0.6f),
            // The last point before the pad is short of it: a bot launched before reaching its point steers back at it in
            // the air and air control brakes the launch (QA trace: hit the shelf face).
            W(k, padS - 1.4f, 1.15f, y0, WaypointAction.Run, 0, 0.5f), W(k, 4.2f, 1.15f, shelfY, WaypointAction.Land, 0, 1.6f),
            W(k, 5.2f, 0.59f, shelfY, WaypointAction.Run, 0, 0.4f), W(k, 7.6f, 0.8f, y1), EndCorner(k, 1.0f)); // brushing the bumper
    }

    /// <summary>k3 (lap 1, y 8.25 → 11): a gentle full-width ramp with 3 bumpers staggered so no straight lane avoids them.</summary>
    private static void BumperLedge()
    {
        const int k = 3;
        var y0 = H(k);
        var y1 = H(k + 1);
        Slope(k, "Ramp", -HalfLen, y0, HalfLen, y1, -HalfW, HalfW - RailW, 0.5f, _ledge);
        RampBody(k, "Ramp", -HalfLen, y0, HalfLen, y1, -HalfW, HalfW, 0.5f);
        Rail(k, "Ramp", -HalfLen, y0, HalfLen, y1);
        float RampY(float s) => y0 + Rise * (s + HalfLen) / (2f * HalfLen);
        // Reach r + capsule 0.31 + margin 0.04: [−2.85, −0.35] ∪ [−1.25, 1.25] ∪ [0.1, 2.6] covers the whole ledge.
        var bs = new[] { (-4f, -1.6f), (0f, 0f), (4f, 1.35f) };
        for (var i = 0; i < bs.Length; i++)
            Bumper(k, $"gt_k3_bumper_{i}", bs[i].Item1, bs[i].Item2, RampY(bs[i].Item1), 0.9f);

        // Left (inner) / right (outer) lines, each threading past the bumpers the way a player would.
        Route(k, "inner", StartCorner(k, -0.6f), W(k, -6.2f, -0.2f, RampY(-6.2f)), W(k, -4f, 0.25f, RampY(-4f), WaypointAction.Run, 0, 0.6f),
            W(k, -1.2f, -1.9f, RampY(-1.2f), WaypointAction.Run, 0, 0.6f), W(k, 2.2f, -1.9f, RampY(2.2f)), W(k, 6.4f, -1.0f, y1), EndCorner(k, -1.0f));
        Route(k, "outer", StartCorner(k, 0.8f), W(k, -6.2f, 1.2f, RampY(-6.2f)), W(k, -2.2f, 1.8f, RampY(-2.2f), WaypointAction.Run, 0, 0.6f),
            W(k, 1.4f, 1.6f, RampY(1.4f), WaypointAction.Run, 0, 0.6f), W(k, 4f, -0.25f, RampY(4f), WaypointAction.Run, 0, 0.6f), W(k, 6.4f, 0.4f, y1), EndCorner(k, 0.5f));
    }

    /// <summary>k4 (lap 2, y 11 → 13.75): two 1.6 m columns of pitched blocks over 2.2 m gaps, a wide mallet along them at s 0.</summary>
    private static void NarrowSteps()
    {
        const int k = 4;
        var y0 = H(k);
        const float gap = 2.0f, jumpRise = 0.2f; // G4.3c rise 0.35 → 0.2: // G4.3c: 2.2 → 2.0 (1.73 m blocks left 0.7 m of run-up: reference bots landed short)
        const int nSeg = 3;
        var segLen = (2f * HalfLen - (nSeg + 1) * gap) / nSeg; // 1.73 m
        var segRise = (Rise - (nSeg + 1) * jumpRise) / nSeg;   // 0.45 m inside each block
        var cols = new[] { -1.25f, 1.25f };
        var segS0 = new float[nSeg];
        var segY0 = new float[nSeg];
        for (var j = 0; j < nSeg; j++)
        {
            segS0[j] = -HalfLen + gap + j * (segLen + gap);
            segY0[j] = y0 + (j + 1) * jumpRise + j * segRise;
        }

        foreach (var cw in cols)
            for (var j = 0; j < nSeg; j++)
                Slope(k, $"Col{(cw < 0 ? "In" : "Out")}{j}", segS0[j], segY0[j], segS0[j] + segLen, segY0[j] + segRise, cw - 0.9f, cw + 0.9f, 1f, _block); // G4.3c: 1.6 → 1.8 m

        // One mallet over the middle blocks at s 0, swinging across both columns. A hit throws you off the column: you drop
        // onto k0, one lap down (from ≈ 25 s the goo is already there).
        // G4.3c: swinging across (the plan): along the columns the head stayed in a jumping capsule's way over its whole
        // sweep (the look-ahead never cleared, bots timed out and were hit). Across, each column is clear half the period.
        AlongMallet(k, "gt_k4_mallet_0", 0f, 0f, segY0[1] + segRise * 0.5f, 0.95f, K4MalletA, K4MalletB, 3.2f, 40f, across: true); // G4.3c: 50 → 40°

        foreach (var cw in cols)
        {
            var name = cw < 0 ? "inner" : "outer";
                        // Line up at the back of the corner first: arriving from k3 the bot moves sideways and a jump falls short.
            var p = new List<Waypoint> { StartCorner(k, cw), W(k, -HalfLen - 2f * CornerHalf + 0.9f, cw, y0, WaypointAction.Run, 0, 0.4f), W(k, -HalfLen - 0.25f, cw, y0, WaypointAction.Jump, 0, 0.3f) };
            for (var j = 0; j < nSeg; j++)
            {
                p.Add(W(k, segS0[j] + 0.9f, cw, segY0[j] + segRise * 0.45f, WaypointAction.Land, 0, 1.0f));
                if (j == 0)
                    // The mallet: wait at the block's back edge until the next two blocks are clear. 1.5 m from the take-off, so the
                    // bot runs straight off (a run-up from mid-block started late and met the head, QA trace).
                    p.Add(W(k, segS0[j] + 0.25f, cw, segY0[j] + segRise * 0.1f, WaypointAction.WaitClear, 4, 0.4f));
                p.Add(W(k, segS0[j] + segLen - 0.25f, cw, segY0[j] + segRise, WaypointAction.Jump, 0, 0.3f));
            }

            p.Add(W(k, HalfLen + 1.2f, cw, H(k + 1), WaypointAction.Land, 0, 1.2f));
            p.Add(EndCorner(k, cw * 0.6f));
            Route(k, name, p.ToArray());
        }
    }

    /// <summary>k5 (lap 2, y 13.75 → 16.5): a flat deck with a low sweeper bar (arm 2.4 m) round a post, then a ramp up.</summary>
    private static void Sweeper()
    {
        const int k = 5;
        var y0 = H(k);
        var y1 = H(k + 1);
        const float pivotS = -1.5f, rampS = 1.6f;
        Floor(k, "Deck", -HalfLen, rampS, -HalfW, HalfW, y0);
        Slope(k, "Ramp", rampS, y0, HalfLen, y1, -HalfW, HalfW, 0.5f, _ledge);
        RampBody(k, "Ramp", rampS, y0, HalfLen, y1, -HalfW, HalfW, 0.5f);
        var sw = KitBuilder.Spinner(_hz, _geo, "gt_k5_sweep_0", P(k, pivotS, 0f, y0), 2.4f, 0.35f, K5SweepA, -K5SweepA);
        Sweepers.Add(sw);

        // Corner 6 spring (the k6 shortcut) is built with k6. Routes: inner / outer arc round the post (the bar reflex jumps).
        foreach (var w in new[] { -1.45f, 1.45f })
            Route(k, w < 0 ? "inner" : "outer", StartCorner(k, w * 0.5f), W(k, -5.6f, w, y0, WaypointAction.Run, 0, 0.8f), W(k, pivotS, w, y0, WaypointAction.Run, 0, 0.8f),
                W(k, rampS + 0.6f, w * 0.8f, y0 + 0.3f, WaypointAction.Run, 0, 0.8f), W(k, 6.4f, w * 0.6f, y1), EndCorner(k, w * 0.4f));
    }

    /// <summary>k6 (lap 2, y 16.5 → 19.25): a full-width belt against you with 2 bumpers; a spring on corner 6 lands mid-belt.</summary>
    private static void BeltAgainstYou()
    {
        const int k = 6;
        var y0 = H(k);
        var y1 = H(k + 1);
        var len = 2f * HalfLen;
        var pitch = Mathf.Atan2(Rise, len) * Mathf.Rad2Deg;
        var sp = U(k).z > 0 ? pitch : -pitch;
        var rot = Quaternion.Euler(-sp, 0f, 0f);
        var climb = (U(k) * len + Vector3.up * Rise).normalized;
        var slopeLen = Mathf.Sqrt(len * len + Rise * Rise);
        Conveyors.Add(KitBuilder.Conveyor(_hz, "gt_k6_belt_0", P(k, 0f, 0f, (y0 + y1) * 0.5f) - rot * Vector3.up * 0.25f, new Vector3(2f * HalfW, 0.5f, slopeLen), -climb * K6BeltMs, sp));
        RampBody(k, "Belt", -HalfLen, y0, HalfLen, y1, -HalfW, HalfW, 0.5f);
        float BeltY(float s) => y0 + Rise * (s + HalfLen) / len;
        // Both on the outer half and passed on the core side: a bonk pushes you toward the core, not off the open edge
        // (QA: passing one on its outer side threw reference bots off into the goo).
        Bumper(k, "gt_k6_bumper_0", -3f, 1.2f, BeltY(-3f), 1.0f);
        Bumper(k, "gt_k6_bumper_1", 3.4f, 0.9f, BeltY(3.4f), 1.0f);

        // Corner 6 spring: on the corner's outer back quadrant, launching up the belt past bumper 0 (lands ≈ s +0.5, w +0.6).
        const float padS = -HalfLen - 1.0f, padW = 1.2f; // G4.3c: corner front (behind the pad, bots got stuck)
        var padTop = P(k, padS, padW, y0 + 0.1f);
        var landS = 0.6f; // between the bumpers
        var flight = 1.0f;
        var launch = U(k) * ((landS - padS) / flight) + Vo(k) * ((-0.4f - padW) / flight) + Vector3.up * 12f;
        Pads.Add(KitBuilder.SpringPad(_hz, "gt_k6_spring_0", padTop, new Vector2(1.8f, 1.8f), launch));

        Route(k, "belt", StartCorner(k, -0.6f), W(k, -6.3f, -0.4f, BeltY(-6.3f)), W(k, -3f, -0.21f, BeltY(-3f), WaypointAction.Run, 0, 0.4f), W(k, 0.4f, -0.5f, BeltY(0.4f)),
            W(k, 3.4f, -0.51f, BeltY(3.4f), WaypointAction.Run, 0, 0.4f), W(k, 6.4f, 0f, y1), EndCorner(k));
        // From the corner straight over the pad up the belt (a launch overrides the run-up speed).
        Route(k, "spring", StartCorner(k, padW), W(k, padS - 1.4f, padW, y0, WaypointAction.Run, 0, 0.5f),
            W(k, landS, -0.4f, BeltY(landS), WaypointAction.Land, 0, 1.8f), W(k, 3.4f, -0.51f, BeltY(3.4f), WaypointAction.Run, 0, 0.4f), W(k, 6.4f, 0f, y1), EndCorner(k));
    }

    /// <summary>k7 (lap 2, y 19.25 → 22): a ramp up to a deck, a wall at s +3 with 3 doors lifting in a travelling wave.</summary>
    private static void GateToSummit()
    {
        const int k = 7;
        var y0 = H(k);
        var y1 = H(k + 1);
        const float rampEnd = 0f, wallS = 3f, ballS = 1.5f; // G4.3c: the ball reaches bots waiting at the doors
        Slope(k, "Ramp", -HalfLen, y0, rampEnd, y1, -HalfW, HalfW, 0.5f, _ledge);
        RampBody(k, "Ramp", -HalfLen, y0, rampEnd, y1, -HalfW, HalfW, 0.5f);
        Floor(k, "Deck", rampEnd, HalfLen, -HalfW, HalfW, y1);
        float[] guides = { -2.4f, -0.8f, 0.8f, 2.4f };
        foreach (var g in guides)
            SideBox(k, $"GateGuide{g:+0.0;-0.0}", wallS - 0.3f, wallS + 0.3f, g - 0.1f, g + 0.1f, y1, y1 + 4.4f, _rail);
        SideBox(k, "GateLintel", wallS - 0.3f, wallS + 0.3f, -HalfW, HalfW, y1 + 4.4f, y1 + 4.8f, _rail);
        var doorW = new[] { -1.6f, 0f, 1.6f };
        const int cycle = DoorClosed + DoorOpening + DoorOpen + DoorClosing;
        for (var i = 0; i < 3; i++)
        {
            var c = P(k, wallS, doorW[i], y1 + 1.1f);
            var d = KitBuilder.GateDoor(_hz, $"gt_k7_door_{i}", c, SideSize(k, 0.4f, 1.4f, 2.2f), DoorClosed, DoorOpening, DoorOpen, DoorClosing, i * cycle / 3, (2 - i) * cycle / 3, lift: 2.1f);
            Doors.Add(d);
        }

        // G4.3c: a gumball crossing in front of the gate, rolling inward (toward the core: an outward knock on lap 2 is a
        // fall into the goo). Without it no door lane had any contact (bots wait for an open door; nobody is pushed).
        const float br = 0.7f;
        Box3(k, "BallDispenser", ballS - 1.0f, ballS + 1.0f, HalfW - 0.4f, HalfW, y1, y1 + 2.0f, KitBuilder.Grape);
        var xball = KitBuilder.RollingBall(_hz, _geo, "gt_k7_ball_0", P(k, ballS, HalfW - 0.4f - br - 0.05f, y1 + br + 0.01f), P(k, ballS, -HalfW + br + 0.1f, y1 + br + 0.01f), br, 4200, 72, 84);
        if (xball.chute != null)
        {
            var t = xball.chute.transform;
            t.SetPositionAndRotation(P(k, ballS, HalfW - 0.42f, y1 + 1.0f), Quaternion.LookRotation(-Vo(k)));
            t.localScale = new Vector3(1.7f, 1.7f, 0.04f);
        }

        Balls.Add(xball);
        for (var i = 0; i < 3; i++)
            Route(k, $"door{i}", StartCorner(k, doorW[i] * 0.5f), W(k, -5.6f, doorW[i] * 0.7f, y0 + 0.4f), W(k, -1.1f, doorW[i], y1 - 0.4f, WaypointAction.WaitClear, 1, 0.5f),
                W(k, wallS - 0.95f, doorW[i], y1, WaypointAction.Door, i, 0.35f), W(k, wallS + 1.1f, doorW[i], y1, WaypointAction.Run, 0, 0.7f),
                W(k, HalfLen + CornerHalf, 0f, y1, WaypointAction.Run, 0, 1.2f));
    }
}
