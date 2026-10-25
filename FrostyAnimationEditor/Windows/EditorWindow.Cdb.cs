using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>the Database tab: a context database as a table of named columns and decoded cells</summary>
    internal sealed partial class EditorWindow
    {
        private int cdbFirst;

        private void RenderCdb()
        {
            int page = 100;
            JObject v = M.Cdb(ObjKey, new JObject { ["first"] = (long)cdbFirst, ["count"] = (long)page });
            long total = v.Long("rows_total");
            long aux = v.Long("auxiliary_rows");
            List<JObject> cols = v.Arr("columns").Cast<JObject>().ToList();
            body.Children.Add(Ui.Wrap(total + " rows x " + cols.Count + " columns" + (aux > 0 ? " (" + aux + " auxiliary)" : "") +
                                      ". The game looks a row up by the columns and plays the assets on it.", Ui.Muted, 11));

            // the columns, with what the engine knows about each
            List<object[]> crows = new List<object[]>();
            foreach (JObject c in cols)
            {
                string range = c["min"] != null && c["max"] != null && (c.Dbl("min") != 0 || c.Dbl("max") != 0)
                    ? c.Dbl("min").ToString("0.###", CultureInfo.InvariantCulture) + " .. " + c.Dbl("max").ToString("0.###", CultureInfo.InvariantCulture) : "";
                List<string> vals = c.Arr("values").Cast<string>().ToList();
                FrameworkElement valsCell = vals.Count == 0 ? (FrameworkElement)Ui.T("", Ui.Muted)
                    : Ui.Dotted(Ui.T(vals.Count + " values", Ui.Muted, 11, false, false, string.Join("\n", vals.Select((s, i) => i + " " + s))));
                crows.Add(new object[]
                {
                    Ui.T(c.Str("name"), null, 12, true),
                    Ui.T(c.Str("kind"), c.Str("kind") == "unknown column" ? Ui.Warn : Ui.Muted, 11),
                    Ui.T(c.Str("unit"), Ui.Muted, 11),
                    Ui.T(range, Ui.Muted, 11),
                    valsCell,
                    Ui.T(c.Bool("custom") ? "game state" : c.Str("interface"), Ui.Muted, 11),
                    c.Obj("driver") != null ? (object)RefLink(c.Obj("driver")) : Ui.T("", Ui.Muted),
                });
            }
            body.Children.Add(Ui.H3("Columns"));
            body.Children.Add(Ui.Table(new[] { "name", "kind", "unit", "range", "values", "from", "driven by" }, crows));

            // the rows
            List<string> header = new List<string> { "#" };
            foreach (JObject c in cols) header.Add(c.Str("name"));
            header.Add("plays");
            List<object[]> rrows = new List<object[]>();
            foreach (JObject r in v.Arr("rows").Cast<JObject>())
            {
                List<object> cells = new List<object> { Ui.T(r.Long("row").ToString(CultureInfo.InvariantCulture), r.Bool("auxiliary") ? Ui.Warn : Ui.Muted, 11, false, true,
                    r.Bool("auxiliary") ? "an auxiliary (continuation) row" : null) };
                foreach (object o in r.Arr("cells"))
                {
                    JObject cj = (JObject)o;
                    string t = cj.Str("text");
                    cells.Add(Ui.Trim(Ui.T(t, t == "any" ? Ui.Muted : null, 11, false, true)));
                }
                List<JObject> assets = r.Arr("assets").Cast<JObject>().ToList();
                if (assets.Count == 0) cells.Add(Ui.T("(none)", Ui.Muted, 11));
                else if (assets.Count == 1) cells.Add(RefLink(assets[0]));
                else
                {
                    StackPanel sp = Ui.V();
                    foreach (JObject a in assets) sp.Children.Add(RefLink(a));
                    cells.Add(sp);
                }
                rrows.Add(cells.ToArray());
            }
            body.Children.Add(Ui.H3("Rows"));
            if (total > page)
            {
                StackPanel nav = Ui.H(
                    Ui.B("first", () => { cdbFirst = 0; SetTab("cdb"); }, null, true),
                    Ui.B("< back", () => { cdbFirst = Math.Max(0, cdbFirst - page); SetTab("cdb"); }, null, true),
                    Ui.Pad(Ui.T("rows " + cdbFirst + " .. " + Math.Min(total - 1, cdbFirst + page - 1), Ui.Muted, 11), 8, 3, 8, 0),
                    Ui.B("next >", () => { if (cdbFirst + page < total) { cdbFirst += page; SetTab("cdb"); } }, null, true));
                body.Children.Add(nav);
            }
            body.Children.Add(Ui.Table(header, rrows));
            foreach (object n in v.Arr("notes")) body.Children.Add(Ui.Wrap((string)n, Ui.Muted, 11));
        }
    }
}
