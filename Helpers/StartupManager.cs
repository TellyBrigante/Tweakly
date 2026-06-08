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
    /// </summary>
    internal static class StartupManager
    {
        private const string TaskName = "Tweakly_AutoStart";

        /// <summary>Le démarrage Windows est-il actif ? (existence de la tâche planifiée)</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\"")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                p?.WaitForExit(5_000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>Active : crée la tâche planifiée (admin + at-logon).</summary>
        public static bool Enable()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule!.FileName;
                // On écrit un XML de tâche : permet de cocher « Run with highest privileges »,
                // « Run only when user is logged on », et de supprimer les conditions par défaut
                // (« stop on battery », « stop after 3 days » qui bloquent Tweakly autrement).
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
                var xml = BuildTaskXml(exe, sid);
                var xmlPath = Path.Combine(Path.GetTempPath(), $"tweakly_task_{Guid.NewGuid():N}.xml");
                File.WriteAllText(xmlPath, xml, new UTF8Encoding(true));   // UTF-8 BOM (schtasks exige)
                try
                {
                    // /F = force (overwrite si existante)
                    using var p = Process.Start(new ProcessStartInfo("schtasks",
                        $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /F")
                    {
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    });
                    p?.WaitForExit(10_000);
                    return p?.ExitCode == 0;
                }
                finally { try { File.Delete(xmlPath); } catch { } }
            }
            catch { return false; }
        }

        /// <summary>Désactive : supprime la tâche planifiée (et l'ancienne clé Run si présente).</summary>
        public static bool Disable()
        {
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
                using var p = Process.Start(new ProcessStartInfo("schtasks", $"/delete /tn \"{TaskName}\" /F")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                p?.WaitForExit(5_000);
                return true;   // succès même si la tâche n'existait pas
            }
            catch { return false; }
        }

        // XML schtasks : RunLevel HighestAvailable + Triggers LogonTrigger + Settings tolérants
        private static string BuildTaskXml(string exePath, string userSid)
        {
            var workDir  = Path.GetDirectoryName(exePath) ?? "";
            var safeExe  = System.Security.SecurityElement.Escape(exePath)!;
            var safeDir  = System.Security.SecurityElement.Escape(workDir)!;
            var safeSid  = System.Security.SecurityElement.Escape(userSid)!;
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Démarre Tweakly à l'ouverture de session.</Description></RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                $"      <UserId>{safeSid}</UserId>\r\n" +
                "      <Delay>PT15S</Delay>\r\n" +   // laisse Windows démarrer avant de spawn Tweakly
                "    </LogonTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                $"      <UserId>{safeSid}</UserId>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +   // ← l'équivalent de « Exécuter avec les privilèges les plus élevés »
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +   // 0 = pas de limite
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                $"      <Command>{safeExe}</Command>\r\n" +
                $"      <WorkingDirectory>{safeDir}</WorkingDirectory>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";
        }
    }
}
