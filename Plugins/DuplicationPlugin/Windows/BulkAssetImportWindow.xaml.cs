using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;
using System.Windows.Forms;

namespace DuplicationPlugin.Windows
{
    public partial class BulkAssetImportWindow : FrostyDockableWindow
    {
        public string RootFolder { get; private set; }
        public string BaseFolder { get; private set; }
        public string Skeleton { get; private set; }

        private readonly string defaultBaseFolder;
        private readonly bool skeletonPerFolder;
        private string baseFolderName;
        private string skeletonName;

        public BulkAssetImportWindow(string assetTypeName, string inDefaultBaseFolder, string inDefaultSkeleton, bool inSkeletonPerFolder)
        {
            InitializeComponent();

            defaultBaseFolder = inDefaultBaseFolder;
            skeletonPerFolder = inSkeletonPerFolder;
            Title = "Bulk Import " + assetTypeName;

            baseFolderName = inDefaultBaseFolder ?? "";
            baseTextBox.Text = LeafName(baseFolderName);

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
        }

        private static string LeafName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            int idx = name.LastIndexOf('/');
            return idx < 0 ? name : name.Substring(idx + 1);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "Select the root folder containing the exported assets to import."
            };
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                localFolderTextBox.Text = fbd.SelectedPath;
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

        private void BaseBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FolderPickerWindow picker = new FolderPickerWindow("Select Base Folder", baseFolderName);
            if (picker.ShowDialog() == true)
            {
                baseFolderName = picker.SelectedFolder;
                baseTextBox.Text = LeafName(baseFolderName);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            string root = (localFolderTextBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(root))
            {
                FrostyMessageBox.Show("Select the local folder containing the assets to import.", "Bulk Import");
                return;
            }

            if (string.IsNullOrEmpty(baseFolderName))
            {
                FrostyMessageBox.Show("Select a base folder to use as the duplication template.", "Bulk Import");
                return;
            }

            RootFolder = root;
            BaseFolder = baseFolderName;
            Skeleton = skeletonName;

            DialogResult = true;
            Close();
        }
    }
}
