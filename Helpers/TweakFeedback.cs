using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Boucle « mesurer → corriger → prouver » (v1.3.5) : passe à true dès qu'un
        /// « Appliquer » d'une page d'optim réussit → la page Tweakly Score propose de
        /// relancer le bench pour CHIFFRER le gain. Remis à false après un bench complet.
        /// </summary>
        public static bool TweaksAppliedSinceBench { get; set; }

        public static void Show(Border banner, Ellipse dot, TextBlock text,
                                IReadOnlyList<string> messages, string okText)
        {
            bool error = messages.Any(FeedbackMessageClassifier.IsFailure);
            bool restart = false, action = false;
            foreach (var m in messages)
            {
                var s = m.ToLowerInvariant();
                if (s.Contains("redémarr") || s.Contains("redemarr"))    restart = true;
                if (s.Contains("ferme")    || s.Contains("introuvable")) action  = true;
            }

            if (messages.Any(message =>
                    !FeedbackMessageClassifier.IsFailure(message) && !IsActionMessage(message)))
                TweaksAppliedSinceBench = true;
            if (error)
            {
                var failures = messages
                    .Where(FeedbackMessageClassifier.IsFailure)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string detail = failures.Count == 0
                    ? "Un réglage n'a pas été appliqué."
                    : string.Join("  •  ", failures.Take(2));
                if (failures.Count > 2) detail += $"  •  +{failures.Count - 2} autre(s) échec(s)";
                Run(banner, dot, text, Level.Err, detail, emphasize: true, autoHide: false);
            }
            else if (action)
            {
                string detail = messages.FirstOrDefault(IsActionMessage) ?? "Une action est requise.";
                Run(banner, dot, text, Level.Warn, detail, emphasize: true, autoHide: false);
            }
            else if (restart) Run(banner, dot, text, Level.Warn, okText + " — redémarre le PC pour activer certains réglages.",            emphasize: true,  autoHide: false);
            else              Run(banner, dot, text, Level.Ok,   okText + " ✓",                                                       emphasize: false, autoHide: true);
        }

        private static bool IsActionMessage(string message)
        {
            string value = message.ToLowerInvariant();
            return value.Contains("ferme") || value.Contains("introuvable");
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

        /// <summary>
        /// Applique une lecture a un switch. Une lecture en echec desactive le switch :
        /// la valeur de repli ne doit jamais devenir un faux etat modifiable.
        /// </summary>
        public static void ApplyDetectedState(
            CheckBox box,
            ProbeResult<bool> result,
            Action<string> log,
            string label)
        {
            box.IsChecked = result.Value;
            box.IsEnabled = result.Success;
            if (!result.Success)
                log($"{label} indisponible : {result.Error}");
        }

        /// <summary>
        /// Compare l'etat demande avec l'etat relu apres application. Une divergence
        /// devient une vraie erreur visible au lieu de laisser le switch sur un faux succes.
        /// </summary>
        public static void VerifyApplied(ICollection<string> messages, Action<string> log,
                                         string label, bool? requested, bool actual)
        {
            if (!requested.HasValue || requested.Value == actual) return;

            string message = $"{label} : erreur - le réglage n'a pas été appliqué. La case a été remise sur l'état détecté.";
            messages.Add(message);
            log(message);
        }

        public static void VerifyApplied(
            ICollection<string> messages,
            Action<string> log,
            string label,
            bool? requested,
            ProbeResult<bool> actual)
        {
            if (!requested.HasValue) return;
            if (!actual.Success)
            {
                string message = $"{label} : erreur - verification impossible apres application ({actual.Error}).";
                messages.Add(message);
                log(message);
                return;
            }

            VerifyApplied(messages, log, label, requested, actual.Value);
        }

        // ── Cœur : contenu + animation d'entrée (+ auto-disparition) ───────────
        private static void Run(Border banner, Ellipse dot, TextBlock text,
                                Level level, string msg, bool emphasize, bool autoHide)
        {
            // Couleur thémable (vive en sombre, assombrie/lisible en clair)
            var role = level switch { Level.Ok => "ThOk", Level.Warn => "ThWarn", Level.Err => "ThCrit", _ => "ThTextDim" };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, role);
            text.Text       = msg;
            text.SetResourceReference(TextBlock.ForegroundProperty, emphasize ? role : "ThTextBody");

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
