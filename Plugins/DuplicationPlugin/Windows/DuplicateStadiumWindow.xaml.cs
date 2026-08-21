using Frosty.Controls;
using Frosty.Core;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateStadiumWindow : FrostyDockableWindow
    {
        public string NewFolder { get; private set; }
        public string NewNameOnly { get; private set; }

        private readonly string sourceFolder;
        private readonly string sourceNameOnly;

        public DuplicateStadiumWindow(string inSourceFolder, string inSourceNameOnly)
        {
            InitializeComponent();

            sourceFolder = inSourceFolder;
            sourceNameOnly = inSourceNameOnly;

            sourceFolderTextBox.Text = inSourceFolder;
            sourceNameTextBox.Text = inSourceNameOnly;
            newNameTextBox.Text = inSourceFolder;
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
                FrostyMessageBox.Show("New stadium name cannot be empty.", "Stadium Duplicator");
                return;
            }

            if (newName.Contains("/") || newName.Contains(" "))
            {
                FrostyMessageBox.Show("New stadium name cannot contain spaces or slashes.\nUse the form name_id (e.g. aaaaa_499).", "Stadium Duplicator");
                return;
            }

            int underscore = newName.LastIndexOf('_');
            if (underscore <= 0 || underscore == newName.Length - 1)
            {
                FrostyMessageBox.Show(
                    "New stadium name must be in the form name_id.\nExample: aaaaa_499",
                    "Stadium Duplicator");
                return;
            }

            string idPart = newName.Substring(underscore + 1);
            if (!int.TryParse(idPart, out _))
            {
                FrostyMessageBox.Show(
                    "New stadium name must end with a numeric ID.\nExample: aaaaa_499",
                    "Stadium Duplicator");
                return;
            }

            string newNameOnly = newName.Substring(0, underscore);

            if (newName.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Stadium Duplicator");
                return;
            }

            NewFolder = newName;
            NewNameOnly = newNameOnly;

            DialogResult = true;
            Close();
        }
    }
}
