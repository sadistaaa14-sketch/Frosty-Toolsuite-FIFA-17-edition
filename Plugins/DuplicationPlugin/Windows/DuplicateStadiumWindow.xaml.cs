using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateStadiumWindow : FrostyDockableWindow
    {
        public string NewFolder { get; private set; }
        public string NewNameOnly { get; private set; }
        public bool SharePitch { get; private set; }

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

            // content/worlds/components/stadiums/<name> and content/effects/glares/<name>
            // are named by the part before the id. With the same name part the copies of
            // those assets would get the source's own names, and AssetManager.AddEbx hands
            // back the existing source entry instead of a new asset.
            if (newNameOnly.Equals(sourceNameOnly, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show(
                    "The name part before the id must differ from the source's (\"" + sourceNameOnly + "\").\n" +
                    "components/stadiums/<name> and effects/glares/<name> are named by it.\n" +
                    "Example: " + sourceNameOnly + "b_" + idPart,
                    "Stadium Duplicator");
                return;
            }

            if (StadiumAssetsExist(newName, newNameOnly))
            {
                FrostyMessageBox.Show(
                    "Assets for \"" + newName + "\" already exist (content/worlds/stadiums/" + newName +
                    ", content/worlds/components/stadiums/" + newNameOnly + " or content/effects/glares/" + newNameOnly + ").\n" +
                    "Pick another name.",
                    "Stadium Duplicator");
                return;
            }

            NewFolder = newName;
            NewNameOnly = newNameOnly;
            SharePitch = sharePitchCheckBox.IsChecked == true;

            DialogResult = true;
            Close();
        }

        private static bool StadiumAssetsExist(string folder, string nameOnly)
        {
            string[] prefixes =
            {
                "content/worlds/stadiums/" + folder.ToLower() + "/",
                "content/worlds/components/stadiums/" + nameOnly.ToLower() + "/",
                "content/effects/glares/" + nameOnly.ToLower() + "/"
            };
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                string name = entry.Name.Replace('\\', '/').ToLower();
                foreach (string prefix in prefixes)
                {
                    if (name.StartsWith(prefix))
                        return true;
                }
            }
            return false;
        }
    }
}
