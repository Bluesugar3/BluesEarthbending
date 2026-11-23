using System;
using System.Collections.Generic;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: how a stone looks. Each copy of a stone prefab carries a StoneView, which dresses the copy's empty "Model"
    // child once it appears: a MeshFilter and a MeshRenderer with the game's own mesh and material, shared and never changed.
    // The Ball and the Disc wear the network prefab "Stone Boulder 01"'s boulder mesh, fitted to their collider (the Disc
    // squashed flat); the Pillar, Cube and Wall a chamfered block (StoneBox) of their collider's size in that boulder's
    // material, mapped onto the part of the texture the boulder uses. Task 10 picks the real looks and makes them settings.
    internal static class StoneLook {
        const string BoulderPrefab = "Stone Boulder 01";
        const float BlockBevel = 0.05f;

        static bool searched, warnedModel;
        static Mesh? boulderMesh;
        static Material[] boulderMaterials = new Material[0];
        static UvRect boulderUv = new UvRect(0f, 0f, 1f, 1f);
        static readonly Dictionary<Vector3, Mesh> blocks = new Dictionary<Vector3, Mesh>();

        internal static void Dress(GameObject root, StoneKind kind) {
            Transform model = root.transform.Find("Model");
            if (model == null) {
                if (!warnedModel) { warnedModel = true; MelonLogger.Warning($"[Earth] Looks: {root.name} has no Model child; the stones stay invisible"); }
                return;
            }
            if (!FindBoulder() || boulderMesh == null) return;
            Vector3 size = ColliderSize(root, kind);
            MeshFilter mf = model.GetComponent<MeshFilter>();
            if (mf == null) mf = model.gameObject.AddComponent<MeshFilter>();
            MeshRenderer mr = model.GetComponent<MeshRenderer>();
            if (mr == null) mr = model.gameObject.AddComponent<MeshRenderer>();
            model.localRotation = Quaternion.identity;
            if (kind == StoneKind.Ball || kind == StoneKind.Disc) {
                // the boulder's bounds stretched onto the collider's, its middle on the stone's middle
                Bounds b = boulderMesh.bounds;
                var scale = new Vector3(Stretch(size.x, b.size.x), Stretch(size.y, b.size.y), Stretch(size.z, b.size.z));
                mf.sharedMesh = boulderMesh;
                mr.sharedMaterials = boulderMaterials;
                model.localScale = scale;
                model.localPosition = -Vector3.Scale(b.center, scale);
            }
            else {
                mf.sharedMesh = Block(size);
                mr.sharedMaterial = boulderMaterials[0];
                model.localScale = Vector3.one;
                model.localPosition = Vector3.zero;
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: dressed {root.name} ({kind}, {size.x:0.##} x {size.y:0.##} x {size.z:0.##} m) at {root.transform.position}");
        }

        static float Stretch(float want, float have) => have > 1e-4f ? want / have : 1f;

        static Vector3 ColliderSize(GameObject root, StoneKind kind) {
            BoxCollider box = root.GetComponent<BoxCollider>();
            if (box != null) return box.size;
            SphereCollider sphere = root.GetComponent<SphereCollider>();
            if (sphere != null) return Vector3.one * (sphere.radius * 2f);
            V3 s = StoneTable.Of(kind).Size;
            return new Vector3(s.X, s.Y, s.Z);
        }

        // Once per game: the boulder prefab's most detailed mesh (a LOD0 first, else the most vertices) and its materials, and
        // the part of the texture its UVs cover (only when the mesh can be read: a game mesh may not be).
        static bool FindBoulder() {
            if (searched) return boulderMesh != null;
            searched = true;
            NetworkPrefab prefab = PrefabManager.GetPrefab(BoulderPrefab.ToLowerInvariant());   // the game compares lower case
            if (prefab == null) { MelonLogger.Warning($"[Earth] Looks: no network prefab '{BoulderPrefab}' in this game; the stones stay invisible"); return false; }
            MeshFilter? best = null;
            foreach (MeshFilter f in prefab.GetComponentsInChildren<MeshFilter>(true)) {
                MeshRenderer r = f.GetComponent<MeshRenderer>();
                if (f.sharedMesh == null || r == null || r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null) continue;
                if (best == null) { best = f; continue; }
                bool lod0 = f.name.Contains("LOD0"), bestLod0 = best.name.Contains("LOD0");
                if ((lod0 && !bestLod0) || (lod0 == bestLod0 && f.sharedMesh.vertexCount > best.sharedMesh.vertexCount)) best = f;
            }
            if (best == null) { MelonLogger.Warning($"[Earth] Looks: '{prefab.name}' has no mesh with a material; the stones stay invisible"); return false; }
            boulderMesh = best.sharedMesh;
            boulderMaterials = best.GetComponent<MeshRenderer>().sharedMaterials;
            bool readable = boulderMesh.isReadable;
            if (readable) boulderUv = UvBounds(boulderMesh.uv, boulderUv);
            MelonLogger.Msg($"[Earth] Looks: stones wear '{boulderMesh.name}' ({boulderMesh.vertexCount} vertices) and '{boulderMaterials[0].name}' from '{prefab.name}'; the blocks' texture area {(readable ? $"u {boulderUv.U0:0.###}-{boulderUv.U1:0.###}, v {boulderUv.V0:0.###}-{boulderUv.V1:0.###}" : "is the whole texture (the mesh cannot be read)")}");
            return true;
        }

        static UvRect UvBounds(Vector2[] uv, UvRect fallback) {
            if (uv == null || uv.Length == 0) return fallback;
            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            foreach (Vector2 p in uv) {
                u0 = Mathf.Min(u0, p.x); v0 = Mathf.Min(v0, p.y);
                u1 = Mathf.Max(u1, p.x); v1 = Mathf.Max(v1, p.y);
            }
            return u1 - u0 > 1e-4f && v1 - v0 > 1e-4f ? new UvRect(u0, v0, u1, v1) : fallback;
        }

        // One mesh per size, kept for the whole game (a mesh made per stone would never be freed).
        static Mesh Block(Vector3 size) {
            if (blocks.TryGetValue(size, out Mesh cached) && cached != null) return cached;
            var uv = new UvRect[StoneBox.Faces];
            for (int i = 0; i < uv.Length; i++) uv[i] = boulderUv;
            Mesh m = ToMesh(StoneBox.Build(size.x, size.y, size.z, BlockBevel, uv), $"Earth block {size.x:0.##} x {size.y:0.##} x {size.z:0.##}");
            blocks[size] = m;
            return m;
        }

        static Mesh ToMesh(StoneMesh s, string name) {
            int n = s.VertexCount;
            var v = new Vector3[n];
            var nr = new Vector3[n];
            var t = new Vector4[n];
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++) {
                v[i] = new Vector3(s.Vertices[i * 3], s.Vertices[i * 3 + 1], s.Vertices[i * 3 + 2]);
                nr[i] = new Vector3(s.Normals[i * 3], s.Normals[i * 3 + 1], s.Normals[i * 3 + 2]);
                t[i] = new Vector4(s.Tangents[i * 4], s.Tangents[i * 4 + 1], s.Tangents[i * 4 + 2], s.Tangents[i * 4 + 3]);
                uv[i] = new Vector2(s.Uvs[i * 2], s.Uvs[i * 2 + 1]);
            }
            var m = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.vertices = v;
            m.normals = nr;
            m.tangents = t;
            m.uv = uv;
            m.triangles = s.Triangles;
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>On every stone prefab (Prefabs.cs puts it on the templates; Instantiate copies it). On a game it dresses the copy
    /// once it appears; on the headless server it does nothing (no renderers or effects there).</summary>
    internal sealed class StoneView : MonoBehaviour {
        static bool warned;

        void Start() {
            if (Main.IsServer) return;
            try {
                StoneKind? kind = Prefabs.KindOf(GetComponent<NetworkEntity>());
                if (kind == null) {
                    if (!warned) { warned = true; MelonLogger.Warning($"[Earth] Looks: {name} is not a registered stone; left undressed"); }
                    return;
                }
                StoneLook.Dress(gameObject, kind.Value);
            }
            catch (Exception e) {
                if (!warned) { warned = true; MelonLogger.Warning($"[Earth] Looks: could not dress {name}: {e}"); }
            }
        }
    }
}
