using System.Collections.Generic;
using System.IO;
using FallRush.Net;
using FallRush.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds <c>Map_BlockPartyDash</c> (doc 08 §3, M3b), code-built and re-runnable like <see cref="SandboxBuilder"/>.
/// +Z is the course direction; about 250 m from the start plaza to the finish arch, a sunny toy-block sky course
/// (lengthened 2026-11-19 from ~160 m: reference bots finished in ~31 s; S5 Turntable Islands and S6 Mallet Bridge added).
/// <list type="bullet">
/// <item>Start deck (z −12…8): 12 staggered spawns, start gate across the deck that opens at RUSH.</item>
/// <item>S1 Block Hop (z 8…37): three lanes of floating blocks: left static (1.5 m gaps), middle bobbing
/// (variant A vertical ±0.6 m / B lateral ±1.2 m), right static with 3.6 m gaps (fewer, longer jumps).</item>
/// <item>CP1 deck (z 37…45).</item>
/// <item>S2 Spinner Alley (z 45…85): a 10 m bridge with three spinner bars (low = jump, high = dive under, mid = jump;
/// variant = direction pattern) and two 1 m edge beams outside the bars' reach (safe, slow, narrow).</item>
/// <item>S3 Conveyor Climb (z 85…105, +3 m): left a conveyor ramp running against you (safe, slower), right two spring
/// pads over void onto a block and up to the deck (fast, risky).</item>
/// <item>CP2 deck (z 100/105…117).</item>
/// <item>S4 Gate wall (z 117): five real, timed gate doors opening in a sweep (variant = sweep order), always ≥ 1 open.</item>
/// <item>CP3 deck (z 117…135).</item>
/// <item>S5 Turntable Islands (z 135…163, y 3): three spinning discs (r 4 m, 2 m gaps; variant = directions), or a 1.5 m
/// walkway on either side (safe, narrow).</item>
/// <item>CP4 deck (z 165…175).</item>
/// <item>S6 Mallet Bridge (z 175…216): a 6 m railed bridge under four foam-roller mallets swinging along it, 10 m apart
/// (time your run between them; a hit bounces you back), or a 1 m edge beam outside their reach on either side (safe,
/// narrow). Overhead gantries; the posts stand outside the beams.</item>
/// <item>CP5 deck (z 216…224).</item>
/// <item>S7 Finish ramp (z 224…244, +2 m): broad ramp with scattered bumpers, then the checkered finish arch at z 247.</item>
/// </list>
/// Fall volume y &lt; −10 → respawn at the last checkpoint. Menu: Fall Rush/Setup/Build Map_BlockPartyDash ·
/// batch: <c>-executeMethod BlockPartyDashBuilder.Run</c>.
/// </summary>
public static class BlockPartyDashBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_BlockPartyDash.unity";
    public const float KillY = -10f;

    // Layout constants (also used by the bot routes).
    private const float LaneL = -6f, LaneM = 0f, LaneR = 6f;
    /// <summary>S1 block: centre z, depth (z), width (x), top height. G1: real v2 jumps (hop 1.2 m, ≈ 3.2 m running reach).</summary>
    private struct S1Block
    {
        public float Z, D, W, Top;
        public S1Block(float z0, float z1, float w, float top) { Z = (z0 + z1) * 0.5f; D = z1 - z0; W = w; Top = top; }
        public float Z0 => Z - D * 0.5f;
        public float Z1 => Z + D * 0.5f;
    }

    // Safe lane (left): big blocks, gaps 1.6–1.9 m, steps of ±0.3 m and one 0.9 m "stair".
    private static readonly S1Block[] BlocksL =
    {
        new S1Block(9.7f, 12.5f, 3.2f, 0.3f), new S1Block(14.2f, 17.0f, 3.0f, 0.6f), new S1Block(18.8f, 21.6f, 3.2f, 0.3f),
        new S1Block(23.3f, 26.1f, 3.0f, 0.6f), new S1Block(27.9f, 30.5f, 3.0f, 0.9f), new S1Block(32.3f, 35.2f, 3.2f, 0.45f),
    };
    // Fast, risky lane (right): fewer, smaller blocks, gaps 2.3–2.7 m (≈ 70–85 % of the v2 running jump), steps up and
    // down of 0.4–0.7 m, a 1.5 m stair top in the middle.
    private static readonly S1Block[] BlocksR =
    {
        new S1Block(10.5f, 12.5f, 2.2f, 0.4f), new S1Block(14.9f, 16.9f, 2.0f, 0.9f), new S1Block(19.3f, 20.9f, 1.8f, 1.5f),
        new S1Block(23.4f, 25.4f, 2.0f, 0.8f), new S1Block(28.0f, 30.0f, 2.2f, 0.3f), new S1Block(32.4f, 34.4f, 2.0f, 0.7f),
    };
    // Middle lane: bobbing platforms (variant A vertical ±0.4 m, B lateral ±1.2 m), gaps ≈ 2.1 m at rest.
    private static readonly float[] BlocksM = { 11.2f, 15.7f, 20.2f, 24.7f, 29.2f, 33.7f };
    private const float BobDepth = 2.4f;
    private static readonly float[] SpinnerZ = { 53f, 65f, 77f };
    private static readonly float[] DoorX = { -10f, -5f, 0f, 5f, 10f };
    private const float WallZ = 117f;
    private static readonly float[] TableZ = { 139f, 149f, 159f };
    private const float TableR = 4f;
    private static readonly float[] MalletZ = { 181f, 191f, 201f, 211f };
    private const float BridgeEnd = 216f;
    private const float RampZ0 = 224f; // S7 finish ramp start (deck y 3 → 5 over 20 m)
    private const float FinishZ = 247f;
    private static readonly Vector2[] Bumpers = { new Vector2(-5f, RampZ0 + 5f), new Vector2(0f, RampZ0 + 8f), new Vector2(5f, RampZ0 + 5f), new Vector2(-2.5f, RampZ0 + 13f), new Vector2(2.5f, RampZ0 + 13f), new Vector2(-7f, RampZ0 + 17.5f), new Vector2(7f, RampZ0 + 17.5f) };

    private static Transform _geo, _hz;
    private static Material _walk, _deck, _block, _wall;

    [MenuItem("Fall Rush/Setup/Build Map_BlockPartyDash")]
    public static void Run()
    {
        KitBuilder.Init();
        _walk = KitBuilder.Walk;
        _deck = Mat("BpdDeck", new Color(0.93f, 0.95f, 0.99f));
        _block = Mat("BpdBlock", new Color(0.84f, 0.92f, 0.99f));
        _wall = Mat("BpdWall", new Color(0.88f, 0.86f, 0.97f));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cam.tag = "MainCamera";
        var c = cam.GetComponent<Camera>();
        c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = KitBuilder.Hex(0xBFE6FF);
        c.farClipPlane = 450f;
        cam.transform.SetPositionAndRotation(new Vector3(0, 8, -22), Quaternion.Euler(18, 0, 0));
        var light = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.15f;
        light.shadows = LightShadows.None;
        light.transform.rotation = Quaternion.Euler(52f, -25f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.64f, 0.70f, 0.80f);
        RenderSettings.fog = false;

        _geo = new GameObject("Geometry").transform;
        _hz = new GameObject("Hazards").transform;
        var platforms = new List<HazardPlatform>();
        var sweepers = new List<HazardSweeper>();
        var conveyors = new List<HazardConveyor>();
        var pads = new List<HazardSpringPad>();
        var doors = new List<HazardGateDoor>();
        var bumpers = new List<HazardBumper>();
        var tables = new List<HazardTurnTable>();
        var mallets = new List<HazardMallet>();

        // ---------------- start deck + start gate
        Deck("StartDeck", -11f, 11f, -12f, 8f, 0f);
        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var row = i / 4;
            var col = i % 4;
            var x = -7.5f + 5f * col + (row == 1 ? -2.5f : 0f);
            var z = -10f + 2.5f * row;
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(new Vector3(x, 0.05f, z), Quaternion.identity);
            spawns[i] = t;
            KitBuilder.Visual(_geo, $"SpawnPad_{i:00}", new Vector3(x, 0.005f, z), new Vector3(1.4f, 0.01f, 1.4f), KitBuilder.Mint);
        }

        var gate = KitBuilder.GateDoor(_hz, "bpd_start_gate", new Vector3(0f, 1.25f, 6.6f), new Vector3(22f, 2.5f, 0.4f), 0, 10, 0, 0, 0, 0, lift: 2.8f);
        gate.def.Mode = 1;
        gate.variantB = gate.def;
        gate.hasVariantB = false;
        doors.Add(gate);
        KitBuilder.Sign(_geo, "BLOCK PARTY DASH", new Vector3(0f, 4.4f, 7f), 0.06f);

        // ---------------- S1 Block Hop (G1 rebuild: wider gaps, height variation, varied sizes)
        for (var i = 0; i < BlocksL.Length; i++)
            Block($"S1_L_{i}", LaneL, BlocksL[i]);
        for (var i = 0; i < BlocksR.Length; i++)
            Block($"S1_R_{i}", LaneR, BlocksR[i]);
        for (var i = 0; i < BlocksM.Length; i++)
        {
            var pc = new Vector3(LaneM, -0.25f, BlocksM[i]);
            var pl = KitBuilder.LoopPlatform(_hz, $"bpd_s1_bob_{i}", pc, new Vector3(3f, 0.5f, BobDepth),
                new[] { new Vector3(0f, -0.4f, 0f), new Vector3(0f, 0.4f, 0f) },
                new[] { new Vector3(-1.2f, 0f, 0f), new Vector3(1.2f, 0f, 0f) }, 120);
            pl.visual.GetComponent<MeshRenderer>().sharedMaterial = KitBuilder.Platform;
            platforms.Add(pl);
        }

        // ---------------- CP1
        Deck("CP1Deck", -11f, 11f, 37f, 45f, 0f);
        var cp1 = Checkpoint(1, new Vector3(0f, 1.5f, 39f), new Vector3(22f, 4f, 2f), 0f, 41.5f, 43.5f, false);

        // ---------------- S2 Spinner Alley: 10 m bridge + 1 m edge beams outside the bars' reach (5 + 0.15 + 0.31 = 5.46 m)
        Deck("S2Bridge", -5f, 5f, 45f, 85f, 0f);
        Deck("S2BeamL", -7.5f, -6.5f, 45f, 85f, 0f);
        Deck("S2BeamR", 6.5f, 7.5f, 45f, 85f, 0f);
        float[] heights = { 0.35f, 0.95f, 0.5f };
        int[] dirA = { 171, -171, 171 };
        string[] names = { "LOW: JUMP", "HIGH: DIVE UNDER", "MID: JUMP" };
        for (var i = 0; i < 3; i++)
        {
            sweepers.Add(KitBuilder.Spinner(_hz, _geo, $"bpd_spinner_{i + 1:00}", new Vector3(0f, 0f, SpinnerZ[i]), 5f, heights[i], dirA[i], -dirA[i]));
            KitBuilder.Label(_geo, names[i], new Vector3(-9f, 2.2f, SpinnerZ[i]));
        }

        KitBuilder.Label(_geo, "S2 SPINNER ALLEY (edge beams: slow & safe)", new Vector3(0f, 3.2f, 46f));

        // ---------------- S3 Conveyor Climb (+3 m)
        Deck("S3Deck", -11f, 11f, 85f, 89f, 0f);
        const float rampLen = 16f, rise = 3f;
        var pitch = Mathf.Atan2(rise, rampLen) * Mathf.Rad2Deg;
        var rot = Quaternion.Euler(-pitch, 0f, 0f);
        var up = rot * Vector3.up;
        var slopeDir = rot * Vector3.forward;
        var topCentre = new Vector3(-6f, rise * 0.5f, 89f + rampLen * 0.5f);
        var slopeLen = Mathf.Sqrt(rampLen * rampLen + rise * rise);
        conveyors.Add(KitBuilder.Conveyor(_hz, "bpd_s3_conveyor", topCentre - up * 0.25f, new Vector3(6f, 0.5f, slopeLen), -slopeDir * 2.0f, pitch)); // G1: 3.5 → 2.0 m/s (v2 runs 5 m/s: net 3 m/s up, the safe lane still costs time)
        KitBuilder.Label(_geo, "CONVEYOR RAMP (3.5 m/s against: safe, slower)", new Vector3(-6f, 3.5f, 88f));
        pads.Add(KitBuilder.SpringPad(_hz, "bpd_s3_pad_1", new Vector3(6f, 0.1f, 87.6f), new Vector2(2.4f, 1.6f), new Vector3(0f, 11f, 7.5f)));
        Box("S3Block", new Vector3(6f, 0.75f, 93.75f), new Vector3(5f, 1.5f, 4.5f), _block); // top 1.5, z 91.5…96
        pads.Add(KitBuilder.SpringPad(_hz, "bpd_s3_pad_2", new Vector3(6f, 1.6f, 95f), new Vector2(2.4f, 1.4f), new Vector3(0f, 11f, 9f))); // 9 m/s: clears the 4 m gap to the deck from the pad's near edge
        KitBuilder.Label(_geo, "SPRING HOPS (fast, risky)", new Vector3(6f, 3.5f, 86.5f));

        // ---------------- CP2 (y 3)
        Deck("CP2DeckR", -1f, 11f, 100f, 117f, 3f);
        Deck("CP2DeckL", -11f, -1f, 105f, 117f, 3f);
        var cp2 = Checkpoint(2, new Vector3(0f, 4.5f, 107f), new Vector3(22f, 4f, 2f), 3f, 110f, 112f, false);

        // ---------------- S4 gate wall: five timed doors (3 m wide, 2 m pillars), cycle 210 ticks, open 45, offsets 42 apart.
        Deck("S4Deck", -12.5f, 12.5f, 113f, 135f, 3f);
        const float wallH = 3f;
        float[] pillars = { -12.5f, -11.5f, -8.5f, -6.5f, -3.5f, -1.5f, 1.5f, 3.5f, 6.5f, 8.5f, 11.5f, 12.5f };
        for (var i = 0; i < pillars.Length; i += 2)
            Box($"S4Pillar_{i / 2}", new Vector3((pillars[i] + pillars[i + 1]) * 0.5f, 3f + wallH * 0.5f, WallZ), new Vector3(pillars[i + 1] - pillars[i], wallH, 0.6f), _wall);
        Box("S4Lintel", new Vector3(0f, 3f + wallH + 1.6f, WallZ), new Vector3(25f, 1.0f, 0.6f), _wall);
        for (var i = 0; i < DoorX.Length; i++)
            // Tuned (QA): cycle 150 → 210 ticks (closed 141, opening 6, open 45, closing 18), offsets 42 apart: 45 > 42, so ≥ 1 door is always open.
            doors.Add(KitBuilder.GateDoor(_hz, $"bpd_door_{i + 1}", new Vector3(DoorX[i], 3f + 1.5f, WallZ), new Vector3(3f, 3f, 0.4f), 141, 6, 45, 18, i * 42, (4 - i) * 42));
        KitBuilder.Label(_geo, "S4 GATE WALL (every door opens; watch the sweep)", new Vector3(0f, 8.8f, WallZ - 1f));

        // ---------------- CP3
        var cp3 = Checkpoint(3, new Vector3(0f, 4.5f, 124f), new Vector3(25f, 4f, 2f), 3f, 127f, 129f, false);

        // ---------------- S5 Turntable Islands (y 3): three discs (r 4, 2 m gaps) or a narrow walkway on either side.
        int[] tableA = { 360, -300, 360 };
        for (var i = 0; i < TableZ.Length; i++)
            tables.Add(KitBuilder.TurnTable(_hz, $"bpd_turntable_{i + 1}", new Vector3(0f, 3f, TableZ[i]), TableR, tableA[i], -tableA[i]));
        Deck("S5WalkL", -10.5f, -9f, 135f, 165f, 3f);
        Deck("S5WalkR", 9f, 10.5f, 135f, 165f, 3f);
        KitBuilder.Label(_geo, "S5 TURNTABLE ISLANDS (walkways: narrow & safe)", new Vector3(0f, 6.5f, 135.5f));

        // ---------------- CP4
        Deck("CP4Deck", -11f, 11f, 165f, 175f, 3f);
        var cp4 = Checkpoint(4, new Vector3(0f, 4.5f, 167.5f), new Vector3(22f, 4f, 2f), 3f, 170f, 172f, false);

        // ---------------- S6 Mallet Bridge: 6 m railed bridge, four mallets swinging along it (along Z) with a 5 m-wide roller
        // head (half-width 2.5 m: no gap past it on the bridge); 1 m beams at |x| 5…6, outside the head (|x| ≤ 2.5 + 0.31).
        // Tuned (QA): swinging across the bridge threw racers off (no rails) or pinned them against the rails.
        Deck("S6Bridge", -3f, 3f, 175f, BridgeEnd, 3f);
        Deck("S6BeamL", -6f, -5f, 175f, BridgeEnd, 3f);
        Deck("S6BeamR", 5f, 6f, 175f, BridgeEnd, 3f);
        // Side rails (1.2 m): a mallet hit bounces you along the bridge and costs time instead of throwing you off
        // (QA, first pass without rails: ~20 falls per race on this bridge).
        Box("S6RailL", new Vector3(-3.1f, 3.6f, (175.5f + BridgeEnd) * 0.5f), new Vector3(0.2f, 1.2f, BridgeEnd - 175.5f), _wall);
        Box("S6RailR", new Vector3(3.1f, 3.6f, (175.5f + BridgeEnd) * 0.5f), new Vector3(0.2f, 1.2f, BridgeEnd - 175.5f), _wall);
        int[] malletA = { 120, 112, 128, 116 }; // ~4 s swings, amplitude 70°: the head is low (dangerous) about a third of the time
        int[] malletB = { 116, 128, 112, 120 };
        for (var i = 0; i < MalletZ.Length; i++)
        {
            var id = $"bpd_mallet_{i + 1}";
            var ml = KitBuilder.Mallet(_hz, _geo, id, new Vector3(0f, 8.5f, MalletZ[i]), 2, 4.9f, 70f, malletA[i], malletB[i]);
            ml.def.HeadHalfWidthMm = 2500;
            ml.variantB.HeadHalfWidthMm = 2500;
            var head = ml.visual.Find("Head");
            if (head != null)
                head.localScale = new Vector3(2500f / 750f, 1f, 1f); // the kit head is 1.5 m wide along X
            mallets.Add(ml);
            // The kit frame puts its posts on the axle (on the bridge here): replace them with a gantry standing off the bridge.
            foreach (var post in new[] { $"MalletPostA_{id}", $"MalletPostB_{id}" })
            {
                var t = _geo.Find(post);
                if (t != null)
                    Object.DestroyImmediate(t.gameObject);
            }

            Box($"S6GantryL_{i}", new Vector3(-6.6f, 5.75f, MalletZ[i]), new Vector3(0.4f, 5.5f, 0.4f), KitBuilder.Grape);
            Box($"S6GantryR_{i}", new Vector3(6.6f, 5.75f, MalletZ[i]), new Vector3(0.4f, 5.5f, 0.4f), KitBuilder.Grape);
            KitBuilder.Visual(_geo, $"S6GantryTop_{i}", new Vector3(0f, 8.7f, MalletZ[i]), new Vector3(13.6f, 0.4f, 0.4f), KitBuilder.Grape);
        }

        KitBuilder.Label(_geo, "S6 MALLET BRIDGE (time your run; edge beams: narrow & safe)", new Vector3(0f, 10f, 175.5f));

        // ---------------- CP5
        Deck("CP5Deck", -11f, 11f, BridgeEnd, RampZ0, 3f);
        var cp5 = Checkpoint(5, new Vector3(0f, 4.5f, BridgeEnd + 2f), new Vector3(22f, 4f, 2f), 3f, BridgeEnd + 4.5f, BridgeEnd + 6.5f, false);

        // ---------------- S7 finish ramp (+2 m over 20 m) with bumpers, finish arch
        var fPitch = Mathf.Atan2(2f, 20f) * Mathf.Rad2Deg;
        var fRot = Quaternion.Euler(-fPitch, 0f, 0f);
        var fUp = fRot * Vector3.up;
        var fLen = Mathf.Sqrt(400f + 4f);
        Box("S7Ramp", new Vector3(0f, 4f, RampZ0 + 10f) - fUp * 0.25f, new Vector3(24f, 0.5f, fLen), _walk, fRot);
        Deck("FinishDeck", -12f, 12f, RampZ0 + 20f, RampZ0 + 31f, 5f);
        foreach (var b in Bumpers)
            bumpers.Add(KitBuilder.Bumper(_hz, $"bpd_bumper_{bumpers.Count + 1}", new Vector3(b.x, RampY(b.y) - 0.05f, b.y), 0.7f, 1.05f));
        var finish = Checkpoint(6, new Vector3(0f, 6.5f, FinishZ), new Vector3(24f, 4f, 2f), 5f, FinishZ + 3f, FinishZ + 5f, true);
        // Checkered finish arch: posts are solid (Gameplay), the banner is visual only.
        Box("ArchPostL", new Vector3(-11.6f, 7f, FinishZ), new Vector3(0.8f, 4f, 0.8f), KitBuilder.Checker);
        Box("ArchPostR", new Vector3(11.6f, 7f, FinishZ), new Vector3(0.8f, 4f, 0.8f), KitBuilder.Checker);
        for (var i = 0; i < 24; i++)
            for (var j = 0; j < 2; j++)
                KitBuilder.Visual(_geo, $"ArchCheck_{i}_{j}", new Vector3(-11.5f + i + 0.5f, 9.25f + j * 0.5f, FinishZ), new Vector3(1f, 0.5f, 0.3f), (i + j) % 2 == 0 ? KitBuilder.Checker : KitBuilder.BeltArrow);
        KitBuilder.Sign(_geo, "FINISH", new Vector3(0f, 10.6f, FinishZ - 0.3f), 0.07f);

        // ---------------- MapInfo, definition, course line, bot routes
        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("BlockPartyDash", MapPoolV1.BlockPartyDash, "Race", KillY, new[] { 0, 1, 2, 3, 5 });
        def.checkpointCount = 6;
        def.targetDuration = new Vector2Int(75, 120);
        def.timerCapSeconds = 150; // lengthened course (~250 m): reference bots ~50 s, humans slower
        def.introSeconds = 3f;
        def.countdownSeconds = 3f;
        def.minPlayers = 1;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(0f, 0f, -5f);
        info.botAreaHalfExtents = new Vector2(9f, 5f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.sweepers = sweepers.ToArray();
        info.platforms = platforms.ToArray();
        info.conveyors = conveyors.ToArray();
        info.pads = pads.ToArray();
        info.doors = doors.ToArray();
        info.bumpers = bumpers.ToArray();
        info.turnTables = tables.ToArray();
        info.mallets = mallets.ToArray();
        info.courseLine = new[]
        {
            new Vector3(0, 0, -8), new Vector3(0, 0, 8), new Vector3(0, 0, 37), new Vector3(0, 0, 45), new Vector3(0, 0, 85), new Vector3(0, 0, 89),
            new Vector3(0, 3, 105), new Vector3(0, 3, 117), new Vector3(0, 3, 135), new Vector3(0, 3, 165), new Vector3(0, 3, 175),
            new Vector3(0, 3, BridgeEnd), new Vector3(0, 3, RampZ0), new Vector3(0, 5, RampZ0 + 20f), new Vector3(0, 5, FinishZ),
        };
        var cps = new[] { cp1, cp2, cp3, cp4, cp5, finish };
        foreach (var cp in cps)
            cp.courseDistance = CourseDistance(info.courseLine, cp.transform.position);
        info.checkpoints = cps;
        info.botRoutes = Routes();

        // Fall volume / bounds marker (visual only, far below).
        KitBuilder.Visual(_geo, "FallVolumeMarker", new Vector3(0f, KillY, 120f), new Vector3(60f, 0.02f, 290f), KitBuilder.Mint).GetComponent<MeshRenderer>().enabled = false;

        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new IOException($"Failed to save {ScenePath}");
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!list.Exists(s => s.path == ScenePath))
        {
            list.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        AssetDatabase.SaveAssets();
        Physics.SyncTransforms();
        MapHashTool.Dump("Map_BlockPartyDash-build", info);
        var hash = info.ContentHash;
        SandboxBuilder.RecordMapHash("Map_BlockPartyDash", hash);
        var problems = new List<string>();
        for (byte v = 0; v < 2; v++)
            foreach (var m in def.allowedModifiers)
                problems.AddRange(info.Validate(new RoundParams { MapId = MapPoolV1.BlockPartyDash, Variant = v, Modifier = (byte)m }));
        Debug.Log($"[setup] built {ScenePath} mapHash={Sha256.Hex(hash)} hazards: {sweepers.Count} spinner, {platforms.Count} platform, {tables.Count} turn table, {mallets.Count} mallet, {conveyors.Count} conveyor, {pads.Count} pad, {doors.Count} door (incl. start gate), {bumpers.Count} bumper; " +
                  $"checkpoints at {string.Join(", ", System.Array.ConvertAll(cps, x => x.courseDistance.ToString("F1")))} m; allowlist problems: {problems.Count} {string.Join("; ", problems)}");
    }

    private static float RampY(float z) => 3f + (z - RampZ0) * 0.1f;

    private static Material Mat(string name, Color color)
    {
        var path = $"{SandboxBuilder.MaterialsDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }

        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", 0.15f);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    private static GameObject Box(string name, Vector3 c, Vector3 size, Material mat, Quaternion? rot = null) => KitBuilder.Box(_geo, name, c, size, mat, rot);

    /// <summary>Walkable deck with its top at <paramref name="top"/>, 1 m thick.</summary>
    private static void Deck(string name, float x0, float x1, float z0, float z1, float top) =>
        Box(name, new Vector3((x0 + x1) * 0.5f, top - 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, 1f, z1 - z0), _deck);

    /// <summary>A pillar block from y −1.5 up to its top.</summary>
    private static void Block(string name, float x, in S1Block b) =>
        Box(name, new Vector3(x, (b.Top - 1.5f) * 0.5f, b.Z), new Vector3(b.W, b.Top + 1.5f, b.D), _block);

    /// <summary>Checkpoint trigger (no collider) + 12 respawn spots in two rows at <paramref name="row1"/>/<paramref name="row2"/>, facing +Z; a mint gate stripe on the floor.</summary>
    private static RaceCheckpoint Checkpoint(int index, Vector3 centre, Vector3 size, float floorY, float row1, float row2, bool finish)
    {
        var go = new GameObject(finish ? "Finish" : $"Checkpoint_{index}");
        go.transform.position = centre;
        var cp = go.AddComponent<RaceCheckpoint>();
        cp.index = index;
        cp.triggerSize = size;
        cp.isFinish = finish;
        var spots = new Transform[12];
        for (var i = 0; i < 12; i++)
        {
            var t = new GameObject($"Respawn_{i:00}").transform;
            t.SetParent(go.transform, false);
            t.SetPositionAndRotation(new Vector3(-7.5f + 3f * (i % 6), floorY + 0.05f, i < 6 ? row1 : row2), Quaternion.identity);
            spots[i] = t;
        }

        cp.respawns = spots;
        KitBuilder.Visual(_geo, $"CheckpointStripe_{index}", new Vector3(0f, floorY + 0.006f, centre.z), new Vector3(size.x, 0.012f, 0.6f), finish ? KitBuilder.Checker : KitBuilder.Mint);
        if (!finish)
        {
            KitBuilder.Box(_geo, $"CPPostL_{index}", new Vector3(-size.x * 0.5f - 0.3f, floorY + 1.5f, centre.z), new Vector3(0.5f, 3f, 0.5f), KitBuilder.Mint);
            KitBuilder.Box(_geo, $"CPPostR_{index}", new Vector3(size.x * 0.5f + 0.3f, floorY + 1.5f, centre.z), new Vector3(0.5f, 3f, 0.5f), KitBuilder.Mint);
            KitBuilder.Sign(_geo, $"CHECKPOINT {index}", new Vector3(0f, floorY + 3.3f, centre.z));
        }

        return cp;
    }

    private static float CourseDistance(Vector3[] line, Vector3 p)
    {
        var best = float.MaxValue;
        var bestS = 0f;
        var acc = 0f;
        for (var i = 1; i < line.Length; i++)
        {
            var a = line[i - 1];
            var b = line[i];
            var u = b - a;
            var t = Mathf.Clamp01(Vector3.Dot(p - a, u) / u.sqrMagnitude);
            var d = (a + u * t - p).sqrMagnitude;
            if (d < best)
            {
                best = d;
                bestS = acc + u.magnitude * t;
            }

            acc += u.magnitude;
        }

        return bestS;
    }

    // ------------------------------------------------------------------ waypoint bot routes (server only)

    private static Waypoint W(float x, float y, float z, WaypointAction a = WaypointAction.Run, int arg = 0, float r = 0.6f) =>
        new Waypoint { XMm = KitBuilder.Mm(x), YMm = KitBuilder.Mm(y), ZMm = KitBuilder.Mm(z), RadiusMm = KitBuilder.Mm(r), Action = a, Arg = arg };

    private static BotRoute[] Routes()
    {
        var r = new List<BotRoute>();

        // Section 0: start → CP1, three lanes.
        Waypoint[] Lane(float x, S1Block[] blocks)
        {
            var p = new List<Waypoint> { W(x, 0, 5.5f), W(x, 0, 7.6f, WaypointAction.Jump, 0, 0.4f) };
            for (var i = 0; i < blocks.Length; i++)
            {
                var b = blocks[i];
                p.Add(W(x, b.Top, Mathf.Min(b.Z, b.Z0 + 1.0f), WaypointAction.Land, 0, Mathf.Min(0.9f, b.D * 0.45f)));
                p.Add(W(x, b.Top, b.Z1 - 0.3f, WaypointAction.Jump, 0, 0.4f));
            }

            p.Add(W(x, 0, 38.6f, WaypointAction.Run, 0, 1.0f));
            p.Add(W(x * 0.5f, 0, 40.5f));
            return p.ToArray();
        }

        r.Add(new BotRoute { Section = 0, Name = "S1-left", Points = Lane(LaneL, BlocksL) });
        r.Add(new BotRoute { Section = 0, Name = "S1-right", Points = Lane(LaneR, BlocksR) });
        var mid = new List<Waypoint> { W(LaneM, 0, 5.5f), W(LaneM, 0, 7.6f, WaypointAction.JumpToPlatform, 0, 0.4f) };
        for (var i = 0; i < BlocksM.Length; i++)
        {
            mid.Add(W(LaneM, 0, BlocksM[i], WaypointAction.Platform, i));
            mid.Add(i + 1 < BlocksM.Length
                ? W(LaneM, 0, BlocksM[i] + BobDepth * 0.5f - 0.3f, WaypointAction.JumpToPlatform, i + 1, 0.4f)
                : W(LaneM, 0, BlocksM[i] + BobDepth * 0.5f - 0.3f, WaypointAction.Jump, 0, 0.4f));
        }

        mid.Add(W(LaneM, 0, 38.6f, WaypointAction.Run, 0, 1.0f));
        mid.Add(W(LaneM, 0, 40.5f));
        r.Add(new BotRoute { Section = 0, Name = "S1-bobbing", Points = mid.ToArray() });

        // Section 1: CP1 → CP2: S2 (centre through the bars | an edge beam) × S3 (conveyor | springs).
        Waypoint[] Centre() => new[] { W(1.8f, 0, 44.5f), W(1.8f, 0, 59f), W(1.8f, 0, 71f), W(1.8f, 0, 84f) };
        Waypoint[] Beam(float x) => new[] { W(x * 0.8f, 0, 43.5f), W(x, 0, 45.5f, WaypointAction.Run, 0, 0.35f), W(x, 0, 65f, WaypointAction.Run, 0, 0.35f), W(x, 0, 84.5f, WaypointAction.Run, 0, 0.35f) };
        Waypoint[] Conveyor() => new[] { W(-6f, 0, 87f), W(-6f, 0, 89.5f), W(-6f, 3, 104.5f, WaypointAction.Run, 0, 0.8f), W(-4f, 3, 108.5f) };
        Waypoint[] Springs() => new[] { W(6f, 0, 86.4f), W(6f, 0.1f, 87.6f, WaypointAction.Run, 0, 0.4f), W(6f, 1.5f, 94.2f, WaypointAction.Run, 0, 1.2f), W(6f, 1.6f, 95f, WaypointAction.Run, 0, 0.5f), W(6f, 3f, 101.5f, WaypointAction.Run, 0, 1.2f), W(3f, 3f, 108.5f) };
        Waypoint[] Join(Waypoint[] a, Waypoint[] b)
        {
            var j = new Waypoint[a.Length + b.Length];
            a.CopyTo(j, 0);
            b.CopyTo(j, a.Length);
            return j;
        }

        r.Add(new BotRoute { Section = 1, Name = "S2-centre+S3-conveyor", Points = Join(Centre(), Conveyor()) });
        r.Add(new BotRoute { Section = 1, Name = "S2-centre+S3-springs", Points = Join(Centre(), Springs()) });
        r.Add(new BotRoute { Section = 1, Name = "S2-beamL+S3-conveyor", Points = Join(Beam(-7f), Conveyor()) });
        r.Add(new BotRoute { Section = 1, Name = "S2-beamR+S3-springs", Points = Join(Beam(7f), Springs()) });

        // Section 2: CP2 → CP3 through one of the five doors (doors[0] is the start gate).
        for (var i = 0; i < DoorX.Length; i++)
            r.Add(new BotRoute
            {
                Section = 2, Name = $"S4-door{i + 1}",
                Points = new[] { W(DoorX[i] * 0.7f, 3, 112.5f), W(DoorX[i], 3, 115.6f, WaypointAction.Door, i + 1, 0.4f), W(DoorX[i], 3, 119f), W(DoorX[i] * 0.5f, 3, 125.5f) },
            });

        // Section 3: CP3 → CP4: across the three turn tables (jump the 2 m gaps) or a walkway on either side.
        r.Add(new BotRoute
        {
            Section = 3, Name = "S5-tables",
            Points = new[]
            {
                W(0f, 3, 133.5f), W(0f, 3, TableZ[0], WaypointAction.Land, 0, 1.5f), W(0f, 3, TableZ[0] + TableR - 0.6f, WaypointAction.Jump, 0, 0.5f),
                W(0f, 3, TableZ[1], WaypointAction.Land, 0, 1.5f), W(0f, 3, TableZ[1] + TableR - 0.6f, WaypointAction.Jump, 0, 0.5f),
                W(0f, 3, TableZ[2], WaypointAction.Land, 0, 1.5f), W(0f, 3, TableZ[2] + TableR - 0.6f, WaypointAction.Jump, 0, 0.5f),
                W(0f, 3, 166.8f, WaypointAction.Run, 0, 1.0f), W(0f, 3, 170.5f),
            },
        });
        r.Add(new BotRoute { Section = 3, Name = "S5-walkL", Points = new[] { W(-7f, 3, 132.5f), W(-9.75f, 3, 135.6f, WaypointAction.Run, 0, 0.35f), W(-9.75f, 3, 164.8f, WaypointAction.Run, 0, 0.35f), W(-5f, 3, 170.5f) } });
        r.Add(new BotRoute { Section = 3, Name = "S5-walkR", Points = new[] { W(7f, 3, 132.5f), W(9.75f, 3, 135.6f, WaypointAction.Run, 0, 0.35f), W(9.75f, 3, 164.8f, WaypointAction.Run, 0, 0.35f), W(5f, 3, 170.5f) } });

        // Section 4: CP4 → CP5: under the mallets (wait for each to clear) or an edge beam.
        var bridge = new List<Waypoint> { W(0f, 3, 175.5f) };
        foreach (var mz in MalletZ)
        {
            bridge.Add(W(0f, 3, mz - 5f, WaypointAction.WaitClear, 0, 0.6f)); // the safe pocket before this mallet
            bridge.Add(W(0f, 3, mz + 4.6f, WaypointAction.Run, 0, 0.8f));
        }

        bridge.Add(W(0f, 3, BridgeEnd + 1f));
        bridge.Add(W(0f, 3, BridgeEnd + 5.5f));
        r.Add(new BotRoute { Section = 4, Name = "S6-bridge", Points = bridge.ToArray() });
        r.Add(new BotRoute { Section = 4, Name = "S6-beamL", Points = new[] { W(-4f, 3, 173.5f), W(-5.5f, 3, 175.6f, WaypointAction.Run, 0, 0.35f), W(-5.5f, 3, BridgeEnd - 0.4f, WaypointAction.Run, 0, 0.35f), W(-3f, 3, BridgeEnd + 5.5f) } });
        r.Add(new BotRoute { Section = 4, Name = "S6-beamR", Points = new[] { W(4f, 3, 173.5f), W(5.5f, 3, 175.6f, WaypointAction.Run, 0, 0.35f), W(5.5f, 3, BridgeEnd - 0.4f, WaypointAction.Run, 0, 0.35f), W(3f, 3, BridgeEnd + 5.5f) } });

        // Section 5: CP5 → finish, around the bumpers (left / centre / right).
        r.Add(new BotRoute { Section = 5, Name = "S7-left", Points = new[] { W(-8.5f, 3, RampZ0 + 0.5f), W(-9.5f, RampY(RampZ0 + 10f), RampZ0 + 10f), W(-9.5f, 5, RampZ0 + 20.5f), W(-3f, 5, FinishZ + 2f) } });
        r.Add(new BotRoute { Section = 5, Name = "S7-centre", Points = new[] { W(0f, 3, RampZ0 + 1f), W(2.5f, RampY(RampZ0 + 6.5f), RampZ0 + 6.5f), W(0f, RampY(RampZ0 + 11f), RampZ0 + 11f), W(0f, RampY(RampZ0 + 16f), RampZ0 + 16f), W(0f, 5, FinishZ + 2f) } });
        r.Add(new BotRoute { Section = 5, Name = "S7-right", Points = new[] { W(8.5f, 3, RampZ0 + 0.5f), W(9.5f, RampY(RampZ0 + 10f), RampZ0 + 10f), W(9.5f, 5, RampZ0 + 20.5f), W(3f, 5, FinishZ + 2f) } });
        return r.ToArray();
    }
}
