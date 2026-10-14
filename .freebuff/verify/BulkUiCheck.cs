using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frosty.Core;
using Frosty.Core.Bulk;
using Frosty.Core.Windows;
using FrostySdk.IO;
using FrostySdk.Managers;

/// <summary>
/// Checks the bulk export/import options dialogs, the skeleton auto-detection rules, the format
/// choices and the xml reader against the real FIFA 17 xml dump - everything in this feature that can
/// be exercised without a loaded game.
/// </summary>
internal static class BulkUiCheck
{
    private const string SkeletonDir = @"C:\Users\andre\Downloads\fifa17xml\content\character\rig\skeleton\player";

    private static int passed;
    private static int failed;
    private static readonly List<string> failures = new List<string>();

    private static void Ok(string what)
    {
        passed++;
        Console.WriteLine("  ok    " + what);
    }

    private static void Check(string what, bool condition, string detail = "")
    {
        if (condition)
        {
            Ok(what);
        }
        else
        {
            failed++;
            failures.Add(what + " -- " + detail);
            Console.WriteLine("  FAIL  " + what + " -- " + detail);
        }
    }

    private static void CheckEqual(string what, object expected, object actual)
    {
        bool same = expected == null ? actual == null : expected.Equals(actual);
        Check(what, same, "expected <" + (expected ?? "null") + "> got <" + (actual ?? "null") + ">");
    }

    // ---- reflection helpers, because the XAML named controls are internal fields of the dialogs

    private static object Field(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
            throw new InvalidOperationException("there is no '" + name + "' in " + target.GetType().Name);
        return field.GetValue(target);
    }

    private static bool HasControl(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
    }

    private static bool IsChecked(object window, string name)
    {
        return ((CheckBox)Field(window, name)).IsChecked == true;
    }

    private static void SetChecked(object window, string name, bool value)
    {
        ((CheckBox)Field(window, name)).IsChecked = value;
    }

    private static bool IsVisible(object window, string name)
    {
        return ((FrameworkElement)Field(window, name)).Visibility == Visibility.Visible;
    }

    private static bool IsEnabled(object window, string name)
    {
        return ((Control)Field(window, name)).IsEnabled;
    }

    /// <summary>The Tag of the selected entry of a combo built from ComboBoxItems.</summary>
    private static string ComboTag(object window, string name)
    {
        ComboBox box = (ComboBox)Field(window, name);
        ComboBoxItem item = box.SelectedItem as ComboBoxItem;
        return item == null ? null : item.Tag as string;
    }

    private static List<string> ComboTags(object window, string name)
    {
        List<string> tags = new List<string>();
        foreach (object item in ((ComboBox)Field(window, name)).Items)
        {
            ComboBoxItem entry = item as ComboBoxItem;
            if (entry != null)
                tags.Add(entry.Tag as string);
        }
        return tags;
    }

    private static void SelectComboTag(object window, string name, string tag)
    {
        ComboBox box = (ComboBox)Field(window, name);
        foreach (object item in box.Items)
        {
            ComboBoxItem entry = item as ComboBoxItem;
            if (entry != null && (entry.Tag as string) == tag)
            {
                box.SelectedItem = entry;
                return;
            }
        }

        throw new InvalidOperationException("no '" + tag + "' in " + name);
    }

    private static void Click(object window, string name)
    {
        ((Button)Field(window, name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>Shows a dialog for real and runs the interaction on its first idle turn.</summary>
    private static bool? Show(Window window, Action<Window> interact)
    {
        DispatcherTimer timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
            (sender, e) =>
            {
                ((DispatcherTimer)sender).Stop();
                try
                {
                    interact(window);
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add("interaction: " + ex.Message);
                    Console.WriteLine("  FAIL  interaction -- " + ex);
                    window.Close();
                }
            }, Dispatcher.CurrentDispatcher);
        timer.Start();
        return window.ShowDialog();
    }

    /// <summary>
    /// Renders a window to a PNG next to the harness. The dialogs are judged by what they look like,
    /// so the run leaves a picture of each one as it was shown rather than only reading its controls.
    /// </summary>
    private static void Capture(Window window, string name)
    {
        try
        {
            window.UpdateLayout();
            int width = (int)Math.Ceiling(window.ActualWidth);
            int height = (int)Math.Ceiling(window.ActualHeight);
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".png");
            using (FileStream stream = new FileStream(path, FileMode.Create))
                encoder.Save(stream);

            Ok("wrote " + name + ".png (" + width + "x" + height + ")");
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add("capture " + name + ": " + ex.Message);
            Console.WriteLine("  FAIL  capture " + name + " -- " + ex.Message);
        }
    }

    /// <summary>
    /// Measures what the eye is actually given: the size of every visible, live piece of text and the
    /// contrast between it and the surface behind it. "I can barely read the text" is a statement
    /// about those two numbers, so they are checked here instead of being judged by eye.
    /// </summary>
    private static void Readability(Window window, string label)
    {
        window.UpdateLayout();

        double minSize = double.MaxValue;
        double minContrast = double.MaxValue;
        string smallest = "nothing";
        string faintest = "nothing";
        string faintestColours = "";
        int measured = 0;
        int dimmed = 0;

        // Switched-off text is measured too, but only reported: a disabled control is meant to look
        // quieter, so it is not held to the live-text bar - it just has to stay legible enough to
        // read what it is offering.
        double minDimSize = double.MaxValue;
        double minDimContrast = double.MaxValue;
        string dimmest = "nothing";

        foreach (FrameworkElement element in Descendants(window))
        {
            string text = TextOf(element);
            if (string.IsNullOrWhiteSpace(text) || !element.IsVisible)
                continue;

            // FontSize and Foreground live on TextBlock or on Control, not on FrameworkElement.
            TextBlock block = element as TextBlock;
            Control control = element as Control;
            double size = block != null ? block.FontSize : (control != null ? control.FontSize : 0.0);
            if (size <= 0.0)
                continue;

            if (!element.IsEnabled)
            {
                dimmed++;
                SolidColorBrush dimForeground = (block != null ? block.Foreground : control.Foreground) as SolidColorBrush;
                if (dimForeground != null)
                {
                    if (size < minDimSize)
                        minDimSize = size;

                    // Dimming is done with opacity, so the colour the eye gets is the declared brush
                    // composited over what is behind it - not the brush on its own.
                    Color dimBehind = BackgroundOf(element);
                    double dimContrast = Contrast(Composite(dimForeground.Color, dimBehind, EffectiveOpacity(element)), dimBehind);
                    if (dimContrast < minDimContrast)
                    {
                        minDimContrast = dimContrast;
                        dimmest = text.Trim();
                    }
                }

                continue;
            }

            measured++;
            if (size < minSize)
            {
                minSize = size;
                smallest = text.Trim();
            }

            SolidColorBrush foreground = (block != null ? block.Foreground : control.Foreground) as SolidColorBrush;
            if (foreground == null)
                continue;

            Color behind = BackgroundOf(element);
            double contrast = Contrast(foreground.Color, behind);
            if (contrast < minContrast)
            {
                minContrast = contrast;
                faintest = text.Trim();
                faintestColours = Hex(foreground.Color) + " on " + Hex(behind);
            }
        }

        Check(label + ": something to read was found", measured >= 20, measured + " pieces of live text");
        Check(label + ": no live text is smaller than 12px", minSize >= 12.0,
            "smallest is " + minSize + "px: " + smallest);
        Check(label + ": no live text is fainter than 4.5:1", minContrast >= 4.5,
            "faintest is " + minContrast.ToString("0.0") + ":1 (" + faintestColours + ") on \"" + faintest + "\"");

        Console.WriteLine("  ..    " + label + ": " + measured + " live, " + dimmed + " switched off, smallest " +
            minSize + "px, faintest " + minContrast.ToString("0.0") + ":1 (" + faintestColours + ")");

        if (dimmed > 0 && minDimContrast != double.MaxValue)
        {
            // Reported, not asserted: this is what a switched-off picker actually measures, so the
            // numbers are on the record even though the theme dims them on purpose.
            Console.WriteLine("  ..    " + label + ": switched-off text, smallest " + minDimSize +
                "px, faintest " + minDimContrast.ToString("0.0") + ":1 with its dimming applied (\"" + dimmest + "\")");
        }
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            FrameworkElement element = child as FrameworkElement;
            if (element != null)
                yield return element;

            foreach (FrameworkElement nested in Descendants(child))
                yield return nested;
        }
    }

    /// <summary>The text a piece of a dialog shows, whether it is a TextBlock or a labelled control.</summary>
    private static string TextOf(FrameworkElement element)
    {
        TextBlock block = element as TextBlock;
        if (block != null)
            return block.Text;

        ContentControl control = element as ContentControl;
        return control == null ? null : control.Content as string;
    }

    /// <summary>The colour behind an element: its own fill, or the nearest one up the tree.</summary>
    private static Color BackgroundOf(DependencyObject element)
    {
        DependencyObject current = element;
        while (current != null)
        {
            Panel panel = current as Panel;
            Border border = current as Border;
            ContentControl control = current as ContentControl;
            Brush brush = panel != null ? panel.Background
                : border != null ? border.Background
                : control != null ? control.Background
                : null;

            SolidColorBrush solid = brush as SolidColorBrush;
            if (solid != null && solid.Color.A != 0)
                return solid.Color;

            current = VisualTreeHelper.GetParent(current);
        }

        return Colors.Black;
    }

    /// <summary>The WCAG contrast ratio between two colours, 1:1 (identical) to 21:1 (black on white).</summary>
    private static double Contrast(Color one, Color other)
    {
        double a = Luminance(one);
        double b = Luminance(other);
        double high = Math.Max(a, b);
        double low = Math.Min(a, b);
        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color colour)
    {
        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }

    /// <summary>The opacity actually applied to an element, which its ancestors can dim further.</summary>
    private static double EffectiveOpacity(DependencyObject element)
    {
        double opacity = 1.0;
        DependencyObject current = element;
        while (current != null)
        {
            UIElement visual = current as UIElement;
            if (visual != null)
                opacity *= visual.Opacity;

            current = VisualTreeHelper.GetParent(current);
        }

        return opacity;
    }

    /// <summary>The colour seen when <paramref name="foreground"/> is drawn at that opacity.</summary>
    private static Color Composite(Color foreground, Color background, double opacity)
    {
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * opacity + background.R * (1.0 - opacity)),
            (byte)Math.Round(foreground.G * opacity + background.G * (1.0 - opacity)),
            (byte)Math.Round(foreground.B * opacity + background.B * (1.0 - opacity)));
    }

    private static string Hex(Color colour)
    {
        return "#" + colour.R.ToString("X2") + colour.G.ToString("X2") + colour.B.ToString("X2");
    }

    private static double Channel(byte value)
    {
        double fraction = value / 255.0;
        return fraction <= 0.03928 ? fraction / 12.92 : Math.Pow((fraction + 0.055) / 1.055, 2.4);
    }

    private static Assembly LoadArtifact(string name)
    {
        return Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
    }

    private static AssetDefinition MakeDefinition(string assemblyName, string typeName)
    {
        Type type = LoadArtifact(assemblyName).GetType(typeName);
        Check(assemblyName + " declares " + typeName, type != null, "type not found");
        return type == null ? null : (AssetDefinition)Activator.CreateInstance(type);
    }

    private static void CheckOverride(string assemblyName, string typeName, string method)
    {
        try
        {
            Type type = LoadArtifact(assemblyName).GetType(typeName);
            Check(assemblyName + " declares " + typeName, type != null, "type not found");
            if (type == null)
                return;

            MethodInfo info = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Check(assemblyName + ": " + typeName + "." + method + " is its own method",
                info != null && info.DeclaringType == type,
                info == null ? "not found" : "declared by " + info.DeclaringType);
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add(assemblyName + " " + typeName + "." + method + ": " + ex.Message);
            Console.WriteLine("  FAIL  " + assemblyName + " " + typeName + "." + method + " -- " + ex.Message);
        }
    }

    private static void CheckMetadata(string file, params string[] expected)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
        string text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        DateTime built = File.GetLastWriteTime(path);
        foreach (string name in expected)
            Check(file + " (" + built.ToString("HH:mm:ss") + ") references " + name, text.Contains(name), "not found in the built assembly");
    }

    [STAThread]
    private static int Main()
    {
        Application app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // If an interaction ever hangs, close whatever is open so the run cannot stall forever.
        DispatcherTimer watchdog = new DispatcherTimer(TimeSpan.FromSeconds(120), DispatcherPriority.Send,
            (sender, e) =>
            {
                ((DispatcherTimer)sender).Stop();
                Console.WriteLine("  WATCHDOG fired");
                foreach (Window open in app.Windows.Cast<Window>().ToList())
                    open.Close();
            }, Dispatcher.CurrentDispatcher);
        watchdog.Start();

        Console.WriteLine("== export options dialog: defaults, enablement and the choices coming back ==");
        ExportDialog();

        Console.WriteLine();
        Console.WriteLine("== export options dialog: the chunk and res scopes ==");
        ExportScopes();

        Console.WriteLine();
        Console.WriteLine("== import options dialog: defaults and the choices coming back ==");
        ImportDialog();

        Console.WriteLine();
        Console.WriteLine("== what the categories mean, and the formats they write ==");
        Categories();

        Console.WriteLine();
        Console.WriteLine("== skeleton auto-detection on the real FIFA 17 layout ==");
        SkeletonRules();

        Console.WriteLine();
        Console.WriteLine("== the xml reader against the real FIFA 17 dump ==");
        XmlRoundTrip();

        Console.WriteLine();
        Console.WriteLine("== the chunks a sound asset names in its own objects ==");
        SoundChunks();

        Console.WriteLine();
        Console.WriteLine("== the same walk against the game's own sound classes ==");
        SoundChunksOnRealTypes();

        Console.WriteLine();
        Console.WriteLine("== the chunk list and the res tree, which run without a dialog ==");
        EditorFlows();

        Console.WriteLine();
        Console.WriteLine("== every source file is actually compiled ==");
        SourceFilesAreCompiled();

        Console.WriteLine();
        Console.WriteLine("== a run leaves a report beside its output ==");
        RunReport();

        Console.WriteLine();
        Console.WriteLine("== the built artifacts ==");
        Artifacts();

        Console.WriteLine();
        Console.WriteLine("passed: " + passed + "   failed: " + failed);
        foreach (string failure in failures)
            Console.WriteLine("  failed: " + failure);
        return failed == 0 ? 0 : 1;
    }

    private static void ExportDialog()
    {
        BulkOptions options = new BulkOptions();
        BulkExportOptionsWindow window = new BulkExportOptionsWindow(options, BulkScope.All);

        bool? result = Show(window, w =>
        {
            CheckEqual("ebx is ticked by default", true, IsChecked(w, "exportEbxCheckBox"));
            CheckEqual("ebx as xml is ticked by default", true, IsChecked(w, "exportEbxXmlCheckBox"));
            CheckEqual("ebx as bin is NOT ticked by default", false, IsChecked(w, "exportEbxBinCheckBox"));
            CheckEqual("meshes are not ticked by default", false, IsChecked(w, "exportMeshCheckBox"));
            CheckEqual("textures are not ticked by default", false, IsChecked(w, "exportTextureCheckBox"));
            CheckEqual("chunks are not ticked by default", false, IsChecked(w, "exportChunkCheckBox"));
            CheckEqual("res is not ticked by default", false, IsChecked(w, "exportResCheckBox"));
            CheckEqual("skeleton auto-detect is on by default", true, IsChecked(w, "skeletonAutoCheckBox"));

            Check("the asset tree offers ebx, meshes, textures, chunks, res and the skeleton picker",
                IsVisible(w, "ebxRow") && IsVisible(w, "meshRow") && IsVisible(w, "textureRow") &&
                IsVisible(w, "chunkRow") && IsVisible(w, "resRow") && IsVisible(w, "skeletonRow"),
                "one of the rows is hidden");

            CheckEqual("the mesh format combo offers fbx, fbx ascii and obj", "fbx|fbx-ascii|obj",
                string.Join("|", ComboTags(w, "meshFormatComboBox")));
            CheckEqual("the mesh format starts as binary fbx", "fbx", ComboTag(w, "meshFormatComboBox"));
            CheckEqual("dds is ticked by default", true, IsChecked(w, "textureDdsCheckBox"));
            CheckEqual("png is not ticked by default", false, IsChecked(w, "texturePngCheckBox"));
            CheckEqual("tga is not ticked by default", false, IsChecked(w, "textureTgaCheckBox"));
            CheckEqual("hdr is not ticked by default", false, IsChecked(w, "textureHdrCheckBox"));

            CheckEqual("the mesh format is dead while meshes are unticked", false, IsEnabled(w, "meshFormatComboBox"));
            CheckEqual("the texture formats are dead while textures are unticked", false, IsEnabled(w, "texturePngCheckBox"));
            CheckEqual("the skeleton list is dead while auto is on", false, IsEnabled(w, "skeletonComboBox"));
            CheckEqual("OK is available with the defaults", true, IsEnabled(w, "okButton"));

            // Read the dialog as it opens, which is the state the user actually meets first - and the
            // only one where the format pickers and the skeleton list are switched off.
            Readability(w, "export dialog (as it opens)");

            SetChecked(w, "exportMeshCheckBox", true);
            SetChecked(w, "exportTextureCheckBox", true);
            CheckEqual("ticking meshes wakes the mesh format", true, IsEnabled(w, "meshFormatComboBox"));
            CheckEqual("ticking textures wakes the texture formats", true, IsEnabled(w, "textureHdrCheckBox"));

            SetChecked(w, "exportEbxCheckBox", false);
            Check("the xml/bin boxes go dead with ebx unticked",
                !IsEnabled(w, "exportEbxXmlCheckBox") && !IsEnabled(w, "exportEbxBinCheckBox"), "still live");

            SetChecked(w, "exportEbxCheckBox", true);
            SetChecked(w, "exportEbxXmlCheckBox", false);
            SetChecked(w, "exportEbxBinCheckBox", false);
            CheckEqual("OK stays available when ebx has no form but another category is ticked", true, IsEnabled(w, "okButton"));

            // The choices a user would make: ascii meshes, dds and png textures, a skeleton by hand.
            SelectComboTag(w, "meshFormatComboBox", "fbx-ascii");
            SetChecked(w, "exportEbxXmlCheckBox", false);
            SetChecked(w, "exportEbxBinCheckBox", true);
            SetChecked(w, "exportChunkCheckBox", true);
            SetChecked(w, "exportResCheckBox", true);
            SetChecked(w, "texturePngCheckBox", true);

            ComboBox skeletons = (ComboBox)Field(w, "skeletonComboBox");
            skeletons.ItemsSource = new List<string>
            {
                "content/character/rig/skeleton/player/skeleton_ball",
                "content/character/rig/skeleton/player/skeleton_player"
            };
            SetChecked(w, "skeletonAutoCheckBox", false);
            skeletons.SelectedItem = "content/character/rig/skeleton/player/skeleton_player";

            Readability(w, "export dialog");
            Capture(w, "export-options");
            Click(w, "okButton");
        });

        CheckEqual("the export dialog was accepted", true, result);
        Check("the accepted options are the object that was passed in", ReferenceEquals(window.Options, options), "different object");
        CheckEqual("ebx as xml came back off", false, options.EbxXml);
        CheckEqual("ebx as bin came back on", true, options.EbxBin);
        CheckEqual("meshes came back on", true, options.Meshes);
        CheckEqual("textures came back on", true, options.Textures);
        CheckEqual("chunks came back on", true, options.Chunks);
        CheckEqual("res came back on", true, options.Res);
        CheckEqual("the ascii mesh format came back", "fbx-ascii", options.MeshFormat);
        CheckEqual("the texture formats came back", "dds+png", options.TextureFormatList);
        CheckEqual("skeleton auto-detect came back off", false, options.SkeletonAuto);
        CheckEqual("the chosen skeleton came back", "content/character/rig/skeleton/player/skeleton_player", options.Skeleton);
        Check("the run describes itself", options.Describe("Bulk export").Contains("ebx as bin") &&
            options.Describe("Bulk export").Contains("meshes as fbx-ascii") &&
            options.Describe("Bulk export").Contains("textures as dds+png") &&
            options.Describe("Bulk export").Contains("chunks"), options.Describe("Bulk export"));

        // A category ticked with no format ticked still writes something.
        BulkOptions bare = new BulkOptions { Ebx = false, EbxBin = false, EbxXml = false };
        BulkExportOptionsWindow second = new BulkExportOptionsWindow(bare, BulkScope.All);
        Show(second, w =>
        {
            SetChecked(w, "exportTextureCheckBox", true);
            SetChecked(w, "textureDdsCheckBox", false);
            Click(w, "okButton");
        });
        CheckEqual("textures with nothing ticked fall back to dds", "dds", bare.TextureFormatList);

        // An export with nothing selected must be a no-op rather than a walk over the whole game.
        BulkOptions nothing = new BulkOptions { Ebx = false, EbxBin = false, EbxXml = false };
        CheckEqual("nothing selected means no work", 0, AssetBulkExporter.Run("content/character", nothing, @"C:\nonexistent", null).Done);
        CheckEqual("nothing selected means no work on import", 0, AssetBulkImporter.Run("content/character", nothing, @"C:\nonexistent", null).Done);
    }

    private static void ExportScopes()
    {
        // Opened from the Chunk/Res Editor's chunk list: only chunks.
        BulkExportOptionsWindow chunks = new BulkExportOptionsWindow(new BulkOptions(), BulkScope.Chunks);
        bool? result = Show(chunks, w =>
        {
            CheckEqual("the chunk list scope hides ebx", false, IsVisible(w, "ebxRow"));
            CheckEqual("the chunk list scope hides meshes", false, IsVisible(w, "meshRow"));
            CheckEqual("the chunk list scope hides textures", false, IsVisible(w, "textureRow"));
            CheckEqual("the chunk list scope hides the skeleton picker", false, IsVisible(w, "skeletonRow"));
            CheckEqual("the chunk list scope hides res", false, IsVisible(w, "resRow"));
            CheckEqual("the chunk list scope shows chunks", true, IsVisible(w, "chunkRow"));
            CheckEqual("nothing is ticked so OK is dead", false, IsEnabled(w, "okButton"));

            SetChecked(w, "exportChunkCheckBox", true);
            CheckEqual("ticking chunks wakes OK", true, IsEnabled(w, "okButton"));
            Click(w, "cancelButton");
        });

        CheckEqual("cancel reports no result", false, result);
        CheckEqual("a cancelled run hands nothing back", true, chunks.Options == null);

        // Opened from the res tree: only resources.
        BulkImportOptionsWindow res = new BulkImportOptionsWindow(new BulkOptions(), BulkScope.Res);
        result = Show(res, w =>
        {
            CheckEqual("the res scope hides ebx", false, IsVisible(w, "ebxRow"));
            CheckEqual("the res scope hides chunks", false, IsVisible(w, "chunkRow"));
            CheckEqual("the res scope shows res", true, IsVisible(w, "resRow"));
            SetChecked(w, "importResCheckBox", true);
            Click(w, "okButton");
        });

        CheckEqual("the res dialog was accepted", true, result);
        CheckEqual("res came back on", true, res.Options.Res);
    }

    private static void ImportDialog()
    {
        BulkOptions options = new BulkOptions();
        BulkImportOptionsWindow window = new BulkImportOptionsWindow(options, BulkScope.All);

        bool? result = Show(window, w =>
        {
            CheckEqual("ebx is ticked by default", true, IsChecked(w, "importEbxCheckBox"));
            CheckEqual("ebx as xml is ticked by default", true, IsChecked(w, "importEbxXmlCheckBox"));
            CheckEqual("ebx as bin is NOT ticked by default", false, IsChecked(w, "importEbxBinCheckBox"));
            CheckEqual("meshes are not ticked by default", false, IsChecked(w, "importMeshCheckBox"));
            CheckEqual("textures are not ticked by default", false, IsChecked(w, "importTextureCheckBox"));
            CheckEqual("chunks are not ticked by default", false, IsChecked(w, "importChunkCheckBox"));
            CheckEqual("res is not ticked by default", false, IsChecked(w, "importResCheckBox"));
            CheckEqual("skeleton auto-detect is on by default", true, IsChecked(w, "skeletonAutoCheckBox"));
            Check("the asset tree offers ebx, meshes, textures, chunks, res and the skeleton picker",
                IsVisible(w, "ebxRow") && IsVisible(w, "meshRow") && IsVisible(w, "textureRow") &&
                IsVisible(w, "chunkRow") && IsVisible(w, "resRow") && IsVisible(w, "skeletonRow"),
                "one of the rows is hidden");

            // Import reads every extension it understands, so it has no format pickers to mislead.
            CheckEqual("the import dialog has no mesh format picker", false, HasControl(w, "meshFormatComboBox"));
            CheckEqual("the import dialog has no texture format picker", false, HasControl(w, "textureDdsCheckBox"));

            Readability(w, "import dialog (as it opens)");

            SetChecked(w, "importEbxXmlCheckBox", false);
            SetChecked(w, "importEbxBinCheckBox", true);
            SetChecked(w, "importMeshCheckBox", true);
            SetChecked(w, "importTextureCheckBox", true);

            ComboBox skeletons = (ComboBox)Field(w, "skeletonComboBox");
            skeletons.ItemsSource = new List<string> { "content/character/rig/skeleton/player/skeleton_player" };
            SetChecked(w, "skeletonAutoCheckBox", false);
            skeletons.SelectedItem = "content/character/rig/skeleton/player/skeleton_player";

            Readability(w, "import dialog");
            Capture(w, "import-options");
            Click(w, "okButton");
        });

        CheckEqual("the import dialog was accepted", true, result);
        CheckEqual("ebx as xml came back off", false, options.EbxXml);
        CheckEqual("ebx as bin came back on", true, options.EbxBin);
        CheckEqual("meshes came back on", true, options.Meshes);
        CheckEqual("textures came back on", true, options.Textures);
        CheckEqual("chunks stay off", false, options.Chunks);
        CheckEqual("res stays off", false, options.Res);
        CheckEqual("the skeleton came back", "content/character/rig/skeleton/player/skeleton_player", options.Skeleton);
        CheckEqual("skeleton auto-detect came back off", false, options.SkeletonAuto);
    }

    private static void Categories()
    {
        AssetDefinition baseDefinition = new AssetDefinition();
        AssetDefinition meshDefinition = MakeDefinition("MeshSetPlugin.dll", "MeshSetPlugin.SkinnedMeshAssetDefinition");
        AssetDefinition textureDefinition = MakeDefinition("TexturePlugin.dll", "TexturePlugin.TextureAssetDefinition");

        CheckEqual("the mesh type can be written as a mesh", true, AssetBulkExporter.CanWriteMesh(meshDefinition, "SkinnedMeshAsset"));
        CheckEqual("the mesh type is not a texture", false, AssetBulkExporter.CanWriteTexture(meshDefinition, "SkinnedMeshAsset"));
        CheckEqual("a subclass of a mesh counts as a mesh", true, AssetBulkExporter.CanWriteMesh(meshDefinition, "RigidMeshAsset"));
        CheckEqual("the base definition alone is not enough for a mesh from its formats", false,
            AssetBulkExporter.CanWriteMesh(baseDefinition, "SoundWaveAsset"));
        CheckEqual("but a mesh type name is", true, AssetBulkExporter.CanWriteMesh(baseDefinition, "CompositeMeshAsset"));
        CheckEqual("the texture type can be written as a texture", true, AssetBulkExporter.CanWriteTexture(textureDefinition, "TextureAsset"));
        CheckEqual("the texture type is not a mesh", false, AssetBulkExporter.CanWriteMesh(textureDefinition, "TextureAsset"));
        CheckEqual("an atlas texture counts as a texture", true, AssetBulkExporter.CanWriteTexture(textureDefinition, "AtlasTextureAsset"));

        // "fbx-ascii" is an .fbx file; everything else is its own extension.
        CheckEqual("fbx writes .fbx", "fbx", AssetBulkExporter.FormatExtension("fbx"));
        CheckEqual("fbx ascii writes .fbx too", "fbx", AssetBulkExporter.FormatExtension("fbx-ascii"));
        CheckEqual("obj writes .obj", "obj", AssetBulkExporter.FormatExtension("obj"));

        List<AssetExportType> meshFormats = AssetBulkExporter.SupportedFormats(meshDefinition);
        string[] choices = { "fbx", "fbx-ascii", "obj" };
        CheckEqual("the ascii fbx choice is honoured", "fbx-ascii", AssetBulkExporter.ChooseFormat(meshFormats, choices, "fbx-ascii"));
        CheckEqual("the obj choice is honoured", "obj", AssetBulkExporter.ChooseFormat(meshFormats, choices, "obj"));
        CheckEqual("a format the type cannot write is passed on to be reported", "fbx-ascii",
            AssetBulkExporter.ChooseFormat(AssetBulkExporter.SupportedFormats(baseDefinition), choices, "fbx-ascii"));

        List<AssetExportType> textureFormats = AssetBulkExporter.SupportedFormats(textureDefinition);
        CheckEqual("the texture type can write all four image formats", "png|tga|hdr|dds",
            string.Join("|", textureFormats.Where(f => f.Extension != "bin" && f.Extension != "xml").Select(f => f.Extension)));

        BulkOptions both = new BulkOptions { TextureFormats = new List<string> { "dds", "png" } };
        CheckEqual("both ticked texture formats are written", "dds+png",
            string.Join("+", AssetBulkExporter.ChosenTextureFormats(both, textureDefinition)));
        BulkOptions empty = new BulkOptions { TextureFormats = new List<string>() };
        CheckEqual("no format ticked falls back to dds", "dds",
            string.Join("+", AssetBulkExporter.ChosenTextureFormats(empty, textureDefinition)));
        BulkOptions onlyPng = new BulkOptions { TextureFormats = new List<string> { "png" } };
        CheckEqual("one ticked texture format is written on its own", "png",
            string.Join("+", AssetBulkExporter.ChosenTextureFormats(onlyPng, textureDefinition)));

        // An export that writes several files beside the target still counts as written.
        string temp = Path.Combine(Path.GetTempPath(), "bulkcheck_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string target = Path.Combine(temp, "some_texture.png");
        CheckEqual("nothing written reads as nothing", false, AssetBulkExporter.WroteSomething(target));

        // A numbered array slice, exactly as cmnTextureExporter names them (i:D3).
        string slice = Path.Combine(temp, "some_texture_000.png");
        File.WriteAllBytes(slice, new byte[] { 1, 2, 3 });
        CheckEqual("a numbered array slice beside the target counts", true, AssetBulkExporter.WroteSomething(target));
        File.Delete(slice);

        // A cube face, exactly as the exporter names them (px/nx/py/ny/pz/nz).
        string face = Path.Combine(temp, "some_texture_px.png");
        File.WriteAllBytes(face, new byte[] { 1, 2, 3 });
        CheckEqual("a cube face beside the target counts", true, AssetBulkExporter.WroteSomething(target));
        File.Delete(face);

        // The regression this replaced a looser rule for: another asset whose name merely starts with
        // this one's must never be read as this one's output. "kit_0" / "kit_0_normal" are ordinary
        // neighbour names, and the old `<stem>_*` glob counted the second for the first.
        string neighbour = Path.Combine(temp, "some_texture_normal.png");
        File.WriteAllBytes(neighbour, new byte[] { 1, 2, 3 });
        CheckEqual("another asset's file with a shared prefix does NOT count", false, AssetBulkExporter.WroteSomething(target));
        File.Delete(neighbour);

        // Nor does a one-digit suffix, which no exporter produces - a sibling has to look like a face
        // or like a slice to be evidence.
        string shortSuffix = Path.Combine(temp, "some_texture_0.png");
        File.WriteAllBytes(shortSuffix, new byte[] { 1, 2, 3 });
        CheckEqual("a one-digit suffix is not a slice and does not count", false, AssetBulkExporter.WroteSomething(target));
        File.Delete(shortSuffix);

        // A file that is not the target's extension is never a sibling.
        File.WriteAllBytes(Path.Combine(temp, "some_texture_px.dds"), new byte[] { 1, 2, 3 });
        CheckEqual("a sibling with a different extension does not count", false, AssetBulkExporter.WroteSomething(target));
        File.Delete(Path.Combine(temp, "some_texture_px.dds"));

        File.WriteAllBytes(target, new byte[0]);
        CheckEqual("an empty target on its own does not count", false, AssetBulkExporter.WroteSomething(target));
        File.WriteAllBytes(target, new byte[] { 1 });
        CheckEqual("the target itself counts", true, AssetBulkExporter.WroteSomething(target));
        Directory.Delete(temp, true);

        BulkResult result = new BulkResult();
        result.Done = 5;
        result.Meshes = 2;
        result.Textures = 3;
        CheckEqual("the summary calls out meshes and textures", "5 done, 0 skipped, 0 failed, 2 meshes, 3 textures", result.Describe());
    }

    // ---- the chunks a sound asset names in its own objects -------------------------------------

    /// <summary>
    /// A stand-in for the objects of a sound asset. The real ones are generated at runtime by the
    /// type library and expose their fields as public properties, which is exactly what the walk
    /// reads, so a plain class with the same members exercises the same code.
    /// </summary>
    private class FakeChunkRef
    {
        public Guid ChunkId { get; set; }
        public uint ChunkSize = 0x100;
        public string Text = "";
        public FakeChunkRef Next;

        /// <summary>A member that cannot be read at all, as some of the generated types have.</summary>
        public Guid Unreadable { get { throw new InvalidOperationException("this member does not read"); } }
    }

    /// <summary>A stand-in for a SoundWaveAsset: an id on itself, plus a list of chunk references.</summary>
    private class FakeSoundWave
    {
        public Guid RamChunkId;
        public List<FakeChunkRef> Chunks = new List<FakeChunkRef>();
        public string AssetName = "content/sound/wave/some_wave";
        public uint StreamChunkIndex = 255;
    }

    private static void SoundChunks()
    {
        Guid ram = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid stream = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid nested = Guid.Parse("33333333-3333-3333-3333-333333333333");

        // The chunks the asset manager knows about. Everything else is a guid that merely exists.
        HashSet<Guid> loadedChunks = new HashSet<Guid> { ram, stream, nested };
        Func<Guid, bool> isChunk = id => loadedChunks.Contains(id);

        FakeSoundWave wave = new FakeSoundWave { RamChunkId = ram };
        wave.Chunks.Add(new FakeChunkRef { ChunkId = stream });
        wave.Chunks.Add(new FakeChunkRef { ChunkId = Guid.Empty });
        wave.Chunks.Add(new FakeChunkRef { ChunkId = stream });
        wave.Chunks[0].Next = new FakeChunkRef { ChunkId = nested };

        List<Guid> found = AssetReferenceUtils.CollectChunkGuids(wave, isChunk);
        CheckEqual("the chunk a sound wave names on itself is found", true, found.Contains(ram));
        CheckEqual("the chunks its chunk list names are found", true, found.Contains(stream));
        CheckEqual("a chunk named one object further down is found", true, found.Contains(nested));
        CheckEqual("a chunk named twice is reported once", 1, found.Count(id => id == stream));
        CheckEqual("only loaded chunks are reported", 3, found.Count);
        CheckEqual("an empty guid is not reported", false, found.Contains(Guid.Empty));

        // The members that are not guids at all: a number, a string spelling out a chunk id, and a
        // member that throws when it is read.
        FakeChunkRef onlyText = new FakeChunkRef { ChunkId = Guid.Empty, Text = stream.ToString() };
        CheckEqual("a string holding a chunk id is not read as one", 0,
            AssetReferenceUtils.CollectChunkGuids(onlyText, isChunk).Count);
        CheckEqual("a member that throws is skipped rather than failing the walk", 1,
            AssetReferenceUtils.CollectChunkGuids(new FakeChunkRef { ChunkId = nested }, isChunk).Count);

        // A self referencing graph must terminate: the real objects point back at each other.
        FakeChunkRef self = new FakeChunkRef { ChunkId = stream };
        self.Next = self;
        CheckEqual("an object that points at itself terminates", 1,
            AssetReferenceUtils.CollectChunkGuids(self, isChunk).Count);

        FakeSoundWave cycle = new FakeSoundWave { RamChunkId = Guid.Empty };
        cycle.Chunks.Add(self);
        CheckEqual("and so does a graph that points back at the root", 1,
            AssetReferenceUtils.CollectChunkGuids(cycle, isChunk).Count);

        // Without a game there is no chunk dictionary to consult, and that has to be an empty
        // answer rather than an exception on the way to a context menu.
        CheckEqual("with no game loaded the walk reports nothing instead of throwing", 0,
            AssetReferenceUtils.CollectChunkGuids(wave).Count);
        CheckEqual("and an asset with nothing loaded reports no chunks", 0,
            AssetReferenceUtils.GetChunkIds(new EbxAssetEntry()).Count);

        // The rule above only reaches the product if the lookup runs it: the asset branch of
        // GetChunkIds has to consult the resource it references, the resource that carries the
        // asset's name, and the asset's own objects, in that order.
        string utilsSource = Path.Combine(WorkspaceRoot, "FrostyPlugin", "AssetReferenceUtils.cs");
        string lookup = MethodSource(utilsSource, "public static List<Guid> GetChunkIds(AssetEntry entry)");
        Check("the asset chunk lookup is where the harness expects it", lookup != null, utilsSource + " not read, or the method moved?");
        if (lookup != null)
        {
            Check("the lookup follows the resource the asset references", lookup.Contains("GetReferencedResEntry"), "the referenced resource is no longer consulted");
            Check("the lookup finds the resource that carries the asset's name", lookup.Contains("GetNamedResEntry"), "a sound's resource is not looked for");
            Check("the lookup walks the asset's own objects", lookup.Contains("GetEbxChunkIds"), "the chunks a sound names itself are not looked for");
            Check("the lookup body stops at the end of the method", !lookup.Contains("FormatChunkId"), "the extraction ran past the method");
        }

        // A folder export has to say what it is doing while it resolves chunks (that part writes
        // nothing, so the window would otherwise sit on the last asset), and explain an asset that
        // contributed none, or an empty output folder stays a mystery.
        string exporterSource = Path.Combine(WorkspaceRoot, "FrostyPlugin", "Bulk", "AssetBulkExporter.cs");
        string exportChunks = MethodSource(exporterSource, "private static void ExportChunks(string folderPrefix, string match, BulkOptions options, string outputRoot,");
        Check("a folder chunk export reports what it is resolving",
            exportChunks != null && exportChunks.Contains("resolving chunks"), "the window would sit on the last asset");
        Check("a folder chunk export explains an asset that gave no chunk",
            exportChunks != null && exportChunks.Contains("ExplainNoChunks"), "an empty output folder stays unexplained");
    }

    // ---- the same walk, on the classes the editor really loads FIFA 17 assets through -----------

    /// <summary>
    /// The chunk walk against the game's own classes instead of a stand-in. Fifa17SDK.dll is the
    /// class library the editor loads a FIFA 17 asset through, so the members here are the real ones:
    /// a SoundWaveAsset and a NewWaveAsset each keep a List&lt;SoundDataChunk&gt; in Chunks, and each
    /// SoundDataChunk carries a Guid ChunkId. This is the part that used to need a loaded game.
    /// </summary>
    private static void SoundChunksOnRealTypes()
    {
        string sdkPath = Path.Combine(WorkspaceRoot, "FrostySdk", "Profiles", "Fifa17SDK.dll");
        if (!File.Exists(sdkPath))
        {
            Check("the FIFA 17 classes are there to walk", false, sdkPath + " not found");
            return;
        }

        Assembly sdk = Assembly.LoadFrom(sdkPath);
        Type chunkType = sdk.GetType("FrostySdk.Ebx.SoundDataChunk");
        Check("the SDK declares SoundDataChunk", chunkType != null, "no such class");
        if (chunkType == null)
            return;

        Guid named = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        Guid unknown = Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd");
        string[] classNames = { "SoundWaveAsset", "NewWaveAsset", "LocalizedWaveAsset" };
        int walked = 0;

        foreach (string shortName in classNames)
        {
            string typeName = "FrostySdk.Ebx." + shortName;
            Type assetType = sdk.GetType(typeName);
            Check("the SDK declares " + shortName, assetType != null, "no such class");
            if (assetType == null)
                continue;

            try
            {
                // A real asset object holding one real chunk object in its Chunks list.
                object asset = Activator.CreateInstance(assetType);
                object chunk = Activator.CreateInstance(chunkType);
                chunkType.GetProperty("ChunkId").SetValue(chunk, named);
                ((IList)assetType.GetProperty("Chunks").GetValue(asset)).Add(chunk);

                List<Guid> found = AssetReferenceUtils.CollectChunkGuids(asset, id => id == named);
                CheckEqual(shortName + ": the id in its own Chunks list is found", 1, found.Count);
                CheckEqual(shortName + ": and it is the right id", named, found.Count == 1 ? found[0] : Guid.Empty);

                // An id this profile does not know is not collected, while the member rule still
                // names it - that difference is what the export's explanation rests on.
                CheckEqual(shortName + ": an id the asset manager does not know is not collected", 0,
                    AssetReferenceUtils.CollectChunkGuids(asset, id => id == unknown).Count);
                List<Guid> byMember = AssetReferenceUtils.CollectChunkGuidsFromMembers(asset, "chunk");
                CheckEqual(shortName + ": the member rule reports it whether or not it is loaded", named,
                    byMember.Count == 1 ? byMember[0] : Guid.Empty);

                walked++;
            }
            catch (Exception ex)
            {
                Check(shortName + " can be built into a real graph", false, ex.Message);
            }
        }

        Check("the walk ran against all three real sound classes", walked == 3, "only " + walked + " of 3");

        // A folder export runs the walk once per asset, so its cost matters: the members of each type
        // are reflected once and reused afterwards. Measure it on a real asset with 500 chunks.
        Type waveType = sdk.GetType("FrostySdk.Ebx.SoundWaveAsset");
        try
        {
            object wave = Activator.CreateInstance(waveType);
            IList chunks = (IList)waveType.GetProperty("Chunks").GetValue(wave);
            for (int i = 0; i < 500; i++)
            {
                object chunk = Activator.CreateInstance(chunkType);
                chunkType.GetProperty("ChunkId").SetValue(chunk, Guid.NewGuid());
                chunks.Add(chunk);
            }

            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
                AssetReferenceUtils.CollectChunkGuids(wave, id => true);
            watch.Stop();

            Console.WriteLine("  note  200 walks of a real 500 chunk asset: " + watch.ElapsedMilliseconds + " ms");
            Check("a real asset walks fast enough for a folder export", watch.ElapsedMilliseconds < 10000,
                "200 walks took " + watch.ElapsedMilliseconds + " ms");
        }
        catch (Exception ex)
        {
            Check("a real 500 chunk asset can be walked", false, ex.Message);
        }
    }

    // ---- the two Chunk/Res Editor commands that name their own scope ----------------------------

    private static void EditorFlows()
    {
        BulkOptions chunks = BulkOptionDefaults.ForScope(BulkScope.Chunks);
        CheckEqual("a chunk run writes chunks", true, chunks.Chunks);
        CheckEqual("a chunk run writes no ebx", false, chunks.AnyEbx);
        CheckEqual("a chunk run writes no resources", false, chunks.Res);
        CheckEqual("a chunk run writes nothing else either", "Bulk export: chunks", chunks.Describe("Bulk export"));
        CheckEqual("a chunk run counts as a selection", true, chunks.AnySelected);

        BulkOptions res = BulkOptionDefaults.ForScope(BulkScope.Res);
        CheckEqual("a res run writes resources", true, res.Res);
        CheckEqual("a res run writes no chunks", false, res.Chunks);
        CheckEqual("a res run writes no ebx", false, res.AnyEbx);
        CheckEqual("a res run writes nothing else either", "Bulk export: res", res.Describe("Bulk export"));
        CheckEqual("a res run counts as a selection", true, res.AnySelected);

        // A scoped run is its own object: the shared defaults are what the Data Explorer's dialog
        // writes the user's choices into, and a scoped run must not disturb them.
        CheckEqual("a scoped run is not the shared defaults", false, ReferenceEquals(res, BulkOptionDefaults.Export));
        bool sharedChunksBefore = BulkOptionDefaults.Export.Chunks;
        BulkOptionDefaults.ForScope(BulkScope.Res);
        CheckEqual("a scoped run leaves the shared defaults alone", sharedChunksBefore, BulkOptionDefaults.Export.Chunks);

        // What the commands themselves do. A window there would only repeat the scope the user has
        // already chosen, so the source of the two methods is checked for the dialogs they must not
        // open; the built methods are covered by the artifact checks.
        string editorSource = Path.Combine(WorkspaceRoot, "Plugins", "ChunkResExplorerPlugin", "FrostyChunkResEditor.cs");
        string chunkExport = MethodSource(editorSource, "public void BulkExportChunks()");
        string resExport = MethodSource(editorSource, "private void ResBulkExportMenuItem_Click(object sender, RoutedEventArgs e)");

        Check("the chunk export method is where the harness expects it", chunkExport != null, editorSource + " not read, or the method moved?");
        Check("the res export method is where the harness expects it", resExport != null, editorSource + " not read, or the method moved?");
        if (chunkExport == null || resExport == null)
            return;

        Check("the chunk export opens no options dialog", !chunkExport.Contains("BulkExportOptionsWindow"), "the options window is still opened there");
        Check("the chunk export shows no confirmation", !chunkExport.Contains("FrostyMessageBox"), "a confirmation is still shown there");
        Check("the chunk export still asks where to write", chunkExport.Contains("FolderBrowserDialog"), "no output folder is chosen");
        Check("the chunk export still runs the exporter on the listed chunks", chunkExport.Contains("AssetBulkExporter.Run"), "the run is gone");
        Check("the chunk export runs the chunk scope", chunkExport.Contains("BulkScope.Chunks"), "the scope is not chunks");

        Check("the res export opens no options dialog", !resExport.Contains("BulkExportOptionsWindow"), "the options window is still opened there");
        Check("the res export shows no confirmation", !resExport.Contains("FrostyMessageBox"), "a confirmation is still shown there");
        Check("the res export still asks where to write", resExport.Contains("FolderBrowserDialog"), "no output folder is chosen");
        Check("the res export still runs the exporter on the folder's resources", resExport.Contains("AssetBulkExporter.Run"), "the run is gone");
        Check("the res export runs the res scope", resExport.Contains("BulkScope.Res"), "the scope is not res");

        // The extractor has to have stopped inside the method, or the checks above would be reading
        // the export that follows it.
        Check("the chunk export body is the one being checked", chunkExport.Contains("Bulk Exporting Chunks"), "wrong body extracted");
        Check("the chunk export body stops before the chunk import", !chunkExport.Contains("Bulk Importing Chunks"), "the extraction ran past the method");
        Check("the res export body is the one being checked", resExport.Contains("Bulk Exporting Resources"), "wrong body extracted");
        Check("the res export body stops before the res import", !resExport.Contains("Bulk Importing Resources"), "the extraction ran past the method");

        // The import commands keep their dialogs on purpose: an import overwrites assets, so it is
        // worth a window that says what is about to be read into what.
        string chunkImport = MethodSource(editorSource, "public void BulkImportChunks()");
        Check("the chunk import still asks what to read", chunkImport != null && chunkImport.Contains("BulkImportOptionsWindow"),
            "the import dialog went missing too");

        // Cancelling a run: the flag the task window's button sets, what a stopped run reports, and
        // the wiring that puts the button in front of every bulk command.
        BulkContext.Begin(new BulkOptions());
        CheckEqual("a run starts uncancelled", false, BulkContext.Cancelled);
        BulkContext.Cancel();
        CheckEqual("pressing cancel is visible to the run", true, BulkContext.Cancelled);
        BulkContext.End();
        BulkContext.Begin(new BulkOptions());
        CheckEqual("the next run starts uncancelled again", false, BulkContext.Cancelled);
        BulkContext.End();

        BulkResult stopped = new BulkResult { Done = 12, Cancelled = true };
        CheckEqual("a cancelled run says so in its summary", "12 done, 0 skipped, 0 failed, cancelled", stopped.Describe());
        CheckEqual("a run that was not cancelled reports as before", "12 done, 0 skipped, 0 failed",
            new BulkResult { Done = 12 }.Describe());

        // Both engines have to look at the flag between items, or the button would be decoration.
        string exporterSource = Path.Combine(WorkspaceRoot, "FrostyPlugin", "Bulk", "AssetBulkExporter.cs");
        string importerSource = Path.Combine(WorkspaceRoot, "FrostyPlugin", "Bulk", "AssetBulkImporter.cs");
        string exportRun = MethodSource(exporterSource, "public static BulkResult Run(string folderPrefix, BulkOptions options, string outputRoot,");
        string importRun = MethodSource(importerSource, "public static BulkResult Run(string folderPrefix, BulkOptions options, string inputRoot, FrostyTaskWindow task)");
        Check("the export run checks for a cancel request", exportRun != null && exportRun.Contains("BulkContext.Cancelled"), "the run cannot be stopped");
        Check("the import run checks for a cancel request", importRun != null && importRun.Contains("BulkContext.Cancelled"), "the run cannot be stopped");

        // And every engine call site has to offer the button, counted rather than assumed.
        string editorText = File.ReadAllText(editorSource);
        string mainSource = Path.Combine(WorkspaceRoot, "FrostyEditor", "Windows", "MainWindow.xaml.cs");
        string mainText = File.ReadAllText(mainSource);
        int editorRuns = Count("AssetBulkExporter.Run", editorText) + Count("AssetBulkImporter.Run", editorText);
        int mainRuns = Count("AssetBulkExporter.Run", mainText) + Count("AssetBulkImporter.Run", mainText);
        CheckEqual("every Chunk/Res editor bulk run offers the cancel button", editorRuns, Count("BulkContext.TaskCancel", editorText));
        CheckEqual("every Data Explorer bulk run offers the cancel button", mainRuns, Count("BulkContext.TaskCancel", mainText));
        Check("the Data Explorer really has bulk runs to wire", mainRuns != 0, mainSource + " has no bulk run call");
    }

    /// <summary>How often <paramref name="needle"/> appears in <paramref name="text"/>.</summary>
    private static int Count(string needle, string text)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>The workspace root: the harness runs from .freebuff/verify, two folders below it.</summary>
    private static string WorkspaceRoot
    {
        get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..")); }
    }

    /// <summary>
    /// The source of one method, from its declaration to the brace that closes it. Braces inside
    /// string literals and line comments are skipped, because a method that logs "{0}" would
    /// otherwise look like it ended early.
    /// </summary>
    private static string MethodSource(string path, string declaration)
    {
        if (!File.Exists(path))
            return null;

        string source = File.ReadAllText(path);
        int start = source.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0)
            return null;

        int open = source.IndexOf('{', start);
        if (open < 0)
            return null;

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '"')
            {
                i = SkipString(source, i);
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int lineEnd = source.IndexOf('\n', i);
                if (lineEnd < 0)
                    break;
                i = lineEnd;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(open, i - open + 1);
            }
        }

        return null;
    }

    /// <summary>The index of the quote that closes the string literal starting at <paramref name="start"/>.</summary>
    private static int SkipString(string source, int start)
    {
        for (int i = start + 1; i < source.Length; i++)
        {
            if (source[i] == '\\')
            {
                i++;
                continue;
            }

            if (source[i] == '"')
                return i;
        }

        return source.Length - 1;
    }

    private static void SkeletonRules()
    {
        // The real skeleton assets, named exactly as the dump lays them out.
        List<string> skeletons = new List<string>
        {
            "content/character/rig/skeleton/player/skeleton_player",
            "content/character/rig/skeleton/player/skeleton_ball",
            "content/character/rig/skeleton/player/skeleton_crowd",
            "content/character/rig/skeleton/goalpost/skeleton_goalpost",
            "content/character/rig/skeleton/boomcamera/skeleton_boomcamera",
            "content/worlds/trophy/trophy_7/trophy_7_skeleton"
        };

        // Paths taken from the dump, so these are the real folders the game uses.
        CheckEqual("a player mesh folder works out the player skeleton", "player",
            BulkSkeletons.GuessToken("content/character/player/player_0/andres_iniesta_lujan_41/hair_41_0_0_mesh"));
        CheckEqual("a player head mesh works out the player skeleton", "player",
            BulkSkeletons.GuessToken("content/character/player/player_0/andres_iniesta_lujan_41/head_41_0_mesh"));
        CheckEqual("a kit works out the player skeleton", "player",
            BulkSkeletons.GuessToken("content/character/kit/kit_0/kit_0_mesh"));
        CheckEqual("an accessory works out the player skeleton", "player",
            BulkSkeletons.GuessToken("content/character/accessory/ankle_tapes_14/accessory_14_0_0_mesh"));
        CheckEqual("a ball mesh works out the ball skeleton", "ball",
            BulkSkeletons.GuessToken("content/character/ball/ball_0/ball_0_mesh"));
        CheckEqual("the ball skeleton is found by name", "content/character/rig/skeleton/player/skeleton_ball",
            BulkSkeletons.PickByName(skeletons, "ball"));
        CheckEqual("the player skeleton is found by name", "content/character/rig/skeleton/player/skeleton_player",
            BulkSkeletons.PickByName(skeletons, "player"));
        CheckEqual("the crowd skeleton is found by name", "content/character/rig/skeleton/player/skeleton_crowd",
            BulkSkeletons.PickByName(skeletons, "crowd"));
        CheckEqual("a goalpost mesh works out the goalpost skeleton", "goalpost",
            BulkSkeletons.GuessToken("content/character/rig/goalpost/goalpost_0_mesh"));
        CheckEqual("the goalpost skeleton is found by name", "content/character/rig/skeleton/goalpost/skeleton_goalpost",
            BulkSkeletons.PickByName(skeletons, "goalpost"));
        CheckEqual("a trophy mesh works out the trophy skeleton", "trophy",
            BulkSkeletons.GuessToken("content/worlds/trophy/trophy_7/trophy_7_mesh"));
        CheckEqual("an unrelated asset works out no skeleton", "",
            BulkSkeletons.GuessToken("content/ui/imgAssets/heads/p20801"));

        // Trophies keep their skeleton in the trophy's own folder.
        List<KeyValuePair<string, string>> paths = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("content/character/rig/skeleton/player/skeleton_player", "content/character/rig/skeleton/player"),
            new KeyValuePair<string, string>("content/worlds/trophy/trophy_7/trophy_7_skeleton", "content/worlds/trophy/trophy_7")
        };
        CheckEqual("a trophy skeleton is found in the trophy's own folder", "content/worlds/trophy/trophy_7/trophy_7_skeleton",
            BulkSkeletons.PickInFolder(paths, "content/worlds/trophy/trophy_7"));

        // An explicit pick beats the folder rule, without needing a loaded game.
        BulkOptions explicitPick = new BulkOptions { SkeletonAuto = false, Skeleton = "content/character/rig/skeleton/player/skeleton_crowd" };
        CheckEqual("an explicit skeleton is used as it is", "content/character/rig/skeleton/player/skeleton_crowd",
            BulkSkeletons.Resolve(explicitPick, "content/character/ball/ball_0", "content/character/ball/ball_0/ball_0_mesh"));

        BulkContext.Begin(explicitPick);
        CheckEqual("the bulk run hands its skeleton to the asset definitions", "content/character/rig/skeleton/player/skeleton_crowd", BulkContext.Skeleton);
        CheckEqual("and marks itself silent", true, BulkContext.Silent);
        BulkContext.End();
        CheckEqual("the bulk run clears up after itself", null, BulkContext.Skeleton);
        CheckEqual("and is no longer silent", false, BulkContext.Silent);
    }

    private static void XmlRoundTrip()
    {
        string ballFile = Path.Combine(SkeletonDir, "skeleton_ball.xml");
        if (!File.Exists(ballFile))
        {
            Check("the dump is present", false, ballFile + " not found");
            return;
        }

        List<EbxXmlClassNode> nodes = EbxXmlReader.ParseFile(ballFile);
        CheckEqual("the ball skeleton parses to one class", 1, nodes.Count);
        CheckEqual("the class is a SkeletonAsset", "SkeletonAsset", nodes[0].ClassName);
        CheckEqual("the instance guid is read", "50a3716d-c1ff-4caf-10d4-6c86794bea02", nodes[0].Guid);

        EbxXmlPropertyNode name = nodes[0].Find("Name");
        Check("Name is read as a leaf", name != null && name.IsLeaf, name == null ? "missing" : "not a leaf");
        CheckEqual("Name holds the asset name", "content/character/rig/skeleton/player/skeleton_ball", name == null ? null : name.Value);

        EbxXmlPropertyNode boneNames = nodes[0].Find("BoneNames");
        Check("BoneNames is read as a list", boneNames != null && boneNames.IsList, boneNames == null ? "missing" : "not a list");
        CheckEqual("BoneNames knows its count", 4, boneNames == null ? -1 : boneNames.Count);
        CheckEqual("BoneNames keeps its members", 4, boneNames == null ? -1 : boneNames.Members.Count);
        CheckEqual("members keep their order", "AITrajectory", boneNames == null ? null : boneNames.Members[1].Value);

        EbxXmlPropertyNode pose = nodes[0].Find("LocalPose");
        Check("LocalPose is a list of nested classes", pose != null && pose.IsList && pose.Members[0].IsClass,
            pose == null ? "missing" : "not nested classes");
        EbxXmlClassNode vec3 = pose == null ? null : pose.Members[0].Class.Find("right").Class;
        CheckEqual("the nested class four levels down names itself", "Vec3", vec3 == null ? null : vec3.ClassName);
        CheckEqual("a value five levels down is read", "1", vec3 == null ? null : vec3.Find("x").Value);

        // The big one: 1.4 MB, 438 bones, a pose per bone.
        string playerFile = Path.Combine(SkeletonDir, "skeleton_player.xml");
        List<EbxXmlClassNode> player = EbxXmlReader.ParseFile(playerFile);
        CheckEqual("the player skeleton parses to one class", 1, player.Count);
        CheckEqual("the player class is a SkeletonAsset", "SkeletonAsset", player[0].ClassName);
        EbxXmlPropertyNode playerBones = player[0].Find("BoneNames");
        CheckEqual("the player skeleton has all 438 bones", 438, playerBones == null ? -1 : playerBones.Members.Count);

        // A dump that mentions nothing about this asset must be refused rather than half applied.
        string temporary = Path.Combine(Path.GetTempPath(), "bulkcheck_other.xml");
        File.WriteAllText(temporary, "<SomethingElse Guid=\"11111111-1111-1111-1111-111111111111\">\r\n    <Name>not this asset</Name>\r\n</SomethingElse>\r\n");
        List<EbxXmlClassNode> other = EbxXmlReader.ParseFile(temporary);
        CheckEqual("an unrelated dump still parses", "SomethingElse", other[0].ClassName);
        Check("the dump is not mistaken for the asset",
            !other[0].ClassName.Equals(player[0].ClassName, StringComparison.OrdinalIgnoreCase), "same class name");
        File.Delete(temporary);
    }

    private static void Artifacts()
    {
        CheckOverride("MeshSetPlugin.dll", "MeshSetPlugin.MeshAssetDefinition", "ExportSilent");
        CheckOverride("MeshSetPlugin.dll", "MeshSetPlugin.MeshAssetDefinition", "ImportSilent");
        CheckOverride("TexturePlugin.dll", "TexturePlugin.TextureAssetDefinition", "ImportSilent");
        CheckOverride("ChunkResEditorPlugin.dll", "ChunkResEditorPlugin.FrostyChunkResEditor", "BulkExportChunks");
        CheckOverride("ChunkResEditorPlugin.dll", "ChunkResEditorPlugin.FrostyChunkResEditor", "BulkImportChunks");

        CheckMetadata("FrostyEditor.exe", "BulkExportOptionsWindow", "BulkImportOptionsWindow",
            "AssetBulkExporter", "AssetBulkImporter", "BulkOptionDefaults", "BulkReport");
        CheckMetadata("FrostyCore.dll", "LeafNameConverter", "BulkOptionDefaults", "AssetBulkExporter", "AssetBulkImporter",
            "ChosenTextureFormats", "CanWriteTexture", "WroteSomething",
            "CollectChunkGuids", "ForScope", "GetEbxChunkIds", "GetNamedResEntry", "TaskCancel",
            "CollectChunkGuidsFromMembers", "ExplainNoChunks", "NamesChunks", "BulkReport");
        CheckMetadata("MeshSetPlugin.dll", "ExportSilent", "ImportSilent", "BulkContext", "MeshFileType");
        CheckMetadata("TexturePlugin.dll", "ImportSilent", "ImportTexture2D");
        CheckMetadata("ChunkResEditorPlugin.dll", "AssetBulkExporter", "AssetBulkImporter", "BulkScope",
            "TaskCancel", "ExplainNoChunks", "WriteRunReport");
        CheckMetadata("FrostySdk.dll", "EbxXmlReader", "EbxXmlApplier");
    }

    // ---- what has to be true for a change to reach the editor at all ----------------------------

    /// <summary>
    /// Every source file these projects hold has to be listed in their csproj, because the three
    /// projects that matter list their files by hand. A file that is not listed still sits in the
    /// tree and still looks like part of a change, but it is not in the assembly that runs - which is
    /// how a diagnostic can be written, called verified, and absent from the editor at the same time.
    /// </summary>
    private static void SourceFilesAreCompiled()
    {
        CheckProjectFiles(Path.Combine("FrostyPlugin"), Path.Combine("FrostyPlugin", "FrostyCore.csproj"));
        CheckProjectFiles(Path.Combine("FrostyEditor"), Path.Combine("FrostyEditor", "FrostyEditor.csproj"));

        // The chunk/res plugin is an SDK style project: it compiles every source file it holds, so
        // there is nothing to compare and nothing to get wrong here.
        string pluginProject = Path.Combine(WorkspaceRoot, "Plugins", "ChunkResExplorerPlugin", "ChunkResEditorPlugin.csproj");
        string pluginText = File.ReadAllText(pluginProject);
        Check("the chunk/res plugin compiles everything it holds",
            !pluginText.Contains("<Compile Include"), "it lists files by hand now - check them here too");
    }

    private static void CheckProjectFiles(string folder, string project)
    {
        string projectPath = Path.Combine(WorkspaceRoot, project);
        if (!File.Exists(projectPath))
        {
            Check("the project file is there to read", false, projectPath + " not found");
            return;
        }

        List<string> listed = new List<string>();
        foreach (Match match in Regex.Matches(File.ReadAllText(projectPath), "<Compile Include=\"([^\"]+)\""))
            listed.Add(match.Groups[1].Value.Replace('/', '\\'));

        List<string> missing = new List<string>();
        foreach (string path in Directory.GetFiles(Path.Combine(WorkspaceRoot, folder), "*.cs", SearchOption.AllDirectories))
        {
            string relative = path.Substring(Path.Combine(WorkspaceRoot, folder).Length + 1);
            if (relative.StartsWith("obj\\", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!listed.Contains(relative))
                missing.Add(relative);
        }

        Check(project.Replace('\\', '/') + " compiles every one of its " + listed.Count + " source files",
            missing.Count == 0,
            "not listed, so not compiled: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The report a run leaves beside its output. The log window is the only other place a run's
    /// notes go and it is gone when the editor closes, so this is what a run that wrote no chunk
    /// file at all is read from afterwards - by whoever picks the problem up next.
    /// </summary>
    private static void RunReport()
    {
        BulkResult result = new BulkResult { Done = 2, Chunks = 1 };
        result.Messages.Add("no chunk for sound/x - it names 3 chunk id(s) this profile does not have");
        result.Details.Add("sound/x: 0 of 3 chunk id(s) are in this profile");
        result.Details.Add("  11111111-2222-3333-4444-555555555555 is not in this profile");

        CheckEqual("the report is named after what it is", "bulk-export-report.txt",
            Path.GetFileName(BulkReport.PathFor(Path.Combine("C:", "out"))));

        string text = BulkReport.Text("Bulk export", "sound", Path.Combine("C:", "out"),
            "Bulk export: chunks, res", result, result.Details);
        Check("the report says what was asked for", text.Contains("Bulk export: chunks, res"), "no description");
        Check("the report names the folder that was exported", text.Contains("folder: sound"), "no folder");
        Check("the report repeats the run's summary", text.Contains("2 done") && text.Contains("1 chunks"), "no summary");
        Check("the report carries the run's notes", text.Contains("it names 3 chunk id(s)"), "no note");
        Check("the report carries the chunk lookup", text.Contains("is not in this profile"), "no detail");

        string path = Path.Combine(Path.GetTempPath(), "frosty-bulk-report-check.txt");
        Check("writing the report says it worked", BulkReport.Write(path, text), "write refused");
        CheckEqual("the bytes on disk are the report", text, File.ReadAllText(path));
        File.Delete(path);

        Check("a report that cannot be written is not an exception",
            !BulkReport.Write(Path.Combine(Path.GetTempPath(), "frosty-no-such-folder", "x.txt"), text),
            "a bad path threw or claimed success");

        // And the commands that produce a run have to write one: a report nothing calls is the same
        // silence as no report at all.
        string explorer = Path.Combine(WorkspaceRoot, "FrostyEditor", "Windows", "MainWindow.xaml.cs");
        string exportFolder = MethodSource(explorer, "private void contextMenuBulkExportEbxFolder_Click(object sender, RoutedEventArgs e)");
        Check("the data explorer folder export writes the report",
            exportFolder != null && exportFolder.Contains("BulkReport.Write"), "the folder export leaves no report");

        string chunkEditor = Path.Combine(WorkspaceRoot, "Plugins", "ChunkResExplorerPlugin", "FrostyChunkResEditor.cs");
        string resExport = MethodSource(chunkEditor, "private void ResBulkExportMenuItem_Click(object sender, RoutedEventArgs e)");
        Check("the res export of the chunk/res editor writes the report",
            resExport != null && resExport.Contains("WriteRunReport"), "the res export leaves no report");
        string chunkExport = MethodSource(chunkEditor, "public void BulkExportChunks()");
        Check("the chunk list export writes the report",
            chunkExport != null && chunkExport.Contains("WriteRunReport"), "the chunk export leaves no report");
    }
}
