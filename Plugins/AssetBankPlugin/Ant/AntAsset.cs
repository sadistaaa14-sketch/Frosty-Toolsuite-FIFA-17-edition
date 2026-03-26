using AssetBankPlugin.Extensions;
using AssetBankPlugin.GenericData;
using FrostySdk.IO;
using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public abstract class AntAsset
    {
        public abstract string Name { get; set; }
        public abstract Guid ID { get; set; }
        public long KeyInt64 { get; set; }  // FIFA 17: __key.Data1

        public Bank Bank { get; set; }

        public abstract void SetData(Dictionary<string, object> data);

        /// <summary>Extract Int64 key from __key Dict if present.</summary>
        protected void ExtractKeyInt64(Dictionary<string, object> data)
        {
            object v;
            if (data.TryGetValue("__key", out v) && v is Dictionary<string, object>)
            {
                var keyDict = (Dictionary<string, object>)v;
                object d1;
                if (keyDict.TryGetValue("Data1", out d1))
                {
                    if (d1 is long) KeyInt64 = (long)d1;
                    else if (d1 is int) KeyInt64 = (int)d1;
                    else try { KeyInt64 = Convert.ToInt64(d1); } catch { }
                }
            }
        }

        public static AntAsset Deserialize(NativeReader r, SectionData section, Dictionary<uint, GenericClass> classes, Bank bank)
        {
            r.BaseStream.Position = section.DataOffset;
            r.ReadDataHeader(section.Endianness, out uint hash, out uint type, out uint offset);

            var values = section.ReadValues(r, classes, section.DataOffset + offset, type);

            if (values.ContainsKey("__base") && values["__base"] is long && (long)values["__base"] != 0)
            {
                r.BaseStream.Position = section.DataOffset + (long)values["__base"];
                r.ReadDataHeader(section.Endianness, out uint base_hash, out uint base_type, out uint base_offset);
                var baseValues = section.ReadValues(r, classes,
                    section.DataOffset + base_offset + Convert.ToUInt32(values["__base"]),
                    base_type);
                foreach (var value in baseValues)
                {
                    if (!values.ContainsKey(value.Key))
                        values.Add(value.Key, value.Value);
                }
            }

            Type assetType = Type.GetType("AssetBankPlugin.Ant." + classes[type].Name);

            if (assetType != null)
            {
                AntAsset asset = (AntAsset)Activator.CreateInstance(assetType);
                asset.Bank = bank;
                asset.SetData(values);
                asset.ExtractKeyInt64(values);

                AntRefTable.Add(asset);
                return asset;
            }
            else
            {
                return null;
            }
        }
    }
}
