using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class FolderPickerWindow : FrostyDockableWindow
    {
        public string SelectedFolder { get; private set; }

        private readonly string defaultFolder;

        public FolderPickerWindow(string title, string inDefaultFolder)
        {
            InitializeComponent();

            Title = title;
            defaultFolder = inDefaultFolder;
            folderSelector.ItemsSource = App.AssetManager.EnumerateEbx();
        }

        private void FrostyDockableWindow_FrostyLoaded(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(defaultFolder))
                return;

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Path.Replace('\\', '/').Equals(defaultFolder, StringComparison.OrdinalIgnoreCase))
                {
                    folderSelector.SelectAsset(entry);
                    break;
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = (folderSelector.SelectedPath ?? "").Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(folder))
            {
                FrostyMessageBox.Show("Select a folder in the tree.", "Select Base Folder");
                return;
            }

            SelectedFolder = folder;
            DialogResult = true;
            Close();
        }
    }
}
