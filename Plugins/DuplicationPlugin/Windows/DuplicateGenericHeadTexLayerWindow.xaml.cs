using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateGenericHeadTexLayerWindow : FrostyDockableWindow
    {
        public string NewName { get; private set; }
        public string DestinationPath { get; private set; }

        private readonly string sourceFull;
        private readonly string sourceFolder;

        public DuplicateGenericHeadTexLayerWindow(string inSourceFull)
        {
            InitializeComponent();

            sourceFull = inSourceFull;
            sourceFolderTextBox.Text = inSourceFull;

            int slash = inSourceFull.LastIndexOf('/');
            sourceFolder = slash > 0 ? inSourceFull.Substring(0, slash) : inSourceFull;

            string sourceName = inSourceFull.Substring(slash + 1);
            newNameTextBox.Text = sourceName;

            pathSelector.ItemsSource = App.AssetManager.EnumerateEbx();
        }

        private void FrostyDockableWindow_FrostyLoaded(object sender, EventArgs e)
        {
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Path.Replace('\\', '/').Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                {
                    pathSelector.SelectAsset(entry);
                    break;
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = newNameTextBox.Text.Replace('\\', '/').Trim('/').Trim();

            if (string.IsNullOrEmpty(newName))
            {
                FrostyMessageBox.Show("New name cannot be empty.", "Generic Head Tex Layer Duplicator");
                return;
            }

            if (newName.Contains("//") || newName.Contains(" ") || newName.Contains("/"))
            {
                FrostyMessageBox.Show("Name contains invalid characters.", "Generic Head Tex Layer Duplicator");
                return;
            }

            string sourceName = sourceFull.Substring(sourceFull.LastIndexOf('/') + 1);
            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Generic Head Tex Layer Duplicator");
                return;
            }

            string destPath = pathSelector.SelectedPath;
            if (string.IsNullOrEmpty(destPath))
            {
                FrostyMessageBox.Show("Select a destination folder in the tree.", "Generic Head Tex Layer Duplicator");
                return;
            }

            NewName = newName;
            DestinationPath = destPath.Replace('\\', '/');

            DialogResult = true;
            Close();
        }
    }
}
