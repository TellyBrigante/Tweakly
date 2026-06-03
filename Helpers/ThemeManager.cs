using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Gère le thème clair / sombre. Les ~17 couleurs structurelles sont exposées
    /// comme brushes de ressource (clés "Th*") référencées via DynamicResource dans
    /// le XAML. Basculer le thème remplace ces ressources → toute l'UI se met à jour.
    /// Les couleurs d'accent (bleu/vert/rouge) restent identiques dans les deux modes.
    /// </summary>
    public static class ThemeManager
    {
        public enum Mode { Dark, Light }
        public static Mode Current { get; private set; } = Mode.Dark;

        // rôle → (sombre, clair)
        private static readonly Dictionary<string, (string dark, string light)> Roles = new()
        {
            ["ThBg"]        = ("#2B3252", "#C2C8D7"),   // fond page — gris clair mat (assombri ++)
            ["ThPanel"]     = ("#313858", "#CBD1E0"),   // cartes — gris (assombri ++)
            ["ThSidebar"]   = ("#262C49", "#C4CAD9"),
            ["ThSecBtn"]    = ("#2E3559", "#C7CEDD"),
            ["ThPill"]      = ("#2B3358", "#CCD2E0"),
            ["ThTrack"]     = ("#2A3358", "#B6BFD2"),
            ["ThLogBg"]     = ("#242A45", "#CCD2E0"),
            ["ThLogHdr"]    = ("#20253E", "#C4CAD9"),
            ["ThHover"]     = ("#2E3658", "#C6CEDF"),
            ["ThSelection"] = ("#34408A", "#BCCEEF"),
            ["ThBorder"]    = ("#3D456E", "#AFB8CF"),
            ["ThTextTitle"] = ("#E2E6FF", "#1B2238"),
            ["ThTextBody"]  = ("#DCE0F6", "#232B45"),
            ["ThTextLabel"] = ("#C6CDEC", "#394163"),
            ["ThTextSub"]   = ("#B4BBE0", "#4A5273"),
            ["ThTextNav"]   = ("#B6BEE4", "#44507A"),
            ["ThTextDim"]   = ("#9CA3CC", "#6A7299"),
            ["ThLogText"]   = ("#5FD98C", "#1E7A3C"),  // vert journal : clair sur sombre, foncé sur clair
            // Couleurs de statut — vives en sombre, ASSOMBRIES en clair pour rester lisibles
            ["ThOk"]        = ("#2EC46A", "#1E9E55"),
            ["ThWarn"]      = ("#F5C24A", "#A87900"),   // amber foncé en clair (le jaune vif était illisible)
            ["ThCrit"]      = ("#E05555", "#C0392B"),
        };

        // Couleurs (pour les dégradés : carte de score, pilule des switches)
        private static readonly Dictionary<string, (string dark, string light)> ColorRoles = new()
        {
            ["ThCardA"]    = ("#353D66", "#CDD3E2"),   // carte score — haut (assombri ++)
            ["ThCardB"]    = ("#272D4C", "#BFC7D7"),   // carte score — bas
            ["ThSwOffA"]   = ("#10132C", "#CDD4E4"),   // switch OFF fond haut
            ["ThSwOffB"]   = ("#080A1E", "#BFC7DB"),   // switch OFF fond bas
            ["ThSwOffBdA"] = ("#2C3462", "#AAB3CC"),   // switch OFF bordure haut
            ["ThSwOffBdB"] = ("#07091B", "#95A0BE"),   // switch OFF bordure bas
        };

        /// <summary>Couleur courante d'un rôle (pour le code-behind).</summary>
        public static Color C(string role)
        {
            if (Roles.TryGetValue(role, out var pair))
                return (Color)ColorConverter.ConvertFromString(Current == Mode.Dark ? pair.dark : pair.light);
            return Colors.Magenta;
        }

        /// <summary>Brush courant d'un rôle.</summary>
        public static SolidColorBrush Brush(string role) => new(C(role));

        /// <summary>Applique un thème : remplace toutes les ressources Th* de l'application.</summary>
        public static void Apply(Mode mode)
        {
            Current = mode;
            var res = Application.Current.Resources;
            foreach (var kv in Roles)
            {
                var hex = mode == Mode.Dark ? kv.Value.dark : kv.Value.light;
                res[kv.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            foreach (var kv in ColorRoles)
            {
                var hex = mode == Mode.Dark ? kv.Value.dark : kv.Value.light;
                res[kv.Key] = (Color)ColorConverter.ConvertFromString(hex);
            }
        }

        public static void Toggle() => Apply(Current == Mode.Dark ? Mode.Light : Mode.Dark);
    }
}
