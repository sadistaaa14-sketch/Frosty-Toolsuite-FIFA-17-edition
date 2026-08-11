using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// Shared runner: shows the bulk-import dialog, then processes every row inside a single
    /// FrostyTaskWindow, logging per-row successes/failures and continuing past bad rows instead
    /// of aborting the whole batch. The data explorer is refreshed once at the end.
    /// </summary>
    internal static class BulkDuplicationRunner
    {
        public static void Run(string assetTypeName, string exampleLine, Action<FrostyTaskWindow, BulkImportRow> duplicateOne)
        {
            BulkImportWindow win = new BulkImportWindow(assetTypeName, exampleLine);
            if (win.ShowDialog() != true)
                return;

            List<BulkImportRow> rows = win.Rows;
            int succeeded = 0;
            int failed = 0;

            FrostyTaskWindow.Show("Bulk Duplicating " + assetTypeName, "", (task) =>
            {
                if (!MeshVariationDb.IsLoaded)
                    MeshVariationDb.LoadVariations(task);

                for (int i = 0; i < rows.Count; i++)
                {
                    BulkImportRow row = rows[i];
                    task.Update(assetTypeName + " " + (i + 1) + "/" + rows.Count + ": " + row);

                    try
                    {
                        duplicateOne(task, row);
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        App.Logger.Log("Bulk " + assetTypeName + " duplication failed for line " + row.LineNumber +
                            " (" + row + "): " + ex.Message);
                    }
                }

                App.Logger.Log("Bulk " + assetTypeName + " duplication complete: " + succeeded + " succeeded, " + failed + " failed.");
            });

            App.EditorWindow.DataExplorer.RefreshAll();

            FrostyMessageBox.Show(
                succeeded + " of " + rows.Count + " " + assetTypeName.ToLower() + "(s) duplicated successfully." +
                (failed > 0 ? "\n" + failed + " failed — see the log for details." : ""),
                "Bulk Duplicate " + assetTypeName);
        }
    }

    public class BulkDuplicateStarheadMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Starhead...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateStarheadMenuExtension runner = new DuplicateStarheadMenuExtension();
            BulkDuplicationRunner.Run("Starhead", "players/heads/achraf_hakimi_235212,messi_lionel_158023",
                (task, row) => runner.DuplicateStarhead(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateKitMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Kit...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateKitMenuExtension runner = new DuplicateKitMenuExtension();
            BulkDuplicationRunner.Run("Kit", "kits/001,999",
                (task, row) => runner.DuplicateKit(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateTrophyMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Trophy...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateTrophyMenuExtension runner = new DuplicateTrophyMenuExtension();
            BulkDuplicationRunner.Run("Trophy", "trophies/uefa_champions_league,new_custom_cup",
                (task, row) => runner.DuplicateTrophy(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateGenericHeadMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Generic Head...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateGenericHeadMenuExtension runner = new DuplicateGenericHeadMenuExtension();
            BulkDuplicationRunner.Run("Generic Head", "genericheads/generichead_00,generichead_99",
                (task, row) => runner.DuplicateGenericHead(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateGenericHairMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Generic Hair...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateGenericHairMenuExtension runner = new DuplicateGenericHairMenuExtension();
            BulkDuplicationRunner.Run("Generic Hair", "generichair/generichair_00,generichair_99",
                (task, row) => runner.DuplicateGenericHair(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateLeagueLogoMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate League Logo...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateLeagueLogoMenuExtension runner = new DuplicateLeagueLogoMenuExtension();
            BulkDuplicationRunner.Run("League Logo", "leaguelogos/logo_001,logo_999",
                (task, row) => runner.DuplicateLeagueLogo(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateManagerMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Manager...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateManagerMenuExtension runner = new DuplicateManagerMenuExtension();
            BulkDuplicationRunner.Run("Manager", "managers/manager_00001,manager_99999",
                (task, row) => runner.DuplicateManager(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateBallMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Ball...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateBallMenuExtension runner = new DuplicateBallMenuExtension();
            BulkDuplicationRunner.Run("Ball", "balls/ball_001,ball_999",
                (task, row) => runner.DuplicateBall(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateShoeMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Shoe...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateShoeMenuExtension runner = new DuplicateShoeMenuExtension();
            BulkDuplicationRunner.Run("Shoe", "shoes/shoe_001,shoe_999",
                (task, row) => runner.DuplicateShoe(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    public class BulkDuplicateAccessoryMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Accessory...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateAccessoryMenuExtension runner = new DuplicateAccessoryMenuExtension();
            BulkDuplicationRunner.Run("Accessory", "accessories/accessory_001,accessory_999",
                (task, row) => runner.DuplicateAccessory(task, row.SourcePath, row.NewName, row.DestPath));
        });
    }

    /// <summary>
    /// Body scale duplication uses a flat naming scheme (no destination folder — new copies are
    /// always placed alongside the source), so its row format is SourcePath,NewName with DestPath ignored.
    /// </summary>
    public class BulkDuplicateBodyScaleMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Duplication";
        public override string MenuItemName => "Bulk Duplicate Body Scale...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateBodyScaleMenuExtension runner = new DuplicateBodyScaleMenuExtension();
            BulkDuplicationRunner.Run("Body Scale", "bbscales/bbscale_0_0_0,bbscale_0_0_1",
                (task, row) =>
                {
                    // For body scales the "source" folder and asset name are separate; SourcePath here
                    // is expected to be "<folder>/<sourceAssetName>" (e.g. bbscales/bbscale_0_0_0).
                    int idx = row.SourcePath.LastIndexOf('/');
                    string folder = idx >= 0 ? row.SourcePath.Substring(0, idx) : row.SourcePath;
                    string sourceName = idx >= 0 ? row.SourcePath.Substring(idx + 1) : row.SourcePath;

                    runner.DuplicateBodyScale(task, folder, sourceName, row.NewName);
                });
        });
    }
}
