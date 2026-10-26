using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicationPlugin
{
    /// <summary>
    /// The package's baked lighting (converter: lightmap UVs, an Enlighten radiosity system per mesh, sky visibility,
    /// night lightmap; lighting/&lt;slot&gt;.fblight, FbLight.cs) written into the target stadium:
    /// <list type="bullet">
    /// <item>Lighting/&lt;folder&gt; (EnlightenDatabase res): a new system per mesh in free atlas space, and the
    /// mesh instance's chart record (key = FNV-1 of the mesh's root instance GUID and placement) pointing at it
    /// (EnlightenDbEditor). The rectangles are chosen after the FIFA components are cleared (BakedLightingPlanner):
    /// the chart records of replaced and emptied meshes do not hold space, a coarser level of the mesh is taken when
    /// the finest does not fit, and systems that light only replaced or emptied meshes are freed when that helps.</item>
    /// <item>the EnlightenDataAsset's SkyVisibilityTexture (4x the atlas) and the night StaticEnlightenData's
    /// StaticIrradianceTexture / StaticDirectionTexture (FluxLightmapScale = 5x): the mesh's blocks copied into its
    /// rectangle (mip 0; FIFA's have one mip).</item>
    /// <item>meshes with a rectangle get EnlightenType_Static, those without one EnlightenType_Dynamic.</item>
    /// </list>
    /// A previous import of the same package (same system GUIDs) is replaced.
    /// </summary>
    public partial class StadiumImporter
    {
        private sealed class BakedSlot
        {
            public PesStadiumPackage.SlotInfo Slot;
            public string MeshName;
            public FbLight Light;
        }

        /// <summary>Slots whose lightmaps were read (they keep their lightmap UVs when imported); placed at write time.</summary>
        private readonly Dictionary<string, BakedSlot> bakedPlan = new Dictionary<string, BakedSlot>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BakedLightingPlanner.Placement> bakedWritten = new List<BakedLightingPlanner.Placement>();
        private bool bakedCommitted;
        private EnlightenDbEditor lightingDb;
        private ResAssetEntry lightingRes;
        private EbxAsset lightingAsset;
        private string lightingName;

        private bool SlotGetsBakedLighting(string slotName) { return bakedPlan.ContainsKey(slotName); }

        /// <summary>Opens the stadium's lighting database and reads the package's lightmaps (before the meshes are imported).</summary>
        private void PlanBakedLighting(List<PesStadiumPackage.SlotInfo> slots)
        {
            bakedPlan.Clear();
            bakedWritten.Clear();
            bakedCommitted = false;
            lightingDb = null;
            List<PesStadiumPackage.SlotInfo> lit = slots.Where(s => !string.IsNullOrEmpty(s.LightingFile)).ToList();
            if (lit.Count == 0)
            {
                Log("--- baked lighting: the package has none (converted without it); lighting stays as the dynamic lighting option says");
                return;
            }
            if (!settings.BakedLighting)
            {
                RemovePreviousBakedLighting(lit);
                return;
            }
            Log("--- baked lighting: {0} mesh(es) with lightmaps in the package", lit.Count);
            if (!OpenLightingDatabase()) return;

            foreach (PesStadiumPackage.SlotInfo s in lit)
            {
                FbLight light;
                try { light = FbLight.Read(pkg.FullPath(s.LightingFile)); }
                catch (Exception ex)
                {
                    Log("  WARNING: {0}: {1} not usable ({2}); the mesh keeps dynamic lighting", s.Name, s.LightingFile, ex.Message);
                    continue;
                }
                bakedPlan[s.Name] = new BakedSlot { Slot = s, MeshName = settings.SlotTargets[s.Name], Light = light };
                Log("  {0}: lightmap {1} (system {2})", s.Name, string.Join(", ", light.Levels.Select(v => string.Format("{0}x{1} at {2:0.00} m/texel", v.Width, v.Height, v.MetersPerTexel))),
                    new Guid(light.SystemGuid));
            }
            if (bakedPlan.Count > 0)
                Log("  the meshes keep their lightmap UVs; their atlas rectangles are chosen after the FIFA components are cleared");
        }

        /// <summary>Reads the stadium's EnlightenDatabase res (Lighting/&lt;folder&gt;) into <see cref="lightingDb"/>; false (logged) when it has none.</summary>
        private bool OpenLightingDatabase()
        {
            lightingDb = null;
            string folder = settings.StadiumFolder;
            lightingName = "content/worlds/stadiums/" + folder + "/Lighting/" + folder;
            EbxAssetEntry dataEntry = App.AssetManager.GetEbxEntry(lightingName);
            if (dataEntry == null)
            {
                Log("  WARNING: no lighting database asset {0}: the meshes keep dynamic lighting", lightingName);
                return false;
            }
            lightingAsset = App.AssetManager.GetEbx(dataEntry);
            ulong rid = ((dynamic)lightingAsset.RootObject).DatabaseResource;
            lightingRes = App.AssetManager.GetResEntry(rid);
            if (lightingRes == null)
            {
                Log("  WARNING: {0}: database res {1:X16} not found: the meshes keep dynamic lighting", lightingName, rid);
                return false;
            }
            byte[] data;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(lightingRes)))
                data = reader.ReadToEnd();
            byte[] meta = (lightingRes.ModifiedEntry != null && lightingRes.ModifiedEntry.ResMeta != null) ? lightingRes.ModifiedEntry.ResMeta : lightingRes.ResMeta;
            if (meta == null || meta.Length < 16)
            {
                Log("  WARNING: {0}: the database res has no 16-byte meta: the meshes keep dynamic lighting", lightingName);
                return false;
            }
            uint relocBytes = BitConverter.ToUInt32(meta, 8);
            lightingMetaStale = !EnlightenDbEditor.ResMetaMatches(meta, data.Length);
            if (lightingMetaStale)
                Log("  note: {0}: its res meta says {1} data + {2} relocation bytes, the res holds {3} (the data size an earlier import of r6 / r7 left); it is corrected when the database is written",
                    lightingName, BitConverter.ToUInt32(meta, 4), relocBytes, data.Length);
            try { lightingDb = new EnlightenDbEditor(data, relocBytes); }
            catch (Exception ex)
            {
                Log("  WARNING: {0}: the database could not be read ({1}): the meshes keep dynamic lighting", lightingName, ex.Message);
                lightingDb = null;
                return false;
            }
            Log("  {0}: atlas {1}x{2}, {3} systems, {4} chart records, {5:0.0}% of the atlas in use", lightingName, lightingDb.AtlasWidth, lightingDb.AtlasHeight,
                lightingDb.Systems.Count, lightingDb.Instances.Count, 100.0 * lightingDb.Occupancy().Count(b => b) / lightingDb.Occupancy().Length);
            return true;
        }

        private bool lightingMetaStale;

        /// <summary>Writes the rebuilt database with a res meta whose data and relocation sizes match it (EnlightenDbEditor.ResMeta).</summary>
        private void CommitLightingDatabase(byte[] data, uint relocBytes)
        {
            byte[] oldMeta = (lightingRes.ModifiedEntry != null && lightingRes.ModifiedEntry.ResMeta != null) ? lightingRes.ModifiedEntry.ResMeta : lightingRes.ResMeta;
            byte[] meta = EnlightenDbEditor.ResMeta(oldMeta, data.Length, relocBytes);
            App.AssetManager.ModifyRes(lightingRes.ResRid, data, meta);
            Log("  {0}: res meta {1} (version {2}, {3} data bytes, {4} relocation bytes, {5})", lightingName, BitConverter.ToString(meta).Replace("-", " ").ToLowerInvariant(),
                BitConverter.ToUInt32(meta, 0), BitConverter.ToUInt32(meta, 4), BitConverter.ToUInt32(meta, 8), BitConverter.ToUInt32(meta, 12));
        }

        /// <summary>Baked lighting off: this package's systems of an earlier import leave the database (the meshes are dynamic now).</summary>
        private void RemovePreviousBakedLighting(List<PesStadiumPackage.SlotInfo> lit)
        {
            Log("--- baked lighting off: lighting systems of an earlier import of this package");
            if (!OpenLightingDatabase()) return;
            List<Guid> guids = new List<Guid>();
            foreach (PesStadiumPackage.SlotInfo s in lit)
            {
                try { guids.Add(new Guid(FbLight.Read(pkg.FullPath(s.LightingFile)).SystemGuid)); }
                catch (Exception ex) { Log("  {0}: {1} not readable ({2})", s.Name, s.LightingFile, ex.Message); }
            }
            int repairedTextures = RepairLightingTextureChunks();
            if (repairedTextures > 0)
                report.Add(string.Format("Baked lighting off: {0} lighting texture chunk(s) an earlier import had written with logical size 0 repaired.", repairedTextures));
            int n = lightingDb.RemoveSystems(guids);
            if (n == 0 && !lightingMetaStale)
                Log("  none in {0}", lightingName);
            else
            {
                // also when nothing is removed but the res meta still has the stale data size of an r6 / r7 import
                uint relocBytes;
                byte[] data = lightingDb.Build(out relocBytes);
                CommitLightingDatabase(data, relocBytes);
                Log("  {0} system(s) removed from {1}", n, lightingName);
                report.Add(string.Format("Baked lighting off: {0} lighting system(s) of an earlier import of this package removed from {1}{2}.", n, lightingName,
                    lightingMetaStale ? "; its res meta (data size) corrected" : ""));
            }
            lightingDb = null;
        }

        /// <summary>Instance key of a stadium mesh (root instance GUID + the base stadium's placement of it, identity when it has none).</summary>
        private bool TryInstanceKey(string meshName, out uint key, bool log)
        {
            key = 0;
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshName);
            if (meshEntry == null) return false;
            Guid root = App.AssetManager.GetEbx(meshEntry).RootInstanceGuid;
            string placedBy;
            float[] placement = StadiumBaseLookup.Placement(settings.StadiumFolder, meshEntry, out placedBy) ?? StadiumBaseLookup.Identity();
            key = EnlightenDbEditor.InstanceKey(root, placement);
            if (log) Log("  {0}: instance key {1:x8} (root instance {2}, {3})", meshName, key, root, placedBy ?? "identity placement");
            return true;
        }

        /// <summary>
        /// Chooses the rectangles, writes the systems, chart records and texture blocks (after the meshes are imported and
        /// the FIFA components cleared) and sets the meshes' EnlightenType.
        /// </summary>
        private void WriteBakedLighting()
        {
            if (lightingDb == null || bakedPlan.Count == 0) return;
            Log("--- baked lighting: atlas rectangles and writing");
            List<BakedLightingPlanner.Candidate> candidates = new List<BakedLightingPlanner.Candidate>();
            HashSet<uint> freedKeys = new HashSet<uint>();
            List<BakedSlot> noKey = new List<BakedSlot>();
            foreach (BakedSlot b in bakedPlan.Values)
            {
                uint key;
                if (!TryInstanceKey(b.MeshName, out key, true))
                {
                    Log("  WARNING: {0}: mesh {1} not found; no lighting written for it", b.Slot.Name, b.MeshName);
                    noKey.Add(b);
                    continue;
                }
                candidates.Add(new BakedLightingPlanner.Candidate { Name = b.Slot.Name, Key = key, Light = b.Light });
                freedKeys.Add(key);
            }
            int clearedKeys = 0;
            foreach (string cleared in clearedMeshes)
            {
                uint key;
                if (TryInstanceKey(cleared, out key, false) && freedKeys.Add(key)) clearedKeys++;
            }
            Log("  chart records that hold no space: {0} of the lit meshes, {1} of the emptied FIFA components", candidates.Count, clearedKeys);

            BakedLightingPlanner.Plan plan = BakedLightingPlanner.Make(lightingDb, candidates, freedKeys, s => Log(s));
            if (plan.PreviousSystemsRemoved > 0) Log("  {0} system(s) of a previous import of this package removed", plan.PreviousSystemsRemoved);
            foreach (int i in plan.FreedSystems)
            {
                EnlightenDbEditor.SystemInfo s = lightingDb.Systems[i];
                Log("  freed: system {0} ({1}) {2}x{3} at ({4}, {5}): it lit only meshes the import replaced or emptied ({6} chart record(s))", i, s.Guid, s.W, s.H, s.X, s.Y,
                    lightingDb.RecordsIn(s).Count);
            }
            float aw = lightingDb.AtlasWidth, ah = lightingDb.AtlasHeight;
            foreach (BakedLightingPlanner.Placement p in plan.Placed)
            {
                FbLight.Level v = p.Data;
                bool known = lightingDb.Instances.Any(i => i.Key == p.Mesh.Key);
                lightingDb.SetInstanceUv(p.Mesh.Key, v.Width / aw, v.Height / ah, p.X / aw, p.Y / ah);
                lightingDb.AddSystem(new EnlightenDbEditor.NewSystem
                {
                    Guid = p.Mesh.Light.SystemGuid, X = p.X, Y = p.Y, Width = v.Width, Height = v.Height,
                    BoxMin = p.Mesh.Light.BoxMin, BoxMax = p.Mesh.Light.BoxMax, Gesg = v.Gesg,
                    Budget = v.Budget, Workspace = v.Workspace, Pixels = v.Pixels, Segments = v.Segments
                });
                Log("  {0}: {1}x{2} texels at ({3}, {4}), {5} ({6:0.00} m per texel); chart record {7:x8} {8}; system {9} ({10} pixels, {11} segments)", p.Mesh.Name, v.Width, v.Height,
                    p.X, p.Y, p.Level == 0 ? "full size" : "level " + p.Level + " of " + (p.Mesh.Light.Levels.Count - 1), v.MetersPerTexel, p.Mesh.Key, known ? "moved" : "added",
                    new Guid(p.Mesh.Light.SystemGuid), v.Pixels, v.Segments);
            }
            foreach (BakedLightingPlanner.Candidate c in plan.Unplaced)
                Log("  WARNING: {0}: no free rectangle in the {1}x{2} atlas even at {3}x{4} (its smallest level); the mesh {5}", c.Name, lightingDb.AtlasWidth, lightingDb.AtlasHeight,
                    c.Light.Levels[c.Light.Levels.Count - 1].Width, c.Light.Levels[c.Light.Levels.Count - 1].Height,
                    settings.DynamicLighting ? "gets dynamic lighting" : "keeps its EnlightenType (dynamic lighting option off)");

            uint relocBytes;
            byte[] data = lightingDb.Build(out relocBytes);

            // ---- from here the project changes
            bakedCommitted = true;
            CommitLightingDatabase(data, relocBytes);
            Log("  {0}: {1} systems ({2} new, {3} removed), {4} chart records, {5} bytes ({6} relocations)", lightingName,
                lightingDb.Systems.Count - lightingDb.RemovedCount + plan.Placed.Count, plan.Placed.Count, lightingDb.RemovedCount, lightingDb.Instances.Count, data.Length, relocBytes / 4);
            bakedWritten.AddRange(plan.Placed);

            // textures: sky visibility (4x) from the EnlightenDataAsset, irradiance / direction (flux scale) from <folder>_flux_night
            int flux = 0;
            try { flux = Convert.ToInt32(((dynamic)lightingAsset.RootObject).FluxLightmapScale); } catch (Exception) { }
            object skyTex = ((dynamic)lightingAsset.RootObject).SkyVisibilityTexture;
            PatchLightingTexture(skyTex, "sky visibility", 4, "BC7", 16, plan.Placed, v => v.SkyBc7);
            EbxAssetEntry fluxEntry = App.AssetManager.GetEbxEntry(lightingName + "_flux_night");
            if (fluxEntry == null)
                Log("  no {0}_flux_night: no night lightmap written", lightingName);
            else if (flux != 5)
                Log("  WARNING: FluxLightmapScale is {0} (the package's night lightmaps are 5x): no night lightmap written", flux);
            else
            {
                EbxAsset fluxAsset = App.AssetManager.GetEbx(fluxEntry);
                object irrTex = ((dynamic)fluxAsset.RootObject).StaticIrradianceTexture;
                object dirTex = ((dynamic)fluxAsset.RootObject).StaticDirectionTexture;
                PatchLightingTexture(irrTex, "night irradiance", 5, "BC6", 16, plan.Placed, v => v.IrrBc6h);
                PatchLightingTexture(dirTex, "night direction", 5, "BC1", 8, plan.Placed, v => v.DirBc1);
            }
            // a lighting texture skipped above may still carry the logical size 0 of an r6 / r7 import
            RepairLightingTextureChunks();

            foreach (BakedLightingPlanner.Placement p in plan.Placed)
                SetEnlightenType(bakedPlan[p.Mesh.Name].MeshName, 2);
            if (settings.DynamicLighting)
            {
                foreach (BakedLightingPlanner.Candidate c in plan.Unplaced)
                    SetEnlightenType(bakedPlan[c.Name].MeshName, 0);
                foreach (BakedSlot b in noKey)
                    SetEnlightenType(b.MeshName, 0);
            }
            report.Add(string.Format("Baked lighting: {0} mesh(es) got an Enlighten system and lightmaps in {1} (EnlightenType_Static{2}){3}{4}.", plan.Placed.Count, lightingName,
                plan.Placed.Any(p => p.Level > 0) ? "; coarser level for " + string.Join(", ", plan.Placed.Where(p => p.Level > 0).Select(p => p.Mesh.Name)) : "",
                plan.FreedSystems.Count > 0 ? "; " + plan.FreedSystems.Count + " FIFA system(s) that lit only replaced or emptied meshes removed" : "",
                plan.Unplaced.Count > 0 ? "; no room for " + string.Join(", ", plan.Unplaced.Select(c => c.Name)) + " (dynamic)" : ""));
        }

        /// <summary>The meshes planned for baked lighting get dynamic lighting (the write failed before the database changed).</summary>
        private void BakedLightingFallBack()
        {
            if (bakedCommitted || !settings.DynamicLighting) return;
            foreach (BakedSlot b in bakedPlan.Values)
                SetEnlightenType(b.MeshName, 0);
        }

        /// <summary>0 = EnlightenType_Dynamic, 2 = EnlightenType_Static.</summary>
        private void SetEnlightenType(string meshName, int type)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(meshName);
            if (entry == null) return;
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic root = asset.RootObject;
            try
            {
                int current = (int)root.EnlightenType;
                if (current != type)
                {
                    root.EnlightenType = type;
                    App.AssetManager.ModifyEbx(entry.Name, asset);
                    Log("  {0}: EnlightenType {1} -> {2}", meshName, current, type == 2 ? "Static" : type == 0 ? "Dynamic" : type.ToString());
                }
            }
            catch (Exception ex) { Log("  WARNING: {0}: EnlightenType not set ({1})", meshName, ex.Message); }
        }

        /// <summary>Copies each placed mesh's blocks into its rectangle (scale x the atlas position) of a lighting texture's mip 0.</summary>
        private void PatchLightingTexture(object pointer, string label, int scale, string format, int blockBytes, List<BakedLightingPlanner.Placement> placed, Func<FbLight.Level, byte[]> blocks)
        {
            if (!(pointer is PointerRef) || ((PointerRef)pointer).Type != PointerRefType.External)
            {
                Log("  {0}: no texture referenced", label);
                return;
            }
            EbxAssetEntry texEntry = App.AssetManager.GetEbxEntry(((PointerRef)pointer).External.FileGuid);
            if (texEntry == null) { Log("  WARNING: {0}: texture asset not found", label); return; }
            EbxAsset texAsset = App.AssetManager.GetEbx(texEntry);
            ulong resRid = ((dynamic)texAsset.RootObject).Resource;
            ResAssetEntry texRes = App.AssetManager.GetResEntry(resRid);
            if (texRes == null) { Log("  WARNING: {0}: texture res not found", label); return; }
            Texture tex = App.AssetManager.GetResAs<Texture>(texRes);
            string pf = tex.PixelFormat ?? "";
            if (tex.Width != scale * lightingDb.AtlasWidth || tex.Height != scale * lightingDb.AtlasHeight || pf.IndexOf(format, StringComparison.OrdinalIgnoreCase) < 0 || tex.FirstMip != 0)
            {
                Log("  WARNING: {0}: {1} is {2}x{3} {4} (first mip {5}); expected {6}x{7} {8}: not written", label, texEntry.Name, tex.Width, tex.Height, pf, tex.FirstMip,
                    scale * lightingDb.AtlasWidth, scale * lightingDb.AtlasHeight, format);
                return;
            }
            if (tex.MipCount != 1)
            {
                // Frosty re-splits a texture chunk with several mips into streaming ranges (Utils.CompressTexture) and the
                // texture res would have to be saved with them; FIFA's lighting textures have one mip
                Log("  WARNING: {0}: {1} has {2} mips (FIFA's lighting textures have 1): not written", label, texEntry.Name, tex.MipCount);
                return;
            }
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(tex.ChunkId);
            if (chunkEntry == null) { Log("  WARNING: {0}: texture chunk not found", label); return; }
            byte[] chunk;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                chunk = reader.ReadToEnd();
            int rowBlocks = tex.Width / 4;
            long mip0 = (long)rowBlocks * (tex.Height / 4) * blockBytes;
            if (chunk.Length < mip0) { Log("  WARNING: {0}: chunk is {1} bytes, mip 0 needs {2}: not written", label, chunk.Length, mip0); return; }
            int written = 0;
            foreach (BakedLightingPlanner.Placement p in placed)
            {
                byte[] src = blocks(p.Data);
                int bw = scale * p.Data.Width / 4, bh = scale * p.Data.Height / 4;
                int bx0 = scale * p.X / 4, by0 = scale * p.Y / 4;
                if (src.Length != bw * bh * blockBytes) { Log("  WARNING: {0}: {1} has {2} bytes, expected {3}: skipped", label, p.Mesh.Name, src.Length, bw * bh * blockBytes); continue; }
                for (int by = 0; by < bh; by++)
                    Buffer.BlockCopy(src, by * bw * blockBytes, chunk, ((by0 + by) * rowBlocks + bx0) * blockBytes, bw * blockBytes);
                written++;
            }
            App.AssetManager.ModifyChunk(tex.ChunkId, chunk);
            ChunkAssetEntry modified = App.AssetManager.GetChunkEntry(tex.ChunkId);
            SetWholeChunkLayout(modified, chunk.Length);
            Log("  {0}: {1} ({2}x{3} {4}): {5} rectangle(s) written; chunk {6} logical offset {7}, logical size {8}, first mip {9}", label, texEntry.Name, tex.Width, tex.Height, pf, written,
                tex.ChunkId, modified.ModifiedEntry.LogicalOffset, modified.ModifiedEntry.LogicalSize, modified.ModifiedEntry.FirstMip);
        }

        /// <summary>
        /// The logical layout of a rewritten one-mip lighting texture chunk: offset 0, size = the whole texture, no range,
        /// no first mip - what Frosty's stadium duplication writes for these chunks (test_499 project: logical offset 0,
        /// logical size 13,107,200 / 26,214,400 / 16,777,216, first mip -1); a game chunk keeps its own values. The mod
        /// writes the chunk's original size as 0 (FrostyModWriter: ModifiedEntry.OriginalSize, which ModifyChunk never
        /// sets), so the logical size is the only size the game gets. r6 / r7 passed the Texture read from the res to
        /// ModifyChunk; Texture.Read never sets LogicalOffset / LogicalSize, so those chunks went out with logical size 0.
        /// </summary>
        private static void SetWholeChunkLayout(ChunkAssetEntry entry, int length)
        {
            uint lo = 0, ls = (uint)length;
            int fm = -1;
            if (!entry.IsAdded && entry.LogicalSize != 0 && (long)entry.LogicalOffset + entry.LogicalSize == length)
            {
                lo = entry.LogicalOffset; ls = entry.LogicalSize; fm = entry.FirstMip;
            }
            entry.ModifiedEntry.LogicalOffset = lo;
            entry.ModifiedEntry.LogicalSize = ls;
            entry.ModifiedEntry.RangeStart = 0;
            entry.ModifiedEntry.RangeEnd = 0;
            entry.ModifiedEntry.FirstMip = fm;
            entry.ModifiedEntry.IsDirty = true;
            entry.IsDirty = true;
        }

        /// <summary>
        /// The stadium's three lighting textures whose modified chunk has logical size 0 (written by an r6 / r7 import) get
        /// the whole-chunk layout back; returns how many were repaired.
        /// </summary>
        private int RepairLightingTextureChunks()
        {
            int repaired = 0;
            List<object> pointers = new List<object>();
            try { pointers.Add(((dynamic)lightingAsset.RootObject).SkyVisibilityTexture); } catch (Exception) { }
            EbxAssetEntry fluxEntry = App.AssetManager.GetEbxEntry(lightingName + "_flux_night");
            if (fluxEntry != null)
            {
                EbxAsset fluxAsset = App.AssetManager.GetEbx(fluxEntry);
                try { pointers.Add(((dynamic)fluxAsset.RootObject).StaticIrradianceTexture); } catch (Exception) { }
                try { pointers.Add(((dynamic)fluxAsset.RootObject).StaticDirectionTexture); } catch (Exception) { }
            }
            foreach (object pointer in pointers)
            {
                if (!(pointer is PointerRef) || ((PointerRef)pointer).Type != PointerRefType.External) continue;
                EbxAssetEntry texEntry = App.AssetManager.GetEbxEntry(((PointerRef)pointer).External.FileGuid);
                if (texEntry == null) continue;
                ulong resRid = ((dynamic)App.AssetManager.GetEbx(texEntry).RootObject).Resource;
                ResAssetEntry texRes = App.AssetManager.GetResEntry(resRid);
                if (texRes == null) continue;
                Texture tex = App.AssetManager.GetResAs<Texture>(texRes);
                ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(tex.ChunkId);
                if (ce == null || ce.ModifiedEntry == null || ce.ModifiedEntry.LogicalSize != 0 || tex.MipCount != 1) continue;
                long length;
                using (Stream s = App.AssetManager.GetChunk(ce))
                    length = s.Length;
                SetWholeChunkLayout(ce, (int)length);
                Log("  {0}: chunk {1} had logical size 0 (earlier import): logical offset {2}, logical size {3}, first mip {4}", texEntry.Name, tex.ChunkId,
                    ce.ModifiedEntry.LogicalOffset, ce.ModifiedEntry.LogicalSize, ce.ModifiedEntry.FirstMip);
                repaired++;
            }
            return repaired;
        }
    }
}
