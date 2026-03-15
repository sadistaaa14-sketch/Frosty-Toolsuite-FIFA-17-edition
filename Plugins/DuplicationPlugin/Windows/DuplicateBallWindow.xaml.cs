using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateBallWindow : FrostyDockableWindow
    {
        public string NewBallName { get; private set; }
        public string DestinationPath { get; private set; }

        private readonly string sourceFolder;

        public DuplicateBallWindow(string inSourceFolder)
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
                FrostyMessageBox.Show("New ball name cannot be empty.", "Ball Duplicator");
                return;
            }

            if (newName.Contains("//") || newName.Contains(" "))
            {
                FrostyMessageBox.Show("Name contains invalid characters.", "Ball Duplicator");
                return;
            }

            int lastUnderscore = newName.LastIndexOf('_');
            if (lastUnderscore < 0)
            {
                FrostyMessageBox.Show(
                    "New name must end with a numeric ID.\nExample: ball_999",
                    "Ball Duplicator");
                return;
            }

            string idPart = newName.Substring(lastUnderscore + 1);
            int dummy;
            if (!int.TryParse(idPart, out dummy))
            {
                FrostyMessageBox.Show(
                    "New name must end with a numeric ID.\nExample: ball_999",
                    "Ball Duplicator");
                return;
            }

            string sourceName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Ball Duplicator");
                return;
            }

            string destPath = pathSelector.SelectedPath;
            if (string.IsNullOrEmpty(destPath))
            {
                FrostyMessageBox.Show("Select a destination folder in the tree.", "Ball Duplicator");
                return;
            }

            NewBallName = newName;
            DestinationPath = destPath.Replace('\\', '/');

            DialogResult = true;
            Close();
        }
    }
}
