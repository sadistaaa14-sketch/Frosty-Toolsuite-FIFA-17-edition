using Frosty.Controls;
using Frosty.Core.Bulk;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Frosty.Core.Windows
{
    /// <summary>
    /// Asks what a bulk import should read in. It mirrors the export dialog so the two are easy to
    /// keep in step: the same categories and the same skeleton choice, described the other way round.
    /// There are no format pickers here on purpose - import reads every extension it understands and
    /// matches files to assets by name, so a format choice would do nothing.
    /// </summary>
    public partial class BulkImportOptionsWindow : FrostyDockableWindow
    {
        /// <summary>The chosen options, set once the user pressed OK.</summary>
        public BulkOptions Options { get; private set; }

        private readonly BulkOptions options;
        private readonly BulkScope scope;

        /// <summary>
        /// Creates the dialog, prefilled from <paramref name="inOptions"/>. Only the categories
        /// inside <paramref name="inScope"/> are offered.
        /// </summary>
        public BulkImportOptionsWindow(BulkOptions inOptions, BulkScope inScope)
        {
            InitializeComponent();

            options = inOptions ?? new BulkOptions();
            scope = inScope == BulkScope.None ? BulkScope.All : inScope;

            FillSkeletons();
            LoadOptions();
            ApplyScope();

            // Wire the handlers last so that loading the initial state does not look like a user edit.
            importEbxCheckBox.Checked += OptionChanged;
            importEbxCheckBox.Unchecked += OptionChanged;
            importEbxXmlCheckBox.Checked += OptionChanged;
            importEbxXmlCheckBox.Unchecked += OptionChanged;
            importEbxBinCheckBox.Checked += OptionChanged;
            importEbxBinCheckBox.Unchecked += OptionChanged;
            importMeshCheckBox.Checked += OptionChanged;
            importMeshCheckBox.Unchecked += OptionChanged;
            importTextureCheckBox.Checked += OptionChanged;
            importTextureCheckBox.Unchecked += OptionChanged;
            importChunkCheckBox.Checked += OptionChanged;
            importChunkCheckBox.Unchecked += OptionChanged;
            importResCheckBox.Checked += OptionChanged;
            importResCheckBox.Unchecked += OptionChanged;
            skeletonAutoCheckBox.Checked += OptionChanged;
            skeletonAutoCheckBox.Unchecked += OptionChanged;

            UpdateEnabledState();
        }

        private void FillSkeletons()
        {
            List<string> names = new List<string>();
            try
            {
                names = BulkSkeletons.AllNames();
            }
            catch
            {
                // No game loaded yet - the dialog still works, the skeleton list is simply empty.
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            skeletonComboBox.ItemsSource = names;
        }

        private void LoadOptions()
        {
            importEbxCheckBox.IsChecked = options.Ebx;
            importEbxXmlCheckBox.IsChecked = options.EbxXml;
            importEbxBinCheckBox.IsChecked = options.EbxBin;
            importMeshCheckBox.IsChecked = options.Meshes;
            importTextureCheckBox.IsChecked = options.Textures;
            importChunkCheckBox.IsChecked = options.Chunks;
            importResCheckBox.IsChecked = options.Res;

            skeletonAutoCheckBox.IsChecked = options.SkeletonAuto;

            string selected = FindSkeleton(options.Skeleton);
            if (selected != null)
                skeletonComboBox.SelectedItem = selected;
        }

        /// <summary>Returns the entry of the skeleton list that matches <paramref name="name"/>, or null.</summary>
        private string FindSkeleton(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            foreach (object item in skeletonComboBox.Items)
            {
                string candidate = item as string;
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Hides the categories this dialog was not opened for. The scope names exactly what the
        /// command can act on: the asset tree offers everything, the Chunk/Res Editor's chunk list
        /// offers chunks and its res tree offers resources.
        /// </summary>
        private void ApplyScope()
        {
            bool assets = (scope & BulkScope.Assets) != 0;

            Collapse(ebxRow, assets);
            Collapse(meshRow, assets);
            Collapse(textureRow, assets);
            Collapse(skeletonRow, assets);
            Collapse(chunkRow, (scope & BulkScope.Chunks) != 0);
            Collapse(resRow, (scope & BulkScope.Res) != 0);
        }

        private static void Collapse(UIElement element, bool visible)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OptionChanged(object sender, RoutedEventArgs e)
        {
            UpdateEnabledState();
        }

        private void UpdateEnabledState()
        {
            bool ebx = importEbxCheckBox.IsChecked == true;
            importEbxXmlCheckBox.IsEnabled = ebx;
            importEbxBinCheckBox.IsEnabled = ebx;

            bool auto = skeletonAutoCheckBox.IsChecked == true;
            skeletonComboBox.IsEnabled = !auto;
            if (!auto && skeletonComboBox.SelectedItem == null && skeletonComboBox.Items.Count != 0)
                skeletonComboBox.SelectedItem = skeletonComboBox.Items[0];

            okButton.IsEnabled = AnyChecked();
        }

        /// <summary>
        /// True when at least one category inside the scope is ticked, which is what makes the OK
        /// button meaningful.
        /// </summary>
        private bool AnyChecked()
        {
            if ((scope & BulkScope.Assets) != 0)
            {
                if (importEbxCheckBox.IsChecked == true &&
                    (importEbxXmlCheckBox.IsChecked == true || importEbxBinCheckBox.IsChecked == true))
                    return true;

                if (importMeshCheckBox.IsChecked == true || importTextureCheckBox.IsChecked == true)
                    return true;
            }

            if ((scope & BulkScope.Chunks) != 0 && importChunkCheckBox.IsChecked == true)
                return true;

            if ((scope & BulkScope.Res) != 0 && importResCheckBox.IsChecked == true)
                return true;

            return false;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            options.Ebx = importEbxCheckBox.IsChecked == true;
            options.EbxXml = importEbxXmlCheckBox.IsChecked == true;
            options.EbxBin = importEbxBinCheckBox.IsChecked == true;
            options.Meshes = importMeshCheckBox.IsChecked == true;
            options.Textures = importTextureCheckBox.IsChecked == true;
            options.Chunks = importChunkCheckBox.IsChecked == true;
            options.Res = importResCheckBox.IsChecked == true;

            options.SkeletonAuto = skeletonAutoCheckBox.IsChecked == true;
            options.Skeleton = (skeletonComboBox.SelectedItem as string) ?? "";

            Options = options;
            DialogResult = true;
            Close();
        }
    }
}
