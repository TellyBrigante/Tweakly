using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Optimisation_Tool.Helpers
{
    /// <summary>Petites animations réutilisables (transitions, compteurs, apparitions).</summary>
    public static class Anim
    {
        /// <summary>Compte de 0 jusqu'à <paramref name="to"/> dans un TextBlock (easeOut).</summary>
        public static void CountUp(TextBlock tb, int to, int ms = 600, string suffix = "")
        {
            int from = 0;
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / ms);
                double e = 1 - Math.Pow(1 - t, 3);
                tb.Text = (int)Math.Round(from + (to - from) * e) + suffix;
                if (t >= 1.0) { tb.Text = to + suffix; timer.Stop(); }
            };
            tb.Text = "0" + suffix;
            timer.Start();
        }

        /// <summary>Fondu + glissement vertical à l'apparition (stagger de listes/cartes).</summary>
        public static void FadeSlideIn(UIElement el, double fromY = 12, int ms = 240, int delayMs = 0)
        {
            var tt = new TranslateTransform(0, fromY);
            el.RenderTransform = tt;
            var ease  = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(delayMs);

            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease });
        }

        /// <summary>Transition d'entrée d'une page : fondu + glissement horizontal.</summary>
        public static void PageIn(UIElement el, double fromX = 16, int ms = 200)
        {
            var tt = new TranslateTransform(fromX, 0);
            el.RenderTransform = tt;
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(ms + 40))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>Anime une largeur de colonne (GridLength en *) — pour faire glisser les jauges.</summary>
    public class GridLengthAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation));
        public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation));
        public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
        {
            double p = clock.CurrentProgress ?? 1.0;
            p = 1 - Math.Pow(1 - p, 3);   // easeOut
            return new GridLength(From + (To - From) * p, GridUnitType.Star);
        }
    }
}
