using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Isle
{
    /// <summary>Kleiner dunkler Dialog (Bestätigen oder Text eingeben) – statt der weißen Windows-MessageBox.</summary>
    internal sealed class DarkDialog : Window
    {
        private readonly TextBox? _input;
        private bool _ok;

        private DarkDialog(string title, string message, string okText, string? initialInput)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"];

            var primary = (Brush)Application.Current.Resources["TextPrimary"];
            var secondary = (Brush)Application.Current.Resources["TextSecondary"];

            var stack = new StackPanel { Width = 320 };
            stack.Children.Add(new TextBlock { Text = title, Foreground = primary, FontSize = 15, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock
            {
                Text = message, Foreground = secondary, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });

            if (initialInput != null)
            {
                _input = new TextBox
                {
                    Text = initialInput,
                    Margin = new Thickness(0, 14, 0, 0),
                    Padding = new Thickness(8, 6, 8, 6),
                    FontSize = 13,
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                    Foreground = primary,
                    CaretBrush = primary,
                    BorderBrush = (Brush)Application.Current.Resources["PanelBorder"],
                    BorderThickness = new Thickness(1),
                };
                stack.Children.Add(_input);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = "Abbrechen", Style = (Style)Application.Current.Resources["FlatButton"], Height = 30, Padding = new Thickness(14, 0, 14, 0) };
            var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources["PrimaryButton"], Height = 30, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            cancel.Click += (_, _) => Close();
            ok.Click += (_, _) => { _ok = true; Close(); };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);

            Content = new Border
            {
                Margin = new Thickness(20),
                Padding = new Thickness(20),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
                BorderBrush = (Brush)Application.Current.Resources["PanelBorder"],
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.6, Color = Colors.Black },
                Child = stack,
            };

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (_, _) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Win32.ForceForeground(h);
                if (_input != null) { _input.Focus(); _input.SelectAll(); }
                else ok.Focus();
            };
        }

        public static bool Confirm(string title, string message, string okText)
        {
            var d = new DarkDialog(title, message, okText, null);
            d.ShowDialog();
            return d._ok;
        }

        public static string? Prompt(string title, string message, string initial)
        {
            var d = new DarkDialog(title, message, "OK", initial);
            d.ShowDialog();
            return d._ok ? d._input!.Text.Trim() : null;
        }
    }
}
