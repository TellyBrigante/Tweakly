using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Gère le démarrage automatique de Tweakly avec Windows.
    ///
    /// ⚠️ POURQUOI une tâche planifiée et PAS la clé HKCU\…\Run :
    /// Tweakly est `requireAdministrator` dans son manifest. Windows REFUSE de lancer une app
    /// élevée depuis la clé Run au démarrage (par sécurité — sinon n'importe quoi pourrait
    /// auto-s'élever). Résultat : la clé est posée, mais rien ne se lance, ou un UAC apparaît
    /// dans le vide. La voie propre = une tâche planifiée avec « Run with highest privileges »,
    /// trigger « At log on », action = lancer Tweakly.exe. C'est ce que font tous les outils
    /// sérieux (AutoHotkey, Discord, etc.).
    ///
    /// HISTORIQUE v1.2.9 : on créait la tâche via un XML déclarant encoding="UTF-16" mais
    /// écrit en UTF-8 BOM (incohérence). Sur certains comptes (notamment compte Microsoft /
    /// Azure AD), schtasks acceptait le fichier sans vraiment enregistrer la tâche → Enable()
    /// renvoyait true (exit 0) mais IsEnabled() retournait false ensuite → la case se
    /// décochait toute seule au redémarrage de l'app.
    ///
    /// FIX (>= v1.3.0) : on utilise schtasks en MODE LIGNE DE COMMANDE (plus simple, plus
    /// fiable, pas de problème d'encodage). On capture stdout/stderr pour exposer un message
    /// d'erreur exploitable (LastError) et on fait un round-trip de vérification : après
    /// /create, on relance /query — si la tâche n'existe pas vraiment, Enable() renvoie false
    /// (au lieu de mentir comme avant).
    /// </summary>
    internal static class StartupManager
    {
        private const string TaskName = "Tweakly_AutoStart";

        /// <summary>Dernier message d'erreur (stderr/stdout de schtasks) — utile au log.</summary>
        public static string? LastError { get; private set; }

        /// <summary>Le démarrage Windows est-il actif ? (existence de la tâche planifiée)</summary>
        public static bool IsEnabled()
        {
            try
            {
                var (exit, _, _) = RunSchtasks($"/query /tn \"{TaskName}\"");
                return exit == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Active : crée la tâche planifiée (admin + at-logon).
        /// Vérifie via re-query que la création a VRAIMENT abouti — si la tâche n'existe pas
        /// après /create, retourne false (et expose LastError pour le diagnostic).
        /// </summary>
        public static bool Enable()
        {
            LastError = null;
            try
            {
                var exe = Process.GetCurrentProcess().MainModule!.FileName;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                {
                    LastError = "Chemin de l'exécutable Tweakly introuvable.";
                    return false;
                }

                // Mode ligne de commande (schtasks /create) :
                //   /tn  : nom de la tâche
                //   /tr  : commande à lancer (chemin de l'exe entre guillemets internes \"…\")
                //   /sc ONLOGON : trigger « à l'ouverture de session »
                //   /rl HIGHEST : équivalent de « Exécuter avec les privilèges les plus élevés »
                //   /F   : force (overwrite si la tâche existe déjà)
                // PAS d'XML, donc pas de problème d'encodage UTF-16/UTF-8.
                // PAS de /ru/rp : la tâche tourne sous l'utilisateur courant par défaut, ce qui
                // évite les problèmes de SID Azure AD (compte Microsoft) qu'on avait en v1.2.9.
                // --startup : marque ce lancement comme « par Windows au boot » → c'est le SEUL
                // cas où le réglage « Démarrer minimisé » s'applique (un lancement manuel affiche
                // toujours l'app au premier plan). Cf. App.LaunchedAtStartup / ShouldStartMinimized.
                var args = $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --startup\" /sc ONLOGON /rl HIGHEST /F";
                var (exit, stdout, stderr) = RunSchtasks(args);

                if (exit != 0)
                {
                    // Schtasks a clairement échoué — on remonte la raison.
                    LastError = Combine(stderr, stdout);
                    return false;
                }

                // ⭐ ROUND-TRIP : on RE-vérifie que la tâche existe vraiment. C'est la garantie
                // qu'on ne ment plus à l'utilisateur (le bug v1.2.9 venait précisément de là :
                // /create renvoyait 0 sans persister la tâche).
                if (!IsEnabled())
                {
                    LastError = "schtasks a renvoyé OK mais la tâche n'a pas été enregistrée. "
                              + "Diagnostic stdout=" + (stdout ?? "(vide)").Trim()
                              + " stderr=" + (stderr ?? "(vide)").Trim();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Auto-réparation des tâches créées par d'anciennes versions (sans l'argument
        /// « --startup ») : si la tâche existe mais que sa commande ne contient pas le flag,
        /// on la re-crée AVEC (l'app est élevée → schtasks /F écrase sans UAC). Sans ça,
        /// un user qui a « démarrer avec Windows » verrait l'app s'ouvrir EN GRAND au boot
        /// (faute du flag) au lieu de rester minimisée comme avant. Silencieux et idempotent.
        /// À appeler une fois au démarrage (best-effort, en arrière-plan).
        /// </summary>
        public static void EnsureStartupArg()
        {
            try
            {
                if (!IsEnabled()) return;
                // /v /fo LIST : sortie verbeuse incluant la commande. On ne parse PAS de
                // libellé localisé (piège FR/EN) — on cherche juste la sous-chaîne du flag.
                var (exit, stdout, _) = RunSchtasks($"/query /tn \"{TaskName}\" /v /fo LIST");
                if (exit != 0) return;
                if (!(stdout ?? "").Contains("--startup", StringComparison.OrdinalIgnoreCase))
                    Enable();   // re-crée la tâche avec le flag (chemin de l'exe rafraîchi au passage)
            }
            catch { }
        }

        /// <summary>Désactive : supprime la tâche planifiée (et l'ancienne clé Run si présente).</summary>
        public static bool Disable()
        {
            LastError = null;

            // Nettoie aussi la clé HKCU\Run héritée (pré-v1.2.9) pour ne pas laisser de résidu.
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                k?.DeleteValue("Tweakly", throwOnMissingValue: false);
            }
            catch { }

            try
            {
                var (exit, stdout, stderr) = RunSchtasks($"/delete /tn \"{TaskName}\" /F");
                // exit != 0 si la tâche n'existait pas → ce n'est PAS une erreur (idempotent).
                // On considère que le résultat final = la tâche n'existe plus → succès.
                if (IsEnabled())
                {
                    LastError = "Suppression refusée — " + Combine(stderr, stdout);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>Lance schtasks et CAPTURE stdout + stderr (5 s timeout).</summary>
        private static (int exitCode, string stdout, string stderr) RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo(WindowsSystemTools.PathFor("schtasks.exe"), args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "Impossible de lancer schtasks.");

            // Lecture asynchrone pour éviter un deadlock si l'un des deux buffers se remplit.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(true); } catch { }
                return (-1, "", "schtasks n'a pas répondu (timeout).");
            }
            return (p.ExitCode, stdoutTask.Result ?? "", stderrTask.Result ?? "");
        }

        private static string Combine(string? a, string? b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length > 0 && b.Length > 0) return a + " | " + b;
            if (a.Length > 0) return a;
            if (b.Length > 0) return b;
            return "(aucun message)";
        }
    }
}
