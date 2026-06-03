using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Bandeau de retour visuel ANIMÉ partagé par les pages d'optimisation
    /// (CPU, Windows, Réseau, Confidentialité). Petit « toast » qui apparaît en fondu +
    /// glissement + pop, et disparaît tout seul en cas de succès. Couleur volontairement
    /// discrète : texte neutre, pastille d'accent ; le rouge/orange ne reste marqué que
    /// pour un avertissement ou une erreur (qui restent affichés).
    /// </summary>
    public static class TweakFeedback
    {
        private enum Level { Ok, Warn, Err, Info }

        private static int _seq;   // jeton anti-collision entre deux toasts rapprochés

        public static void Show(Border banner, Ellipse dot, TextBlock text,
                                IReadOnlyList<string> messages, string okText)
        {
            bool error = false, restart = false, action = false;
            foreach (var m in messages)
            {
                var s = m.ToLowerInvariant();
                if (s.Contains("erreur"))                                error   = true;
                if (s.Contains("redémarr") || s.Contains("redemarr"))    restart = true;
                if (s.Contains("fermez")   || s.Contains("introuvable")) action  = true;
            }

            if (error)        Run(banner, dot, text, Level.Err,  "Appliqué, mais des erreurs sont survenues — voir le journal d'activité.", emphasize: true,  autoHide: false);
            else if (action)  Run(banner, dot, text, Level.Warn, okText + " — une action est requise, voir le journal d'activité.",        emphasize: true,  autoHide: false);
            else if (restart) Run(banner, dot, text, Level.Warn, okText + " — redémarre le PC pour activer certains réglages.",            emphasize: true,  autoHide: false);
            else              Run(banner, dot, text, Level.Ok,   okText + " ✓",                                                       emphasize: false, autoHide: true);
        }

        public static void ShowSimple(Border banner, Ellipse dot, TextBlock text,
                                      bool ok, string okMsg, string errMsg)
        {
            if (ok) Run(banner, dot, text, Level.Ok,  okMsg + " ✓", emphasize: false, autoHide: true);
            else    Run(banner, dot, text, Level.Err, errMsg,            emphasize: true,  autoHide: false);
        }

        public static void ShowInfo(Border banner, Ellipse dot, TextBlock text, string msg)
            => Run(banner, dot, text, Level.Info, msg, emphasize: false, autoHide: true);

        /// <summary>Retourne l'état cible si la case a changé, sinon null (= ne rien faire).</summary>
        public static bool? Changed(CheckBox box, bool was)
        {
            bool cur = box.IsChecked == true;
            return cur != was ? cur : (bool?)null;
        }

        // ── Cœur : contenu + animation d'entrée (+ auto-disparition) ───────────
        private static void Run(Border banner, Ellipse dot, TextBlock text,
                                Level level, string msg, bool emphasize, bool autoHide)
        {
            // Couleur thémable (vive en sombre, assombrie/lisible en clair)
            var role = level switch { Level.Ok => "ThOk", Level.Warn => "ThWarn", Level.Err => "ThCrit", _ => "ThTextDim" };
            var accent = ThemeManager.C(role);
            dot.Fill        = new SolidColorBrush(accent);
            text.Text       = msg;
            text.Foreground = emphasize
                ? new SolidColorBrush(accent)
                : (Application.Current?.Resources["ThTextBody"] as Brush ?? Brushes.White);

            // Transformations pour le « pop » + glissement
            var scale = new ScaleTransform(0.94, 0.94);
            var slide = new TranslateTransform(0, 8);
            var grp   = new TransformGroup();
            grp.Children.Add(scale);
            grp.Children.Add(slide);
            banner.RenderTransform       = grp;
            banner.RenderTransformOrigin = new Point(0, 0.5);
            banner.Visibility            = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur  = TimeSpan.FromMilliseconds(260);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,  new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,  new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty,   new DoubleAnimation(8, 0, dur)    { EasingFunction = ease });

            int token = unchecked(++_seq);
            banner.Tag = token;

            if (autoHide)
            {
                var op = new DoubleAnimationUsingKeyFrames();
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
                op.Completed += (_, _) =>
                {
                    if (banner.Tag is int t && t == token)   // pas remplacé entre-temps
                        banner.Visibility = Visibility.Collapsed;
                };
                banner.BeginAnimation(UIElement.OpacityProperty, op);
            }
            else
            {
                banner.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            }

            // Son cohérent avec le type de notif (info = silencieux)
            if (level == Level.Ok)                              UiSound.Success();
            else if (level == Level.Warn || level == Level.Err) UiSound.Warn();
        }
    }
}
