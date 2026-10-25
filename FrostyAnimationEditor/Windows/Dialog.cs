using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Frosty.Controls;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>a modal dialog: content above a row of buttons; a button's action returns false to keep the dialog open</summary>
    internal class Dialog : FrostyWindow
    {
        private readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        public readonly StackPanel Body = new StackPanel();
        private readonly TextBlock problem = Ui.Wrap("", Ui.Bad);
        public bool Accepted;

        public Dialog(Window owner, string title, double width = 600)
        {
            Title = title;
            if (owner != null && owner.IsVisible) Owner = owner;
            WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            Width = width;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80);
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            DockPanel root = new DockPanel { Margin = new Thickness(14, 10, 14, 12) };
            problem.Visibility = Visibility.Collapsed;
            StackPanel bottom = new StackPanel();
            bottom.Children.Add(problem);
            bottom.Children.Add(buttons);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            ScrollViewer sv = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(sv);
            Content = root;
            Background = Ui.Back;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }

        public Dialog Add(params object[] kids)
        {
            Ui.Add(Body, kids);
            return this;
        }

        public void Problem(string text)
        {
            problem.Text = text ?? "";
            problem.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>a button; action returns false to stay open (errors thrown by it are shown in the dialog)</summary>
        public Button Button(string text, Func<bool> action = null, bool isDefault = false)
        {
            Button b = Ui.B(text, null);
            b.MinWidth = 80;
            b.Margin = new Thickness(6, 0, 0, 0);
            b.IsDefault = isDefault;
            b.Click += (s, e) =>
            {
                Problem(null);
                bool close = true;
                try { if (action != null) close = action(); }
                catch (Exception ex) when (EditorWindow.IsUserError(ex)) { Problem(ex.Message); close = false; }
                if (close)
                {
                    Accepted = action != null;
                    Close();
                }
            };
            buttons.Children.Add(b);
            return b;
        }

        public bool Run()
        {
            ShowDialog();
            return Accepted;
        }

        // ------------------------------------------------------------------ simple ones
        public static void Info(Window owner, string title, string text)
        {
            FrostyMessageBox.Show(text, title);
        }

        public static bool Confirm(Window owner, string title, string text)
        {
            return FrostyMessageBox.Show(text, title, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        }

        /// <summary>one line of text; null when cancelled</summary>
        public static string Ask(Window owner, string title, string label, string value, string note = null)
        {
            Dialog d = new Dialog(owner, title, 520);
            TextBox t = Ui.Box(value);
            Ui.Form f = new Ui.Form();
            f.Row(label, t);
            d.Add(f.Grid);
            if (note != null) d.Add(Ui.Wrap(note, Ui.Muted, 11));
            string result = null;
            d.Button("Cancel");
            d.Button("OK", () => { result = t.Text; return true; }, true);
            d.Loaded += (s, e) => { t.Focus(); t.SelectAll(); };
            d.ShowDialog();
            return result;
        }

        /// <summary>pick one item of a list; null when cancelled</summary>
        public static T Pick<T>(Window owner, string title, string note, IList<T> items, Func<T, string> label) where T : class
        {
            Dialog d = new Dialog(owner, title, 640);
            if (note != null) d.Add(Ui.Wrap(note, Ui.Muted));
            ListBox lb = new ListBox { Height = 300, Margin = new Thickness(0, 6, 0, 0) };
            foreach (T it in items) lb.Items.Add(new ListBoxItem { Content = label(it), Tag = it });
            if (lb.Items.Count > 0) lb.SelectedIndex = 0;
            d.Add(lb);
            T result = null;
            d.Button("Cancel");
            Func<bool> ok = () =>
            {
                ListBoxItem it = lb.SelectedItem as ListBoxItem;
                if (it == null) { d.Problem("choose one"); return false; }
                result = (T)it.Tag;
                return true;
            };
            d.Button("OK", ok, true);
            lb.MouseDoubleClick += (s, e) => { if (ok()) { d.Accepted = true; d.Close(); } };
            d.ShowDialog();
            return result;
        }
    }

    /// <summary>
    /// search the file's objects by name or 0x key, optionally within one class (the web editor's picker)
    /// </summary>
    internal sealed class ObjectPicker
    {
        public readonly StackPanel Panel = new StackPanel();
        private readonly TextBox query = Ui.Box("", double.NaN, false, "part of a name or a 0x key; Enter to search");
        private readonly ComboBox cls;
        private readonly ListBox list = new ListBox { Height = 260, Margin = new Thickness(0, 4, 0, 0) };
        private readonly EditorModel m;
        public event Action<JObject> Picked;

        public ObjectPicker(EditorModel model, string typeFilter, bool allClasses = false)
        {
            m = model;
            List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>> { Ui.KV("", "all classes") };
            foreach (object o in m.Types(allClasses)) { JObject t = (JObject)o; items.Add(Ui.KV(t.Str("name"), t.Str("name"))); }
            cls = Ui.Combo(items, typeFilter ?? "", 260);
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button go = Ui.B("Search", Run);
            Grid.SetColumn(cls, 1);
            Grid.SetColumn(go, 2);
            row.Children.Add(query);
            row.Children.Add(cls);
            row.Children.Add(go);
            Panel.Children.Add(row);
            Panel.Children.Add(list);
            query.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Run(); } };
            cls.SelectionChanged += (s, e) => Run();
            list.SelectionChanged += (s, e) =>
            {
                ListBoxItem it = list.SelectedItem as ListBoxItem;
                if (it != null && it.Tag is JObject && Picked != null) Picked((JObject)it.Tag);
            };
            if (!string.IsNullOrEmpty(typeFilter)) Run();
        }

        public void Run()
        {
            string q = query.Text.Trim();
            string t = Ui.Value(cls);
            list.Items.Clear();
            if (q.Length == 0 && string.IsNullOrEmpty(t)) { list.Items.Add(new ListBoxItem { Content = Ui.T("type part of a name, or choose a class", Ui.Muted), IsEnabled = false }); return; }
            bool byKey = q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && q.Length > 4;
            JObject r = m.Objects(byKey ? null : t, q, 0, 300);
            foreach (object o in r.Arr("items"))
            {
                JObject it = (JObject)o;
                string name = Ui.Label(it);
                StackPanel p = Ui.H(Ui.T(string.IsNullOrEmpty(name) ? "(no name)" : name, string.IsNullOrEmpty(name) ? Ui.Muted : null),
                                    Ui.Pad(Ui.T((it.Str("alias") != null ? it.Str("name") + "  " : "") + it.Str("type") + " " + it.Str("key"), Ui.Muted, 11, false, true), 8, 0, 0, 0));
                list.Items.Add(new ListBoxItem { Content = p, Tag = it });
            }
            if (list.Items.Count == 0) list.Items.Add(new ListBoxItem { Content = Ui.T("nothing found", Ui.Muted), IsEnabled = false });
        }
    }
}
