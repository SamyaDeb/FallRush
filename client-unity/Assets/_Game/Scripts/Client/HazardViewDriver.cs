using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// Client-only hazard visuals and telegraphs (doc 08 §1.3), driven by the same pose functions the motor collides with,
    /// at the local predicted hazard time (fractional ticks, so motion is smooth at any frame rate).
    /// <list type="bullet">
    /// <item>Spinner bars and turn tables rotate; platforms and doors move (interpolated between whole ticks).</item>
    /// <item>Doors flash magenta for <see cref="GateDoorDef.TelegraphTicks"/> before they start closing, and while closing.</item>
    /// <item>Mallet heads glow during their wind-up (<see cref="HazardMath.MalletWindup"/>), before each pass through the bottom.</item>
    /// <item>Conveyor chevrons light up in a travelling wave in the belt direction (speed-true).</item>
    /// <item>G4.1a rolling balls are placed and spun from the pure lane pose; the chute light flashes before each release.</item>
    /// <item>G4.1b lids: lit = yellow pulse, flipped = a 180° flip over 6 ticks (and back), retire warn = blinking, retired = drop away.
        /// G4.5: grids built without per-lid visuals are drawn instanced by <see cref="LidGridView"/> (hinge swing, yellow, magenta pulse).</item>
    /// </list>
    /// No colliders are touched here: only visual transforms and material property blocks.
    /// </summary>
    public sealed class HazardViewDriver
    {
        private readonly MapInfo _map;
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        // G2: hazard visual groups are merged into one vertex-coloured mesh each (MapArt); the telegraphs drive the
        // toon shader's flash mask (_FlashColor, vertex alpha marks the part) and the belts' travelling chevron light
        // (_Scroll / _ChevronCount, uv2.x numbers the chevrons).
        private static readonly int FlashId = Shader.PropertyToID("_FlashColor");
        private static readonly int ScrollId = Shader.PropertyToID("_Scroll");
        private static readonly int ChevronCountId = Shader.PropertyToID("_ChevronCount");
        private readonly Renderer[][] _doorRenderers;
        private readonly Renderer[] _malletHeads;
        private readonly Renderer[] _chevrons;
        private readonly int[] _chevronCount;
        private readonly float[] _padSquash;
        private readonly float[] _bumperHit;
        private static readonly Color Flash = UiKit.Magenta;
        private static readonly Color Glow = new Color(1f, 0.95f, 0.6f);

        public HazardViewDriver(MapInfo map)
        {
            _map = map;
            _doorRenderers = new Renderer[map.doors.Length][];
            for (var i = 0; i < map.doors.Length; i++)
                _doorRenderers[i] = map.doors[i] != null && map.doors[i].visual != null ? new[] { map.doors[i].visual.GetComponent<Renderer>() } : Array.Empty<Renderer>();
            _malletHeads = new Renderer[map.mallets.Length];
            for (var i = 0; i < map.mallets.Length; i++)
            {
                _malletHeads[i] = map.mallets[i] != null && map.mallets[i].visual != null ? map.mallets[i].visual.GetComponent<Renderer>() : null;
            }

            _bumperHit = new float[map.bumpers.Length];
            for (var i = 0; i < _bumperHit.Length; i++)
                _bumperHit[i] = -1f;
            _padSquash = new float[map.pads.Length];
            for (var i = 0; i < _padSquash.Length; i++)
                _padSquash[i] = -1f;
            _chevrons = new Renderer[map.conveyors.Length];
            _chevronCount = new int[map.conveyors.Length];
            for (var i = 0; i < map.conveyors.Length; i++)
            {
                var r = map.conveyors[i] != null && map.conveyors[i].visual != null ? map.conveyors[i].visual.GetComponent<Renderer>() : null;
                _chevrons[i] = r;
                var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mf != null && mf.sharedMesh != null)
                {
                    // Count the chevron pairs once (uv2.x = 1..n); runs at map load, not per frame.
                    var uvs = new List<Vector2>();
                    mf.sharedMesh.GetUVs(1, uvs);
                    var max = 0f;
                    foreach (var u in uvs)
                        max = Mathf.Max(max, u.x);
                    _chevronCount[i] = Mathf.RoundToInt(max);
                }
            }
        }

        /// <summary>G3.3 spring-pad squash: the pad nearest a launch (within 2.5 m) bounces its arrows (visual only).</summary>
        public void SquashPadNear(Vector3 feet)
        {
            for (var i = 0; i < _map.pads.Length; i++)
            {
                var pd = _map.pads[i];
                if (pd != null && pd.visual != null && (pd.visual.position - feet).sqrMagnitude < 2.5f * 2.5f)
                    _padSquash[i] = Time.unscaledTime;
            }
        }

        /// <summary>G4.9 squash on hit: the full squash of a padded bumper (12 %), and with reduced motion (4 %, no wobble).</summary>
        public const float BumperSquash = 0.12f, BumperSquashReduced = 0.04f, BumperWobbleSecs = 0.4f;

        /// <summary>
        /// G4.9: a stun right next to a bumper (within its radius + 1.2 m, at its height) squashes the pillow and wobbles it
        /// back over 0.4 s. Client-only: the bumper's render transform (its visual root); the collider is a separate object.
        /// </summary>
        public void SquashBumperNear(Vector3 feet)
        {
            for (var i = 0; i < _map.bumpers.Length; i++)
            {
                var bm = _map.bumpers[i];
                if (bm == null || bm.visual == null)
                    continue;
                var d = feet - bm.visual.position;
                var r = bm.def.RadiusMm / 1000f + 1.2f;
                if (d.x * d.x + d.z * d.z < r * r && d.y > -1.5f && d.y < bm.def.HeightMm / 1000f + 1.5f)
                    _bumperHit[i] = Time.unscaledTime;
            }
        }

        /// <summary>The bumper's render scale <paramref name="secs"/> after a hit (squash down, bulge out, wobble back).</summary>
        public static Vector3 BumperScale(float secs, bool reduced)
        {
            var u = secs / BumperWobbleSecs;
            if (u >= 1f || u < 0f)
                return Vector3.one;
            var s = reduced ? BumperSquashReduced * (1f - u) * (1f - u) : BumperSquash * Mathf.Cos(u * Mathf.PI * 3f) * Mathf.Pow(1f - u, 1.5f);
            return new Vector3(1f + 0.6f * s, 1f - s, 1f + 0.6f * s);
        }

        private void BumperSquashUpdate(bool reduced)
        {
            for (var i = 0; i < _bumperHit.Length; i++)
            {
                if (_bumperHit[i] < 0f || _map.bumpers[i] == null || _map.bumpers[i].visual == null)
                    continue;
                var secs = Time.unscaledTime - _bumperHit[i];
                _map.bumpers[i].visual.localScale = BumperScale(secs, reduced);
                if (secs >= BumperWobbleSecs)
                    _bumperHit[i] = -1f;
            }
        }

        private void PadSquash(bool reduced)
        {
            for (var i = 0; i < _padSquash.Length; i++)
            {
                if (_padSquash[i] < 0f || _map.pads[i] == null || _map.pads[i].visual == null)
                    continue;
                var u = (Time.unscaledTime - _padSquash[i]) / 0.45f;
                var v = _map.pads[i].visual;
                if (u >= 1f || reduced)
                {
                    v.localScale = Vector3.one;
                    _padSquash[i] = -1f;
                    continue;
                }

                var k = Mathf.Sin(u * Mathf.PI * 2.5f) * (1f - u);
                v.localScale = new Vector3(1f + 0.35f * k, 1f - 0.5f * k, 1f + 0.35f * k);
            }
        }

        /// <summary>Poses every hazard visual at hazard time <paramref name="t"/> (ticks since the round start, fractional).</summary>
        public void Update(HazardSet hz, double t, bool reducedMotion)
        {
            if (hz == null)
                return;
            var t0 = (int)Math.Floor(t);
            var f = (float)(t - t0);
            PadSquash(reducedMotion);
            BumperSquashUpdate(reducedMotion);

            for (var i = 0; i < _map.sweepers.Length && i < hz.Sweepers.Length; i++)
            {
                var sw = _map.sweepers[i];
                if (sw != null && sw.barVisual != null)
                    sw.barVisual.rotation = Quaternion.Euler(0f, HazardMath.SweeperYawDegrees(in hz.Sweepers[i], t + ChaosDropFx.SweeperWarpTicks(t + hz.RoundStartTick)), 0f); // P4 Sweeper Rush
            }

            for (var i = 0; i < _map.platforms.Length && i < hz.Platforms.Length; i++)
            {
                var pl = _map.platforms[i];
                if (pl == null || pl.visual == null)
                    continue;
                var a = HazardMath.PlatformCenter(in hz.Platforms[i], t0);
                var b = HazardMath.PlatformCenter(in hz.Platforms[i], t0 + 1);
                pl.visual.position = new Vector3(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
            }

            for (var i = 0; i < _map.turnTables.Length && i < hz.TurnTables.Length; i++)
            {
                var tt = _map.turnTables[i];
                if (tt != null && tt.visual != null)
                    tt.visual.rotation = Quaternion.Euler(0f, HazardMath.TurnTableYawDegrees(in hz.TurnTables[i], t), 0f);
            }

            for (var i = 0; i < _map.doors.Length && i < hz.Doors.Length; i++)
            {
                var dr = _map.doors[i];
                if (dr == null || dr.visual == null)
                    continue;
                ref readonly var d = ref hz.Doors[i];
                var l0 = HazardMath.DoorLiftMm(in d, t0);
                var l1 = HazardMath.DoorLiftMm(in d, t0 + 1);
                var lift = (l0 + (l1 - l0) * f) * 0.001f;
                dr.visual.position = new Vector3(d.CenterXMm * 0.001f, d.CenterYMm * 0.001f + lift, d.CenterZMm * 0.001f);
                // Telegraph: flash before closing and while closing (the stripes stay; the body pulses).
                var until = HazardMath.DoorTicksUntilClosing(in d, t0);
                var closingNow = l1 < l0;
                var warn = closingNow || (until > 0 && until <= d.TelegraphTicks);
                var on = warn && (reducedMotion || ((int)(t * 0.5) & 1) == 0);
                SetFlash(_doorRenderers[i], new Color(Flash.r, Flash.g, Flash.b, on ? 1f : 0f));
            }

            for (var i = 0; i < _map.mallets.Length && i < hz.Mallets.Length; i++)
            {
                var ml = _map.mallets[i];
                if (ml == null || ml.visual == null)
                    continue;
                ref readonly var d = ref hz.Mallets[i];
                var period = d.PeriodTicks == 0 ? 1.0 : d.PeriodTicks;
                var turns = t / period + d.Phase16 / (double)HazardMath.PhaseOne;
                var deg = d.AmplitudePhase16 / (double)HazardMath.PhaseOne * 360.0 * Math.Sin(turns * 2.0 * Math.PI);
                ml.visual.localRotation = d.SwingAxis == 0 ? Quaternion.Euler(0f, 0f, (float)deg) : Quaternion.Euler((float)-deg, 0f, 0f);
                if (_malletHeads[i] != null)
                {
                    var w = HazardMath.MalletWindup(in d, t);
                    _mpb.Clear();
                    _mpb.SetColor(FlashId, new Color(Glow.r, Glow.g, Glow.b, w));
                    _malletHeads[i].SetPropertyBlock(_mpb);
                }
            }

            UpdateBalls(hz, t);
            UpdateLids(hz, t, reducedMotion);

            for (var i = 0; i < _map.conveyors.Length && i < hz.Conveyors.Length; i++)
            {
                var ch = _chevrons[i];
                var pairs = _chevronCount[i];
                if (ch == null || pairs < 1)
                    continue;
                ref readonly var d = ref hz.Conveyors[i];
                var speed = Math.Sqrt((double)d.BeltXMms * d.BeltXMms + (double)d.BeltYMms * d.BeltYMms + (double)d.BeltZMms * d.BeltZMms) * 0.001;
                // Chevron pairs are laid out from the belt's start to its end; the lit pair travels with the belt (~1.5 m spacing).
                var wave = t / 30.0 * speed / 1.5;
                _mpb.Clear();
                _mpb.SetFloat(ScrollId, (float)(wave % pairs));
                _mpb.SetFloat(ChevronCountId, pairs);
                ch.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>G4.1a: ball visuals at the lane pose (fractional ticks), rolled by the distance travelled; unused visuals hidden.</summary>
        private void UpdateBalls(HazardSet hz, double t)
        {
            var lanes = _map.rollingBalls;
            if (lanes == null)
                return;
            for (var i = 0; i < lanes.Length && i < hz.RollingBalls.Length; i++)
            {
                var lane = lanes[i];
                if (lane == null)
                    continue;
                ref readonly var d = ref hz.RollingBalls[i];
                var axis = Vector3.Cross(Vector3.up, new Vector3(d.EndXMm - d.StartXMm, 0f, d.EndZMm - d.StartZMm)).normalized;
                if (axis.sqrMagnitude < 0.5f)
                    axis = Vector3.right;
                var r = Mathf.Max(0.1f, d.RadiusMm * 0.001f);
                for (var j = 0; j < lane.balls.Length; j++)
                {
                    var b = lane.balls[j];
                    if (b == null)
                        continue;
                    var alive = HazardMath.BallRenderPos(in d, t, j, out var x, out var y, out var z, out var rolled);
                    if (b.gameObject.activeSelf != alive)
                        b.gameObject.SetActive(alive);
                    if (!alive)
                        continue;
                    b.position = new Vector3((float)x, (float)y, (float)z);
                    b.rotation = Quaternion.AngleAxis((float)(rolled / r * Mathf.Rad2Deg), axis);
                }

                if (lane.chute != null)
                {
                    var w = HazardMath.RollingBallTelegraph(in d, t);
                    _mpb.Clear();
                    _mpb.SetColor(FlashId, new Color(Glow.r, Glow.g, Glow.b, w));
                    lane.chute.SetPropertyBlock(_mpb);
                }
            }
        }

        private LidGridView _lidView;

        /// <summary>G4.5: the instanced lid view (null until a map with instanced lid grids is posed). Editor shots snapshot it.</summary>
        public LidGridView LidView => _lidView;

        /// <summary>False in Editor shots (they snapshot the posed lids into temporary renderers instead).</summary>
        public bool DrawInstanced = true;

        private LidState[] _lidStates = Array.Empty<LidState>();
        private int[] _lidScratch = Array.Empty<int>();
        private Renderer[][] _lidRenderers;
        private static readonly Color Lemon = new Color(1f, 0.83f, 0.23f);

        /// <summary>G4.1b: lid visuals from the pure lid states at the hazard tick (the colliders follow the same states in the motor world).</summary>
        private void UpdateLids(HazardSet hz, double t, bool reduced)
        {
            var grids = _map.lidGrids;
            if (grids == null || grids.Length == 0)
                return;
            if (_lidRenderers == null)
            {
                _lidRenderers = new Renderer[grids.Length][];
                for (var g = 0; g < grids.Length; g++)
                {
                    var vs = grids[g] != null ? grids[g].visuals : Array.Empty<Transform>();
                    _lidRenderers[g] = new Renderer[vs.Length];
                    for (var i = 0; i < vs.Length; i++)
                        _lidRenderers[g][i] = vs[i] != null ? vs[i].GetComponentInChildren<Renderer>() : null;
                }
            }

            var t0 = (int)Math.Floor(t);
            var f = (float)(t - t0);
            // G4.5: grids without per-lid visuals (Lid Flip) are drawn instanced, one batch per state colour.
            if (Array.Exists(grids, LidGridView.Instanced))
            {
                _lidView ??= new LidGridView(_map);
                _lidView.Pose(hz, t, reduced);
                if (DrawInstanced)
                    _lidView.Draw();
            }

            for (var g = 0; g < grids.Length && g < hz.LidGrids.Length; g++)
            {
                var grid = grids[g];
                if (grid == null || LidGridView.Instanced(grid))
                    continue;
                ref readonly var d = ref hz.LidGrids[g];
                if (_lidStates.Length < d.Count)
                {
                    _lidStates = new LidState[d.Count];
                    _lidScratch = new int[d.Count];
                }

                HazardMath.LidStates(in d, t0, _lidStates, _lidScratch);
                for (var i = 0; i < grid.visuals.Length && i < d.Count; i++)
                {
                    var v = grid.visuals[i];
                    if (v == null)
                        continue;
                    d.LidCenterMm(i, out var x, out var y, out var z);
                    var st = _lidStates[i];
                    var pos = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
                    var angle = 0f;
                    var flash = new Color(Lemon.r, Lemon.g, Lemon.b, 0f);
                    var visible = true;
                    switch (st)
                    {
                        case LidState.Lit:
                            flash.a = reduced ? 1f : 0.55f + 0.45f * Mathf.Sin((float)t * 0.7f);
                            break;
                        case LidState.Flipped:
                        {
                            // 180° over the first 6 ticks of the flipped window, and back over its last 6.
                            var into = HazardMath.LidFlippedFor(in d, i, t0, _lidScratch) + f;
                            var left = d.FlippedTicks - into;
                            var u = Mathf.Clamp01(Mathf.Min((float)into, (float)left) / 6f);
                            angle = 180f * (reduced ? (u > 0.5f ? 1f : 0f) : u);
                            break;
                        }
                        case LidState.RetireWarn:
                            flash = new Color(Flash.r, Flash.g, Flash.b, reduced || ((int)(t * 0.25) & 1) == 0 ? 1f : 0f);
                            break;
                        case LidState.Retired:
                        {
                            var since = (float)(HazardMath.LidLocalT(in d, t0) + f - d.RetireTick(i)) / 30f;
                            pos.y -= 0.5f * 20f * since * since;
                            visible = since < 1.5f;
                            break;
                        }
                    }

                    if (v.gameObject.activeSelf != visible)
                        v.gameObject.SetActive(visible);
                    if (!visible)
                        continue;
                    v.position = pos;
                    v.rotation = Quaternion.Euler(angle, 0f, 0f);
                    var r = _lidRenderers[g].Length > i ? _lidRenderers[g][i] : null;
                    if (r != null)
                    {
                        _mpb.Clear();
                        _mpb.SetColor(FlashId, flash);
                        r.SetPropertyBlock(_mpb);
                    }
                }
            }
        }

        private void SetFlash(Renderer[] rs, Color c)
        {
            _mpb.Clear();
            _mpb.SetColor(FlashId, c);
            foreach (var r in rs)
                if (r != null)
                    r.SetPropertyBlock(_mpb);
        }
    }
}
