using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G3.4 signature moment 3, the claw (doc 17 §4): when the round result names a player eliminated, their Rushling
    /// turns into a grey capsule and a giant claw-machine claw comes down, grabs it and lifts it out of the round.
    /// Client-side presentation only, driven by the match state (the eliminated players of this round); the simulation
    /// never sees it. One merged mesh per claw (1 renderer); at most 4 claws at once, staggered.
    /// </summary>
    public sealed class ClawPresenter : MonoBehaviour
    {
        private const float Descend = 0.65f, Grab = 0.3f, Lift = 1.1f, Height = 14f;

        private sealed class Pluck
        {
            public int Slot;
            public float Start;
            public Vector3 At;
            public Transform Claw;
            public bool Sounded;
        }

        private readonly List<Pluck> _plucks = new List<Pluck>();
        private readonly bool[] _grey = new bool[Protocol.MaxPlayers];
        private readonly Stack<Transform> _pool = new Stack<Transform>();
        private MatchPhase _phase = (MatchPhase)255;
        private byte _round;
        private Mesh _mesh;
        private Material _mat;

        public bool IsGrey(int slot) => slot >= 0 && slot < _grey.Length && _grey[slot];

        /// <summary>While the claw holds <paramref name="slot"/>, the Rushling's feet follow the claw.</summary>
        public bool Holding(int slot, out Vector3 feet)
        {
            foreach (var p in _plucks)
            {
                if (p.Slot != slot)
                    continue;
                var t = Time.unscaledTime - p.Start;
                if (t < Descend + Grab * 0.6f)
                    break;
                var u = Mathf.Clamp01((t - Descend - Grab * 0.6f) / Lift);
                feet = p.At + Vector3.up * (u * u * Height * 1.3f);
                return true;
            }

            feet = default;
            return false;
        }

        private void Update()
        {
            var cs = ClientSim.Instance;
            var m = cs != null ? cs.Match : null;
            if (m == null)
                return;
            if (m.Phase != _phase || m.Round != _round)
            {
                var entered = m.Phase == MatchPhase.RoundResult && (_phase != MatchPhase.RoundResult || m.Round != _round);
                _phase = m.Phase;
                _round = m.Round;
                if (m.Phase != MatchPhase.RoundResult)
                    Clear();
                if (entered)
                    Schedule(cs, m);
            }

            var now = Time.unscaledTime;
            foreach (var p in _plucks)
            {
                var t = now - p.Start;
                if (t < 0f)
                    continue;
                if (p.Claw == null)
                {
                    p.Claw = Take();
                    _grey[p.Slot] = true;
                }

                if (!p.Sounded)
                {
                    p.Sounded = true;
                    GameAudio.Play(GameAudio.Sfx.Claw, p.At, 0.9f);
                }

                // descend → grab (close) → lift (with the capsule)
                float y, close;
                if (t < Descend)
                {
                    var u = t / Descend;
                    y = Mathf.Lerp(Height, 1.25f, 1f - (1f - u) * (1f - u));
                    close = 0f;
                }
                else if (t < Descend + Grab)
                {
                    y = 1.25f;
                    close = (t - Descend) / Grab;
                }
                else
                {
                    var u = Mathf.Clamp01((t - Descend - Grab) / Lift);
                    y = 1.25f + u * u * Height * 1.3f;
                    close = 1f;
                }

                p.Claw.position = p.At + Vector3.up * y;
                var spread = Mathf.Lerp(1.25f, 0.82f, close);
                p.Claw.localScale = new Vector3(spread, 1f, spread);
                p.Claw.gameObject.SetActive(t < Descend + Grab + Lift + 0.2f);
            }
        }

        private void Schedule(ClientSim cs, MatchStateMessage m)
        {
            var k = 0;
            foreach (var s in m.Slots)
            {
                if (!s.Has(MatchSlotInfo.Rostered) || s.Has(MatchSlotInfo.Alive) || s.EliminatedRound != m.Round)
                    continue;
                if (!cs.TrySample(s.Slot, out var feet, out _, out var flags, out _, out _) || (flags & MotorState.Respawning) != 0)
                    continue; // already gone (fell out / gooed): no claw
                _grey[s.Slot] = true;
                _plucks.Add(new Pluck { Slot = s.Slot, Start = Time.unscaledTime + 0.9f + (k % 4) * 0.35f + (k / 4) * 1.9f, At = feet });
                k++;
            }
        }

        private void Clear()
        {
            foreach (var p in _plucks)
                if (p.Claw != null)
                {
                    p.Claw.gameObject.SetActive(false);
                    _pool.Push(p.Claw);
                }

            _plucks.Clear();
            for (var i = 0; i < _grey.Length; i++)
                _grey[i] = false;
        }

        private Transform Take()
        {
            if (_pool.Count > 0)
            {
                var t = _pool.Pop();
                t.gameObject.SetActive(true);
                return t;
            }

            _mesh ??= BuildMesh();
            if (_mat == null)
            {
                var src = Resources.Load<Material>("FallRush/Character");
                _mat = src != null ? new Material(src) : new Material(Shader.Find("FallRush/Toon"));
                _mat.SetFloat("_RushlingMesh", 0f);
                _mat.SetFloat("_Gloss", 0.8f);
            }

            var go = new GameObject("Claw");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        /// <summary>Our own claw: a grape hub with a yellow band, a long cable, three magenta prongs with mint tips.</summary>
        private static Mesh BuildMesh()
        {
            var c = new RushlingParts.Combiner();
            var grape = UiKit.Grape;
            c.Add(RushlingParts.Shapes.Cylinder12, Matrix4x4.TRS(new Vector3(0f, 0.55f, 0f), Quaternion.identity, new Vector3(0.9f, 0.22f, 0.9f)), grape, RushlingParts.FlagNone);
            c.Add(RushlingParts.Shapes.Cylinder12, Matrix4x4.TRS(new Vector3(0f, 0.55f, 0f), Quaternion.identity, new Vector3(0.96f, 0.06f, 0.96f)), UiKit.SunYellow, RushlingParts.FlagNoHull);
            c.Add(RushlingParts.Shapes.Puff, Matrix4x4.TRS(new Vector3(0f, 0.8f, 0f), Quaternion.identity, new Vector3(0.7f, 0.45f, 0.7f)), grape, RushlingParts.FlagNone);
            c.Add(RushlingParts.Shapes.Cylinder8, Matrix4x4.TRS(new Vector3(0f, 16f, 0f), Quaternion.identity, new Vector3(0.09f, 15.2f, 0.09f)), UiKit.Ink, RushlingParts.FlagNoHull);
            for (var i = 0; i < 3; i++)
            {
                var rot = Quaternion.Euler(0f, i * 120f, 0f);
                // upper prong angled out, lower prong curling in (the hub scale closes them)
                c.Add(RushlingParts.Shapes.Box, Matrix4x4.TRS(rot * new Vector3(0f, 0.15f, 0.42f), rot * Quaternion.Euler(-25f, 0f, 0f), new Vector3(0.14f, 0.75f, 0.14f)), UiKit.Magenta, RushlingParts.FlagNone);
                c.Add(RushlingParts.Shapes.Box, Matrix4x4.TRS(rot * new Vector3(0f, -0.38f, 0.5f), rot * Quaternion.Euler(25f, 0f, 0f), new Vector3(0.13f, 0.55f, 0.13f)), UiKit.Magenta, RushlingParts.FlagNone);
                c.Add(RushlingParts.Shapes.Puff, Matrix4x4.TRS(rot * new Vector3(0f, -0.66f, 0.36f), Quaternion.identity, Vector3.one * 0.2f), UiKit.Mint, RushlingParts.FlagNoHull);
            }

            return c.Build("Claw");
        }
    }
}
