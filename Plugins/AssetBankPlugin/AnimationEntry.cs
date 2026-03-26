// FIFA 17 Animation Browser Plugin — Data Model
// Represents a single animation clip in the AssetBank catalog.

using System;
using System.Collections.Generic;

namespace AnimationBrowserPlugin
{
    /// <summary>Channel configuration for a VBR animation.</summary>
    public struct VbrConfig
    {
        public ushort Q, V3, F;      // animated channel counts
        public ushort CQ, CV3, CF;   // constant channel counts
        public ushort NumKeys;
        public ushort KtsSize;
        public ushort Flags;

        public int AnimatedDofs => Q * 4 + V3 * 3 + F;
        public int TotalChannels => Q + CQ + V3 + CV3 + F + CF;

        public string ConfigString
        {
            get
            {
                var s = $"Q{Q}V{V3}F{F}";
                if (CQ + CV3 + CF > 0) s += $"+CQ{CQ}CV{CV3}CF{CF}";
                return s;
            }
        }

        public override string ToString() => ConfigString;
    }

    /// <summary>A single animation clip entry in the catalog.</summary>
    public class AnimationEntry
    {
        // Identity
        public string Name { get; set; }
        public string Category { get; set; }      // e.g. "Gameplay"
        public string Subcategory { get; set; }    // e.g. "Celebrations"

        // File offsets (within fifagame_win32_antstate.res)
        public long CcaOffset { get; set; }        // ClipControllerAsset section offset
        public long VbrOffset { get; set; }        // VbrAnimationAsset section offset (0 if cross-bundle stub)

        // ClipController fields
        public float FPS { get; set; }
        public float TimeScale { get; set; }
        public float Distance { get; set; }
        public uint Modes { get; set; }

        // VBR fields (only valid when IsLocal)
        public VbrConfig Config { get; set; }
        public int SectionSize { get; set; }

        // Derived
        public bool IsLocal => VbrOffset > 0;
        public float DurationSeconds => Config.NumKeys > 0 && FPS > 0 ? Config.NumKeys / FPS : 0;

        public string ModesString
        {
            get
            {
                var bytes = BitConverter.GetBytes(Modes);
                if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                var chars = new char[4];
                for (int i = 0; i < 4; i++)
                    chars[i] = bytes[i] >= 32 && bytes[i] < 127 ? (char)bytes[i] : '.';
                return new string(chars);
            }
        }

        public override string ToString() =>
            $"{Name} [{Config}] {Config.NumKeys}f @{FPS:F0}fps {(IsLocal ? "LOCAL" : "STUB")}";
    }

    /// <summary>Category tree node for the browser UI.</summary>
    public class CategoryNode
    {
        public string Name { get; set; }
        public List<CategoryNode> Children { get; set; } = new List<CategoryNode>();
        public List<AnimationEntry> Entries { get; set; } = new List<AnimationEntry>();
        public int TotalCount { get; set; }
        public int LocalCount { get; set; }
    }
}
