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
        // HISTORIQUE DES TENTATIVES LIGHT (utilisateur TRÈS photosensible) :
        //   v1 bleu pâle clair   → trop lumineux ;
        //   v2 papier sépia      → « jaunit bêtement », les teintes chaudes fatiguent AUSSI ;
        //   v3 gris neutre ~82 % → encore trop de lumière émise.
        // v4 (v1.3.5) : changement de PHILOSOPHIE — ce n'est plus un « light mode »,
        // c'est un MODE GRIS ÉTEINT (type « Dim » de Twitter/Discord) : luminance ~75 %
        // (papier à l'ombre), teinte STRICTEMENT neutre avec micro-biais FROID (aucune
        // dérive jaune possible). Leçon anti-« gris boueux » toujours appliquée :
        // hiérarchie par la lumière (cartes > fond), bordures nettes, encre ardoise.
        // ⚠️ Si v4 échoue : descendre encore (~68 %), JAMAIS éclaircir ni réchauffer.
        private static readonly Dictionary<string, (string dark, string light)> Roles = new()
        {
            ["ThBg"]        = ("#2B3252", "#C2C4C9"),   // fond page — gris éteint, micro-biais froid
            ["ThPanel"]     = ("#313858", "#CFD1D6"),   // cartes — plus claires que le fond (hiérarchie)
            ["ThSidebar"]   = ("#262C49", "#B3B5BB"),
            ["ThSecBtn"]    = ("#2E3559", "#C7C9CE"),
            ["ThPill"]      = ("#2B3358", "#CACCD1"),
            ["ThTrack"]     = ("#252C4A", "#ADAFB5"),   // creux des barres (dosage validé en sombre v1.3.5)
            ["ThLogBg"]     = ("#242A45", "#CBCDD2"),
            ["ThLogHdr"]    = ("#20253E", "#BABCC2"),
            ["ThHover"]     = ("#2E3658", "#AFBCD2"),   // hover bleuté = se voit
            ["ThSelection"] = ("#34408A", "#9FB5D8"),   // sélection bleu doux
            ["ThBorder"]    = ("#3D456E", "#999BA2"),
            ["ThTextTitle"] = ("#E2E6FF", "#26282E"),   // encre ardoise, PAS noir
            ["ThTextBody"]  = ("#DCE0F6", "#34373F"),
            ["ThTextLabel"] = ("#C6CDEC", "#464952"),
            ["ThTextSub"]   = ("#B4BBE0", "#54575F"),
            ["ThTextNav"]   = ("#B6BEE4", "#3F424D"),
            ["ThTextDim"]   = ("#9CA3CC", "#6B6E77"),
            ["ThLogText"]   = ("#5FD98C", "#1E6E38"),  // vert journal : clair sur sombre, foncé sur clair
            ["ThAccentIcon"]= ("#8FC0FF", "#2A62C4"),  // glyphes/initiales sur fond bleu alpha : clairs en sombre, PROFONDS en clair (sinon illisibles — retour utilisateur v1.3.5)
            // Couleurs de statut — vives en sombre, ASSOMBRIES en clair pour rester lisibles
            ["ThOk"]        = ("#2EC46A", "#1E9E55"),
            ["ThWarn"]      = ("#F5C24A", "#A87900"),   // amber foncé en clair (le jaune vif était illisible)
            ["ThCrit"]      = ("#E05555", "#C0392B"),
        };

        // Couleurs (pour les dégradés : carte de score, pilule des switches)
        private static readonly Dictionary<string, (string dark, string light)> ColorRoles = new()
        {
            ["ThCardA"]    = ("#353D66", "#CDCFD4"),   // carte score — haut (gris éteint v4)
            ["ThCardB"]    = ("#272D4C", "#BDBFC5"),   // carte score — bas
            ["ThSwOffA"]   = ("#10132C", "#C5C7CC"),   // switch OFF fond haut
            ["ThSwOffB"]   = ("#080A1E", "#B7B9BF"),   // switch OFF fond bas
            ["ThSwOffBdA"] = ("#2C3462", "#9C9EA5"),   // switch OFF bordure haut
            ["ThSwOffBdB"] = ("#07091B", "#82848B"),   // switch OFF bordure bas
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
