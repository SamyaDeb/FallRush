using UnityEngine;

namespace FallRush.Client
{
    /// <summary>The UI icon set (P7 D-GLYPH): every icon a card or chip shows. Never rely on emoji fonts in WebGL.</summary>
    public enum UiIcon : byte
    {
        None = 0, Crown, Swirl, Lock, Warning, Capsule, Check, Cross, Race, Survival, Skill, Final, Chip, Link, Eye, Trophy, Bolt, Ring,
    }

    /// <summary>
    /// P7 A1.6: a small UI icon atlas drawn in code (one 384 × 384 texture, 6 × 6 cells of 64 px), white on transparent so an
    /// Image tints it. Shapes are anti-aliased signed-distance unions of circles, rounded boxes, capsules and polygons; the
    /// crown is our own (a band with five rounded capsule prongs and ball tips, doc 17 §8.4), no copied icon.
    /// </summary>
    public static class UiIcons
    {
        private const int Cell = 64, Cols = 6;
        private static Sprite[] s_Sprites;

        public static Sprite Get(UiIcon icon)
        {
            if (icon == UiIcon.None)
                return null;
            s_Sprites ??= Build();
            return s_Sprites[(int)icon];
        }

        /// <summary>The icon for a round mode ("Race" / "Survival" / "Skill" / "Final").</summary>
        public static UiIcon ForMode(string mode) => mode switch
        {
            "Race" => UiIcon.Race,
            "Survival" => UiIcon.Survival,
            "Skill" => UiIcon.Skill,
            "Final" => UiIcon.Final,
            _ => UiIcon.Capsule,
        };

        private static Sprite[] Build()
        {
            var n = System.Enum.GetValues(typeof(UiIcon)).Length;
            var rows = (n + Cols - 1) / Cols;
            var tex = new Texture2D(Cols * Cell, rows * Cell, TextureFormat.RGBA32, false) { name = "UiIcons", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[tex.width * tex.height];
            var sprites = new Sprite[n];
            for (var i = 1; i < n; i++)
            {
                var cx = i % Cols * Cell;
                var cy = i / Cols * Cell;
                for (var y = 0; y < Cell; y++)
                    for (var x = 0; x < Cell; x++)
                    {
                        var p = new Vector2((x + 0.5f) / Cell, (y + 0.5f) / Cell);
                        var d = Sdf((UiIcon)i, p);
                        var a = Mathf.Clamp01(0.5f - d * Cell); // 1 px anti-aliasing
                        px[(cy + y) * tex.width + cx + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                    }

                sprites[i] = Sprite.Create(tex, new Rect(cx, cy, Cell, Cell), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                sprites[i].name = "Icon" + (UiIcon)i;
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return sprites;
        }

        // ------------------------------------------------------------------ shapes (unit square, y up; < 0 inside)

        /// <summary>Signed distance of icon <paramref name="i"/> at p (public for the EditMode coverage test).</summary>
        public static float Sdf(UiIcon i, Vector2 p)
        {
            switch (i)
            {
                case UiIcon.Check:
                    return Mathf.Min(Seg(p, new Vector2(0.18f, 0.52f), new Vector2(0.41f, 0.28f)), Seg(p, new Vector2(0.41f, 0.28f), new Vector2(0.83f, 0.74f))) - 0.085f;
                case UiIcon.Cross:
                    return Mathf.Min(Seg(p, new Vector2(0.24f, 0.24f), new Vector2(0.76f, 0.76f)), Seg(p, new Vector2(0.24f, 0.76f), new Vector2(0.76f, 0.24f))) - 0.085f;
                case UiIcon.Lock:
                {
                    var body = Box(p, new Vector2(0.5f, 0.36f), new Vector2(0.28f, 0.22f), 0.07f);
                    var ring = Mathf.Abs(Circle(p, new Vector2(0.5f, 0.6f), 0.17f)) - 0.06f;
                    ring = Mathf.Max(ring, 0.56f - p.y);
                    var legs = Mathf.Min(Seg(p, new Vector2(0.33f, 0.6f), new Vector2(0.33f, 0.5f)), Seg(p, new Vector2(0.67f, 0.6f), new Vector2(0.67f, 0.5f))) - 0.06f;
                    var hole = Mathf.Min(Circle(p, new Vector2(0.5f, 0.4f), 0.06f), Seg(p, new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.27f)) - 0.03f);
                    return Mathf.Max(Mathf.Min(body, Mathf.Min(ring, legs)), -hole);
                }
                case UiIcon.Warning:
                {
                    var tri = Poly(p, new Vector2(0.5f, 0.9f), new Vector2(0.08f, 0.13f), new Vector2(0.92f, 0.13f)) - 0.04f;
                    var bar = Seg(p, new Vector2(0.5f, 0.64f), new Vector2(0.5f, 0.42f)) - 0.055f;
                    var dot = Circle(p, new Vector2(0.5f, 0.27f), 0.06f);
                    return Mathf.Max(tri, -Mathf.Min(bar, dot));
                }
                case UiIcon.Capsule:
                {
                    var c = Box(p, new Vector2(0.5f, 0.5f), new Vector2(0.24f, 0.38f), 0.24f);
                    var seam = Mathf.Abs(p.y - 0.5f) - 0.03f;
                    return Mathf.Max(c, -seam);
                }
                case UiIcon.Crown:
                {
                    var band = Box(p, new Vector2(0.5f, 0.27f), new Vector2(0.34f, 0.09f), 0.04f);
                    var d = band;
                    for (var k = 0; k < 5; k++)
                    {
                        var x = 0.18f + k * 0.16f;
                        var top = k == 2 ? 0.74f : k % 2 == 1 ? 0.62f : 0.66f;
                        d = Mathf.Min(d, Seg(p, new Vector2(x, 0.3f), new Vector2(x, top)) - 0.055f);
                        d = Mathf.Min(d, Circle(p, new Vector2(x, top + 0.045f), 0.07f));
                    }

                    return d;
                }
                case UiIcon.Swirl:
                {
                    var q = p - new Vector2(0.5f, 0.5f);
                    var r = q.magnitude;
                    var th = Mathf.Atan2(q.y, q.x);
                    if (th < 0f)
                        th += Mathf.PI * 2f;
                    var best = 9f;
                    for (var k = 0; k < 3; k++)
                    {
                        var rs = 0.05f + 0.055f * (th + k * Mathf.PI * 2f);
                        if (rs > 0.42f)
                            continue;
                        best = Mathf.Min(best, Mathf.Abs(r - rs));
                    }

                    return Mathf.Min(best - 0.045f, Circle(p, new Vector2(0.5f, 0.5f), 0.07f));
                }
                case UiIcon.Race:
                {
                    var pole = Seg(p, new Vector2(0.24f, 0.1f), new Vector2(0.24f, 0.88f)) - 0.05f;
                    var flag = Poly(p, new Vector2(0.27f, 0.88f), new Vector2(0.84f, 0.72f), new Vector2(0.27f, 0.52f)) - 0.03f;
                    return Mathf.Min(pole, flag);
                }
                case UiIcon.Survival:
                {
                    var head = Poly(p, new Vector2(0.5f, 0.9f), new Vector2(0.17f, 0.52f), new Vector2(0.83f, 0.52f)) - 0.03f;
                    var stem = Seg(p, new Vector2(0.5f, 0.55f), new Vector2(0.5f, 0.32f)) - 0.1f;
                    var goo = Box(p, new Vector2(0.5f, 0.14f), new Vector2(0.36f, 0.05f), 0.05f);
                    return Mathf.Min(Mathf.Min(head, stem), goo);
                }
                case UiIcon.Skill:
                    return Star(p, new Vector2(0.5f, 0.47f), 0.42f, 0.19f) - 0.025f;
                case UiIcon.Final:
                {
                    var d = 9f;
                    for (var k = 0; k < 4; k++)
                        d = Mathf.Min(d, Box(p, new Vector2(k % 2 == 0 ? 0.3f : 0.7f, k < 2 ? 0.3f : 0.7f), new Vector2(0.16f, 0.16f), 0.06f));
                    return d;
                }
                case UiIcon.Chip:
                {
                    var outer = Hex(p, new Vector2(0.5f, 0.5f), 0.42f) - 0.02f;
                    var ring = Mathf.Max(outer, -Hex(p, new Vector2(0.5f, 0.5f), 0.26f));
                    return Mathf.Min(ring, Hex(p, new Vector2(0.5f, 0.5f), 0.15f));
                }
                case UiIcon.Link:
                {
                    var shaft = Seg(p, new Vector2(0.24f, 0.24f), new Vector2(0.68f, 0.68f)) - 0.065f;
                    var head = Poly(p, new Vector2(0.82f, 0.82f), new Vector2(0.42f, 0.82f), new Vector2(0.82f, 0.42f)) - 0.02f;
                    return Mathf.Min(shaft, head);
                }
                case UiIcon.Eye:
                {
                    var almond = Mathf.Max(Circle(p, new Vector2(0.5f, 0.12f), 0.56f), Circle(p, new Vector2(0.5f, 0.88f), 0.56f));
                    var pupil = Circle(p, new Vector2(0.5f, 0.5f), 0.15f);
                    var ring = Circle(p, new Vector2(0.5f, 0.5f), 0.22f);
                    return Mathf.Min(Mathf.Max(almond, -ring), pupil);
                }
                case UiIcon.Trophy:
                {
                    var cup = Box(p, new Vector2(0.5f, 0.66f), new Vector2(0.24f, 0.2f), 0.18f);
                    cup = Mathf.Max(cup, p.y - 0.86f);
                    var handles = Mathf.Min(Mathf.Abs(Circle(p, new Vector2(0.24f, 0.68f), 0.1f)) - 0.035f, Mathf.Abs(Circle(p, new Vector2(0.76f, 0.68f), 0.1f)) - 0.035f);
                    var stem = Seg(p, new Vector2(0.5f, 0.45f), new Vector2(0.5f, 0.22f)) - 0.05f;
                    var foot = Box(p, new Vector2(0.5f, 0.15f), new Vector2(0.2f, 0.05f), 0.04f);
                    return Mathf.Min(Mathf.Min(cup, handles), Mathf.Min(stem, foot));
                }
                case UiIcon.Bolt:
                    return Mathf.Min(Poly(p, new Vector2(0.6f, 0.92f), new Vector2(0.24f, 0.46f), new Vector2(0.52f, 0.46f)), Poly(p, new Vector2(0.48f, 0.54f), new Vector2(0.76f, 0.54f), new Vector2(0.4f, 0.08f))) - 0.02f;
                case UiIcon.Ring:
                    return Mathf.Abs(Circle(p, new Vector2(0.5f, 0.5f), 0.4f)) - 0.07f;
                default:
                    return 1f;
            }
        }

        private static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        private static float Box(Vector2 p, Vector2 c, Vector2 half, float r)
        {
            var q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half + new Vector2(r, r);
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        private static float Seg(Vector2 p, Vector2 a, Vector2 b)
        {
            var pa = p - a;
            var ba = b - a;
            var h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude;
        }

        /// <summary>Exact signed distance to a polygon (any winding, convex or not).</summary>
        private static float Poly(Vector2 p, params Vector2[] v)
        {
            var d = Vector2.Dot(p - v[0], p - v[0]);
            var s = 1f;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
            {
                var e = v[j] - v[i];
                var w = p - v[i];
                var b = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Vector2.Dot(e, e));
                d = Mathf.Min(d, Vector2.Dot(b, b));
                var c1 = p.y >= v[i].y;
                var c2 = p.y < v[j].y;
                var c3 = e.x * w.y > e.y * w.x;
                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3))
                    s = -s;
            }

            return s * Mathf.Sqrt(d);
        }

        private static readonly Vector2[] s_Star = new Vector2[10];

        private static float Star(Vector2 p, Vector2 c, float ro, float ri)
        {
            for (var k = 0; k < 10; k++)
            {
                var a = Mathf.PI * 0.5f + k * Mathf.PI / 5f;
                var r = k % 2 == 0 ? ro : ri;
                s_Star[k] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }

            return Poly(p, s_Star);
        }

        private static readonly Vector2[] s_Hex = new Vector2[6];

        private static float Hex(Vector2 p, Vector2 c, float r)
        {
            for (var k = 0; k < 6; k++)
            {
                var a = Mathf.PI / 6f + k * Mathf.PI / 3f;
                s_Hex[k] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }

            return Poly(p, s_Hex);
        }
    }
}
