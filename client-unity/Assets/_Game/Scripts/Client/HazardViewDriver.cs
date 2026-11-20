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
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private readonly Renderer[][] _doorRenderers;
        private readonly Renderer[] _malletHeads;
        private readonly Renderer[][] _chevrons;
        private static readonly Color DoorColor = new Color(0.97f, 0.88f, 0.55f);
        private static readonly Color Flash = UiKit.Magenta;
        private static readonly Color HeadColor = UiKit.Magenta;
        private static readonly Color Glow = new Color(1f, 0.95f, 0.6f);
        private static readonly Color Chevron = new Color(0.96f, 0.97f, 1f);
        private static readonly Color ChevronLit = UiKit.Mint;

        public HazardViewDriver(MapInfo map)
        {
            _map = map;
            _doorRenderers = new Renderer[map.doors.Length][];
            for (var i = 0; i < map.doors.Length; i++)
                _doorRenderers[i] = map.doors[i] != null && map.doors[i].visual != null ? new[] { map.doors[i].visual.GetComponent<Renderer>() } : Array.Empty<Renderer>();
            _malletHeads = new Renderer[map.mallets.Length];
            for (var i = 0; i < map.mallets.Length; i++)
            {
                var head = map.mallets[i] != null && map.mallets[i].visual != null ? map.mallets[i].visual.Find("Head/HeadBody") : null;
                _malletHeads[i] = head != null ? head.GetComponent<Renderer>() : null;
            }

            _chevrons = new Renderer[map.conveyors.Length][];
            for (var i = 0; i < map.conveyors.Length; i++)
            {
                var list = new List<Renderer>();
                if (map.conveyors[i] != null && map.conveyors[i].visual != null)
                    foreach (Transform c in map.conveyors[i].visual)
                        list.Add(c.GetComponent<Renderer>());
                _chevrons[i] = list.ToArray();
            }
        }

        /// <summary>Poses every hazard visual at hazard time <paramref name="t"/> (ticks since the round start, fractional).</summary>
        public void Update(HazardSet hz, double t, bool reducedMotion)
        {
            if (hz == null)
                return;
            var t0 = (int)Math.Floor(t);
            var f = (float)(t - t0);

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
                SetColor(_doorRenderers[i], on ? Flash : DoorColor);
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
                    _mpb.SetColor(BaseColor, Color.Lerp(HeadColor, Glow, w));
                    _malletHeads[i].SetPropertyBlock(_mpb);
                }
            }

            for (var i = 0; i < _map.conveyors.Length && i < hz.Conveyors.Length; i++)
            {
                var ch = _chevrons[i];
                if (ch.Length < 2)
                    continue;
                ref readonly var d = ref hz.Conveyors[i];
                var speed = Math.Sqrt((double)d.BeltXMms * d.BeltXMms + (double)d.BeltYMms * d.BeltYMms + (double)d.BeltZMms * d.BeltZMms) * 0.001;
                // Chevrons come in L/R pairs, laid out from the belt's start to its end; the lit pair travels with the belt.
                var pairs = ch.Length / 2;
                var wave = t / 30.0 * speed / 1.5; // ~1.5 m spacing
                for (var p = 0; p < pairs; p++)
                {
                    var lit = (((wave - p) % pairs) + pairs) % pairs < 1.0;
                    _mpb.SetColor(BaseColor, lit ? ChevronLit : Chevron);
                    ch[p * 2].SetPropertyBlock(_mpb);
                    ch[p * 2 + 1].SetPropertyBlock(_mpb);
                }
            }
        }

        private void SetColor(Renderer[] rs, Color c)
        {
            _mpb.SetColor(BaseColor, c);
            foreach (var r in rs)
                if (r != null)
                    r.SetPropertyBlock(_mpb);
        }
    }
}
