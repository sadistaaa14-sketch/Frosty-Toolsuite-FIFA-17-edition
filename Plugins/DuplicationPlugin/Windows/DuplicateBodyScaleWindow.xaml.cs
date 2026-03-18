using Frosty.Controls;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateBodyScaleWindow : FrostyDockableWindow
    {
        public string NewBodyScaleName { get; private set; }

        private readonly string sourceName;

        public DuplicateBodyScaleWindow(string sourceFolder, string inSourceName)
        {
            InitializeComponent();

            sourceName = inSourceName;
            sourceFolderTextBox.Text = sourceFolder + "/" + inSourceName;
            newNameTextBox.Text = inSourceName;
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
                FrostyMessageBox.Show("New name cannot be empty.", "Body Scale Duplicator");
                return;
            }

            if (newName.Contains("//") || newName.Contains(" ") || newName.Contains("/"))
            {
                FrostyMessageBox.Show("Name contains invalid characters.", "Body Scale Duplicator");
                return;
            }

            if (!newName.StartsWith("bbscale_", StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show(
                    "Name must start with 'bbscale_'.\nExample: bbscale_0_0_99",
                    "Body Scale Duplicator");
                return;
            }

            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Body Scale Duplicator");
                return;
            }

            NewBodyScaleName = newName;

            DialogResult = true;
            Close();
        }
    }
}