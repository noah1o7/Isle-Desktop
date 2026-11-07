using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Isle
{
    /// <summary>Kleine hüpfende Balken, solange Musik läuft.</summary>
    public sealed class EqualizerBars : StackPanel
    {
        private static readonly Random Rng = new();
        private readonly ScaleTransform[] _scales;
        private readonly Rectangle[] _bars;
        private bool _playing;

        public EqualizerBars(double height = 14, int count = 4)
        {
            Orientation = Orientation.Horizontal;
            Height = height;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;

            _scales = new ScaleTransform[count];
            _bars = new Rectangle[count];
            for (int i = 0; i < count; i++)
            {
                var st = new ScaleTransform(1, 0.25);
                var bar = new Rectangle
                {
                    Width = 3,
                    Height = height,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = st,
                    Fill = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                _scales[i] = st;
                _bars[i] = bar;
                Children.Add(bar);
            }
        }

        public void SetBrush(Brush brush)
        {
            foreach (var b in _bars) b.Fill = brush;
        }

        public void SetPlaying(bool playing)
        {
            if (playing == _playing) return;
            _playing = playing;

            for (int i = 0; i < _scales.Length; i++)
            {
                if (playing)
                {
                    var anim = new DoubleAnimation
                    {
                        From = 0.2 + Rng.NextDouble() * 0.15,
                        To = 0.65 + Rng.NextDouble() * 0.35,
                        Duration = TimeSpan.FromMilliseconds(280 + Rng.Next(320)),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    Timeline.SetDesiredFrameRate(anim, 30); // 30 Bilder/s reichen fürs Auge, halbe Last
                    _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
                else
                {
                    var settle = new DoubleAnimation(0.22, TimeSpan.FromMilliseconds(250))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    };
                    _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, settle);
                }
            }
        }
    }
}
