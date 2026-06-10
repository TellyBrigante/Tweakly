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
        //
        // ── LIGHT MODE v1.3.3 — refonte « doux pour les yeux » ──────────────────
        // Diagnostic des versions précédentes : pour adoucir, on avait ASSOMBRI les
        // fonds (#C2C8D7…) → gris boueux où fond et cartes étaient quasi identiques
        // (zéro hiérarchie, l'œil force) avec du texte presque noir par-dessus
        // (contraste dur). Doublement fatigant.
        // Recette appliquée (GitHub Light / Notion / Linear) :
        //   1. surfaces CLAIRES mais teintées bleu pâle — JAMAIS de blanc pur ;
        //   2. cartes PLUS CLAIRES que le fond → hiérarchie par la lumière + bordures
        //      fines, pas par des gris concurrents ;
        //   3. texte ARDOISE (#27314A) — JAMAIS de noir pur : contraste ~12:1 au lieu
        //      de 18:1, largement lisible mais sans éblouir.
        // v1.3.3-bis : le light « bleu pâle » restait trop lumineux pour l'utilisateur →
        // passage en MODE PAPIER (sépia doux, type liseuse/mode lecture) : à luminance
        // égale, un fond CHAUD est perçu bien plus doux qu'un fond froid. Luminance
        // abaissée (~89 %), encre chaude, hiérarchie conservée (cartes > fond + bordures).
        private static readonly Dictionary<string, (string dark, string light)> Roles = new()
        {
            ["ThBg"]        = ("#2B3252", "#E9E5DB"),   // fond page — crème-gris chaud (papier)
            ["ThPanel"]     = ("#313858", "#F2EFE7"),   // cartes — plus claires que le fond (hiérarchie)
            ["ThSidebar"]   = ("#262C49", "#DFDACE"),
            ["ThSecBtn"]    = ("#2E3559", "#E6E1D6"),
            ["ThPill"]      = ("#2B3358", "#ECE8DE"),
            ["ThTrack"]     = ("#2A3358", "#D8D3C5"),
            ["ThLogBg"]     = ("#242A45", "#EFECE3"),
            ["ThLogHdr"]    = ("#20253E", "#E2DDD1"),
            ["ThHover"]     = ("#2E3658", "#E2DDD2"),
            ["ThSelection"] = ("#34408A", "#C8D6EC"),   // sélection bleu doux (accent froid sur papier chaud)
            ["ThBorder"]    = ("#3D456E", "#CFC9BA"),
            ["ThTextTitle"] = ("#E2E6FF", "#33312A"),   // encre chaude, PAS noir
            ["ThTextBody"]  = ("#DCE0F6", "#45433A"),
            ["ThTextLabel"] = ("#C6CDEC", "#57544A"),
            ["ThTextSub"]   = ("#B4BBE0", "#67645A"),
            ["ThTextNav"]   = ("#B6BEE4", "#54526A"),
            ["ThTextDim"]   = ("#9CA3CC", "#847F72"),
            ["ThLogText"]   = ("#5FD98C", "#1E7A3C"),  // vert journal : clair sur sombre, foncé sur clair
            // Couleurs de statut — vives en sombre, ASSOMBRIES en clair pour rester lisibles
            ["ThOk"]        = ("#2EC46A", "#1E9E55"),
            ["ThWarn"]      = ("#F5C24A", "#A87900"),   // amber foncé en clair (le jaune vif était illisible)
            ["ThCrit"]      = ("#E05555", "#C0392B"),
        };

        // Couleurs (pour les dégradés : carte de score, pilule des switches)
        private static readonly Dictionary<string, (string dark, string light)> ColorRoles = new()
        {
            ["ThCardA"]    = ("#353D66", "#EFEBE1"),   // carte score — haut (papier v1.3.3-bis)
            ["ThCardB"]    = ("#272D4C", "#E3DED2"),   // carte score — bas
            ["ThSwOffA"]   = ("#10132C", "#EAE6DB"),   // switch OFF fond haut
            ["ThSwOffB"]   = ("#080A1E", "#DDD8CB"),   // switch OFF fond bas
            ["ThSwOffBdA"] = ("#2C3462", "#C5BFAF"),   // switch OFF bordure haut
            ["ThSwOffBdB"] = ("#07091B", "#A9A294"),   // switch OFF bordure bas
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
