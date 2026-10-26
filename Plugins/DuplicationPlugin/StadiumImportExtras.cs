using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DuplicationPlugin
{
    /// <summary>
    /// The parts of the PES stadium import that touch the rest of the stadium: the crowd point cloud, the ball
    /// collision data, the FIFA components the package replaces (railings, nets, scoreboard, city skirt, static
    /// adboards, banners, light fixtures) and the FIFA bench area (dugout furniture of the package dropped, the
    /// package geometry carved around the FIFA benches).
    /// </summary>
    public partial class StadiumImporter
    {
        private const float SinkDepth = -1000f;

        // ─── crowd ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces the Data of the stadium's CrowdPlacementPointCloudDataAsset with the package's spectator positions.
        /// Position, Orientation and SeatColor come from the package; HomeAwayNeutral / HomeAwayUltra /
        /// HomeAwayFiftyFifty / Attendance are copied from the nearest point (in xz) of the stadium's own cloud, so the
        /// home / away sections and the attendance pattern of the base stadium are kept. Returns the number of points.
        /// </summary>
        private int ImportCrowd()
        {
            if (pkg.Crowd == null)
            {
                Log("  the package has no crowd (crowd.bin): the stadium's crowd stays as it is");
                return 0;
            }
            string file = pkg.FullPath(pkg.Crowd.File);
            if (!File.Exists(file))
            {
                Log("  WARNING: crowd file not found: {0}", file);
                return 0;
            }
            float pitch, setback;
            List<CrowdPointRecord> points = CrowdReader.Read(file, out pitch, out setback);
            List<EbxAssetEntry> clouds = StadiumBaseLookup.CrowdPointClouds(settings.StadiumFolder);
            if (clouds.Count == 0)
            {
                EbxAssetEntry byName = FindCrowdPointCloud();
                if (byName != null) clouds.Add(byName);
            }
            if (clouds.Count == 0)
            {
                Log("  WARNING: no CrowdPlacementPointCloudDataAsset found for {0}: crowd not imported", settings.StadiumFolder);
                return 0;
            }
            EbxAssetEntry cloudEntry = clouds[0];
            // a stadium with several clouds (boleynground_325: one per tier) gets everything in the first; the others are emptied
            for (int c = 1; c < clouds.Count; c++)
            {
                try
                {
                    EbxAsset other = App.AssetManager.GetEbx(clouds[c]);
                    dynamic otherData = ((dynamic)other.RootObject).Data;
                    int n = otherData.Count;
                    otherData.Clear();
                    App.AssetManager.ModifyEbx(clouds[c].Name, other);
                    Log("  crowd: {0} emptied ({1} points); the package crowd goes into {2}", clouds[c].Name, n, cloudEntry.Name);
                }
                catch (Exception ex) { Log("  WARNING: crowd: {0}: {1}", clouds[c].Name, ex.Message); }
            }
            EbxAsset asset = App.AssetManager.GetEbx(cloudEntry);
            dynamic root = asset.RootObject;
            dynamic data = root.Data;
            int nBase = data.Count;
            Log("  crowd: {0} package points (seat pitch {1} m) -> {2} ({3} points before)", points.Count, pitch, cloudEntry.Name, nBase);
            // FIFA17.exe (FUN_147688700) takes a point's SeatColor from a UserData channel named "SeatColor" when the
            // asset has one, instead of the point's own value; generic_crowd, Old Trafford and Bernabeu have none
            try
            {
                int nUser = root.UserData.Count;
                if (nUser > 0) Log("  crowd: WARNING: {0} has {1} UserData channel(s); a channel named SeatColor would replace the package seat colours", cloudEntry.Name, nUser);
            }
            catch (Exception) { }

            // the base cloud's attributes, indexed by a 2 m grid for the nearest-neighbour transfer
            float[] bx = new float[nBase], bz = new float[nBase], bn = new float[nBase], bu = new float[nBase], bf = new float[nBase], ba = new float[nBase];
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            const float cell = 2f;
            for (int i = 0; i < nBase; i++)
            {
                dynamic d = data[i];
                dynamic p = d.Position;
                bx[i] = (float)p.x; bz[i] = (float)p.z;
                baseCrowd.Add(new float[] { (float)p.x, (float)p.y, (float)p.z });
                bn[i] = (float)d.HomeAwayNeutral; bu[i] = (float)d.HomeAwayUltra; bf[i] = (float)d.HomeAwayFiftyFifty; ba[i] = (float)d.Attendance;
                long key = GridKey(bx[i], bz[i], cell);
                List<int> l;
                if (!grid.TryGetValue(key, out l)) grid[key] = l = new List<int>();
                l.Add(i);
            }
            int Nearest(float x, float z)
            {
                int cx = (int)Math.Floor(x / cell), cz = (int)Math.Floor(z / cell);
                for (int ring = 0; ring <= 16; ring++)
                {
                    int best = -1; float bd = float.MaxValue;
                    for (int dx = -ring; dx <= ring; dx++)
                        for (int dz = -ring; dz <= ring; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                            List<int> l;
                            if (!grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out l)) continue;
                            foreach (int i in l)
                            {
                                float d = (bx[i] - x) * (bx[i] - x) + (bz[i] - z) * (bz[i] - z);
                                if (d < bd) { bd = d; best = i; }
                            }
                        }
                    if (best >= 0) return best;
                }
                return -1;
            }

            data.Clear();
            int transferred = 0, inBoxes = 0;
            List<Box3> boxes = CarveBoxes();
            List<CrowdPointRecord> kept = new List<CrowdPointRecord>(points.Count);
            foreach (CrowdPointRecord pt in points)
            {
                if (boxes.Count > 0 && InAnyBox(boxes, pt.X, pt.Y, pt.Z, 0.3f)) { inBoxes++; continue; }
                kept.Add(pt);
            }
            // the crowd system's agent limit (see CrowdAgentLimit): a cloud with more points overflows the game's crowd
            // buffers (Camp Nou: 78,173 points -> crash in the crowd code, 2026-10-25)
            int limit = CrowdAgentLimit();
            int thinned = 0;
            if (kept.Count > limit)
            {
                thinned = kept.Count - limit;
                kept = ThinEvenly(kept, limit);
                Log("  crowd: {0} spectators are more than the crowd system holds ({1}, SwarmEntityData.MaxAgentCount of {2}): {3} left out, spread evenly over the stands (empty seats)",
                    kept.Count + thinned, limit, CrowdSwarmAsset, thinned);
                report.Add(string.Format("Crowd: {0} of the package's {1} spectators written - FIFA 17's crowd system holds at most {0} (more crash the game).", limit, kept.Count + thinned));
            }
            foreach (CrowdPointRecord pt in kept)
            {
                dynamic np = TypeLibrary.CreateObject("CrowdPlacementPointCloudData");
                // the EBX Vec3 is a runtime-generated type: never `new Vec3` (that is MeshSetPlugin's own struct)
                dynamic pos = TypeLibrary.CreateObject("Vec3");
                pos.x = pt.X; pos.y = pt.Y; pos.z = pt.Z;
                np.Position = pos;
                dynamic col = TypeLibrary.CreateObject("Vec3");
                col.x = pt.R; col.y = pt.G; col.z = pt.B;
                np.SeatColor = col;
                np.Orientation = pt.OrientationDeg;
                int k = nBase > 0 ? Nearest(pt.X, pt.Z) : -1;
                if (k >= 0)
                {
                    np.HomeAwayNeutral = bn[k]; np.HomeAwayUltra = bu[k]; np.HomeAwayFiftyFifty = bf[k]; np.Attendance = ba[k];
                    transferred++;
                }
                else
                {
                    np.HomeAwayNeutral = 0.5f; np.HomeAwayUltra = 0.5f; np.HomeAwayFiftyFifty = 0.5f; np.Attendance = 0.9f;
                }
                np.AttendantHideable = false;
                data.Add(np);
            }
            App.AssetManager.ModifyEbx(cloudEntry.Name, asset);
            Log("  crowd: {0} points written, attributes of the base cloud transferred to {1} of them{2}{3}", kept.Count, transferred,
                inBoxes > 0 ? ", " + inBoxes + " left out inside the FIFA bench / tunnel boxes" : "",
                thinned > 0 ? ", " + thinned + " left out for the crowd system's limit" : "");
            return kept.Count;
        }

        /// <summary>The Swarm system of the stadium crowd (CrowdSystem_PF places it).</summary>
        private const string CrowdSwarmAsset = "content/common/components/crowd/crowd_default_swarmsys";

        /// <summary>
        /// The most spectators the stadium crowd can have: SwarmEntityData.MaxAgentCount of the crowd's Swarm system
        /// (65,000 in FIFA 17), at most 65,000. FIFA17.exe (FUN_1476994f0) creates at most MaxAgentCount agents (field
        /// offset 0x94 in the exe's SwarmEntityData type info) and sets the agent count of the position data store to
        /// that number without resizing it; FUN_147688700 (via FUN_14768c850) still writes every point of a cloud from
        /// the cloud's first agent on. FIFA's own clouds stay far below (Old Trafford 45,336, Bernabeu 44,039).
        /// </summary>
        private int CrowdAgentLimit()
        {
            const int fifaLimit = 65000;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(CrowdSwarmAsset);
                EbxAsset a = e != null ? App.AssetManager.GetEbx(e) : null;
                if (a != null)
                {
                    foreach (object o in a.Objects)
                    {
                        if (o == null || o.GetType().Name != "SwarmEntityData")
                            continue;
                        uint v = (uint)((dynamic)o).MaxAgentCount;
                        if (v > 0)
                            return (int)Math.Min(v, (uint)fifaLimit);
                    }
                }
            }
            catch (Exception ex) { Log("  crowd: {0} not readable ({1}); limit {2}", CrowdSwarmAsset, ex.Message, fifaLimit); }
            return fifaLimit;
        }

        /// <summary>
        /// <paramref name="keep"/> of the points, in their order: the ones whose index has the lowest hash, so the
        /// left-out ones are spread over every stand like empty seats (deterministic).
        /// </summary>
        private static List<CrowdPointRecord> ThinEvenly(List<CrowdPointRecord> points, int keep)
        {
            if (keep >= points.Count)
                return points;
            int[] order = Enumerable.Range(0, points.Count).OrderBy(i => MixIndex((uint)i)).Take(keep).OrderBy(i => i).ToArray();
            List<CrowdPointRecord> result = new List<CrowdPointRecord>(keep);
            foreach (int i in order)
                result.Add(points[i]);
            return result;
        }

        private static uint MixIndex(uint x)
        {
            // 32-bit integer hash (lowbias32)
            x ^= x >> 16; x *= 0x7feb352d;
            x ^= x >> 15; x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        /// <summary>The base stadium's spectator positions (x, y, z) as they were before the package crowd replaced them.</summary>
        private readonly List<float[]> baseCrowd = new List<float[]>();

        private static long GridKey(float x, float z, float cell)
        {
            return ((long)(int)Math.Floor(x / cell) << 32) ^ (uint)(int)Math.Floor(z / cell);
        }

        /// <summary>A point cloud under the component folder, by name (fallback when no crowd placement entity references one).</summary>
        private EbxAssetEntry FindCrowdPointCloud()
        {
            string compPrefix = "content/worlds/components/stadiums/" + settings.StadiumNameOnly + "/";
            EbxAssetEntry byName = App.AssetManager.EnumerateEbx("CrowdPlacementPointCloudDataAsset").FirstOrDefault(e => e.Name.StartsWith(compPrefix, StringComparison.OrdinalIgnoreCase));
            return byName;
        }

        // ─── ball collision ────────────────────────────────────────────────────

        /// <summary>Writes the package's collision soups into the stadium's FifaPhysicsResourceAsset res (same material name).</summary>
        private int ImportPhysics()
        {
            int written = 0;
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder + "/";
            List<EbxAssetEntry> physAssets = App.AssetManager.EnumerateEbx("FifaPhysicsResourceAsset").Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (physAssets.Count == 0)
            {
                Log("  WARNING: no FifaPhysicsResourceAsset under {0}: collision not written", prefix);
                return 0;
            }
            foreach (PesStadiumPackage.PhysicsInfo pi in pkg.Physics)
            {
                string suffix = "_fifaphysicsdata_" + pi.Material;
                EbxAssetEntry entry = physAssets.FirstOrDefault(e => e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    Log("  WARNING: no physics asset ending with {0}: {1} not written", suffix, pi.File);
                    continue;
                }
                string file = pkg.FullPath(pi.File);
                if (!File.Exists(file))
                {
                    Log("  WARNING: collision file not found: {0}", file);
                    continue;
                }
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length % 36 != 0 || bytes.Length == 0)
                {
                    Log("  WARNING: {0}: {1} bytes is not a triangle soup", pi.File, bytes.Length);
                    continue;
                }
                // the soup follows the carved geometry: nothing left inside the FIFA bench / tunnel boxes
                List<Box3> boxes = CarveBoxes();
                if (boxes.Count > 0)
                {
                    float[] soup = new float[bytes.Length / 4];
                    Buffer.BlockCopy(bytes, 0, soup, 0, bytes.Length);
                    int before = soup.Length / 9, cutTotal = 0;
                    foreach (Box3 box in boxes)
                    {
                        int cut;
                        // the ground plane (y 0) stays: only the steps and walls inside the box go
                        soup = BoxClip.CarveSoup(soup, box.MinX, box.MaxX, Math.Max(box.MinY, 0.05f), box.MaxY, box.MinZ, box.MaxZ, out cut);
                        cutTotal += cut;
                    }
                    if (cutTotal > 0)
                    {
                        bytes = new byte[soup.Length * 4];
                        Buffer.BlockCopy(soup, 0, bytes, 0, bytes.Length);
                        Log("  collision: {0}: {1} triangle(s) cut along the FIFA bench / tunnel boxes ({2} -> {3} triangles)", pi.File, cutTotal, before, soup.Length / 9);
                    }
                }
                if (WritePhysicsRes(entry, bytes, pi.File))
                    written++;
            }
            return written;
        }

        private bool WritePhysicsRes(EbxAssetEntry entry, byte[] bytes, string what)
        {
            ulong rid;
            try { rid = ((dynamic)App.AssetManager.GetEbx(entry).RootObject).PhysicsData; }
            catch (Exception ex) { Log("  WARNING: {0}: no PhysicsData res id ({1})", entry.Name, ex.Message); return false; }
            ResAssetEntry res = App.AssetManager.GetResEntry(rid);
            if (res == null)
            {
                Log("  WARNING: {0}: res {1:X16} not found", entry.Name, rid);
                return false;
            }
            App.AssetManager.ModifyRes(rid, bytes);
            Log("  collision: {0} -> {1} ({2} triangles)", what, res.Name, bytes.Length / 36);
            return true;
        }

        /// <summary>Empties a per-material collision soup (one triangle far below the ground; an empty res is not written).</summary>
        private void ClearPhysicsMaterial(string material)
        {
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder + "/";
            string suffix = "_fifaphysicsdata_" + material;
            EbxAssetEntry entry = App.AssetManager.EnumerateEbx("FifaPhysicsResourceAsset").FirstOrDefault(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;
            byte[] bytes = new byte[36];
            float[] tri = { 0, SinkDepth, 0, 1, SinkDepth, 0, 0, SinkDepth, 1 };
            Buffer.BlockCopy(tri, 0, bytes, 0, 36);
            WritePhysicsRes(entry, bytes, "cleared '" + material + "' soup");
        }

        // ─── FIFA components the package replaces ──────────────────────────────

        /// <summary>Sets every section of every lod of the mesh to zero primitives (the asset, its blueprint and its StaticModelGroup members stay).</summary>
        /// <summary>
        /// The stadium's bench mesh: playerbenches_mesh (81 of the 92 levels), else the component mesh whose name starts
        /// with "playerbenches" (white_hart_lane_116: playerbenches_fixed_mesh), the shortest name first.
        /// </summary>
        private EbxAssetEntry BenchMeshEntry()
        {
            string prefix = "content/worlds/components/stadiums/" + settings.StadiumNameOnly + "/";
            EbxAssetEntry exact = App.AssetManager.GetEbxEntry(prefix + "playerbenches_mesh");
            if (exact != null) return exact;
            EbxAssetEntry best = null;
            foreach (EbxAssetEntry e in StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly))
            {
                string stem = e.Filename.ToLowerInvariant();
                if (!stem.StartsWith("playerbench") || stem.Contains("glass")) continue;
                if (best == null || e.Filename.Length < best.Filename.Length) best = e;
            }
            return best;
        }

        private bool ClearComponentMesh(string meshStem)
        {
            string name = "content/worlds/components/stadiums/" + settings.StadiumNameOnly + "/" + meshStem;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
            {
                Log("  clear: {0} not found (nothing to do)", name);
                return false;
            }
            return ClearComponentMeshEntry(entry);
        }

        private readonly HashSet<string> clearedMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool ClearComponentMeshEntry(EbxAssetEntry entry)
        {
            string name = entry.Name;
            string meshStem = entry.Filename;
            if (clearedMeshes.Contains(name)) return false;
            if (settings.SlotTargets.Values.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
            {
                Log("  clear: {0} receives package geometry, not cleared", meshStem);
                return false;
            }
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                ulong rid = ((dynamic)asset.RootObject).MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                MeshSet meshSet = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
                if (meshSet == null)
                {
                    Log("  clear: {0}: mesh set not readable", meshStem);
                    return false;
                }
                int sections = 0;
                foreach (MeshSetLod lod in meshSet.Lods)
                    foreach (MeshSetSection section in lod.Sections)
                    {
                        section.PrimitiveCount = 0;
                        section.VertexCount = 0;
                        sections++;
                    }
                App.AssetManager.ModifyRes(rid, meshSet);
                entry.LinkAsset(resEntry);
                clearedMeshes.Add(name);
                Log("  clear: {0}: {1} sections in {2} lods set to zero primitives", meshStem, sections, meshSet.Lods.Count);
                return true;
            }
            catch (Exception ex)
            {
                Log("  WARNING: clear {0} failed: {1}", meshStem, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Moves the ObjectReferenceObjectData placements of a stadium sublevel whose blueprint path contains one of
        /// the fragments 1000 m below the ground (the objects stay in the level and its network registry).
        /// </summary>
        private int SinkPlacements(string sublevelStem, string[] blueprintFragments)
        {
            string name = "content/worlds/stadiums/" + settings.StadiumFolder + "/" + sublevelStem;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
            {
                Log("  sink: {0} not found (nothing to do)", name);
                return 0;
            }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Log("  WARNING: sink {0}: {1}", sublevelStem, ex.Message); return 0; }
            int moved = 0, already = 0;
            foreach (object obj in asset.Objects)
            {
                if (obj == null || obj.GetType().Name != "ObjectReferenceObjectData") continue;
                dynamic o = obj;
                PointerRef bp;
                try { bp = o.Blueprint; } catch (Exception) { continue; }
                if (bp.Type != PointerRefType.External) continue;
                EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(bp.External.FileGuid);
                string bpName = bpEntry != null ? bpEntry.Name : "";
                if (!blueprintFragments.Any(f => bpName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                try
                {
                    dynamic lt = o.BlueprintTransform;
                    dynamic trans = lt.trans;
                    if ((float)trans.y < SinkDepth + 1f) { already++; continue; }
                    trans.y = (float)trans.y + SinkDepth;
                    lt.trans = trans;
                    o.BlueprintTransform = lt;
                    moved++;
                }
                catch (Exception ex) { Log("  WARNING: sink {0}: {1}", bpName, ex.Message); }
            }
            if (moved > 0)
                App.AssetManager.ModifyEbx(entry.Name, asset);
            Log("  sink: {0}: {1} placement(s) moved {2} m down{3}", sublevelStem, moved, -SinkDepth, already > 0 ? ", " + already + " already down" : "");
            return moved;
        }

        /// <summary>Sinks the instances of StaticModelGroup members whose mesh path contains a fragment (e.g. "/light/"), in the main level.</summary>
        private int SinkGroupMembers(string[] meshFragments)
        {
            string name = "content/worlds/stadiums/" + settings.StadiumFolder + "/" + settings.StadiumFolder;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
            {
                Log("  sink: level {0} not found", name);
                return 0;
            }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Log("  WARNING: sink members: {0}", ex.Message); return 0; }
            int moved = 0;
            bool changed = false;
            foreach (object obj in asset.Objects)
            {
                if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData") continue;
                dynamic group = obj;
                dynamic members;
                try { members = group.MemberDatas; } catch (Exception) { continue; }
                for (int i = 0; i < members.Count; i++)
                {
                    dynamic member = members[i];
                    PointerRef meshRef;
                    try { meshRef = member.MeshAsset; } catch (Exception) { continue; }
                    if (meshRef.Type != PointerRefType.External) continue;
                    EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshRef.External.FileGuid);
                    string meshName = meshEntry != null ? meshEntry.Name : "";
                    if (!meshFragments.Any(f => meshName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    dynamic transforms = member.InstanceTransforms;
                    for (int j = 0; j < transforms.Count; j++)
                    {
                        dynamic lt = transforms[j];
                        dynamic trans = lt.trans;
                        if ((float)trans.y < SinkDepth + 1f) continue;
                        trans.y = (float)trans.y + SinkDepth;
                        lt.trans = trans;
                        transforms[j] = lt;
                        moved++;
                    }
                    member.InstanceTransforms = transforms;
                    members[i] = member;
                    changed = true;
                }
            }
            if (changed)
                App.AssetManager.ModifyEbx(entry.Name, asset);
            Log("  sink: {0} group instance(s) of {1} moved {2} m down", moved, string.Join("|", meshFragments), -SinkDepth);
            return moved;
        }

        // ─── the FIFA bench area ───────────────────────────────────────────────

        private struct Box3
        {
            public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public bool Contains(float x, float y, float z) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ;
            public override string ToString() => string.Format("x {0:0.0}..{1:0.0} y {2:0.0}..{3:0.0} z {4:0.0}..{5:0.0}", MinX, MaxX, MinY, MaxY, MinZ, MaxZ);
        }

        private Box3? benchBox;
        private bool benchBoxSearched;

        /// <summary>
        /// The box of the target stadium's playerbenches_mesh bench sections (section names with "bench"), lod 0,
        /// grown by 0.4 m sideways and 0.3 m up: the package geometry inside it is removed so the FIFA dugouts do not
        /// sit in PES hedges, walls or furniture.
        /// </summary>
        private Box3? FifaBenchBox()
        {
            if (benchBoxSearched) return benchBox;
            benchBoxSearched = true;
            EbxAssetEntry entry = BenchMeshEntry();
            if (entry == null)
            {
                Log("  bench area: no playerbenches mesh in {0}, no carving", settings.StadiumNameOnly);
                return null;
            }
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
                if (lod.PartTransforms.Count > 0)
                    Log("  bench area: the mesh has {0} part transforms; the bench box is taken from the stored vertices", lod.PartTransforms.Count);
                float[] placement = ComponentPlacement(entry, "bench area");
                Box3 box = new Box3 { MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue };
                int used = 0;
                foreach (MeshSetSection section in lod.Sections)
                {
                    if (section.Name.IndexOf("bench", StringComparison.OrdinalIgnoreCase) < 0 || section.VertexCount == 0) continue;
                    StadiumBlenderExporter.SectionData sd = StadiumBlenderExporter.DecodeSection(lod, section, chunk);
                    for (int v = 0; v < sd.VertexCount; v++)
                    {
                        float x = sd.Positions[v * 3], y = sd.Positions[v * 3 + 1], z = sd.Positions[v * 3 + 2];
                        StadiumBaseLookup.TransformPoint(placement, ref x, ref y, ref z);
                        box.MinX = Math.Min(box.MinX, x); box.MaxX = Math.Max(box.MaxX, x);
                        box.MinY = Math.Min(box.MinY, y); box.MaxY = Math.Max(box.MaxY, y);
                        box.MinZ = Math.Min(box.MinZ, z); box.MaxZ = Math.Max(box.MaxZ, z);
                    }
                    used++;
                }
                if (used == 0)
                {
                    Log("  bench area: no 'bench' section in {0}, no carving", entry.Name);
                    return null;
                }
                box.MinX -= 0.4f; box.MaxX += 0.4f; box.MinZ -= 0.4f; box.MaxZ += 0.4f; box.MinY = Math.Min(box.MinY, 0f) - 0.3f; box.MaxY += 0.3f;
                benchBox = box;
                Log("  bench area: FIFA benches at {0} ({1} sections)", box, used);
                return box;
            }
            catch (Exception ex)
            {
                Log("  WARNING: bench area: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>The world placement of a component mesh of the base stadium (identity when it is not placed by the level data).</summary>
        private float[] ComponentPlacement(EbxAssetEntry meshEntry, string what)
        {
            string placedBy;
            float[] m = StadiumBaseLookup.Placement(settings.StadiumFolder, meshEntry, out placedBy);
            if (m == null) return StadiumBaseLookup.Identity();
            if (!StadiumBaseLookup.IsIdentity(m))
                Log("  {0}: {1} is placed by {2} with {3}", what, meshEntry.Filename, placedBy, StadiumBaseLookup.Describe(m));
            return m;
        }

        /// <summary>
        /// The boxes carved out of the package geometry: the FIFA bench box (when the benches are kept) and the FIFA
        /// tunnel boxes (the playerbenches tunnel when the benches are kept, the entrancetunnel meshes always, they
        /// stay). Computed once.
        /// </summary>
        private List<Box3> CarveBoxes()
        {
            if (carveBoxes.Count > 0 || carveBoxesSearched) return carveBoxes;
            carveBoxesSearched = true;
            if (settings.KeepFifaBenches)
            {
                Box3? bench = FifaBenchBox();
                if (bench.HasValue) carveBoxes.Add(bench.Value);
            }
            if (settings.CarveTunnel)
                carveBoxes.AddRange(FifaTunnelBoxes());
            return carveBoxes;
        }

        private bool carveBoxesSearched;

        // ─── the whole post-import pass ────────────────────────────────────────

        /// <summary>Everything that is not a mesh import: run once after the slots.</summary>
        private void ImportStadiumWide()
        {
            if (settings.ImportCrowd)
            {
                task.Update("Crowd");
                Log("--- crowd");
                try
                {
                    int n = ImportCrowd();
                    if (n > 0) report.Add(string.Format("Crowd: {0} spectator positions written on the PES stands.", n));
                }
                catch (Exception ex) { Log("  WARNING: crowd import failed: {0}", ex.Message); }
            }
            if (settings.ImportPhysics)
            {
                task.Update("Collision");
                Log("--- collision");
                try
                {
                    int n = ImportPhysics();
                    if (settings.ClearRailings) ClearPhysicsMaterial("plastic");
                    if (n > 0) report.Add(string.Format("Collision: {0} soup(s) replaced.", n));
                }
                catch (Exception ex) { Log("  WARNING: collision import failed: {0}", ex.Message); }
            }
            task.Update("FIFA components");
            Log("--- FIFA components replaced by the package");
            List<string> cleared = new List<string>();
            if (settings.ClearRailings && ClearComponentMesh("railings_mesh")) cleared.Add("railings");
            if (settings.ClearCrowdNetting)
            {
                bool any = false;
                foreach (string stem in new[] { "crowdnetting_north_mesh", "crowdnetting_south_mesh", "crowdnetting_mesh" })
                    any |= ClearComponentMesh(stem);
                if (any) cleared.Add("crowd nets");
            }
            cleared.AddRange(ClearBackdrop());
            if (settings.ClearScoreboard && ClearComponentMesh("scoreclock_mesh")) cleared.Add("scoreboard");
            if (settings.ClearAdboardPlinth && ClearComponentMesh("adboards_mesh")) cleared.Add("adboard plinth");
            if (settings.ClearOtherComponents)
            {
                int others = 0;
                EbxAssetEntry benchMesh = BenchMeshEntry();
                foreach (EbxAssetEntry e in StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly))
                {
                    string stem = e.Filename.ToLowerInvariant();
                    if (settings.SlotTargets.Values.Any(v => string.Equals(v, e.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    if (stem.Contains("pitch") || stem.Contains("entrancetunnel")) continue;
                    if (settings.KeepFifaBenches && stem.Contains("playerbench")) continue;
                    if (benchMesh != null && string.Equals(e.Name, benchMesh.Name, StringComparison.OrdinalIgnoreCase)) continue;   // furniture only, tunnel kept (StadiumBenches.cs)
                    if (!backdropCleared && IsBackdropMesh(stem)) continue;     // the painted city wall stays (StadiumExterior.cs)
                    if (ClearComponentMeshEntry(e)) { others++; cleared.Add(e.Filename); }
                }
                Log("  clear: {0} other component mesh(es) of {1} emptied", others, settings.StadiumNameOnly);
            }
            int sunk = 0;
            if (settings.SinkBanners) sunk += SinkPlacements("banners", new[] { "/banner/" });
            if (settings.SinkStaticAdboards) sunk += SinkPlacements("adboards", new[] { "/adboard/" });
            if (settings.SinkLightFixtures) sunk += SinkGroupMembers(new[] { "/light/light_fixture" });
            if (cleared.Count > 0 || sunk > 0)
                report.Add(string.Format("FIFA components cleared: {0}{1}.", string.Join(", ", cleared), sunk > 0 ? "; " + sunk + " prop placements moved out of sight" : ""));
            if (settings.RefitLensFlares)
            {
                task.Update("Lens flares");
                Log("--- lens flares and fog cones vs the PES lamps");
                try { RefitLensFlares(); }
                catch (Exception ex) { Log("  WARNING: lens flare re-fit failed: {0}", ex.Message); }
                Log("--- roof lights vs the PES lamps");
                try { RefitRoofLights(); }
                catch (Exception ex) { Log("  WARNING: roof light re-fit failed: {0}", ex.Message); }
            }
            if (settings.RefitPlacements || settings.SinkCrests)
            {
                task.Update("Pitch-side characters and props");
                Log("--- pitch-side characters and props vs the package geometry");
                try { RefitPlacements(); }
                catch (Exception ex) { Log("  WARNING: re-fit failed: {0}", ex.Message); }
            }
            if (settings.RefitPlacements)
            {
                Log("--- crowd flags vs the PES crowd");
                try { RefitCrowdFlags(); }
                catch (Exception ex) { Log("  WARNING: crowd flag re-fit failed: {0}", ex.Message); }
            }
            task.Update("Adboards and screen");
            Log("--- LED adboard distances (DB) measured on the package");
            try { MeasureAdboardDistances(); }
            catch (Exception ex) { Log("  WARNING: adboard measure failed: {0}", ex.Message); }
            if (settings.ClearScoreboard)
            {
                Log("--- the FIFA video screen on the package's screen");
                try { WireJumbotron(); }
                catch (Exception ex) { Log("  WARNING: video screen failed: {0}", ex.Message); }
            }
            if (settings.ExteriorGroundPlane)
            {
                task.Update("Exterior ground");
                Log("--- exterior ground under the PES surroundings");
                try { BuildExteriorGroundPlane(); }
                catch (Exception ex) { Log("  WARNING: exterior ground plane failed: {0}", ex.Message); }
            }
        }
    }
}
