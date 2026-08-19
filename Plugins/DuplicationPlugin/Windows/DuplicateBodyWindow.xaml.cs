using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateBodyWindow : FrostyDockableWindow
    {
        public string NewName { get; private set; }
        public string NewFolderName { get; private set; }
        public string DestinationPath { get; private set; }
        public string ClothPrefix { get; private set; }

        private readonly string sourceFolder;

        public DuplicateBodyWindow(string inSourceFolder, string inClothPrefix,
            string sourceDisplay = null, string defaultNewName = null, string defaultNewFolder = null)
        {
            InitializeComponent();

            sourceFolder = inSourceFolder;
            sourceFolderTextBox.Text = sourceDisplay ?? inSourceFolder;

            string sourceName = inSourceFolder.Substring(inSourceFolder.LastIndexOf('/') + 1);
            newNameTextBox.Text = defaultNewName ?? sourceName;
            newFolderNameTextBox.Text = defaultNewFolder ?? sourceName;
            clothPrefixTextBox.Text = inClothPrefix ?? "";

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
            string newFolderName = newFolderNameTextBox.Text.Replace('\\', '/').Trim('/').Trim();

            if (string.IsNullOrEmpty(newName))
            {
                FrostyMessageBox.Show("New name cannot be empty.", "Body Duplicator");
                return;
            }

            if (newName.Contains("//") || newName.Contains(" ") || newName.Contains("/"))
            {
                FrostyMessageBox.Show("New name contains invalid characters.", "Body Duplicator");
                return;
            }

            if (!string.IsNullOrEmpty(newFolderName) &&
                (newFolderName.Contains("//") || newFolderName.Contains(" ") || newFolderName.Contains("/")))
            {
                FrostyMessageBox.Show("New folder contains invalid characters.", "Body Duplicator");
                return;
            }

            string sourceName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Body Duplicator");
                return;
            }

            string destPath = pathSelector.SelectedPath;
            if (string.IsNullOrEmpty(destPath))
            {
                FrostyMessageBox.Show("Select a destination folder in the tree.", "Body Duplicator");
                return;
            }

            NewName = newName;
            NewFolderName = string.IsNullOrEmpty(newFolderName) ? newName : newFolderName;
            DestinationPath = destPath.Replace('\\', '/');
            ClothPrefix = clothPrefixTextBox.Text.Trim();

            DialogResult = true;
            Close();
        }
    }
}
