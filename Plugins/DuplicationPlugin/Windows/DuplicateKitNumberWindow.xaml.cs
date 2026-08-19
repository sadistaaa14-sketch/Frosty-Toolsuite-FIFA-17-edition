using Frosty.Controls;
using Frosty.Core;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateKitNumberWindow : FrostyDockableWindow
    {
        public string NewFolderName { get; private set; }

        private readonly string sourceFolderName;

        public DuplicateKitNumberWindow(string inSourceFolder)
        {
            InitializeComponent();

            sourceFolderTextBox.Text = inSourceFolder;
            sourceFolderName = inSourceFolder.Substring(inSourceFolder.LastIndexOf('/') + 1);
            newFolderTextBox.Text = sourceFolderName;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            string newFolder = newFolderTextBox.Text.Replace('\\', '/').Trim('/').Trim();

            if (string.IsNullOrEmpty(newFolder))
            {
                FrostyMessageBox.Show("New folder name cannot be empty.", "Kit Number Duplicator");
                return;
            }

            if (newFolder.Contains("/") || newFolder.Contains(" "))
            {
                FrostyMessageBox.Show("Folder name contains invalid characters.", "Kit Number Duplicator");
                return;
            }

            int lastUnderscore = newFolder.LastIndexOf('_');
            if (lastUnderscore < 0)
            {
                FrostyMessageBox.Show(
                    "New folder name must use the format string_number.\nExample: a_league_99",
                    "Kit Number Duplicator");
                return;
            }

            string idPart = newFolder.Substring(lastUnderscore + 1);
            int dummy;
            if (!int.TryParse(idPart, out dummy))
            {
                FrostyMessageBox.Show(
                    "New folder name must use the format string_number.\nExample: a_league_99",
                    "Kit Number Duplicator");
                return;
            }

            if (newFolder.Equals(sourceFolderName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New folder name must be different from the source.", "Kit Number Duplicator");
                return;
            }

            NewFolderName = newFolder;
            DialogResult = true;
            Close();
        }
    }
}
