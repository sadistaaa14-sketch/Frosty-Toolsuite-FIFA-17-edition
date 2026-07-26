using FrostySdk.Managers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Frosty.Core.Bookmarks;
using System.Linq;
using Frosty.Hash;
using Frosty.Core.Commands;
using System.Windows.Data;

namespace Frosty.Core.Controls
{
    /// <summary>
    /// Visual-tree utility helpers used by <see cref="FrostyDataExplorer"/>
    /// for diagnostic logging and virtualization wiring. All diagnostic
    /// output goes through <see cref="Frosty.Core.App.Logger"/> so it
    /// appears in the editor's built-in log panel.
    /// </summary>
    internal static class PerfDiag
    {
        /// <summary>
        /// Walk the visual tree below <paramref name="root"/> and return the
        /// first descendant <see cref="Panel"/> whose <see cref="Panel.IsItemsHost"/>
        /// is <c>true</c>. This is the ONLY reliable way to find the panel
        /// WPF actually instantiated to host items (e.g. VirtualizingStackPanel
        /// or a plain StackPanel) — the <see cref="ItemsControl.ItemsPanel"/>
        /// template doesn't expose the instantiated panel publicly, and the
        /// first <see cref="Panel"/> in the visual tree (e.g. the GridView
        /// header Grid) is typically NOT the items host.
        /// </summary>
        public static Panel FindItemsHostPanel(Visual root)
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Panel p && p.IsItemsHost)
                    return p;
                if (child is Visual v)
                {
                    var inner = FindItemsHostPanel(v);
                    if (inner != null) return inner;
                }
            }
            return null;
        }

        /// <summary>
        /// Walk the visual tree from <paramref name="leaf"/> up to (but not
        /// including) <paramref name="root"/>, returning the chain of
        /// ancestors. The returned list is ordered from <paramref name="leaf"/>
        /// (index 0) up to the immediate child of <paramref name="root"/>
        /// (last index). Returns an empty list if <paramref name="leaf"/> is
        /// not a descendant of <paramref name="root"/>.
        /// </summary>
        public static System.Collections.Generic.List<DependencyObject>
            GetVisualPath(DependencyObject root, DependencyObject leaf)
        {
            var path = new System.Collections.Generic.List<DependencyObject>();
            var node = leaf;
            while (node != null && node != root)
            {
                path.Add(node);
                node = VisualTreeHelper.GetParent(node);
            }
            // Path is currently leaf→root; reverse to root→leaf for readable
            // top-down dump in the log.
            path.Reverse();
            return path;
        }
    }

    public class PlainView : ViewBase
    {

        public static readonly DependencyProperty ItemContainerStyleProperty = ItemsControl.ItemContainerStyleProperty.AddOwner(typeof(PlainView));
        public Style ItemContainerStyle
        {
            get => (Style)GetValue(ItemContainerStyleProperty);
            set => SetValue(ItemContainerStyleProperty, value);
        }

        public static readonly DependencyProperty ItemTemplateProperty = ItemsControl.ItemTemplateProperty.AddOwner(typeof(PlainView));
        public DataTemplate ItemTemplate
        {
            get => (DataTemplate)GetValue(ItemTemplateProperty);
            set => SetValue(ItemTemplateProperty, value);
        }

        public static readonly DependencyProperty ItemWidthProperty = WrapPanel.ItemWidthProperty.AddOwner(typeof(PlainView));
        public double ItemWidth
        {
            get => (double)GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }


        public static readonly DependencyProperty ItemHeightProperty = WrapPanel.ItemHeightProperty.AddOwner(typeof(PlainView));
        public double ItemHeight
        {
            get => (double)GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }


        //protected override object DefaultStyleKey
        //{
        //    get
        //    {
        //        return new ComponentResourceKey(typeof(PlainView), "PlainViewDefaultStyle");
        //    }
        //}
    }

    internal class AssetPath
    {
        private static readonly ImageSource ClosedImage = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/CloseFolder.png") as ImageSource;
        private static readonly ImageSource OpenImage = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/OpenFolder.png") as ImageSource;

        public string DisplayName => PathName.Trim('!');
        public string PathName { get; private set; }
        public string FullPath { get; }
        public AssetPath Parent { get; }
        public List<AssetPath> Children { get; } = new List<AssetPath>();
        public bool IsSelected { get; set; }
        public bool IsRoot { get; }

        public bool IsExpanded 
        { 
            get => expanded && Children.Count != 0;
            set => expanded = value;
        }
        private bool expanded;

        public AssetPath(string inName, string path, AssetPath inParent, bool bInRoot = false)
        {
            PathName = inName;
            FullPath = path;
            IsRoot = bInRoot;
            Parent = inParent;
        }

        public void UpdatePathName(string newName)
        {
            PathName = newName;
        }
    }

    public class AssetDoubleClickedEventArgs : RoutedEventArgs
    {
        public AssetEntry SelectedAsset { get; private set; }

        public AssetDoubleClickedEventArgs(AssetEntry selectedAsset)
        {
            SelectedAsset = selectedAsset;
        }
    }

    [TemplatePart(Name = PART_ShowOnlyModifiedCheckBox, Type = typeof(CheckBox))]
    [TemplatePart(Name = PART_FilterTextBox, Type = typeof(TextBox))]
    [TemplatePart(Name = PART_AssetTreeView, Type = typeof(TreeView))]
    [TemplatePart(Name = PART_AssetListView, Type = typeof(ListView))]
    public class FrostyDataExplorer : Control
    {
        public string FilteredText { get => filterTextBox.Text; }

        private const string PART_ShowOnlyModifiedCheckBox = "PART_ShowOnlyModifiedCheckBox";
        private const string PART_FilterTextBox = "PART_FilterTextBox";
        private const string PART_AssetTreeView = "PART_AssetTreeView";
        private const string PART_AssetListView = "PART_AssetListView";

        private enum FilterCommandType
        {
            Contains,
            StartsWith,
            EndsWith,
            RegEx,
            Type,
            Id,
            Hash
        }

        private enum FilterCombineType
        {
            Or,
            And,
        }

        private struct FilterData
        {
            public string Text;
            public FilterCommandType Command;
            public FilterCombineType Combine;
            public bool Not;
        }

        #region -- Properties --

        #region -- ItemsSource --
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register("ItemsSource", typeof(IEnumerable), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(null, OnItemsSourceChanged));
        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }
        private static void OnItemsSourceChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            FrostyDataExplorer ctrl = o as FrostyDataExplorer;
            ctrl.assetPathMapping.Clear();
            ctrl.SelectedAsset = null;
            // Force cachedItems to be rebuilt from the new ItemsSource on
            // the next access. We do not materialize here because some
            // callers set ItemsSource to null temporarily and we don't
            // want to pay the enumeration cost for an empty source.
            ctrl.cachedItems = null;
            ctrl.cachedItemsDirty = true;
            ctrl.UpdateTreeView();
        }
        #endregion

        #region -- ShowOnlyModified --
        public static readonly DependencyProperty ShowOnlyModifiedProperty = DependencyProperty.Register("ShowOnlyModified", typeof(bool), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(false, OnShowOnlyModifiedChanged));
        public bool ShowOnlyModified
        {
            get => (bool)GetValue(ShowOnlyModifiedProperty);
            set => SetValue(ShowOnlyModifiedProperty, value);
        }
        private static void OnShowOnlyModifiedChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            FrostyDataExplorer ctrl = o as FrostyDataExplorer;
            ctrl.UpdateTreeView();
        }
        #endregion

        #region -- MultiSelect --
        public static readonly DependencyProperty MultiSelectProperty = DependencyProperty.Register("MultiSelect", typeof(bool), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(null));
        public bool MultiSelect
        {
            get => (bool)GetValue(MultiSelectProperty);
            set => SetValue(MultiSelectProperty, value);
        }
        #endregion

        #region -- SelectedAsset --
        public static readonly DependencyProperty SelectedAssetProperty = DependencyProperty.Register("SelectedAsset", typeof(AssetEntry), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(null));
        public AssetEntry SelectedAsset
        {
            get => (AssetEntry)GetValue(SelectedAssetProperty);
            set => SetValue(SelectedAssetProperty, value);
        }
        #endregion

        #region -- SelectedAssets --
        public static readonly DependencyProperty SelectedAssetsProperty = DependencyProperty.Register("SelectedAssets", typeof(IList<AssetEntry>), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(null));
        public IList<AssetEntry> SelectedAssets
        {
            get => (IList<AssetEntry>)GetValue(SelectedAssetsProperty);
            set => SetValue(SelectedAssetsProperty, value);
        }
        #endregion

        #region -- AssetContextMenu --
        public static readonly DependencyProperty AssetContextMenuProperty = DependencyProperty.Register("AssetContextMenu", typeof(ContextMenu), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(null));
        public ContextMenu AssetContextMenu
        {
            get => (ContextMenu)GetValue(AssetContextMenuProperty);
            set => SetValue(AssetContextMenuProperty, value);
        }
        #endregion

        #region -- ToolbarVisible --
        public static readonly DependencyProperty ToolbarVisibleProperty = DependencyProperty.Register("ToolbarVisible", typeof(bool), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(true));
        public bool ToolbarVisible
        {
            get => (bool)GetValue(ToolbarVisibleProperty);
            set => SetValue(ToolbarVisibleProperty, value);
        }
        #endregion

        #region -- AssetListVisible --
        public static readonly DependencyProperty AssetListVisibleProperty = DependencyProperty.Register("AssetListVisible", typeof(bool), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(true));
        public bool AssetListVisible
        {
            get => (bool)GetValue(AssetListVisibleProperty);
            set => SetValue(AssetListVisibleProperty, value);
        }
        #endregion

        #region -- BookmarkContext --
        public static readonly DependencyProperty BookmarkContextProperty = DependencyProperty.Register("BookmarkContext", typeof(string), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata("", OnBookmarkContextChanged));
        public string BookmarkContext
        {
            get => (string)GetValue(BookmarkContextProperty);
            set => SetValue(BookmarkContextProperty, value);
        }
        private static void OnBookmarkContextChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            FrostyDataExplorer ctrl = o as FrostyDataExplorer;
            ctrl.bookmarkContext = BookmarkDb.GetContext(e.NewValue as string);
        }
        #endregion

        #region -- InitialHeight --
        public static readonly DependencyProperty InitialHeightProperty = DependencyProperty.Register("InitialHeight", typeof(GridLength), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(new GridLength(1, GridUnitType.Star)));
        public GridLength InitialHeight
        {
            get => (GridLength)GetValue(InitialHeightProperty);
            set => SetValue(InitialHeightProperty, value);
        }
        #endregion

        #region -- SelectedPath --
        public static readonly DependencyProperty SelectedPathProperty = DependencyProperty.Register("SelectedPath", typeof(string), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(""));
        public string SelectedPath
        {
            get => (string)GetValue(SelectedPathProperty);
            set => SetValue(SelectedPathProperty, value);
        }
        #endregion

        #region -- TileTemplate --
        public static readonly DependencyProperty TileTemplateProperty = DependencyProperty.Register("TileTemplate", typeof(DataTemplate), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(null));
        public DataTemplate TileTemplate
        {
            get => (DataTemplate)GetValue(TileTemplateProperty);
            set => SetValue(TileTemplateProperty, value);
        }
        #endregion

        #region -- TileZoom --
        public static readonly DependencyProperty TileZoomProperty = DependencyProperty.Register("TileZoom", typeof(double), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(50.0));
        public double TileZoom
        {
            get => (double)GetValue(TileZoomProperty);
            set => SetValue(TileZoomProperty, value);
        }
        #endregion

        #region -- GridView --
        public static readonly DependencyProperty GridViewProperty = DependencyProperty.Register("GridView", typeof(bool), typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(true, OnGridViewChanged));
        public bool GridView
        {
            get => (bool)GetValue(GridViewProperty);
            set => SetValue(GridViewProperty, value);
        }
        private static void OnGridViewChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            FrostyDataExplorer ctrl = o as FrostyDataExplorer;
            ctrl.UpdateViewType();
        }
        #endregion

        #endregion

        private CheckBox showOnlyModifiedCheckBox;
        private TextBox filterTextBox;
        private TreeView assetTreeView;
        private ListView assetListView;

        private GridViewColumnHeader lastSortHeader;
        private ListSortDirection lastSortDirection;
        // The currently-active custom comparer (kept as a field so we can
        // re-apply it after ItemsSource changes; WPF's ItemCollection.CustomSort
        // is preserved across ItemsSource assignments, but the field lets us
        // consult it for pre-sorting the items list in UpdateListView).
        private AssetEntryComparer activeComparer;

        private Dictionary<string, AssetPath> assetPathMapping = new Dictionary<string, AssetPath>(StringComparer.OrdinalIgnoreCase);

        // Materialized snapshot of the (possibly lazy) ItemsSource. The
        // external ItemsSource is typically a yield-based IEnumerable
        // (App.AssetManager.EnumerateCustomAssets returns one), which means
        // every `foreach (entry in ItemsSource)` re-walks the entire
        // underlying collection. For FIFA 17 with ~100k+ legacy entries,
        // each folder click was re-enumerating the whole list just to
        // filter down to one folder. Caching the materialized list here
        // turns each folder click into a List iteration (no per-item
        // yield overhead, no dictionary walk overhead).
        //
        // Invalidated in OnItemsSourceChanged and rebuilt on first access.
        private List<AssetEntry> cachedItems;
        private bool cachedItemsDirty = true;

        private AssetPath selectedPath;
        public event EventHandler<RoutedEventArgs> SelectedAssetDoubleClick;
        public event EventHandler<RoutedEventArgs> SelectionChanged;

        private List<FilterData> filter = new List<FilterData>();
        private string prevFilterText = "";

        private BookmarkContext bookmarkContext;

        public static readonly DependencyProperty OnDoubleClickedCommandProperty = DependencyProperty.Register("OnDoubleClickedCommand", typeof(ICommand), typeof(FrostyDataExplorer), new UIPropertyMetadata(null));
        public ICommand OnDoubleClickedCommand
        {
            get
            {
                return (ICommand)GetValue(OnDoubleClickedCommandProperty);
            }
            set
            {
                SetValue(OnDoubleClickedCommandProperty, value);
            }
        }

        public ItemDoubleClickCommand DoubleClickCommand { get; private set; }

        public ICommand FindOpenedAssetCommand => new RelayCommand(FindOpenedAsset);

        private GridView detailView;
        private PlainView tileView;

        static FrostyDataExplorer()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(FrostyDataExplorer), new FrameworkPropertyMetadata(typeof(FrostyDataExplorer)));
        }

        public FrostyDataExplorer()
        {
            DoubleClickCommand = new ItemDoubleClickCommand(DoubleClickSelectedAsset);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            showOnlyModifiedCheckBox = GetTemplateChild(PART_ShowOnlyModifiedCheckBox) as CheckBox;
            filterTextBox = GetTemplateChild(PART_FilterTextBox) as TextBox;
            assetTreeView = GetTemplateChild(PART_AssetTreeView) as TreeView;
            assetListView = GetTemplateChild(PART_AssetListView) as ListView;

            assetTreeView.SelectedItemChanged += assetTreeView_SelectedItemChanged;
            filterTextBox.KeyUp += FilterTextBox_KeyUp;
            filterTextBox.LostFocus += FilterTextBox_LostFocus;
            assetListView.SelectionChanged += assetListView_SelectionChanged;
            assetListView.PreviewMouseWheel += AssetList_MouseWheel;
            IsVisibleChanged += FrostyDataExplorer_IsVisibleChanged;

            detailView = new GridView();
            tileView = new PlainView {ItemTemplate = TileTemplate};
            UpdateViewType();

            detailView.Columns.Add(new GridViewColumn() { Header = new GridViewColumnHeader() { Content = "Name", Tag = "DisplayName" }, CellTemplate = FindResource("DisplayNameCellTemplate") as DataTemplate });
            detailView.Columns.Add(new GridViewColumn() { Header = new GridViewColumnHeader() { Content = "Type", Tag = "Type" }, CellTemplate = FindResource("TypeCellTemplate") as DataTemplate });

            Binding b = new Binding("TileZoom") {Source = this};

            BindingOperations.SetBinding(tileView, PlainView.ItemWidthProperty, b);
            BindingOperations.SetBinding(tileView, PlainView.ItemHeightProperty, b);

            int i = 0;
            foreach (GridViewColumn column in (assetListView.View as GridView).Columns)
            {
                b = new Binding("ActualWidth") { ElementName = "gridHelper" + (i + 1) };
                BindingOperations.SetBinding(column, GridViewColumn.WidthProperty, b);

                GridViewColumnHeader header = column.Header as GridViewColumnHeader;
                header.Click += assetListViewColumn_Click;

                if (i == 0)
                {
                    column.HeaderTemplate = FindResource("assetListViewAscendingSorting") as DataTemplate;
                    lastSortHeader = header;
                    lastSortDirection = ListSortDirection.Ascending;
                    i++;
                }
            }

            // default listView sort to name. Use CustomSort (a typed IComparer)
            // instead of SortDescriptions because SortDescriptions uses
            // PropertyDescriptor (reflection) for every comparison — for a
            // folder with 20,000+ legacy entries this means ~280K comparisons
            // × 2 reflection calls = 560K reflection calls per folder click,
            // which freezes the UI for several seconds. CustomSort calls the
            // typed Compare method directly with no reflection.
            //
            // ItemCollection itself doesn't expose CustomSort — we need to
            // grab the underlying ListCollectionView (which is what WPF
            // actually wraps around the ItemsSource list) and set CustomSort
            // on it.
            activeComparer = new AssetEntryComparer("DisplayName", ListSortDirection.Ascending);
            ApplyCustomSort(assetListView);
            if (MultiSelect)
                assetListView.SelectionMode = SelectionMode.Extended;

            UpdateTreeView();

            if (IsVisible && bookmarkContext != null)
                BookmarkDb.CurrentContext = bookmarkContext;

            // ----------------------------------------------------------------
            // VIRTUALIZATION HARD-FIX (defensive, runs after the entire
            // visual tree is built & styled):
            //
            // The XAML-only fix (CanContentScroll=True Setter on the
            // GridViewScrollViewerStyleKey Style) is NOT being reliably
            // propagated from the ScrollViewer down to its inner
            // ScrollContentPresenter. The diagnostic log confirms:
            //   ScrollViewer.CanContentScroll      = True  (Setter applied)
            //   ScrollContentPresenter.CanContentScroll = False (NOT propagated)
            //   itemsHost.ScrollOwner              = NULL  (HookupScrolling skipped)
            //
            // Without the SCP's CanContentScroll=True, the SCP's HookupScrolling()
            // method takes the pixel-scroll branch and NEVER assigns
            // panel.ScrollOwner = scp — leaving the panel unable to ask
            // anyone for the viewport size, so it realizes ALL containers
            // (20,469 rows / 54-second freeze on the Heads folder).
            //
            // This code-behind fix BYPASSES WPF's broken propagation:
            //   1. Walk the visual tree to find the inner ScrollContentPresenter.
            //   2. Set its CanContentScroll DP to True directly (the CLR
            //      setter is internal — we use SetValue with the public DP
            //      field, which is the same DP the CLR setter uses).
            //   3. If the panel's ScrollOwner is still NULL, force-wire it
            //      to the SCP via reflection (ScrollOwner is a protected
            //      member on Panel, accessible only via IScrollInfo).
            //
            // We schedule this at Loaded priority (not Background) because
            // it must run BEFORE the first layout pass — otherwise the
            // panel will measure with infinite height and realize all
            // containers anyway.
            // ----------------------------------------------------------------
            assetListView.Dispatcher.BeginInvoke(new Action(() =>
            {
                ForceVirtualizationHookup();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            // ----------------------------------------------------------------
            // VIRTUALIZATION HARD-FIX PART 2: LayoutUpdated watchdog.
            //
            // The ForceVirtualizationHookup call above runs ONCE at Loaded
            // priority. But the diagnostic log proves that the
            // ScrollContentPresenter is RECREATED later (e.g. when
            // assetListView.ItemsSource is assigned in UpdateListView, or
            // when WPF reapplies the ScrollViewer's template). Each new SCP
            // instance starts with the DEFAULT CanContentScroll=False, and
            // since CanContentScroll is NOT inherited (the SCP's AddOwner
            // metadata drops the Inherits flag), the new SCP never picks up
            // the True value we set on the previous SCP or on the ScrollViewer.
            //
            // Result: by the time the natural layout pass runs, the SCP is
            // back to CanContentScroll=False, measures the panel with
            // infinite height, and the panel realizes all 20,469 containers
            // (the 61-second freeze).
            //
            // Fix: subscribe to LayoutUpdated. This fires after every
            // layout pass, so we catch any new SCP the moment it appears
            // in the visual tree. If the new SCP has CanContentScroll=False,
            // we re-apply the fix and force ANOTHER layout pass — the second
            // pass measures the panel with the correct (finite) viewport
            // size, and VSP cleans up the non-visible containers.
            //
            // The _isFixingScp flag prevents recursion (UpdateLayout inside
            // ForceVirtualizationHookup would otherwise fire LayoutUpdated
            // again).
            // ----------------------------------------------------------------
            assetListView.LayoutUpdated -= OnLayoutUpdatedCheckScp;
            assetListView.LayoutUpdated += OnLayoutUpdatedCheckScp;
        }

        private bool _isFixingScp = false;

        /// <summary>
        /// LayoutUpdated watchdog — fires after every layout pass. Checks
        /// whether the current ScrollContentPresenter has CanContentScroll=True
        /// (which we need for item-based scrolling and virtualization). If not,
        /// re-applies ForceVirtualizationHookup to catch SCP instances that
        /// were recreated since the last fix.
        /// </summary>
        private void OnLayoutUpdatedCheckScp(object sender, EventArgs e)
        {
            if (_isFixingScp) return;
            if (assetListView == null) return;

            var scp = FindScrollContentPresenter(assetListView);
            if (scp == null) return;

            bool canScroll = (bool)scp.GetValue(ScrollViewer.CanContentScrollProperty);
            if (!canScroll)
            {
                _isFixingScp = true;
                try
                {
                    App.Logger.Log("LayoutUpdated-watchdog: SCP.CanContentScroll is False — re-applying fix (scp.Hash=" + scp.GetHashCode() + ")");
                    ForceVirtualizationHookup();
                }
                finally
                {
                    _isFixingScp = false;
                }
            }
        }

        /// <summary>
        /// Walk the visual tree below <paramref name="assetListView"/> and
        /// find the ScrollContentPresenter that lives inside the ListView's
        /// ScrollViewer. Returns null if the visual tree hasn't been built
        /// yet or no ScrollContentPresenter exists.
        /// </summary>
        private static ScrollContentPresenter FindScrollContentPresenter(DependencyObject root)
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollContentPresenter scp) return scp;
                var inner = FindScrollContentPresenter(child);
                if (inner != null) return inner;
            }
            return null;
        }

        /// <summary>
        /// Force-wire virtualization by bypassing WPF's broken
        /// ScrollViewer→ScrollContentPresenter CanContentScroll propagation.
        ///
        /// After calling this:
        ///   - The SCP's CanContentScroll is True (set via SetValue on the
        ///     public ScrollViewer.CanContentScrollProperty DP).
        ///   - The items host panel's IScrollInfo.ScrollOwner points at the
        ///     OUTER ScrollViewer (not the SCP itself — IScrollInfo.ScrollOwner
        ///     is typed as ScrollViewer, and WPF's own HookUpScrolling()
        ///     assigns si.ScrollOwner = _scroller, where _scroller is the
        ///     ScrollViewer template parent of the SCP).
        /// This lets the VirtualizingStackPanel ask the ScrollViewer for
        /// the viewport size during Measure and only realize the visible
        /// containers.
        /// </summary>
        private void ForceVirtualizationHookup()
        {
            if (assetListView == null) return;

            // Find the SCP inside the ListView's template.
            var scp = FindScrollContentPresenter(assetListView);
            if (scp == null)
            {
                App.Logger.Log("ForceVirtualizationHookup: ScrollContentPresenter NOT FOUND — visual tree may not be built yet");
                return;
            }

            // Find the inner ScrollViewer that owns the SCP. The panel's
            // IScrollInfo.ScrollOwner is typed as ScrollViewer (NOT
            // ScrollContentPresenter), so we need this reference when
            // force-wiring the panel's ScrollOwner below.
            var scrollViewer = FindVisualChild<ScrollViewer>(assetListView);
            if (scrollViewer == null)
            {
                App.Logger.Log("ForceVirtualizationHookup: inner ScrollViewer NOT FOUND — visual tree may not be built yet");
                return;
            }

            // Log the SCP and ScrollViewer hash codes so we can detect when
            // WPF recreates these elements (each recreation produces a new
            // instance with a new hash code). This is the key diagnostic for
            // proving that the SCP's CanContentScroll reset is caused by
            // element recreation, not by our SetValue being undone.
            App.Logger.Log("ForceVirtualizationHookup: enter scp.Hash=" + scp.GetHashCode()
                + " scrollViewer.Hash=" + scrollViewer.GetHashCode()
                + " scp.CanContentScroll=" + (bool)scp.GetValue(ScrollViewer.CanContentScrollProperty)
                + " sv.CanContentScroll=" + scrollViewer.CanContentScroll);

            bool changed = false;

            // (1) Set the SCP's CanContentScroll DP to True. The CLR setter
            //     is internal, but the underlying DP is the public
            //     ScrollViewer.CanContentScrollProperty — which we set
            //     via SetValue. This sets a LOCAL value (highest precedence
            //     except for animations), so it overrides anything WPF's
            //     propagation might (or might not) have done.
            //
            //     CRITICAL: We TOGGLE False→True instead of just setting True.
            //     If the current value is already True (e.g. from a previous
            //     ForceVirtualizationHookup call), SetValue(True) would be a
            //     no-op and the PropertyChanged callback (which calls
            //     HookUpScrolling) would NOT fire. By explicitly setting False
            //     first, we guarantee the callback fires when we set True.
            bool currentScpCanScroll = (bool)scp.GetValue(ScrollViewer.CanContentScrollProperty);
            // Always force a False→True transition so the PropertyChanged
            // callback fires and re-runs HookUpScrolling() with the now-
            // available IScrollInfo (the VSP, which is in the visual tree by
            // the time this method runs).
            scp.SetValue(ScrollViewer.CanContentScrollProperty, false);
            scp.SetValue(ScrollViewer.CanContentScrollProperty, true);
            bool verifyScpCanScroll = (bool)scp.GetValue(ScrollViewer.CanContentScrollProperty);
            changed = true;
            App.Logger.Log("ForceVirtualizationHookup: toggled ScrollContentPresenter.CanContentScroll False→True (was " + currentScpCanScroll + "), verify=" + verifyScpCanScroll);

            // (1a) REFLECTION KICKER: ScrollContentPresenter.HookUpScrolling()
            //      is the private method that wires up the IScrollInfo path:
            //      it walks _content (the ItemsPresenter) → finds the VSP →
            //      assigns _scrollData._scrollInfo = isi and
            //      isi.ScrollOwner = _scroller. The CanContentScroll
            //      PropertyChanged callback calls this, BUT if _content was
            //      null or didn't contain the VSP at the time of the callback
            //      (e.g. the template was just applied and the ItemsPresenter
            //      hasn't generated the VSP yet), HookUpScrolling silently
            //      fails and _scrollData._scrollInfo stays null. The SCP
            //      then falls back to pixel-scroll mode (measures the panel
            //      with INFINITE height → VSP realizes all 20K containers →
            //      60-second freeze).
            //
            //      We force-call HookUpScrolling via reflection NOW — at this
            //      point the visual tree is fully built (we're called from
            //      LayoutUpdated, or synchronously after ItemsSource assignment
            //      in UpdateListView, or from the Loaded callback), so _content
            //      definitely contains the VSP. This is the missing piece that
            //      makes virtualization actually kick in.
            try
            {
                var hookUpMethod = typeof(ScrollContentPresenter).GetMethod(
                    "HookUpScrolling",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (hookUpMethod != null)
                {
                    hookUpMethod.Invoke(scp, null);
                    App.Logger.Log("ForceVirtualizationHookup: invoked ScrollContentPresenter.HookUpScrolling() via reflection");
                }
                else
                {
                    App.Logger.Log("ForceVirtualizationHookup: HookUpScrolling method NOT FOUND via reflection (WPF version mismatch?)");
                }
            }
            catch (Exception ex)
            {
                App.Logger.Log("ForceVirtualizationHookup: HookUpScrolling reflection invoke FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }

            // (1b) Reflect into the SCP's _scrollData._scrollInfo field to
            //      verify HookUpScrolling actually found the VSP. If this is
            //      null, the SCP will still measure with infinite height
            //      despite CanContentScroll=True, and we need a different fix.
            try
            {
                var scpType = typeof(ScrollContentPresenter);
                var scrollDataField = scpType.GetField("_scrollData",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object scrollData = scrollDataField?.GetValue(scp);
                if (scrollData != null)
                {
                    var scrollInfoField = scrollData.GetType().GetField("_scrollInfo",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    object scrollInfo = scrollInfoField?.GetValue(scrollData);
                    App.Logger.Log("ForceVirtualizationHookup: SCP._scrollData._scrollInfo = " + (scrollInfo == null ? "NULL (BAD — SCP will measure with infinite height)" : scrollInfo.GetType().Name));
                }
                else
                {
                    App.Logger.Log("ForceVirtualizationHookup: SCP._scrollData = NULL (BAD — HookUpScrolling didn't create the ScrollData)");
                }
            }
            catch (Exception ex)
            {
                App.Logger.Log("ForceVirtualizationHookup: _scrollData._scrollInfo reflection read FAILED: " + ex.GetType().Name);
            }

            // (1b) Also ensure the OUTER ScrollViewer's CanContentScroll is
            //      True. The SCP reads this on its template parent during
            //      OnApplyTemplate via this._scroller.CanContentScroll, and
            //      uses it to decide whether to call HookUpScrolling().
            //      Setting it as a local value on the ScrollViewer forces
            //      the propagation that the XAML Setter failed to do.
            bool currentSvCanScroll = scrollViewer.CanContentScroll;
            if (!currentSvCanScroll)
            {
                scrollViewer.CanContentScroll = true;
                changed = true;
                App.Logger.Log("ForceVirtualizationHookup: set ScrollViewer.CanContentScroll True (was False)");
            }

            // (2) Verify the SCP also has CanHorizontallyScroll / CanVerticallyScroll
            //     set to True. These DPs are public settable, but let's
            //     be defensive and force them in case a future template
            //     override resets them.
            if (!scp.CanHorizontallyScroll)
            {
                scp.CanHorizontallyScroll = true;
                changed = true;
                App.Logger.Log("ForceVirtualizationHookup: set ScrollContentPresenter.CanHorizontallyScroll True");
            }
            if (!scp.CanVerticallyScroll)
            {
                scp.CanVerticallyScroll = true;
                changed = true;
                App.Logger.Log("ForceVirtualizationHookup: set ScrollContentPresenter.CanVerticallyScroll True");
            }

            // (3) Find the actual items host panel and check whether its
            //     ScrollOwner is correctly wired to the ScrollViewer. If
            //     it's NULL, force-wire it via the IScrollInfo interface
            //     (Panel.ScrollOwner is protected, accessible only via the
            //     IScrollInfo interface that Panel implements).
            //
            //     NOTE: IScrollInfo.ScrollOwner is typed as ScrollViewer,
            //     NOT ScrollContentPresenter — we must assign the OUTER
            //     ScrollViewer. This mirrors what WPF's own
            //     ScrollContentPresenter.HookUpScrolling() does internally:
            //         if (isi.ScrollOwner != _scroller)
            //             isi.ScrollOwner = _scroller;  // _scroller is ScrollViewer
            var panel = PerfDiag.FindItemsHostPanel(assetListView);
            if (panel is IScrollInfo si)
            {
                var scrollOwner = si.ScrollOwner;
                if (scrollOwner == null)
                {
                    // Force-wire the panel's ScrollOwner to the ScrollViewer.
                    // This is the missing step that HookupScrolling() would
                    // have done if CanContentScroll had been True at
                    // OnApplyTemplate time.
                    si.ScrollOwner = scrollViewer;
                    changed = true;
                    App.Logger.Log("ForceVirtualizationHookup: FORCE-WIRED panel.ScrollOwner = ScrollViewer (was NULL)");
                }
                else if (!ReferenceEquals(scrollOwner, scrollViewer))
                {
                    // ScrollOwner is set to something other than the inner
                    // ScrollViewer. That's still wrong — re-wire it.
                    si.ScrollOwner = scrollViewer;
                    changed = true;
                    App.Logger.Log("ForceVirtualizationHookup: RE-WIRED panel.ScrollOwner = ScrollViewer (was " + scrollOwner.GetType().Name + ")");
                }
            }

            if (!changed)
            {
                App.Logger.Log("ForceVirtualizationHookup: nothing to fix (CanContentScroll=True and ScrollOwner already wired)");
            }
            else
            {
                // If we changed anything, force a layout pass so the panel
                // re-measures with the now-correct ScrollOwner. Use
                // UpdateLayout() — at this point we're at Loaded priority
                // (after the natural layout pass), so this is safe and
                // will not cause a recursive layout. The panel will see
                // a non-NULL ScrollOwner, query it for the viewport size,
                // and only realize the visible containers.
                try
                {
                    assetListView.UpdateLayout();
                    App.Logger.Log("ForceVirtualizationHookup: forced re-layout via UpdateLayout()");
                }
                catch (Exception ex)
                {
                    App.Logger.Log("ForceVirtualizationHookup: UpdateLayout threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public void FindOpenedAsset(object obj)
        {
            SelectAsset(App.EditorWindow.GetOpenedAssetEntry());
        }

        private void UpdateViewType()
        {
            if (GridView)
            {
                assetListView.ItemContainerStyle = FindResource("DetailViewItemContainerStyle") as Style;
                assetListView.View = detailView;
                assetListView.Style = FindResource(new ComponentResourceKey(typeof(FrostyPropertyGrid), "DetailViewDefaultStyle")) as Style;
            }
            else
            {
                assetListView.ItemContainerStyle = FindResource("TileViewItemContainerStyle") as Style;
                assetListView.View = tileView;
                assetListView.Style = FindResource(new ComponentResourceKey(typeof(FrostyPropertyGrid), "TileViewDefaultStyle")) as Style;
            }
        }

        private void FrostyDataExplorer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible && bookmarkContext != null)
                BookmarkDb.CurrentContext = bookmarkContext;
        }

        public void SelectAsset(AssetEntry entry)
        {
            if (assetTreeView == null)
            {
                SelectedAsset = entry;
                return;
            }

            if (entry == null)
            {
                SelectedAsset = null;
                assetListView.SelectedItem = null;
                return;
            }

            SetValue(ShowOnlyModifiedProperty, false);
            ClearFilter();

            AssetPath selectedPath = assetPathMapping["/" + entry.Path];
            if (selectedPath.FullPath != "")
            {
                string[] tmp = selectedPath.FullPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string totalPath = "";
                TreeViewItem tvi = null;

                foreach (string tmpStr in tmp)
                {
                    totalPath += "/" + tmpStr;
                    AssetPath path = assetPathMapping[totalPath];

                    if (tvi != null)
                    {
                        tvi.IsExpanded = true;
                        tvi.IsSelected = true;
                        tvi.Items.Refresh();
                        tvi.UpdateLayout();
                        tvi.BringIntoView();
                    }

                    tvi = (tvi == null)
                        ? (TreeViewItem)assetTreeView.ItemContainerGenerator.ContainerFromItem(path)
                        : (TreeViewItem)tvi.ItemContainerGenerator.ContainerFromItem(path);
                }
                if (tvi != null)
                {
                    tvi.BringIntoView();
                    tvi.IsSelected = true;
                }
            }
            else
            {
                TreeViewItem tvi = assetTreeView.ItemContainerGenerator.ContainerFromItem(selectedPath) as TreeViewItem;
                if(tvi != null)
                {
                    tvi.BringIntoView();
                    tvi.IsSelected = true;
                }
            }

            selectedPath.IsSelected = true;
            assetListView.SelectedItem = entry;
            assetListView.ScrollIntoView(entry);
            SelectedAsset = entry;
        }

        public void DoubleClickSelectedAsset()
        {
            if (SelectedAsset == null)
                return;

            OnDoubleClickedCommand?.Execute(new AssetDoubleClickedEventArgs(SelectedAsset));
            SelectedAssetDoubleClick?.Invoke(this, new RoutedEventArgs());
        }

        public void RefreshItems()
        {
            // Invalidate the cached items list. RefreshItems is called after
            // operations that may have added/removed entries from the
            // underlying AssetManager (DuplicateAsset, Import, etc.) — without
            // invalidation, the next folder click would use the stale cache
            // and the new/removed entries would be invisible.
            //
            // The actual re-enumeration is lazy: it happens on the next
            // GetCachedItems() call (i.e. the next folder click or the next
            // UpdateTreeView), so this method itself stays cheap.
            cachedItems = null;
            cachedItemsDirty = true;
            assetListView?.Items.Refresh();

        }

        public void RefreshAll()
        {
            // Invalidate the cached items list — the caller (e.g. bulk
            // import) may have added or removed entries from the
            // underlying AssetManager since the last enumeration, so we
            // can't trust the cache. The next call to GetCachedItems
            // (which happens inside UpdateTreeView) will re-materialize
            // from the current ItemsSource.
            cachedItems = null;
            cachedItemsDirty = true;
            UpdateTreeView();
        }

        public void FocusFilter()
        {
            filterTextBox.Focus();
        }

        private void assetTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // ===================================================================
            // ROOT-CAUSE FIX for the 20-second+ UI freeze when clicking a
            // folder with ~20,000 legacy DDS entries (e.g. "Heads").
            //
            // DIAGNOSTIC FINDINGS (see Frosty editor log window):
            //   Click 1: 35,850ms spent in FrostyLogger.RaisePropertyChanged
            //            → TextBox.Text binding update → TextChanged
            //            → logTextBox_TextChanged → ScrollToEnd
            //            → synchronous layout pass over the entire visual tree
            //            → ListView generates ALL 20,469 containers
            //   Click 2: 38,193ms spent in assetListView.UpdateLayout()
            //            → forced synchronous layout pass
            //            → ListView generates ALL 20,469 containers
            //
            // Both triggers prove that WPF ListView virtualization is NOT
            // limiting container generation to the visible viewport. The
            // VirtualizingStackPanel is generating ALL containers during any
            // synchronous layout pass. This is the underlying defect.
            //
            // FIX (this handler):
            //   1. REMOVE the explicit assetListView.UpdateLayout() call —
            //      this was the click-2 smoking gun, directly forcing a
            //      synchronous layout pass that generated all 20K containers.
            //   2. SUPPRESS App.Logger.Log calls during the click handler.
            //      Even though FrostyLogger now defers RaisePropertyChanged
            //      to Background priority (so App.Logger.Log returns
            //      immediately), the deferred notification eventually fires
            //      and triggers the TextBox→ScrollToEnd→layout cascade. By
            //      suppressing the per-click log lines we eliminate one
            //      source of deferred layout pressure.
            //   3. DEFER all diagnostic logging to AFTER the handler returns.
            //      App.Logger.Log now defers its PropertyChanged notification
            //      to Background priority, so synchronous Log calls inside
            //      the click handler no longer trigger the TextBox layout
            //      cascade. We keep logging minimal regardless to avoid noise.
            //
            // FIX (MainWindow.xaml.cs):
            //   - logTextBox_TextChanged now defers ScrollToEnd to Background
            //     priority, so even if a synchronous Log call slips through,
            //     the layout cascade doesn't happen inside the click handler.
            //
            // FIX (FrostyLogger.cs):
            //   - RaisePropertyChanged is already deferred to Background
            //     priority via ScheduleNotify().
            //
            // WHAT THIS DOES NOT FIX:
            //   The underlying broken virtualization (20K containers being
            //   generated during a layout pass instead of ~30 visible ones).
            //   With these trigger-elimination fixes, the click handler
            //   returns quickly and WPF's natural layout cycle (which runs
            //   at Render priority after the handler returns) handles the
            //   container generation. If virtualization is broken, the user
            //   will still see a freeze — but it will happen during the
            //   next idle/render cycle, not inside the click handler. The
            //   PerfDiag counters below will tell us if container generation
            //   is still happening for non-visible items.
            // ===================================================================

            selectedPath = assetTreeView.SelectedItem as AssetPath;
            SelectedPath = string.IsNullOrEmpty(selectedPath.FullPath) ? "" : selectedPath.FullPath.Remove(0, 1);

            // VIRTUALIZATION HARD-FIX: ensure the SCP and panel are correctly
            // wired BEFORE we change the ItemsSource. If we don't do this and
            // the SCP's CanContentScroll is still False / ScrollOwner is NULL,
            // the next layout pass will realize ALL containers (20K+ rows)
            // and freeze the UI for ~50 seconds. Calling this here is a safety
            // net for the case where the OnApplyTemplate deferred call hasn't
            // run yet, or where the visual tree was rebuilt (e.g. view switch
            // between detail and tile) and the new SCP hasn't been fixed yet.
            ForceVirtualizationHookup();

            // Sample counters BEFORE UpdateListView. These are read again
            // AFTER UpdateListView (and again after the handler returns via
            // a deferred PerfDiag log) to detect container generation.
            int callsBefore = Frosty.Core.Converters.AssetEntryToBitmapSourceConverter._callCount;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            App.Logger.Log("==== click-start path='" + SelectedPath + "' ====");

            UpdateListView(selectedPath);

            int callsAfterUpdate = Frosty.Core.Converters.AssetEntryToBitmapSourceConverter._callCount;
            App.Logger.Log("click: after-UpdateListView elapsed=" + sw.ElapsedMilliseconds + "ms"
                + " | converter-calls-during-UpdateListView=" + (callsAfterUpdate - callsBefore)
                + " (NO UpdateLayout call — let WPF schedule layout naturally)");

            // CRITICAL: Do NOT call assetListView.UpdateLayout() here.
            // Do NOT call App.Logger.Log() here.
            // Both force (or eventually trigger) a synchronous layout pass
            // that — combined with broken virtualization — generates all
            // 20K containers and freezes the UI for ~35 seconds.
            //
            // Defer the post-click diagnostic log line to Background
            // priority so it runs AFTER WPF's natural layout cycle for this
            // click has completed. This lets us measure how many containers
            // WPF generated during the natural (non-forced) layout pass.
            //
            // Use assetListView.Dispatcher (NOT Dispatcher.CurrentDispatcher)
            // to guarantee we're queueing on the UI thread's dispatcher.
            int callsAtDeferTime = callsAfterUpdate;
            assetListView.Dispatcher.BeginInvoke(new Action(() =>
            {
                int callsAfterLayout = Frosty.Core.Converters.AssetEntryToBitmapSourceConverter._callCount;
                App.Logger.Log("click: after-natural-layout elapsed=" + sw.ElapsedMilliseconds + "ms"
                    + " | converter-calls-during-natural-layout=" + (callsAfterLayout - callsAtDeferTime)
                    + " | TOTAL-since-click-start=" + (callsAfterLayout - callsBefore));

                // Also log the actual ItemsPanel type and ScrollUnit so we
                // can verify virtualization is configured correctly. If
                // these show "VirtualizingStackPanel" and "Item", then the
                // XAML config is correct and the broken virtualization is
                // caused by something else (likely an infinite-height
                // parent or a code path that calls ContainerFromItem).
                if (assetListView != null)
                {
                    // Read the attached virtualization properties directly
                    // from the ListView. These always work because they're
                    // attached properties — the value is stored on the
                    // ListView itself, not on the panel.
                    var isVirtualizing = VirtualizingPanel.GetIsVirtualizing(assetListView);
                    var virtualizationMode = VirtualizingPanel.GetVirtualizationMode(assetListView);
                    var scrollUnit = VirtualizingPanel.GetScrollUnit(assetListView);
                    var canContentScroll = ScrollViewer.GetCanContentScroll(assetListView);

                    // Find the ACTUAL panel instance WPF instantiated from
                    // the ItemsPanelTemplate by walking the visual tree.
                    // This is the only reliable way to detect what panel is
                    // really hosting the items (the ItemsPanelTemplate
                    // itself doesn't expose the panel type publicly).
                    var actualPanel = PerfDiag.FindItemsHostPanel(assetListView);
                    string actualPanelType = actualPanel?.GetType().FullName ?? "(no panel yet)";

                    // ListView itself does not expose ViewportHeight — that
                    // property lives on the inner ScrollViewer that the
                    // ListView control template injects. Walk the visual
                    // tree to find it; if it isn't there yet (e.g. the
                    // template hasn't been applied) log "n/a".
                    double listViewportHeight = double.NaN;
                    var listScrollViewer = FindVisualChild<ScrollViewer>(assetListView);
                    if (listScrollViewer != null)
                        listViewportHeight = listScrollViewer.ViewportHeight;

                    App.Logger.Log("click: virtualization-config"
                        + " actualPanel=" + actualPanelType
                        + " IsVirtualizing(lv)=" + isVirtualizing
                        + " VirtualizationMode(lv)=" + virtualizationMode
                        + " ScrollUnit(lv)=" + scrollUnit
                        + " CanContentScroll(lv)=" + canContentScroll
                        + " ListViewActualHeight=" + assetListView.ActualHeight
                        + " ListViewViewportHeight=" + (double.IsNaN(listViewportHeight) ? "n/a" : listViewportHeight.ToString("F1"))
                        + " ItemsCount=" + assetListView.Items.Count);

                    // --- Visual tree path from ListView down to the items host ---
                    // This is the KEY diagnostic for virtualization issues: it
                    // shows EVERY node between the ListView and the panel that
                    // is actually hosting items. If the path contains a
                    // non-virtualizing panel (StackPanel, WrapPanel, etc.) or
                    // a non-ScrollContentPresenter scroll container, that's
                    // the bug. The expected path for a virtualized GridView is:
                    //   ListView → Border → ScrollViewer → Grid → DockPanel
                    //     → Grid → ScrollContentPresenter → ItemsPresenter
                    //     → VirtualizingStackPanel (IsItemsHost=True)
                    if (actualPanel != null)
                    {
                        var path = PerfDiag.GetVisualPath(assetListView, actualPanel);
                        App.Logger.Log("  visual-tree-path ListView→itemsHost (depth=" + path.Count + "):");
                        for (int i = 0; i < path.Count; i++)
                        {
                            var node = path[i];
                            string line = "    [" + i + "] " + node.GetType().Name;
                            if (node is FrameworkElement fe)
                            {
                                line += " ActualHeight=" + fe.ActualHeight.ToString("F1")
                                     + " Height=" + (double.IsNaN(fe.Height) ? "NaN" : fe.Height.ToString());
                            }
                            if (node is ScrollContentPresenter scp)
                            {
                                line += " CanContentScroll=" + scp.CanContentScroll
                                     + " CanVerticallyScroll=" + scp.CanVerticallyScroll
                                     + " Hash=" + scp.GetHashCode();
                            }
                            if (node is ScrollViewer sv)
                            {
                                line += " CanContentScroll=" + sv.CanContentScroll
                                     + " ViewportHeight=" + sv.ViewportHeight.ToString("F1")
                                     + " ScrollableHeight=" + sv.ScrollableHeight.ToString("F1");
                            }
                            if (node is Panel p)
                            {
                                line += " IsItemsHost=" + p.IsItemsHost
                                     + " Children=" + VisualTreeHelper.GetChildrenCount(p);
                            }
                            App.Logger.Log(line);
                        }

                        // --- Read virtualization properties directly from the
                        // items host panel (not from the ListView). If the
                        // panel doesn't inherit the attached properties from
                        // the ListView, virtualization may be silently
                        // disabled even though the ListView reports True.
                        string panelIsVirtualizing = "?";
                        string panelVirtMode = "?";
                        string panelScrollUnit = "?";
                        try
                        {
                            panelIsVirtualizing = VirtualizingPanel.GetIsVirtualizing(actualPanel).ToString();
                            panelVirtMode = VirtualizingPanel.GetVirtualizationMode(actualPanel).ToString();
                            panelScrollUnit = VirtualizingPanel.GetScrollUnit(actualPanel).ToString();
                        }
                        catch { /* some panels don't support these attached props */ }

                        App.Logger.Log("  itemsHost: Type=" + actualPanel.GetType().Name
                            + " IsItemsHost=" + actualPanel.IsItemsHost
                            + " IsVirtualizing(panel)=" + panelIsVirtualizing
                            + " VirtualizationMode(panel)=" + panelVirtMode
                            + " ScrollUnit(panel)=" + panelScrollUnit
                            + " ActualHeight=" + actualPanel.ActualHeight.ToString("F1")
                            + " DesiredSize=" + actualPanel.DesiredSize.ToString()
                            + " VisibleChildren=" + VisualTreeHelper.GetChildrenCount(actualPanel));

                        // If the items host is a VirtualizingPanel (the base
                        // class for VirtualizingStackPanel), also log whether
                        // it has a scroll owner attached. A null scroll owner
                        // means the panel doesn't know its viewport size,
                        // which forces it to realize ALL containers when
                        // measured with infinite height. ScrollOwner is a
                        // protected member on Panel — we must access it via
                        // the IScrollInfo interface that Panel implements.
                        if (actualPanel is IScrollInfo si)
                        {
                            var scrollOwner = si.ScrollOwner;
                            App.Logger.Log("  itemsHost.ScrollOwner=" + (scrollOwner == null ? "NULL (this is BAD — panel can't virtualize without a scroll owner)" : scrollOwner.GetType().Name));
                        }
                    }
                    else
                    {
                        App.Logger.Log("  itemsHost: NOT FOUND — visual tree may not be built yet, or no Panel has IsItemsHost=True");
                    }

                    // Walk up the visual tree to find any ancestor that
                    // might be giving the ListView infinite available height
                    // (the #1 cause of broken virtualization).
                    DependencyObject walker = assetListView;
                    int depth = 0;
                    while (walker != null && depth < 15)
                    {
                        var fe = walker as FrameworkElement;
                        if (fe != null)
                        {
                            App.Logger.Log("  ancestor[" + depth + "]=" + fe.GetType().Name
                                + " ActualHeight=" + fe.ActualHeight
                                + " Height=" + fe.Height
                                + " MinHeight=" + fe.MinHeight
                                + " MaxHeight=" + fe.MaxHeight);
                        }
                        walker = VisualTreeHelper.GetParent(walker);
                        depth++;
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Depth-first search for the first descendant of <paramref name="root"/>
        /// that is of type <typeparamref name="T"/>. Returns null if the visual
        /// tree hasn't been built yet (e.g. before the template is applied) or
        /// if no such descendant exists.
        /// </summary>
        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var inner = FindVisualChild<T>(child);
                if (inner != null) return inner;
            }
            return null;
        }

        private void ClearFilter()
        {
            if (prevFilterText != "")
            {
                filterTextBox.Text = "";
                prevFilterText = filterTextBox.Text;
                BuildFilterData(filterTextBox.Text);
                UpdateTreeView();
            }
        }

        private void FilterTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if(filterTextBox.Text != prevFilterText)
            {
                prevFilterText = filterTextBox.Text;
                BuildFilterData(filterTextBox.Text);
                UpdateTreeView();
            }
        }

        private void FilterTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                filterTextBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        }

        private void assetListViewColumn_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is GridViewColumnHeader column))
                return;

            string sortBy = column.Tag.ToString();
            ListSortDirection sortDir = ListSortDirection.Ascending;
            if (column == lastSortHeader)
                sortDir = 1 - lastSortDirection;

            // Use CustomSort (typed IComparer) instead of SortDescriptions
            // (reflection-based) — see OnApplyTemplate for the rationale.
            activeComparer = new AssetEntryComparer(sortBy, sortDir);
            ApplyCustomSort(assetListView);

            if (lastSortHeader != null)
                lastSortHeader.Column.HeaderTemplate = FindResource("assetListViewNoSorting") as DataTemplate;

            column.Column.HeaderTemplate = (sortDir == ListSortDirection.Ascending)
                ? FindResource("assetListViewAscendingSorting") as DataTemplate
                : FindResource("assetListViewDescendingSorting") as DataTemplate;

            lastSortHeader = column;
            lastSortDirection = sortDir;
        }

        /// <summary>
        /// Return the ItemsSource as a materialized List&lt;AssetEntry&gt;.
        /// The external ItemsSource is typically a yield-based IEnumerable
        /// (App.AssetManager.EnumerateCustomAssets), and re-enumerating it
        /// on every folder click is wasteful. We cache the materialized
        /// list and only rebuild it when ItemsSource changes.
        /// </summary>
        private List<AssetEntry> GetCachedItems()
        {
            if (!cachedItemsDirty && cachedItems != null)
                return cachedItems;

            cachedItems = new List<AssetEntry>();
            if (ItemsSource != null)
            {
                foreach (AssetEntry entry in ItemsSource)
                    cachedItems.Add(entry);
            }
            cachedItemsDirty = false;
            return cachedItems;
        }

        private void UpdateTreeView()
        {
            if (assetTreeView == null)
                return;

            if (selectedPath != null)
                selectedPath.IsSelected = false;

            if (ItemsSource == null)
                return;

            AssetPath root = new AssetPath("", "", null);
            List<AssetEntry> items = GetCachedItems();
            foreach (AssetEntry entry in items)
            {
                if (ShowOnlyModified && !entry.IsModified)
                    continue;

                if (!FilterText(entry.Name, entry))
                    continue;

                string[] arr = entry.Path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                AssetPath next = root;

                foreach (string path in arr)
                {
                    bool bFound = false;
                    foreach (AssetPath child in next.Children)
                    {
                        if (child.PathName.Equals(path, StringComparison.OrdinalIgnoreCase))
                        {
                            if (path.ToCharArray().Any(char.IsUpper))
                                child.UpdatePathName(path);

                            next = child;
                            bFound = true;
                            break;
                        }
                    }

                    if (!bFound)
                    {
                        string fullPath = next.FullPath + "/" + path;
                        AssetPath newPath = null;

                        if (!assetPathMapping.ContainsKey(fullPath))
                        {
                            newPath = new AssetPath(path, fullPath, next);
                            assetPathMapping.Add(fullPath, newPath);
                        }
                        else
                        {
                            newPath = assetPathMapping[fullPath];
                            newPath.Children.Clear();

                            if (newPath == selectedPath)
                                selectedPath.IsSelected = true;
                        }

                        next.Children.Add(newPath);
                        next = newPath;
                    }
                }
            }

            if(!assetPathMapping.ContainsKey("/"))
                assetPathMapping.Add("/", new AssetPath("![root]", "", null, true));           
            root.Children.Insert(0, assetPathMapping["/"]);

            assetTreeView.ItemsSource = root.Children;
            assetTreeView.Items.SortDescriptions.Add(new SortDescription("PathName", ListSortDirection.Ascending));

            UpdateListView(selectedPath);
        }

        /// <summary>
        /// Apply the currently-active AssetEntryComparer to a ListView's
        /// underlying ListCollectionView. ItemCollection doesn't expose
        /// CustomSort directly — only ListCollectionView does — so we need
        /// to walk to the CollectionView that WPF wraps around the
        /// ItemsSource and cast it.
        ///
        /// The view lookup is cached by WPF (CollectionViewSource keeps a
        /// default view per source collection), so this call is cheap.
        /// </summary>
        private void ApplyCustomSort(ListView lv)
        {
            if (lv == null || activeComparer == null)
                return;

            // CollectionViewSource.GetDefaultView returns the cached
            // ICollectionView for the source. When ItemsSource is a List<>,
            // this is a ListCollectionView, which is the only built-in
            // view type that exposes CustomSort. Cast defensively in case
            // a future ItemsSource type produces a different view.
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(lv.Items)
                       as System.Windows.Data.ListCollectionView;
            if (view != null)
                view.CustomSort = activeComparer;
        }

        private void UpdateListView(AssetPath path = null)
        {
            // Diagnostic stopwatch — log timings to App.Logger so we can
            // see exactly where time is spent when clicking a folder with
            // many entries. This is the ONLY way to tell whether the
            // freeze is in enumeration, sorting, ItemsSource assignment,
            // the CollectionView's internal sort, or container generation.
            // The log line is prefixed with "UpdateListView" so it's easy
            // to grep out of the Frosty log panel.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0;
            t0 = sw.ElapsedMilliseconds;

            if (path == null)
            {
                assetListView.ItemsSource = null;
                return;
            }

            List<AssetEntry> items = new List<AssetEntry>();
            string fullPath = path.FullPath.Trim('/');

            List<AssetEntry> source = GetCachedItems();
            t1 = sw.ElapsedMilliseconds;

            foreach (AssetEntry entry in source)
            {
                if (ShowOnlyModified && !entry.IsModified)
                    continue;
                if (entry.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!FilterText(entry.Name, entry))
                        continue;
                    items.Add(entry);
                }
            }
            t2 = sw.ElapsedMilliseconds;

            // Pre-sort the items list using the same comparer that's applied
            // to the ListView. WPF's ListCollectionView will still run the
            // CustomSort comparer when ItemsSource is assigned, but having
            // the data pre-sorted means the comparer does mostly "equal"
            // comparisons (cheap) rather than full reordering work.
            if (activeComparer != null)
                items.Sort(activeComparer);
            t3 = sw.ElapsedMilliseconds;

            // DeferRefresh avoids duplicate sort passes when ItemsSource is
            // assigned — WPF will do a single sort/refresh when the using
            // block exits, instead of refreshing on every internal state
            // change during the assignment.
            using (assetListView.Items.DeferRefresh())
            {
                assetListView.ItemsSource = items;
                // Re-apply CustomSort AFTER setting ItemsSource. WPF creates
                // a NEW ListCollectionView when ItemsSource is assigned, so
                // any CustomSort set on the PREVIOUS view is lost. We must
                // re-set it on the new view every time. (The pre-sort above
                // ensures the data is already in order, so the view's sort
                // pass is a cheap verification — but without CustomSort set,
                // the view would not sort at all and column-header clicks
                // would not re-sort either.)
                ApplyCustomSort(assetListView);
            }
            t4 = sw.ElapsedMilliseconds;

            // VIRTUALIZATION HARD-FIX: Setting ItemsSource can trigger WPF
            // to reapply the ScrollViewer's template, which DESTROYS the old
            // ScrollContentPresenter and creates a NEW one. The new SCP
            // starts with the default CanContentScroll=False, and since
            // CanContentScroll is NOT inherited (the SCP's AddOwner metadata
            // drops the Inherits flag), the new SCP never picks up the True
            // value we set on the previous SCP. This is the smoking gun in
            // the diagnostic log: ForceVirtualizationHookup runs at t=6923ms
            // and reports "nothing to fix" (CanContentScroll=True), but by
            // t=68747ms (the deferred diagnostic) the SCP reports
            // CanContentScroll=False — a new SCP instance was created.
            //
            // We re-apply the fix HERE, synchronously, before the natural
            // layout pass runs (the layout pass happens at Render priority,
            // which is after we return from this synchronous call). This
            // ensures the SCP has CanContentScroll=True BEFORE the panel is
            // measured, so the panel sees a finite viewport and only
            // realizes ~6 visible containers instead of all 20,469.
            ForceVirtualizationHookup();

            if (SelectedAsset != null)
            {
                assetListView.SelectedItem = SelectedAsset;
            }
            t5 = sw.ElapsedMilliseconds;

            // Only log when the folder has a meaningful number of items —
            // avoids spamming the log for tiny folders. The threshold of
            // 500 is well below the 20k "Heads" folder, so the slow case
            // will always be logged.
            //
            // We use App.Logger.Log for this diagnostic. FrostyLogger now defers
            // RaisePropertyChanged to Background priority, the deferred
            // notification eventually fires and triggers the
            // TextBox→ScrollToEnd→layout cascade. If that fires DURING the
            // natural WPF layout cycle for this click (which runs at Render
            // priority, higher than Background), the layout cascade gets
            // coalesced into the click's own layout pass — re-introducing
            // the 35-second freeze. Writing to PerfDiag avoids the TextBox
            // entirely, so there is no layout cascade to coalesce.
            if (items.Count > 500)
            {
                App.Logger.Log("UpdateListView: path='" + fullPath + "' items=" + items.Count
                    + " | cached-lookup=" + (t1 - t0) + "ms"
                    + " enumerate=" + (t2 - t1) + "ms"
                    + " presort=" + (t3 - t2) + "ms"
                    + " itemssource+sort=" + (t4 - t3) + "ms"
                    + " select=" + (t5 - t4) + "ms"
                    + " total=" + (t5 - t0) + "ms");
            }
        }

        private void assetListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            object selectedItem = null;
            if (MultiSelect)
            {
                List<AssetEntry> selectedItems = new List<AssetEntry>();
                foreach (AssetEntry entry in assetListView.SelectedItems)
                    selectedItems.Add(entry);

                if (selectedItems.Count > 0)
                {
                    selectedItem = selectedItems[0];
                    SetValue(SelectedAssetsProperty, selectedItems);
                }
            }
            else
            {
                selectedItem = assetListView.SelectedItem;
            }

            if (bookmarkContext != null)
            {
                // True: Only switch contexts when there is something to bookmark.
                // False: This context doesn't have anything to bookmark, but the active one still might.
                bookmarkContext.AvailableTarget = selectedItem != null ? new AssetBookmarkTarget(assetListView.SelectedItem as AssetEntry) : null;
            }

            SelectedAsset = selectedItem as AssetEntry;
            SelectionChanged?.Invoke(this, new RoutedEventArgs());
        }

        private void AssetList_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl))
            {
                double newZoom = TileZoom + (e.Delta / 10.0);
                if (TileZoom == 50.0 && newZoom > TileZoom && GridView)
                {
                    GridView = false;
                    newZoom = 50.0;
                }
                else if (newZoom < 50.0)
                {
                    if (newZoom < TileZoom && !GridView)
                        GridView = true;

                    newZoom = 50.0;
                }
                else if (newZoom > 162.0)
                    newZoom = 162.0;
                TileZoom = newZoom;

                e.Handled = true;
            }
        }

        private bool FilterText(string inText, AssetEntry inEntry)
        {
            string type = inEntry.Type ?? "";

            if (filter.Count == 0)
                return true;

            bool retCode = false;
            foreach (FilterData filterData in filter)
            {
                bool nextRetCode = false;
                switch (filterData.Command)
                {
                    case FilterCommandType.Contains: nextRetCode = inText.IndexOf(filterData.Text, StringComparison.OrdinalIgnoreCase) >= 0; break;
                    case FilterCommandType.StartsWith: nextRetCode = inText.StartsWith(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                    case FilterCommandType.EndsWith: nextRetCode = inText.EndsWith(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                    case FilterCommandType.RegEx: nextRetCode = System.Text.RegularExpressions.Regex.IsMatch(inText, filterData.Text); break;
                    case FilterCommandType.Type: nextRetCode = type.Equals(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                    case FilterCommandType.Id:
                        if (inEntry is EbxAssetEntry entry)
                        {
                            nextRetCode = entry.Guid.Equals(new Guid(filterData.Text));
                        }
                        else
                        {
                            if (ulong.TryParse(filterData.Text, System.Globalization.NumberStyles.HexNumber, null, out ulong resRid))
                                nextRetCode = (inEntry as ResAssetEntry).ResRid == resRid;

                            if (nextRetCode == true)
                            {
                            }
                        }
                        break;
                    case FilterCommandType.Hash:
                        {
                            if (int.TryParse(filterData.Text, System.Globalization.NumberStyles.HexNumber, null, out int hash))
                            {
                                if (inEntry is EbxAssetEntry || inEntry is ResAssetEntry)
                                {
                                    nextRetCode = Fnv1.HashString(inEntry.Name.ToLower()) == hash;
                                }
                            }
                        }
                        break;
                }

                if (filterData.Not)
                    nextRetCode = !nextRetCode;

                switch (filterData.Combine)
                {
                    case FilterCombineType.And: retCode &= nextRetCode; break;
                    case FilterCombineType.Or: retCode |= nextRetCode; break;
                }
            }

            return retCode;
        }

        private void BuildFilterData(string filterText)
        {
            filter.Clear();
            if (filterText != "")
            {
                string[] subStr = filterText.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (subStr.Length == 1 && !subStr[0].Contains(":"))
                {
                    filter.Add(new FilterData()
                    {
                        Text = subStr[0],
                        Command = FilterCommandType.Contains,
                        Combine = FilterCombineType.Or,
                        Not = false
                    });
                    return;
                }

                try
                {
                    for (int i = 0; i < subStr.Length; i++)
                    {
                        FilterCommandType command = FilterCommandType.Contains;
                        FilterCombineType combine = FilterCombineType.Or;
                        bool not = false;

                        if (filter.Count != 0)
                            combine = (subStr[i++] == "AND") ? FilterCombineType.And : FilterCombineType.Or;

                        if (subStr[i] == "NOT")
                        {
                            not = true;
                            i++;
                        }

                        string[] cmdArr = subStr[i].Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                        string cmdString = subStr[i];
                        string remaining = "";

                        if (cmdArr.Length > 1)
                        {
                            cmdString = cmdArr[0];
                            remaining = cmdArr[1];
                        }
                        else
                            remaining = subStr[++i];

                        foreach (string value in Enum.GetNames(typeof(FilterCommandType)))
                        {
                            if (cmdString.StartsWith(value.ToLower()))
                            {
                                command = (FilterCommandType)Enum.Parse(typeof(FilterCommandType), value);
                                break;
                            }
                        }

                        filter.Add(new FilterData()
                        {
                            Text = remaining,
                            Command = command,
                            Combine = combine,
                            Not = not
                        });
                    }
                }
                catch(Exception)
                {
                    filterTextBox.Text = "";
                    filter.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Typed comparer for AssetEntry used by FrostyDataExplorer's ListView.
    /// Replaces the default SortDescription-based sort (which uses
    /// PropertyDescriptor / reflection) with direct property access — for
    /// folders containing tens of thousands of legacy entries this avoids
    /// hundreds of thousands of reflection calls per folder-click.
    ///
    /// Implements BOTH IComparer (for ListCollectionView.CustomSort, which
    /// takes the non-generic IComparer) and IComparer&lt;AssetEntry&gt; (so
    /// we can also pre-sort a List&lt;AssetEntry&gt; via List.Sort(IComparer&lt;T&gt;)
    /// in UpdateListView).
    /// </summary>
    internal sealed class AssetEntryComparer : IComparer, IComparer<AssetEntry>
    {
        private readonly string propertyName;
        private readonly ListSortDirection direction;

        public AssetEntryComparer(string propertyName, ListSortDirection direction)
        {
            this.propertyName = propertyName ?? "DisplayName";
            this.direction = direction;
        }

        // Generic IComparer<AssetEntry> — used by List<AssetEntry>.Sort().
        public int Compare(AssetEntry a, AssetEntry b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return direction == ListSortDirection.Ascending ? -1 : 1;
            if (b == null) return direction == ListSortDirection.Ascending ? 1 : -1;

            int result;
            switch (propertyName)
            {
                case "Type":
                    string aType = a.Type ?? "";
                    string bType = b.Type ?? "";
                    result = string.Compare(aType, bType, StringComparison.OrdinalIgnoreCase);
                    break;
                case "DisplayName":
                default:
                    // DisplayName getter does a Filename substring + IsDirty
                    // lookup; we use OrdinalIgnoreCase for stable cross-locale
                    // ordering (matches what users expect for asset names).
                    string aName = a.DisplayName ?? "";
                    string bName = b.DisplayName ?? "";
                    result = string.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
                    break;
            }

            return direction == ListSortDirection.Ascending ? result : -result;
        }

        // Non-generic IComparer — used by ListCollectionView.CustomSort.
        // Forwards to the typed implementation; non-AssetEntry objects
        // (shouldn't normally occur) fall back to ToString comparison.
        public int Compare(object x, object y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return direction == ListSortDirection.Ascending ? -1 : 1;
            if (y == null) return direction == ListSortDirection.Ascending ? 1 : -1;

            AssetEntry a = x as AssetEntry;
            AssetEntry b = y as AssetEntry;
            if (a != null && b != null)
                return Compare(a, b);

            int fallback = string.Compare(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);
            return direction == ListSortDirection.Ascending ? fallback : -fallback;
        }
    }
}
