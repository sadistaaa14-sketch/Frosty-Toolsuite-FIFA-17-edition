using Frosty.Controls;
using Frosty.Core.Bulk;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Frosty.Core.Windows
{
    /// <summary>
    /// Shows only the leaf of a skeleton asset name in the skeleton list, so a long path does not
    /// push the interesting part of the name out of view.
    /// </summary>
    public class LeafNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return BulkSkeletons.LeafName(value as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }

    /// <summary>
    /// Asks what a bulk export should write. Every category is a check box with a line explaining it,
    /// the ebx form is chosen separately for xml and bin, meshes pick one model format, textures pick
    /// any number of image formats, and skinned meshes can be given a skeleton either explicitly or
    /// by letting the asset's folder decide.
    /// </summary>
    public partial class BulkExportOptionsWindow : FrostyDockableWindow
    {
        /// <summary>The chosen options, set once the user pressed OK.</summary>
        public BulkOptions Options { get; private set; }

        private readonly BulkOptions options;
        private readonly BulkScope scope;

        /// <summary>
        /// Creates the dialog, prefilled from <paramref name="inOptions"/>. Only the categories
        /// inside <paramref name="inScope"/> are offered.
        /// </summary>
        public BulkExportOptionsWindow(BulkOptions inOptions, BulkScope inScope)
        {
            InitializeComponent();

            options = inOptions ?? new BulkOptions();
            scope = inScope == BulkScope.None ? BulkScope.All : inScope;

            FillSkeletons();
            LoadOptions();
            ApplyScope();

            // Wire the handlers last so that loading the initial state does not look like a user edit.
            exportEbxCheckBox.Checked += OptionChanged;
            exportEbxCheckBox.Unchecked += OptionChanged;
            exportEbxXmlCheckBox.Checked += OptionChanged;
            exportEbxXmlCheckBox.Unchecked += OptionChanged;
            exportEbxBinCheckBox.Checked += OptionChanged;
            exportEbxBinCheckBox.Unchecked += OptionChanged;
            exportMeshCheckBox.Checked += OptionChanged;
            exportMeshCheckBox.Unchecked += OptionChanged;
            exportTextureCheckBox.Checked += OptionChanged;
            exportTextureCheckBox.Unchecked += OptionChanged;
            exportChunkCheckBox.Checked += OptionChanged;
            exportChunkCheckBox.Unchecked += OptionChanged;
            exportResCheckBox.Checked += OptionChanged;
            exportResCheckBox.Unchecked += OptionChanged;
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
            exportEbxCheckBox.IsChecked = options.Ebx;
            exportEbxXmlCheckBox.IsChecked = options.EbxXml;
            exportEbxBinCheckBox.IsChecked = options.EbxBin;
            exportMeshCheckBox.IsChecked = options.Meshes;
            exportTextureCheckBox.IsChecked = options.Textures;
            exportChunkCheckBox.IsChecked = options.Chunks;
            exportResCheckBox.IsChecked = options.Res;

            SelectMeshFormat(options.MeshFormat);

            List<string> formats = options.TextureFormats ?? new List<string>();
            textureDdsCheckBox.IsChecked = formats.Count == 0 || HasFormat(formats, "dds");
            texturePngCheckBox.IsChecked = HasFormat(formats, "png");
            textureTgaCheckBox.IsChecked = HasFormat(formats, "tga");
            textureHdrCheckBox.IsChecked = HasFormat(formats, "hdr");

            skeletonAutoCheckBox.IsChecked = options.SkeletonAuto;

            string selected = FindSkeleton(options.Skeleton);
            if (selected != null)
                skeletonComboBox.SelectedItem = selected;
        }

        private static bool HasFormat(List<string> formats, string format)
        {
            return formats.Exists(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Selects the mesh format combo entry whose Tag carries the format's name.</summary>
        private void SelectMeshFormat(string format)
        {
            if (string.IsNullOrEmpty(format))
                format = "fbx";

            foreach (object item in meshFormatComboBox.Items)
            {
                if (item is ComboBoxItem entry && string.Equals(entry.Tag as string, format, StringComparison.OrdinalIgnoreCase))
                {
                    meshFormatComboBox.SelectedItem = entry;
                    return;
                }
            }

            meshFormatComboBox.SelectedIndex = 0;
        }

        private string ChosenMeshFormat()
        {
            if (meshFormatComboBox.SelectedItem is ComboBoxItem entry && entry.Tag is string format)
                return format;

            return "fbx";
        }

        /// <summary>
        /// The texture formats to write: everything that is ticked, or .dds when the category is on
        /// with nothing ticked, because a ticked category that writes no image at all would look like
        /// a broken option rather than a choice.
        /// </summary>
        private List<string> ChosenTextureFormats()
        {
            List<string> formats = new List<string>();
            if (textureDdsCheckBox.IsChecked == true)
                formats.Add("dds");
            if (texturePngCheckBox.IsChecked == true)
                formats.Add("png");
            if (textureTgaCheckBox.IsChecked == true)
                formats.Add("tga");
            if (textureHdrCheckBox.IsChecked == true)
                formats.Add("hdr");

            if (formats.Count == 0)
                formats.Add("dds");

            return formats;
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
            bool ebx = exportEbxCheckBox.IsChecked == true;
            exportEbxXmlCheckBox.IsEnabled = ebx;
            exportEbxBinCheckBox.IsEnabled = ebx;

            bool mesh = exportMeshCheckBox.IsChecked == true;
            meshFormatComboBox.IsEnabled = mesh;

            bool texture = exportTextureCheckBox.IsChecked == true;
            textureDdsCheckBox.IsEnabled = texture;
            texturePngCheckBox.IsEnabled = texture;
            textureTgaCheckBox.IsEnabled = texture;
            textureHdrCheckBox.IsEnabled = texture;

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
                if (exportEbxCheckBox.IsChecked == true &&
                    (exportEbxXmlCheckBox.IsChecked == true || exportEbxBinCheckBox.IsChecked == true))
                    return true;

                if (exportMeshCheckBox.IsChecked == true || exportTextureCheckBox.IsChecked == true)
                    return true;
            }

            if ((scope & BulkScope.Chunks) != 0 && exportChunkCheckBox.IsChecked == true)
                return true;

            if ((scope & BulkScope.Res) != 0 && exportResCheckBox.IsChecked == true)
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
            options.Ebx = exportEbxCheckBox.IsChecked == true;
            options.EbxXml = exportEbxXmlCheckBox.IsChecked == true;
            options.EbxBin = exportEbxBinCheckBox.IsChecked == true;
            options.Meshes = exportMeshCheckBox.IsChecked == true;
            options.Textures = exportTextureCheckBox.IsChecked == true;
            options.Chunks = exportChunkCheckBox.IsChecked == true;
            options.Res = exportResCheckBox.IsChecked == true;

            options.MeshFormat = ChosenMeshFormat();
            options.TextureFormats = ChosenTextureFormats();

            options.SkeletonAuto = skeletonAutoCheckBox.IsChecked == true;
            options.Skeleton = (skeletonComboBox.SelectedItem as string) ?? "";

            Options = options;
            DialogResult = true;
            Close();
        }
    }
}
