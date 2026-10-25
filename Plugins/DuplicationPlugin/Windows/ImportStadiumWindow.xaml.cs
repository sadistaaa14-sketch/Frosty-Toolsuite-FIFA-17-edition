using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DuplicationPlugin.Windows
{
    /// <summary>One package mesh and the stadium mesh it replaces.</summary>
    public class ImportSlotRow : INotifyPropertyChanged
    {
        public const string Skip = "(skip)";
        /// <summary>Target prefix: the importer creates this component mesh (copy of the slot's template mesh) in the slot's sublevel.</summary>
        public const string NewPrefix = "(new) ";

        private string selectedTarget = Skip;

        public string SlotName { get; set; }
        public string Info { get; set; }
        public List<string> Candidates { get; set; } = new List<string>();
        /// <summary>Name (without folder) of the component mesh the importer can create for this slot; null when the slot has no host.</summary>
        public string NewMeshName { get; set; }
        public ObservableCollection<string> Targets { get; } = new ObservableCollection<string>();
        public string SelectedTarget
        {
            get => selectedTarget;
            set
            {
                if (selectedTarget != value)
                {
                    selectedTarget = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTarget)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class ImportStadiumWindow : FrostyDockableWindow
    {
        public StadiumImportSettings Settings { get; private set; }

        private readonly ObservableCollection<ImportSlotRow> rows = new ObservableCollection<ImportSlotRow>();
        private readonly List<KeyValuePair<string, string>> stadiums;   // folder -> name part
        private List<EbxAssetEntry> componentMeshes = new List<EbxAssetEntry>();
        private PesStadiumPackage package;
        private bool updatingStadium;

        public ImportStadiumWindow(string stadiumFolder, string stadiumNameOnly)
        {
            InitializeComponent();

            slotListView.ItemsSource = rows;

            stadiums = ImportStadiumMenuExtension.ListStadiums();
            foreach (KeyValuePair<string, string> kv in stadiums)
                stadiumComboBox.Items.Add(kv.Key + "   (components: " + kv.Value + ")");
            int preselect = -1;
            if (!string.IsNullOrEmpty(stadiumFolder))
                preselect = stadiums.FindIndex(kv => kv.Key.Equals(stadiumFolder, StringComparison.OrdinalIgnoreCase));
            if (preselect < 0 && stadiums.Count > 0)
                preselect = 0;
            if (preselect >= 0)
                stadiumComboBox.SelectedIndex = preselect;

            string last = Config.Get<string>("PesStadiumImportPackage", "", ConfigScope.Game);
            if (!string.IsNullOrEmpty(last) && File.Exists(last))
            {
                packageTextBox.Text = last;
                LoadPackage(last);
            }
        }

        private string CurrentStadiumFolder => stadiumComboBox.SelectedIndex >= 0 && stadiumComboBox.SelectedIndex < stadiums.Count ? stadiums[stadiumComboBox.SelectedIndex].Key : null;
        private string CurrentStadiumNameOnly => stadiumComboBox.SelectedIndex >= 0 && stadiumComboBox.SelectedIndex < stadiums.Count ? stadiums[stadiumComboBox.SelectedIndex].Value : null;
        /// <summary>The resolved component folder of the selected stadium (content/worlds/components/stadiums/&lt;this&gt;/).</summary>
        private string componentFolder;

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog("Open converted stadium package", "Stadium package (stadium.json)|stadium.json|JSON files (*.json)|*.json", "PesStadiumPackage");
            if (ofd.ShowDialog())
            {
                packageTextBox.Text = ofd.FileName;
                LoadPackage(ofd.FileName);
            }
        }

        private void PackageTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string path = packageTextBox.Text.Trim();
            if (File.Exists(path) && (package == null || !string.Equals(package.Directory, Path.GetDirectoryName(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase)))
                LoadPackage(path);
        }

        private void LoadPackage(string path)
        {
            try
            {
                package = PesStadiumPackage.Load(path);
            }
            catch (Exception ex)
            {
                package = null;
                rows.Clear();
                packageInfoText.Text = "Not a converted stadium package: " + ex.Message;
                return;
            }

            int sections = package.Slots.Sum(s => s.Sections.Count);
            int verts = package.Slots.Sum(s => s.Vertices);
            int tris = package.Slots.Sum(s => s.Triangles);
            crowdCheckBox.IsEnabled = package.Crowd != null;
            crowdCheckBox.IsChecked = package.Crowd != null;
            crowdCheckBox.Content = package.Crowd != null
                ? string.Format("Crowd: rewrite the stadium's crowd point cloud with the package's {0:N0} spectator positions (home/away/attendance pattern copied from the base cloud)", package.Crowd.Count)
                : "Crowd: the package has no crowd.bin (convert again with the current converter)";
            physicsCheckBox.IsEnabled = package.Physics.Count > 0;
            physicsCheckBox.IsChecked = package.Physics.Count > 0;
            physicsCheckBox.Content = package.Physics.Count > 0
                ? string.Format("Collision: replace the stadium's ball-collision data ({0})", string.Join(", ", package.Physics.Select(x => x.Material + " " + x.Triangles + " triangles")))
                : "Collision: the package has no physics/*.bin";
            packageInfoText.Text = string.Format("{0} {1} ({2}): {3} meshes, {4} sections, {5:N0} vertices, {6:N0} triangles, {7} textures, written {8}",
                package.Game, package.StadiumName, package.StadiumId, package.Slots.Count, sections, verts, tris, package.Textures.Count, package.Created.ToString("yyyy-MM-dd HH:mm"));
            if (package.Warnings.Count > 0)
                packageInfoText.Text += string.Format("; {0} converter warning(s)", package.Warnings.Count);

            rows.Clear();
            foreach (PesStadiumPackage.SlotInfo slot in package.Slots)
            {
                ImportSlotRow row = new ImportSlotRow
                {
                    SlotName = slot.Name,
                    Info = string.Format("{0} sections, {1:N0} vertices, {2:N0} triangles{3}", slot.Sections.Count, slot.Vertices, slot.Triangles,
                        slot.Overlay ? (slot.HostSublevel != "" ? ", shown by the " + slot.HostSublevel + " sublevel" : ", always shown") : ""),
                    Candidates = new List<string>(slot.FifaMeshCandidates),
                    NewMeshName = slot.CanCreateMesh ? slot.NewMeshName : null
                };
                rows.Add(row);
            }
            FillTargets();
        }

        private void StadiumComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingStadium)
                return;
            updatingStadium = true;
            try
            {
                string nameOnly = CurrentStadiumNameOnly;
                // the component folder follows the level's own mesh paths (the training levels share another stadium's folder)
                if (nameOnly != null && CurrentStadiumFolder != null)
                {
                    string resolved = StadiumBaseLookup.ResolveComponentFolder(CurrentStadiumFolder, nameOnly);
                    if (!string.Equals(resolved, nameOnly, StringComparison.OrdinalIgnoreCase))
                        App.Logger.Log("[PES Import] {0}: component folder {1} (not {2})", CurrentStadiumFolder, resolved, nameOnly);
                    nameOnly = resolved;
                }
                componentFolder = nameOnly;
                componentMeshes = nameOnly != null ? ImportStadiumMenuExtension.ListComponentMeshes(nameOnly) : new List<EbxAssetEntry>();
                if (nameOnly != null)
                {
                    textureFolderTextBox.Text = "content/worlds/textures/pes/" + nameOnly.ToLower();
                    string opaque, alpha, glass;
                    ImportStadiumMenuExtension.DiscoverPresets(nameOnly, out opaque, out alpha, out glass);
                    opaquePresetTextBox.Text = opaque ?? StadiumImporter.DefaultOpaquePreset;
                    alphaPresetTextBox.Text = alpha ?? StadiumImporter.DefaultAlphaTestPreset;
                    glassPresetTextBox.Text = glass ?? StadiumImporter.DefaultGlassPreset;
                }
                FillTargets();
            }
            finally
            {
                updatingStadium = false;
            }
        }

        /// <summary>The target choices of every row: the stadium's component meshes; the first candidate name that exists is preselected.</summary>
        private void FillTargets()
        {
            foreach (ImportSlotRow row in rows)
            {
                string previous = row.SelectedTarget;
                row.Targets.Clear();
                row.Targets.Add(ImportSlotRow.Skip);
                string newTarget = null;
                if (row.NewMeshName != null && componentFolder != null)
                {
                    newTarget = ImportSlotRow.NewPrefix + "content/worlds/components/stadiums/" + componentFolder.ToLower() + "/" + row.NewMeshName;
                    row.Targets.Add(newTarget);
                }
                foreach (EbxAssetEntry mesh in componentMeshes)
                    row.Targets.Add(mesh.Name);

                string pick = null;
                foreach (string candidate in row.Candidates)
                {
                    EbxAssetEntry match = componentMeshes.FirstOrDefault(m => m.Filename.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        pick = match.Name;
                        break;
                    }
                }
                // a weather / night layer the stadium has no mesh for: a new component mesh (an existing pes_* mesh is re-used)
                if (pick == null && newTarget != null)
                {
                    EbxAssetEntry existing = componentMeshes.FirstOrDefault(m => m.Filename.Equals(row.NewMeshName, StringComparison.OrdinalIgnoreCase));
                    pick = existing != null ? existing.Name : newTarget;
                }
                if (pick == null && previous != null && row.Targets.Contains(previous))
                    pick = previous;
                row.SelectedTarget = pick ?? ImportSlotRow.Skip;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (package == null)
            {
                FrostyMessageBox.Show("Choose a stadium.json written by the converter first.", "Import PES Stadium");
                return;
            }
            if (CurrentStadiumFolder == null)
            {
                FrostyMessageBox.Show("Choose the target stadium.", "Import PES Stadium");
                return;
            }

            StadiumImportSettings s = new StadiumImportSettings
            {
                PackagePath = packageTextBox.Text.Trim(),
                StadiumFolder = CurrentStadiumFolder,
                StadiumNameOnly = componentFolder ?? CurrentStadiumNameOnly,
                ImportTextures = importTexturesCheckBox.IsChecked == true,
                DynamicLighting = dynamicLightingCheckBox.IsChecked == true,
                TextureFolder = textureFolderTextBox.Text.Trim().Replace('\\', '/').Trim('/'),
                OpaquePreset = opaquePresetTextBox.Text.Trim(),
                AlphaTestPreset = alphaPresetTextBox.Text.Trim(),
                GlassPreset = glassPresetTextBox.Text.Trim(),
                EmissivePreset = emissivePresetTextBox.Text.Trim(),
                ImportCrowd = crowdCheckBox.IsChecked == true,
                ImportPhysics = physicsCheckBox.IsChecked == true,
                KeepFifaBenches = keepBenchesCheckBox.IsChecked == true,
                CarveTunnel = carveTunnelCheckBox.IsChecked == true,
                RefitPlacements = refitCheckBox.IsChecked == true,
                SinkCrests = sinkCrestsCheckBox.IsChecked == true,
                ClearOtherComponents = clearOthersCheckBox.IsChecked == true,
                ClearRailings = clearRailingsCheckBox.IsChecked == true,
                ClearCrowdNetting = clearNettingCheckBox.IsChecked == true,
                ClearCitySkirt = clearCitySkirtCheckBox.IsChecked == true,
                ClearScoreboard = clearScoreboardCheckBox.IsChecked == true,
                ClearAdboardPlinth = clearAdboardsCheckBox.IsChecked == true,
                SinkBanners = sinkBannersCheckBox.IsChecked == true,
                SinkStaticAdboards = clearAdboardsCheckBox.IsChecked == true,
                SinkLightFixtures = sinkLightsCheckBox.IsChecked == true,
                RefitLensFlares = flaresCheckBox.IsChecked == true,
                ExteriorGroundPlane = groundPlaneCheckBox.IsChecked == true
            };
            HashSet<string> usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ImportSlotRow row in rows)
            {
                if (string.IsNullOrEmpty(row.SelectedTarget) || row.SelectedTarget == ImportSlotRow.Skip)
                    continue;
                if (!usedTargets.Add(row.SelectedTarget))
                {
                    FrostyMessageBox.Show("The mesh " + row.SelectedTarget + " is the target of more than one package mesh.", "Import PES Stadium");
                    return;
                }
                s.SlotTargets[row.SlotName] = row.SelectedTarget;
            }
            if (s.SlotTargets.Count == 0)
            {
                FrostyMessageBox.Show("Map at least one package mesh to a stadium mesh.", "Import PES Stadium");
                return;
            }
            if (s.ImportTextures && string.IsNullOrEmpty(s.TextureFolder))
            {
                FrostyMessageBox.Show("The texture folder cannot be empty.", "Import PES Stadium");
                return;
            }

            Config.Add("PesStadiumImportPackage", s.PackagePath, ConfigScope.Game);
            Settings = s;
            DialogResult = true;
            Close();
        }
    }
}
