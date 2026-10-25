using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>context databases (the query tables the game picks animations from)</summary>
    public sealed partial class EditorModel
    {
        /// <summary>the ContextDatabaseAsset part of an object (it is a base of StepClipAsset and others), or null</summary>
        public static GdObj CdbOf(GdObj o)
        {
            if (o == null) return null;
            foreach (GdObj x in o.Chain()) if (x.Type.Name == "ContextDatabaseAsset") return x;
            for (int i = 0; i < o.Type.Fields.Count; i++)
            {
                GdObj x = o.Values[i] as GdObj;
                if (x != null && o.Type.Fields[i].Name != "__base")
                    foreach (GdObj y in x.Chain()) if (y.Type.Name == "ContextDatabaseAsset") return y;
            }
            return null;
        }

        public static bool HasCdbPanel(GdObj o) { return CdbOf(o) != null; }

        private sealed class CdbCol
        {
            public string Name = "", Interface = "", Unit = "", Kind = "";
            public uint Guid, FieldId;
            public int Type;
            public bool Custom;
            public List<string> Values = new List<string>();
            public float? Min, Max;
            public GdObj Driver, Remap, Desc;
            public long? InputSource;
        }

        /// <summary>the enum value names of a custom column: its driver's enumeration, as the game reads it</summary>
        private List<string> DriverValues(GdObj driver)
        {
            List<string> r = new List<string>();
            if (driver == null) return r;
            GdObj en = null;
            foreach (string f in new[] { "EnumerationAsset", "Enumeration", "GameStateEnumerationAsset" })
                if (driver.Has(f)) { en = Db.Get(driver.GetKey(f)); if (en != null) break; }
            if (en != null && en.Type.Name != "EnumerationAsset" && en.Has("EnumerationAsset")) en = Db.Get(en.GetKey("EnumerationAsset"));
            if (en == null || !en.Has("EnumerationValues")) return r;
            foreach (object k in en["EnumerationValues"] as IList ?? new object[0])
            {
                GdObj v = Db.Get(Num.ToKey(k));
                r.Add(v != null ? NameOf(v) : "");
            }
            return r;
        }

        private List<CdbCol> CdbCols(GdObj cdb)
        {
            List<CdbCol> cols = new List<CdbCol>();
            IList fds = cdb["FieldDescriptionAssets"] as IList ?? new object[0];
            IList src = cdb["FieldInputSourceIndices"] as IList;
            int custom = 0, meta = 0;
            for (int i = 0; i < fds.Count; i++)
            {
                GdStruct d = fds[i] as GdStruct;
                CdbCol c = new CdbCol();
                c.Desc = d as GdObj;
                if (src != null && i < src.Count) c.InputSource = Num.ToLong(src[i]);
                if (d == null) { c.Name = "(null)"; cols.Add(c); continue; }
                if (d.Type.Name == "RegistryFieldAsset")
                {
                    c.Guid = (uint)d.GetInt("ClassGuid");
                    c.FieldId = (uint)d.GetInt("FieldInstanceId");
                    uint baseId = (uint)d.GetInt("FieldId");
                    ContextFields.Entry e = ContextFields.Get(c.Guid, baseId);
                    string inst = d["FieldInstanceName"] as string ?? "";
                    c.Name = inst.Length > 0 ? inst : (e != null ? e.Name : "field " + baseId.ToString("x"));
                    c.Interface = ContextFields.Interface(c.Guid);
                    if (e != null)
                    {
                        c.Type = e.Type; c.Unit = e.Unit; c.Values = e.Values; c.Min = e.Min; c.Max = e.Max;
                    }
                    else c.Type = -1;
                    c.Kind = e != null ? e.TypeName : "unknown column";
                }
                else
                {
                    // a custom column: the engine gives it the GameState interface (or the metadata one) and a
                    // running index, and takes its type from FieldType
                    c.Custom = true;
                    bool isMeta = d.Type.Has("IsMetadata") && d.GetBool("IsMetadata");
                    c.Guid = isMeta ? ContextFields.MetadataGuid : ContextFields.GameStateGuid;
                    c.FieldId = (uint)(isMeta ? meta++ : custom++);
                    c.Name = d["FieldName"] as string ?? "";
                    c.Interface = isMeta ? "metadata" : "GameState";
                    c.Type = (int)d.GetInt("FieldType");
                    c.Kind = ContextFields.KindName(c.Type);
                    if (d.Type.Has("FloatMin")) c.Min = (float)d.GetFloat("FloatMin");
                    if (d.Type.Has("FloatMax")) c.Max = (float)d.GetFloat("FloatMax");
                    c.Driver = Db.Get(d.GetKey("GameStateDriverAsset0"));
                    c.Remap = Db.Get(d.GetKey("GameStateRemapAsset0"));
                    c.Values = DriverValues(c.Driver);
                }
                cols.Add(c);
            }
            return cols;
        }

        private static string BitList(long v, List<string> names)
        {
            List<string> on = new List<string>();
            for (int b = 0; b < 32; b++)
                if ((v & (1L << b)) != 0) on.Add(b < names.Count && names[b].Length > 0 ? names[b] : "bit " + b);
            return on.Count == 0 ? "(none)" : string.Join(", ", on);
        }

        private static string CellText(CdbCol c, GdStruct cell)
        {
            if (cell == null) return "";
            long i0 = cell["Int0"] == null ? 0 : Num.ToLong(cell["Int0"]);
            double f0 = cell["Float0"] == null ? 0 : Num.ToDouble(cell["Float0"]);
            double f1 = cell["Float1"] == null ? 0 : Num.ToDouble(cell["Float1"]);
            double f2 = cell["Float2"] == null ? 0 : Num.ToDouble(cell["Float2"]);
            switch (c.Type)
            {
                case 0: return i0 == 2 ? "any" : i0 == 1 ? "true" : "false";
                case 1: return i0 < 0 ? "any" : i0 < c.Values.Count ? c.Values[(int)i0] : i0.ToString(CultureInfo.InvariantCulture);
                case 2: return i0 == -1 || (uint)i0 == 0xffffffff ? "any" : i0.ToString(CultureInfo.InvariantCulture);
                case 3: return BitList(i0, c.Values);
                case 4:
                case 6: return f0.ToString("0.###", CultureInfo.InvariantCulture) + " .. " + f1.ToString("0.###", CultureInfo.InvariantCulture);
                case 5:
                case 7: return f1 == 0 ? f0.ToString("0.###", CultureInfo.InvariantCulture)
                        : f0.ToString("0.###", CultureInfo.InvariantCulture) + " +- " + f1.ToString("0.###", CultureInfo.InvariantCulture);
                case 8: return f0.ToString("0.###", CultureInfo.InvariantCulture) + ", " + f1.ToString("0.###", CultureInfo.InvariantCulture) + ", " + f2.ToString("0.###", CultureInfo.InvariantCulture);
            }
            return "int " + i0 + "  floats " + f0.ToString("0.###", CultureInfo.InvariantCulture) + " " + f1.ToString("0.###", CultureInfo.InvariantCulture) + " " + f2.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// a context database as the game reads it: the columns
        /// are its FieldDescriptionAssets (a RegistryFieldAsset names an engine field by ClassGuid + FieldId, a
        /// FieldDescriptionAsset is a custom game-state column), the rows its EntryAssets - one CellAsset per column
        /// plus the assets the row selects. a: first (row, 0), count (rows, 200)
        /// </summary>
        public JObject Cdb(object key, JObject a = null)
        {
            GdObj o = Root(key);
            GdObj cdb = CdbOf(o);
            if (cdb == null) throw new ApiError(NameOf(o) + " has no ContextDatabaseAsset part");
            int first = a != null && a["first"] != null ? (int)a.Long("first") : 0;
            int count = a != null && a["count"] != null ? (int)a.Long("count") : 200;
            List<CdbCol> cols = CdbCols(cdb);
            JObject r = new JObject();
            r["key"] = H(o.Key);
            r["name"] = NameOf(o);
            r["type"] = o.Type.Name;
            r["cdb_key"] = H(cdb.Key);
            IList entries = cdb["EntryAssets"] as IList ?? new object[0];
            long aux = cdb["NumAuxiliaryEntryAsset"] == null ? 0 : Num.ToLong(cdb["NumAuxiliaryEntryAsset"]);
            r["rows_total"] = entries.Count;
            r["auxiliary_rows"] = aux;
            r["debug_category"] = cdb["DebugCategoryId"] == null ? null : (object)Num.ToLong(cdb["DebugCategoryId"]);
            List<object> cj = new List<object>();
            foreach (CdbCol c in cols)
            {
                JObject j = new JObject();
                j["name"] = c.Name;
                j["interface"] = c.Interface;
                j["class_guid"] = c.Guid.ToString("x8");
                j["field_id"] = c.FieldId.ToString("x8");
                j["kind"] = c.Kind;
                j["field_type"] = c.Type;
                j["unit"] = c.Unit;
                j["custom"] = c.Custom;
                j["min"] = c.Min.HasValue ? (object)(double)c.Min.Value : null;
                j["max"] = c.Max.HasValue ? (object)(double)c.Max.Value : null;
                j["values"] = c.Values.Cast<object>().ToList();
                j["input_source"] = c.InputSource.HasValue ? (object)c.InputSource.Value : null;
                j["driver"] = c.Driver != null ? Ref(c.Driver.Key) : null;
                j["remap"] = c.Remap != null ? Ref(c.Remap.Key) : null;
                j["descriptor"] = c.Desc != null ? Ref(c.Desc.Key) : null;
                cj.Add(j);
            }
            r["columns"] = cj;
            List<object> rows = new List<object>();
            for (int i = first; i < entries.Count && rows.Count < count; i++)
            {
                GdStruct e = entries[i] as GdStruct;
                JObject rj = new JObject();
                rj["row"] = i;
                rj["auxiliary"] = i >= entries.Count - aux;
                List<object> cells = new List<object>();
                IList cl = e != null ? e["Cells"] as IList : null;
                for (int k = 0; k < cols.Count; k++)
                {
                    GdStruct cell = cl != null && k < cl.Count ? cl[k] as GdStruct : null;
                    JObject cjj = new JObject();
                    cjj["text"] = CellText(cols[k], cell);
                    cjj["int"] = cell != null && cell["Int0"] != null ? (object)Num.ToLong(cell["Int0"]) : null;
                    cjj["f0"] = cell != null && cell["Float0"] != null ? (object)Num.ToDouble(cell["Float0"]) : null;
                    cjj["f1"] = cell != null && cell["Float1"] != null ? (object)Num.ToDouble(cell["Float1"]) : null;
                    cjj["f2"] = cell != null && cell["Float2"] != null ? (object)Num.ToDouble(cell["Float2"]) : null;
                    cells.Add(cjj);
                }
                rj["cells"] = cells;
                List<object> assets = new List<object>();
                if (e != null)
                    foreach (object k in e["Assets"] as IList ?? new object[0])
                    {
                        ulong ak = Num.ToKey(k);
                        if (ak != 0) assets.Add(Ref(ak));
                    }
                rj["assets"] = assets;
                rows.Add(rj);
            }
            r["rows"] = rows;
            List<object> notes = new List<object>();
            if (cols.Any(c => c.Type < 0)) notes.Add("an unknown column points at a game value the editor does not know, so its cells are shown as raw numbers");
            notes.Add("cells: a bool column reads Int0 as 1 true / 0 false / 2 any, an enum column as the value (-1 = any), an int column as the value (-1 = any) and a bitmap column as the set of bits; a float column reads Float0 and Float1 as a range and an angle column reads Float0 as the angle and Float1 as the tolerance either side of it");
            notes.Add("the game stores float and angle cells at reduced precision: 255 steps between the column's minimum and maximum (128 for the tolerance), so finer edits are rounded when the game loads them");
            if (aux > 0) notes.Add("the last " + aux + " rows are auxiliary (continuation) rows");
            r["notes"] = notes;
            return r;
        }
        /// <summary>
        /// the context database rows that select this asset: every ContextDatabaseAsset that holds a key to it in a
        /// row's Assets, with the row's decoded cells. a: max (rows, 40)
        /// </summary>
        public List<object> PickedBy(object key, int max = 40)
        {
            ulong k = Root(key).Key;
            List<object> r = new List<object>();
            int roots = 0;
            foreach (int i in Db.RefsTo(k))
            {
                if (++roots > 400) break;
                GdObj root = Db.Obj(i);
                GdObj cdb = CdbOf(root);
                if (cdb == null) continue;
                IList entries = cdb["EntryAssets"] as IList;
                if (entries == null) continue;
                List<CdbCol> cols = null;
                long aux = cdb["NumAuxiliaryEntryAsset"] == null ? 0 : Num.ToLong(cdb["NumAuxiliaryEntryAsset"]);
                for (int row = 0; row < entries.Count; row++)
                {
                    GdStruct e = entries[row] as GdStruct;
                    if (e == null) continue;
                    bool hit = false;
                    foreach (object a in e["Assets"] as IList ?? new object[0]) if (Num.ToKey(a) == k) { hit = true; break; }
                    if (!hit) continue;
                    if (cols == null) cols = CdbCols(cdb);
                    JObject j = new JObject();
                    j["database"] = Ref(root.Key);
                    j["row"] = row;
                    j["auxiliary"] = row >= entries.Count - aux;
                    List<object> cells = new List<object>();
                    IList cl = e["Cells"] as IList;
                    for (int c = 0; c < cols.Count; c++)
                    {
                        GdStruct cell = cl != null && c < cl.Count ? cl[c] as GdStruct : null;
                        string text = CellText(cols[c], cell);
                        if (text == "any" || text.Length == 0) continue;      // only the conditions that narrow the row
                        JObject cj = new JObject();
                        cj["column"] = cols[c].Name;
                        cj["kind"] = cols[c].Kind;
                        cj["unit"] = cols[c].Unit;
                        cj["text"] = text;
                        cells.Add(cj);
                    }
                    j["cells"] = cells;
                    List<object> others = new List<object>();
                    foreach (object a in e["Assets"] as IList ?? new object[0])
                    {
                        ulong ak = Num.ToKey(a);
                        if (ak != 0 && ak != k) others.Add(Ref(ak));
                    }
                    j["with"] = others;
                    r.Add(j);
                    if (r.Count >= max) return r;
                }
            }
            return r;
        }

    }
}
