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
    /// The pitch-side systems of a FIFA 17 stadium that are data rather than geometry, measured on the package:
    ///  - the LED adboards: the game places them from the DB stadiums row (adboardsidelinedistance /
    ///    adboardendlinedistance, cm from the touchline / goal line; 365..1167 and 336..1214 over the 86 rows,
    ///    generic_158: 1167 / 1214, a running-track stadium). The first PES obstacle in the 0.3..3 m band next to
    ///    each line gives the distance the boards can stand at; the values are printed (the plugin cannot write the DB);
    ///  - the big screen: scoreclock_mesh's "jumbotron" section is the game's video screen. When the package has a
    ///    screen (section / PES model names with elbd, screen, monitor, vision, jumbo ..., a vertical face of at
    ///    least 12 m² and 3 m facing the pitch), the FIFA video quad is rebuilt on that face (5 cm in front of it);
    ///    the FIFA frame, clock and score digits are left out;
    ///  - the roof lights (lighting_stadium_roof prefab lights): moved onto the PES lamp heads like the lens flares,
    ///    each keeping the point on the ground it was aimed at;
    ///  - the giant / small crowd flags (crowd_main): put onto the PES crowd of the same end.
    /// </summary>
    public partial class StadiumImporter
    {
        // ─── LED adboards ──────────────────────────────────────────────────────

        private const float PitchHalfX = 52.5f, PitchHalfZ = 34f;
        private const float AdboardClearance = 0.75f;          // board depth + a step in front of the first object
        private const float WallMinTop = 0.65f;                // taller than a kerb or step

        /// <summary>
        /// The free strip next to each pitch line, measured on the package: the first triangle at least 0.3 m tall
        /// (starting below 3 m; dugouts and chairs included, flat ground sheets not) behind the touchlines (|x| within
        /// 45 m) and the goal lines (|z| within 25 m). The DB has one value for both touchlines and one for both goal
        /// lines, so the narrower side decides; the boards go 0.75 m in front of the first object. The first wall
        /// taller than 0.65 m is reported as well.
        /// </summary>
        private void MeasureAdboardDistances()
        {
            // per side: (distance, along-line min, along-line max, tag, counts as a wall)
            List<Tuple<float, float, float, string, bool>>[] hits = new List<Tuple<float, float, float, string, bool>>[4];
            for (int i = 0; i < 4; i++) hits[i] = new List<Tuple<float, float, float, string, bool>>();
            foreach (KeyValuePair<string, List<GeometryImportSection>> kv in importedGeometry)
            {
                if (kv.Key.Equals("roof", StringComparison.OrdinalIgnoreCase)) continue;
                List<PesStadiumPackage.SectionInfo> infos;
                importedSectionInfos.TryGetValue(kv.Key, out infos);
                for (int si = 0; si < kv.Value.Count; si++)
                {
                    GeometryImportSection gs = kv.Value[si];
                    PesStadiumPackage.SectionInfo info = infos != null && si < infos.Count ? infos[si] : null;
                    if (info != null && (info.Role == "ground" || info.Role == "runoff")) continue;
                    bool dugout = info != null && info.Role == "dugout";
                    float[] p = gs.Positions;
                    uint[] idx = gs.Indices;
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                    {
                        int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
                        float hi = Math.Max(p[a + 1], Math.Max(p[b + 1], p[c + 1])), lo = Math.Min(p[a + 1], Math.Min(p[b + 1], p[c + 1]));
                        if (hi <= 0.3f || lo >= 3f) continue;
                        float cx = (p[a] + p[b] + p[c]) / 3f, cz = (p[a + 2] + p[b + 2] + p[c + 2]) / 3f;
                        int k = -1; float d = 0;
                        if (Math.Abs(cx) <= 45f && cz < -PitchHalfZ) { k = 0; d = -PitchHalfZ - cz; }
                        else if (Math.Abs(cx) <= 45f && cz > PitchHalfZ) { k = 1; d = cz - PitchHalfZ; }
                        else if (Math.Abs(cz) <= 25f && cx < -PitchHalfX) { k = 2; d = -PitchHalfX - cx; }
                        else if (Math.Abs(cz) <= 25f && cx > PitchHalfX) { k = 3; d = cx - PitchHalfX; }
                        if (k < 0) continue;
                        bool alongX = k < 2;
                        float amin = alongX ? Math.Min(p[a], Math.Min(p[b], p[c])) : Math.Min(p[a + 2], Math.Min(p[b + 2], p[c + 2]));
                        float amax = alongX ? Math.Max(p[a], Math.Max(p[b], p[c])) : Math.Max(p[a + 2], Math.Max(p[b + 2], p[c + 2]));
                        hits[k].Add(Tuple.Create(d, amin, amax, kv.Key + "/" + gs.Name, !dugout && hi > WallMinTop));
                    }
                }
            }
            // an object counts when what lies within 1 m behind its front covers at least 2 m of the line (a sprinkler,
            // a bottle or a camera box does not stop a board)
            float[] first = new float[4], wall = new float[4];
            string[] firstBy = new string[4], wallBy = new string[4];
            for (int i = 0; i < 4; i++)
            {
                first[i] = FrontOfObject(hits[i], false, out firstBy[i]);
                wall[i] = FrontOfObject(hits[i], true, out wallBy[i]);
            }
            if (first.All(w => w == float.MaxValue))
            {
                Log("  adboards: no package geometry next to the pitch lines, nothing to measure");
                return;
            }
            Log("  adboards: first object at least 0.3 m tall: near touchline {0} ({1}), far touchline {2} ({3}), -x goal line {4} ({5}), +x goal line {6} ({7})",
                Dist(first[0]), firstBy[0], Dist(first[1]), firstBy[1], Dist(first[2]), firstBy[2], Dist(first[3]), firstBy[3]);
            Log("  adboards: first wall taller than {0} m: near {1} ({2}), far {3} ({4}), -x {5} ({6}), +x {7} ({8})",
                WallMinTop, Dist(wall[0]), wallBy[0], Dist(wall[1]), wallBy[1], Dist(wall[2]), wallBy[2], Dist(wall[3]), wallBy[3]);
            // the boards 0.75 m in front of the first object; when that leaves a strip narrower than on any FIFA stadium
            // (low items such as 3D carpets or kerbs close to the line), in front of the first wall instead
            float side = Math.Min(first[0], first[1]), end = Math.Min(first[2], first[3]);
            float sideWall = Math.Min(wall[0], wall[1]), endWall = Math.Min(wall[2], wall[3]);
            float sideBoard = BoardDistance(side, sideWall, 3.65f), endBoard = BoardDistance(end, endWall, 3.36f);
            List<string> notes = new List<string>();
            if (side != float.MaxValue && side - AdboardClearance < 3.65f && sideBoard != float.MaxValue && sideBoard > side)
                notes.Add(string.Format("touchlines: the first object ({0}) is closer than FIFA's narrowest strip, the boards are placed in front of the first wall ({1}) behind it", Dist(side), Dist(sideWall)));
            if (end != float.MaxValue && end - AdboardClearance < 3.36f && endBoard != float.MaxValue && endBoard > end)
                notes.Add(string.Format("goal lines: the first object ({0}) is closer than FIFA's narrowest strip, the boards are placed in front of the first wall ({1}) behind it", Dist(end), Dist(endWall)));
            if (sideBoard != float.MaxValue && sideBoard < 3.65f) notes.Add("the touchline strip is narrower than on any FIFA stadium (FIFA's narrowest: 3.65 m)");
            if (endBoard != float.MaxValue && endBoard < 3.36f) notes.Add("the goal line strip is narrower than on any FIFA stadium (FIFA's narrowest: 3.36 m)");
            string line = string.Format("DB stadiums row: adboardsidelinedistance={0} adboardendlinedistance={1} (cm; the 86 stadium rows use 365..1167 and 336..1214)",
                sideBoard == float.MaxValue ? "(nothing within 15 m: keep)" : Cm(sideBoard).ToString(), endBoard == float.MaxValue ? "(nothing within 15 m: keep)" : Cm(endBoard).ToString());
            Log("  adboards: {0}{1}", line, notes.Count > 0 ? "; note: " + string.Join("; ", notes) : "");
            report.Add("Adboards for the database (the plugin cannot write it): " + line + (notes.Count > 0 ? ". Note: " + string.Join("; ", notes) + "." : ""));
        }

        /// <summary>0.75 m in front of the first object; in front of the first wall when the first object leaves less than FIFA's narrowest strip.</summary>
        private static float BoardDistance(float firstObject, float firstWall, float fifaMinimum)
        {
            if (firstObject == float.MaxValue) return float.MaxValue;
            float board = Math.Max(0.5f, firstObject - AdboardClearance);
            if (board < fifaMinimum && firstWall != float.MaxValue && firstWall - AdboardClearance > board)
                board = firstWall - AdboardClearance;
            return board;
        }

        private static float FrontOfObject(List<Tuple<float, float, float, string, bool>> hits, bool wallsOnly, out string tag)
        {
            tag = "";
            List<Tuple<float, float, float, string, bool>> h = hits.Where(t => !wallsOnly || t.Item5).OrderBy(t => t.Item1).ToList();
            for (int i = 0; i < h.Count; i++)
            {
                HashSet<int> bins = new HashSet<int>();
                for (int j = i; j < h.Count && h[j].Item1 <= h[i].Item1 + 1f; j++)
                    for (int bin = (int)Math.Floor(h[j].Item2); bin <= (int)Math.Floor(h[j].Item3) && bins.Count < 3; bin++)
                        bins.Add(bin);
                if (bins.Count >= 2) { tag = h[i].Item4; return h[i].Item1; }
            }
            return float.MaxValue;
        }

        private static string Dist(float d) { return d == float.MaxValue ? "none within 45 m" : d.ToString("0.00") + " m"; }

        // ─── the big screen ────────────────────────────────────────────────────

        private static readonly string[] ScreenWords = { "elbd", "screen", "scrn", "vision", "jumbo", "monitor", "mntr", "display", "disp", "video", "bigscr", "scoreb", "tron", "ledscreen", "ledwall" };
        private const float ScreenMinArea = 12f, ScreenMinHeight = 3f, ScreenOffset = 0.05f;
        private const int ScreenMaxFaces = 4;

        private sealed class ScreenFace
        {
            public float[] Corners;    // 4 x xyz: top-left, top-right, bottom-right, bottom-left as seen from the pitch
            public float[] Normal;     // towards the pitch
            public float Width, Height, Area;
            public string Source;
            public override string ToString() { return string.Format("{0:0.0} x {1:0.0} m at ({2:0.0}, {3:0.0}, {4:0.0}) facing ({5:0.00}, {6:0.00}, {7:0.00}) [{8}]", Width, Height, (Corners[0] + Corners[6]) / 2, (Corners[1] + Corners[7]) / 2, (Corners[2] + Corners[8]) / 2, Normal[0], Normal[1], Normal[2], Source); }
        }

        private static bool HasScreenWord(PesStadiumPackage.SectionInfo info, string sectionName)
        {
            List<string> names = new List<string> { sectionName };
            if (info != null) { names.Add(info.Material); names.AddRange(info.PesInstances); names.AddRange(info.Sources); }
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                string l = n.ToLowerInvariant();
                foreach (string w in ScreenWords) if (l.Contains(w)) return true;
            }
            return false;
        }

        /// <summary>The vertical planar faces of the package's screen sections that face the pitch, largest first.</summary>
        private List<ScreenFace> FindPackageScreens()
        {
            List<ScreenFace> faces = new List<ScreenFace>();
            foreach (KeyValuePair<string, List<GeometryImportSection>> kv in importedGeometry)
            {
                List<PesStadiumPackage.SectionInfo> infos;
                importedSectionInfos.TryGetValue(kv.Key, out infos);
                for (int si = 0; si < kv.Value.Count; si++)
                {
                    GeometryImportSection gs = kv.Value[si];
                    PesStadiumPackage.SectionInfo info = infos != null && si < infos.Count ? infos[si] : null;
                    if (!HasScreenWord(info, gs.Name)) continue;
                    faces.AddRange(PlanarFaces(gs, kv.Key + "/" + gs.Name));
                }
            }
            // nearest to the pitch first: of two overlapping faces with the same direction (a screen and the panel behind
            // it, Guadalajara: x 104.8 and 116.1) the front one is the screen the cameras see
            faces.Sort((a, b) => FaceDistance(a).CompareTo(FaceDistance(b)));
            List<ScreenFace> chosen = new List<ScreenFace>();
            foreach (ScreenFace f in faces)
            {
                bool hidden = false;
                foreach (ScreenFace c in chosen)
                {
                    float dot = c.Normal[0] * f.Normal[0] + c.Normal[1] * f.Normal[1] + c.Normal[2] * f.Normal[2];
                    if (dot < 0.9f) continue;
                    float cx = (c.Corners[0] + c.Corners[6]) / 2, cz = (c.Corners[2] + c.Corners[8]) / 2, fx = (f.Corners[0] + f.Corners[6]) / 2, fz = (f.Corners[2] + f.Corners[8]) / 2;
                    if (Math.Abs(cx - fx) < (c.Width + f.Width) / 2 && Math.Abs(cz - fz) < (c.Width + f.Width) / 2) { hidden = true; break; }
                }
                if (hidden) continue;
                chosen.Add(f);
                if (chosen.Count >= ScreenMaxFaces) break;
            }
            return chosen;
        }

        private static float FaceDistance(ScreenFace f)
        {
            float cx = (f.Corners[0] + f.Corners[6]) / 2, cz = (f.Corners[2] + f.Corners[8]) / 2;
            return (float)Math.Sqrt(cx * cx + cz * cz);
        }

        /// <summary>Groups the near-vertical triangles of a section by plane and fits a rectangle to each group facing the pitch.</summary>
        private static List<ScreenFace> PlanarFaces(GeometryImportSection gs, string source)
        {
            List<ScreenFace> result = new List<ScreenFace>();
            float[] p = gs.Positions;
            uint[] idx = gs.Indices;
            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>();
            Dictionary<string, float[]> normals = new Dictionary<string, float[]>();
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
                float ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
                float vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-4f) continue;
                nx /= len; ny /= len; nz /= len;
                if (Math.Abs(ny) > 0.3f) continue;                                  // not a vertical face
                float d = nx * p[a] + ny * p[a + 1] + nz * p[a + 2];
                string key = string.Format("{0},{1},{2},{3}", Math.Round(nx * 4), Math.Round(ny * 4), Math.Round(nz * 4), Math.Round(d / 0.5));
                List<int> l;
                if (!groups.TryGetValue(key, out l)) { groups[key] = l = new List<int>(); normals[key] = new float[3]; }
                l.Add(t);
                float[] n = normals[key]; n[0] += nx * len; n[1] += ny * len; n[2] += nz * len;
            }
            foreach (KeyValuePair<string, List<int>> kv in groups)
            {
                float[] n = normals[kv.Key];
                float nl = (float)Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (nl < 1e-6f) continue;
                n[0] /= nl; n[1] /= nl; n[2] /= nl;
                // basis in the plane: right = up x n (viewer's right), up' = n x right
                float rx = 1 * n[2] - 0 * n[1], ry = 0 * n[0] - 0 * n[2], rz = 0 * n[1] - 1 * n[0];
                float rl = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz);
                if (rl < 1e-6f) continue;
                rx /= rl; ry /= rl; rz /= rl;
                float ux = n[1] * rz - n[2] * ry, uy = n[2] * rx - n[0] * rz, uz = n[0] * ry - n[1] * rx;
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue, area = 0;
                double sx = 0, sy = 0, sz = 0; int cnt = 0;
                foreach (int t in kv.Value)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int v = (int)idx[t + k] * 3;
                        float u = p[v] * rx + p[v + 1] * ry + p[v + 2] * rz, w = p[v] * ux + p[v + 1] * uy + p[v + 2] * uz;
                        minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, w); maxV = Math.Max(maxV, w);
                        sx += p[v]; sy += p[v + 1]; sz += p[v + 2]; cnt++;
                    }
                    int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
                    float e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2], e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
                    float cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x;
                    area += (float)Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2f;
                }
                float width = maxU - minU, height = maxV - minV;
                if (area < ScreenMinArea || height < ScreenMinHeight || width < 1f) continue;
                if (area < 0.6f * width * height) continue;                           // not a filled rectangle
                float mx = (float)(sx / cnt), my = (float)(sy / cnt), mz = (float)(sz / cnt);
                if (n[0] * (0 - mx) + n[2] * (0 - mz) <= 0) continue;                 // faces away from the pitch
                float planeD = n[0] * mx + n[1] * my + n[2] * mz;
                // corners: (u, v) -> point = n * planeD + right * u + up' * v
                Func<float, float, float[]> corner = (u, w) => new float[] { n[0] * planeD + rx * u + ux * w, n[1] * planeD + ry * u + uy * w, n[2] * planeD + rz * u + uz * w };
                float[] tl = corner(minU, maxV), tr = corner(maxU, maxV), br = corner(maxU, minV), bl = corner(minU, minV);
                result.Add(new ScreenFace
                {
                    Corners = new float[] { tl[0], tl[1], tl[2], tr[0], tr[1], tr[2], br[0], br[1], br[2], bl[0], bl[1], bl[2] },
                    Normal = new float[] { n[0], n[1], n[2] }, Width = width, Height = height, Area = area, Source = source
                });
            }
            return result;
        }

        /// <summary>The FIFA video screen on the package's screen face(s): the scoreclock mesh's jumbotron section rebuilt there.</summary>
        private void WireJumbotron()
        {
            string prefix = "content/worlds/components/stadiums/" + settings.StadiumNameOnly + "/";
            EbxAssetEntry clock = App.AssetManager.GetEbxEntry(prefix + "scoreclock_mesh");
            if (clock == null)
                clock = StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly).FirstOrDefault(e => e.Filename.ToLowerInvariant().StartsWith("scoreclock"));
            if (clock == null)
            {
                Log("  screen: {0} has no scoreclock mesh, no video screen to wire", settings.StadiumNameOnly);
                return;
            }
            List<ScreenFace> faces = FindPackageScreens();
            if (faces.Count == 0)
            {
                Log("  screen: the package has no screen section (names with {0}); the FIFA scoreclock stays {1}", string.Join("/", ScreenWords), settings.ClearScoreboard ? "emptied" : "as it is");
                report.Add("Big screen: the package has no screen, the FIFA video screen is not shown.");
                return;
            }
            foreach (ScreenFace f in faces) Log("  screen: package face {0}", f);

            EbxAsset meshAsset = App.AssetManager.GetEbx(clock);
            dynamic root = meshAsset.RootObject;
            ulong rid = root.MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            MeshSet meshSet = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
            if (meshSet == null || meshSet.Lods.Count == 0)
            {
                Log("  screen: {0}: mesh set not readable", clock.Filename);
                return;
            }
            // the jumbotron section and its material
            int jumboIndex = -1; string jumboName = null;
            foreach (MeshSetSection s in meshSet.Lods[0].Sections)
                if (s.Name.IndexOf("jumbotron", StringComparison.OrdinalIgnoreCase) >= 0) { jumboIndex = s.MaterialId; jumboName = s.Name; break; }
            if (jumboIndex < 0)
            {
                Log("  screen: {0} has no jumbotron section; the video screen cannot be wired", clock.Filename);
                report.Add("Big screen: the stadium's scoreclock mesh has no jumbotron section, the FIFA video screen is not shown.");
                return;
            }
            dynamic materials = root.Materials;
            if (jumboIndex >= materials.Count)
            {
                Log("  screen: jumbotron material index {0} outside the {1} materials", jumboIndex, materials.Count);
                return;
            }
            if (jumboIndex != 0)
            {
                dynamic jumboMat = materials[jumboIndex];
                materials.RemoveAt(jumboIndex);
                materials.Insert(0, jumboMat);
                Log("  screen: jumbotron material moved to slot 0 (was {0})", jumboIndex);
            }

            // the quads, 5 cm in front of the package faces, UVs as the stadium's own screen (u left->right, v top->bottom for a viewer facing it)
            float[] pos = new float[faces.Count * 12], nrm = new float[faces.Count * 12], uv = new float[faces.Count * 8];
            List<uint> idx = new List<uint>();
            for (int f = 0; f < faces.Count; f++)
            {
                ScreenFace face = faces[f];
                for (int c = 0; c < 4; c++)
                {
                    pos[f * 12 + c * 3] = face.Corners[c * 3] + face.Normal[0] * ScreenOffset;
                    pos[f * 12 + c * 3 + 1] = face.Corners[c * 3 + 1] + face.Normal[1] * ScreenOffset;
                    pos[f * 12 + c * 3 + 2] = face.Corners[c * 3 + 2] + face.Normal[2] * ScreenOffset;
                    nrm[f * 12 + c * 3] = face.Normal[0]; nrm[f * 12 + c * 3 + 1] = face.Normal[1]; nrm[f * 12 + c * 3 + 2] = face.Normal[2];
                }
                uv[f * 8] = 0; uv[f * 8 + 1] = 0;      // top-left
                uv[f * 8 + 2] = 1; uv[f * 8 + 3] = 0;  // top-right
                uv[f * 8 + 4] = 1; uv[f * 8 + 5] = 1;  // bottom-right
                uv[f * 8 + 6] = 0; uv[f * 8 + 7] = 1;  // bottom-left
                uint b = (uint)(f * 4);
                // winding: the stadium's own quad has cross(b-a, c-a) along the normal (facing the viewer)
                idx.Add(b); idx.Add(b + 2); idx.Add(b + 1);
                idx.Add(b); idx.Add(b + 3); idx.Add(b + 2);
            }
            GeometryImportSection quad = new GeometryImportSection
            {
                Name = jumboName, Part = 0, Positions = pos, Normals = nrm, TexCoord0 = uv, Indices = idx.ToArray(),
                Categories = MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque
            };
            // check the winding against the face normal and flip if needed
            {
                float ax = pos[0], ay = pos[1], az = pos[2], bx = pos[6], by = pos[7], bz = pos[8], cx = pos[3], cy = pos[4], cz = pos[5];
                float e1x = bx - ax, e1y = by - ay, e1z = bz - az, e2x = cx - ax, e2y = cy - ay, e2z = cz - az;
                float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                if (nx * faces[0].Normal[0] + ny * faces[0].Normal[1] + nz * faces[0].Normal[2] < 0)
                {
                    for (int t = 0; t + 2 < quad.Indices.Length; t += 3) { uint tmp = quad.Indices[t + 1]; quad.Indices[t + 1] = quad.Indices[t + 2]; quad.Indices[t + 2] = tmp; }
                    Log("  screen: quad winding flipped to face the pitch");
                }
            }
            List<GeometryImportSection> sections = new List<GeometryImportSection> { quad };

            string placedBy;
            float[] placement = StadiumBaseLookup.Placement(settings.StadiumFolder, clock, out placedBy);
            if (placement != null && !StadiumBaseLookup.IsIdentity(placement))
            {
                float[] inv = StadiumBaseLookup.InverseRigid(placement);
                for (int v = 0; v < quad.VertexCount; v++)
                {
                    StadiumBaseLookup.TransformPoint(inv, ref quad.Positions[v * 3], ref quad.Positions[v * 3 + 1], ref quad.Positions[v * 3 + 2]);
                    StadiumBaseLookup.TransformDir(inv, ref quad.Normals[v * 3], ref quad.Normals[v * 3 + 1], ref quad.Normals[v * 3 + 2]);
                }
            }
            try
            {
                GeometryImportOptions options = new GeometryImportOptions { ForceDynamicLighting = true, KeepDedicatedDepthSection = false, SyncStaticModelGroups = true };
                GeometryImportResult result = new FBXImporter(logger).ImportGeometry(sections, meshSet, meshAsset, clock, options);
                clearedMeshes.Remove(clock.Name);
                Log("  screen: {0} <- {1} video quad(s) on the package screen(s) ({2} triangles); frame, clock and score digits left out", clock.Filename, faces.Count, result.Triangles);
                report.Add(string.Format("Big screen: the FIFA video screen is drawn on the package's screen ({0}); the FIFA scoreboard frame and digits are left out.", string.Join("; ", faces.Select(f => f.ToString()))));
            }
            catch (Exception ex)
            {
                Log("  WARNING: screen: writing the video quad into {0} failed: {1}", clock.Filename, ex.Message);
                report.Add("Big screen: writing the video quad failed (" + ex.Message + ").");
            }
        }

        // ─── roof lights ───────────────────────────────────────────────────────

        private void RefitRoofLights()
        {
            string name = "content/worlds/stadiums/" + settings.StadiumFolder + "/lighting_stadium_roof";
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
            {
                Log("  roof lights: no lighting_stadium_roof layer under {0}", settings.StadiumFolder);
                return;
            }
            if (pkg.Lamps.Count == 0)
            {
                Log("  roof lights: the package has no lamp heads; the roof lights stay where they are");
                report.Add("Roof lights: the package lists no lamps, the FIFA roof lights were not moved.");
                return;
            }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Log("  WARNING: roof lights: {0}", ex.Message); return; }
            List<object> refs = new List<object>();
            foreach (object obj in asset.Objects)
                if (obj != null && obj.GetType().Name == "SpatialPrefabReferenceObjectData") refs.Add(obj);
            if (refs.Count == 0)
            {
                Log("  roof lights: the layer has no prefab references");
                return;
            }
            List<float[]> groups = GroupLamps(pkg.Lamps, refs.Count);
            groups.Sort((a, b) => Math.Atan2(a[2], a[0]).CompareTo(Math.Atan2(b[2], b[0])));
            List<KeyValuePair<double, object>> ordered = new List<KeyValuePair<double, object>>();
            foreach (object r in refs)
            {
                double ang = 0;
                try { dynamic t = ((dynamic)r).BlueprintTransform; ang = Math.Atan2((double)(float)t.trans.z, (double)(float)t.trans.x); } catch (Exception) { }
                ordered.Add(new KeyValuePair<double, object>(ang, r));
            }
            ordered.Sort((a, b) => a.Key.CompareTo(b.Key));
            int moved = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                dynamic r = ordered[i].Value;
                dynamic t = r.BlueprintTransform;
                float[] g = groups[(int)((long)i * groups.Count / ordered.Count)];   // the lights spread over the lamp groups in angular order
                // the point on the ground the light was aimed at (its forward axis hitting y = 0), else the pitch centre
                float fx = (float)t.forward.x, fy = (float)t.forward.y, fz = (float)t.forward.z;
                float px = (float)t.trans.x, py = (float)t.trans.y, pz = (float)t.trans.z;
                float tx = 0, ty = 0, tz = 0;
                if (fy < -0.05f)
                {
                    float s = -py / fy;
                    tx = px + fx * s; tz = pz + fz * s;
                    if (Math.Abs(tx) > 80f || Math.Abs(tz) > 60f) { tx = 0; tz = 0; }
                }
                SetAimedTransform(t, g[0], g[1], g[2], tx, ty, tz);
                r.BlueprintTransform = t;
                moved++;
            }
            App.AssetManager.ModifyEbx(entry.Name, asset);
            Log("  roof lights: {0} prefab light(s) moved onto {1} PES lamp group(s), each aimed at the ground point it lit before", moved, groups.Count);
            report.Add(string.Format("Roof lights: {0} FIFA roof lights moved onto the PES lamps ({1} groups).", moved, groups.Count));
        }

        // ─── crowd flags ───────────────────────────────────────────────────────

        /// <summary>How far above the nearest base spectator (within 3 m in x/z) the flag stands; 2.2 m when the base crowd is unknown.</summary>
        private float FlagLiftAboveCrowd(float x, float y, float z)
        {
            float best = float.MaxValue, lift = 2.2f;
            foreach (float[] p in baseCrowd)
            {
                float d = (p[0] - x) * (p[0] - x) + (p[2] - z) * (p[2] - z);
                if (d < best && d <= 9f) { best = d; lift = y - p[1]; }
            }
            if (lift < 0f || lift > 6f) lift = 2.2f;
            return lift;
        }

        private void RefitCrowdFlags()
        {
            string name = "content/worlds/stadiums/" + settings.StadiumFolder + "/crowd_main";
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
            {
                Log("  crowd flags: no crowd_main layer under {0}", settings.StadiumFolder);
                return;
            }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Log("  WARNING: crowd flags: {0}", ex.Message); return; }
            List<CrowdPointRecord> points = new List<CrowdPointRecord>();
            if (pkg.Crowd != null)
            {
                string file = pkg.FullPath(pkg.Crowd.File);
                if (System.IO.File.Exists(file))
                {
                    float pitch, setback;
                    points = CrowdReader.Read(file, out pitch, out setback);
                }
            }
            int moved = 0, sunk = 0;
            bool changed = false;
            foreach (object obj in asset.Objects)
            {
                if (obj == null || obj.GetType().Name != "ObjectReferenceObjectData") continue;
                dynamic o = obj;
                PointerRef bp;
                try { bp = o.Blueprint; } catch (Exception) { continue; }
                if (bp.Type != PointerRefType.External) continue;
                EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(bp.External.FileGuid);
                string bpName = bpEntry != null ? bpEntry.Name.ToLowerInvariant() : "";
                if (!bpName.Contains("/flag/")) continue;
                dynamic lt = o.BlueprintTransform;
                dynamic trans = lt.trans;
                float x = (float)trans.x, y = (float)trans.y, z = (float)trans.z;
                if (y < SinkDepth + 1f) continue;
                // the PES crowd of the same end (sign of x), near the flag's z: the point at the middle of that block's depth
                List<CrowdPointRecord> same = points.Where(p => Math.Sign(p.X) == Math.Sign(x) && Math.Abs(p.X) > PitchHalfX && Math.Abs(p.Z - z) <= 8f).ToList();
                if (same.Count == 0) same = points.Where(p => Math.Sign(p.X) == Math.Sign(x) && Math.Abs(p.X) > PitchHalfX && Math.Abs(p.Z - z) <= 20f).ToList();
                if (same.Count < 10)
                {
                    trans.y = y + SinkDepth; lt.trans = trans; o.BlueprintTransform = lt; sunk++; changed = true;
                    Log("  crowd flags: {0}: no PES crowd at that end, moved out of sight", bpEntry.Filename);
                    continue;
                }
                same.Sort((a, b) => Math.Abs(a.X).CompareTo(Math.Abs(b.X)));
                CrowdPointRecord mid = same[same.Count * 55 / 100];
                // the flag's height above the spectators, measured on the base stadium (generic_158: 2.2 m for both flags)
                float lift = FlagLiftAboveCrowd(x, y, z);
                trans.x = mid.X; trans.y = mid.Y + lift; trans.z = mid.Z;
                lt.trans = trans; o.BlueprintTransform = lt;
                moved++; changed = true;
                Log("  crowd flags: {0}: ({1:0.0}, {2:0.0}, {3:0.0}) -> ({4:0.0}, {5:0.0}, {6:0.0}): PES spectator position at 55% of the end stand's depth, {7:0.00} m above it as on the base stadium", bpEntry.Filename, x, y, z, mid.X, mid.Y + lift, mid.Z, lift);
            }
            if (changed) App.AssetManager.ModifyEbx(entry.Name, asset);
            if (moved + sunk > 0)
                report.Add(string.Format("Crowd flags: {0} moved onto the PES crowd{1}.", moved, sunk > 0 ? ", " + sunk + " out of sight (no crowd at that end)" : ""));
            else
                Log("  crowd flags: none in crowd_main");
        }
    }
}
