using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using System.Collections.Generic;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    /// <summary>
    /// Shared bulk-import dialog used by every "Bulk Duplicate ..." menu extension.
    /// The caller supplies a title/asset-type-name and an example line to show in the
    /// placeholder text; the dialog returns a validated list of <see cref="BulkImportRow"/>.
    /// </summary>
    public partial class BulkImportWindow : FrostyDockableWindow
    {
        public List<BulkImportRow> Rows { get; private set; } = new List<BulkImportRow>();

        private readonly string assetTypeName;

        public BulkImportWindow(string inAssetTypeName, string exampleLine)
        {
            InitializeComponent();

            assetTypeName = inAssetTypeName;
            Title = "Bulk Duplicate " + inAssetTypeName;

            importTextBox.Text =
                "# One row per " + inAssetTypeName.ToLower() + " to duplicate.\r\n" +
                "# Format: SourcePath,NewName,DestPath   (DestPath is optional)\r\n" +
                "# Example:\r\n" +
                "# " + exampleLine + "\r\n";
        }

        private void LoadFileButton_Click(object sender, RoutedEventArgs e)
        {
            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog(
                "Load Bulk Import File",
                "CSV/Text Files (*.csv;*.txt)|*.csv;*.txt|All Files (*.*)|*.*",
                "BulkDuplicate" + assetTypeName);

            if (ofd.ShowDialog())
            {
                importTextBox.Text = System.IO.File.ReadAllText(ofd.FileName);
                ValidateInternal();
            }
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            ValidateInternal();
        }

        private bool ValidateInternal()
        {
            List<string> errors;
            List<BulkImportRow> rows = BulkImportParser.Parse(importTextBox.Text, out errors);

            errorListBox.ItemsSource = errors;
            rowCountText.Text = rows.Count + " valid row(s)" + (errors.Count > 0 ? ", " + errors.Count + " error(s)" : "");

            Rows = rows;
            return errors.Count == 0 && rows.Count > 0;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInternal())
            {
                if (Rows.Count == 0)
                    FrostyMessageBox.Show("No valid rows to import. Add at least one line in the format 'SourcePath,NewName,DestPath'.", "Bulk Duplicate " + assetTypeName);
                else
                    FrostyMessageBox.Show("Fix the errors listed below before starting (or remove the offending lines).", "Bulk Duplicate " + assetTypeName);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
