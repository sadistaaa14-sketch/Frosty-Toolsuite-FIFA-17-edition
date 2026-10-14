using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using Frosty.Core;
using Frosty.Core.Bulk;
using System.Windows.Media;
using FrostySdk;

namespace ChunkResEditorPlugin
{
    public class FrostyChunkExportCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (parameter is FrameworkElement param && param.Tag is FrostyChunkResEditor explorer)
            {
                explorer.ExportChunk();
            }
        }
    }
    public class FrostyChunkImportCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (parameter is FrameworkElement param && param.Tag is FrostyChunkResEditor explorer)
            {
                explorer.ImportChunk();
            }
        }
    }
    public class FrostyChunkRevertCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (parameter is FrameworkElement param && param.Tag is FrostyChunkResEditor explorer)
            {
                explorer.RevertChunk();
            }
        }
    }
    public class FrostyChunkRightClickCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (parameter is ListBoxItem lbi) 
                lbi.IsSelected = true;
        }
    }

    public class FrostyChunkBulkExportCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (parameter is FrameworkElement param && param.Tag is FrostyChunkResEditor explorer)
                explorer.BulkExportChunks();
        }
    }

    public class FrostyChunkBulkImportCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (parameter is FrameworkElement param && param.Tag is FrostyChunkResEditor explorer)
                explorer.BulkImportChunks();
        }
    }

    /// <summary>
    /// Copies a chunk id. The chunk to copy is passed in as the command parameter (the data
    /// context of the row the menu was raised on) so it never depends on which row happened to be
    /// selected when the menu opened.
    /// </summary>
    public class FrostyChunkCopyIdCommand : ICommand
    {
        public bool CanExecute(object parameter)
        {
            return true;
        }

#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public void Execute(object parameter)
        {
            if (!(parameter is ChunkAssetEntry chunk))
                return;

            Clipboard.SetText(AssetReferenceUtils.FormatChunkId(chunk.Id));
            App.Logger?.Log("Copied chunk id {0} to the clipboard.", chunk.Id);
        }
    }

    [TemplatePart(Name = PART_ChunksListBox, Type = typeof(ListBox))]
    [TemplatePart(Name = PART_ChunksBundlesBox, Type = typeof(ListBox))]
    [TemplatePart(Name = PART_ResExplorer, Type = typeof(FrostyDataExplorer))]
    [TemplatePart(Name = PART_ResBundlesBox, Type = typeof(ListBox))]
    [TemplatePart(Name = PART_ResExportMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_ResImportMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_RevertMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_ResBulkExportMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_ResBulkImportMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_ResCopyChunkIdMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_ResCopyResIdMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_ChunkFilter, Type = typeof(TextBox))]
    [TemplatePart(Name = PART_ChunkModified, Type = typeof(CheckBox))]
    public class FrostyChunkResEditor : FrostyBaseEditor
    {
        public override ImageSource Icon => ChunkResEditorMenuExtension.imageSource;

        private const string PART_ChunksListBox = "PART_ChunksListBox";
        private const string PART_ChunksBundlesBox = "PART_ChunksBundlesBox";
        private const string PART_ResExplorer = "PART_ResExplorer";
        private const string PART_ResBundlesBox = "PART_ResBundlesBox";
        private const string PART_ResExportMenuItem = "PART_ResExportMenuItem";
        private const string PART_ResImportMenuItem = "PART_ResImportMenuItem";
        private const string PART_RevertMenuItem = "PART_RevertMenuItem";
        private const string PART_ResBulkExportMenuItem = "PART_ResBulkExportMenuItem";
        private const string PART_ResBulkImportMenuItem = "PART_ResBulkImportMenuItem";
        private const string PART_ResCopyChunkIdMenuItem = "PART_ResCopyChunkIdMenuItem";
        private const string PART_ResCopyResIdMenuItem = "PART_ResCopyResIdMenuItem";
        private const string PART_ChunkFilter = "PART_ChunkFilter";
        private const string PART_ChunkModified = "PART_ChunkModified";

        private ListBox chunksListBox;
        private ListBox chunksBundleBox;
        private FrostyDataExplorer resExplorer;
        private ListBox resBundleBox;
        private TextBox chunkFilterTextBox;
        private CheckBox chunkModifiedBox;
        private MenuItem resExportMenuItemRef;
        private MenuItem resImportMenuItemRef;
        private MenuItem revertMenuItemRef;
        private MenuItem resBulkExportMenuItem;
        private MenuItem resBulkImportMenuItem;
        private MenuItem resCopyChunkIdMenuItem;
        private MenuItem resCopyResIdMenuItem;
        private ILogger logger;

        static FrostyChunkResEditor()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(FrostyChunkResEditor), new FrameworkPropertyMetadata(typeof(FrostyChunkResEditor)));
        }

        public FrostyChunkResEditor(ILogger inLogger = null)
        {
            logger = inLogger;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            chunksBundleBox = GetTemplateChild(PART_ChunksBundlesBox) as ListBox;
            resBundleBox = GetTemplateChild(PART_ResBundlesBox) as ListBox;
            chunksListBox = GetTemplateChild(PART_ChunksListBox) as ListBox;
            resExplorer = GetTemplateChild(PART_ResExplorer) as FrostyDataExplorer;
            chunkFilterTextBox = GetTemplateChild(PART_ChunkFilter) as TextBox;
            chunkModifiedBox = GetTemplateChild(PART_ChunkModified) as CheckBox;

            resExplorer.SelectionChanged += ResExplorer_SelectionChanged;
            MenuItem mi = GetTemplateChild(PART_ResExportMenuItem) as MenuItem;
            resExportMenuItemRef = mi;
            mi.Click += ResExportMenuItem_Click;

            mi = GetTemplateChild(PART_ResImportMenuItem) as MenuItem;
            resImportMenuItemRef = mi;
            mi.Click += ResImportMenuItem_Click;

            mi = GetTemplateChild(PART_RevertMenuItem) as MenuItem;
            revertMenuItemRef = mi;
            mi.Click += ResRevertMenuItem_Click;

            resBulkExportMenuItem = GetTemplateChild(PART_ResBulkExportMenuItem) as MenuItem;
            resBulkExportMenuItem.Click += ResBulkExportMenuItem_Click;

            resBulkImportMenuItem = GetTemplateChild(PART_ResBulkImportMenuItem) as MenuItem;
            resBulkImportMenuItem.Click += ResBulkImportMenuItem_Click;

            resCopyChunkIdMenuItem = GetTemplateChild(PART_ResCopyChunkIdMenuItem) as MenuItem;
            resCopyChunkIdMenuItem.Click += ResCopyChunkIdMenuItem_Click;

            resCopyResIdMenuItem = GetTemplateChild(PART_ResCopyResIdMenuItem) as MenuItem;
            resCopyResIdMenuItem.Click += ResCopyResIdMenuItem_Click;

            // The res context menu is shared between res assets in the list and folders in the
            // tree, so enable/disable the entries that only make sense for one of the two.
            resExplorer.ContextMenuOpening += ResExplorer_ContextMenuOpening;

            Loaded += FrostyChunkResEditor_Loaded;
            chunksListBox.SelectionChanged += ChunksListBox_SelectionChanged;
            chunkFilterTextBox.LostFocus += ChunkFilterTextBox_LostFocus;
            chunkFilterTextBox.KeyUp += ChunkFilterTextBox_KeyUp;
            chunkModifiedBox.Checked += ChunkFilterTextBox_LostFocus;
            chunkModifiedBox.Unchecked += ChunkFilterTextBox_LostFocus;
        }

        private void ResExplorer_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (resExplorer.SelectedAsset != null)
            {
                resBundleBox.Items.Clear();
                ResAssetEntry SelectedRes = (ResAssetEntry)resExplorer.SelectedAsset;
                resBundleBox.Items.Add("Selected resource is in Bundles: ");
                foreach (int bundle in SelectedRes.Bundles)
                {
                    resBundleBox.Items.Add(App.AssetManager.GetBundleEntry(bundle).Name);
                }
                if (SelectedRes.AddedBundles.Count != 0)
                {
                    resBundleBox.Items.Add("Added to Bundles:");
                    foreach (int bundle in SelectedRes.AddedBundles)
                    {
                        resBundleBox.Items.Add(App.AssetManager.GetBundleEntry(bundle).Name);
                    }
                }
            }
            else
            {
                resBundleBox.Items.Clear();
                resBundleBox.Items.Add("No res selected");
            }
        }

        private void ChunksListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (chunksListBox.SelectedIndex != -1)
            {
                chunksBundleBox.Items.Clear();
                ChunkAssetEntry SelectedChk = (ChunkAssetEntry)chunksListBox.SelectedItem;
                string FirstLine = "Selected chunk is in Bundles: ";
                if (SelectedChk.FirstMip != -1)
                    FirstLine += " (FirstMip:" + SelectedChk.FirstMip + ")";
                if (App.FileSystem.GetManifestChunk(SelectedChk.Id) != null)
                {
                        chunksBundleBox.Items.Add("Selected chunk is a Manifest chunk.");
                }
                else if (SelectedChk.Bundles.Count == 0 && SelectedChk.AddedBundles.Count == 0)
                {
                    chunksBundleBox.Items.Add("Selected chunk is only in SuperBundles.");
                }
                if (SelectedChk.Bundles.Count != 0)
                {
                    chunksBundleBox.Items.Add(FirstLine);
                    foreach (int bundle in SelectedChk.Bundles)
                    {
                        chunksBundleBox.Items.Add(App.AssetManager.GetBundleEntry(bundle).Name);
                    }
                }
                if (SelectedChk.AddedBundles.Count != 0)
                {
                    chunksBundleBox.Items.Add("Added to Bundles:");
                    foreach (int bundle in SelectedChk.AddedBundles)
                    {
                        chunksBundleBox.Items.Add(App.AssetManager.GetBundleEntry(bundle).Name);
                    }
                }
            }
            else
            {
                chunksBundleBox.Items.Clear();
                chunksBundleBox.Items.Add("No chunk selected");
            }
        }

        private void ChunkFilterTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                UpdateFilter();
        }

        private void ChunkFilterTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateFilter();
        }

        private void UpdateFilter()
        {
            if (chunkFilterTextBox.Text == "" & chunkModifiedBox.IsChecked == false)
            {
                chunksListBox.Items.Filter = null;
                return;
            }
            else if (chunkFilterTextBox.Text != "" & chunkModifiedBox.IsChecked == false)
            {
                chunksListBox.Items.Filter = new Predicate<object>((object a) => ((ChunkAssetEntry)a).Id.ToString().Contains(chunkFilterTextBox.Text.ToLower()));
            }
            else if (chunkFilterTextBox.Text == "" & chunkModifiedBox.IsChecked == true)
            {
                chunksListBox.Items.Filter = new Predicate<object>((object a) => ((ChunkAssetEntry)a).IsModified);
            }
            else if (chunkFilterTextBox.Text != "" & chunkModifiedBox.IsChecked == true)
            {
                chunksListBox.Items.Filter = new Predicate<object>((object a) => (((ChunkAssetEntry)a).IsModified) & ((ChunkAssetEntry)a).Id.ToString().Contains(chunkFilterTextBox.Text.ToLower()));
            }
        }

        private void ResRevertMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(resExplorer.SelectedAsset is ResAssetEntry selectedAsset) || !selectedAsset.IsModified)
                return;

            FrostyTaskWindow.Show("Reverting Asset", "", (task) => { App.AssetManager.RevertAsset(selectedAsset, suppressOnModify: false); });
            resExplorer.RefreshItems();
        }

        private void ResImportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // The context menu is also shown for folder nodes in the tree, where there is no
            // selected resource to import into.
            ResAssetEntry selectedAsset = resExplorer.SelectedAsset as ResAssetEntry;
            if (selectedAsset == null)
                return;

            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog("Open Resource", "*.res (Resource Files)|*.res", "Res");

            if (ofd.ShowDialog())
            {
                FrostyTaskWindow.Show("Importing Asset", "", (task) =>
                {
                    using (NativeReader reader = new NativeReader(new FileStream(ofd.FileName, FileMode.Open, FileAccess.Read)))
                    {
                        byte[] resMeta = reader.ReadBytes(0x10);
                        byte[] buffer = reader.ReadToEnd();

                        if (App.PluginManager.GetCustomHandler((ResourceType)selectedAsset.ResType) != null)
                        {
                            // @todo: throw some kind of error
                        }

                        // @todo
                        //if (selectedAsset.ResType == (uint)ResourceType.ShaderBlockDepot)
                        //{
                        //    // treat manually imported shaderblocks as if every block has been modified

                        //    ShaderBlockDepot sbd = new ShaderBlockDepot(resMeta);
                        //    using (NativeReader subReader = new NativeReader(new MemoryStream(buffer)))
                        //        sbd.Read(subReader, App.AssetManager, selectedAsset, null);

                        //    for (int j = 0; j < sbd.ResourceCount; j++)
                        //    {
                        //        var sbr = sbd.GetResource(j);
                        //        if (sbr is ShaderPersistentParamDbBlock || sbr is MeshParamDbBlock)
                        //            sbr.IsModified = true;
                        //    }

                        //    App.AssetManager.ModifyRes(selectedAsset.Name, sbd);
                        //}

                        //else
                        {
                            App.AssetManager.ModifyRes(selectedAsset.Name, buffer, resMeta);
                        }
                    }
                });
                resExplorer.RefreshItems();
            }
        }

        private void ResExportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // The context menu is also shown for folder nodes in the tree, where there is no
            // selected resource to export.
            ResAssetEntry selectedAsset = resExplorer.SelectedAsset as ResAssetEntry;
            if (selectedAsset == null)
                return;

            FrostySaveFileDialog sfd = new FrostySaveFileDialog("Save Resource", "*.res (Resource Files)|*.res", "Res", selectedAsset.Filename);

            Stream resStream = App.AssetManager.GetRes(selectedAsset);
            if (resStream == null)
                return;

            if (sfd.ShowDialog())
            {
                FrostyTaskWindow.Show("Exporting Asset", "", (task) =>
                {
                    using (NativeWriter writer = new NativeWriter(new FileStream(sfd.FileName, FileMode.Create)))
                    {
                        // write res meta first
                        writer.Write(selectedAsset.ResMeta);

                        // followed by remaining data
                        using (NativeReader reader = new NativeReader(resStream))
                            writer.Write(reader.ReadToEnd());
                    }
                });
                logger?.Log("Resource saved to {0}", sfd.FileName);
            }
        }

        /// <summary>
        /// Enables only the res context menu entries that apply to whatever the menu was opened on.
        /// A folder node in the tree has no selected res asset, so the single-asset commands are
        /// disabled for it - and the folder-wide bulk commands are disabled for a single asset.
        /// </summary>
        private void ResExplorer_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            ResAssetEntry selected = resExplorer.SelectedAsset as ResAssetEntry;
            bool onFolder = selected == null;

            // Single-resource commands cannot do anything for a folder node in the tree, and they
            // used to throw when invoked without a selected resource.
            if (resExportMenuItemRef != null)
                resExportMenuItemRef.IsEnabled = !onFolder;
            if (resImportMenuItemRef != null)
                resImportMenuItemRef.IsEnabled = !onFolder;
            if (revertMenuItemRef != null)
                revertMenuItemRef.IsEnabled = !onFolder;

            // A resource can stream from several chunks (meshes and atlases do), so the header
            // says how many ids the command is about to copy - the same wording the explorer
            // menus use.
            int chunkIdCount = selected != null ? AssetReferenceUtils.GetChunkIds(selected).Count : 0;
            if (resCopyChunkIdMenuItem != null)
            {
                resCopyChunkIdMenuItem.IsEnabled = chunkIdCount != 0;
                resCopyChunkIdMenuItem.Header = chunkIdCount > 1
                    ? "Copy Chunk Ids (" + chunkIdCount + ")"
                    : "Copy Chunk Id";
            }

            if (resCopyResIdMenuItem != null)
                resCopyResIdMenuItem.IsEnabled = AssetReferenceUtils.GetResId(selected) != 0;

            bool bulkEnabled = TryGetResBulkFolder(out string _);
            if (resBulkExportMenuItem != null)
                resBulkExportMenuItem.IsEnabled = bulkEnabled;
            if (resBulkImportMenuItem != null)
                resBulkImportMenuItem.IsEnabled = bulkEnabled;
        }

        /// <summary>
        /// Works out which folder the res bulk commands should act on.
        /// </summary>
        /// <param name="folderPath">
        /// The folder path with no leading slash; "" means the root folder (every res asset).
        /// </param>
        /// <returns>
        /// False when there is no folder to act on - i.e. a single res asset sitting at the root
        /// level, where a folder-wide operation would unexpectedly cover the whole game.
        /// </returns>
        private bool TryGetResBulkFolder(out string folderPath)
        {
            ResAssetEntry selected = resExplorer.SelectedAsset as ResAssetEntry;
            if (selected != null)
            {
                // A root level asset has no folder of its own to export.
                folderPath = selected.Path;
                return folderPath.Length != 0;
            }

            // No selected asset means the menu was raised on a folder node in the tree.
            if (resExplorer.SelectedPath == null)
            {
                folderPath = null;
                return false;
            }

            folderPath = resExplorer.SelectedPath;
            return true;
        }

        private void ResCopyChunkIdMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ResAssetEntry selected = resExplorer.SelectedAsset as ResAssetEntry;
            if (selected == null)
                return;

            List<Guid> chunkIds = AssetReferenceUtils.GetChunkIds(selected);
            if (chunkIds.Count == 0)
            {
                // The same two reasons the asset tree reports: no chunk is named at all, or the ones
                // named are not part of this profile.
                string reason = AssetReferenceUtils.ExplainNoChunks(selected);
                if (string.IsNullOrEmpty(reason))
                    logger?.Log("Resource '{0}' has no associated chunk.", selected.Name);
                else
                    logger?.Log("Resource '{0}' has no associated chunk: {1}", selected.Name, reason);
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, chunkIds.Select(id => AssetReferenceUtils.FormatChunkId(id))));
            logger?.Log("Copied {0} chunk id(s) for '{1}' to the clipboard.", chunkIds.Count, selected.Name);
        }

        private void ResCopyResIdMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ResAssetEntry selected = resExplorer.SelectedAsset as ResAssetEntry;
            if (selected == null)
                return;

            ulong rid = AssetReferenceUtils.GetResId(selected);
            if (rid == 0)
            {
                logger?.Log("Resource '{0}' has no resource id.", selected.Name);
                return;
            }

            Clipboard.SetText(AssetReferenceUtils.FormatResId(rid));
            logger?.Log("Copied resource id of '{0}' to the clipboard.", selected.Name);
        }

        /// <summary>
        /// Exports the resources under the folder the context menu was raised on. The command's scope
        /// already says what is being written, so it asks nothing but where to put the files and the
        /// shared exporter does the work in the format the single asset "Export" command uses - the
        /// 16 byte resource header followed by the resource data - so that "Bulk Import Res" can read
        /// it straight back.
        /// </summary>
        private void ResBulkExportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetResBulkFolder(out string folderPath))
            {
                logger?.Log("Bulk Export: right-click a folder in the tree, or a resource inside a folder.");
                return;
            }

            string match = folderPath.Length == 0 ? "" : folderPath + "/";

            List<ResAssetEntry> toExport = new List<ResAssetEntry>();
            foreach (ResAssetEntry entry in App.AssetManager.EnumerateRes())
            {
                if (entry.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                    toExport.Add(entry);
            }

            if (toExport.Count == 0)
            {
                logger?.Log("No resources found under folder: {0}", folderPath.Length == 0 ? "[root]" : folderPath);
                return;
            }

            // Nothing here needs deciding: the tree node says which resources, and a resource has
            // exactly one form to be written in. The run is handed that scope directly rather than
            // putting a dialog with a single already-ticked box in front of it.
            BulkOptions options = BulkOptionDefaults.ForScope(BulkScope.Res);

            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select output folder for the bulk export (" + toExport.Count + " resources)";
                if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string outputRoot = fbd.SelectedPath;
                string description = options.Describe("Bulk export");

                FrostyTaskWindow.Show("Bulk Exporting Resources", folderPath.Length == 0 ? "[root]" : folderPath, (task) =>
                {
                    // The scope is the folder being exported, so the resources are handed over instead
                    // of being looked up again - the two cannot drift apart that way.
                    BulkResult result = AssetBulkExporter.Run(folderPath, options, outputRoot, task, null, toExport);

                    logger?.Log("{0} -> {1}: {2}", description, outputRoot, result.Describe());
                    foreach (string message in result.Messages)
                        logger?.Log("  {0}", message);

                    WriteRunReport("Bulk export", folderPath, outputRoot, description, result);
                }, true, BulkContext.TaskCancel);
            }
        }

        /// <summary>
        /// Imports the .res files in a user-selected folder into the resources under the folder the
        /// context menu was raised on. Files are matched to existing resources by filename, ignoring
        /// where they sit locally, and each one is read back exactly as "Bulk Export Res" wrote it.
        /// </summary>
        private void ResBulkImportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetResBulkFolder(out string folderPath))
            {
                logger?.Log("Bulk Import: right-click a folder in the tree, or a resource inside a folder.");
                return;
            }

            BulkImportOptionsWindow optionsWindow = new BulkImportOptionsWindow(BulkOptionDefaults.Import, BulkScope.Res);
            if (optionsWindow.ShowDialog() != true)
                return;

            BulkOptions options = optionsWindow.Options;

            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select a folder of .res files to bulk-import. Each file is matched against resources in '"
                    + (folderPath.Length == 0 ? "[root]" : folderPath) + "' by filename.";
                if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string inputRoot = fbd.SelectedPath;
                string description = options.Describe("Bulk import");

                FrostyTaskWindow.Show("Bulk Importing Resources", folderPath.Length == 0 ? "[root]" : folderPath, (task) =>
                {
                    BulkResult result = AssetBulkImporter.Run(folderPath, options, inputRoot, task);

                    logger?.Log("{0} <- {1}: {2}", description, inputRoot, result.Describe());
                    foreach (string message in result.Messages)
                        logger?.Log("  {0}", message);
                }, true, BulkContext.TaskCancel);

                resExplorer.RefreshAll();
            }
        }

        /// <summary>
        /// Exports the chunks currently listed in the chunk list, one "&lt;chunk id&gt;.chunk" file per
        /// chunk. The chunk filter and the "show only modified" checkbox are respected, so the user
        /// can export a subset by filtering first - the list is the scope, and it is what the command
        /// writes, without a dialog in between saying the same thing back.
        /// </summary>
        public void BulkExportChunks()
        {
            List<ChunkAssetEntry> chunks = GetListedChunks();
            if (chunks.Count == 0)
            {
                logger?.Log("Bulk Export: no chunks are listed - clear the filter and try again.");
                return;
            }

            // Chunks have exactly one form to be written in, so the listed chunks are the whole
            // answer: no dialog to say it, and no confirmation on top of it either.
            BulkOptions options = BulkOptionDefaults.ForScope(BulkScope.Chunks);

            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select output folder for the bulk chunk export (" + chunks.Count + " chunks)";
                if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string outputRoot = fbd.SelectedPath;
                string description = options.Describe("Bulk export");

                FrostyTaskWindow.Show("Bulk Exporting Chunks", outputRoot, (task) =>
                {
                    // The listed chunks are the scope, so the run exports exactly what the user can
                    // see (and is filtered to).
                    BulkResult result = AssetBulkExporter.Run("", options, outputRoot, task, chunks);

                    logger?.Log("{0} -> {1}: {2}", description, outputRoot, result.Describe());
                    foreach (string message in result.Messages)
                        logger?.Log("  {0}", message);

                    WriteRunReport("Bulk export", "", outputRoot, description, result);
                }, true, BulkContext.TaskCancel);
            }
        }

        /// <summary>
        /// Imports the "*.chunk" files in a user-selected folder, after asking what to read in the
        /// bulk import options window. Each file is named after the chunk id it belongs to (the same
        /// naming "Bulk Export Chunks" produces) and is written into that chunk. Files whose name is
        /// not a chunk id, or whose chunk is not loaded, are reported and skipped rather than guessed
        /// at.
        /// </summary>
        public void BulkImportChunks()
        {
            BulkImportOptionsWindow optionsWindow = new BulkImportOptionsWindow(BulkOptionDefaults.Import, BulkScope.Chunks);
            if (optionsWindow.ShowDialog() != true)
                return;

            BulkOptions options = optionsWindow.Options;

            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select the folder of .chunk files to bulk-import. Each file must be named after the chunk id it belongs to.";
                if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string inputRoot = fbd.SelectedPath;
                string description = options.Describe("Bulk import");

                FrostyTaskWindow.Show("Bulk Importing Chunks", inputRoot, (task) =>
                {
                    BulkResult result = AssetBulkImporter.Run("", options, inputRoot, task);

                    logger?.Log("{0} <- {1}: {2}", description, inputRoot, result.Describe());
                    foreach (string message in result.Messages)
                        logger?.Log("  {0}", message);
                }, true, BulkContext.TaskCancel);

                RefreshChunksListBox(null);
            }
        }

        /// <summary>
        /// Leaves the run's report beside its output. The log window is the only other place these
        /// notes go, and it is gone once the editor closes, so a run that wrote no chunk file still
        /// has something to read afterwards.
        /// </summary>
        private void WriteRunReport(string operation, string folder, string outputRoot, string description, BulkResult result)
        {
            string reportPath = BulkReport.PathFor(outputRoot);
            if (BulkReport.Write(reportPath, BulkReport.Text(operation, folder, outputRoot, description, result, result.Details)))
                logger?.Log("run report: {0}", reportPath);
        }

        /// <summary>
        /// Returns the chunks currently listed in the chunk list box, honouring both the text filter
        /// and the "show only modified" checkbox (the ListBox's view applies them for us).
        /// </summary>
        private List<ChunkAssetEntry> GetListedChunks()
        {
            List<ChunkAssetEntry> chunks = new List<ChunkAssetEntry>();
            if (chunksListBox == null)
                return chunks;

            foreach (object item in chunksListBox.Items)
            {
                if (item is ChunkAssetEntry chunk)
                    chunks.Add(chunk);
            }

            return chunks;
        }

        public void ImportChunk()
        {
            ChunkAssetEntry selectedAsset = chunksListBox.SelectedItem as ChunkAssetEntry;
            if (selectedAsset == null)
            {
                // The commands are only reachable from a chunk row, which right-click selects -
                // but never write into a chunk that is not actually there.
                logger?.Log("Import: select a chunk first.");
                return;
            }

            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog("Open Chunk", "*.chunk (Chunk Files)|*.chunk", "Chunk");

            if (ofd.ShowDialog())
            {
                FrostyTaskWindow.Show("Importing Chunk", "", (task) =>
                {
                    using (NativeReader reader = new NativeReader(new FileStream(ofd.FileName, FileMode.Open, FileAccess.Read)))
                    {
                        byte[] buffer = reader.ReadToEnd();
                        App.AssetManager.ModifyChunk(selectedAsset.Id, buffer);
                    }
                });
                RefreshChunksListBox(selectedAsset);
            }
        }

        public void ExportChunk()
        {
            ChunkAssetEntry selectedAsset = chunksListBox.SelectedItem as ChunkAssetEntry;
            if (selectedAsset == null)
            {
                logger?.Log("Export: select a chunk first.");
                return;
            }

            FrostySaveFileDialog sfd = new FrostySaveFileDialog("Save Chunk", "*.chunk (Chunk Files)|*.chunk", "Chunk", selectedAsset.Filename);

            Stream chunkStream = App.AssetManager.GetChunk(selectedAsset);
            if (chunkStream == null)
                return;

            if (sfd.ShowDialog())
            {
                FrostyTaskWindow.Show("Exporting Chunk", "", (task) =>
                {
                    using (NativeWriter writer = new NativeWriter(new FileStream(sfd.FileName, FileMode.Create)))
                    {
                        using (NativeReader reader = new NativeReader(chunkStream))
                            writer.Write(reader.ReadToEnd());
                    }
                });
                logger?.Log("Chunk saved to {0}", sfd.FileName);
            }
        }

        public void RevertChunk()
        {
            ChunkAssetEntry selectedAsset = chunksListBox.SelectedItem as ChunkAssetEntry;
            if (selectedAsset == null)
            {
                logger?.Log("Revert: select a chunk first.");
                return;
            }

            if (!selectedAsset.IsModified)
            {
                logger?.Log("Revert: chunk {0} has not been modified.", selectedAsset.Id);
                return;
            }

            FrostyTaskWindow.Show("Reverting Chunk", "", (task) => { App.AssetManager.RevertAsset(selectedAsset, suppressOnModify: false); });
            RefreshChunksListBox(selectedAsset);
        }

        private void FrostyChunkResEditor_Loaded(object sender, RoutedEventArgs e)
        {
            if (resExplorer.ItemsSource != null)
                return;

            resExplorer.ItemsSource = App.AssetManager.EnumerateRes();
            chunksListBox.ItemsSource = App.AssetManager.EnumerateChunks();
            chunksListBox.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("DisplayName", System.ComponentModel.ListSortDirection.Ascending));
        }

        private void RefreshChunksListBox(ChunkAssetEntry selectedAsset)
        {
            chunksListBox.Items.Refresh();
        }
    }
}
