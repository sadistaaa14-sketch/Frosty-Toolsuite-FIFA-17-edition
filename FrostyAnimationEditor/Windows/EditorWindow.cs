using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Frosty.Controls;
using FrostyAnimationEditor.Core;
using FrostyAnimationEditor.Plugin;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>
    /// The animation editor: classes | objects of the class | the selected object (fields, references and the
    /// animation / state machine / blend space / expression / curve views). Every edit is one undoable change; the
    /// container is written back to the Frosty project with "Save to project".
    /// </summary>
    internal sealed partial class EditorWindow : FrostyWindow
    {
        public EditorModel M { get; private set; }
        private LoadedSource src;

        // navigation state
        private string typeSel;
        public string ObjKey { get; private set; }
        private readonly List<string> hist = new List<string>(), fwd = new List<string>();
        public JObject View { get; private set; }
        private string tab = "fields";
        private readonly HashSet<string> open = new HashSet<string>();
        private List<JObject> types = new List<JObject>();
        private int objOff;
        private static readonly object MoreMarker = new object();

        // controls
        private readonly TextBlock fileLabel = Ui.Trim(Ui.T("", Ui.Muted));
        private readonly Button bSaveFrosty, bSaveFile, bUndo, bRedo;
        private readonly TextBox typeFilter = Ui.Box("", double.NaN, false, "filter classes");
        private readonly ListBox typeList = new ListBox();
        private readonly TextBox objFilter = Ui.Box("", double.NaN, false, "filter by name or 0x key (Enter)");
        private readonly TextBlock objCount = Ui.T("", Ui.Muted, 11);
        private readonly ListBox objList = new ListBox();
        private readonly Button bBack, bFwd;
        private readonly StackPanel title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly WrapPanel actions = new WrapPanel();
        private readonly Dictionary<string, Button> tabs = new Dictionary<string, Button>();
        private readonly CheckBox exported = Ui.Check("exported (C)", false, "the key is in table C: other containers may reference this object");
        private readonly ScrollViewer bodyScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly StackPanel body = new StackPanel { Margin = new Thickness(10, 6, 10, 40) };
        private readonly TextBlock stMsg = Ui.Trim(Ui.T(""));
        private readonly TextBlock stInfo = Ui.T("", Ui.Muted, 11);

        private static readonly string[] PanelTabs = { "clip", "seq", "chooser", "layers", "mask", "steer", "tags", "tree", "sm", "blend", "expr", "curves", "cdb" };

        public EditorWindow(LoadedSource source)
        {
            src = source;
            M = source.Model;
            Title = "Animation Editor";
            Width = Math.Min(1500, SystemParameters.WorkArea.Width - 60);
            Height = Math.Min(950, SystemParameters.WorkArea.Height - 60);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Back;
            ShowInTaskbar = true;

            // ---- toolbar
            bSaveFrosty = Ui.B("Save to project", SaveToFrosty, "write the edited container into the Frosty project (Ctrl+S); save the project to keep it");
            Button bOpen = Ui.B("Open .res…", OpenFile, "edit a .res file exported from Frosty instead");
            bSaveFile = Ui.B("Save .res…", SaveFile, "write the edited container to a .res file (with the 16-byte ResMeta, as Frosty exports it)");
            bUndo = Ui.B("Undo", Undo, "Undo (Ctrl+Z)");
            bRedo = Ui.B("Redo", Redo, "Redo (Ctrl+Y)");
            Button bNew = Ui.B("New object…", NewObject);
            Button bCheck = Ui.B("Check…", CheckDialog, "consistency and engine limits");
            DockPanel bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6), LastChildFill = true };
            StackPanel barRight = Ui.H(bSaveFrosty, bOpen, bSaveFile, Ui.Pad(new Separator { Width = 1 }, 4, 0, 4, 0), bUndo, bRedo, bNew, bCheck);
            DockPanel.SetDock(barRight, Dock.Right);
            bar.Children.Add(barRight);
            bar.Children.Add(Ui.H(Ui.T("FIFA 17 Animation Editor", null, 13, true), Ui.Pad(fileLabel, 10, 0, 0, 0)));

            // ---- status bar
            DockPanel status = new DockPanel { Margin = new Thickness(8, 3, 8, 4) };
            DockPanel.SetDock(stInfo, Dock.Right);
            status.Children.Add(stInfo);
            status.Children.Add(stMsg);

            // ---- classes column
            DockPanel colTypes = new DockPanel();
            DockPanel.SetDock(typeFilter, Dock.Top);
            colTypes.Children.Add(typeFilter);
            colTypes.Children.Add(typeList);
            typeFilter.TextChanged += (s, e) => RenderTypes();
            typeList.SelectionChanged += (s, e) =>
            {
                ListBoxItem it = typeList.SelectedItem as ListBoxItem;
                if (it == null || ignoreTypeSel) return;
                typeSel = it.Tag as string;
                LoadObjects(false);
            };

            // ---- objects column
            DockPanel colObjs = new DockPanel();
            StackPanel objTop = Ui.V(objFilter, Ui.Pad(objCount, 6, 0, 0, 2));
            DockPanel.SetDock(objTop, Dock.Top);
            colObjs.Children.Add(objTop);
            colObjs.Children.Add(objList);
            objFilter.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; LoadObjects(false); } };
            objList.SelectionChanged += (s, e) =>
            {
                ListBoxItem it = objList.SelectedItem as ListBoxItem;
                if (it == null || ignoreObjSel) return;
                if (it.Tag is string) Go((string)it.Tag);
            };

            // ---- editor column
            bBack = Ui.B("←", Back, "Back (Alt+Left)", true);
            bFwd = Ui.B("→", Forward, "Forward (Alt+Right)", true);
            DockPanel head1 = new DockPanel();
            StackPanel nav = Ui.H(bBack, bFwd);
            DockPanel.SetDock(nav, Dock.Left);
            head1.Children.Add(nav);
            head1.Children.Add(title);
            foreach (KeyValuePair<string, string> t in new[] { Ui.KV("fields", "Fields"), Ui.KV("refs", "References"), Ui.KV("clip", "Animation"),
                     Ui.KV("seq", "Sequence"), Ui.KV("chooser", "Chooser"), Ui.KV("layers", "Layers"), Ui.KV("mask", "Blend mask"), Ui.KV("steer", "Steering"), Ui.KV("tags", "Tags"), Ui.KV("tree", "Controllers"), Ui.KV("sm", "State machine"), Ui.KV("blend", "Blend space"), Ui.KV("expr", "Expression"), Ui.KV("curves", "Curves"), Ui.KV("cdb", "Database") })
            {
                string id = t.Key;
                Button tb = Ui.B(t.Value, () => SetTab(id));
                tabs[id] = tb;
                actions.Children.Add(tb);
            }
            actions.Children.Add(Ui.Pad(new Border { Width = 18 }, 0, 0, 0, 0));
            actions.Children.Add(Ui.B("Edit JSON…", EditJson, "the whole object as JSON text"));
            actions.Children.Add(Ui.B("Graph…", GraphDialog, "the objects this one references (and the ones referencing it) as a graph"));
            actions.Children.Add(Ui.B("Clone…", CloneDialog));
            actions.Children.Add(Ui.B("Delete", DeleteObject));
            actions.Children.Add(exported);
            exported.Click += (s, e) => { if (ObjKey != null) Guard(() => { M.ExportFlag(ObjKey, Ui.On(exported)); RefreshState(); }); };
            StackPanel head = Ui.V(head1, actions);
            head.Margin = new Thickness(8, 4, 8, 4);
            actions.Visibility = Visibility.Collapsed;
            DockPanel colEd = new DockPanel();
            Border headBorder = new Border { Child = head, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(headBorder, Dock.Top);
            colEd.Children.Add(headBorder);
            bodyScroll.Content = body;
            colEd.Children.Add(bodyScroll);

            // ---- main grid
            Grid main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240), MinWidth = 120 });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 150 });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            AddCol(main, colTypes, 0);
            AddCol(main, Splitter(), 1);
            AddCol(main, colObjs, 2);
            AddCol(main, Splitter(), 3);
            AddCol(main, colEd, 4);

            DockPanel root = new DockPanel();
            Border barBorder = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(barBorder, Dock.Top);
            root.Children.Add(barBorder);
            Border stBorder = new Border { Child = status, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(stBorder, Dock.Bottom);
            root.Children.Add(stBorder);
            root.Children.Add(main);
            Content = root;

            PreviewKeyDown += OnKey;
            Closing += OnClosing;
            Loaded += (s, e) =>
            {
                LoadTypes();
                LoadObjects(false);
                RefreshState();
                Msg("opened " + M.SourceLabel + (src.CacheNote != null ? " · " + src.CacheNote : ""));
                ShowEmpty();
            };
        }

        private static void AddCol(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static GridSplitter Splitter()
        {
            return new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Ui.Soft, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        }

        // ================================================================== errors / status
        /// <summary>errors shown to the user instead of ending the program</summary>
        public static bool IsUserError(Exception e)
        {
            return !(e is OutOfMemoryException || e is StackOverflowException || e is AccessViolationException || e is System.Threading.ThreadAbortException);
        }

        public void Msg(string text, bool bad = false)
        {
            stMsg.Text = text ?? "";
            stMsg.Foreground = bad ? (Brush)Ui.Bad : Ui.Fg;
            stMsg.ToolTip = text;
        }

        /// <summary>runs an action; an error goes to the status bar (and the log for unexpected ones)</summary>
        public bool Guard(Action a)
        {
            try
            {
                a();
                return true;
            }
            catch (Exception e) when (IsUserError(e))
            {
                Report(e);
                return false;
            }
        }

        public void Report(Exception e)
        {
            Msg(e.Message, true);
            if (!(e is ApiError || e is GdException || e is KernelException || e is FormatException))
                EditorCache.Log("Animation Editor: " + e);
        }

        public T Busy<T>(string what, Func<Action<string, double>, T> job)
        {
            return Work.Run(this, "Animation Editor", what, job);
        }

        public void RefreshState()
        {
            JObject s = M.State();
            List<object> undo = s.Arr("undo"), redo = s.Arr("redo");
            bUndo.IsEnabled = undo.Count > 0;
            bRedo.IsEnabled = redo.Count > 0;
            bUndo.ToolTip = undo.Count > 0 ? "Undo: " + undo[undo.Count - 1] + " (Ctrl+Z)" : "Undo (Ctrl+Z)";
            bRedo.ToolTip = redo.Count > 0 ? "Redo: " + redo[redo.Count - 1] + " (Ctrl+Y)" : "Redo (Ctrl+Y)";
            long changes = s.Long("changes");
            stInfo.Text = Ui.Thousands(s.Long("objects")) + " objects · " + Ui.Plural(changes, "change") + " · " + s.Long("added") + " added · " +
                          s.Long("removed") + " removed" + (M.S.Dirty ? " · not saved" : "");
            fileLabel.Text = M.SourceLabel ?? "";
            bSaveFrosty.IsEnabled = src.Frosty != null || FrostyTargetAvailable();
        }

        private static bool FrostyTargetAvailable()
        {
            try { return global::Frosty.Core.App.AssetManager != null; } catch (Exception) { return false; }
        }

        // ================================================================== classes / objects
        private bool ignoreTypeSel, ignoreObjSel;

        private void LoadTypes()
        {
            types = M.Types().Cast<JObject>().ToList();
            RenderTypes();
        }

        private void RenderTypes()
        {
            ignoreTypeSel = true;
            typeList.Items.Clear();
            string f = typeFilter.Text.Trim().ToLowerInvariant();
            long all = types.Sum(t => t.Long("count"));
            typeList.Items.Add(TypeItem(null, "all classes", all));
            foreach (JObject t in types)
            {
                string n = t.Str("name");
                if (f.Length > 0 && n.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) < 0) continue;
                ListBoxItem it = TypeItem(n, n, t.Long("count"));
                it.ToolTip = n + " " + t.Str("tid");
                typeList.Items.Add(it);
            }
            foreach (object o in typeList.Items)
                if (((ListBoxItem)o).Tag as string == typeSel) { typeList.SelectedItem = o; break; }
            ignoreTypeSel = false;
        }

        private static ListBoxItem TypeItem(string tag, string label, long count)
        {
            DockPanel d = new DockPanel { LastChildFill = true };
            TextBlock c = Ui.T(Ui.Thousands(count), Ui.Muted, 11);
            c.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(c, Dock.Right);
            d.Children.Add(c);
            d.Children.Add(Ui.Trim(Ui.T(label)));
            return new ListBoxItem { Content = d, Tag = tag, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }

        private void LoadObjects(bool more)
        {
            Guard(() =>
            {
                string q = objFilter.Text.Trim();
                if (!more) objOff = 0;
                bool byKey = q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && q.Length >= 6;
                ignoreObjSel = true;
                if (!more) objList.Items.Clear();
                else if (objList.Items.Count > 0 && (objList.Items[objList.Items.Count - 1] as ListBoxItem).Tag == MoreMarker) objList.Items.RemoveAt(objList.Items.Count - 1);
                if (typeSel == null && q.Length == 0)
                {
                    objCount.Text = "choose a class, or search all classes by name";
                    ignoreObjSel = false;
                    return;
                }
                string t = byKey ? null : typeSel;
                JObject r = M.Objects(t, q, objOff, 500);
                List<object> items = r.Arr("items");
                long total = r.Long("total");
                foreach (object o in items)
                {
                    JObject it = (JObject)o;
                    string name = Ui.Label(it);
                    StackPanel p = Ui.H(Ui.T(string.IsNullOrEmpty(name) ? "(no name)" : name, string.IsNullOrEmpty(name) ? Ui.Muted : null));
                    if (it.Str("alias") != null) p.Children.Add(Ui.Pad(Ui.T(it.Str("name"), Ui.Muted, 11, false, true), 6, 0, 0, 0));
                    if (t == null) p.Children.Add(Ui.Pad(Ui.T(it.Str("type"), Ui.Muted, 11, false, true), 6, 0, 0, 0));
                    ListBoxItem li = new ListBoxItem { Content = p, Tag = it.Str("key"), ToolTip = it.Str("type") + " " + it.Str("key") };
                    objList.Items.Add(li);
                    if (it.Str("key") == ObjKey) objList.SelectedItem = li;
                }
                objOff += items.Count;
                objCount.Text = Ui.Thousands(total) + " objects" + (total > objOff ? " (showing " + Ui.Thousands(objOff) + ")" : "");
                if (total > objOff)
                {
                    ListBoxItem moreItem = new ListBoxItem { Content = Ui.T("show more…", Ui.Accent), Tag = MoreMarker };
                    moreItem.PreviewMouseLeftButtonUp += (s, e) => { e.Handled = true; LoadObjects(true); };
                    objList.Items.Add(moreItem);
                }
                ignoreObjSel = false;
            });
            ignoreObjSel = false;
        }

        private void MarkSelected()
        {
            ignoreObjSel = true;
            objList.SelectedItem = null;
            foreach (object o in objList.Items)
                if (((ListBoxItem)o).Tag as string == ObjKey) { objList.SelectedItem = o; break; }
            ignoreObjSel = false;
        }

        // ================================================================== navigation
        public void Go(string key, bool noHist = false)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (ObjKey != null && !noHist && ObjKey != key)
            {
                hist.Add(ObjKey);
                fwd.Clear();
            }
            ObjKey = key;
            MarkSelected();
            LoadObject(false);
        }

        private void Back()
        {
            if (hist.Count == 0) return;
            fwd.Add(ObjKey);
            string k = hist[hist.Count - 1];
            hist.RemoveAt(hist.Count - 1);
            Go(k, true);
        }

        private void Forward()
        {
            if (fwd.Count == 0) return;
            hist.Add(ObjKey);
            string k = fwd[fwd.Count - 1];
            fwd.RemoveAt(fwd.Count - 1);
            Go(k, true);
        }

        private void ShowEmpty()
        {
            title.Children.Clear();
            title.Children.Add(Ui.T("select an object", Ui.Muted));
            actions.Visibility = Visibility.Collapsed;
            body.Children.Clear();
            body.Children.Add(Ui.Wrap("Pick a class on the left and an object of it, or search all classes by name / 0x key. " +
                "Edits are kept in this window until you write them to the project with \"Save to project\" (then save the Frosty project or export a mod).", Ui.Muted));
            bBack.IsEnabled = hist.Count > 0;
            bFwd.IsEnabled = fwd.Count > 0;
        }

        /// <summary>reloads the current object's view (after an edit, undo or navigation)</summary>
        public void LoadObject(bool keepScroll = true)
        {
            bBack.IsEnabled = hist.Count > 0;
            bFwd.IsEnabled = fwd.Count > 0;
            if (ObjKey == null) { ShowEmpty(); return; }
            JObject v;
            try { v = M.ObjView(ObjKey); }
            catch (Exception e) when (IsUserError(e))
            {
                Report(e);
                View = null;
                title.Children.Clear();
                title.Children.Add(Ui.T(ObjKey + " is not in the file", Ui.Bad));
                actions.Visibility = Visibility.Collapsed;
                body.Children.Clear();
                return;
            }
            View = v;
            title.Children.Clear();
            title.Children.Add(Ui.Trim(Ui.T(string.Join(" : ", v.Arr("chain").Cast<string>()), Ui.Muted)));
            title.Children.Add(Ui.Pad(Ui.T(string.IsNullOrEmpty(Ui.Label(v)) ? "(no name)" : Ui.Label(v), null, 13, true), 8, 0, 0, 0));
            if (v.Str("alias") != null) title.Children.Add(Ui.Pad(Ui.T(v.Str("name"), Ui.Muted, 12, false, true), 8, 0, 0, 0));
            TextBlock kt = Ui.T(v.Str("key"), Ui.Muted, 12, false, true);
            kt.Margin = new Thickness(8, 0, 0, 0);
            kt.ToolTip = "double-click to copy the key";
            kt.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { try { Clipboard.SetText(v.Str("key")); Msg("copied " + v.Str("key")); } catch (Exception) { } } };
            title.Children.Add(kt);
            if (v.Bool("new")) title.Children.Add(Ui.Badge("new"));
            actions.Visibility = Visibility.Visible;
            exported.IsChecked = v.Bool("exported");
            List<string> panels = v.Arr("panels").Cast<string>().ToList();
            foreach (KeyValuePair<string, Button> t in tabs)
                t.Value.Visibility = PanelTabs.Contains(t.Key) && !panels.Contains(t.Key) ? Visibility.Collapsed : Visibility.Visible;
            if (PanelTabs.Contains(tab) && !panels.Contains(tab)) tab = "fields";
            SetTab(tab, keepScroll);
        }

        private void SetTab(string t, bool keepScroll = false)
        {
            double offset = keepScroll && t == tab ? bodyScroll.VerticalOffset : 0;
            tab = t;
            foreach (KeyValuePair<string, Button> b in tabs)
            {
                bool on = b.Key == t;
                b.Value.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
                b.Value.BorderBrush = on ? Ui.Accent : Ui.Line;
                b.Value.BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0);
            }
            StopPreview();
            body.Children.Clear();
            if (View == null) return;
            try
            {
                switch (t)
                {
                    case "fields": RenderFields(); break;
                    case "refs": RenderRefs(); break;
                    case "clip": RenderClip(); break;
                    case "seq": RenderSeq(); break;
                    case "tags": RenderTags(); break;
                    case "chooser": RenderChooser(); break;
                    case "cdb": RenderCdb(); break;
                    case "layers": RenderLayers(); break;
                    case "mask": RenderMask(); break;
                    case "steer": RenderSteering(); break;
                    case "tree": RenderTree(); break;
                    case "sm": RenderSm(); break;
                    case "blend": RenderBlend(); break;
                    case "expr": RenderExpr(false); break;
                    case "curves": RenderCurves(); break;
                }
            }
            catch (Exception e) when (IsUserError(e))
            {
                Report(e);
                body.Children.Add(Ui.Wrap(e.Message, Ui.Bad));
            }
            if (offset > 0)
            {
                bodyScroll.UpdateLayout();
                bodyScroll.ScrollToVerticalOffset(offset);
            }
            else bodyScroll.ScrollToTop();
        }

        /// <summary>after an edit: the object again, the status bar</summary>
        public void AfterEdit()
        {
            LoadObject();
            RefreshState();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool inText = Keyboard.FocusedElement is TextBox;
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !inText) { e.Handled = true; Undo(); }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control && !inText) { e.Handled = true; Redo(); }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SaveToFrosty(); }
            else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; Back(); }
            else if (e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; Forward(); }
        }

        // ================================================================== toolbar
        private void Undo()
        {
            Guard(() =>
            {
                string lab = M.Undo();
                Msg(lab != null ? "undone: " + lab : "nothing to undo");
                AfterUndo();
            });
        }

        private void Redo()
        {
            Guard(() =>
            {
                string lab = M.Redo();
                Msg(lab != null ? "redone: " + lab : "nothing to redo");
                AfterUndo();
            });
        }

        private void AfterUndo()
        {
            PlayerWindow.Refresh();
            LoadTypes();
            if (ObjKey != null) LoadObject();
            RefreshState();
        }

        private void SaveToFrosty()
        {
            if (!bSaveFrosty.IsEnabled) return;
            FrostyAntState target = src.Frosty;
            if (target == null)
            {
                // a .res opened from disk: offer to write it into the project's AntStateAsset
                List<FrostySdk.Managers.EbxAssetEntry> found = FrostyAntState.Find();
                if (found.Count == 0) { Msg("this game has no AntStateAsset to write to", true); return; }
                FrostySdk.Managers.EbxAssetEntry e = found.Count == 1 ? found[0] : Dialog.Pick(this, "Save to project", "Write the edited container into which asset?", found, x => x.Name);
                if (e == null) return;
                if (!Dialog.Confirm(this, "Save to project", "Replace the data of " + e.Name + " in the project with this edited file?")) return;
                try { target = FrostyAntState.Locate(e); }
                catch (Exception ex) when (IsUserError(ex)) { Report(ex); return; }
            }
            SaveResult r;
            try
            {
                r = Busy("writing " + target.Name, progress =>
                {
                    SaveResult res = M.Save(true, null, null, progress);
                    progress("handing the data to Frosty (compressing " + (res.Image.Length >> 20) + " MB)", 0.9);
                    target.WriteData(res.Image);
                    try { EditorLauncher.CacheSaved(M.Db, res, target.Name, progress); }
                    catch (Exception ce) { EditorCache.Log("Animation Editor: the cache of the saved data was not written: " + ce.Message); }
                    return res;
                });
            }
            catch (Exception e) when (IsUserError(e))
            {
                M.S.Dirty = true;
                Report(e);
                Dialog.Info(this, "Save to project", "Saving failed:\n" + e.Message);
                return;
            }
            try { target.Link(); }
            catch (Exception e) when (IsUserError(e)) { Report(e); }
            src.Frosty = target;
            M.SourceLabel = target.Name + " (modified in this project)";
            RefreshState();
            try
            {
                if (global::Frosty.Core.App.EditorWindow != null && global::Frosty.Core.App.EditorWindow.DataExplorer != null)
                    global::Frosty.Core.App.EditorWindow.DataExplorer.RefreshItems();
            }
            catch (Exception) { }
            string summary = SaveSummary(r);
            Msg("written to the project: " + target.Name + " · " + summary);
            if (r.Warnings.Count > 0)
                Dialog.Info(this, "Save to project", "Written, with warnings (the game may not load these edits):\n\n" + string.Join("\n", r.Warnings.Take(30)));
        }

        private static string SaveSummary(SaveResult r)
        {
            string s = Ui.Thousands(r.Blocks) + " objects, " + r.Modified + " changed";
            if (r.Plan != null && r.Plan.Measured) s += ", loading limits " + r.H7 + " references / 0x" + r.H8.ToString("X") + " bytes";
            return s;
        }

        private void SaveFile()
        {
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Frosty resource (*.res)|*.res|All files|*.*",
                FileName = src.FilePath != null ? Path.GetFileNameWithoutExtension(src.FilePath) + "_edited.res" : "fifagame_win32_antstate.res",
                Title = "Save the edited container as a .res file"
            };
            if (src.FilePath != null) d.InitialDirectory = Path.GetDirectoryName(src.FilePath);
            if (d.ShowDialog(this) != true) return;
            string path = d.FileName;
            if (src.FilePath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(src.FilePath), StringComparison.OrdinalIgnoreCase))
            {
                // the open file's bytes are in memory, so writing over it is safe
            }
            Dialog opt = new Dialog(this, "Save .res", 560);
            TextBox h7 = Ui.Box(""), h8 = Ui.Box("");
            Ui.Form f = new Ui.Form(170);
            f.Row("reference limit", h7);
            f.Row("loading memory (bytes)", h8);
            opt.Add(Ui.Wrap(path, null, 12, true),
                    Ui.Wrap("Loading limits stored in the file (advanced): left empty, the editor keeps the file's values and raises them when your edits need more.", Ui.Muted, 11),
                    f.Grid,
                    Ui.Wrap("Count fields, two-way links, blend space diagrams and the state machines' lookup tables are brought up to date before writing.", Ui.Muted, 11));
            uint? v7 = null, v8 = null;
            opt.Button("Cancel");
            opt.Button("Save", () =>
            {
                v7 = h7.Text.Trim().Length > 0 ? (uint?)ParseU32(h7.Text, "reference limit") : null;
                v8 = h8.Text.Trim().Length > 0 ? (uint?)ParseU32(h8.Text, "loading memory") : null;
                return true;
            }, true);
            if (!opt.Run()) return;
            SaveResult r;
            try
            {
                r = Busy("writing " + Path.GetFileName(path), progress =>
                {
                    SaveResult res = M.Save(false, v7, v8, progress);
                    progress("writing " + path, 0.92);
                    string tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, res.Image);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    try { EditorLauncher.CacheSaved(M.Db, res, "file_" + Path.GetFileNameWithoutExtension(path), progress); }
                    catch (Exception ce) { EditorCache.Log("Animation Editor: the cache of the saved file was not written: " + ce.Message); }
                    return res;
                });
            }
            catch (Exception e) when (IsUserError(e))
            {
                M.S.Dirty = true;
                Report(e);
                Dialog.Info(this, "Save .res", "Saving failed:\n" + e.Message);
                return;
            }
            RefreshState();
            Msg("saved " + path + " · " + SaveSummary(r));
            if (r.Warnings.Count > 0)
                Dialog.Info(this, "Save .res", "Saved, with warnings (the game may not load these edits):\n\n" + string.Join("\n", r.Warnings.Take(30)));
        }

        private static uint ParseU32(string s, string what)
        {
            s = s.Trim();
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt32(s.Substring(2), 16);
                return uint.Parse(s, CultureInfo.InvariantCulture);
            }
            catch (Exception) { throw new FormatException(what + ": a number (decimal or 0x hex) up to 4294967295"); }
        }

        private void OpenFile()
        {
            if (M.S.Dirty && !Dialog.Confirm(this, "Open .res", "Discard the edits that are not saved?")) return;
            Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog { Filter = "Frosty resource (*.res)|*.res|All files|*.*", Title = "Open an antstate .res file" };
            if (d.ShowDialog(this) != true) return;
            LoadedSource ls;
            try { ls = EditorLauncher.LoadFile(this, d.FileName); }
            catch (Exception e) when (IsUserError(e))
            {
                Report(e);
                Dialog.Info(this, "Open .res", "Could not open " + d.FileName + ":\n" + e.Message);
                return;
            }
            src = ls;
            M = ls.Model;
            PlayerWindow.CloseAll();
            ObjKey = null;
            hist.Clear();
            fwd.Clear();
            open.Clear();
            typeSel = null;
            LoadTypes();
            LoadObjects(false);
            ShowEmpty();
            RefreshState();
            Msg("opened " + d.FileName + " · " + ls.CacheNote);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopPreview();
            if (!M.S.Dirty) return;
            MessageBoxResult r = FrostyMessageBox.Show("Write the edits to the project before closing?\n(No discards them.)", "Animation Editor", MessageBoxButton.YesNoCancel);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes)
            {
                SaveToFrosty();
                if (M.S.Dirty) e.Cancel = true;
            }
        }

        // ================================================================== object actions
        private void NewObject()
        {
            Dialog d = new Dialog(this, "New object", 680);
            CheckBox allClasses = Ui.Check("list every class of the type table (also those without objects)", false);
            ComboBox cls = Ui.Combo(M.Types(true).Cast<JObject>().Select(t => Ui.KV(t.Str("name"), t.Str("name") + "  (" + t.Long("count") + ")")), typeSel ?? "");
            TextBox name = Ui.Box("");
            JObject tpl = null;
            TextBlock tplShow = Ui.T("no template: a blank object of the class (with its base-class parts)", Ui.Muted, 11);
            ObjectPicker pk = new ObjectPicker(M, typeSel);
            pk.Picked += o => { tpl = o; tplShow.Text = "template: " + Ui.Label(o) + " " + o.Str("key"); tplShow.Foreground = Ui.Fg; Ui.Select(cls, o.Str("type")); };
            Ui.Form f = new Ui.Form(80);
            f.Row("class", cls);
            f.Row("name", name);
            d.Add(f.Grid, Ui.Wrap("Pick a template to copy instead (optional):", Ui.Muted, 11), pk.Panel, tplShow,
                  Ui.Pad(Ui.Wrap("A copy keeps the template's references; a blank object has every field zero / empty.", Ui.Muted, 11), 0, 6, 0, 0));
            d.Button("Cancel");
            d.Button("Clear template", () => { tpl = null; tplShow.Text = "no template"; tplShow.Foreground = Ui.Muted; return false; });
            string made = null;
            d.Button("Create", () =>
            {
                string c = Ui.Value(cls);
                if (string.IsNullOrEmpty(c)) throw new ApiError("choose a class");
                JObject r = M.Create(c, name.Text.Trim(), tpl != null ? tpl.Str("key") : null);
                made = r.Str("key");
                return true;
            }, true);
            if (!d.Run() || made == null) return;
            LoadTypes();
            Go(made);
            RefreshState();
            Msg("created " + made);
        }

        private void CloneDialog()
        {
            if (View == null) return;
            Dialog d = new Dialog(this, "Clone " + View.Str("name"), 520);
            TextBox n = Ui.Box((View.Str("name") ?? "") + "_copy");
            TextBox c = Ui.Box("1", 80);
            Ui.Form f = new Ui.Form(80);
            f.Row("name", n);
            f.Row("copies", c);
            d.Add(f.Grid, Ui.Wrap("Copies get new keys; references inside a copy still point to the same objects.", Ui.Muted, 11));
            List<object> keys = null;
            d.Button("Cancel");
            d.Button("Clone", () =>
            {
                int count = Ui.ParseInt(c.Text, "copies");
                if (count < 1 || count > 1000) throw new FormatException("copies: 1 to 1000");
                keys = M.Clone(ObjKey, n.Text.Trim(), count).Arr("keys");
                return true;
            }, true);
            if (!d.Run() || keys == null || keys.Count == 0) return;
            LoadObjects(false);
            Go((string)keys[0]);
            RefreshState();
            Msg("cloned: " + string.Join(", ", keys.Take(5)) + (keys.Count > 5 ? ", …" : ""));
        }

        private void DeleteObject()
        {
            if (View == null) return;
            if (!Dialog.Confirm(this, "Delete", "Delete " + View.Str("type") + " \"" + View.Str("name") + "\"?\nObjects that reference it keep the key " +
                                "(References > \"Find objects that reference this one\" lists them)."))
                return;
            string name = View.Str("name");
            if (!Guard(() => M.Delete(new object[] { ObjKey }))) return;
            Msg("deleted " + name);
            ObjKey = null;
            View = null;
            LoadTypes();
            LoadObjects(false);
            ShowEmpty();
            RefreshState();
        }

        private void EditJson()
        {
            if (View == null) return;
            string text;
            try { text = M.JsonGet(ObjKey); }
            catch (Exception e) when (IsUserError(e)) { Report(e); return; }
            Dialog d = new Dialog(this, "Edit " + View.Str("name") + " as JSON", 900);
            TextBox ta = Ui.Area(text, 34);
            d.Add(ta, Ui.Wrap("Keys are \"0x…\" strings; nested objects carry \"$type\"; byte lists are {\"$bytes\": hex}; NaN / infinity are \"nan\" / \"inf\". The __key must stay.", Ui.Muted, 11));
            d.Button("Cancel");
            d.Button("Apply", () => { M.JsonPut(ObjKey, ta.Text); return true; }, true);
            if (d.Run())
            {
                AfterEdit();
                Msg("applied the JSON of " + View.Str("name"));
            }
        }

        private void GraphDialog()
        {
            if (View == null) return;
            bool isSm = View.Arr("panels").Cast<string>().Contains("sm");
            Dialog d = new Dialog(this, "Graph of " + Ui.Label(View), 520);
            TextBox depth = Ui.Box("2", 60);
            CheckBox inc = Ui.Check("also follow references to each object (incoming)", false);
            TextBox max = Ui.Box("600", 80);
            Ui.Form f = new Ui.Form(150);
            f.Row("reference depth", depth);
            f.Row("max objects", max);
            f.Row("", inc);
            d.Add(f.Grid, Ui.Wrap("Nodes are objects, arrows are Key fields. Drag to pan, wheel to zoom, drag a node to move it, double-click a node to open it here.", Ui.Muted, 11));
            int mode = 0;
            d.Button("Cancel");
            if (isSm) d.Button("State machine graph", () => { mode = 2; return true; });
            d.Button("Reference graph", () => { mode = 1; return true; }, true);
            if (!d.Run() || mode == 0) return;
            try
            {
                int dep = Ui.ParseInt(depth.Text, "depth"), mx = Ui.ParseInt(max.Text, "max objects");
                bool incoming = Ui.On(inc);
                string key = ObjKey;
                Graph g = Busy("building the graph", progress => M.GraphOf(key, mode == 2, Math.Max(1, Math.Min(8, dep)), incoming, Math.Max(10, Math.Min(5000, mx)),
                                                                         (a, b) => progress("indexing references " + a + " / " + b, b > 0 ? (double)a / b : 0)));
                new GraphWindow(this, g).Show();
            }
            catch (Exception e) when (IsUserError(e)) { Report(e); }
        }

        // ================================================================== check
        private void CheckDialog()
        {
            JObject r;
            try { r = Busy("checking", p => M.Check()); }
            catch (Exception e) when (IsUserError(e)) { Report(e); return; }
            Dialog d = new Dialog(this, "Consistency check", 860);
            Action<string> go = k => { d.Close(); Go(k); };
            d.Add(Ui.Wrap(r.Str("note"), Ui.Muted));
            Action<string, List<object>, Func<JObject, string>, bool> section = (head, items, line, bad) =>
            {
                d.Add(Ui.H3(head + ": " + items.Count));
                foreach (JObject x in items.Take(200).Cast<JObject>())
                {
                    string k = x.Str("key");
                    d.Add(Ui.Link(line(x), () => go(k), bad ? Ui.Bad : null));
                }
                if (items.Count > 200) d.Add(Ui.T("… " + (items.Count - 200) + " more", Ui.Muted));
            };
            section("State machine tables that differ from the rule", r.Arr("state_machines"), x => x.Str("name") + " " + x.Str("key"), false);
            section("Count fields out of date in edited objects", r.Arr("counts"), x => x.Str("key") + " " + x.Str("path") + " = " + Ui.Num(x["value"]) + ", list has " + Ui.Num(x["list"]), false);
            section("Two-way links that disagree", r.Arr("links"), x => x.Str("name") + ": " + x.Str("problem"), false);
            section("Edits the game cannot load (engine limits)", r.Arr("limits"), x => (string.IsNullOrEmpty(x.Str("name")) ? x.Str("key") : x.Str("name")) + " " + x.Str("path") + ": " + x.Str("problem"), true);
            section("Keys in edited objects that point to nothing", r.Arr("dangling"), x => x.Str("name") + " " + x.Str("path") + " -> " + x.Str("target"), false);
            d.Add(Ui.H3("Loading memory"),
                  Ui.Wrap("Checks that the edited file still fits in the memory the game sets aside for loading it (takes a few seconds).", Ui.Muted, 11));
            StackPanel rt = new StackPanel();
            Button measure = null;
            measure = Ui.B("Measure", () =>
            {
                JObject x;
                try { x = Busy("measuring loading memory", p => M.Runtime((a, b) => p("objects " + a + " / " + b, b > 0 ? (double)a / b : 0))); }
                catch (Exception e) when (IsUserError(e)) { rt.Children.Add(Ui.Wrap(e.Message, Ui.Bad)); return; }
                measure.Visibility = Visibility.Collapsed;
                Func<string, string> n = k => Ui.Thousands(x.Long(k));
                rt.Children.Add(Ui.Wrap("open references while loading: " + n("waiter_peak") + " (before your edits " + n("waiter_peak_original") + ") · limit stored in the file " + n("h7") + " · limit written on save " + n("h7_save")));
                rt.Children.Add(Ui.Wrap("memory used while loading: " + n("linear_bytes") + " bytes (before your edits " + n("linear_bytes_original") + ") + " + n("post_load_bytes") +
                                        " after loading · size stored in the file " + n("h8") + " · size written on save " + n("h8_save") + " · most the game allows " + n("capacity")));
                if (x.Long("linear_bytes") + x.Long("post_load_bytes") > x.Long("capacity")) rt.Children.Add(Ui.Wrap("more than the game allows: objects would fail to load", Ui.Bad));
                List<object> um = x.Arr("unmodelled");
                if (um.Count > 0) rt.Children.Add(Ui.Wrap("types the check cannot measure yet: " + string.Join(", ", um), Ui.Bad));
            });
            measure.HorizontalAlignment = HorizontalAlignment.Left;
            rt.Children.Add(measure);
            d.Add(rt);
            d.Button("Close", null, true);
            d.ShowDialog();
        }
    }
}
