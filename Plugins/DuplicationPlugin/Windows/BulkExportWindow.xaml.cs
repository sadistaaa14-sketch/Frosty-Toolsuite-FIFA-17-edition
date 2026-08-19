using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;
using System.Windows.Forms;

namespace DuplicationPlugin.Windows
{
    public partial class BulkExportWindow : FrostyDockableWindow
    {
        public string OutputFolder { get; private set; }
        public string BaseFolder { get; private set; }
        public string Skeleton { get; private set; }

        private readonly string defaultBaseFolder;
        private string skeletonName;

        public BulkExportWindow(string assetTypeName, string inDefaultBaseFolder, string inDefaultSkeleton, bool skeletonPerFolder)
        {
            InitializeComponent();

            defaultBaseFolder = inDefaultBaseFolder;
            Title = "Bulk Export " + assetTypeName;
            baseHintLabel.Content = "All folders of this type under the selected folder are exported (layout preserved).";
            skeletonName = inDefaultSkeleton ?? "";

            if (skeletonPerFolder)
            {
                skeletonBrowseButton.IsEnabled = false;
                skeletonTextBox.Text = "(per-folder — each trophy uses its own skeleton)";
            }
            else
            {
                skeletonTextBox.Text = LeafName(skeletonName);
            }

            pathSelector.ItemsSource = App.AssetManager.EnumerateEbx();
        }

        private static string LeafName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            int idx = name.LastIndexOf('/');
            return idx < 0 ? name : name.Substring(idx + 1);
        }

        private void FrostyDockableWindow_FrostyLoaded(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(defaultBaseFolder))
                return;

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Path.Replace('\\', '/').Equals(defaultBaseFolder, StringComparison.OrdinalIgnoreCase))
                {
                    pathSelector.SelectAsset(entry);
                    break;
                }
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "Select the local folder to export into."
            };
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                outputFolderTextBox.Text = fbd.SelectedPath;
        }

        private void SkeletonBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            SkeletonPickerWindow picker = new SkeletonPickerWindow();
            if (picker.ShowDialog() == true)
            {
                skeletonName = picker.SelectedSkeleton;
                skeletonTextBox.Text = LeafName(skeletonName);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            string output = (outputFolderTextBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(output))
            {
                FrostyMessageBox.Show("Select a local output folder.", "Bulk Export");
                return;
            }

            string baseFolder = (pathSelector.SelectedPath ?? "").Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(baseFolder))
            {
                FrostyMessageBox.Show("Select a base asset folder in the tree.", "Bulk Export");
                return;
            }

            OutputFolder = output;
            BaseFolder = baseFolder;
            Skeleton = skeletonName;

            DialogResult = true;
            Close();
        }
    }
}
