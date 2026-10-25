using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostyAnimationEditor.Core.Anim;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyAnimationEditor.Plugin
{
    /// <summary>
    /// The player's render skeleton (EBX SkeletonAsset) and skinned meshes (SkinnedMeshAsset -> MeshSet res + chunk)
    /// read through Frosty's asset manager. Mesh vertices are decoded as MeshSetPlugin's exporters do; FIFA 17 vertex
    /// bone indices are skeleton bone indices (MeshSetPlugin's FBX export maps them so for FIFA 17).
    /// </summary>
    public static class PlayerAssets
    {
        public static bool Available { get { return App.AssetManager != null; } }

        private static double D(object v) { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>a float field of an EBX struct (Vec3.x), by reflection</summary>
        private static double X(object o, string field)
        {
            Type t = o.GetType();
            System.Reflection.PropertyInfo p = t.GetProperty(field);
            if (p != null) return D(p.GetValue(o));
            System.Reflection.FieldInfo f = t.GetField(field);
            if (f != null) return D(f.GetValue(o));
            throw new InvalidDataException("the EBX value " + t.Name + " has no field " + field);
        }

        public static PlayerSkeleton LoadSkeleton() { return LoadSkeleton(PlayerSkeleton.PlayerSkeletonAsset); }

        public static PlayerSkeleton LoadSkeleton(string name)
        {
            if (App.AssetManager == null) return null;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(name);
            if (e == null) return null;
            EbxAsset asset = App.AssetManager.GetEbx(e);
            dynamic root = asset.RootObject;
            dynamic names = root.BoneNames, hier = root.Hierarchy, pose = root.LocalPose;
            int n = names.Count;
            List<string> bn = new List<string>(n);
            List<int> par = new List<int>(n);
            List<double[]> lp = new List<double[]>(n);
            for (int i = 0; i < n; i++)
            {
                bn.Add(names[i].ToString());
                long p = Convert.ToInt64((object)hier[i]);
                if (p >= 0x80000000L) p -= 0x100000000L;
                par.Add((int)p);
                dynamic lt = pose[i];
                object r = lt.right, u = lt.up, f = lt.forward, t = lt.trans;
                lp.Add(new[] { X(r, "x"), X(r, "y"), X(r, "z"), X(u, "x"), X(u, "y"), X(u, "z"), X(f, "x"), X(f, "y"), X(f, "z"), X(t, "x"), X(t, "y"), X(t, "z") });
            }
            return PlayerSkeleton.FromLinearTransforms(e.Name, "EBX " + e.Name, bn, par, lp);
        }

        /// <summary>names of the SkinnedMeshAssets under content/character/</summary>
        public static List<string> CharacterMeshes()
        {
            if (App.AssetManager == null) return new List<string>();
            return App.AssetManager.EnumerateEbx("SkinnedMeshAsset")
                      .Select(x => x.Name)
                      .Where(x => x.StartsWith("content/character/", StringComparison.OrdinalIgnoreCase))
                      .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string File(string name) { int k = name.LastIndexOf('/'); return k >= 0 ? name.Substring(k + 1) : name; }
        private static string Folder(string name) { int k = name.LastIndexOf('/'); return k >= 0 ? name.Substring(0, k) : ""; }

        /// <summary>
        /// a default outfit: one body folder (body_simhigh_0 when there is one), one mesh of each kind in it
        /// (the file name's first word, two for body_*), one head, one hair and one shoe
        /// </summary>
        public static List<string> DefaultParts(List<string> all)
        {
            List<string> r = new List<string>();
            List<string> body = all.Where(x => x.IndexOf("/body/", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            string folder = body.Select(Folder).FirstOrDefault(f => f.EndsWith("body_simhigh_0", StringComparison.OrdinalIgnoreCase))
                            ?? body.Select(Folder).FirstOrDefault(f => f.IndexOf("simhigh", StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? body.Select(Folder).FirstOrDefault();
            if (folder != null)
            {
                Func<string, int> score = x =>
                {
                    string f = File(x).ToLowerInvariant();
                    int s = 0;
                    if (f.Contains("short_sleeve")) s -= 4;
                    if (f.Contains("average")) s -= 2;
                    if (f.Contains("_male")) s -= 1;
                    if (f.Contains("female")) s += 8;
                    if (f.Contains("gk") || f.Contains("goalkeeper") || f.Contains("glove")) s += 6;
                    return s;
                };
                foreach (IGrouping<string, string> g in body.Where(x => Folder(x) == folder).GroupBy(x =>
                         {
                             string[] w = File(x).ToLowerInvariant().Split('_');
                             return w.Length > 1 && w[0] == "body" ? w[0] + "_" + w[1] : w[0];
                         }))
                {
                    if (g.Key.StartsWith("glove")) continue;
                    r.Add(g.OrderBy(score).ThenBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).First());
                }
            }
            foreach (string kind in new[] { "/generichead/", "/generichair/", "/shoe/" })
            {
                string m = all.Where(x => x.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0)
                              .OrderBy(x => File(x).ToLowerInvariant().Contains("lod") ? 1 : 0).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (m != null) r.Add(m);
            }
            return r;
        }

        // ------------------------------------------------------------------ meshes
        private static float Half(ushort h)
        {
            int s = (h >> 15) & 1, e = (h >> 10) & 0x1f, m = h & 0x3ff;
            float v;
            if (e == 0) v = (float)(m * Math.Pow(2, -24));
            else if (e == 31) v = m == 0 ? float.PositiveInfinity : float.NaN;
            else v = (float)((1 + m / 1024.0) * Math.Pow(2, e - 15));
            return s != 0 ? -v : v;
        }

        private static float Sn10(uint a)
        {
            int s = (a & 0x200) != 0 ? (int)a - 1024 : (int)a;
            return Math.Max(s / 511f, -1f);
        }

        private static float[] Decode(VertexElementFormat fmt, byte[] b, long off)
        {
            int o = (int)off;
            switch (fmt)
            {
                case VertexElementFormat.Float: return new[] { BitConverter.ToSingle(b, o) };
                case VertexElementFormat.Float2: return new[] { BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4) };
                case VertexElementFormat.Float3: return new[] { BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8) };
                case VertexElementFormat.Float4: return new[] { BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8), BitConverter.ToSingle(b, o + 12) };
                case VertexElementFormat.Half: return new[] { Half(BitConverter.ToUInt16(b, o)) };
                case VertexElementFormat.Half2: return new[] { Half(BitConverter.ToUInt16(b, o)), Half(BitConverter.ToUInt16(b, o + 2)) };
                case VertexElementFormat.Half3: return new[] { Half(BitConverter.ToUInt16(b, o)), Half(BitConverter.ToUInt16(b, o + 2)), Half(BitConverter.ToUInt16(b, o + 4)) };
                case VertexElementFormat.Half4: return new[] { Half(BitConverter.ToUInt16(b, o)), Half(BitConverter.ToUInt16(b, o + 2)), Half(BitConverter.ToUInt16(b, o + 4)), Half(BitConverter.ToUInt16(b, o + 6)) };
                case VertexElementFormat.UByte4N: return new[] { b[o] / 255f, b[o + 1] / 255f, b[o + 2] / 255f, b[o + 3] / 255f };
                case VertexElementFormat.UByte4: return new float[] { b[o], b[o + 1], b[o + 2], b[o + 3] };
                case VertexElementFormat.Byte4: return new float[] { (sbyte)b[o], (sbyte)b[o + 1], (sbyte)b[o + 2], (sbyte)b[o + 3] };
                case VertexElementFormat.Byte4N: return new[] { Math.Max((sbyte)b[o] / 127f, -1f), Math.Max((sbyte)b[o + 1] / 127f, -1f), Math.Max((sbyte)b[o + 2] / 127f, -1f), Math.Max((sbyte)b[o + 3] / 127f, -1f) };
                case VertexElementFormat.UShort2N: return new[] { BitConverter.ToUInt16(b, o) / 65535f, BitConverter.ToUInt16(b, o + 2) / 65535f };
                case VertexElementFormat.UShort4N: return new[] { BitConverter.ToUInt16(b, o) / 65535f, BitConverter.ToUInt16(b, o + 2) / 65535f, BitConverter.ToUInt16(b, o + 4) / 65535f, BitConverter.ToUInt16(b, o + 6) / 65535f };
                case VertexElementFormat.Short2N: return new[] { Math.Max(BitConverter.ToInt16(b, o) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 2) / 32767f, -1f) };
                case VertexElementFormat.Short4N: return new[] { Math.Max(BitConverter.ToInt16(b, o) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 2) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 4) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 6) / 32767f, -1f) };
                case VertexElementFormat.UShort2: return new float[] { BitConverter.ToUInt16(b, o), BitConverter.ToUInt16(b, o + 2) };
                case VertexElementFormat.UShort4: return new float[] { BitConverter.ToUInt16(b, o), BitConverter.ToUInt16(b, o + 2), BitConverter.ToUInt16(b, o + 4), BitConverter.ToUInt16(b, o + 6) };
                case VertexElementFormat.UComp4N_10_10_10_2:
                case VertexElementFormat.UComp3N_10_10_10:
                {
                    uint v = BitConverter.ToUInt32(b, o);
                    return new[] { (v & 0x3ff) / 1023f, ((v >> 10) & 0x3ff) / 1023f, ((v >> 20) & 0x3ff) / 1023f, (v >> 30) / 3f };
                }
                case VertexElementFormat.Comp4N_10_10_10_2:
                case VertexElementFormat.Comp3N_10_10_10:
                {
                    uint v = BitConverter.ToUInt32(b, o);
                    return new[] { Sn10(v & 0x3ff), Sn10((v >> 10) & 0x3ff), Sn10((v >> 20) & 0x3ff), (float)(v >> 30) };
                }
                default: return null;
            }
        }

        public sealed class MeshLoad
        {
            public MeshPart Mesh;
            public int Lods;
            public int Lod;
            public List<string> Notes = new List<string>();
        }

        /// <summary>
        /// lod lod (clamped) of a SkinnedMeshAsset as one MeshPart (all renderable sections merged), bone indices checked
        /// against the skeleton
        /// </summary>
        public static MeshLoad LoadMesh(string ebxName, PlayerSkeleton sk, int lod)
        {
            MeshLoad r = new MeshLoad();
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxName);
            if (entry == null) throw new FileNotFoundException("no asset " + ebxName);
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            ulong rid = ((dynamic)asset.RootObject).MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            if (resEntry == null) throw new FileNotFoundException("the mesh set of " + ebxName + " is not in the project");
            MeshSet ms = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (ms == null || ms.Lods.Count == 0) throw new InvalidDataException("the mesh set of " + ebxName + " does not read");
            r.Lods = ms.Lods.Count;
            r.Lod = Math.Max(0, Math.Min(lod, ms.Lods.Count - 1));
            MeshSetLod l = ms.Lods[r.Lod];
            byte[] chunk = l.InlineData;
            ChunkAssetEntry ce = l.ChunkId != Guid.Empty ? App.AssetManager.GetChunkEntry(l.ChunkId) : null;
            if (ce != null)
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(ce))) chunk = reader.ReadToEnd();
            if (chunk == null || chunk.Length == 0) throw new InvalidDataException("the geometry of " + ebxName + " (chunk " + l.ChunkId + ") is not available");

            List<float> pos = new List<float>(), nrm = new List<float>(), uv = new List<float>(), bw = new List<float>();
            List<int> tri = new List<int>(), bi = new List<int>();
            const int Inf = 8;
            bool allNormals = true, anyUv = false;
            int maxBone = -1, procedural = 0, outOfRange = 0;
            int sections = 0;
            // the sections drawn in the colour passes (shadow / depth-only copies are left out); when the lod has no
            // category lists, every named section
            bool anyRenderable = l.Sections.Any(x => x.VertexCount > 0 && x.PrimitiveCount > 0 && l.IsSectionRenderable(x));
            foreach (MeshSetSection section in l.Sections)
            {
                if (section.VertexCount == 0 || section.PrimitiveCount == 0) continue;
                if (anyRenderable ? !l.IsSectionRenderable(section) : string.IsNullOrEmpty(section.Name)) continue;
                sections++;
                int nv = (int)section.VertexCount, v0 = pos.Count / 3;
                float[] p = new float[nv * 3], n = null, t = null, w = new float[nv * Inf];
                int[] ix = new int[nv * Inf];
                for (int i = 0; i < ix.Length; i++) ix[i] = -1;
                GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                long streamBase = section.VertexOffset;
                for (int si = 0; si < decl.Streams.Length; si++)
                {
                    GeometryDeclarationDesc.Stream stream = decl.Streams[si];
                    if (stream.VertexStride == 0) continue;
                    foreach (GeometryDeclarationDesc.Element el in decl.Elements)
                    {
                        if (el.StreamIndex != si || el.Usage == VertexElementUsage.Unknown) continue;
                        VertexElementUsage u = el.Usage;
                        if (u != VertexElementUsage.Pos && u != VertexElementUsage.Normal && u != VertexElementUsage.TexCoord0 &&
                            u != VertexElementUsage.BoneIndices && u != VertexElementUsage.BoneIndices2 && u != VertexElementUsage.BoneWeights && u != VertexElementUsage.BoneWeights2) continue;
                        if (u == VertexElementUsage.Normal) n = new float[nv * 3];
                        if (u == VertexElementUsage.TexCoord0) t = new float[nv * 2];
                        for (int v = 0; v < nv; v++)
                        {
                            long off = streamBase + (long)v * stream.VertexStride + el.Offset;
                            if (off + el.Size > chunk.Length) throw new InvalidDataException("vertex data of " + ebxName + " runs past its chunk");
                            float[] c = Decode(el.Format, chunk, off);
                            if (c == null) throw new InvalidDataException("vertex format " + el.Format + " (" + u + ") of " + ebxName + " is not handled");
                            switch (u)
                            {
                                case VertexElementUsage.Pos: p[v * 3] = c[0]; p[v * 3 + 1] = c.Length > 1 ? c[1] : 0; p[v * 3 + 2] = c.Length > 2 ? c[2] : 0; break;
                                case VertexElementUsage.Normal: n[v * 3] = c[0]; n[v * 3 + 1] = c.Length > 1 ? c[1] : 0; n[v * 3 + 2] = c.Length > 2 ? c[2] : 0; break;
                                case VertexElementUsage.TexCoord0: t[v * 2] = c[0]; t[v * 2 + 1] = c.Length > 1 ? c[1] : 0; break;
                                case VertexElementUsage.BoneIndices:
                                case VertexElementUsage.BoneIndices2:
                                {
                                    int k0 = u == VertexElementUsage.BoneIndices ? 0 : 4;
                                    for (int k = 0; k < 4 && k < c.Length; k++) ix[v * Inf + k0 + k] = (int)c[k];
                                    break;
                                }
                                default:
                                {
                                    int k0 = u == VertexElementUsage.BoneWeights ? 0 : 4;
                                    for (int k = 0; k < 4 && k < c.Length; k++) w[v * Inf + k0 + k] = c[k];
                                    break;
                                }
                            }
                        }
                    }
                    streamBase += (long)stream.VertexStride * nv;
                }
                // weights without a bone, procedural (0x8000) or out-of-range bones are dropped
                for (int v = 0; v < nv; v++)
                {
                    bool any = false;
                    for (int k = 0; k < Inf; k++)
                    {
                        int b = ix[v * Inf + k];
                        if (w[v * Inf + k] <= 0) { ix[v * Inf + k] = -1; continue; }
                        if (b < 0) { w[v * Inf + k] = 0; continue; }
                        if ((b & 0x8000) != 0) { procedural++; ix[v * Inf + k] = -1; w[v * Inf + k] = 0; continue; }
                        if (b >= sk.Count) { outOfRange++; ix[v * Inf + k] = -1; w[v * Inf + k] = 0; continue; }
                        maxBone = Math.Max(maxBone, b);
                        any = true;
                    }
                    if (!any && nv > 0) { }
                }
                pos.AddRange(p);
                if (n != null) nrm.AddRange(n); else { allNormals = false; nrm.AddRange(new float[nv * 3]); }
                if (t != null) { uv.AddRange(t); anyUv = true; } else uv.AddRange(new float[nv * 2]);
                bi.AddRange(ix);
                bw.AddRange(w);

                int unit = l.IndexUnitSize / 8;
                long ib = l.VertexBufferSize + (long)section.StartIndex * unit;
                bool strip = section.PrimitiveType == PrimitiveType.PrimitiveType_TriangleStrip;
                int count = strip ? (int)section.PrimitiveCount + 2 : (int)section.PrimitiveCount * 3;
                if (ib + (long)count * unit > chunk.Length) throw new InvalidDataException("index data of " + ebxName + " runs past its chunk");
                int[] raw = new int[count];
                for (int i = 0; i < count; i++) raw[i] = unit == 2 ? BitConverter.ToUInt16(chunk, (int)(ib + i * 2)) : (int)BitConverter.ToUInt32(chunk, (int)(ib + i * 4));
                if (strip)
                {
                    for (int i = 0; i + 2 < raw.Length; i++)
                    {
                        int a = raw[i], b = raw[i + 1], c = raw[i + 2];
                        if (a == b || b == c || a == c) continue;
                        if (i % 2 == 0) { tri.Add(v0 + a); tri.Add(v0 + b); tri.Add(v0 + c); }
                        else { tri.Add(v0 + b); tri.Add(v0 + a); tri.Add(v0 + c); }
                    }
                }
                else foreach (int x in raw) tri.Add(v0 + x);
            }
            MeshPart mp = new MeshPart
            {
                Name = File(ebxName), Source = ebxName, Positions = pos.ToArray(), Triangles = tri.ToArray(), Influences = Inf,
                BoneIndices = bi.ToArray(), BoneWeights = bw.ToArray(), Uvs = anyUv ? uv.ToArray() : null
            };
            int nvt = mp.VertexCount;
            foreach (int x in mp.Triangles) if (x < 0 || x >= nvt) throw new InvalidDataException("a triangle of " + ebxName + " points past its vertices");
            if (allNormals && nrm.Count == nvt * 3) mp.Normals = nrm.ToArray();
            else mp.ComputeNormals();
            r.Mesh = mp;
            r.Notes.Add(File(ebxName) + ": lod " + r.Lod + " of " + r.Lods + ", " + sections + " sections, " + nvt + " vertices, " + mp.TriangleCount + " triangles, bones up to " + maxBone +
                        (procedural > 0 ? ", " + procedural + " procedural-bone weights dropped" : "") + (outOfRange > 0 ? ", " + outOfRange + " weights on bones past the skeleton dropped" : ""));
            return r;
        }

        // ------------------------------------------------------------------ saved choice
        private static string PartsFile { get { return Path.Combine(EditorCache.Folder, "player_parts.txt"); } }

        public static List<string> SavedParts()
        {
            try
            {
                if (!System.IO.File.Exists(PartsFile)) return null;
                return System.IO.File.ReadAllLines(PartsFile).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#")).ToList();
            }
            catch (Exception) { return null; }
        }

        public static void SaveParts(IEnumerable<string> parts, int lod)
        {
            try
            {
                Directory.CreateDirectory(EditorCache.Folder);
                System.IO.File.WriteAllLines(PartsFile, new[] { "# FIFA 17 Animation Editor: player view meshes (one SkinnedMeshAsset per line); lod " + lod }.Concat(parts));
            }
            catch (Exception) { }
        }

        public static int SavedLod()
        {
            try
            {
                if (!System.IO.File.Exists(PartsFile)) return 0;
                string first = System.IO.File.ReadLines(PartsFile).FirstOrDefault() ?? "";
                int k = first.LastIndexOf("lod ", StringComparison.Ordinal);
                int v;
                return k >= 0 && int.TryParse(first.Substring(k + 4).Trim(), out v) ? v : 0;
            }
            catch (Exception) { return 0; }
        }
    }
}
