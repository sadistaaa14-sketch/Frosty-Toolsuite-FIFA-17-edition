using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DuplicationPlugin
{
    /// <summary>
    /// Geometry helpers of the PES stadium import: exact box carving of the package geometry (the FIFA bench and
    /// tunnel boxes) and the surface grid that the placement re-fit uses to test where the base stadium's pitch-side
    /// characters and props end up inside the PES stands.
    /// </summary>
    public static class BoxClip
    {
        /// <summary>
        /// Clips a triangle list against an axis-aligned box and keeps everything outside it: triangles crossing a
        /// face are split along it, triangles fully inside are dropped. <paramref name="verts"/> holds one float[]
        /// per vertex (x y z first, any number of linearly interpolated attributes after); new vertices are appended.
        /// Returns the new index list; <paramref name="touched"/> counts the triangles that were cut or removed.
        /// </summary>
        public static List<int> ClipOutside(List<float[]> verts, IList<int> indices, float minX, float maxX, float minY, float maxY, float minZ, float maxZ, out int touched)
        {
            touched = 0;
            List<int> result = new List<int>(indices.Count);
            // planes: d(p) > 0 means outside
            Func<float[], float>[] planes =
            {
                p => minX - p[0], p => p[0] - maxX,
                p => minY - p[1], p => p[1] - maxY,
                p => minZ - p[2], p => p[2] - maxZ
            };
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
                float[] a = verts[ia], b = verts[ib], c = verts[ic];
                // quick reject: the triangle's box does not meet the carve box
                if (Math.Max(a[0], Math.Max(b[0], c[0])) <= minX || Math.Min(a[0], Math.Min(b[0], c[0])) >= maxX ||
                    Math.Max(a[1], Math.Max(b[1], c[1])) <= minY || Math.Min(a[1], Math.Min(b[1], c[1])) >= maxY ||
                    Math.Max(a[2], Math.Max(b[2], c[2])) <= minZ || Math.Min(a[2], Math.Min(b[2], c[2])) >= maxZ)
                {
                    result.Add(ia); result.Add(ib); result.Add(ic);
                    continue;
                }
                touched++;
                // polygons still to be tested against the remaining planes (vertex index lists)
                List<List<int>> pending = new List<List<int>> { new List<int> { ia, ib, ic } };
                List<List<int>> outside = new List<List<int>>();
                foreach (Func<float[], float> plane in planes)
                {
                    List<List<int>> next = new List<List<int>>();
                    foreach (List<int> poly in pending)
                    {
                        List<int> outPart = new List<int>(), inPart = new List<int>();
                        for (int i = 0; i < poly.Count; i++)
                        {
                            int i0 = poly[i], i1 = poly[(i + 1) % poly.Count];
                            float d0 = plane(verts[i0]), d1 = plane(verts[i1]);
                            bool o0 = d0 > 0, o1 = d1 > 0;
                            if (o0 != o1)
                            {
                                float s = d0 / (d0 - d1);
                                int im = verts.Count;
                                verts.Add(Lerp(verts[i0], verts[i1], s));
                                outPart.Add(im); inPart.Add(im);
                            }
                            if (o1) outPart.Add(i1); else inPart.Add(i1);
                        }
                        if (outPart.Count >= 3) outside.Add(outPart);
                        if (inPart.Count >= 3) next.Add(inPart);
                    }
                    pending = next;
                    if (pending.Count == 0) break;
                }
                // pending polygons are inside every plane: dropped; the outside parts are fanned
                foreach (List<int> poly in outside)
                    for (int i = 1; i + 1 < poly.Count; i++)
                    {
                        if (Degenerate(verts[poly[0]], verts[poly[i]], verts[poly[i + 1]])) continue;
                        result.Add(poly[0]); result.Add(poly[i]); result.Add(poly[i + 1]);
                    }
            }
            return result;
        }

        private static bool Degenerate(float[] a, float[] b, float[] c)
        {
            float ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            float vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            return nx * nx + ny * ny + nz * nz < 1e-12f;
        }

        private static float[] Lerp(float[] a, float[] b, float s)
        {
            float[] r = new float[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * s;
            return r;
        }

        /// <summary>Carves the box out of a geometry section (positions, normals, UVs and colours interpolated); returns the number of triangles cut or removed.</summary>
        public static int CarveSection(GeometryImportSection gs, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
        {
            int n = gs.VertexCount;
            if (n == 0 || gs.Indices == null) return 0;
            bool hasN = gs.Normals != null && gs.Normals.Length == n * 3;
            bool hasUv1 = gs.TexCoord1 != null && gs.TexCoord1.Length == n * 2;
            bool hasLm = gs.LightmapUv != null && gs.LightmapUv.Length == n * 2;
            bool hasCol = gs.Colors != null && gs.Colors.Length == n * 4;
            int stride = 3 + (hasN ? 3 : 0) + 2 + (hasUv1 ? 2 : 0) + (hasLm ? 2 : 0) + (hasCol ? 4 : 0);
            List<float[]> verts = new List<float[]>(n + 64);
            for (int i = 0; i < n; i++)
            {
                float[] v = new float[stride];
                int o = 0;
                v[o++] = gs.Positions[i * 3]; v[o++] = gs.Positions[i * 3 + 1]; v[o++] = gs.Positions[i * 3 + 2];
                if (hasN) { v[o++] = gs.Normals[i * 3]; v[o++] = gs.Normals[i * 3 + 1]; v[o++] = gs.Normals[i * 3 + 2]; }
                v[o++] = gs.TexCoord0[i * 2]; v[o++] = gs.TexCoord0[i * 2 + 1];
                if (hasUv1) { v[o++] = gs.TexCoord1[i * 2]; v[o++] = gs.TexCoord1[i * 2 + 1]; }
                if (hasLm) { v[o++] = gs.LightmapUv[i * 2]; v[o++] = gs.LightmapUv[i * 2 + 1]; }
                if (hasCol) { v[o++] = gs.Colors[i * 4]; v[o++] = gs.Colors[i * 4 + 1]; v[o++] = gs.Colors[i * 4 + 2]; v[o++] = gs.Colors[i * 4 + 3]; }
                verts.Add(v);
            }
            List<int> idx = new List<int>(gs.Indices.Length);
            foreach (uint u in gs.Indices) idx.Add((int)u);
            int touched;
            List<int> clipped = ClipOutside(verts, idx, minX, maxX, minY, maxY, minZ, maxZ, out touched);
            if (touched == 0) return 0;
            // compact to the used vertices
            int[] remap = new int[verts.Count];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            int m = 0;
            foreach (int i in clipped) if (remap[i] < 0) remap[i] = m++;
            float[] pos = new float[m * 3];
            float[] nrm = hasN ? new float[m * 3] : null;
            float[] uv0 = new float[m * 2];
            float[] uv1 = hasUv1 ? new float[m * 2] : null;
            float[] lm = hasLm ? new float[m * 2] : null;
            byte[] col = hasCol ? new byte[m * 4] : null;
            for (int i = 0; i < verts.Count; i++)
            {
                int j = remap[i];
                if (j < 0) continue;
                float[] v = verts[i];
                int o = 0;
                pos[j * 3] = v[o++]; pos[j * 3 + 1] = v[o++]; pos[j * 3 + 2] = v[o++];
                if (hasN)
                {
                    float x = v[o++], y = v[o++], z = v[o++];
                    float len = (float)Math.Sqrt(x * x + y * y + z * z);
                    if (len > 1e-6f) { x /= len; y /= len; z /= len; } else { x = 0; y = 1; z = 0; }
                    nrm[j * 3] = x; nrm[j * 3 + 1] = y; nrm[j * 3 + 2] = z;
                }
                uv0[j * 2] = v[o++]; uv0[j * 2 + 1] = v[o++];
                if (hasUv1) { uv1[j * 2] = v[o++]; uv1[j * 2 + 1] = v[o++]; }
                if (hasLm) { lm[j * 2] = v[o++]; lm[j * 2 + 1] = v[o++]; }
                if (hasCol) for (int k = 0; k < 4; k++) col[j * 4 + k] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v[o++])));
            }
            gs.Positions = pos; gs.Normals = nrm; gs.TexCoord0 = uv0; gs.TexCoord1 = uv1; gs.LightmapUv = lm; gs.Colors = col;
            gs.Indices = clipped.Select(i => (uint)remap[i]).ToArray();
            return touched;
        }

        /// <summary>Carves the box out of a raw triangle soup (float32 xyz, three per triangle); returns the new soup.</summary>
        public static float[] CarveSoup(float[] soup, float minX, float maxX, float minY, float maxY, float minZ, float maxZ, out int touched)
        {
            int nv = soup.Length / 3;
            List<float[]> verts = new List<float[]>(nv + 64);
            List<int> idx = new List<int>(nv);
            for (int i = 0; i < nv; i++) { verts.Add(new[] { soup[i * 3], soup[i * 3 + 1], soup[i * 3 + 2] }); idx.Add(i); }
            List<int> clipped = ClipOutside(verts, idx, minX, maxX, minY, maxY, minZ, maxZ, out touched);
            if (touched == 0) return soup;
            float[] result = new float[clipped.Count * 3];
            for (int i = 0; i < clipped.Count; i++) { float[] v = verts[clipped[i]]; result[i * 3] = v[0]; result[i * 3 + 1] = v[1]; result[i * 3 + 2] = v[2]; }
            return result;
        }
    }

    /// <summary>
    /// Per 0.5 m cell of the xz plane: the highest and lowest surface sample of the package geometry below 8 m
    /// (triangle interiors sampled at the cell centres, edges sampled every quarter cell so walls and thin rails
    /// register too). Used to tell whether a character or prop stands inside / under the PES stands and where
    /// the nearest open ground is.
    /// </summary>
    public class SurfaceGrid
    {
        public readonly float Cell, HalfX, HalfZ, MaxY;
        public readonly int Nx, Nz;
        private readonly float[] top, bottom;
        public int Triangles;

        public SurfaceGrid(float cell = 0.5f, float halfX = 130f, float halfZ = 110f, float maxY = 8f)
        {
            Cell = cell; HalfX = halfX; HalfZ = halfZ; MaxY = maxY;
            Nx = (int)(2 * halfX / cell) + 1; Nz = (int)(2 * halfZ / cell) + 1;
            top = new float[Nx * Nz]; bottom = new float[Nx * Nz];
            for (int i = 0; i < top.Length; i++) { top[i] = float.NegativeInfinity; bottom[i] = float.PositiveInfinity; }
        }

        private void Mark(float x, float z, float y)
        {
            int ix = (int)Math.Floor((x + HalfX) / Cell), iz = (int)Math.Floor((z + HalfZ) / Cell);
            if (ix < 0 || iz < 0 || ix >= Nx || iz >= Nz) return;
            int k = ix * Nz + iz;
            if (y > top[k]) top[k] = y;
            if (y < bottom[k]) bottom[k] = y;
        }

        public void Add(float[] positions, uint[] indices)
        {
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int ia = (int)indices[t] * 3, ib = (int)indices[t + 1] * 3, ic = (int)indices[t + 2] * 3;
                AddTriangle(positions[ia], positions[ia + 1], positions[ia + 2], positions[ib], positions[ib + 1], positions[ib + 2], positions[ic], positions[ic + 1], positions[ic + 2]);
            }
        }

        public void AddTriangle(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
        {
            float minY = Math.Min(ay, Math.Min(by, cy));
            if (minY >= MaxY) return;
            float minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
            float minZ = Math.Min(az, Math.Min(bz, cz)), maxZ = Math.Max(az, Math.Max(bz, cz));
            if (maxX < -HalfX || minX > HalfX || maxZ < -HalfZ || minZ > HalfZ) return;
            Triangles++;
            // edges
            Edge(ax, ay, az, bx, by, bz); Edge(bx, by, bz, cx, cy, cz); Edge(cx, cy, cz, ax, ay, az);
            // interior: cell centres inside the xz projection
            if (maxX - minX <= Cell && maxZ - minZ <= Cell) return;
            float area2 = (bx - ax) * (cz - az) - (cx - ax) * (bz - az);
            if (Math.Abs(area2) < 1e-6f) return;
            int x0 = Math.Max(0, (int)Math.Floor((minX + HalfX) / Cell)), x1 = Math.Min(Nx - 1, (int)Math.Floor((maxX + HalfX) / Cell));
            int z0 = Math.Max(0, (int)Math.Floor((minZ + HalfZ) / Cell)), z1 = Math.Min(Nz - 1, (int)Math.Floor((maxZ + HalfZ) / Cell));
            for (int ix = x0; ix <= x1; ix++)
            {
                float px = (ix + 0.5f) * Cell - HalfX;
                for (int iz = z0; iz <= z1; iz++)
                {
                    float pz = (iz + 0.5f) * Cell - HalfZ;
                    float w0 = ((bx - px) * (cz - pz) - (cx - px) * (bz - pz)) / area2;
                    float w1 = ((cx - px) * (az - pz) - (ax - px) * (cz - pz)) / area2;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f) continue;
                    Mark(px, pz, w0 * ay + w1 * by + w2 * cy);
                }
            }
        }

        private void Edge(float ax, float ay, float az, float bx, float by, float bz)
        {
            float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            int n = Math.Min(4000, Math.Max(1, (int)Math.Ceiling(len / (Cell * 0.25f))));
            for (int i = 0; i <= n; i++)
            {
                float s = (float)i / n;
                Mark(ax + (bx - ax) * s, az + (bz - az) * s, ay + (by - ay) * s);
            }
        }

        /// <summary>Something inside the footprint (radius) between the feet (y + 0.15) and the head (y + 2.2).</summary>
        public bool Blocked(float x, float y, float z, float radius, float step = 0.15f, float head = 2.2f)
        {
            int ix0, ix1, iz0, iz1;
            if (!Footprint(x, z, radius, out ix0, out ix1, out iz0, out iz1)) return false;
            for (int ix = ix0; ix <= ix1; ix++)
                for (int iz = iz0; iz <= iz1; iz++)
                {
                    int k = ix * Nz + iz;
                    if (top[k] > y + step && bottom[k] < y + head) return true;
                }
            return false;
        }

        /// <summary>
        /// The ground under the footprint is near y: every cell's highest sample at or below the feet is within
        /// -0.25 .. +0.15 m of y; a cell without geometry counts as the FIFA apron (y 0) inside the apron rectangle.
        /// </summary>
        public bool FloorOk(float x, float y, float z, float radius, float apronHalfX = 78f, float apronMinZ = -57f, float apronMaxZ = 52f)
        {
            int ix0, ix1, iz0, iz1;
            if (!Footprint(x, z, radius, out ix0, out ix1, out iz0, out iz1)) return false;
            for (int ix = ix0; ix <= ix1; ix++)
                for (int iz = iz0; iz <= iz1; iz++)
                {
                    int k = ix * Nz + iz;
                    float floor;
                    if (float.IsNegativeInfinity(top[k]) || (top[k] > y + 0.15f && bottom[k] > y + 0.15f))
                    {
                        // nothing here (or only something overhead): the FIFA apron is the floor
                        float px = (ix + 0.5f) * Cell - HalfX, pz = (iz + 0.5f) * Cell - HalfZ;
                        if (Math.Abs(px) > apronHalfX || pz < apronMinZ || pz > apronMaxZ) return false;
                        floor = 0f;
                    }
                    else
                        floor = top[k] <= y + 0.15f ? top[k] : bottom[k];
                    if (floor < y - 0.25f || floor > y + 0.15f) return false;
                }
            return true;
        }

        /// <summary>
        /// Height of the package floor under the footprint: the highest floor sample (the top of a cell when it is
        /// at or below the feet, else what is below the feet) within -0.25 .. +0.15 m of y over the cells that hold
        /// geometry; NaN when no cell of the footprint has a floor near y (the FIFA apron is the floor then).
        /// </summary>
        public float FloorAt(float x, float y, float z, float radius)
        {
            int ix0, ix1, iz0, iz1;
            if (!Footprint(x, z, radius, out ix0, out ix1, out iz0, out iz1)) return float.NaN;
            float best = float.NaN;
            for (int ix = ix0; ix <= ix1; ix++)
                for (int iz = iz0; iz <= iz1; iz++)
                {
                    int k = ix * Nz + iz;
                    if (float.IsNegativeInfinity(top[k])) continue;
                    float floor = top[k] <= y + 0.15f ? top[k] : bottom[k];
                    if (floor < y - 0.25f || floor > y + 0.15f) continue;
                    if (float.IsNaN(best) || floor > best) best = floor;
                }
            return best;
        }

        /// <summary>Highest sample in the footprint (diagnostics); NaN when the footprint is empty.</summary>
        public float TopAt(float x, float z, float radius)
        {
            int ix0, ix1, iz0, iz1;
            if (!Footprint(x, z, radius, out ix0, out ix1, out iz0, out iz1)) return float.NaN;
            float best = float.NegativeInfinity;
            for (int ix = ix0; ix <= ix1; ix++)
                for (int iz = iz0; iz <= iz1; iz++)
                    best = Math.Max(best, top[ix * Nz + iz]);
            return float.IsNegativeInfinity(best) ? float.NaN : best;
        }

        private bool Footprint(float x, float z, float radius, out int ix0, out int ix1, out int iz0, out int iz1)
        {
            ix0 = (int)Math.Floor((x - radius + HalfX) / Cell); ix1 = (int)Math.Floor((x + radius + HalfX) / Cell);
            iz0 = (int)Math.Floor((z - radius + HalfZ) / Cell); iz1 = (int)Math.Floor((z + radius + HalfZ) / Cell);
            if (ix1 < 0 || iz1 < 0 || ix0 >= Nx || iz0 >= Nz) return false;
            ix0 = Math.Max(ix0, 0); iz0 = Math.Max(iz0, 0); ix1 = Math.Min(ix1, Nx - 1); iz1 = Math.Min(iz1, Nz - 1);
            return true;
        }
    }

    public partial class StadiumImporter
    {
        // FIFA 17 pitch: the lines (105 x 68) and the grass sheet with the run-off (129 x 83.6)
        private const float PitchLineHalfX = 52.5f, PitchLineHalfZ = 34f;
        private const float RefitLineMargin = 2.5f;          // nobody is moved closer than this to the lines
        private const float RefitFootprint = 0.4f;           // radius of a standing character / prop
        private const float RefitClearance = 0.6f;           // free space wanted beyond the footprint, towards the pitch
        private const float RefitStep = 0.25f;
        private const float RefitMaxTravel = 20f;
        private const float RefitRingRadius = 10f;

        /// <summary>Package geometry of the imported slots (after the bench / tunnel carving), world space.</summary>
        private readonly Dictionary<string, List<GeometryImportSection>> importedGeometry = new Dictionary<string, List<GeometryImportSection>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Parallel to importedGeometry: the package's section records (null entries = no record in stadium.json).</summary>
        private readonly Dictionary<string, List<PesStadiumPackage.SectionInfo>> importedSectionInfos = new Dictionary<string, List<PesStadiumPackage.SectionInfo>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Boxes carved out of the package geometry (FIFA benches, FIFA tunnel); the crowd and the collision soups avoid them too.</summary>
        private readonly List<Box3> carveBoxes = new List<Box3>();

        private List<Box3> tunnelBoxes;

        /// <summary>
        /// The boxes of the FIFA tunnels: the sections of playerbenches_mesh that are neither benches nor flat ground
        /// decals or rails (at least 1 m tall; generic_158: the concrete tunnel x 3.6..8.5, z -67..-47, 3.5 m high, and
        /// its glass canopy) and every "entrancetunnel" mesh of the component folder (56 stadiums), each at the
        /// stadium's placement, grown by 5 cm and extended 3 m towards the pitch (the walk-out lane). The package
        /// geometry inside them is removed so a tunnel reads as an opening in the PES stand instead of a concrete
        /// box poking through the terrace, and nothing stands in front of its mouth.
        /// </summary>
        private List<Box3> FifaTunnelBoxes()
        {
            if (tunnelBoxes != null) return tunnelBoxes;
            tunnelBoxes = new List<Box3>();
            // the tunnel sections of playerbenches_mesh stay in both modes (with the PES dugouts only the furniture
            // sections are emptied, StadiumBenches.cs), so their lane is always cut
            EbxAssetEntry benches = BenchMeshEntry();
            if (benches != null)
            {
                Box3? b = MeshBox(benches, "tunnel", sectionName => sectionName.IndexOf("bench", StringComparison.OrdinalIgnoreCase) < 0, 1.0f);
                if (b.HasValue) tunnelBoxes.Add(Lane(b.Value));
            }
            foreach (EbxAssetEntry e in StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly))
            {
                if (e.Filename.IndexOf("entrancetunnel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Box3? b = MeshBox(e, "entrance tunnel", sectionName => true, 0f);
                if (b.HasValue) tunnelBoxes.Add(Lane(b.Value));
            }
            foreach (Box3 box in tunnelBoxes)
                Log("  tunnel: FIFA tunnel box {0}", box);
            return tunnelBoxes;
        }

        /// <summary>Grows a tunnel box by 5 cm and extends it 3 m towards the pitch along its dominant axis.</summary>
        private static Box3 Lane(Box3 box)
        {
            const float grow = 0.05f;
            box.MinX -= grow; box.MaxX += grow; box.MinZ -= grow; box.MaxZ += grow; box.MinY = Math.Min(box.MinY, 0f) - 0.15f; box.MaxY += grow;
            float cx = (box.MinX + box.MaxX) * 0.5f, cz = (box.MinZ + box.MaxZ) * 0.5f;
            if (Math.Abs(cz) >= Math.Abs(cx)) { if (cz < 0) box.MaxZ += 3f; else box.MinZ -= 3f; }
            else { if (cx < 0) box.MaxX += 3f; else box.MinX -= 3f; }
            return box;
        }

        /// <summary>World box of the lod 0 sections of a component mesh that pass the name filter and are at least minHeight tall.</summary>
        private Box3? MeshBox(EbxAssetEntry entry, string what, Func<string, bool> sectionFilter, float minHeight)
        {
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                ulong rid = ((dynamic)asset.RootObject).MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                MeshSet meshSet = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
                if (meshSet == null || meshSet.Lods.Count == 0) return null;
                MeshSetLod lod = meshSet.Lods[0];
                byte[] chunk = lod.InlineData;
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(lod.ChunkId);
                if (chunkEntry != null)
                    using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                        chunk = reader.ReadToEnd();
                if (chunk == null || chunk.Length == 0) return null;
                float[] placement = ComponentPlacement(entry, what);
                Box3 box = new Box3 { MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue };
                int used = 0;
                List<string> names = new List<string>();
                foreach (MeshSetSection section in lod.Sections)
                {
                    if (section.VertexCount == 0 || !sectionFilter(section.Name)) continue;
                    StadiumBlenderExporter.SectionData sd = StadiumBlenderExporter.DecodeSection(lod, section, chunk);
                    float sMinY = float.MaxValue, sMaxY = float.MinValue;
                    for (int v = 0; v < sd.VertexCount; v++) { sMinY = Math.Min(sMinY, sd.Positions[v * 3 + 1]); sMaxY = Math.Max(sMaxY, sd.Positions[v * 3 + 1]); }
                    if (sMaxY - sMinY < minHeight) continue;      // coach lines, ground shadows, bench rails
                    for (int v = 0; v < sd.VertexCount; v++)
                    {
                        float x = sd.Positions[v * 3], y = sd.Positions[v * 3 + 1], z = sd.Positions[v * 3 + 2];
                        StadiumBaseLookup.TransformPoint(placement, ref x, ref y, ref z);
                        box.MinX = Math.Min(box.MinX, x); box.MaxX = Math.Max(box.MaxX, x);
                        box.MinY = Math.Min(box.MinY, y); box.MaxY = Math.Max(box.MaxY, y);
                        box.MinZ = Math.Min(box.MinZ, z); box.MaxZ = Math.Max(box.MaxZ, z);
                    }
                    used++;
                    names.Add(section.Name);
                }
                if (used == 0) return null;
                Log("  {0}: {1} sections of {2}: {3}", what, used, entry.Filename, string.Join(", ", names));
                return box;
            }
            catch (Exception ex)
            {
                Log("  WARNING: {0} box of {1}: {2}", what, entry.Filename, ex.Message);
                return null;
            }
        }

        private static bool InAnyBox(List<Box3> boxes, float x, float y, float z, float grow)
        {
            foreach (Box3 b in boxes)
                if (x >= b.MinX - grow && x <= b.MaxX + grow && y >= b.MinY - grow && y <= b.MaxY + grow && z >= b.MinZ - grow && z <= b.MaxZ + grow)
                    return true;
            return false;
        }

        // ─── the re-fit of pitch-side characters and props ─────────────────────

        private SurfaceGrid BuildSurfaceGrid()
        {
            SurfaceGrid grid = new SurfaceGrid();
            foreach (KeyValuePair<string, List<GeometryImportSection>> kv in importedGeometry)
            {
                if (kv.Key.Equals("roof", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (GeometryImportSection gs in kv.Value)
                    grid.Add(gs.Positions, gs.Indices);
            }
            return grid;
        }

        private enum RefitStatus { Clear, Moved, Stuck }

        /// <summary>Direction (unit, xz) from the point towards the nearest point of the pitch-line rectangle and the distance to it.</summary>
        private static float PitchDirection(float x, float z, out float dx, out float dz)
        {
            float cx = Math.Min(Math.Max(x, -PitchLineHalfX), PitchLineHalfX), cz = Math.Min(Math.Max(z, -PitchLineHalfZ), PitchLineHalfZ);
            dx = cx - x; dz = cz - z;
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            if (d < 1e-6f) { dx = 0; dz = 0; return 0; }
            dx /= d; dz /= d;
            return d;
        }

        private static bool Fits(SurfaceGrid grid, float x, float y, float z, float dx, float dz)
        {
            if (grid.Blocked(x, y, z, RefitFootprint)) return false;
            if (!grid.FloorOk(x, y, z, RefitFootprint)) return false;
            if (grid.Blocked(x + dx * RefitClearance, y, z + dz * RefitClearance, RefitFootprint)) return false;
            return true;
        }

        /// <summary>
        /// A placement inside / under the package geometry is moved towards the pitch (never closer than 2.5 m to
        /// the lines) until it and 0.6 m in front of it are free and the ground under it is level; when the way
        /// towards the pitch is blocked, the nearest fitting spot within 10 m is used.
        /// </summary>
        private RefitStatus Refit(SurfaceGrid grid, float x, float y, float z, out float nx, out float nz, out float travelled)
        {
            nx = x; nz = z; travelled = 0;
            if (!grid.Blocked(x, y, z, RefitFootprint)) return RefitStatus.Clear;
            float dx, dz;
            float dist = PitchDirection(x, z, out dx, out dz);
            if (dist > 0)
            {
                float cx = x, cz = z, t = 0;
                while (t < RefitMaxTravel)
                {
                    cx += dx * RefitStep; cz += dz * RefitStep; t += RefitStep;
                    float ddx, ddz;
                    if (PitchDirection(cx, cz, out ddx, out ddz) < RefitLineMargin) break;
                    if (Fits(grid, cx, y, cz, dx, dz)) { nx = cx; nz = cz; travelled = t; return RefitStatus.Moved; }
                }
            }
            // ring search: nearest fitting spot outside the line margin
            float bestD = float.MaxValue;
            for (float r = RefitStep; r <= RefitRingRadius; r += RefitStep)
            {
                int steps = Math.Max(16, (int)(2 * Math.PI * r / RefitStep));
                for (int i = 0; i < steps; i++)
                {
                    double a = 2 * Math.PI * i / steps;
                    float cx = x + r * (float)Math.Cos(a), cz = z + r * (float)Math.Sin(a);
                    float ddx, ddz;
                    if (PitchDirection(cx, cz, out ddx, out ddz) < RefitLineMargin) continue;
                    float fdx = dx, fdz = dz;
                    if (fdx == 0 && fdz == 0) { fdx = ddx; fdz = ddz; }
                    if (!Fits(grid, cx, y, cz, fdx, fdz)) continue;
                    // prefer the spot closest to the original position
                    if (r < bestD) { bestD = r; nx = cx; nz = cz; }
                }
                if (bestD < float.MaxValue) { travelled = bestD; return RefitStatus.Moved; }
            }
            return RefitStatus.Stuck;
        }

        private static readonly string[] RefitSkipSublevels = { "crowd", "effects", "lighting", "networkregistry", "schematic", "banners", "adboards", "pitch", "trophy", "presentation", "tournament", "exterior", "scoreclock", "crowdnetting", "railings", "roof", "standsupper" };
        private static readonly string[] RefitSkipBlueprints = { "/stadiums/", "/lights/", "/lighting/", "/logic/", "slcsystem", "/crest/" };

        /// <summary>
        /// Every character and prop placement of the stadium's sublevels (the SLC stewards, police, photographers,
        /// ball boys, camera crews, managers, the sideline props, the weather dressing) is tested against the
        /// imported package geometry and moved onto open ground; the ones with no room are moved out of sight.
        /// </summary>
        private void RefitPlacements()
        {
            bool doRefit = settings.RefitPlacements && importedGeometry.Count > 0;
            if (settings.RefitPlacements && importedGeometry.Count == 0)
                Log("  re-fit: no imported geometry, nothing to test against");
            SurfaceGrid grid = doRefit ? BuildSurfaceGrid() : null;
            if (grid != null)
                Log("  re-fit: surface grid from {0} package triangles below {1} m ({2} m cells)", grid.Triangles, grid.MaxY, grid.Cell);
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder + "/";
            int clear = 0, moved = 0, sunk = 0, crests = 0, assets = 0, lifted = 0;
            float totalTravel = 0;
            Dictionary<string, int[]> perLayer = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx().ToList())
            {
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Type != "LayerData" && e.Type != "SubWorldData" && e.Type != "WorldPartData" && e.Type != "DetachedSubWorldData") continue;
                string stem = e.Name.Substring(prefix.Length).ToLowerInvariant();
                if (stem.Contains("/")) continue;
                bool skip = false;
                foreach (string s in RefitSkipSublevels)
                {
                    // the sublevel's own name (tournament, tournament_main ...), not a character layer that happens
                    // to contain the word (slc_tournamentofficial stands at the touchline and must be re-fitted)
                    if (stem.StartsWith(s) || ((s == "networkregistry" || s == "schematic") && stem.Contains(s))) { skip = true; break; }
                }
                if (skip && !stem.StartsWith("standslower")) continue;      // standslower holds the crest boards
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch (Exception) { continue; }
                if (asset == null) continue;
                bool changed = false;
                int[] counts = new int[3];
                foreach (object obj in asset.Objects)
                {
                    if (obj == null) continue;
                    string type = obj.GetType().Name;
                    bool isGroup = type == "StaticModelGroupEntityData";
                    if (!isGroup && type != "ReferenceObjectData" && type != "ObjectReferenceObjectData" && type != "SpatialPrefabReferenceObjectData" && type != "PrefabReferenceObjectData") continue;
                    dynamic o = obj;
                    if (isGroup)
                    {
                        if (grid != null) changed |= RefitGroup(grid, o, counts, ref sunk, ref totalTravel, ref lifted);
                        continue;
                    }
                    PointerRef bp;
                    try { bp = o.Blueprint; } catch (Exception) { continue; }
                    if (bp.Type != PointerRefType.External) continue;
                    EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(bp.External.FileGuid);
                    string bpName = (bpEntry != null ? bpEntry.Name : "").ToLowerInvariant();
                    if (bpName.Contains("/crest/"))
                    {
                        if (!settings.SinkCrests) continue;
                        // crest boards hang on the base stadium's walls: out of sight
                        try
                        {
                            dynamic lt0 = o.BlueprintTransform; dynamic tr0 = lt0.trans;
                            if ((float)tr0.y > SinkDepth + 1f) { tr0.y = (float)tr0.y + SinkDepth; lt0.trans = tr0; o.BlueprintTransform = lt0; crests++; changed = true; }
                        }
                        catch (Exception) { }
                        continue;
                    }
                    if (stem.StartsWith("standslower") || grid == null) continue;
                    bool skipBp = false;
                    foreach (string s in RefitSkipBlueprints) if (bpName.Contains(s)) { skipBp = true; break; }
                    if (skipBp) continue;
                    try
                    {
                        dynamic lt = o.BlueprintTransform;
                        dynamic trans = lt.trans;
                        float x = (float)trans.x, y = (float)trans.y, z = (float)trans.z;
                        if (y < SinkDepth + 1f || y > 6f || Math.Abs(x) > 125f || Math.Abs(z) > 105f) continue;
                        float nx, nz, travel;
                        RefitStatus st = Refit(grid, x, y, z, out nx, out nz, out travel);
                        if (st == RefitStatus.Clear)
                        {
                            counts[0]++;
                            // standing on a package floor a little above the FIFA apron (a PES walkway sheet): feet on it
                            if (!LiftToFloor(grid, trans, x, y, z, ref lifted)) continue;
                        }
                        else if (st == RefitStatus.Moved)
                        {
                            trans.x = nx; trans.z = nz;
                            counts[1]++; totalTravel += travel;
                            LiftToFloor(grid, trans, nx, y, nz, ref lifted);
                        }
                        else
                        {
                            trans.y = y + SinkDepth;
                            counts[2]++;
                            Log("  re-fit: {0}: {1} at ({2:0.0}, {3:0.0}, {4:0.0}) has no room (surface {5:0.00} m): moved out of sight", stem, bpName.Substring(bpName.LastIndexOf('/') + 1), x, y, z, grid.TopAt(x, z, RefitFootprint));
                        }
                        lt.trans = trans;
                        o.BlueprintTransform = lt;
                        changed = true;
                    }
                    catch (Exception ex) { Log("  WARNING: re-fit {0}: {1}", stem, ex.Message); }
                }
                if (changed)
                {
                    App.AssetManager.ModifyEbx(e.Name, asset);
                    assets++;
                }
                if (counts[0] + counts[1] + counts[2] > 0)
                {
                    perLayer[stem] = counts;
                    clear += counts[0]; moved += counts[1]; sunk += counts[2];
                }
            }
            foreach (KeyValuePair<string, int[]> kv in perLayer.OrderBy(k => k.Key))
                Log("  re-fit: {0,-28} {1,4} clear {2,4} moved {3,4} out of sight", kv.Key, kv.Value[0], kv.Value[1], kv.Value[2]);
            Log("  re-fit: {0} placements tested: {1} clear, {2} moved ({3:0.0} m on average), {4} out of sight, {5} set onto the package floor; {6} crest board(s) out of sight; {7} sublevel(s) modified",
                clear + moved + sunk, clear, moved, moved > 0 ? totalTravel / moved : 0, sunk, lifted, crests, assets);
            if (moved + sunk + crests + lifted > 0)
                report.Add(string.Format("Pitch-side characters and props re-fitted: {0} moved onto open ground, {1} without room moved out of sight, {2} set onto the package floor{3}.", moved, sunk, lifted, crests > 0 ? ", " + crests + " crest boards out of sight" : ""));
        }

        /// <summary>
        /// Puts the feet of a placement on the package floor when that floor is up to 15 cm above the placement
        /// (a PES walkway or track sheet over the FIFA apron); a floor below the placement is left alone (the FIFA
        /// apron at 0 carries it). Returns true when y was changed.
        /// </summary>
        private static bool LiftToFloor(SurfaceGrid grid, dynamic trans, float x, float y, float z, ref int lifted)
        {
            float floor = grid.FloorAt(x, y, z, RefitFootprint);
            if (float.IsNaN(floor) || floor <= y + 0.005f || floor > y + 0.15f) return false;
            trans.y = floor;
            lifted++;
            return true;
        }

        /// <summary>Re-fits the instances of a StaticModelGroup whose meshes are props (not stadium components); returns true when the group changed.</summary>
        private bool RefitGroup(SurfaceGrid grid, dynamic group, int[] counts, ref int sunkTotal, ref float totalTravel, ref int lifted)
        {
            bool changed = false;
            dynamic members;
            try { members = group.MemberDatas; } catch (Exception) { return false; }
            for (int i = 0; i < members.Count; i++)
            {
                dynamic member = members[i];
                PointerRef meshRef;
                try { meshRef = member.MeshAsset; } catch (Exception) { continue; }
                if (meshRef.Type != PointerRefType.External) continue;
                EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshRef.External.FileGuid);
                string meshName = (meshEntry != null ? meshEntry.Name : "").ToLowerInvariant();
                if (meshName.Contains("/stadiums/") || meshName.Contains("/light")) continue;
                dynamic transforms = member.InstanceTransforms;
                bool memberChanged = false;
                for (int j = 0; j < transforms.Count; j++)
                {
                    dynamic lt = transforms[j];
                    dynamic trans = lt.trans;
                    float x = (float)trans.x, y = (float)trans.y, z = (float)trans.z;
                    if (y < SinkDepth + 1f || y > 6f || Math.Abs(x) > 125f || Math.Abs(z) > 105f) continue;
                    float nx, nz, travel;
                    RefitStatus st = Refit(grid, x, y, z, out nx, out nz, out travel);
                    if (st == RefitStatus.Clear)
                    {
                        counts[0]++;
                        if (!LiftToFloor(grid, trans, x, y, z, ref lifted)) continue;
                    }
                    else if (st == RefitStatus.Moved) { trans.x = nx; trans.z = nz; counts[1]++; totalTravel += travel; LiftToFloor(grid, trans, nx, y, nz, ref lifted); }
                    else { trans.y = y + SinkDepth; counts[2]++; }
                    lt.trans = trans;
                    transforms[j] = lt;
                    memberChanged = true;
                }
                if (memberChanged)
                {
                    member.InstanceTransforms = transforms;
                    members[i] = member;
                    changed = true;
                }
            }
            return changed;
        }
    }
}
