using AnimationBrowserPlugin;
using AssetBankPlugin.Ant;
using AssetBankPlugin.Export;
using AssetBankPlugin.GenericData;
using Frosty.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AssetBankPlugin
{
    public class AnimationBrowserWindow : Window
    {
        private TreeView _tree;
        private TextBox _searchBox;
        private TextBlock _statusBar;
        private ListBox _resultsList;
        private Button _exportBtn;
        private Button _exportAllBtn;
        private TextBlock _infoPanel;
        private ProgressBar _progressBar;
        private CheckBox _subfolderCheck;
        private CheckBox _fbxCheck;

        private Bank _bank;
        private InternalSkeleton _skeleton;
        private string _outDir;
        private Dictionary<string, AnimationAsset> _animsByName;
        private static string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "frosty_anim_export.log");
        private static void Log(string msg)
        { try { File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " | " + msg + Environment.NewLine); } catch { } }

        public AnimationBrowserWindow(Bank bank, InternalSkeleton skeleton, string outDir)
        {
            _bank = bank; _skeleton = skeleton; _outDir = outDir;
            _animsByName = new Dictionary<string, AnimationAsset>();
            foreach (var kvp in bank.AssetsByName)
                if (kvp.Value is AnimationAsset anim) _animsByName[kvp.Key] = anim;

            Title = "FIFA 17 Animation Browser — " + _animsByName.Count + " animations";
            Width = 1050; Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            BuildUI(); PopulateTree(); UpdateStatus();
        }

        private void BuildUI()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Search bar
            var searchPanel = new DockPanel { Margin = new Thickness(8, 8, 8, 4) };
            searchPanel.Children.Add(new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) });
            
            var dumpBtn = new Button { Content = "Export Name List", Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0), FontSize = 11 };
            dumpBtn.Click += OnDumpNameList;
            DockPanel.SetDock(dumpBtn, Dock.Right);
            searchPanel.Children.Add(dumpBtn);


            _searchBox = new TextBox { };
            _searchBox.TextChanged += OnSearchChanged;
            searchPanel.Children.Add(_searchBox);
            Grid.SetRow(searchPanel, 0); Grid.SetColumnSpan(searchPanel, 4);
            root.Children.Add(searchPanel);

            // Left: category tree
            _tree = new TreeView { Margin = new Thickness(8, 0, 0, 0) };
            _tree.SelectedItemChanged += OnCategorySelected;
            Grid.SetRow(_tree, 1); Grid.SetColumn(_tree, 0);
            root.Children.Add(_tree);

            // Splitter
            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetRow(splitter, 1); Grid.SetColumn(splitter, 1);
            root.Children.Add(splitter);

            // Middle: results list with context menu
            _resultsList = new ListBox { Margin = new Thickness(0, 0, 0, 0), SelectionMode = SelectionMode.Extended };
            _resultsList.SelectionChanged += OnSelectionChanged;
            _resultsList.MouseDoubleClick += OnDoubleClickExport;

            var ctx = new ContextMenu();
            var miCopy = new MenuItem { Header = "Copy Name" };
            miCopy.Click += (s, e) => {
                if (_resultsList.SelectedItem is string n) Clipboard.SetText(n);
            };
            ctx.Items.Add(miCopy);
            var miCopyAll = new MenuItem { Header = "Copy All Visible Names" };
            miCopyAll.Click += (s, e) => {
                var sb = new StringBuilder();
                foreach (var item in _resultsList.Items) sb.AppendLine(item.ToString());
                Clipboard.SetText(sb.ToString());
            };
            ctx.Items.Add(miCopyAll);
            ctx.Items.Add(new Separator());
            var miExport = new MenuItem { Header = "Export Selected" };
            miExport.Click += OnExportSelected;
            ctx.Items.Add(miExport);
            _resultsList.ContextMenu = ctx;

            Grid.SetRow(_resultsList, 1); Grid.SetColumn(_resultsList, 2);
            root.Children.Add(_resultsList);

            // Right: info panel
            var infoScroll = new ScrollViewer { Margin = new Thickness(4, 0, 8, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _infoPanel = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "Select an animation\nto see details.\n\nDouble-click to\nexport single anim."
            };
            infoScroll.Content = _infoPanel;
            Grid.SetRow(infoScroll, 1); Grid.SetColumn(infoScroll, 3);
            root.Children.Add(infoScroll);

            // Progress bar
            _progressBar = new ProgressBar { Height = 4, Margin = new Thickness(8, 2, 8, 0), Visibility = Visibility.Collapsed };
            Grid.SetRow(_progressBar, 2); Grid.SetColumnSpan(_progressBar, 4);
            root.Children.Add(_progressBar);

            // Bottom bar
            var bottomPanel = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
            _exportAllBtn = new Button { Content = "Export All Visible", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 0, 0) };
            _exportAllBtn.Click += OnExportAll;
            DockPanel.SetDock(_exportAllBtn, Dock.Right);
            bottomPanel.Children.Add(_exportAllBtn);

            _exportBtn = new Button { Content = "Export Selected", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 0, 0) };
            _exportBtn.Click += OnExportSelected;
            DockPanel.SetDock(_exportBtn, Dock.Right);
            bottomPanel.Children.Add(_exportBtn);

            _subfolderCheck = new CheckBox { Content = "Category subfolders", IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            DockPanel.SetDock(_subfolderCheck, Dock.Right);
            bottomPanel.Children.Add(_subfolderCheck);

            _fbxCheck = new CheckBox { Content = "Export FBX (via Blender)", IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            DockPanel.SetDock(_fbxCheck, Dock.Right);
            bottomPanel.Children.Add(_fbxCheck);

            _statusBar = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            bottomPanel.Children.Add(_statusBar);

            Grid.SetRow(bottomPanel, 3); Grid.SetColumnSpan(bottomPanel, 4);
            root.Children.Add(bottomPanel);

            Content = root;
        }

        #region Categories

        private static readonly Dictionary<string, string[]> SubCategories = new Dictionary<string, string[]>
        {
            { "Celebrations", new[] { "UCC", "Trophy", "Knee", "Slide", "Dance", "Flip", "Heart", "Violin", "Drogba" } },
            { "Locomotion", new[] { "Sprint", "Run", "Jog", "Walk", "Strafe", "Backpedal", "Cycle" } },
            { "Dribble", new[] { "Shield", "Skill", "Turn", "Feint", "Hocus" } },
            { "Shooting", new[] { "Volley", "Sidefoot", "Instep", "Chip", "Bicycle", "Penalty" } },
            { "Goalkeeper", new[] { "Dive", "Save", "Kick", "Throw", "Catch", "1v1" } },
            { "Passing", new[] { "Short", "Long", "Through", "Lob", "Cross" } },
            { "Tackling", new[] { "Standing", "Sliding", "Block" } },
            { "Set Pieces", new[] { "Freekick", "Corner", "Throwin", "GoalKick" } },
        };

        private static string Categorize(string name)
        {
            string nl = name.ToLower();
            if (nl.Contains("ucc_") || nl.Contains("celeb") || nl.Contains("_violin") || nl.Contains("_dance")) return "Celebrations";
            if (nl.Contains("_gk_") || nl.Contains("goalkeeper") || nl.StartsWith("gk")) return "Goalkeeper";
            if (nl.Contains("dribble") || nl.Contains("shield")) return "Dribble";
            if (nl.Contains("shot_") || nl.Contains("_shot_") || nl.Contains("shoot") || nl.Contains("volley")) return "Shooting";
            if (nl.Contains("pass") || nl.Contains("cross")) return "Passing";
            if (nl.Contains("tackle") || (nl.Contains("slide") && !nl.Contains("celeb") && !nl.Contains("ucc"))) return "Tackling";
            if (nl.Contains("header") || nl.Contains("head_")) return "Headers";
            if (nl.Contains("sprint") || nl.Contains("run") || nl.Contains("jog") || nl.Contains("walk") || nl.Contains("loco")) return "Locomotion";
            if (nl.Contains("idle") || nl.Contains("stand")) return "Idle/Stand";
            if (nl.Contains("trap") || nl.Contains("receive") || nl.Contains("control")) return "Ball Control";
            if (nl.Contains("foul") || nl.Contains("injury") || nl.Contains("pain")) return "Fouls/Injury";
            if (nl.Contains("throw") || nl.Contains("corner") || nl.Contains("freekick") || nl.Contains("fk_")) return "Set Pieces";
            if (nl.Contains("_sc_") || nl.Contains("cutscene") || nl.Contains("nis") || nl.Contains("ngnis")) return "Cutscenes";
            if (nl.Contains("_ci_") || nl.Contains("crowd") || nl.Contains("sit_")) return "Crowd/Cinematic";
            if (nl.Contains("_ee_") || nl.Contains("react") || nl.Contains("emotion")) return "Emotions";
            if (nl.Contains("transition") || nl.Contains("turn")) return "Transitions";
            if (nl.Contains("sub_") || nl.Contains("manager") || nl.Contains("coach") || nl.Contains("tunnel")) return "Managers/Subs";
            return "Other";
        }

        #endregion

        private void PopulateTree()
        {
            _tree.Items.Clear();
            var allItem = new TreeViewItem { Header = "All Animations (" + _animsByName.Count + ")",
                Tag = "ALL", FontWeight = FontWeights.Bold };
            _tree.Items.Add(allItem);

            var catAnims = new SortedDictionary<string, List<string>>();
            foreach (var name in _animsByName.Keys)
            {
                string cat = Categorize(name);
                if (!catAnims.ContainsKey(cat)) catAnims[cat] = new List<string>();
                catAnims[cat].Add(name);
            }

            foreach (var kvp in catAnims.OrderByDescending(x => x.Value.Count))
            {
                var catItem = new TreeViewItem { Header = kvp.Key + " (" + kvp.Value.Count + ")", Tag = "CAT:" + kvp.Key };
                if (SubCategories.ContainsKey(kvp.Key))
                {
                    foreach (string sub in SubCategories[kvp.Key])
                    {
                        string sl = sub.ToLower();
                        int sc = kvp.Value.Count(n => n.ToLower().Contains(sl));
                        if (sc > 0)
                            catItem.Items.Add(new TreeViewItem { Header = sub + " (" + sc + ")", Tag = "SUB:" + kvp.Key + ":" + sl });
                    }
                }
                _tree.Items.Add(catItem);
            }
        }

        private void OnCategorySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!string.IsNullOrEmpty(_searchBox.Text)) return; // search overrides category
            var item = _tree.SelectedItem as TreeViewItem;
            if (item == null) return;
            string tag = item.Tag as string;
            if (tag == null) return;

            _resultsList.Items.Clear();
            List<string> names;
            if (tag == "ALL") names = _animsByName.Keys.OrderBy(x => x).ToList();
            else if (tag.StartsWith("CAT:"))
                names = _animsByName.Keys.Where(n => Categorize(n) == tag.Substring(4)).OrderBy(x => x).ToList();
            else if (tag.StartsWith("SUB:"))
            {
                var p = tag.Split(new[] { ':' }, 3);
                names = _animsByName.Keys.Where(n => Categorize(n) == p[1] && n.ToLower().Contains(p[2])).OrderBy(x => x).ToList();
            }
            else names = new List<string>();

            foreach (var n in names) _resultsList.Items.Add(n);
            UpdateStatus();
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            string q = _searchBox.Text.Trim().ToLower();
            _resultsList.Items.Clear();
            if (string.IsNullOrEmpty(q)) { OnCategorySelected(null, null); return; }

            string[] terms = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var matches = _animsByName.Keys
                .Where(n => { string nl = n.ToLower(); return terms.All(t => nl.Contains(t)); })
                .OrderBy(x => x).ToList();
            foreach (var n in matches) _resultsList.Items.Add(n);
            UpdateStatus();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStatus();
            if (_resultsList.SelectedItems.Count == 1)
            {
                string name = _resultsList.SelectedItem as string;
                if (name != null && _animsByName.ContainsKey(name)) ShowAnimInfo(name, _animsByName[name]);
            }
            else if (_resultsList.SelectedItems.Count > 1)
                _infoPanel.Text = _resultsList.SelectedItems.Count + " animations selected\n\nClick 'Export Selected' to export all.";
            else
                _infoPanel.Text = "Select an animation\nto see details.\n\nDouble-click to\nexport single anim.";
        }

        private void ShowAnimInfo(string name, AnimationAsset anim)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Name:");
                sb.AppendLine("  " + name);
                sb.AppendLine();
                sb.AppendLine("Type: " + anim.GetType().Name);
                sb.AppendLine("Category: " + Categorize(name));

                if (anim is VbrAnimationAsset vbr)
                {
                    var intern = vbr.ConvertToInternal();
                    if (intern != null)
                    {
                        float dur = intern.Frames.Count / 30f;
                        sb.AppendLine();
                        sb.AppendLine("Frames: " + intern.Frames.Count);
                        sb.AppendLine("Duration: " + dur.ToString("F1") + "s");
                        sb.AppendLine("FPS: 30");
                        sb.AppendLine("Additive: " + intern.Additive);
                        sb.AppendLine();
                        sb.AppendLine("Rot channels: " + intern.RotationChannels.Count);
                        sb.AppendLine("Pos channels: " + intern.PositionChannels.Count);

                        if (intern.RotationChannels.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine("Animated bones:");
                            foreach (var ch in intern.RotationChannels)
                                sb.AppendLine("  " + ch);
                        }
                        if (intern.PositionChannels.Count > 0)
                        {
                            sb.AppendLine();
                            sb.AppendLine("Position bones:");
                            foreach (var ch in intern.PositionChannels)
                                sb.AppendLine("  " + ch);
                        }
                    }
                    else sb.AppendLine("\n(decode failed)");
                }
                else
                {
                    sb.AppendLine("AnimId: " + anim.AnimId);
                    sb.AppendLine("EndFrame: " + anim.EndFrame);
                }
                _infoPanel.Text = sb.ToString();
            }
            catch (Exception ex) { _infoPanel.Text = "Error: " + ex.Message; }
        }

        private void OnDoubleClickExport(object sender, MouseButtonEventArgs e)
        {
            if (_resultsList.SelectedItem is string name) DoExport(new List<string> { name });
        }

        private void OnExportSelected(object sender, RoutedEventArgs e)
        {
            var sel = _resultsList.SelectedItems.Cast<string>().ToList();
            if (sel.Count == 0) { FrostyMessageBox.Show("Select animations first", "Export"); return; }
            DoExport(sel);
        }

        private void OnExportAll(object sender, RoutedEventArgs e)
        {
            var vis = _resultsList.Items.Cast<string>().ToList();
            if (vis.Count == 0) { FrostyMessageBox.Show("No animations visible", "Export"); return; }
            if (vis.Count > 50)
            {
                if (MessageBox.Show("Export " + vis.Count + " animations?\n\nOutput: " + _outDir,
                    "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            }
            DoExport(vis);
        }

        private void OnDumpNameList(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(_outDir, "animation_names.txt");
            var sb = new StringBuilder();
            sb.AppendLine("# FIFA 17 Animation Names (" + _animsByName.Count + " total)");
            sb.AppendLine("# Exported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            var cats = new SortedDictionary<string, List<string>>();
            foreach (var name in _animsByName.Keys.OrderBy(x => x))
            {
                string cat = Categorize(name);
                if (!cats.ContainsKey(cat)) cats[cat] = new List<string>();
                cats[cat].Add(name);
            }
            foreach (var kvp in cats.OrderByDescending(x => x.Value.Count))
            {
                sb.AppendLine("=== " + kvp.Key + " (" + kvp.Value.Count + ") ===");
                foreach (var n in kvp.Value) sb.AppendLine("  " + n);
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
            FrostyMessageBox.Show("Saved " + _animsByName.Count + " names to:\n" + path, "Name List");
        }


        private void DoExport(List<string> names)
        {
            var exporter = new AnimationExporterBVH();
            int exported = 0, errors = 0;
            _progressBar.Visibility = Visibility.Visible;
            _progressBar.Minimum = 0; _progressBar.Maximum = names.Count; _progressBar.Value = 0;
            _exportBtn.IsEnabled = false; _exportAllBtn.IsEnabled = false;
            bool useSubs = _subfolderCheck != null && _subfolderCheck.IsChecked == true;
            Log("EXPORT: " + names.Count + " to " + _outDir + (useSubs ? " (subfolders)" : ""));
            Log("  outDir exists: " + System.IO.Directory.Exists(_outDir) + " | outDir='" + _outDir + "'");

            foreach (var name in names)
            {
                AnimationAsset anim;
                if (!_animsByName.TryGetValue(name, out anim)) continue;
                try
                {
                    anim.Name = name;
                    try { anim.Channels = anim.GetChannels(anim.ChannelToDofAsset); } catch { anim.Channels = null; }
                    var intern = anim.ConvertToInternal();
                    if (intern != null && intern.Frames.Count > 0 && intern.RotationChannels.Count > 0)
                    {
                        string dir = _outDir;
                        if (useSubs)
                        {
                            string cat = Categorize(name).Replace("/", "-");
                            dir = Path.Combine(_outDir, cat);
                        }
                        if (_fbxCheck != null && _fbxCheck.IsChecked == true)
                        {
                            var fbxExporter = new AnimationExporterFBX();
                            fbxExporter.Export(intern, _skeleton, dir);
                        }
                        else
                        {
                            exporter.Export(intern, _skeleton, dir);
                        }
                        exported++;
                        Log("OK: " + name + " " + intern.Frames.Count + "f → " + dir);
                    }
                }
                catch (Exception ex) { errors++; Log("ERR: " + name + ": " + ex.Message); }

                _progressBar.Value = exported + errors;
                _statusBar.Text = "Exporting " + (exported + errors) + "/" + names.Count + "...";
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, new Action(delegate { }));
            }

            _progressBar.Visibility = Visibility.Collapsed;
            _exportBtn.IsEnabled = true; _exportAllBtn.IsEnabled = true;
            UpdateStatus();
            Log("DONE: " + exported + "/" + names.Count);
            FrostyMessageBox.Show("Exported " + exported + " BVH" + (errors > 0 ? " (" + errors + " err)" : "")
                + "\n\n" + _outDir, "Export");
        }

        private void UpdateStatus()
        {
            int sel = _resultsList.SelectedItems.Count;
            _statusBar.Text = _resultsList.Items.Count + " animations" + (sel > 0 ? " (" + sel + " selected)" : "")
                + " | Output: " + _outDir;
        }
    }
}
