using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DuplicationPlugin
{
    /// <summary>
    /// FIFA 17 draws the floodlight glow of a stadium with lens-flare and fog-cone prefabs placed at the lamps of the
    /// base stadium's roof (effects_glares_*_main -> content/effects/glares/&lt;stadium&gt;/Scenes/&lt;stadium&gt;_a_b ->
    /// layers flare_primary / flare_secondary / atmos_primary, ~170 ReferenceObjectData each). After the roof is
    /// replaced they would hang in the air. The package lists the PES lamp heads; the references are moved onto
    /// them (grouped so that there is at most one reference per lamp group, each aimed at the pitch centre like the
    /// originals) and the surplus is moved out of sight. A package without lamps moves them all out of sight.
    /// </summary>
    public partial class StadiumImporter
    {
        private const float SunkY = -1000f;

        private void RefitLensFlares()
        {
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder.ToLower() + "/effects_glares";
            List<EbxAssetEntry> mains = App.AssetManager.EnumerateEbx("LayerData").Where(e => e.Name.ToLower().StartsWith(prefix)).ToList();
            if (mains.Count == 0)
            {
                Log("  lens flares: no effects_glares layer under {0}", settings.StadiumFolder);
                return;
            }
            List<float[]> lamps = pkg.Lamps;
            Log("  lens flares: {0} glare layer(s); the package has {1} lamp heads", mains.Count, lamps.Count);

            HashSet<Guid> layerAssetsDone = new HashSet<Guid>();
            int moved = 0, sunk = 0, layers = 0;
            foreach (EbxAssetEntry main in mains)
            {
                foreach (EbxAssetEntry scene in PrefabReferences(main))
                {
                    foreach (EbxAssetEntry layer in PrefabReferences(scene))
                    {
                        if (!layerAssetsDone.Add(layer.Guid))
                            continue;
                        int m, s;
                        if (RefitGlareLayer(layer, lamps, out m, out s))
                        {
                            layers++; moved += m; sunk += s;
                        }
                    }
                }
            }
            Log("  lens flares: {0} layer(s): {1} glare(s) moved onto the PES lamps, {2} moved out of sight", layers, moved, sunk);
            report.Add(lamps.Count > 0
                ? string.Format("Lens flares: {0} moved onto the PES lamps, {1} out of sight ({2} glare layers).", moved, sunk, layers)
                : string.Format("Lens flares: the package has no lamps; {0} glares moved out of sight ({1} layers).", sunk, layers));
        }

        /// <summary>The blueprints the SpatialPrefabReferenceObjectData of an asset point at.</summary>
        private static List<EbxAssetEntry> PrefabReferences(EbxAssetEntry entry)
        {
            List<EbxAssetEntry> result = new List<EbxAssetEntry>();
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); } catch (Exception) { return result; }
            if (asset == null)
                return result;
            foreach (object obj in asset.Objects)
            {
                if (obj == null || obj.GetType().Name != "SpatialPrefabReferenceObjectData")
                    continue;
                PointerRef bp;
                try { bp = ((dynamic)obj).Blueprint; } catch (Exception) { continue; }
                if (bp.Type != PointerRefType.External)
                    continue;
                EbxAssetEntry target = App.AssetManager.GetEbxEntry(bp.External.FileGuid);
                if (target != null && !result.Contains(target))
                    result.Add(target);
            }
            return result;
        }

        /// <summary>Moves the ReferenceObjectData of one glare layer onto the lamp groups; false when the layer has none.</summary>
        private bool RefitGlareLayer(EbxAssetEntry layer, List<float[]> lamps, out int moved, out int sunk)
        {
            moved = 0; sunk = 0;
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(layer); } catch (Exception) { return false; }
            if (asset == null)
                return false;
            List<object> refs = new List<object>();
            foreach (object obj in asset.Objects)
            {
                if (obj != null && obj.GetType().Name == "ReferenceObjectData")
                    refs.Add(obj);
            }
            if (refs.Count == 0)
                return false;

            // the lamp groups (at most one glare per group) and the references, both in angular order around the pitch
            List<float[]> groups = lamps.Count > 0 ? GroupLamps(lamps, refs.Count) : new List<float[]>();
            groups.Sort((a, b) => Math.Atan2(a[2], a[0]).CompareTo(Math.Atan2(b[2], b[0])));
            List<KeyValuePair<double, object>> ordered = new List<KeyValuePair<double, object>>();
            foreach (object r in refs)
            {
                double ang = 0;
                try
                {
                    dynamic t = ((dynamic)r).BlueprintTransform;
                    ang = Math.Atan2((double)(float)t.trans.z, (double)(float)t.trans.x);
                }
                catch (Exception) { }
                ordered.Add(new KeyValuePair<double, object>(ang, r));
            }
            ordered.Sort((a, b) => a.Key.CompareTo(b.Key));

            for (int i = 0; i < ordered.Count; i++)
            {
                dynamic r = ordered[i].Value;
                dynamic t = r.BlueprintTransform;
                if (i < groups.Count)
                {
                    float[] g = groups[i];
                    SetAimedTransform(t, g[0], g[1], g[2], 0f, 0f, 0f);
                    moved++;
                }
                else
                {
                    dynamic trans = t.trans;
                    trans.y = SunkY;
                    t.trans = trans;
                    sunk++;
                }
                r.BlueprintTransform = t;
            }
            App.AssetManager.ModifyEbx(layer.Name, asset);
            Log("  lens flares: {0}: {1} reference(s) -> {2} lamp group(s), {3} out of sight", layer.Name, refs.Count, groups.Count, sunk);
            return true;
        }

        /// <summary>
        /// Lamp heads merged on a square grid whose cell grows until there are at most <paramref name="maxGroups"/>
        /// groups; each group is the mean position of its lamps.
        /// </summary>
        private static List<float[]> GroupLamps(List<float[]> lamps, int maxGroups)
        {
            float cell = 1.0f;
            for (int round = 0; round < 40; round++)
            {
                Dictionary<long, float[]> sums = new Dictionary<long, float[]>();
                foreach (float[] l in lamps)
                {
                    long ix = (long)Math.Floor(l[0] / cell) + 100000, iy = (long)Math.Floor(l[1] / cell) + 100000, iz = (long)Math.Floor(l[2] / cell) + 100000;
                    long key = (ix * 400000 + iy) * 400000 + iz;
                    float[] s;
                    if (!sums.TryGetValue(key, out s)) { s = new float[4]; sums[key] = s; }
                    s[0] += l[0]; s[1] += l[1]; s[2] += l[2]; s[3] += 1;
                }
                if (sums.Count <= maxGroups || cell > 200f)
                {
                    List<float[]> groups = new List<float[]>();
                    foreach (float[] s in sums.Values)
                        groups.Add(new float[] { s[0] / s[3], s[1] / s[3], s[2] / s[3] });
                    return groups;
                }
                cell *= 1.4f;
            }
            return new List<float[]>();
        }

        /// <summary>A LinearTransform at the position, its forward axis pointing at the target (the originals aim at the pitch).</summary>
        private static void SetAimedTransform(dynamic t, float x, float y, float z, float tx, float ty, float tz)
        {
            float fx = tx - x, fy = ty - y, fz = tz - z;
            float fl = (float)Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (fl < 1e-6f) { fx = 0; fy = -1; fz = 0; } else { fx /= fl; fy /= fl; fz /= fl; }
            // up: world up made perpendicular to forward (forward is never vertical for a lamp aimed at the pitch from the side)
            float ux = 0 - fx * fy, uy = 1 - fy * fy, uz = 0 - fz * fy;
            float ul = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-6f) { ux = 0; uy = 0; uz = 1; } else { ux /= ul; uy /= ul; uz /= ul; }
            // right = up x forward (the convention of the stadium's own glare references)
            float rx = uy * fz - uz * fy, ry = uz * fx - ux * fz, rz = ux * fy - uy * fx;
            // the EBX Vec3 fields are written back (they may be value types behind the dynamic binder)
            dynamic right = t.right; right.x = rx; right.y = ry; right.z = rz; t.right = right;
            dynamic up = t.up; up.x = ux; up.y = uy; up.z = uz; t.up = up;
            dynamic forward = t.forward; forward.x = fx; forward.y = fy; forward.z = fz; t.forward = forward;
            dynamic trans = t.trans; trans.x = x; trans.y = y; trans.z = z; t.trans = trans;
        }
    }
}
