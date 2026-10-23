using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostyAnimationEditor.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyAnimationEditor.Plugin
{
    /// <summary>
    /// The AntStateAsset of the open game in Frosty and where its container lives: a res of the same name, or the
    /// chunk named by the asset's StreamingGuid (the way Frosty's AntState asset definition reads it).
    /// The res/chunk data is the container from its version dword on (Frosty keeps the 16-byte ResMeta apart).
    /// </summary>
    internal sealed class FrostyAntState
    {
        public EbxAssetEntry Ebx;
        public ResAssetEntry Res;
        public ChunkAssetEntry Chunk;

        public string Name { get { return Ebx.Name; } }

        public string Where
        {
            get { return Res != null ? "res " + Res.Name : "chunk " + Chunk.Id; }
        }

        public bool IsModified
        {
            get { return Res != null ? Res.IsModified : Chunk.IsModified; }
        }

        public static List<EbxAssetEntry> Find()
        {
            if (App.AssetManager == null) return new List<EbxAssetEntry>();
            return App.AssetManager.EnumerateEbx("AntStateAsset").OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static FrostyAntState Locate(EbxAssetEntry e)
        {
            FrostyAntState s = new FrostyAntState { Ebx = e };
            Guid streaming = Guid.Empty;
            EbxAsset asset = App.AssetManager.GetEbx(e);
            object root = asset != null ? asset.RootObject : null;
            if (root != null)
            {
                PropertyInfo p = root.GetType().GetProperty("StreamingGuid");
                if (p != null && p.PropertyType == typeof(Guid)) streaming = (Guid)p.GetValue(root);
            }
            if (streaming != Guid.Empty)
            {
                s.Chunk = App.AssetManager.GetChunkEntry(streaming);
                if (s.Chunk == null) throw new GdException(e.Name + ": its StreamingGuid " + streaming + " names no chunk of this game");
            }
            else
            {
                s.Res = App.AssetManager.GetResEntry(e.Name);
                if (s.Res == null) throw new GdException(e.Name + ": no res of the same name (and no StreamingGuid)");
            }
            return s;
        }

        /// <summary>the container as a file image: 16 zero bytes (where an exported .res keeps its ResMeta) + the data</summary>
        public byte[] ReadImage()
        {
            using (Stream st = Res != null ? App.AssetManager.GetRes(Res) : App.AssetManager.GetChunk(Chunk))
            {
                if (st == null) throw new GdException("Frosty returned no data for " + Where);
                return Container.FromBody(st, null);
            }
        }

        /// <summary>writes the container data (from the version dword on) into the project (Frosty compresses it; any thread)</summary>
        public void WriteData(byte[] body)
        {
            if (Res != null) App.AssetManager.ModifyRes(Res.Name, body);
            else if (!App.AssetManager.ModifyChunk(Chunk.Id, body)) throw new GdException("Frosty did not take the chunk " + Chunk.Id);
        }

        /// <summary>links the written data to the asset, so the asset shows as modified and the project keeps the data with it
        /// (UI thread: listeners of the asset's modified event may touch the UI)</summary>
        public void Link()
        {
            if (Res != null) Ebx.LinkAsset(Res);
            else Ebx.LinkAsset(Chunk);
            Ebx.IsDirty = true;
        }
    }
}
