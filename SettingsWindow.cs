using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Isle
{
    /// <summary>Kleines dunkles Einstellungsfenster. Änderungen wirken sofort (live).</summary>
    internal sealed class SettingsWindow : Window
    {
        private static SettingsWindow? _open;

        public static void ShowOrFocus()
        {
            if (_open != null)
            {
                Win32.ForceForeground(new WindowInteropHelper(_open).Handle);
                _open.Activate();
                return;
            }
            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }

        private SettingsWindow()
        {
            Title = "Isle-Einstellungen";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var primary = (Brush)Application.Current.Resources["TextPrimary"];
            var secondary = (Brush)Application.Current.Resources["TextSecondary"];

            var root = new StackPanel { Width = 360 };

            // Titelzeile (zum Verschieben anfassen)
            var head = new Grid { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            head.Children.Add(new TextBlock { Text = "Einstellungen", Foreground = primary, FontSize = 16, FontWeight = FontWeights.SemiBold });
            var close = new Button
            {
                Style = (Style)Application.Current.Resources["IconButton"], Width = 26, Height = 26,
                HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L8,8 M0,8 L8,0"), Stroke = secondary, StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                },
                ToolTip = "Schließen",
            };
            close.Click += (_, _) => Close();
            head.Children.Add(close);
            head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            root.Children.Add(head);

            // ---- Top-Bar: Schriftstärke ----
            root.Children.Add(new TextBlock { Text = "TOP-BAR", Foreground = secondary, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

            var row = new Grid();
            row.Children.Add(new TextBlock { Text = "Schriftstärke", Foreground = primary, FontSize = 13 });
            var valueText = new TextBlock { Foreground = secondary, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(valueText);
            root.Children.Add(row);

            var slider = new Slider
            {
                Style = (Style)Application.Current.Resources["DarkSlider"],
                Minimum = 0, Maximum = TopBar.Weights.Length - 1, TickFrequency = 1, SmallChange = 1, LargeChange = 1,
                IsSnapToTickEnabled = true, Value = Math.Clamp(Settings.Current.BarFontWeight, 0, TopBar.Weights.Length - 1),
                Margin = new Thickness(0, 8, 0, 2),
            };
            root.Children.Add(slider);

            var scale = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            var segoe = new FontFamily("Segoe UI");
            scale.Children.Add(new TextBlock { Text = "Dünn", Foreground = secondary, FontSize = 11, FontFamily = segoe, FontWeight = TopBar.Weights[0].Weight });
            scale.Children.Add(new TextBlock { Text = "Fett", Foreground = secondary, FontSize = 11, FontFamily = segoe, FontWeight = TopBar.Weights[^1].Weight, HorizontalAlignment = HorizontalAlignment.Right });
            root.Children.Add(scale);

            // Vorschau auf hellem und dunklem Grund
            var preview = new UniformGrid2(
                PreviewChip("Spotify   Sa. 7. Nov.  06:30", Color.FromRgb(0xE9, 0xE6, 0xE1), Color.FromRgb(0x1C, 0x1C, 0x1E)),
                PreviewChip("Spotify   Sa. 7. Nov.  06:30", Color.FromRgb(0x2A, 0x2D, 0x3A), Color.FromRgb(0xF5, 0xF5, 0xF7)));
            preview.Margin = new Thickness(0, 6, 0, 4);
            root.Children.Add(preview);

            void Apply()
            {
                int i = (int)Math.Round(slider.Value);
                valueText.Text = TopBar.Weights[i].Name;
                foreach (var tb in preview.Texts) tb.FontWeight = TopBar.Weights[i].Weight;
                if (Settings.Current.BarFontWeight != i)
                {
                    Settings.Current.BarFontWeight = i;
                    Settings.Save();
                    ((App)Application.Current).TopBarWindow?.ApplyFontWeight(); // live an der Leiste
                }
            }
            slider.ValueChanged += (_, _) => Apply();
            Apply();

            // Fertig
            var done = new Button
            {
                Content = "Fertig", Style = (Style)Application.Current.Resources["PrimaryButton"], Height = 30,
                Padding = new Thickness(18, 0, 18, 0), HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0), IsDefault = true,
            };
            done.Click += (_, _) => Close();
            root.Children.Add(done);

            Content = new Border
            {
                Margin = new Thickness(20),
                Padding = new Thickness(20),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
                BorderBrush = (Brush)Application.Current.Resources["PanelBorder"],
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.6, Color = Colors.Black },
                Child = root,
            };

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (_, _) =>
            {
                Win32.ForceForeground(new WindowInteropHelper(this).Handle);
                slider.Focus();
            };
        }

        private static Border PreviewChip(string text, Color bg, Color fg) => new()
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(fg), FontSize = 13, FontFamily = new FontFamily("Segoe UI") },
        };

        /// <summary>Zwei Vorschau-Kärtchen untereinander, deren Texte live angepasst werden.</summary>
        private sealed class UniformGrid2 : StackPanel
        {
            public TextBlock[] Texts { get; }

            public UniformGrid2(params Border[] chips)
            {
                foreach (var c in chips) Children.Add(c);
                Texts = Array.ConvertAll(chips, c => (TextBlock)c.Child);
            }
        }
    }
}
