using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateKitWindow : FrostyDockableWindow
    {
        public string NewFolderName { get; private set; }
        public string DestinationPath { get; private set; }

        private readonly string sourceFolder;

        public DuplicateKitWindow(string inSourceFolder)
        {
            InitializeComponent();

            sourceFolder = inSourceFolder;
            sourceFolderTextBox.Text = inSourceFolder;

            string sourceName = inSourceFolder.Substring(inSourceFolder.LastIndexOf('/') + 1);
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
                FrostyMessageBox.Show("New folder name cannot be empty.", "Kit Duplicator");
                return;
            }

            if (newName.Contains("//") || newName.Contains(" "))
            {
                FrostyMessageBox.Show("Name contains invalid characters.", "Kit Duplicator");
                return;
            }

            string sourceName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Kit Duplicator");
                return;
            }

            string destPath = pathSelector.SelectedPath;
            if (string.IsNullOrEmpty(destPath))
            {
                FrostyMessageBox.Show("Select a destination folder in the tree.", "Kit Duplicator");
                return;
            }

            NewFolderName = newName;
            DestinationPath = destPath.Replace('\\', '/');

            DialogResult = true;
            Close();
        }
    }
}
