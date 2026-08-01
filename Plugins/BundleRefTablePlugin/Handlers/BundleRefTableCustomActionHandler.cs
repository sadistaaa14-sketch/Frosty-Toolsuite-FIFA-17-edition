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
            try
            {
                FileLogger.Log("  BRT.Load START: existing={0} newData.Length={1}",
                    existing != null ? "non-null" : "null", newData?.Length ?? 0);

                ModifiedBundleRefTableResource oldTable = (ModifiedBundleRefTableResource)existing;
                ModifiedBundleRefTableResource newTable = (ModifiedBundleRefTableResource)ModifiedResource.Read(newData);

                FileLogger.Log("  BRT.Load: newTable.DuplicationDict.Count={0}", newTable?.DuplicationDict?.Count ?? -1);

                if (oldTable == null)
                {
                    FileLogger.Log("  BRT.Load: no existing table — returning new table");
                    return newTable;
                }

                foreach (string key in newTable.DuplicationDict.Keys)
                {
                    FileLogger.Log("  BRT.Load: merging pair: '{0}' -> '{1}'", key, newTable.DuplicationDict[key]);
                    oldTable.AddAsset(key, newTable.DuplicationDict[key]);
                }

                FileLogger.Log("  BRT.Load: merged table now has {0} pairs", oldTable.DuplicationDict.Count);
                return oldTable;
            }
            catch (Exception ex)
            {
                FileLogger.LogException("BRT.Load", ex);
                throw;
            }
        }

        public void Modify(AssetEntry origEntry, AssetManager am, RuntimeResources runtimeResources, object data, out byte[] outData)
        {
            try
            {
                FileLogger.Log("  BRT.Modify START: entry.Name='{0}'", origEntry.Name);

                ModifiedBundleRefTableResource modifiedData = data as ModifiedBundleRefTableResource;
                FileLogger.Log("  BRT.Modify: modifiedData={0} pairs={1}",
                    modifiedData != null ? "non-null" : "null",
                    modifiedData?.DuplicationDict?.Count ?? -1);

                ResAssetEntry resAssetEntry = am.GetResEntry(origEntry.Name);
                if (resAssetEntry == null)
                {
                    FileLogger.Log("  BRT.Modify ERROR: am.GetResEntry('{0}') returned null — BRT resource not found in asset manager!", origEntry.Name);
                    throw new InvalidOperationException(string.Format("BRT resource '{0}' not found in asset manager", origEntry.Name));
                }
                FileLogger.Log("  BRT.Modify: resAssetEntry found: ResRid={0} ResType=0x{1:X8} ResMeta.Length={2}",
                    resAssetEntry.ResRid, resAssetEntry.ResType, resAssetEntry.ResMeta?.Length ?? 0);

                BundleRefTableResource resource = am.GetResAs<BundleRefTableResource>(resAssetEntry, modifiedData);
                if (resource == null)
                {
                    FileLogger.Log("  BRT.Modify ERROR: am.GetResAs<BundleRefTableResource> returned null!");
                    throw new InvalidOperationException(string.Format("Failed to load BRT resource '{0}' as BundleRefTableResource", origEntry.Name));
                }
                FileLogger.Log("  BRT.Modify: BRT loaded — assetLookups={0} assets={1}",
                    resource.assetLookups?.Count ?? -1, resource.assets?.Count ?? -1);

                resource.ApplyModifiedResource(modifiedData);
                FileLogger.Log("  BRT.Modify: ApplyModifiedResource done — assetLookups={0} assets={1}",
                    resource.assetLookups?.Count ?? -1, resource.assets?.Count ?? -1);

                byte[] savedBytes = resource.SaveBytes();
                FileLogger.Log("  BRT.Modify: SaveBytes done — size={0}", savedBytes?.Length ?? 0);

                origEntry.OriginalSize = savedBytes.Length;
                outData = Utils.CompressFile(savedBytes);
                FileLogger.Log("  BRT.Modify: CompressFile done — compressed={0}", outData?.Length ?? 0);

                ((ResAssetEntry)origEntry).ResMeta = resource.ResourceMeta;
                origEntry.Size = outData.Length;
                origEntry.Sha1 = Utils.GenerateSha1(outData);
                FileLogger.Log("  BRT.Modify COMPLETE: entry.Size={0} entry.Sha1={1}", origEntry.Size, origEntry.Sha1);
            }
            catch (Exception ex)
            {
                FileLogger.LogException(string.Format("BRT.Modify (entry='{0}')", origEntry?.Name), ex);
                throw;
            }
        }

        #endregion
    }
}
