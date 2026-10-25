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
    /// The bench areas of a FIFA 17 stadium are data, not geometry: the per-stadium broadcasting record
    /// Broadcasting/Attribsys/presentation/stadium/stadium_stadium_&lt;id&gt; (reached through the record
    /// stadium_&lt;id&gt; of Broadcasting/attribsys/AttribsysDataAssetsCollection) holds manager_area_home/away
    /// (the technical areas: measured on every stadium, their x range is the x range of that side's bench and
    /// they end about 1.8 m behind the touchline) and subs_area_home/away (the substitutes' warm-up strips), all in
    /// cm as (top-left x, top-left z, bottom-right x, bottom-right z) with top-left = (max x, max z); the DB
    /// stadiums row carries the same technical areas (stadhometechzone*, stadawaytechzone*). When the PES dugouts
    /// replace the FIFA benches, the record is rewritten from the dugout chairs of the package: home is the -X
    /// dugout, away the +X one (the FIFA convention, 7 of 7 measured stadiums). The DB values are printed for the
    /// user, the plugin cannot write the database.
    /// </summary>
    public partial class StadiumImporter
    {
        private const string AttribsysCollectionPath = "Broadcasting/attribsys/AttribsysDataAssetsCollection";
        private const float TechZoneMaxZ = -35.8f;      // metres; the touchline is at z -34
        private const float WarmUpStripMinZ = -38.5f, WarmUpStripLength = 20f, WarmUpStripGap = 2f;

        /// <summary>Package sections tagged "dugout" (PES dugouts, chairs, 4th official desk), world space, collected by the slot imports.</summary>
        private readonly List<GeometryImportSection> dugoutSections = new List<GeometryImportSection>();

        private sealed class BenchSide
        {
            public string Name;
            public bool Found;
            public float MinX, MaxX, MinZ, MaxZ, TopY;
            public int Triangles;
            public override string ToString()
            {
                return Found ? string.Format("x {0:0.0}..{1:0.0} z {2:0.0}..{3:0.0} top {4:0.00} m ({5} triangles)", MinX, MaxX, MinZ, MaxZ, TopY, Triangles) : "not found";
            }
        }

        /// <summary>Rectangle in metres (max x, max z, min x, min z): the record's Vec4 / Vec2 pair layout.</summary>
        private struct Area
        {
            public float MaxX, MaxZ, MinX, MinZ;
            public override string ToString() { return string.Format("x {0:0.0}..{1:0.0} z {2:0.0}..{3:0.0}", MinX, MaxX, MinZ, MaxZ); }
        }

        // ─── the FIFA bench mesh with the PES dugouts: furniture out, tunnel in ──

        private static readonly string[] BenchFurnitureWords = { "bench", "coach", "plyr", "dugout", "seat" };

        private static bool IsBenchFurnitureName(string name)
        {
            foreach (string w in BenchFurnitureWords)
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// playerbenches_mesh is not only the benches: in generic_158 it also carries the players' tunnel (the concrete
        /// walls, the glass canopy, the sign above the mouth and the asphalt shadow under it). With the PES dugouts in
        /// use only the furniture goes: the sections named after benches / coach lines / player seats, and every low
        /// section (under 1 m: rails, painted lines) that does not lie inside the tunnel's box. The tunnel sections
        /// stay and are put into the depth and shadow categories themselves, because the mesh's unnamed dedicated
        /// depth section (a welded copy of the whole mesh, benches included) is emptied with the furniture.
        /// Returns the names of the cleared sections (empty = nothing changed).
        /// </summary>
        private List<string> ClearBenchFurniture(EbxAssetEntry entry, out List<string> kept)
        {
            kept = new List<string>();
            List<string> cleared = new List<string>();
            if (entry == null || clearedMeshes.Contains(entry.Name)) return cleared;
            if (settings.SlotTargets.Values.Any(v => string.Equals(v, entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Log("  benches: {0} receives package geometry, not cleared", entry.Filename);
                return cleared;
            }
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            ulong rid = ((dynamic)asset.RootObject).MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            MeshSet meshSet = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
            if (meshSet == null || meshSet.Lods.Count == 0)
            {
                Log("  benches: {0}: mesh set not readable", entry.Filename);
                return cleared;
            }
            // the tunnel box: the named sections without a furniture word that are at least 1 m tall (lod 0, mesh space)
            MeshSetLod lod0 = meshSet.Lods[0];
            byte[] chunk0 = LodChunk(lod0);
            Box3 tunnel = new Box3 { MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue };
            bool tunnelFound = false;
            Dictionary<string, Box3> sectionBoxes = new Dictionary<string, Box3>(StringComparer.OrdinalIgnoreCase);
            if (chunk0 != null)
                foreach (MeshSetSection section in lod0.Sections)
                {
                    if (section.Name == "" || section.VertexCount == 0) continue;
                    Box3 b = SectionBox(lod0, section, chunk0);
                    sectionBoxes[section.Name] = b;
                    if (IsBenchFurnitureName(section.Name) || b.MaxY - b.MinY < 1f) continue;
                    tunnel.MinX = Math.Min(tunnel.MinX, b.MinX); tunnel.MaxX = Math.Max(tunnel.MaxX, b.MaxX);
                    tunnel.MinY = Math.Min(tunnel.MinY, b.MinY); tunnel.MaxY = Math.Max(tunnel.MaxY, b.MaxY);
                    tunnel.MinZ = Math.Min(tunnel.MinZ, b.MinZ); tunnel.MaxZ = Math.Max(tunnel.MaxZ, b.MaxZ);
                    tunnelFound = true;
                }
            const float grow = 0.5f;
            foreach (MeshSetLod lod in meshSet.Lods)
            {
                List<MeshSetSection> keptSections = new List<MeshSetSection>();
                foreach (MeshSetSection section in lod.Sections)
                {
                    if (section.VertexCount == 0) continue;
                    bool clear;
                    if (section.Name == "") clear = true;                       // the welded depth copy of the whole mesh
                    else if (IsBenchFurnitureName(section.Name)) clear = true;
                    else
                    {
                        Box3 b;
                        bool low = sectionBoxes.TryGetValue(section.Name, out b) && b.MaxY - b.MinY < 1f;
                        bool inTunnel = tunnelFound && sectionBoxes.TryGetValue(section.Name, out b)
                            && b.MinX >= tunnel.MinX - grow && b.MaxX <= tunnel.MaxX + grow
                            && b.MinY >= tunnel.MinY - grow && b.MaxY <= tunnel.MaxY + grow
                            && b.MinZ >= tunnel.MinZ - grow && b.MaxZ <= tunnel.MaxZ + grow;
                        clear = low && !inTunnel;
                    }
                    if (clear)
                    {
                        section.PrimitiveCount = 0;
                        section.VertexCount = 0;
                        if (section.Name != "" && !cleared.Contains(section.Name)) cleared.Add(section.Name);
                    }
                    else
                    {
                        keptSections.Add(section);
                        if (!kept.Contains(section.Name)) kept.Add(section.Name);
                    }
                }
                // the kept sections write depth and shadows themselves now that the dedicated depth section is gone
                foreach (MeshSetSection section in keptSections)
                {
                    if (!lod.IsSectionInCategory(section, MeshSubsetCategory.MeshSubsetCategory_Opaque)) continue;
                    if (!lod.IsSectionInCategory(section, MeshSubsetCategory.MeshSubsetCategory_ZOnly)) lod.SetSectionCategory(section, MeshSubsetCategory.MeshSubsetCategory_ZOnly);
                    if (!lod.IsSectionInCategory(section, MeshSubsetCategory.MeshSubsetCategory_Shadow)) lod.SetSectionCategory(section, MeshSubsetCategory.MeshSubsetCategory_Shadow);
                }
            }
            if (cleared.Count == 0 && kept.Count > 0)
            {
                Log("  benches: {0}: no furniture section found, nothing cleared", entry.Filename);
                return cleared;
            }
            App.AssetManager.ModifyRes(rid, meshSet);
            entry.LinkAsset(resEntry);
            if (kept.Count == 0) clearedMeshes.Add(entry.Name);
            Log("  benches: {0}: cleared {1}; kept {2}{3}", entry.Filename, string.Join(", ", cleared), kept.Count > 0 ? string.Join(", ", kept) : "nothing",
                tunnelFound ? string.Format(" (tunnel box x {0:0.0}..{1:0.0} y {2:0.0}..{3:0.0} z {4:0.0}..{5:0.0})", tunnel.MinX, tunnel.MaxX, tunnel.MinY, tunnel.MaxY, tunnel.MinZ, tunnel.MaxZ) : "");
            return cleared;
        }

        private static byte[] LodChunk(MeshSetLod lod)
        {
            byte[] chunk = lod.InlineData;
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(lod.ChunkId);
            if (chunkEntry != null)
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                    chunk = reader.ReadToEnd();
            return chunk != null && chunk.Length > 0 ? chunk : null;
        }

        /// <summary>Mesh-space box of one section's stored vertices.</summary>
        private static Box3 SectionBox(MeshSetLod lod, MeshSetSection section, byte[] chunk)
        {
            StadiumBlenderExporter.SectionData sd = StadiumBlenderExporter.DecodeSection(lod, section, chunk);
            Box3 b = new Box3 { MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue };
            for (int v = 0; v < sd.VertexCount; v++)
            {
                float x = sd.Positions[v * 3], y = sd.Positions[v * 3 + 1], z = sd.Positions[v * 3 + 2];
                b.MinX = Math.Min(b.MinX, x); b.MaxX = Math.Max(b.MaxX, x);
                b.MinY = Math.Min(b.MinY, y); b.MaxY = Math.Max(b.MaxY, y);
                b.MinZ = Math.Min(b.MinZ, z); b.MaxZ = Math.Max(b.MaxZ, z);
            }
            return b;
        }

        private void RewriteBenchAreas()
        {
            if (dugoutSections.Count == 0)
            {
                Log("  bench areas: the package has no dugout sections; the stadium's bench areas are left as they are");
                report.Add("Bench areas: the package has no dugout furniture, the stadium's bench areas were not changed.");
                return;
            }
            BenchSide[] sides = DetectPesDugouts();
            Log("  bench areas: PES dugout home (-x): {0}; away (+x): {1}", sides[0], sides[1]);
            if (!sides[0].Found && !sides[1].Found)
            {
                report.Add("Bench areas: no dugout chairs found in the package's dugout sections, the stadium's bench areas were not changed.");
                return;
            }
            // one side only: mirror it (the other dugout has no chairs in the package)
            if (!sides[0].Found) sides[0] = Mirror(sides[1], "home");
            if (!sides[1].Found) sides[1] = Mirror(sides[0], "away");

            Area managerHome = TechZone(sides[0]), managerAway = TechZone(sides[1]);
            Area subsHome = WarmUpStrip(sides[0], -1), subsAway = WarmUpStrip(sides[1], +1);
            Log("  bench areas: manager areas home {0}, away {1}; warm-up strips home {2}, away {3}", managerHome, managerAway, subsHome, subsAway);

            int stadiumId;
            if (!TryGetStadiumId(settings.StadiumFolder, out stadiumId))
            {
                Log("  bench areas: no stadium id at the end of '{0}'; the stadium_<id> record cannot be found", settings.StadiumFolder);
                report.Add("Bench areas: the stadium folder has no id, the broadcasting record was not changed.");
                return;
            }
            EbxAssetEntry recordEntry = FindAttribsysAsset("stadium_" + stadiumId);
            if (recordEntry == null)
            {
                Log("  bench areas: no stadium_{0} record in {1} (a duplicate made with an older duplicator has none): bench areas not written", stadiumId, AttribsysCollectionPath);
                report.Add(string.Format("Bench areas: no stadium_{0} record exists (Tools > Duplicate Stadium of the current build adds it); the broadcasting record was not changed.", stadiumId));
                PrintDbValues(managerHome, managerAway, stadiumId);
                return;
            }
            EbxAsset asset = App.AssetManager.GetEbx(recordEntry);
            dynamic root = asset.RootObject;
            WriteArea(root, "manager_area_home", managerHome);
            WriteArea(root, "manager_area_away", managerAway);
            WriteArea(root, "subs_area_home", subsHome);
            WriteArea(root, "subs_area_away", subsAway);
            App.AssetManager.ModifyEbx(recordEntry.Name, asset);
            Log("  bench areas: {0} rewritten", recordEntry.Name);
            report.Add(string.Format("Bench areas moved to the PES dugouts ({0}): manager areas home {1}, away {2}; warm-up strips home {3}, away {4}.",
                recordEntry.Filename, managerHome, managerAway, subsHome, subsAway));
            PrintDbValues(managerHome, managerAway, stadiumId);
        }

        private void PrintDbValues(Area home, Area away, int stadiumId)
        {
            string line = string.Format("DB stadiums row {0} (cm): stadhometechzoneminx={1} stadhometechzonemaxx={2} stadhometechzoneminz={3} stadhometechzonemaxz={4} stadawaytechzoneminx={5} stadawaytechzonemaxx={6} stadawaytechzoneminz={7} stadawaytechzonemaxz={8}",
                stadiumId, Cm(home.MinX), Cm(home.MaxX), Cm(home.MinZ), Cm(home.MaxZ), Cm(away.MinX), Cm(away.MaxX), Cm(away.MinZ), Cm(away.MaxZ));
            Log("  bench areas: {0}", line);
            report.Add("Technical areas for the database (the plugin cannot write it): " + line);
        }

        private static int Cm(float m) { return (int)Math.Round(m * 100f); }

        private static bool TryGetStadiumId(string folder, out int id)
        {
            id = 0;
            if (string.IsNullOrEmpty(folder)) return false;
            int i = folder.LastIndexOf('_');
            return i >= 0 && int.TryParse(folder.Substring(i + 1), out id);
        }

        /// <summary>The asset the collection's record with this access key points at (null when there is none).</summary>
        private EbxAssetEntry FindAttribsysAsset(string accessKey)
        {
            EbxAssetEntry collectionEntry = App.AssetManager.GetEbxEntry(AttribsysCollectionPath);
            if (collectionEntry == null) return null;
            EbxAsset collection = App.AssetManager.GetEbx(collectionEntry);
            dynamic records = ((dynamic)collection.RootObject).AttribsysDataAssets;
            foreach (dynamic rec in records)
            {
                string key;
                try { key = (string)rec.AccessKey; } catch (Exception) { continue; }
                if (!string.Equals(key, accessKey, StringComparison.OrdinalIgnoreCase)) continue;
                PointerRef r = rec.DataAsset;
                return r.Type == PointerRefType.External ? App.AssetManager.GetEbxEntry(r.External.FileGuid) : null;
            }
            return null;
        }

        /// <summary>Writes the Vec4 (x, y, z, w) = (max x, max z, min x, min z) and its _top_left / _bottom_right Vec2 pair, in cm.</summary>
        private static void WriteArea(dynamic root, string field, Area a)
        {
            float tlx = Cm(a.MaxX), tlz = Cm(a.MaxZ), brx = Cm(a.MinX), brz = Cm(a.MinZ);
            switch (field)
            {
                case "manager_area_home":
                    { dynamic v = root.manager_area_home; v.x = tlx; v.y = tlz; v.z = brx; v.w = brz; root.manager_area_home = v; }
                    { dynamic v = root.manager_area_home_top_left; v.x = tlx; v.y = tlz; root.manager_area_home_top_left = v; }
                    { dynamic v = root.manager_area_home_bottom_right; v.x = brx; v.y = brz; root.manager_area_home_bottom_right = v; }
                    break;
                case "manager_area_away":
                    { dynamic v = root.manager_area_away; v.x = tlx; v.y = tlz; v.z = brx; v.w = brz; root.manager_area_away = v; }
                    { dynamic v = root.manager_area_away_top_left; v.x = tlx; v.y = tlz; root.manager_area_away_top_left = v; }
                    { dynamic v = root.manager_area_away_bottom_right; v.x = brx; v.y = brz; root.manager_area_away_bottom_right = v; }
                    break;
                case "subs_area_home":
                    { dynamic v = root.subs_area_home; v.x = tlx; v.y = tlz; v.z = brx; v.w = brz; root.subs_area_home = v; }
                    { dynamic v = root.subs_area_home_top_left; v.x = tlx; v.y = tlz; root.subs_area_home_top_left = v; }
                    { dynamic v = root.subs_area_home_bottom_right; v.x = brx; v.y = brz; root.subs_area_home_bottom_right = v; }
                    break;
                case "subs_area_away":
                    { dynamic v = root.subs_area_away; v.x = tlx; v.y = tlz; v.z = brx; v.w = brz; root.subs_area_away = v; }
                    { dynamic v = root.subs_area_away_top_left; v.x = tlx; v.y = tlz; root.subs_area_away_top_left = v; }
                    { dynamic v = root.subs_area_away_bottom_right; v.x = brx; v.y = brz; root.subs_area_away_bottom_right = v; }
                    break;
            }
        }

        /// <summary>
        /// The technical area in front of a dugout: the dugout's x range, from half a metre in front of the chairs
        /// (never deeper than 42 m, never shallower than 1.7 m) to 1.8 m behind the touchline, like the originals
        /// (generic_158: bench x -2.1..4.5 / zone -1.9..4.3, bench front z -46.7 / zone -39..-36).
        /// </summary>
        private static Area TechZone(BenchSide b)
        {
            float minZ = Math.Min(Math.Max(b.MaxZ + 0.5f, -42f), TechZoneMaxZ - 1.7f);
            return new Area { MinX = b.MinX, MaxX = b.MaxX, MinZ = minZ, MaxZ = TechZoneMaxZ };
        }

        /// <summary>The warm-up strip beside a dugout: 20 m along the touchline outward from the dugout's outer end, inside the goal lines.</summary>
        private static Area WarmUpStrip(BenchSide b, int sign)
        {
            float outer = sign < 0 ? -b.MinX : b.MaxX;        // distance of the dugout's outer end from the centre line
            float x0 = Math.Min(outer + WarmUpStripGap, 30f);
            float x1 = Math.Min(x0 + WarmUpStripLength, 52f);
            float a = sign * x0, c = sign * x1;
            return new Area { MinX = Math.Min(a, c), MaxX = Math.Max(a, c), MinZ = WarmUpStripMinZ, MaxZ = TechZoneMaxZ };
        }

        private static BenchSide Mirror(BenchSide b, string name)
        {
            return new BenchSide { Name = name, Found = true, MinX = -b.MaxX, MaxX = -b.MinX, MinZ = b.MinZ, MaxZ = b.MaxZ, TopY = b.TopY, Triangles = b.Triangles };
        }

        /// <summary>
        /// The two dugouts in the package's dugout sections: triangle centroids 0.15..2.5 m above the ground (chairs
        /// and shelters, not ground slabs) left (x &lt;= -2) and right (x &gt;= 2) of the centre line (the fourth
        /// official's desk sits in between); on each side the chairs are grouped along x with 1.5 m gaps and the
        /// widest group at least 3 m wide is the dugout (else the group with most triangles).
        /// </summary>
        private BenchSide[] DetectPesDugouts()
        {
            List<float[]> left = new List<float[]>(), right = new List<float[]>();
            foreach (GeometryImportSection gs in dugoutSections)
            {
                float[] p = gs.Positions;
                uint[] idx = gs.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
                    float cx = (p[a] + p[b] + p[c]) / 3f, cy = (p[a + 1] + p[b + 1] + p[c + 1]) / 3f, cz = (p[a + 2] + p[b + 2] + p[c + 2]) / 3f;
                    if (cy < 0.15f || cy > 2.5f) continue;
                    float top = Math.Max(p[a + 1], Math.Max(p[b + 1], p[c + 1]));
                    if (cx <= -2f) left.Add(new[] { cx, cy, cz, top });
                    else if (cx >= 2f) right.Add(new[] { cx, cy, cz, top });
                }
            }
            return new[] { SideBox(left, "home"), SideBox(right, "away") };
        }

        private BenchSide SideBox(List<float[]> pts, string name)
        {
            BenchSide side = new BenchSide { Name = name };
            if (pts.Count < 30) return side;
            pts.Sort((u, v) => u[0].CompareTo(v[0]));
            List<List<float[]>> groups = new List<List<float[]>>();
            List<float[]> cur = null;
            float lastX = float.NaN;
            foreach (float[] q in pts)
            {
                if (cur == null || q[0] - lastX > 1.5f) { cur = new List<float[]>(); groups.Add(cur); }
                cur.Add(q);
                lastX = q[0];
            }
            List<float[]> best = null;
            float bestWidth = 0;
            foreach (List<float[]> g in groups)
            {
                if (g.Count < 30) continue;
                float w = g[g.Count - 1][0] - g[0][0];
                if (w >= 3f && w > bestWidth) { best = g; bestWidth = w; }
            }
            if (best == null)
                best = groups.OrderByDescending(g => g.Count).First();
            if (best.Count < 30) return side;
            // x range between the 2nd and 98th percentile (stray bits of the shelter do not widen the area)
            int lo = Math.Max(0, (int)(best.Count * 0.02)), hi = Math.Min(best.Count - 1, (int)(best.Count * 0.98));
            side.Found = true;
            side.Triangles = best.Count;
            side.MinX = best[lo][0]; side.MaxX = best[hi][0];
            side.MinZ = float.MaxValue; side.MaxZ = float.MinValue; side.TopY = float.MinValue;
            foreach (float[] q in best)
            {
                side.MinZ = Math.Min(side.MinZ, q[2]); side.MaxZ = Math.Max(side.MaxZ, q[2]); side.TopY = Math.Max(side.TopY, q[3]);
            }
            Log("  bench areas: {0}: {1} chair group(s) on this side, the dugout is {2}", name, groups.Count(g => g.Count >= 30), side);
            return side;
        }
    }
}
