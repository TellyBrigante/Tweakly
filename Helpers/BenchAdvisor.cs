using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Conseiller du Tweakly Score (v1.3.5, idée « verdicts → actions ») : quand le score
    /// Système est bas, on VÉRIFIE réellement les causes connues de micro-saccades sur LA
    /// machine et on renvoie des constats en français clair, chacun avec une action.
    /// Retour utilisateur à l'origine : « réduit l'autostart, ça veut dire quoi ? même moi
    /// je capte pas » → fini les pistes vagues, place aux causes CONSTATÉES.
    ///
    /// Les clés lues sont EXACTEMENT celles que manipulent les pages CPU / Windows
    /// (cohérence garantie : le bouton d'action mène au réglage qui corrige).
    /// Tout est best-effort : un check qui échoue est simplement omis.
    /// </summary>
    public static class BenchAdvisor
    {
        public sealed class Finding
        {
            public string Text        { get; init; } = "";   // constat en français clair
            public string ActionLabel { get; init; } = "";   // libellé du bouton
            public string NavTag      { get; init; } = "";   // page Tweakly (Tag de nav) — ou vide
            public string Uri         { get; init; } = "";   // URI externe (ms-settings:…) — ou vide
        }

        // Plans d'alimentation « performances » (GUID Windows publics)
        private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

        /// <summary>Analyse les causes connues d'un score Système bas. Peut renvoyer une liste vide.</summary>
        public static List<Finding> Analyze()
        {
            var list = new List<Finding>();

            // 1. Plan d'alimentation pas en mode performances
            try
            {
                using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                var outp = (p?.StandardOutput.ReadToEnd() ?? "").ToLowerInvariant();
                p?.WaitForExit(3000);
                if (outp.Length > 0 && !outp.Contains(HighPerfGuid) && !outp.Contains(UltimateGuid))
                    list.Add(new Finding
                    {
                        Text = "Ton plan d'alimentation Windows n'est pas en mode performances — "
                             + "le CPU baisse sa fréquence dès qu'il le peut, ce qui crée des irrégularités.",
                        ActionLabel = "Corriger dans Optimisations > CPU", NavTag = "CPU",
                    });
            }
            catch { }

            // 2. Game DVR : capture vidéo permanente en arrière-plan
            try
            {
                var v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
                    "AppCaptureEnabled", null);
                if (v == null || Convert.ToInt32(v) != 0)
                    list.Add(new Finding
                    {
                        Text = "L'enregistrement Game DVR tourne en permanence pendant tes jeux "
                             + "(c'est lui qui permet « les 30 dernières secondes » de la Game Bar).",
                        ActionLabel = "Corriger dans Optimisations > Windows", NavTag = "Windows",
                    });
            }
            catch { }

            // 6. Programmes lancés avec Windows — UNIQUEMENT les ACTIFS.
            // ⚠️ PIÈGE (vécu, capture utilisateur à l'appui ET déjà documenté dans
            // SUCCESSION §4 du temps du Gestionnaire de démarrage) : désactiver un
            // programme au démarrage ne retire PAS sa valeur de la clé Run — Windows le
            // marque dans Explorer\StartupApproved\Run (byte 0 : PAIR = activé,
            // 0x03 = désactivé, entrée absente = activé par défaut). Compter Run brut
            // = nommer des programmes qui ne se lancent pas (EpicGamesLauncher déjà
            // désactivé chez l'utilisateur, par ex.) → crédibilité du conseil morte.
            try
            {
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectActiveRunNames(Registry.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", names);
                CollectActiveRunNames(Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", names);
                CollectActiveRunNames(Registry.LocalMachine,
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", names);
                if (names.Count >= 6)
                {
                    var sample = string.Join(", ", System.Linq.Enumerable.Take(names, 3));
                    list.Add(new Finding
                    {
                        Text = $"{names.Count} programmes ACTIFS se lancent avec Windows (dont {sample}…) — "
                             + "chacun consomme du CPU et de la RAM en permanence, même fermé.",
                        ActionLabel = "Gérer les programmes au démarrage", Uri = "ms-settings:startupapps",
                    });
                }
            }
            catch { }

            return list;
        }

        /// <summary>
        /// Noms des valeurs Run RÉELLEMENT actives : croise la clé Run avec
        /// StartupApproved (même ruche) — byte 0 pair = activé, impair (0x03) = désactivé,
        /// entrée absente de StartupApproved = activé par défaut.
        /// </summary>
        private static void CollectActiveRunNames(RegistryKey root, string runPath,
                                                  string approvedPath, SortedSet<string> names)
        {
            try
            {
                using var run      = root.OpenSubKey(runPath);
                if (run == null) return;
                using var approved = root.OpenSubKey(approvedPath);

                foreach (var n in run.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (approved?.GetValue(n) is byte[] b && b.Length > 0 && (b[0] & 0x01) == 0x01)
                        continue;   // 0x03 & co : désactivé par l'utilisateur → on ne le compte PAS
                    names.Add(n);
                }
            }
            catch { }
        }
    }
}
