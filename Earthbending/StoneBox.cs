using System;
using System.Collections.Generic;

// Unity-free, compiled by Earthbending.Tests, so only System.* in here. A verbatim copy of the four types UvRect,
// StoneUv, StoneMesh and StoneBox from BluesArcheryOverhaul/Lectern.cs (the shrine tablet's chamfered stone blocks);
// only the namespace differs. The archery mod is a separate assembly, so the code is copied, not shared: keep the two
// in step by hand if either ever changes. StoneLook.cs turns a StoneMesh into a Unity mesh (Pillar, Cube, Wall).
namespace Earthbending {
    /// <summary>A rectangle of the texture: (U0, V0) bottom-left, (U1, V1) top-right. U may run outside 0..1 (the atlas repeats sideways).</summary>
    internal readonly struct UvRect {
        public readonly float U0, V0, U1, V1;
        public UvRect(float u0, float v0, float u1, float v1) { U0 = u0; V0 = v0; U1 = u1; V1 = v1; }
    }

    /// <summary>Bands of the game's ruins atlas (ruins_02_diffuse, 2048 x 2048), by pixel row from the top.</summary>
    internal static class StoneUv {
        public const int Atlas = 2048;
        public const int MeanderTop = 1000, MeanderBottom = 1200;   // the carved meander, lit by the emissive map
        public const int SlateTop = 1345, SlateBottom = 1555;       // the smooth slate (the writing surface)
        public const int BrickTop = 1700, BrickBottom = 1900;       // the small brick row
        const float MaxSpan = 8f;

        /// <summary>The band's rows over a face of this size: v covers the rows, u is centred on <paramref name="centreU"/>
        /// and as wide as keeps the stone's proportions.</summary>
        public static UvRect Band(int rowTop, int rowBottom, float width, float height, float centreU = 0.5f) {
            float rows = (rowBottom - rowTop) / (float)Atlas;
            float span = height > 1e-5f && width > 0f ? width / height * rows : rows;
            if (float.IsNaN(span) || float.IsInfinity(span) || span <= 0f) span = rows;
            if (span > MaxSpan) span = MaxSpan;
            return new UvRect(centreU - span * 0.5f, 1f - rowBottom / (float)Atlas, centreU + span * 0.5f, 1f - rowTop / (float)Atlas);
        }

        public static UvRect Meander(float width, float height, float centreU = 0.5f) => Band(MeanderTop, MeanderBottom, width, height, centreU);
        public static UvRect Slate(float width, float height, float centreU = 0.5f) => Band(SlateTop, SlateBottom, width, height, centreU);
        public static UvRect Bricks(float width, float height, float centreU = 0.5f) => Band(BrickTop, BrickBottom, width, height, centreU);
    }

    /// <summary>A mesh as plain arrays: 3 floats per vertex and normal, 4 per tangent (w = -1), 2 per UV.</summary>
    internal sealed class StoneMesh {
        public float[] Vertices = new float[0], Normals = new float[0], Tangents = new float[0], Uvs = new float[0];
        public int[] Triangles = new int[0];
        public int VertexCount => Vertices.Length / 3;
        public int TriangleCount => Triangles.Length / 3;
    }

    /// <summary>A box centred on the origin with all twelve edges chamfered: 6 faces, 12 edge strips, 8 corner
    /// triangles, flat-shaded (no vertex is shared between faces). Front faces are wound as Unity wants them
    /// (cross(b - a, c - a) along the normal). Each face takes a UV rectangle, in the order +X, -X, +Y, -Y, +Z, -Z;
    /// u runs to the right and v up as seen from outside (top: v to +Z; bottom: v to -Z). A chamfer continues the
    /// flat mapping of one of its faces (Z faces first, then X, then Y), so the stone runs over the bevel.</summary>
    internal static class StoneBox {
        public const int Faces = 6;
        public const int BevelledVertices = 96, BevelledTriangles = 44, PlainVertices = 24, PlainTriangles = 12;
        const float MaxBevelShare = 0.49f, MinSize = 1e-4f;

        //                                  +X  -X  +Y  -Y  +Z  -Z
        static readonly int[] UAxis = { 2, 2, 0, 0, 0, 0 };
        static readonly int[] USign = { 1, -1, 1, 1, -1, 1 };
        static readonly int[] VAxis = { 1, 1, 2, 2, 1, 1 };
        static readonly int[] VSign = { 1, 1, 1, -1, 1, 1 };

        public static int Face(int axis, int sign) => axis * 2 + (sign > 0 ? 0 : 1);

        /// <summary>The bevel actually cut: never negative, never NaN, under half the smallest side.</summary>
        public static float Bevel(float sx, float sy, float sz, float bevel) {
            if (float.IsNaN(bevel) || bevel <= 0f) return 0f;
            float min = Math.Min(Clean(sx), Math.Min(Clean(sy), Clean(sz)));
            return Math.Min(bevel, min * MaxBevelShare);
        }

        static float Clean(float size) => float.IsNaN(size) || float.IsInfinity(size) || size < MinSize ? MinSize : size;

        public static StoneMesh Build(float sx, float sy, float sz, float bevel, UvRect[] faceUv) {
            var h = new[] { Clean(sx) * 0.5f, Clean(sy) * 0.5f, Clean(sz) * 0.5f };
            float b = Bevel(sx, sy, sz, bevel);
            var w = new Writer(h, faceUv);
            var n = new float[3];
            var p = new float[4][];
            for (int i = 0; i < 4; i++) p[i] = new float[3];

            for (int f = 0; f < Faces; f++) {
                int a = f / 2, s = f % 2 == 0 ? 1 : -1, o1 = (a + 1) % 3, o2 = (a + 2) % 3;
                for (int k = 0; k < 4; k++) {
                    int c1 = k < 2 ? -1 : 1, c2 = k == 1 || k == 2 ? 1 : -1;
                    p[k][a] = s * h[a]; p[k][o1] = c1 * (h[o1] - b); p[k][o2] = c2 * (h[o2] - b);
                }
                n[0] = n[1] = n[2] = 0f; n[a] = s;
                w.Add(p, 4, n, f);
            }
            if (b > 0f) {
                float r2 = (float)(1.0 / Math.Sqrt(2.0)), r3 = (float)(1.0 / Math.Sqrt(3.0));
                for (int a1 = 0; a1 < 3; a1++)
                    for (int a2 = a1 + 1; a2 < 3; a2++) {
                        int a3 = 3 - a1 - a2;
                        for (int s1 = -1; s1 <= 1; s1 += 2)
                            for (int s2 = -1; s2 <= 1; s2 += 2) {
                                for (int k = 0; k < 4; k++) {
                                    bool onFirst = k < 2;                       // two points on face a1, then two on face a2
                                    int along = k == 1 || k == 2 ? 1 : -1;
                                    p[k][a1] = s1 * (onFirst ? h[a1] : h[a1] - b);
                                    p[k][a2] = s2 * (onFirst ? h[a2] - b : h[a2]);
                                    p[k][a3] = along * (h[a3] - b);
                                }
                                n[a1] = s1 * r2; n[a2] = s2 * r2; n[a3] = 0f;
                                w.Add(p, 4, n, Owner(a1, s1, a2, s2));
                            }
                    }
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2) {
                            Set(p[0], x * h[0], y * (h[1] - b), z * (h[2] - b));
                            Set(p[1], x * (h[0] - b), y * h[1], z * (h[2] - b));
                            Set(p[2], x * (h[0] - b), y * (h[1] - b), z * h[2]);
                            n[0] = x * r3; n[1] = y * r3; n[2] = z * r3;
                            w.Add(p, 3, n, Face(2, z));
                        }
            }
            return w.Done();
        }

        static void Set(float[] p, float x, float y, float z) { p[0] = x; p[1] = y; p[2] = z; }

        // Z faces own their chamfers first (the fronts carry the carved bands), then X, then Y.
        static int Owner(int a1, int s1, int a2, int s2) {
            if (a1 == 2) return Face(a1, s1);
            if (a2 == 2) return Face(a2, s2);
            return a1 == 0 ? Face(a1, s1) : Face(a2, s2);
        }

        sealed class Writer {
            readonly float[] h;
            readonly UvRect[] uv;
            readonly List<float> v = new List<float>(BevelledVertices * 3), nrm = new List<float>(BevelledVertices * 3),
                                 tan = new List<float>(BevelledVertices * 4), tex = new List<float>(BevelledVertices * 2);
            readonly List<int> tri = new List<int>(BevelledTriangles * 3);

            public Writer(float[] half, UvRect[] faceUv) { h = half; uv = faceUv; }

            public void Add(float[][] p, int count, float[] n, int owner) {
                // wind the polygon so that cross(p1 - p0, p2 - p0) points along the normal
                float ax = p[1][0] - p[0][0], ay = p[1][1] - p[0][1], az = p[1][2] - p[0][2];
                float bx = p[2][0] - p[0][0], by = p[2][1] - p[0][1], bz = p[2][2] - p[0][2];
                float facing = (ay * bz - az * by) * n[0] + (az * bx - ax * bz) * n[1] + (ax * by - ay * bx) * n[2];
                bool flip = facing < 0f;

                int ua = UAxis[owner], us = USign[owner], va = VAxis[owner], vs = VSign[owner];
                UvRect r = uv != null && owner < uv.Length ? uv[owner] : new UvRect(0f, 0f, 1f, 1f);
                // tangent: the owner's u direction, laid into this face's plane
                float tx = ua == 0 ? us : 0f, ty = ua == 1 ? us : 0f, tz = ua == 2 ? us : 0f;
                float d = tx * n[0] + ty * n[1] + tz * n[2];
                tx -= d * n[0]; ty -= d * n[1]; tz -= d * n[2];
                float len = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (len > 1e-6f) { tx /= len; ty /= len; tz /= len; }

                int first = v.Count / 3;
                for (int i = 0; i < count; i++) {
                    float[] q = p[flip ? count - 1 - i : i];
                    v.Add(q[0]); v.Add(q[1]); v.Add(q[2]);
                    nrm.Add(n[0]); nrm.Add(n[1]); nrm.Add(n[2]);
                    tan.Add(tx); tan.Add(ty); tan.Add(tz); tan.Add(-1f);
                    float cu = us * q[ua] / (2f * h[ua]) + 0.5f, cv = vs * q[va] / (2f * h[va]) + 0.5f;
                    tex.Add(r.U0 + cu * (r.U1 - r.U0)); tex.Add(r.V0 + cv * (r.V1 - r.V0));
                }
                tri.Add(first); tri.Add(first + 1); tri.Add(first + 2);
                if (count == 4) { tri.Add(first); tri.Add(first + 2); tri.Add(first + 3); }
            }

            public StoneMesh Done() => new StoneMesh {
                Vertices = v.ToArray(), Normals = nrm.ToArray(), Tangents = tan.ToArray(), Uvs = tex.ToArray(), Triangles = tri.ToArray()
            };
        }
    }
}
