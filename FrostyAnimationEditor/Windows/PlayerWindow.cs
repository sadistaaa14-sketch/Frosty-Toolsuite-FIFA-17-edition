using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Frosty.Controls;
using FrostyAnimationEditor.Core;
using FrostyAnimationEditor.Core.Anim;
using FrostyAnimationEditor.Plugin;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>
    /// The player view: a clip on the player's render skeleton (EBX skeleton_player) with the player's skinned meshes
    /// from the project, playback, FBX export (skeleton + animation, optionally the meshes) and FBX import.
    /// </summary>
    internal sealed class PlayerWindow : FrostyWindow
    {
        private static PlayerWindow instance;

        public static void Open(EditorWindow owner, string clipKey)
        {
            Ensure(owner);
            instance.LoadClip(clipKey);
        }

        /// <summary>play a sequence (SequenceContainerAsset: every actor; ActorControllerAsset: that actor)</summary>
        public static void OpenSequence(EditorWindow owner, string seqKey)
        {
            Ensure(owner);
            instance.LoadSequence(seqKey);
        }

        private static void Ensure(EditorWindow owner)
        {
            if (instance == null)
            {
                instance = new PlayerWindow(owner);
                instance.Closed += (s, e) => instance = null;
                instance.Show();
            }
            else
            {
                if (instance.WindowState == WindowState.Minimized) instance.WindowState = WindowState.Normal;
                instance.Activate();
            }
        }

        /// <summary>a clip controller's timeline was clicked: show the clip frame of that controller time (when its
        /// clip is shown)</summary>
        public static void ShowControllerTicks(string controllerKey, double ticks)
        {
            PlayerWindow w = instance;
            if (w == null || w.anim == null || w.seqKey != null || w.clipKey == null) return;
            double frame;
            if (!w.M.ControllerFrame(controllerKey, w.clipKey, ticks, out frame)) return;
            w.SetPlaying(false);
            w.ShowFrame((int)Math.Round(frame));
        }

        /// <summary>the sequence timeline was clicked: show that sequence time (when this sequence is shown)</summary>
        public static void ShowSequenceTime(string key, double ticks)
        {
            PlayerWindow w = instance;
            if (w == null || w.anim == null || w.seqKey != key) return;
            w.SetPlaying(false);
            w.ShowFrame((int)Math.Round(ticks / w.ticksPerFrame), false);
        }

        public static void CloseAll()
        {
            if (instance != null) instance.Close();
            lock (meshCache) meshCache.Clear();
        }

        /// <summary>the editor's data changed (undo, redo, another file): show the clip again</summary>
        public static void Refresh()
        {
            if (instance == null) return;
            if (instance.seqKey != null) instance.LoadSequence(instance.seqKey, true);
            else if (instance.clipKey != null) instance.LoadClip(instance.clipKey, true);
        }

        private readonly EditorWindow ed;
        private EditorModel M { get { return ed.M; } }
        private readonly PlayerView view = new PlayerView();
        private PlayerAnimation anim;
        private string clipKey;
        private string seqKey;              // the sequence shown (clipKey is null then)
        private double ticksPerFrame = 2;   // sequence ticks per frame of the baked sequence
        private readonly List<MeshPart> meshes = new List<MeshPart>();
        private string meshesFor;   // parts + lod the loaded meshes are for
        private static readonly Dictionary<string, MeshPart> meshCache = new Dictionary<string, MeshPart>();
        private readonly List<string> notes = new List<string>();

        private readonly TextBlock title = Ui.T("", null, 14, true);
        private readonly TextBlock sub = Ui.Trim(Ui.T("", Ui.Muted, 11));
        private readonly Button bPlay, bImport, bAdd;
        private readonly Slider slider = new Slider { Minimum = 0, Maximum = 1, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock frameText = Ui.T("", Ui.Muted, 11);
        private readonly ComboBox speed = Ui.Combo(new[] { Ui.KV("0.25", "0.25×"), Ui.KV("0.5", "0.5×"), Ui.KV("1", "1×"), Ui.KV("2", "2×") }, "1", 70);
        private readonly CheckBox cMesh = Ui.Check("mesh", true), cBones = Ui.Check("bones", false), cFollow = Ui.Check("follow", true, "keep the camera on the player"),
                                  cInPlace = Ui.Check("in place", false, "cancel the trajectory's movement on the ground"), cLoop = Ui.Check("loop", true);
        private readonly TextBlock status = Ui.Trim(Ui.T("", Ui.Muted, 11));
        private readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        private readonly Stopwatch clock = new Stopwatch();
        private double startFrame;
        private int shown = -1;
        private bool playing, sliding;
        private int trajBone = -1, hipsBone = -1;
        private double groundX, groundZ;

        private PlayerWindow(EditorWindow owner)
        {
            ed = owner;
            Title = "Player view";
            Width = Math.Min(1100, SystemParameters.WorkArea.Width - 80);
            Height = Math.Min(860, SystemParameters.WorkArea.Height - 80);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Back;
            ShowInTaskbar = true;

            Button bClip = Ui.B("Clip…", PickClip, "show another clip");
            Button bEditor = Ui.B("Open in editor", () => { string k = seqKey ?? clipKey; if (k != null) { ed.Go(k); ed.Activate(); } });
            Button bParts = Ui.B("Meshes…", ChooseParts, "which of the project's character meshes to show");
            Button bExport = Ui.B("Export FBX…", ExportFbx, "skeleton and animation (and the meshes) as .fbx");
            bImport = Ui.B("Import FBX…", () => ImportFbx(false), "replace this clip with the animation of an .fbx file");
            bAdd = Ui.B("Add from FBX…", () => ImportFbx(true), "add a new clip (and controller) from an .fbx file");
            DockPanel bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            StackPanel right = Ui.H(bClip, bEditor, Ui.Pad(new Separator { Width = 1 }, 4, 0, 4, 0), bParts, bExport, bImport, bAdd);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            bar.Children.Add(Ui.V(title, sub));

            bPlay = Ui.B("Pause", TogglePlay);
            bPlay.Width = 64;
            slider.ValueChanged += (s, e) =>
            {
                if (sliding || anim == null) return;
                SetPlaying(false);
                ShowFrame((int)slider.Value);
            };
            speed.SelectionChanged += (s, e) => { if (playing) Restart(shown); };
            cMesh.Checked += (s, e) => Visibility3D(); cMesh.Unchecked += (s, e) => Visibility3D();
            cBones.Checked += (s, e) => Visibility3D(); cBones.Unchecked += (s, e) => Visibility3D();
            cFollow.Checked += (s, e) => Redraw(); cFollow.Unchecked += (s, e) => Redraw();
            cInPlace.Checked += (s, e) => { PlaceGround(); Redraw(); };
            cInPlace.Unchecked += (s, e) => { PlaceGround(); Redraw(); };
            Grid play = new Grid { Margin = new Thickness(8, 4, 8, 2) };
            play.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            play.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            play.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            UIElement opts = Ui.H(Ui.Pad(frameText, 8, 0, 8, 0), speed, Ui.Pad(cLoop, 8, 0, 0, 0), Ui.Pad(cFollow, 8, 0, 0, 0), Ui.Pad(cInPlace, 8, 0, 0, 0),
                                  Ui.Pad(cMesh, 8, 0, 0, 0), Ui.Pad(cBones, 8, 0, 0, 0));
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(opts, 2);
            play.Children.Add(bPlay);
            play.Children.Add(slider);
            play.Children.Add(opts);
            DockPanel bottom = new DockPanel();
            DockPanel.SetDock(play, Dock.Top);
            bottom.Children.Add(play);
            TextBlock help = Ui.T("drag: orbit · right drag: pan · wheel: zoom · double-click: reset", Ui.Muted, 11);
            DockPanel.SetDock(help, Dock.Right);
            DockPanel st = new DockPanel { Margin = new Thickness(8, 0, 8, 5) };
            st.Children.Add(help);
            st.Children.Add(status);
            status.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) ShowNotes(); };
            status.ToolTip = "double-click for the details";
            bottom.Children.Add(st);

            DockPanel root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(bottom);
            view.Margin = new Thickness(8, 0, 8, 4);
            root.Children.Add(view);
            Content = root;
            view.Reset += () => { if (anim != null) Redraw(); };

            timer.Tick += (s, e) => Tick();
            Closed += (s, e) => timer.Stop();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Space && !(Keyboard.FocusedElement is TextBox)) { e.Handled = true; TogglePlay(); }
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Left && anim != null) { e.Handled = true; SetPlaying(false); ShowFrame(Math.Max(0, shown - 1)); }
                else if (e.Key == Key.Right && anim != null) { e.Handled = true; SetPlaying(false); ShowFrame(Math.Min(anim.FrameCount - 1, shown + 1)); }
            };
        }

        private T Busy<T>(string what, Func<Action<string, double>, T> job) { return Work.Run(this, "Player view", what, job); }

        private void Report(Exception e)
        {
            status.Text = e.Message;
            status.Foreground = Ui.Bad;
            ed.Report(e);
        }

        // ------------------------------------------------------------------ clip
        public void LoadClip(string key, bool keepFrame = false)
        {
            int frame = keepFrame ? Math.Max(0, shown) : 0;
            PlayerAnimation a;
            try { a = Busy("evaluating the clip on the skeleton", p => M.ClipOnSkeleton(key)); }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { Report(e); return; }
            anim = a;
            clipKey = key;
            seqKey = null;
            bImport.IsEnabled = bAdd.IsEnabled = true;
            trajBone = a.Skeleton.Find("AITrajectory");
            hipsBone = a.Skeleton.Find("Hips");
            JObject info = M.Info(a.ClipKey);
            title.Text = Ui.Label(info);
            List<string> s = new List<string>();
            if (info.Str("alias") != null) s.Add(info.Str("name"));
            s.Add(EditorModel.H(a.ClipKey));
            s.Add(a.FrameCount + " frames at " + Ui.Num(a.Fps) + " fps");
            if (a.RigName.Length > 0) s.Add("rig " + a.RigName);
            sub.Text = string.Join(" · ", s);
            Title = "Player view · " + title.Text;
            notes.Clear();
            notes.Add("skeleton: " + a.Skeleton.Source + ", " + a.Skeleton.Count + " bones" + (a.Skeleton.Extra.Count > 0 ? " (" + a.Skeleton.Extra.Count + " added for clip joints)" : ""));
            notes.Add("clip: " + a.MappedJoints + " of " + a.ChannelJoints + " animated joints on the skeleton; the other bones take the defaults of " +
                      (a.RigName.Length > 0 ? a.RigName : "the rig") + (a.DefaultsRig.Length > 0 && a.DefaultsRig != a.RigName ? ", then " + a.DefaultsRig : ""));
            notes.AddRange(a.Notes);
            EnsureMeshes();
            slider.Maximum = Math.Max(0, a.FrameCount - 1);
            PlaceGround();
            if (!keepFrame) view.PanOffset = new System.Windows.Media.Media3D.Vector3D();
            shown = -1;
            ShowFrame(Math.Min(frame, a.FrameCount - 1));
            UpdateStatus();
            if (!keepFrame) SetPlaying(true);
        }

        public void LoadSequence(string key, bool keepFrame = false)
        {
            int frame = keepFrame ? Math.Max(0, shown) : 0;
            Sequence.Baked b;
            try { b = Busy("evaluating the sequence on the skeleton", p => M.SeqOnSkeleton(key)); }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { Report(e); return; }
            PlayerAnimation a = b.Anim;
            anim = a;
            seqKey = key;
            clipKey = null;
            ticksPerFrame = b.TicksPerFrame;
            bImport.IsEnabled = bAdd.IsEnabled = false;
            trajBone = a.Skeleton.Find("AITrajectory");
            hipsBone = a.Skeleton.Find("Hips");
            JObject info = M.Info(a.ClipKey);
            title.Text = Ui.Label(info);
            double ticks = (a.FrameCount - 1) * ticksPerFrame;
            List<string> s = new List<string>();
            s.Add(info.Str("type"));
            s.Add(EditorModel.H(a.ClipKey));
            if (b.Actors > 1) s.Add(b.Actors + " actors");
            s.Add(Ui.Num(ticks) + " ticks, " + a.FrameCount + " frames at " + Ui.Num(a.Fps) + " fps");
            sub.Text = string.Join(" · ", s);
            Title = "Player view · " + title.Text;
            notes.Clear();
            notes.Add("sequence: " + (b.Actors == 1 ? "1 actor" : b.Actors + " actors") + " on " + a.Skeleton.Source);
            notes.AddRange(b.Notes);
            notes.Add("time: 60 ticks a second is assumed (clip FPS 30 × controller TimeScale 0.5); the game's tick rate is not established");
            EnsureMeshes();
            slider.Maximum = Math.Max(0, a.FrameCount - 1);
            PlaceGround();
            if (!keepFrame) view.PanOffset = new System.Windows.Media.Media3D.Vector3D();
            shown = -1;
            ShowFrame(Math.Min(frame, a.FrameCount - 1));
            UpdateStatus();
            if (!keepFrame) SetPlaying(true);
        }

        private void PickClip()
        {
            Dialog d = new Dialog(this, "Show a clip", 700);
            ObjectPicker pk = new ObjectPicker(M, "ClipControllerAsset");
            string chosen = null;
            pk.Picked += o => { chosen = o.Str("key"); d.Accepted = true; d.Close(); };
            d.Add(Ui.Wrap("a clip (VbrAnimationAsset, …) or its ClipControllerAsset", Ui.Muted, 11), pk.Panel);
            d.Button("Cancel");
            d.ShowDialog();
            if (chosen != null) LoadClip(chosen);
        }

        // ------------------------------------------------------------------ meshes
        private bool OnRenderSkeleton()
        {
            PlayerSkeleton rs = M.RenderSkeleton();
            return rs != null && anim != null && anim.Skeleton.Name == rs.Name && anim.Skeleton.Source == rs.Source;
        }

        private List<string> PartNames(out int lod)
        {
            lod = PlayerAssets.SavedLod();
            List<string> saved = PlayerAssets.SavedParts();
            if (saved != null) return saved;
            return PlayerAssets.DefaultParts(PlayerAssets.CharacterMeshes());
        }

        private void EnsureMeshes(bool force = false)
        {
            if (!PlayerAssets.Available || !OnRenderSkeleton())
            {
                if (meshes.Count > 0) { meshes.Clear(); view.SetParts(meshes); }
                meshesFor = null;
                if (!PlayerAssets.Available) notes.Add("meshes: no Frosty project is loaded");
                else if (anim != null) notes.Add(seqKey != null ? "meshes: shown for a one-actor sequence only (the actors are drawn as bones)" : "meshes: the clip is not on the player skeleton");
                return;
            }
            int lod;
            List<string> names = PartNames(out lod);
            string sig = string.Join("|", names) + "#" + lod;
            if (!force && sig == meshesFor) { notes.Add("meshes: " + meshes.Count + " loaded"); return; }
            PlayerSkeleton rs = M.RenderSkeleton();
            List<string> loadNotes = new List<string>();
            List<MeshPart> got;
            try
            {
                got = Busy("loading the player meshes", progress =>
                {
                    List<MeshPart> r = new List<MeshPart>();
                    for (int i = 0; i < names.Count; i++)
                    {
                        progress("loading " + names[i], i / (double)Math.Max(1, names.Count));
                        string ck = names[i] + "#" + lod;
                        MeshPart mp;
                        lock (meshCache) meshCache.TryGetValue(ck, out mp);
                        if (mp == null)
                        {
                            try
                            {
                                PlayerAssets.MeshLoad ml = PlayerAssets.LoadMesh(names[i], rs, lod);
                                mp = ml.Mesh;
                                lock (loadNotes) loadNotes.AddRange(ml.Notes);
                                lock (meshCache) meshCache[ck] = mp;
                            }
                            catch (Exception e) when (EditorWindow.IsUserError(e))
                            {
                                lock (loadNotes) loadNotes.Add(names[i] + ": " + e.Message);
                                continue;
                            }
                        }
                        r.Add(mp);
                    }
                    return r;
                });
            }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { Report(e); return; }
            meshes.Clear();
            meshes.AddRange(got);
            meshesFor = sig;
            view.SetParts(meshes);
            notes.Add("meshes (lod " + lod + "): " + meshes.Count + " of " + names.Count + ", " + Ui.Thousands(meshes.Sum(m => (long)m.VertexCount)) + " vertices");
            notes.AddRange(loadNotes);
        }

        private void ChooseParts()
        {
            if (!PlayerAssets.Available) { Dialog.Info(this, "Meshes", "No Frosty project is loaded."); return; }
            List<string> all;
            try { all = PlayerAssets.CharacterMeshes(); }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { Report(e); return; }
            int lod;
            HashSet<string> chosen = new HashSet<string>(PartNames(out lod), StringComparer.OrdinalIgnoreCase);
            Dialog d = new Dialog(this, "Meshes of the player view", 760);
            TextBox filter = Ui.Box("", double.NaN, false, "filter by name");
            ListBox list = new ListBox { Height = 380, Margin = new Thickness(0, 4, 0, 4) };
            TextBlock count = Ui.T("", Ui.Muted, 11);
            ComboBox lodBox = Ui.Combo(new[] { Ui.KV("0", "lod 0 (most detail)"), Ui.KV("1", "lod 1"), Ui.KV("2", "lod 2"), Ui.KV("3", "lod 3") }, Math.Max(0, Math.Min(3, lod)).ToString(), 180);
            Action fill = null;
            Action recount = () => count.Text = chosen.Count + " chosen of " + all.Count + " character meshes";
            fill = () =>
            {
                list.Items.Clear();
                string q = filter.Text.Trim().ToLowerInvariant();
                IEnumerable<string> shown2 = all.Where(x => q.Length == 0 || x.ToLowerInvariant().Contains(q));
                if (q.Length == 0) shown2 = shown2.OrderBy(x => chosen.Contains(x) ? 0 : 1).ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
                foreach (string n in shown2.Take(1500))
                {
                    string nn = n;
                    CheckBox c = Ui.Check(n, chosen.Contains(n));
                    c.Checked += (s, e) => { chosen.Add(nn); recount(); };
                    c.Unchecked += (s, e) => { chosen.Remove(nn); recount(); };
                    list.Items.Add(c);
                }
                recount();
            };
            filter.TextChanged += (s, e) => fill();
            fill();
            d.Add(Ui.Wrap("SkinnedMeshAssets under content/character/ (body parts, kits, heads, hair, shoes). The choice is kept for the next sessions.", Ui.Muted, 11),
                  filter, list, Ui.H(lodBox, Ui.Pad(count, 10, 0, 0, 0)));
            d.Button("Defaults", () =>
            {
                chosen.Clear();
                foreach (string x in PlayerAssets.DefaultParts(all)) chosen.Add(x);
                fill();
                return false;
            });
            d.Button("Cancel");
            d.Button("Show", () => true, true);
            if (!d.Run()) return;
            int newLod;
            if (!int.TryParse(Ui.Value(lodBox) ?? "0", out newLod)) newLod = 0;
            PlayerAssets.SaveParts(all.Where(x => chosen.Contains(x)), newLod);
            notes.Clear();
            EnsureMeshes(true);
            Redraw();
            UpdateStatus();
        }

        // ------------------------------------------------------------------ playback
        private void TogglePlay() { SetPlaying(!playing); }

        private void SetPlaying(bool on)
        {
            if (anim == null) on = false;
            playing = on;
            bPlay.Content = on ? "Pause" : "Play";
            if (on)
            {
                if (anim != null && shown >= anim.FrameCount - 1 && !Ui.On(cLoop)) shown = 0;
                Restart(Math.Max(0, shown));
                timer.Start();
            }
            else
            {
                timer.Stop();
                clock.Stop();
            }
        }

        private double Speed { get { double v; return double.TryParse(Ui.Value(speed), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 1; } }

        private void Restart(int fromFrame)
        {
            startFrame = fromFrame;
            clock.Restart();
        }

        private void Tick()
        {
            if (!playing || anim == null) return;
            double f = startFrame + clock.Elapsed.TotalSeconds * anim.Fps * Speed;
            int n = anim.FrameCount;
            int fi;
            if (Ui.On(cLoop)) fi = (int)(f % n);
            else
            {
                fi = (int)Math.Min(n - 1, f);
                if (f >= n - 1) { ShowFrame(n - 1); SetPlaying(false); return; }
            }
            if (fi != shown) ShowFrame(fi);
        }

        private void PlaceGround()
        {
            if (anim == null) return;
            if (Ui.On(cInPlace) || trajBone < 0) { groundX = 0; groundZ = 0; }
            else
            {
                V3 t = anim.World(0)[trajBone].Translation;
                groundX = Math.Round(t.X);
                groundZ = Math.Round(t.Z);
            }
            view.BuildGround(groundX, groundZ);
        }

        private void Redraw()
        {
            if (anim == null) return;
            int f = Math.Max(0, shown);
            shown = -1;
            ShowFrame(f);
        }

        private void ShowFrame(int f, bool tellEditor = true)
        {
            if (anim == null) return;
            f = Math.Max(0, Math.Min(anim.FrameCount - 1, f));
            shown = f;
            Affine[] w = anim.World(f);
            if (Ui.On(cInPlace) && trajBone >= 0)
            {
                V3 t = w[trajBone].Translation;
                Affine back = Affine.Translate(new V3(-t.X, 0, -t.Z));
                for (int i = 0; i < w.Length; i++) w[i] = back * w[i];
            }
            if (Ui.On(cFollow))
            {
                V3 c = hipsBone >= 0 ? w[hipsBone].Translation : (trajBone >= 0 ? w[trajBone].Translation : V3.Zero);
                view.Target = new System.Windows.Media.Media3D.Point3D(c.X, 1.0, c.Z) + view.PanOffset;
                view.UpdateCamera();
                if (!Ui.On(cInPlace) && (Math.Abs(c.X - groundX) > 8 || Math.Abs(c.Z - groundZ) > 8))
                {
                    groundX = Math.Round(c.X);
                    groundZ = Math.Round(c.Z);
                    view.BuildGround(groundX, groundZ);
                }
            }
            view.SetPose(anim.Skeleton, w);
            sliding = true;
            slider.Value = f;
            sliding = false;
            frameText.Text = seqKey != null ? "tick " + Ui.Num(f * ticksPerFrame) + " / " + Ui.Num((anim.FrameCount - 1) * ticksPerFrame)
                                            : "frame " + f + " / " + (anim.FrameCount - 1);
            if (seqKey != null && tellEditor) ed.SequenceTimeShown(seqKey, f * ticksPerFrame);
        }

        private void Visibility3D()
        {
            view.SetVisibility(Ui.On(cMesh), Ui.On(cBones));
            Redraw();
        }

        private void UpdateStatus()
        {
            status.Foreground = Ui.Muted;
            status.Text = string.Join(" · ", notes.Take(3));
        }

        private void ShowNotes()
        {
            Dialog.Info(this, "Player view details", string.Join("\n", notes));
        }

        // ------------------------------------------------------------------ FBX
        private void ExportFbx()
        {
            if (anim == null) return;
            bool withMesh = false;
            if (meshes.Count > 0)
            {
                Dialog d = new Dialog(this, "Export FBX", 520);
                CheckBox cm = Ui.Check("include the meshes (" + meshes.Count + ", " + Ui.Thousands(meshes.Sum(m => (long)m.VertexCount)) + " vertices, skinned)", true);
                d.Add(cm, Ui.Wrap("The file has the skeleton (" + anim.Skeleton.Count + " bones, rest = the skeleton's bind pose), the " + (seqKey != null ? "sequence" : "clip") +
                                  " as one key per frame on every bone it poses, Y up, 1 unit = 1 m.", Ui.Muted, 11));
                d.Button("Cancel");
                d.Button("Export", () => true, true);
                if (!d.Run()) return;
                withMesh = Ui.On(cm);
            }
            Tuple<string, byte[]> file;
            string key = clipKey, sk = seqKey;
            List<MeshPart> ms = withMesh ? new List<MeshPart>(meshes) : null;
            PlayerAnimation baked = anim;
            try { file = Busy("writing the FBX file", p => sk != null ? M.SeqFbx(sk, baked, ms) : M.ClipFbx(key, ms)); }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { Report(e); return; }
            Microsoft.Win32.SaveFileDialog sd = new Microsoft.Win32.SaveFileDialog { FileName = file.Item1, Filter = "FBX (*.fbx)|*.fbx|All files|*.*", Title = "Export the animation as FBX" };
            if (sd.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllBytes(sd.FileName, file.Item2);
                status.Foreground = Ui.Muted;
                status.Text = "exported " + sd.FileName;
                ed.Msg("exported " + sd.FileName);
            }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { Report(e); }
        }

        private void ImportFbx(bool asNew)
        {
            if (clipKey == null) return;
            string newKey = ed.ImportClipFile(clipKey, asNew, "fbx", this);
            if (newKey == null) return;
            LoadClip(newKey, !asNew);
        }
    }
}
