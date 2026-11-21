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

            for (var i = 0; i < _map.sweepers.Length && i < hz.Sweepers.Length; i++)
            {
                var sw = _map.sweepers[i];
                if (sw != null && sw.barVisual != null)
                    sw.barVisual.rotation = Quaternion.Euler(0f, HazardMath.SweeperYawDegrees(in hz.Sweepers[i], t), 0f);
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
