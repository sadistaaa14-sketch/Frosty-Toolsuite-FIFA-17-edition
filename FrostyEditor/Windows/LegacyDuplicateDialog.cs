using System.Windows;
using System.Windows.Controls;

namespace FrostyEditor.Windows
{
    public class LegacyDuplicateDialog : Window
    {
        private TextBox textBox;
        public string Result => textBox.Text;

        public LegacyDuplicateDialog(string initialValue)
        {
            Title = "Duplicate Legacy Asset";
            Width = 500;
            Height = 120;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            StackPanel panel = new StackPanel { Margin = new Thickness(10) };

            textBox = new TextBox
            {
                Text = initialValue,
                Margin = new Thickness(0, 0, 0, 8)
            };
            textBox.SelectAll();

            Button okButton = new Button
            {
                Content = "OK",
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
            okButton.Click += (s, e) => { DialogResult = true; };

            panel.Children.Add(new TextBlock { Text = "Enter new asset path:", Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(textBox);
            panel.Children.Add(okButton);

            Content = panel;
        }
    }
}
