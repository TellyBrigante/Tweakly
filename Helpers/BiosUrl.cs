using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Construction de l'URL de la page BIOS du constructeur de la carte mere
    /// (MSI / Gigabyte / ASUS / ASRock...). EXTRAIT de Pages/PageSpecs.xaml.cs
    /// en v1.3.3 (audit M-6) — logique pure sans UI, deplacee telle quelle.
    /// </summary>
    internal static class BiosUrl
    {
        public static string Build(string biosMfr, string biosModel)
        {
            var mfr   = (biosMfr ?? "").Trim();
            var model = NormalizeModel(biosModel);
            var mfrUp = mfr.ToUpperInvariant();

            // ── MSI / Micro-Star ─────────────────────────────────────────────
            // Page BIOS directe : msi.com/Motherboard/MAG-Z790-TOMAHAWK-WIFI/support#bios
            if (mfrUp.Contains("MSI") || mfrUp.Contains("MICRO-STAR"))
                return $"https://www.msi.com/Motherboard/{MakeSlug(StripMsiBoardId(model))}/support#bios";

            // ── Gigabyte / AORUS ─────────────────────────────────────────────
            // Page BIOS directe : gigabyte.com/{pays}/Motherboard/Z790-AORUS-MASTER/support#bios
            if (mfrUp.Contains("GIGABYTE"))
            {
                var cc = GetCountryCode("GIGABYTE");
                var cp = cc.Length > 0 ? $"{cc}/" : "";
                return $"https://www.gigabyte.com/{cp}Motherboard/{MakeSlug(model)}/support#bios";
            }

            // ── ASUS / ASUSTeK ───────────────────────────────────────────────
            // La route supportonly évite les segments fragiles par gamme/pays :
            // asus.com/supportonly/TUF%20GAMING%20B550-PLUS/helpdesk_bios/
            if (mfrUp.Contains("ASUS") || mfrUp.Contains("ASUSTEK"))
            {
                var supportModel = NormalizeAsusSupportOnlyModel(model);
                return $"https://www.asus.com/supportonly/{Uri.EscapeDataString(supportModel)}/helpdesk_bios/";
            }

            // ── ASRock ───────────────────────────────────────────────────────
            // URL directe : asrock.com/mb/Intel/Z790%20Taichi/index.asp#BIOS
            // La plateforme (Intel/AMD) se déduit du chipset présent dans le modèle
            if (mfrUp.Contains("ASROCK"))
            {
                var platform = HardwareInfo.DetectMbPlatform(model);
                return $"https://www.asrock.com/mb/{platform}/{Uri.EscapeDataString(model)}/index.asp#BIOS";
            }

            // ── Biostar ──────────────────────────────────────────────────────
            if (mfrUp.Contains("BIOSTAR"))
                return $"https://www.biostar.com.tw/app/en/mb/introduction.php?S_ID={Uri.EscapeDataString(model)}";

            // ── Supermicro ───────────────────────────────────────────────────
            if (mfrUp.Contains("SUPERMICRO"))
                return $"https://www.supermicro.com/en/support/resources/downloadcenter/firmware?q={Uri.EscapeDataString(model)}";

            // ── Fallback universel ───────────────────────────────────────────
            return "https://www.google.com/search?q="
                 + Uri.EscapeDataString($"{mfr} {model} BIOS update download");
        }

        private static string NormalizeModel(string? model)
        {
            var s = (model ?? "").Trim();
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim();
        }

        private static string StripMsiBoardId(string model)
        {
            // WMI peut exposer "MAG Z790 TOMAHAWK WIFI (MS-7D91)" alors que MSI attend
            // uniquement le nom commercial dans l'URL produit.
            return Regex.Replace(model, @"\s*\(MS-[^)]+\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        }

        private static string NormalizeAsusSupportOnlyModel(string model)
        {
            var s = NormalizeModel(model);
            s = Regex.Replace(s, @"\s*\(rev(?:ision)?\.?\s*[^)]*\)\s*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+rev(?:ision)?\.?\s*[\w.]+$", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        /// <summary>
        /// Retourne le segment pays à insérer dans l'URL du fabricant,
        /// basé sur les paramètres régionaux Windows de l'utilisateur (RegionInfo).
        /// Retourne "" si le pays n'est pas reconnu (→ URL globale sans pays).
        /// </summary>
        private static string GetCountryCode(string brand)
        {
            string iso;
            try
            {
                iso = System.Globalization.RegionInfo.CurrentRegion
                            .TwoLetterISORegionName.ToUpperInvariant();
            }
            catch { return ""; }

            var bUp = brand.ToUpperInvariant();

            // ── ASUS ─────────────────────────────────────────────────────────
            // asus.com/{pays}/... — codes spéciaux : GB→uk, CA→ca-en, Golfe→me
            if (bUp.Contains("ASUS"))
            {
                // Pays avec segment régional sur asus.com (whitelist)
                var asusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Europe
                    {"FR","fr"}, {"DE","de"}, {"IT","it"}, {"ES","es"},
                    {"GB","uk"}, {"NL","nl"}, {"BE","be"}, {"PL","pl"},
                    {"PT","pt"}, {"SE","se"}, {"NO","no"}, {"DK","dk"},
                    {"FI","fi"}, {"AT","at"}, {"CH","ch"}, {"CZ","cz"},
                    {"SK","sk"}, {"HU","hu"}, {"RO","ro"}, {"BG","bg"},
                    {"GR","gr"}, {"HR","hr"}, {"RS","rs"}, {"SI","si"},
                    {"TR","tr"}, {"UA","ua"}, {"RU","ru"}, {"IL","il"},
                    // Amériques
                    {"US","us"}, {"CA","ca-en"}, {"MX","mx"}, {"BR","br"},
                    // Asie-Pacifique
                    {"AU","au"}, {"NZ","nz"}, {"JP","jp"}, {"KR","kr"},
                    {"CN","cn"}, {"TW","tw"}, {"HK","hk"}, {"SG","sg"},
                    {"MY","my"}, {"TH","th"}, {"ID","id"}, {"PH","ph"},
                    {"VN","vn"}, {"IN","in"},
                    // Moyen-Orient / Afrique
                    {"ZA","za"},
                    {"AE","me"}, {"SA","me"}, {"KW","me"},
                    {"QA","me"}, {"BH","me"}, {"OM","me"},
                };
                return asusMap.TryGetValue(iso, out var ac) ? ac : "";
            }

            // ── Gigabyte ─────────────────────────────────────────────────────
            // gigabyte.com/{pays}/Motherboard/... — codes ISO standard (gb, fr, de…)
            if (bUp.Contains("GIGABYTE"))
            {
                var gigaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Europe
                    {"FR","fr"}, {"DE","de"}, {"IT","it"}, {"ES","es"},
                    {"GB","gb"}, {"NL","nl"}, {"BE","be"}, {"PL","pl"},
                    {"PT","pt"}, {"SE","se"}, {"NO","no"}, {"DK","dk"},
                    {"FI","fi"}, {"AT","at"}, {"CH","ch"}, {"CZ","cz"},
                    {"SK","sk"}, {"HU","hu"}, {"RO","ro"}, {"BG","bg"},
                    {"GR","gr"}, {"HR","hr"}, {"RS","rs"}, {"TR","tr"},
                    {"UA","ua"}, {"RU","ru"}, {"IL","il"},
                    // Amériques
                    {"US","us"}, {"CA","ca"}, {"MX","mx"}, {"BR","br"},
                    // Asie-Pacifique
                    {"AU","au"}, {"NZ","nz"}, {"JP","jp"}, {"KR","kr"},
                    {"CN","cn"}, {"TW","tw"}, {"HK","hk"}, {"SG","sg"},
                    {"MY","my"}, {"TH","th"}, {"ID","id"}, {"PH","ph"},
                    {"VN","vn"}, {"IN","in"},
                    // Moyen-Orient / Afrique
                    {"ZA","za"}, {"AE","ae"}, {"SA","sa"},
                };
                return gigaMap.TryGetValue(iso, out var gc) ? gc : "";
            }

            // MSI et ASRock : pages produit identiques dans le monde entier — pas de segment pays
            return "";
        }

        /// <summary>
        /// Espaces → tirets, parenthèses retirées.
        /// "ROG STRIX Z790-F GAMING WIFI" → "ROG-STRIX-Z790-F-GAMING-WIFI"
        /// </summary>
        private static string MakeSlug(string model)
        {
            var s = Regex.Replace(model, @"\s+", "-");
            return Regex.Replace(s, @"[()\\\/]", "").Trim('-');
        }

        /// <summary>
        /// Détermine la plateforme CPU (Intel / AMD) à partir du chipset dans le nom du modèle.
        /// Utilisé pour les URLs ASRock qui incluent la plateforme dans le chemin.
        /// </summary>
    }
}
