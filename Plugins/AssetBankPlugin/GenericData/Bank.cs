using AssetBankPlugin.Ant;
using AssetBankPlugin.Enums;
using FrostySdk;
using FrostySdk.IO;
using System;
using System.Collections.Generic;
using System.IO;

namespace AssetBankPlugin.GenericData
{
    public class Bank
    {
        public uint PackagingType { get; set; }
        public List<Section> Sections { get; set; } = new List<Section>();
        public Dictionary<uint, GenericClass> Classes { get; set; } = new Dictionary<uint, GenericClass>();
        public Dictionary<string, Guid> DataNames { get; set; } = new Dictionary<string, Guid>();
        public Dictionary<string, AntAsset> AssetsByName { get; set; } = new Dictionary<string, AntAsset>();
        public static Dictionary<long, AntAsset> AssetsByKey { get; set; } = new Dictionary<long, AntAsset>();
        public static Dictionary<long, long> SectionOffsets { get; set; } = new Dictionary<long, long>();

        private static string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "frosty_anim_export.log");
        private static void Log(string msg)
        { try { File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " | " + msg + Environment.NewLine); } catch { } }

        public Bank(NativeReader r, int bundleId)
        {
            PackagingType = r.ReadUInt(Endian.Big);
            switch ((ProfileVersion)ProfilesLibrary.DataVersion)
            {
                case ProfileVersion.Battlefield4:
                case ProfileVersion.Battlefield1:
                {
                    if (PackagingType == 3)
                    {
                        r.BaseStream.Position = 56;
                        uint antRefMapCount = r.ReadUInt(Endian.Big) / 20;
                        for (int i = 0; i < antRefMapCount; i++)
                        {
                            Guid a = r.ReadGuid(); byte[] bytes = new byte[16];
                            bytes[0]=r.ReadByte();bytes[1]=r.ReadByte();bytes[2]=r.ReadByte();bytes[3]=r.ReadByte();
                            Guid b = new Guid(bytes); AntRefTable.InternalRefs[a]=b; Cache.AntRefMap[a]=b;
                        }
                        r.BaseStream.Position = 4;
                    }
                } break;
            }
            uint headerStart = (uint)r.BaseStream.Position; uint headerSize;
            string str = r.ReadSizedString(3); bool hasHeader = str != "GD."; r.BaseStream.Position -= 3;
            if (hasHeader) headerSize = r.ReadUInt(Endian.Big); else headerSize = 0;
            r.BaseStream.Position = headerStart + headerSize;

            // Parse sections, recording stream positions for import patching
            var sectionPositions = new List<long>();
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                long sectionStart = r.BaseStream.Position;
                Sections.Add(Section.ReadSection(r));
                sectionPositions.Add(sectionStart);
            }

            for (int i = 0; i < Sections.Count; i++)
            {
                var section = Sections[i];
                if (section is SectionStrm) { }
                else if (section is SectionRefl reflSection) { Classes = reflSection.Classes; }
                else if (section is SectionData dataSection)
                {
                    AntAsset asset = null;
                    try { asset = AntAsset.Deserialize(r, dataSection, Classes, this); } catch { }
                    if (asset != null)
                    {
                        int index = 0; string name = asset.Name;
                        while (DataNames.ContainsKey(name)) { name = asset.Name + " [" + index + "]"; index++; }
                        DataNames.Add(name, asset.ID);
                        AssetsByName[name] = asset;
                        AntRefTable.Add(asset);
                        Cache.AntStateBundleIndices[asset.ID] = bundleId;
                        if (asset.KeyInt64 != 0)
                        {
                            AssetsByKey[asset.KeyInt64] = asset;
                            // Store ABSOLUTE stream position of section header
                            // The section data starts 16 bytes after this (skip GD.DATAb header)
                            SectionOffsets[asset.KeyInt64] = sectionPositions[i];
                        }
                    }
                }
            }

            ResolveCcaNames();
        }

        private void ResolveCcaNames()
        {
            // Count CCAs and check AnimRefKey
            int ccaCount = 0, withKey = 0, resolved = 0;
            var renames = new List<KeyValuePair<string, string>>();

            foreach (var kvp in AssetsByName)
            {
                if (!(kvp.Value is ClipControllerAsset cca)) continue;
                ccaCount++;
                if (cca.AnimRefKey == 0) continue;
                withKey++;

                AntAsset vbrAsset;
                if (!AssetsByKey.TryGetValue(cca.AnimRefKey, out vbrAsset)) continue;
                if (!(vbrAsset is AnimationAsset)) continue;

                // Find VBR's current name in AssetsByName
                string vbrName = null;
                foreach (var inner in AssetsByName)
                    if (ReferenceEquals(inner.Value, vbrAsset)) { vbrName = inner.Key; break; }
                if (vbrName == null) continue;

                renames.Add(new KeyValuePair<string, string>(vbrName, kvp.Key));
                resolved++;
            }

            Log("CCA resolve: " + ccaCount + " CCAs, " + withKey + " with AnimRefKey, " + resolved + " resolved to VBR");
            if (ccaCount > 0 && withKey == 0)
                Log("  WARNING: No CCA has an AnimRefKey — Anim field may not be parsed correctly");
            if (withKey > 0 && resolved == 0)
                Log("  WARNING: AnimRefKeys found but no matching VBR in AssetsByKey");

            // Log first few renames
            int logged = 0;
            foreach (var rename in renames)
            {
                if (logged >= 5) break;
                Log("  RENAME: " + rename.Key + " → " + rename.Value);
                logged++;
            }

            // Apply renames
            foreach (var rename in renames)
            {
                string oldName = rename.Key;
                string newName = rename.Value;
                if (!AssetsByName.ContainsKey(oldName)) continue;
                var asset = AssetsByName[oldName];

                string finalName = newName; int idx = 0;
                while (AssetsByName.ContainsKey(finalName) && !ReferenceEquals(AssetsByName[finalName], asset))
                { finalName = newName + " [" + idx + "]"; idx++; }

                AssetsByName.Remove(oldName);
                AssetsByName[finalName] = asset;
                asset.Name = finalName;

                if (DataNames.ContainsKey(oldName))
                { var guid = DataNames[oldName]; DataNames.Remove(oldName); DataNames[finalName] = guid; }
            }

            Log("Applied " + renames.Count + " renames. Total assets: " + AssetsByName.Count);
        }
    }
}
