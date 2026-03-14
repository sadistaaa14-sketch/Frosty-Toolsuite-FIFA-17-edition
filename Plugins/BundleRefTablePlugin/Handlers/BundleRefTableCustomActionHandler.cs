using Frosty.Core.IO;
using Frosty.Core.Mod;
using Frosty.Hash;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using FrostySdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BundleRefTablePlugin.Handlers
{
    public class BundleRefTableCustomActionHandler : ICustomActionHandler
    {
        public HandlerUsage Usage { get { return HandlerUsage.Merge; } }

        private class BundleRefTableModResource : EditorModResource
        {
            public override ModResourceType Type { get { return ModResourceType.Res; } }

            private readonly uint m_resType;
            private readonly ulong m_resRid;
            private readonly byte[] m_resMeta;

            public BundleRefTableModResource(ResAssetEntry entry, FrostyModWriter.Manifest manifest)
                : base(entry)
            {
                ModifiedResource md = entry.ModifiedEntry.DataObject as ModifiedResource;
                byte[] data = md.Save();

                name = entry.Name.ToLower();
                sha1 = Utils.GenerateSha1(data);
                resourceIndex = manifest.Add(sha1, data);
                size = data.Length;
                handlerHash = Fnv1.HashString(entry.Type.ToLower());

                m_resType = entry.ResType;
                m_resRid = entry.ResRid;
                m_resMeta = entry.ResMeta;
            }

            public override void Write(NativeWriter writer)
            {
                base.Write(writer);
                writer.Write(m_resType);
                writer.Write(m_resRid);
                writer.Write((m_resMeta != null) ? m_resMeta.Length : 0);
                if (m_resMeta != null)
                    writer.Write(m_resMeta);
            }
        }

        #region -- Editor Specific --

        public void SaveToMod(FrostyModWriter writer, AssetEntry entry)
        {
            writer.AddResource(new BundleRefTableModResource(entry as ResAssetEntry, writer.ResourceManifest));
        }

        #endregion

        #region -- Mod Manager Specific --

        public IEnumerable<string> GetResourceActions(string name, byte[] data)
        {
            return new List<string>();
        }

        public object Load(object existing, byte[] newData)
        {
            ModifiedBundleRefTableResource oldTable = (ModifiedBundleRefTableResource)existing;
            ModifiedBundleRefTableResource newTable = (ModifiedBundleRefTableResource)ModifiedResource.Read(newData);

            if (oldTable == null)
                return newTable;

            foreach (string key in newTable.DuplicationDict.Keys)
            {
                oldTable.AddAsset(key, newTable.DuplicationDict[key]);
            }

            return oldTable;
        }

        public void Modify(AssetEntry origEntry, AssetManager am, RuntimeResources runtimeResources, object data, out byte[] outData)
        {
            ModifiedBundleRefTableResource modifiedData = data as ModifiedBundleRefTableResource;

            ResAssetEntry resAssetEntry = am.GetResEntry(origEntry.Name);
            BundleRefTableResource resource = am.GetResAs<BundleRefTableResource>(resAssetEntry, modifiedData);

            resource.ApplyModifiedResource(modifiedData);

            byte[] savedBytes = resource.SaveBytes();
            origEntry.OriginalSize = savedBytes.Length;
            outData = Utils.CompressFile(savedBytes);

            ((ResAssetEntry)origEntry).ResMeta = resource.ResourceMeta;
            origEntry.Size = outData.Length;
            origEntry.Sha1 = Utils.GenerateSha1(outData);
        }

        #endregion
    }
}
