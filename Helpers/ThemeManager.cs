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
    /// Les couleurs d'accent ont aussi une variante claire quand la version sombre devient illisible.
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
            // CLAIR = palette RÉGLÉE AU CURSEUR par l'utilisateur (2026-06-15) : fond ~70 %,
            // tuiles CREUSÉES sous le fond, sidebar/track/bordure plus profonds, texte tranché.
            // Les 8 valeurs (fond/tuile/sidebar/bordure/track/titre/corps/dim) sont les SIENNES ;
            // secbtn/pill/loghdr/hover/sélection/label/sub/nav sont dérivés dans le même esprit.
            ["ThBg"]        = ("#2B3252", "#ABB1BB"),   // fond page (réglage user)
            ["ThPanel"]     = ("#313858", "#A0A6B0"),   // tuiles creusées sous le fond (réglage user)
            ["ThSidebar"]   = ("#262C49", "#9DA3AD"),   // rail (réglage user)
            ["ThSecBtn"]    = ("#2E3559", "#A6ACB6"),   // dérivé : entre tuile et fond
            ["ThPill"]      = ("#2B3358", "#A3A9B3"),   // dérivé
            ["ThTrack"]     = ("#252C4A", "#9399A3"),   // creux des barres (réglage user)
            ["ThLogBg"]     = ("#242A45", "#A0A6B0"),   // journal = niveau tuile
            ["ThLogHdr"]    = ("#20253E", "#989EA8"),   // dérivé : sous la sidebar
            ["ThHover"]     = ("#2E3658", "#A2AAB9"),   // dérivé : hover un peu plus bleu
            ["ThSelection"] = ("#34408A", "#A4AFC6"),   // dérivé : sélection périwinkle
            ["ThBorder"]    = ("#3D456E", "#797F89"),   // bordures nettes (réglage user)
            ["ThTextTitle"] = ("#E2E6FF", "#191C21"),   // titre (réglage user)
            ["ThTextBody"]  = ("#DCE0F6", "#2F3237"),   // corps (réglage user)
            ["ThTextLabel"] = ("#C6CDEC", "#3D4046"),   // dérivé entre corps et dim
            ["ThTextSub"]   = ("#B4BBE0", "#45484E"),   // dérivé
            ["ThTextNav"]   = ("#B6BEE4", "#383B41"),   // dérivé (nav sur sidebar)
            ["ThTextDim"]   = ("#9CA3CC", "#4A4C50"),   // dim (réglage user)
            ["ThLogText"]   = ("#5FD98C", "#0D6334"),  // vert journal : clair sur sombre, foncé sur clair
            ["ThAccentIcon"]= ("#8FC0FF", "#2A62C4"),  // glyphes/initiales sur fond bleu alpha : clairs en sombre, PROFONDS en clair (sinon illisibles — retour utilisateur v1.3.5)
            ["ThChartLine"] = ("#3F88C5", "#1F4E94"),  // courbe FPS du Suivi en jeu : bleu MAT en sombre, sensiblement plus foncé/sobre que le #8FC0FF historique (qui pétait) ; bleu profond en clair

            // ── « Tints » : fonds COLORÉS thémés (alpha) pour cartes/pastilles. Avant : hex en
            // dur #33F5C24A (jaune vif) → en LIGHT, le jaune vif traversait l'alpha et arrachait
            // les yeux. Désormais chaque tint a sa version sombre + clair, calibrée pour rester
            // DISCRÈTE dans les deux modes.
            ["ThWarnTint"]    = ("#33F5C24A", "#1AA87900"),   // ambre / brun profond très léger en clair
            ["ThCritTint"]    = ("#33E05555", "#1AC0392B"),   // rouge / rouge profond très léger en clair
            ["ThInfoTint"]    = ("#22315FA0", "#1A1F4E94"),   // bleu très léger
            ["ThInfoBorderTint"] = ("#2E5BA0FF", "#332A62C4"),
            ["ThInfoStrongTint"] = ("#505BA0FF", "#442A62C4"),
            ["ThNeutralTint"] = ("#22A0A0A0", "#1A4A4D52"),   // gris très léger
            ["ThNeutralBorderTint"] = ("#55B4BBE0", "#554A4D52"),
            ["ThFaintBorder"] = ("#1A808080", "#264A4D52"),
            ["ThScrollThumb"] = ("#4A5285", "#6D7480"),
            ["ThScrollThumbHover"] = ("#5E68A8", "#596270"),
            ["ThScrollThumbDragging"] = ("#6E78BE", "#465162"),
            // Couleurs de statut — vives en sombre, ASSOMBRIES en clair pour rester lisibles
            ["ThOk"]        = ("#2EC46A", "#0D6334"),
            ["ThOkTint"]    = ("#225FD98C", "#220D6334"),
            ["ThOkBorderTint"] = ("#555FD98C", "#550D6334"),
            ["ThOkButton"]  = ("#1E7A3C", "#0D6334"),
            ["ThOkButtonHover"] = ("#28A050", "#0D6334"),
            ["ThOkButtonPressed"] = ("#16602E", "#0D6334"),
            ["ThWarn"]      = ("#F5C24A", "#A87900"),   // amber foncé en clair (le jaune vif était illisible)
            ["ThWarnBorderTint"] = ("#55F5C24A", "#55A87900"),
            ["ThWarnButton"] = ("#B5781E", "#8E5E16"),
            ["ThWarnButtonHover"] = ("#D8941E", "#A87900"),
            ["ThWarnButtonPressed"] = ("#8E5E16", "#704A10"),
            ["ThCrit"]      = ("#E05555", "#C0392B"),
            ["ThCritBorderTint"] = ("#55E05555", "#55C0392B"),
            ["ThCritButton"] = ("#7A1E1E", "#8F2B25"),
            ["ThCritButtonHover"] = ("#9E2828", "#A9362E"),
            ["ThCritButtonPressed"] = ("#5E1818", "#70211D"),
            ["ThViolet"]    = ("#C08CF0", "#7A3FB8"),   // RAM / accent violet — assombri en clair (le mauve clair était illisible sur fond clair)
            ["ThCyan"]      = ("#29C7D6", "#0E7C8A"),   // débits réseau — assombri en clair
            ["ThOrange"]    = ("#F5A623", "#C26A12"),   // classement « ta mesure » — vif en sombre, brûlé profond en clair (l'orange vif était illisible/criard sur le bleu-ardoise clair)
            ["ThPink"]      = ("#F08CB8", "#B5417A"),   // accent mémoire (rose) — assombri en clair
            ["ThTabSel"]    = ("#254E8C", "#2A62C4"),   // pastille d'onglet sélectionné (segmented) : bleu assez foncé pour du texte blanc dans les DEUX thèmes
            ["ThLadderCpu"] = ("#5BA0FF", "#2A62C4"),   // classement « ton CPU » — SOMBRE = valeur validée (dark intact), clair = bleu profond
            ["ThSteel"]     = ("#4F6EA8", "#566C98"),   // classement « voisins » bleu acier — dark validé, clair ajusté
            ["ThPrimary"]       = ("#1870CC", "#2A62C4"),
            ["ThPrimaryHover"]  = ("#2080E0", "#1F5FBF"),
            ["ThPrimaryPressed"]= ("#1260AA", "#174A96"),
            ["ThPrimaryText"]   = ("#E8F0FF", "#FFFFFF"),
            ["ThBlueLine"]      = ("#3B82E0", "#1F4E94"),
            ["ThBlueGradA"]     = ("#2F6FD8", "#1F4E94"),
            ["ThBlueGradB"]     = ("#5BA0FF", "#2A62C4"),
            ["ThBlueGradC"]     = ("#8AC0FF", "#3A73D9"),
            ["ThRamGradA"]      = ("#9A63D8", "#6E35A7"),
            ["ThRamGradB"]      = ("#C08CF0", "#7A3FB8"),
            ["ThRamGradC"]      = ("#DCC0F8", "#8F58C7"),
            ["ThNvme1"]         = ("#F5A623", "#C26A12"),
            ["ThNvme2"]         = ("#29C7D6", "#0E7C8A"),
            ["ThNvme3"]         = ("#FF6B9D", "#B5417A"),
            ["ThNvme4"]         = ("#E0C84A", "#8E7400"),
            ["ThCloseHover"]    = ("#C42B1C", "#A4261A"),
            ["ThOverlayStrong"] = ("#CC1A1F36", "#CC313845"),
            ["ThWhite"]         = ("#FFFFFF", "#FFFFFF"),
            ["ThBlack"]         = ("#000000", "#000000"),
        };

        // Couleurs (pour les dégradés : carte de score, pilule des switches)
        private static readonly Dictionary<string, (string dark, string light)> ColorRoles = new()
        {
            ["ThCardA"]    = ("#353D66", "#A6ACB6"),   // carte score — haut (dérivé palette user)
            ["ThCardB"]    = ("#272D4C", "#989EAA"),   // carte score — bas
            ["ThSwOffA"]   = ("#10132C", "#A6ACB6"),   // switch OFF fond haut
            ["ThSwOffB"]   = ("#080A1E", "#989EAA"),   // switch OFF fond bas
            ["ThSwOffBdA"] = ("#2C3462", "#868C96"),   // switch OFF bordure haut
            ["ThSwOffBdB"] = ("#07091B", "#6E7480"),   // switch OFF bordure bas
            ["ThPrimaryColor"]        = ("#1870CC", "#2A62C4"),
            ["ThPrimaryHoverColor"]   = ("#2080E0", "#1F5FBF"),
            ["ThPrimaryPressedColor"] = ("#1260AA", "#174A96"),
            ["ThBlueLineColor"]       = ("#3B82E0", "#1F4E94"),
            ["ThBlueGradAColor"]      = ("#2F6FD8", "#1F4E94"),
            ["ThBlueGradBColor"]      = ("#5BA0FF", "#2A62C4"),
            ["ThBlueGradCColor"]      = ("#8AC0FF", "#3A73D9"),
            ["ThRamGradAColor"]       = ("#9A63D8", "#6E35A7"),
            ["ThRamGradBColor"]       = ("#C08CF0", "#7A3FB8"),
            ["ThRamGradCColor"]       = ("#DCC0F8", "#8F58C7"),
            ["ThOverlayStrongColor"]  = ("#CC1A1F36", "#CC313845"),
            ["ThWhiteColor"]          = ("#FFFFFF", "#FFFFFF"),
            ["ThBlackColor"]          = ("#000000", "#000000"),
            ["ThBlackAlpha18Color"]   = ("#00000018", "#00000018"),
            ["ThInfoSelectionColor"]  = ("#1F5BA0FF", "#1F2A62C4"),
            ["ThInfoFaintColor"]      = ("#145BA0FF", "#142A62C4"),
            ["ThInfoTextTintColor"]   = ("#805BA0FF", "#802A62C4"),
            ["ThInfoGradientColor"]   = ("#3D5BA0FF", "#3D2A62C4"),
            ["ThSheenClearColor"]     = ("#00FFFFFF", "#00FFFFFF"),
            ["ThSheenMidColor"]       = ("#66FFFFFF", "#4DFFFFFF"),
            ["ThGlassHighlightStrongColor"] = ("#26FFFFFF", "#26FFFFFF"),
            ["ThGlassHighlightFaintColor"] = ("#08FFFFFF", "#08FFFFFF"),
            ["ThGlassShadowFaintColor"] = ("#0A000000", "#0A000000"),
            ["ThBoardVoidColor"] = ("#0E1526", "#626B78"),
            ["ThBoardOutlineColor"] = ("#46557F", "#56647A"),
            ["ThBoardContactColor"] = ("#384670", "#657187"),
            ["ThBoardModuleColor"] = ("#243052", "#7E8794"),
            ["ThBoardAccentStrokeColor"] = ("#56689E", "#415A82"),
            ["ThBoardTraceColor"] = ("#27335C", "#687386"),
            ["ThBoardScrewFillColor"] = ("#3A4870", "#737D8B"),
            ["ThBoardInnerColor"] = ("#33406A", "#69758A"),
            ["ThBoardSlotColor"] = ("#2C3962", "#77818F"),
            ["ThBoardPortColor"] = ("#1C2747", "#737D8C"),
            ["ThBoardStrongOutlineColor"] = ("#4C5C94", "#4E5F7B"),
            ["ThBoardLabelColor"] = ("#8193CC", "#304A78"),
            ["ThBoardSocketLineColor"] = ("#5468A8", "#3F5A8C"),
            ["ThBoardCpuEdgeColor"] = ("#3C4B7A", "#65718A"),
            ["ThBoardModuleHighlightColor"] = ("#243056", "#87909C"),
            ["ThBoardLabelDimColor"] = ("#54659C", "#465A7E"),
            ["ThBoardChipTopColor"] = ("#2F3C66", "#7F8997"),
            ["ThBoardCpuFrameColor"] = ("#4A5880", "#526178"),
            ["ThBoardCpuAreaColor"] = ("#1B2440", "#747D89"),
            ["ThBoardBaseBottomColor"] = ("#131D38", "#828A96"),
            ["ThBoardBaseTopColor"] = ("#1A2542", "#8E95A0"),
            ["ThBoardCpuCoreColor"] = ("#2B3860", "#727C8A"),
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
                var color = (Color)ColorConverter.ConvertFromString(hex);
                if (res[kv.Key] is SolidColorBrush existing && !existing.IsFrozen)
                    existing.Color = color;
                else
                    res[kv.Key] = new SolidColorBrush(color);
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
