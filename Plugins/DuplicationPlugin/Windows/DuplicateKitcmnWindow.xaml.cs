using Frosty.Controls;
using Frosty.Core;
using System;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class DuplicateKitcmnWindow : FrostyDockableWindow
    {
        public string NewName { get; private set; }

        private readonly string sourceName;

        public DuplicateKitcmnWindow(string inSourceFull)
        {
            InitializeComponent();

            sourceTextBox.Text = inSourceFull;
            sourceName = inSourceFull.Substring(inSourceFull.LastIndexOf('/') + 1);
            newNameTextBox.Text = sourceName;
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
                FrostyMessageBox.Show("New name cannot be empty.", "Kit Common Duplicator");
                return;
            }

            if (newName.Contains("/") || newName.Contains(" "))
            {
                FrostyMessageBox.Show("Name contains invalid characters.", "Kit Common Duplicator");
                return;
            }

            if (newName.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show("New name must be different from the source.", "Kit Common Duplicator");
                return;
            }

            NewName = newName;
            DialogResult = true;
            Close();
        }
    }
}
