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
/// Builds <c>Map_BlockPartyDash</c> (key <c>BlockPartyDash</c>, map id 0), shown to players as <b>Capsule Run</b>
/// (G4.2, 2026-11-19; doc 08 §3, plan page "Capsule Run"). Code-built and re-runnable. +Z is the course direction,
/// x lateral, metres; ≈ 212 m from the start deck to the finish arch, rising 11 m. No in-world text.
/// <list type="bullet">
/// <item>Start funnel (z 0…14): 20 m deck, 12 staggered spawns, start gate (GATE_DOOR mode 1) at z 10, angled rails
/// narrow it to 12 m by z 14.</item>
/// <item>S1 Gumball Slope (z 14…50, +5 m, 7.9°, 0.6 m rails): five ROLLING_BALL lanes (r 1 m) at x 0 / ±2.4 / ±4.8
/// rolling down, staggered releases; a floor hatch lights before each release. CP1 (z 50…54).</item>
/// <item>Wave Gates (z 54…62, a 10 m choke): five 1.8 m grille doors in a wall at z 58.6 lifting in a travelling wave
/// (always ≥ 1 open; variant = wave direction), one ROLLING_BALL crossing the approach at z 55.5 (knocks you
/// sideways into the crowd; variant = period / speed).</item>
/// <item>S2 Hop Field (z 62…104, over the void): centre column of eleven bobbing blocks (gaps ≈ 1.5 m; variant A
/// vertical / B lateral), two outer columns of nine static 2 m blocks at x ±4.6 (gaps 2.3–2.4 m, rises ≤ 0.3 m); a
/// gantry at z 83 with one MALLET over each column swinging across it. CP2 (z 104…108).</item>
/// <item>S3 Sweep Pad (z 108…132): static disc r 9.3 m with a hub pillar, two SWEEPER arms (9 m) turning in opposite
/// directions: low (0.35 m, jump) and high (0.95 m, dive under). CP3 (z 132…136).</item>
/// <item>Mallet Run (z 136…166, a 7 m choke): bridge with 0.4 m rails under three MALLETs swinging along it
/// (z 142 / 151 / 160, head 5 m wide). No edge beams. CP4 (z 166…170).</item>
/// <item>S4 Belt Climb (z 170…200, +6 m): 10 m CONVEYOR against you with three BUMPERs covering every lane; side
/// ledges at |x| 5…7.8 (z 170…188) with a gumball lane each and a SPRING_PAD launching onto the finish deck.</item>
/// <item>Finish (z 200…213): mint deck, tall arch at z 206 (visible from the spawn view), backboard.</item>
/// </list>
/// Fall volume y &lt; −10 → respawn at the last checkpoint. Menu: Fall Rush/Setup/Build Map_BlockPartyDash ·
/// batch: <c>-executeMethod BlockPartyDashBuilder.Run</c>.
/// </summary>
public static class BlockPartyDashBuilder
{
    public const string ScenePath = ProjectSetup.ScenesDir + "/Map_BlockPartyDash.unity";
    public const float KillY = -10f;

    // ------------------------------------------------------------------ layout (also used by the bot routes)

    private const float Y1 = 5f;                     // deck height after the slope
    private const float SlopeZ0 = 14f, SlopeZ1 = 50f; // S1 Gumball Slope
    private static readonly float[] BallLaneX = { -4.8f, -2.4f, 0f, 2.4f, 4.8f };
    // Lane tuning (G4.2c log in docs/proof/G4.2-capsule-run.md): A / B release periods (ticks) and speeds (mm/s).
    private static readonly int[] BallPeriodA = { 112, 126, 104, 134, 118 };
    private static readonly int[] BallPeriodB = { 122, 108, 130, 114, 138 };
    private static readonly int[] BallSpeedA = { 3800, 4200, 3600, 4400, 4000 };
    private static readonly int[] BallSpeedB = { 4200, 3800, 4400, 3600, 4000 };

    private const float GateWallZ = 58.6f, CrossBallZ = 55.5f;
    private static readonly float[] DoorX = { -4f, -2f, 0f, 2f, 4f };

    private const float HopZ0 = 62f, HopZ1 = 120f, GantryZ = 91f; // G4.2c: 42 → 58 m (normal-bot median was 71 s)
    private const float OuterX = 4.6f;
    /// <summary>Centre column: 15 bobbing blocks, 3 m wide × 2.2 m deep, gaps 1.5625 m; block 7 sits under the gantry.</summary>
    private const int CentreCount = 15;
    private const float CentreDepth = 2.2f, CentreWidth = 3.0f;
    private static readonly float CentreGap = (HopZ1 - HopZ0 - CentreCount * CentreDepth) / (CentreCount + 1);
    private static readonly float[] CentreTop = { 0.3f, 0.6f, 0.9f, 1.2f, 0.9f, 0.6f, 0.9f, 0.6f, 0.9f, 1.2f, 0.9f, 0.6f, 0.9f, 0.6f, 0.3f };
    private const int CentreMalletBlock = 7;
    /// <summary>Outer columns: 13 static blocks (depth, gap before it, top above the deck); block 6 sits under the gantry.</summary>
    // G4.2c: 2.2 m blocks, 2.22 m gaps (first cut 2.0–2.2 m blocks with 2.3–2.4 m gaps: reference bots landed 0.1–0.3 m
    // short on the 2.4 m gaps, QA trace).
    private static readonly float[] OuterDepth = { 2.2f, 2.0f, 2.2f, 2.0f, 2.2f, 2.0f, 2.2f, 2.0f, 2.2f, 2.0f, 2.2f, 2.0f, 2.0f };
    private static readonly float[] OuterGap = { 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f, 2.2f }; // + 2.2 m to CP2
    private static readonly float[] OuterTop = { 0.2f, 0.5f, 0.8f, 0.5f, 0.8f, 0.5f, 0.6f, 0.9f, 0.6f, 0.3f, 0.6f, 0.9f, 0.5f };
    private const int OuterMalletBlock = 6;

    private const float PadZ = 135.5f, PadR = 9.3f, SweepArm = 9.0f;
    private const float RunZ0 = 152f, RunZ1 = 182f, RunHalfW = 3.5f;
    private static readonly float[] MalletZ = { 158f, 167f, 176f };

    private const float BeltZ0 = 186f, BeltZ1 = 216f, BeltRise = 6f;
    private const float LedgeX0 = 5f, LedgeX1 = 7.8f, LedgeZ1 = 204f, LedgeBallX = 6.4f, SpringZ = 200.5f;
    private static readonly Vector2[] BumperXZ = { new Vector2(-3.3f, 192.5f), new Vector2(0f, 200f), new Vector2(3.3f, 207.5f) };
    private const float BumperR = 1.7f; // with the capsule: 2 m reach each, three bumpers cover the 10 m belt
    private const float FinishY = Y1 + BeltRise, FinishZ = 222f, FinishZ1 = 229f;

    private static float SlopeY(float z) => Mathf.Clamp01((z - SlopeZ0) / (SlopeZ1 - SlopeZ0)) * Y1;
    private static float BeltY(float z) => Y1 + Mathf.Clamp01((z - BeltZ0) / (BeltZ1 - BeltZ0)) * BeltRise;
    private static float CentreZ(int i) => HopZ0 + CentreGap + CentreDepth * 0.5f + i * (CentreDepth + CentreGap);

    private static float OuterZ0(int i)
    {
        var z = HopZ0;
        for (var k = 0; k < i; k++)
            z += OuterGap[k] + OuterDepth[k];
        return z + OuterGap[i];
    }

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
        cam.transform.SetPositionAndRotation(new Vector3(0, 3.4f, -2f), Quaternion.Euler(10, 0, 0));
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
        var mallets = new List<HazardMallet>();
        var balls = new List<HazardRollingBall>();

        // ---------------- start funnel (z 0…14)
        Deck("StartDeck", -10f, 10f, 0f, 10f, 0f);
        Deck("FunnelDeck", -10f, 10f, 10f, 14f, 0f);
        foreach (var s in new[] { -1f, 1f })
        {
            // Angled rails (1.2 m, not hoppable) from the deck corners at the gate to the slope edges at z 14.
            var a = new Vector3(s * 10f, 0f, 10f);
            var b = new Vector3(s * 6.15f, 0f, 14f);
            var mid = (a + b) * 0.5f + Vector3.up * 0.6f;
            var dir = b - a;
            Box($"FunnelRail{(s < 0 ? "L" : "R")}", mid, new Vector3(0.3f, 1.2f, dir.magnitude + 0.3f), _wall, Quaternion.LookRotation(dir.normalized));
        }

        var spawnsRoot = new GameObject("Spawns").transform;
        var spawns = new Transform[Protocol.MaxPlayers];
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            var row = i / 4;
            var col = i % 4;
            var x = -6f + 4f * col + (row == 1 ? 1.5f : 0f);
            var z = 1.5f + 2.5f * row;
            var t = new GameObject($"Spawn_{i:00}").transform;
            t.SetParent(spawnsRoot, false);
            t.SetPositionAndRotation(new Vector3(x, 0.05f, z), Quaternion.identity);
            spawns[i] = t;
            KitBuilder.Visual(_geo, $"SpawnPad_{i:00}", new Vector3(x, 0.005f, z), new Vector3(1.4f, 0.01f, 1.4f), KitBuilder.Mint);
        }

        var gate = KitBuilder.GateDoor(_hz, "bpd_start_gate", new Vector3(0f, 1.25f, 10f), new Vector3(20f, 2.5f, 0.4f), 0, 10, 0, 0, 0, 0, lift: 5.2f);
        gate.def.Mode = 1;
        gate.variantB = gate.def;
        gate.hasVariantB = false;
        doors.Add(gate);

        // ---------------- S1 Gumball Slope (z 14…50, +5 m)
        Ramp("S1Slope", -6f, 6f, SlopeZ0, 0f, SlopeZ1, Y1, _walk);
        foreach (var s in new[] { -1f, 1f })
            Ramp($"S1Rail{(s < 0 ? "L" : "R")}", s * 6f - (s < 0 ? 0.3f : 0f), s * 6f + (s > 0 ? 0.3f : 0f), SlopeZ0, 0.6f, SlopeZ1, Y1 + 0.6f, _wall, 0.6f + 0.5f);
        for (var i = 0; i < BallLaneX.Length; i++)
        {
            var x = BallLaneX[i];
            const float zs = SlopeZ1 - 0.6f, ze = SlopeZ0 + 0.6f;
            var lane = KitBuilder.RollingBall(_hz, _geo, $"bpd_s1_ball_{i}", new Vector3(x, SlopeY(zs) + 1.01f, zs), new Vector3(x, SlopeY(ze) + 1.01f, ze), 1f, BallSpeedA[i], BallPeriodA[i], BallPeriodB[i]);
            lane.variantB.SpeedMmS = BallSpeedB[i];
            lane.hasVariantB = true;
            // The release telegraph is a hatch in the slope top that lights up (flush, nothing to collide with): an
            // overhead dispenser would block the spawn view of the finish arch (rule 6).
            Chute(lane, new Vector3(x, SlopeY(zs) + 0.012f, zs), new Vector3(1.9f, 0.02f, 1.9f), Quaternion.Euler(-Mathf.Atan2(Y1, SlopeZ1 - SlopeZ0) * Mathf.Rad2Deg, 0f, 0f));
            balls.Add(lane);
        }

        KitBuilder.Visual(_geo, "S1CatchGrate", new Vector3(0f, 0.008f, SlopeZ0 + 0.3f), new Vector3(12f, 0.016f, 0.5f), KitBuilder.HazardY);

        // ---------------- CP1 (z 50…54)
        Deck("CP1Deck", -6f, 6f, SlopeZ1, 54f, Y1);
        var cp1 = Checkpoint(1, new Vector3(0f, Y1 + 1.5f, 50.9f), new Vector3(12f, 4f, 1.6f), Y1, 52.0f, 53.2f, 6f, false);

        // ---------------- Wave Gates (z 54…62, 10 m choke)
        Deck("GatesDeck", -5f, 5f, 54f, HopZ0, Y1);
        foreach (var s in new[] { -1f, 1f })
            Box($"GatesPen{(s < 0 ? "L" : "R")}", new Vector3(s * 5.15f, Y1 + 0.6f, 56.15f), new Vector3(0.3f, 1.2f, 4.3f), _wall);
        // Door guides between the doors and at the ends (up to the lifted door's top), the doors themselves.
        float[] guides = { -5.1f, -3f, -1f, 1f, 3f, 5.1f };
        foreach (var gx in guides)
        {
            var w = Mathf.Abs(gx) > 5f ? 0.4f : 0.2f;
            Box($"GateGuide_{gx:+0.0;-0.0}", new Vector3(gx, Y1 + 2.1f, GateWallZ), new Vector3(w, 4.2f, 0.6f), _wall);
        }

        for (var i = 0; i < DoorX.Length; i++)
        {
            // Cycle 240 ticks (closed 165, opening 6, open 54, closing 15); offsets 48 apart: 54 > 48, so ≥ 1 door is
            // always open, and the open door travels across the wall (A left → right, B right → left).
            var d = KitBuilder.GateDoor(_hz, $"bpd_gate_door_{i}", new Vector3(DoorX[i], Y1 + 1.1f, GateWallZ), new Vector3(1.8f, 2.2f, 0.4f), 165, 6, 54, 15, i * 48, (4 - i) * 48, lift: 2.0f);
            Grille(d);
            doors.Add(d);
        }

        var xball = KitBuilder.RollingBall(_hz, _geo, "bpd_gate_xball", new Vector3(-4f, Y1 + 1.01f, CrossBallZ), new Vector3(4f, Y1 + 1.01f, CrossBallZ), 1f, 4000, 60, 54);
        xball.variantB.SpeedMmS = 4600;
        Chute(xball, new Vector3(-4.98f, Y1 + 1.2f, CrossBallZ), new Vector3(0.04f, 2.2f, 2.2f), Quaternion.identity);
        balls.Add(xball);

        // ---------------- S2 Hop Field (z 62…104, over the void)
        for (var i = 0; i < CentreCount; i++)
        {
            var top = Y1 + CentreTop[i];
            var pl = KitBuilder.LoopPlatform(_hz, $"bpd_hop_bob_{i}", new Vector3(0f, top - 0.25f, CentreZ(i)), new Vector3(CentreWidth, 0.5f, CentreDepth),
                new[] { new Vector3(0f, -0.25f, 0f), new Vector3(0f, 0.25f, 0f) }, // ±0.25 m: the next block is never > 0.8 m higher
                new[] { new Vector3(-0.8f, 0f, 0f), new Vector3(0.8f, 0f, 0f) }, 120);
            pl.visual.GetComponent<MeshRenderer>().sharedMaterial = KitBuilder.Platform;
            platforms.Add(pl);
        }

        foreach (var s in new[] { -1f, 1f })
            for (var i = 0; i < OuterDepth.Length; i++)
            {
                var z0 = OuterZ0(i);
                var top = Y1 + OuterTop[i];
                Box($"HopBlock{(s < 0 ? "L" : "R")}_{i}", new Vector3(s * OuterX, (top + Y1 - 2.5f) * 0.5f, z0 + OuterDepth[i] * 0.5f), new Vector3(2f, top - (Y1 - 2.5f), OuterDepth[i]), _block);
            }

        // Gantry with one mallet over each column, swinging across it (x). Low point: head centre 0.55 m over the
        // block top; arm 3 m, ±50° (the outer and centre heads never meet).
        // ≥ 120 ticks: a clear window still exists under TurboToys (÷ 1.2); at ≈ 105 reference bots timed out and fell.
        int[] hopA = { 122, 120, 124 };
        int[] hopB = { 128, 122, 124 };
        float[] hopX = { -OuterX, 0f, OuterX };
        var lowTop = Y1 + Mathf.Max(CentreTop[CentreMalletBlock], OuterTop[OuterMalletBlock]);
        for (var i = 0; i < 3; i++)
        {
            var id = $"bpd_hop_mallet_{i}";
            var ml = KitBuilder.Mallet(_hz, _geo, id, new Vector3(hopX[i], lowTop + 0.55f + 3f, GantryZ), 0, 3f, 50f, hopA[i], hopB[i]);
            ml.def.HeadHalfWidthMm = 1100;
            ml.variantB.HeadHalfWidthMm = 1100;
            ScaleHead(ml, 0, 1100);
            StripKitFrame(id);
            mallets.Add(ml);
        }

        Gantry("HopGantry", GantryZ, 7.2f, Y1 - 3f, lowTop + 3.75f);

        // ---------------- CP2 (z 104…108)
        Deck("CP2Deck", -6f, 6f, HopZ1, 124f, Y1);
        var cp2 = Checkpoint(2, new Vector3(0f, Y1 + 1.5f, 120.8f), new Vector3(12f, 4f, 1.6f), Y1, 122f, 123.2f, 6f, false);

        // ---------------- S3 Sweep Pad (z 108…132)
        Deck("PadInDeck", -3f, 3f, 124f, PadZ - PadR + 0.6f, Y1);
        Deck("PadOutDeck", -3f, 3f, PadZ + PadR - 0.6f, 148f, Y1);
        KitBuilder.Prism(_geo, "SweepPadDisc", new Vector3(0f, Y1, PadZ), PadR, 1f, _deck, collider: true).isStatic = true;
        var low = KitBuilder.Spinner(_hz, _geo, "bpd_sweep_low", new Vector3(0f, Y1, PadZ), SweepArm, 0.35f, 192, -192);
        var high = KitBuilder.Spinner(_hz, _geo, "bpd_sweep_high", new Vector3(0f, Y1, PadZ), SweepArm, 0.95f, -216, 216);
        sweepers.Add(low);
        sweepers.Add(high);
        foreach (var id in new[] { "bpd_sweep_low", "bpd_sweep_high" })
            DestroyGeo($"SpinnerPost_{id}");
        KitBuilder.Prism(_geo, "SweepHub", new Vector3(0f, Y1 + 1.5f, PadZ), 0.6f, 1.5f, KitBuilder.Grape, collider: true).isStatic = true;

        // ---------------- CP3 (z 132…136)
        Deck("CP3Deck", -6f, 6f, 148f, RunZ0, Y1);
        var cp3 = Checkpoint(3, new Vector3(0f, Y1 + 1.5f, 148.8f), new Vector3(12f, 4f, 1.6f), Y1, 150f, 151.2f, 6f, false);

        // ---------------- Mallet Run (z 136…166, 7 m bridge, 0.4 m rails on its edges)
        Deck("MalletBridge", -RunHalfW, RunHalfW, RunZ0, RunZ1, Y1);
        foreach (var s in new[] { -1f, 1f })
            Box($"MalletRail{(s < 0 ? "L" : "R")}", new Vector3(s * (RunHalfW - 0.125f), Y1 + 0.2f, (RunZ0 + RunZ1) * 0.5f), new Vector3(0.25f, 0.4f, RunZ1 - RunZ0 - 0.6f), _wall);
        // Head half-width 2.5 m (+ head r 0.45 + capsule r 0.31 = 3.26 m reach) covers the 6.5 m between the rails.
        // Periods ≥ 150 ticks: a 9 m pocket-to-pocket run (≈ 43 ticks through a mallet's reach) fits between two passes
        // even under TurboToys (×1.2 → ≥ 125); at 112–128 it did not (bots waited forever, players are always hit).
        int[] runA = { 180, 190, 200 };
        int[] runB = { 190, 200, 180 };
        for (var i = 0; i < MalletZ.Length; i++)
        {
            var id = $"bpd_run_mallet_{i}";
            var ml = KitBuilder.Mallet(_hz, _geo, id, new Vector3(0f, Y1 + 5.5f, MalletZ[i]), 2, 4.9f, 70f, runA[i], runB[i]);
            ml.def.HeadHalfWidthMm = 2500;
            ml.variantB.HeadHalfWidthMm = 2500;
            ScaleHead(ml, 2, 2500);
            StripKitFrame(id);
            mallets.Add(ml);
            Gantry($"RunGantry_{i}", MalletZ[i], 4.3f, Y1 - 3f, Y1 + 5.7f);
        }

        // ---------------- CP4 (z 166…170; widened to 16 m so the side ledges connect)
        Deck("CP4Deck", -LedgeX1, LedgeX1, RunZ1, BeltZ0, Y1);
        var cp4 = Checkpoint(4, new Vector3(0f, Y1 + 1.5f, 182.6f), new Vector3(15.6f, 4f, 1.2f), Y1, 183.8f, 184.8f, 7.5f, false);

        // ---------------- S4 Belt Climb (z 170…200, +6 m)
        var bPitch = Mathf.Atan2(BeltRise, BeltZ1 - BeltZ0) * Mathf.Rad2Deg;
        var bRot = Quaternion.Euler(-bPitch, 0f, 0f);
        var bUp = bRot * Vector3.up;
        var bDir = bRot * Vector3.forward;
        var bLen = Mathf.Sqrt((BeltZ1 - BeltZ0) * (BeltZ1 - BeltZ0) + BeltRise * BeltRise);
        var bTop = new Vector3(0f, Y1 + BeltRise * 0.5f, (BeltZ0 + BeltZ1) * 0.5f);
        conveyors.Add(KitBuilder.Conveyor(_hz, "bpd_belt", bTop - bUp * 0.25f, new Vector3(10f, 0.5f, bLen), -bDir * 0.7f, bPitch));
        for (var i = 0; i < BumperXZ.Length; i++)
        {
            var b = BumperXZ[i];
            bumpers.Add(KitBuilder.Bumper(_hz, $"bpd_belt_bumper_{i}", new Vector3(b.x, BeltY(b.y) - 0.3f, b.y), BumperR, 1.6f));
        }

        // Belt edge walls (0.6 m above the belt, down below the ledges): the ledge is its own lane (a bot dodging a ledge ball
        // stepped onto the belt or under it, QA), and past the ledges there is void beside the belt.
        foreach (var s in new[] { -1f, 1f })
            Ramp($"BeltRail{(s < 0 ? "L" : "R")}", s * 5f - (s < 0 ? 0.25f : 0f), s * 5f + (s > 0 ? 0.25f : 0f), BeltZ0, BeltY(BeltZ0) + 0.6f, BeltZ1, BeltY(BeltZ1) + 0.6f, _wall, 5.2f);
        foreach (var s in new[] { -1f, 1f })
        {
            var side = s < 0 ? "L" : "R";
            Deck($"Ledge{side}", s < 0 ? -LedgeX1 : LedgeX0, s < 0 ? -LedgeX0 : LedgeX1, BeltZ0, LedgeZ1, Y1);
            Box($"LedgeRail{side}", new Vector3(s * (LedgeX1 - 0.125f), Y1 + 0.3f, (BeltZ0 + SpringZ - 1f) * 0.5f), new Vector3(0.25f, 0.6f, SpringZ - 1f - BeltZ0 - 0.4f), _wall);
            // Gumball dispenser at the ledge's end (solid: a missed pad stops you here instead of in the void).
            Box($"LedgeDispenser{side}", new Vector3(s * (LedgeX0 + LedgeX1) * 0.5f, Y1 + 1.3f, LedgeZ1 + 0.6f), new Vector3(LedgeX1 - LedgeX0, 2.6f, 1.2f), KitBuilder.Grape);
            var lane = KitBuilder.RollingBall(_hz, _geo, $"bpd_ledge_ball_{side}", new Vector3(s * LedgeBallX, Y1 + 1.01f, LedgeZ1 - 0.4f), new Vector3(s * LedgeBallX, Y1 + 1.01f, BeltZ0 + 0.6f), 1f, 5200, 232, 222);
            lane.variantB.SpeedMmS = 5600; // faster than a run: a mistimed dash up the ledge is caught
            Chute(lane, new Vector3(s * LedgeBallX, Y1 + 1.2f, LedgeZ1 - 0.02f), new Vector3(2.2f, 2.2f, 0.04f), Quaternion.identity);
            balls.Add(lane);
            // Launch (∓1.8, 19, 11.8) m/s: clears the dispenser, apex ≈ 9 m over the pad, lands ≈ (±3.8, 11, 201.4) on
            // the finish deck (MoonBounce ≈ z 210.7: the backboard stops an overshoot).
            pads.Add(KitBuilder.SpringPad(_hz, $"bpd_spring_{side}", new Vector3(s * LedgeBallX, Y1 + 0.1f, SpringZ), new Vector2(2.4f, 2.0f), new Vector3(-s * 1.8f, 19f, 11.8f)));
        }

        // ---------------- Finish (z 200…213)
        Box("FinishDeck", new Vector3(0f, FinishY - 0.5f, (BeltZ1 + FinishZ1) * 0.5f), new Vector3(14f, 1f, FinishZ1 - BeltZ1), KitBuilder.Mint);
        Box("FinishBackboard", new Vector3(0f, FinishY + 1.25f, FinishZ1 + 0.2f), new Vector3(14f, 2.5f, 0.4f), _wall);
        var finish = Checkpoint(5, new Vector3(0f, FinishY + 5f, FinishZ), new Vector3(14f, 10f, 2f), FinishY, 224.5f, 226.5f, 6f, true);
        Arch();

        // ---------------- MapInfo, definition, course line, bot routes
        var info = new GameObject("MapInfo").AddComponent<MapInfo>();
        var def = SandboxBuilder.MapDefinitionAsset("BlockPartyDash", MapPoolV1.BlockPartyDash, "Race", KillY, new[] { 0, 1, 2, 3, 5 });
        def.checkpointCount = 5;
        def.targetDuration = new Vector2Int(85, 110);
        def.timerCapSeconds = 150;
        def.introSeconds = 3f;
        def.countdownSeconds = 3f;
        def.minPlayers = 1;
        EditorUtility.SetDirty(def);
        info.definition = def;
        info.spawns = spawns;
        info.botAreaCenter = new Vector3(0f, 0f, 4f);
        info.botAreaHalfExtents = new Vector2(8f, 3f);
        info.playerMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/PlayerPlaceholder.mat");
        info.unlitMaterial = AssetDatabase.LoadAssetAtPath<Material>(SandboxBuilder.MaterialsDir + "/CharacterUnlit.mat");
        info.sweepers = sweepers.ToArray();
        info.platforms = platforms.ToArray();
        info.conveyors = conveyors.ToArray();
        info.pads = pads.ToArray();
        info.doors = doors.ToArray();
        info.bumpers = bumpers.ToArray();
        info.turnTables = new HazardTurnTable[0];
        info.mallets = mallets.ToArray();
        info.rollingBalls = balls.ToArray();
        info.courseLine = new[]
        {
            new Vector3(0, 0, 0), new Vector3(0, 0, SlopeZ0), new Vector3(0, Y1, SlopeZ1), new Vector3(0, Y1, HopZ1), new Vector3(0, Y1, RunZ0),
            new Vector3(0, Y1, BeltZ0), new Vector3(0, FinishY, BeltZ1), new Vector3(0, FinishY, FinishZ),
        };
        var cps = new[] { cp1, cp2, cp3, cp4, finish };
        foreach (var cp in cps)
            cp.courseDistance = CourseDistance(info.courseLine, cp.transform.position);
        info.checkpoints = cps;
        info.botRoutes = Routes();

        // Fall volume / bounds marker (visual only, far below).
        KitBuilder.Visual(_geo, "FallVolumeMarker", new Vector3(0f, KillY, 114f), new Vector3(60f, 0.02f, 256f), KitBuilder.Mint).GetComponent<MeshRenderer>().enabled = false;

        MapArt.Apply(scene, info, "BlockPartyDash"); // G2: toon look, toy blocks, merged hazards, sky, dressing
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
        Debug.Log($"[setup] built {ScenePath} (Capsule Run) mapHash={Sha256.Hex(hash)} hazards: {balls.Count} ball lane, {doors.Count} door (incl. start gate), {platforms.Count} platform, {mallets.Count} mallet, {sweepers.Count} sweeper, {conveyors.Count} conveyor, {bumpers.Count} bumper, {pads.Count} pad; " +
                  $"checkpoints at {string.Join(", ", System.Array.ConvertAll(cps, x => x.courseDistance.ToString("F1")))} m; allowlist problems: {problems.Count} {string.Join("; ", problems)}");
    }

    // ------------------------------------------------------------------ pieces

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

    /// <summary>A pitched box whose top runs from (z0, y0) to (z1, y1) across x0…x1.</summary>
    private static void Ramp(string name, float x0, float x1, float z0, float y0, float z1, float y1, Material mat, float thick = 0.5f)
    {
        var len = Mathf.Sqrt((z1 - z0) * (z1 - z0) + (y1 - y0) * (y1 - y0));
        var rot = Quaternion.Euler(-Mathf.Atan2(y1 - y0, z1 - z0) * Mathf.Rad2Deg, 0f, 0f);
        var top = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        Box(name, top - rot * Vector3.up * (thick * 0.5f), new Vector3(x1 - x0, thick, len), mat, rot);
    }

    private static void DestroyGeo(string name)
    {
        var t = _geo.Find(name);
        if (t != null)
            Object.DestroyImmediate(t.gameObject);
    }

    /// <summary>The kit mallet frame puts its posts next to the axle (on the course here): replaced by a gantry.</summary>
    private static void StripKitFrame(string id)
    {
        DestroyGeo($"MalletPostA_{id}");
        DestroyGeo($"MalletPostB_{id}");
        DestroyGeo($"MalletBeam_{id}");
    }

    /// <summary>The kit head is 1.5 m long across the swing: stretch it to the def's half-width.</summary>
    private static void ScaleHead(HazardMallet ml, int axis, int halfWidthMm)
    {
        var head = ml.visual.Find("Head");
        if (head != null)
            head.localScale = axis == 2 ? new Vector3(halfWidthMm / 750f, 1f, 1f) : new Vector3(1f, 1f, halfWidthMm / 750f);
    }

    /// <summary>Two posts at ±<paramref name="halfX"/> (from <paramref name="yBottom"/>) and a beam at <paramref name="yBeam"/> across z.</summary>
    private static void Gantry(string name, float z, float halfX, float yBottom, float yBeam)
    {
        foreach (var s in new[] { -1f, 1f })
            Box($"{name}Post{(s < 0 ? "L" : "R")}", new Vector3(s * halfX, (yBottom + yBeam + 0.2f) * 0.5f, z), new Vector3(0.4f, yBeam + 0.2f - yBottom, 0.4f), KitBuilder.Grape);
        Box($"{name}Beam", new Vector3(0f, yBeam, z), new Vector3(halfX * 2f + 0.4f, 0.4f, 0.4f), KitBuilder.Grape);
    }

    /// <summary>Puts a ball lane's release light (telegraph) where it belongs on this map (a hatch, a wall panel).</summary>
    private static void Chute(HazardRollingBall lane, Vector3 pos, Vector3 size, Quaternion rot)
    {
        if (lane.chute == null)
            return;
        var t = lane.chute.transform;
        t.SetPositionAndRotation(pos, rot);
        t.localScale = size;
    }

    /// <summary>
    /// Gate doors as see-through grilles (rule 6: the finish arch stays visible through the Wave Gates from the spawn).
    /// The collider is the whole doorway (no gap a capsule fits through: bars 0.27 m apart, rails 0.5 m apart).
    /// </summary>
    private static void Grille(HazardGateDoor d)
    {
        var vis = d.visual.gameObject;
        Object.DestroyImmediate(vis.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(vis.GetComponent<MeshFilter>());
        for (var i = 0; i < 5; i++)
        {
            var bar = KitBuilder.LocalVisual(d.visual, $"Bar_{i}", new Vector3(-0.4f + i * 0.2f, 0f, 0f), Vector3.one, KitBuilder.Door);
            bar.transform.localScale = new Vector3(0.07f, 1f, 0.6f);
        }
    }

    /// <summary>Tall inflatable-style finish arch over the deck (greybox: two fat posts and a rounded top), all solid.</summary>
    private static void Arch()
    {
        const float half = 6.3f, postTop = FinishY + 8f;
        foreach (var s in new[] { -1f, 1f })
        {
            Box($"ArchPost{(s < 0 ? "L" : "R")}", new Vector3(s * half, (FinishY + postTop) * 0.5f, FinishZ), new Vector3(1.4f, postTop - FinishY, 1.4f), KitBuilder.Mint);
            for (var b = 0; b < 3; b++)
                Box($"ArchBand{(s < 0 ? "L" : "R")}_{b}", new Vector3(s * half, FinishY + 1.6f + b * 2.4f, FinishZ), new Vector3(1.5f, 0.4f, 1.5f), KitBuilder.Grape);
        }

        const int n = 9;
        for (var i = 0; i < n; i++)
        {
            var a = Mathf.PI * (i + 0.5f) / n;
            var p = new Vector3(-Mathf.Cos(a) * half, postTop + Mathf.Sin(a) * 2.6f, FinishZ);
            var tangent = new Vector3(Mathf.Sin(a) * half, Mathf.Cos(a) * 2.6f, 0f).normalized;
            Box($"ArchTop_{i}", p, new Vector3(1.9f, 1.4f, 1.4f), i % 2 == 0 ? KitBuilder.Checker : KitBuilder.Mint, Quaternion.LookRotation(Vector3.forward, Vector3.Cross(Vector3.forward, tangent)));
        }
    }

    /// <summary>Checkpoint trigger (no collider) + 12 respawn spots in two rows at <paramref name="row1"/>/<paramref name="row2"/> within ±<paramref name="halfW"/>, facing +Z; a mint stripe on the floor.</summary>
    private static RaceCheckpoint Checkpoint(int index, Vector3 centre, Vector3 size, float floorY, float row1, float row2, float halfW, bool finish)
    {
        var go = new GameObject(finish ? "Finish" : $"Checkpoint_{index}");
        go.transform.position = centre;
        var cp = go.AddComponent<RaceCheckpoint>();
        cp.index = index;
        cp.triggerSize = size;
        cp.isFinish = finish;
        var spots = new Transform[12];
        var step = (halfW * 2f - 2f) / 5f;
        for (var i = 0; i < 12; i++)
        {
            var t = new GameObject($"Respawn_{i:00}").transform;
            t.SetParent(go.transform, false);
            t.SetPositionAndRotation(new Vector3(-halfW + 1f + step * (i % 6) + (i < 6 ? 0f : step * 0.5f) - step * 0.25f, floorY + 0.05f, i < 6 ? row1 : row2), Quaternion.identity);
            spots[i] = t;
        }

        cp.respawns = spots;
        KitBuilder.Visual(_geo, $"CheckpointStripe_{index}", new Vector3(0f, floorY + 0.006f, centre.z), new Vector3(size.x, 0.012f, 0.6f), finish ? KitBuilder.Checker : KitBuilder.Mint);
        if (!finish)
        {
            KitBuilder.Box(_geo, $"CPPostL_{index}", new Vector3(-size.x * 0.5f - 0.3f, floorY + 1.5f, centre.z), new Vector3(0.5f, 3f, 0.5f), KitBuilder.Mint);
            KitBuilder.Box(_geo, $"CPPostR_{index}", new Vector3(size.x * 0.5f + 0.3f, floorY + 1.5f, centre.z), new Vector3(0.5f, 3f, 0.5f), KitBuilder.Mint);
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

    /// <summary>
    /// Routes per section (section = checkpoints reached). WaitClear's Arg = how many waypoints ahead the clear check
    /// covers at running speed (bars, mallets and balls); the ball-dodge layer of <see cref="WaypointBot"/> handles the
    /// slope and the ledges between waypoints.
    /// </summary>
    public static BotRoute[] Routes()
    {
        var r = new List<BotRoute>();

        // Section 0: start → CP1 up the Gumball Slope, between the ball lanes (dodging by timing).
        foreach (var x in new[] { -2.4f, -1.2f, 1.2f, 2.4f })
        {
            var p = new List<Waypoint> { W(x * 0.6f, 0, 9f, WaypointAction.Run, 0, 0.8f), W(x, 0, 13.4f, WaypointAction.Run, 0, 0.8f) };
            for (var z = 20f; z < SlopeZ1; z += 7f)
                p.Add(W(x, SlopeY(z), z, WaypointAction.Run, 0, 0.8f));
            p.Add(W(x * 0.5f, Y1, 52.2f, WaypointAction.Run, 0, 1.0f));
            r.Add(new BotRoute { Section = 0, Name = $"S1-slope{(x < 0 ? "L" : "R")}{Mathf.Abs(x):0.0}", Points = p.ToArray() });
        }

        // Section 1: CP1 → CP2: a Wave Gates door (1…5, door index 1…5; 0 is the start gate) × a Hop Field column.
        for (var door = 0; door < DoorX.Length; door++)
        {
            var dx = DoorX[door];
            Waypoint[] Gates() => new[]
            {
                W(dx * 0.9f, Y1, 52.6f, WaypointAction.Run, 0, 0.8f),
                W(dx, Y1, 53.4f, WaypointAction.WaitClear, 1, 0.5f), // the cross ball: the run to the door is clear
                W(dx, Y1, GateWallZ - 0.95f, WaypointAction.Door, door + 1, 0.35f),
                W(dx, Y1, GateWallZ + 1.0f, WaypointAction.Run, 0, 0.6f),
            };
            // The columns within reach of this door (≤ 4.6 m sideways): a diagonal across the whole deck is not a line
            // anyone takes (it was the only route over 1.25×, QA).
            if (Mathf.Abs(dx - 0f) <= OuterX)
                r.Add(new BotRoute { Section = 1, Name = $"gate{door + 1}+hopC", Points = Join(Gates(), HopCentre()) });
            if (Mathf.Abs(dx + OuterX) <= OuterX)
                r.Add(new BotRoute { Section = 1, Name = $"gate{door + 1}+hopL", Points = Join(Gates(), HopOuter(-1f)) });
            if (Mathf.Abs(dx - OuterX) <= OuterX)
                r.Add(new BotRoute { Section = 1, Name = $"gate{door + 1}+hopR", Points = Join(Gates(), HopOuter(1f)) });
        }

        // Section 2: CP2 → CP3 across the Sweep Pad, left or right of the hub (the bar reflex jumps / dives).
        foreach (var s in new[] { -1f, 1f })
            r.Add(new BotRoute
            {
                Section = 2, Name = $"sweep-{(s < 0 ? "left" : "right")}",
                Points = new[]
                {
                    W(s * 1.0f, Y1, 123f, WaypointAction.Run, 0, 0.8f), W(s * 2.0f, Y1, PadZ - PadR + 1.0f, WaypointAction.Run, 0, 0.8f),
                    W(s * 4.3f, Y1, PadZ - 5.2f, WaypointAction.Run, 0, 0.9f), W(s * 5.0f, Y1, PadZ, WaypointAction.Run, 0, 0.9f),
                    W(s * 4.3f, Y1, PadZ + 5.2f, WaypointAction.Run, 0, 0.9f), W(s * 2.0f, Y1, PadZ + PadR - 1.0f, WaypointAction.Run, 0, 0.8f),
                    W(0f, Y1, 149.6f, WaypointAction.Run, 0, 1.0f),
                },
            });

        // Section 3: CP3 → CP4 under the mallets: wait in the pocket before each mallet until the run to the next pocket is clear.
        var run = new List<Waypoint> { W(0f, Y1, RunZ0 + 0.2f, WaypointAction.Run, 0, 0.8f), W(0f, Y1, MalletZ[0] - 4.4f, WaypointAction.WaitClear, 1, 0.6f) };
        for (var i = 0; i + 1 < MalletZ.Length; i++)
            run.Add(W(0f, Y1, (MalletZ[i] + MalletZ[i + 1]) * 0.5f, WaypointAction.WaitClear, 1, 0.6f));
        run.Add(W(0f, Y1, RunZ1 - 1.4f, WaypointAction.Run, 0, 0.8f));
        run.Add(W(0f, Y1, 183.9f, WaypointAction.Run, 0, 1.0f));
        r.Add(new BotRoute { Section = 3, Name = "mallet-run", Points = run.ToArray() });

        // Section 4: CP4 → finish: the belt (left / centre / right, round one bumper each) or a ledge spring.
        r.Add(new BotRoute
        {
            Section = 4, Name = "belt-left",
            Points = new[]
            {
                W(-3.3f, Y1, 184.9f, WaypointAction.Run, 0, 0.8f), W(-3.3f, BeltY(188.5f), 188.5f), W(-1.0f, BeltY(BumperXZ[0].y), BumperXZ[0].y),
                W(-3.3f, BeltY(197f), 197f), W(-3.3f, BeltY(213f), 213f, WaypointAction.Run, 0, 1.0f), W(-1.5f, FinishY, 223.8f, WaypointAction.Run, 0, 1.0f),
            },
        });
        r.Add(new BotRoute
        {
            Section = 4, Name = "belt-centre",
            Points = new[]
            {
                W(0f, Y1, 184.9f, WaypointAction.Run, 0, 0.8f), W(0f, BeltY(195.5f), 195.5f), W(-2.2f, BeltY(BumperXZ[1].y), BumperXZ[1].y),
                W(0f, BeltY(204.5f), 204.5f), W(0f, BeltY(213f), 213f, WaypointAction.Run, 0, 1.0f), W(0f, FinishY, 223.8f, WaypointAction.Run, 0, 1.0f),
            },
        });
        r.Add(new BotRoute
        {
            Section = 4, Name = "belt-right",
            Points = new[]
            {
                W(3.3f, Y1, 184.9f, WaypointAction.Run, 0, 0.8f), W(3.3f, BeltY(202.5f), 202.5f), W(1.0f, BeltY(BumperXZ[2].y), BumperXZ[2].y),
                W(3.3f, BeltY(212f), 212f, WaypointAction.Run, 0, 1.0f), W(1.5f, FinishY, 223.8f, WaypointAction.Run, 0, 1.0f),
            },
        });
        foreach (var s in new[] { -1f, 1f })
            r.Add(new BotRoute
            {
                Section = 4, Name = $"spring-{(s < 0 ? "left" : "right")}",
                Points = new[]
                {
                    W(s * 4.6f, Y1, 183.9f, WaypointAction.Run, 0, 0.8f),
                    W(s * LedgeBallX, Y1, 184.7f, WaypointAction.WaitClear, 1, 0.5f), // the ledge ball: the run to the pad is clear
                    // Reached at the pad's back edge: a bot launched before reaching its pad point steered back at it in the
                    // air and air control braked the launch (QA trace: landed short beside the belt).
                    W(s * LedgeBallX, Y1 + 0.1f, SpringZ - 0.8f, WaypointAction.Run, 0, 1.0f),
                    W(s * 3.6f, FinishY, 218.5f, WaypointAction.Run, 0, 1.8f),
                    W(s * 1.0f, FinishY, 223.8f, WaypointAction.Run, 0, 1.0f),
                },
            });
        return r.ToArray();
    }

    private static Waypoint[] Join(Waypoint[] a, Waypoint[] b)
    {
        var j = new Waypoint[a.Length + b.Length];
        a.CopyTo(j, 0);
        b.CopyTo(j, a.Length);
        return j;
    }

    /// <summary>Hop Field centre: board each bobbing block in turn; on the block before the gantry wait until the next two blocks are clear of the mallet.</summary>
    private static Waypoint[] HopCentre()
    {
        // Line up first (from a door we would take off moving sideways and miss the block).
        var p = new List<Waypoint> { W(0f, Y1, HopZ0 - 1.6f, WaypointAction.Run, 0, 0.4f), W(0f, Y1, HopZ0 - 0.35f, WaypointAction.JumpToPlatform, 0, 0.4f) };
        for (var i = 0; i < CentreCount; i++)
        {
            var top = Y1 + CentreTop[i];
            p.Add(W(0f, top, CentreZ(i), WaypointAction.Platform, i));
            if (i == CentreMalletBlock - 1)
                p.Add(W(0f, top, CentreZ(i) - 0.75f, WaypointAction.WaitClear, 4, 0.6f)); // at the back: a run-up for the hop
            // Take off 0.55 m past the middle (a running hop of ≈ 3.1 m lands on the next block's middle). From the front
            // edge it landed 1.4 m deep at full speed and slid off; from the middle the landing of the previous hop was
            // already past the take-off circle (QA traces).
            p.Add(i + 1 < CentreCount
                ? W(0f, top, CentreZ(i) + 0.55f, WaypointAction.JumpToPlatform, i + 1, 0.4f)
                : W(0f, top, CentreZ(i) + CentreDepth * 0.5f - 0.3f, WaypointAction.Jump, 0, 0.3f));
        }

        p.Add(W(0f, Y1, HopZ1 + 1.2f, WaypointAction.Run, 0, 1.0f));
        p.Add(W(0f, Y1, 122.4f, WaypointAction.Run, 0, 1.0f));
        return p.ToArray();
    }

    /// <summary>Hop Field outer column (static blocks, real jumps): land, jump at the front edge; before the gantry block wait until it is clear.</summary>
    private static Waypoint[] HopOuter(float s)
    {
        var x = s * OuterX;
        // Line up on the column first (coming from a door we would take off moving sideways and fall short).
        var p = new List<Waypoint> { W(x, Y1, HopZ0 - 1.7f, WaypointAction.Run, 0, 0.4f), W(x, Y1, HopZ0 - 0.25f, WaypointAction.Jump, 0, 0.25f) };
        for (var i = 0; i < OuterDepth.Length; i++)
        {
            var z0 = OuterZ0(i);
            var top = Y1 + OuterTop[i];
            p.Add(W(x, top, z0 + Mathf.Min(1.0f, OuterDepth[i] * 0.5f), WaypointAction.Land, 0, 0.8f));
            if (i == OuterMalletBlock - 1)
                p.Add(W(x, top, z0 + OuterDepth[i] * 0.5f, WaypointAction.WaitClear, 3, 0.8f));
            p.Add(W(x, top, z0 + OuterDepth[i] - 0.25f, WaypointAction.Jump, 0, 0.25f));
        }

        p.Add(W(x * 0.8f, Y1, HopZ1 + 1.2f, WaypointAction.Run, 0, 1.0f));
        p.Add(W(x * 0.4f, Y1, 122.4f, WaypointAction.Run, 0, 1.0f));
        return p.ToArray();
    }
}
