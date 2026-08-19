using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers;
using System.Windows;

namespace DuplicationPlugin.Windows
{
    public partial class SkeletonPickerWindow : FrostyDockableWindow
    {
        public string SelectedSkeleton { get; private set; }

        public SkeletonPickerWindow()
        {
            InitializeComponent();
            skeletonSelector.ItemsSource = App.AssetManager.EnumerateEbx("SkeletonAsset");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            EbxAssetEntry selected = skeletonSelector.SelectedAsset as EbxAssetEntry;
            if (selected == null)
            {
                FrostyMessageBox.Show("Select a skeleton asset in the tree.", "Select Skeleton");
                return;
            }

            SelectedSkeleton = selected.Name;
            DialogResult = true;
            Close();
        }
    }
}
