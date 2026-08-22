using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers
{
    internal sealed class CleanupEstimateResult
    {
        public long Bytes { get; init; }
        public int Files { get; init; }
        public int Skipped { get; init; }
        public bool Available { get; init; } = true;

        public static CleanupEstimateResult Combine(params CleanupEstimateResult[] results) => new()
        {
            Bytes = results.Sum(result => result.Bytes),
            Files = results.Sum(result => result.Files),
            Skipped = results.Sum(result => result.Skipped),
            Available = results.All(result => result.Available),
        };
    }

    internal sealed class CleanupOperationResult
    {
        public long Freed { get; init; }
        public int Ops { get; init; }
        public int Residues { get; init; }
        public int ResiduesRemoved { get; init; }
        public int Skipped { get; init; }
        public int Errors { get; init; }
        public string Summary { get; init; } = "";
        public List<string> Details { get; init; } = new();

        public static CleanupOperationResult Combine(params CleanupOperationResult[] results)
        {
            long freed = results.Sum(result => result.Freed);
            int ops = results.Sum(result => result.Ops);
            int residues = results.Sum(result => result.Residues);
            int residuesRemoved = results.Sum(result => result.ResiduesRemoved);
            int skipped = results.Sum(result => result.Skipped);
            int errors = results.Sum(result => result.Errors);

            return new CleanupOperationResult
            {
                Freed = freed,
                Ops = ops,
                Residues = residues,
                ResiduesRemoved = residuesRemoved,
                Skipped = skipped,
                Errors = errors,
                Summary = FormatSummary(freed, ops, skipped, errors),
                Details = results.SelectMany(result => result.Details).ToList(),
            };
        }

        public static string FormatSummary(long freed, int ops, int skipped, int errors)
        {
            var parts = new List<string> { FormatBytes(freed) };
            if (ops > 0) parts.Add($"{ops} élément(s)");
            if (skipped > 0) parts.Add($"{skipped} ignoré(s)");
            if (errors > 0) parts.Add($"{errors} erreur(s)");
            return string.Join(" | ", parts);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} Go";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} Mo";
            if (bytes >= 1_024) return $"{bytes / 1_024.0:F0} Ko";
            return $"{bytes} octet{(bytes > 1 ? "s" : "")}";
        }
    }

    /// <summary>
    /// Contient les opérations système de nettoyage. La page WPF ne gère que
    /// la sélection et l'affichage ; chaque opération retourne un résultat vérifiable.
    /// </summary>
    internal static class CleanupOperations
    {
        public static CleanupEstimateResult EstimateFolder(string path, string searchPattern = "*", bool recursive = true)
        {
            if (!Directory.Exists(path))
                return new CleanupEstimateResult();

            try
            {
                using IEnumerator<string> accessProbe = Directory
                    .EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly)
                    .GetEnumerator();
                _ = accessProbe.MoveNext();
            }
            catch
            {
                return new CleanupEstimateResult { Available = false, Skipped = 1 };
            }

            long bytes = 0;
            int files = 0;
            int skipped = 0;
            var options = CreateEnumerationOptions(recursive);

            try
            {
                foreach (string file in Directory.EnumerateFiles(path, searchPattern, options))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists) continue;
                        bytes += info.Length;
                        files++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            }
            catch
            {
                skipped++;
            }

            return new CleanupEstimateResult { Bytes = bytes, Files = files, Skipped = skipped };
        }

        public static CleanupOperationResult CleanFolder(string path)
        {
            if (!Directory.Exists(path))
                return new CleanupOperationResult { Summary = "Introuvable" };

            long freed = 0;
            int ops = 0;
            int skipped = 0;
            var options = CreateEnumerationOptions(recursive: true);

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*", options).ToList();
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("cleanup-folder-enumeration:" + path, "Nettoyage : dossier inaccessible", ex);
                return new CleanupOperationResult
                {
                    Errors = 1,
                    Summary = "Accès refusé",
                    Details = { $"Dossier inaccessible : {path}" },
                };
            }

            foreach (string file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    long length = info.Exists ? info.Length : 0;
                    info.Delete();
                    freed += length;
                    ops++;
                }
                catch
                {
                    // Un fichier temporaire ouvert est attendu : il est compté comme ignoré.
                    skipped++;
                }
            }

            try
            {
                // Les fichiers système, cachés et les points de jonction sont volontairement
                // exclus plus haut. Ne jamais contourner cette protection avec une suppression
                // récursive du dossier parent : seuls les dossiers réellement vides sont retirés.
                foreach (string directory in Directory
                             .EnumerateDirectories(path, "*", options)
                             .OrderByDescending(static directory => directory.Length))
                {
                    try
                    {
                        Directory.Delete(directory, recursive: false);
                        ops++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception ex)
            {
                skipped++;
                AppLog.ErrorOnce("cleanup-folder-subdirectories:" + path, "Nettoyage : sous-dossiers inaccessibles", ex);
            }

            return new CleanupOperationResult
            {
                Freed = freed,
                Ops = ops,
                Skipped = skipped,
                Summary = CleanupOperationResult.FormatSummary(freed, ops, skipped, errors: 0),
            };
        }

        public static CleanupOperationResult RunTrim()
        {
            int total = 0;
            int completed = 0;
            int skipped = 0;
            int errors = 0;
            var details = new List<string>();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                total++;
                string letter = drive.Name.TrimEnd('\\');
                ProcessCommandResult result = ProcessCommand.Run("defrag", $"{letter} /L", 120_000);
                if (result.Success)
                {
                    completed++;
                    details.Add($"TRIM (optimisation SSD) - {letter} : terminé");
                    continue;
                }

                if (result.Started && !result.TimedOut)
                {
                    skipped++;
                    details.Add($"TRIM - {letter} : ignoré (volume non compatible ou occupé)");
                    continue;
                }

                errors++;
                string reason = result.FailureDescription;
                details.Add($"TRIM - {letter} : échec ({reason})");
                AppLog.WriteOnce("cleanup-trim:" + letter, $"Nettoyage : TRIM {letter} échoué : {reason}");
            }

            string summary = total == 0 ? "0 volume" : $"{completed}/{total} volume(s)";
            if (skipped > 0) summary += $", {skipped} ignoré(s)";
            if (errors > 0) summary += $" | {errors} erreur(s)";
            return new CleanupOperationResult
            {
                Ops = completed,
                Skipped = skipped,
                Errors = errors,
                Summary = summary,
                Details = details,
            };
        }

        public static CleanupOperationResult ClearEventLogs()
        {
            ProcessCommandResult list = ProcessCommand.Run("wevtutil", "el", 15_000);
            if (!list.Success)
            {
                string reason = list.FailureDescription;
                AppLog.WriteOnce("cleanup-eventlogs-list", "Nettoyage : liste des journaux inaccessible : " + reason);
                return new CleanupOperationResult
                {
                    Errors = 1,
                    Summary = "Journaux inaccessibles",
                    Details = { "Liste des journaux Windows inaccessible : " + reason },
                };
            }

            int completed = 0;
            int skipped = 0;
            int errors = 0;
            var details = new List<string>();

            foreach (string rawName in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string name = rawName.Trim();
                if (name.Length == 0) continue;

                string quotedName = name.Replace("\"", "\\\"", StringComparison.Ordinal);
                ProcessCommandResult clear = ProcessCommand.Run("wevtutil", $"cl \"{quotedName}\"", 5_000);
                if (clear.Success)
                {
                    completed++;
                }
                else if (clear.Started && !clear.TimedOut)
                {
                    // Certains canaux Windows désactivés ou protégés ne peuvent pas être vidés.
                    skipped++;
                }
                else
                {
                    errors++;
                    if (details.Count < 5)
                        details.Add($"Journal {name} : {clear.FailureDescription}");
                }
            }

            string summary = $"{completed} journal(aux) traité(s)";
            if (skipped > 0) summary += $", {skipped} ignoré(s)";
            if (errors > 0) summary += $" | {errors} erreur(s)";
            return new CleanupOperationResult
            {
                Ops = completed,
                Skipped = skipped,
                Errors = errors,
                Summary = summary,
                Details = details,
            };
        }

        public static CleanupOperationResult CleanSoftwareResidues()
        {
            CleanupOperationResult registry = CleanOrphanUninstallEntries();
            CleanupOperationResult appPaths = CleanOrphanAppPaths();
            CleanupOperationResult startup = CleanOrphanStartupValues();
            CleanupOperationResult? shortcuts = null;
            Exception? threadError = null;
            using var cancellation = new CancellationTokenSource();
            var thread = new Thread(() =>
            {
                try
                {
                    shortcuts = CleanBrokenShortcuts(cancellation.Token);
                }
                catch (Exception ex)
                {
                    threadError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "Tweakly-CleanupShortcuts",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!thread.Join(20_000))
            {
                cancellation.Cancel();
                _ = thread.Join(2_000);
                AppLog.WriteOnce("cleanup-shortcuts-timeout", "Nettoyage : l'analyse des raccourcis a dépassé 20 000 ms.");
                shortcuts = new CleanupOperationResult
                {
                    Skipped = 1,
                    Summary = "Analyse des raccourcis arrêtée",
                    Details = { "Analyse des raccourcis : délai de 20 000 ms dépassé" },
                };
            }
            else if (threadError is not null)
            {
                AppLog.ErrorOnce("cleanup-shortcuts-thread", "Nettoyage : analyse des raccourcis échouée", threadError);
                shortcuts = new CleanupOperationResult
                {
                    Errors = 1,
                    Summary = "Analyse des raccourcis impossible",
                    Details = { "Analyse des raccourcis impossible : " + threadError.Message },
                };
            }

            CleanupOperationResult combined = CleanupOperationResult.Combine(
                registry,
                appPaths,
                startup,
                shortcuts ?? new CleanupOperationResult { Errors = 1, Summary = "Analyse des raccourcis impossible" });
            return new CleanupOperationResult
            {
                Freed = combined.Freed,
                Ops = combined.Ops,
                Residues = combined.Residues,
                ResiduesRemoved = combined.ResiduesRemoved,
                Skipped = combined.Skipped,
                Errors = combined.Errors,
                Summary = combined.Residues > 0
                    ? $"{combined.ResiduesRemoved} traité(s) sur {combined.Residues} résidu(s) détecté(s)"
                    : "aucun résidu sûr détecté",
                Details = combined.Details,
            };
        }

        private static CleanupOperationResult CleanOrphanAppPaths()
        {
            int detected = 0;
            int removed = 0;
            int skipped = 0;
            var details = new List<string>();
            var roots = new (RegistryKey Hive, string Path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
            };

            foreach ((RegistryKey hive, string path) in roots)
            {
                try
                {
                    using RegistryKey? root = hive.OpenSubKey(path, writable: true);
                    if (root == null) continue;

                    foreach (string subKeyName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey? key = root.OpenSubKey(subKeyName))
                            {
                                if (key == null || !IsProvablyOrphanedAppPath(key)) continue;
                            }

                            detected++;
                            string fullKeyName = root.Name + "\\" + subKeyName;
                            if (!TryExportRegistryKey(fullKeyName, details))
                            {
                                skipped++;
                                continue;
                            }

                            root.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            AppLog.ErrorOnce(
                                "cleanup-app-path:" + subKeyName,
                                "Nettoyage : App Path orphelin ignoré",
                                ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    details.Add("Registre inaccessible : " + path);
                    AppLog.ErrorOnce("cleanup-app-path-root:" + path, "Nettoyage : App Paths inaccessible", ex);
                }
            }

            return new CleanupOperationResult
            {
                Ops = removed,
                Residues = detected,
                ResiduesRemoved = removed,
                Skipped = skipped,
                Summary = detected > 0
                    ? $"{removed} App Path(s) retiré(s) sur {detected} détecté(s)"
                    : "aucun App Path orphelin",
                Details = details,
            };
        }

        internal static bool IsProvablyOrphanedAppPath(RegistryKey key)
        {
            ArgumentNullException.ThrowIfNull(key);
            string target = NormalizeAbsolutePath(
                key.GetValue(null, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "",
                requireExecutable: true);
            return target.Length > 0 && DriveExists(target) && !File.Exists(target);
        }

        private static CleanupOperationResult CleanOrphanStartupValues()
        {
            int detected = 0;
            int removed = 0;
            int skipped = 0;
            var details = new List<string>();
            var roots = new (RegistryKey Hive, string Path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            };

            foreach ((RegistryKey hive, string path) in roots)
            {
                try
                {
                    using RegistryKey? root = hive.OpenSubKey(path, writable: true);
                    if (root == null) continue;

                    string[] candidates = root.GetValueNames()
                        .Where(name => IsProvablyOrphanedStartupCommand(
                            root.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string))
                        .ToArray();
                    if (candidates.Length == 0) continue;

                    detected += candidates.Length;
                    if (!TryExportRegistryKey(root.Name, details))
                    {
                        skipped += candidates.Length;
                        continue;
                    }

                    foreach (string valueName in candidates)
                    {
                        try
                        {
                            root.DeleteValue(valueName, throwOnMissingValue: false);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            AppLog.ErrorOnce(
                                "cleanup-startup:" + root.Name + ":" + valueName,
                                "Nettoyage : démarrage orphelin ignoré",
                                ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    details.Add("Registre inaccessible : " + path);
                    AppLog.ErrorOnce("cleanup-startup-root:" + path, "Nettoyage : démarrage automatique inaccessible", ex);
                }
            }

            return new CleanupOperationResult
            {
                Ops = removed,
                Residues = detected,
                ResiduesRemoved = removed,
                Skipped = skipped,
                Summary = detected > 0
                    ? $"{removed} démarrage(s) retiré(s) sur {detected} détecté(s)"
                    : "aucun démarrage orphelin",
                Details = details,
            };
        }

        internal static bool IsProvablyOrphanedStartupCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            string executable = ExtractExecutablePath(command);
            return executable.Length > 0 && DriveExists(executable) && !File.Exists(executable);
        }

        private static CleanupOperationResult CleanOrphanUninstallEntries()
        {
            int detected = 0;
            int removed = 0;
            int skipped = 0;
            var details = new List<string>();
            var roots = new (RegistryKey Hive, string Path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach ((RegistryKey hive, string path) in roots)
            {
                try
                {
                    using RegistryKey? root = hive.OpenSubKey(path, writable: true);
                    if (root == null) continue;

                    foreach (string subKeyName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey? key = root.OpenSubKey(subKeyName))
                            {
                                if (key == null) continue;
                                if (!IsProvablyOrphanedUninstallEntry(key))
                                    continue;
                            }

                            detected++;
                            string fullKeyName = root.Name + "\\" + subKeyName;
                            if (!TryExportRegistryKey(fullKeyName, details))
                            {
                                skipped++;
                                continue;
                            }

                            root.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            AppLog.ErrorOnce("cleanup-uninstall-entry:" + subKeyName, "Nettoyage : résidu de désinstallation ignoré", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    details.Add($"Registre inaccessible : {path}");
                    AppLog.ErrorOnce("cleanup-uninstall-root:" + path, "Nettoyage : registre de désinstallation inaccessible", ex);
                }
            }

            return new CleanupOperationResult
            {
                Ops = removed,
                Residues = detected,
                ResiduesRemoved = removed,
                Skipped = skipped,
                Summary = detected > 0
                    ? $"{removed} entrée(s) retirée(s) sur {detected} détectée(s)"
                    : "aucune entrée orpheline",
                Details = details,
            };
        }

        private static bool TryExportRegistryKey(string fullKeyName, List<string> details)
        {
            try
            {
                string backupDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Tweakly",
                    "RegistryBackups");
                Directory.CreateDirectory(backupDirectory);

                string safeName = string.Concat(fullKeyName.Select(static c =>
                    char.IsLetterOrDigit(c) ? c : '_'));
                string backupPath = Path.Combine(
                    backupDirectory,
                    $"cleanup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeName}.reg");

                ProcessCommandResult export = ProcessCommand.Run(
                    "reg.exe",
                    $"export \"{fullKeyName}\" \"{backupPath}\" /y",
                    10_000);
                if (export.Success && File.Exists(backupPath))
                {
                    details.Add("Sauvegarde registre : " + backupPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("cleanup-registry-backup:" + fullKeyName, "Nettoyage : sauvegarde registre impossible", ex);
            }

            details.Add("Suppression registre ignorée : sauvegarde impossible pour " + fullKeyName);
            return false;
        }

        internal static bool IsProvablyOrphanedUninstallEntry(RegistryKey key)
        {
            ArgumentNullException.ThrowIfNull(key);

            string? displayName = key.GetValue("DisplayName") as string;
            string? installLocation = key.GetValue("InstallLocation") as string;
            string? uninstallString = key.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(displayName)
                || string.IsNullOrWhiteSpace(installLocation)
                || string.IsNullOrWhiteSpace(uninstallString))
                return false;

            if (ReadDword(key, "WindowsInstaller") == 1
                || ReadDword(key, "SystemComponent") == 1
                || ReadDword(key, "NoRemove") == 1
                || !string.IsNullOrWhiteSpace(key.GetValue("ParentKeyName") as string)
                || !string.IsNullOrWhiteSpace(key.GetValue("ReleaseType") as string))
                return false;

            string normalizedLocation = NormalizeAbsolutePath(installLocation, requireExecutable: false);
            if (normalizedLocation.Length == 0
                || !DriveExists(normalizedLocation)
                || Directory.Exists(normalizedLocation))
                return false;

            string executable = ExtractExecutablePath(uninstallString);
            if (executable.Length == 0
                || !DriveExists(executable)
                || File.Exists(executable))
                return false;

            string displayIcon = NormalizeAbsolutePath(
                (key.GetValue("DisplayIcon") as string ?? "").Split(',')[0],
                requireExecutable: true);
            if (displayIcon.Length > 0 && File.Exists(displayIcon))
                return false;

            return true;
        }

        private static int ReadDword(RegistryKey key, string name)
            => key.GetValue(name) is int value ? value : 0;

        private static bool DriveExists(string path)
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }

        private static string ExtractExecutablePath(string command)
        {
            string expanded = Environment.ExpandEnvironmentVariables(command).Trim();
            if (expanded.Length == 0)
                return "";

            string candidate;
            if (expanded[0] == '"')
            {
                int closingQuote = expanded.IndexOf('"', 1);
                if (closingQuote <= 1)
                    return "";
                candidate = expanded[1..closingQuote];
            }
            else
            {
                int executableEnd = expanded.IndexOf(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase);
                if (executableEnd < 0)
                    return "";
                candidate = expanded[..(executableEnd + 4)];
            }

            return NormalizeAbsolutePath(candidate, requireExecutable: true);
        }

        private static string NormalizeAbsolutePath(string value, bool requireExecutable)
        {
            string candidate = Environment.ExpandEnvironmentVariables(value)
                .Trim()
                .Trim('"')
                .TrimEnd('\\');
            if (!Path.IsPathFullyQualified(candidate))
                return "";
            if (requireExecutable
                && !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return "";

            try
            {
                return Path.GetFullPath(candidate);
            }
            catch
            {
                return "";
            }
        }

        private static CleanupOperationResult CleanBrokenShortcuts(CancellationToken cancellationToken)
        {
            int detected = 0;
            int removed = 0;
            int skipped = 0;
            string quarantineRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Tweakly",
                "CleanupQuarantine",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            var directories = new (string Path, bool Recursive, string Scope)[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true, "UserStartMenu"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true, "CommonStartMenu"),
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false, "UserDesktop"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false, "CommonDesktop"),
            }.Where(directory => !string.IsNullOrEmpty(directory.Path))
             .DistinctBy(directory => directory.Path);

            var details = new List<string>();
            foreach ((string directory, bool recursive, string scope) in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(directory)) continue;

                List<string> shortcuts;
                try
                {
                    EnumerationOptions options = CreateEnumerationOptions(recursive);
                    shortcuts = Directory.EnumerateFiles(directory, "*.lnk", options).ToList();
                }
                catch
                {
                    skipped++;
                    continue;
                }

                foreach (string shortcutPath in shortcuts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        string target = ReadShortcutTarget(shortcutPath);
                        if (!IsBrokenShortcutTarget(target)) continue;

                        detected++;
                        cancellationToken.ThrowIfCancellationRequested();
                        string quarantined = QuarantineShortcut(
                            shortcutPath,
                            directory,
                            scope,
                            quarantineRoot);
                        removed++;
                        if (details.Count < 20)
                            details.Add("Raccourci cassé mis en quarantaine : " + quarantined);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        if (details.Count < 20)
                            details.Add("Raccourci ignoré : " + shortcutPath + " — " + ex.Message);
                    }
                }
            }

            return new CleanupOperationResult
            {
                Ops = removed,
                Residues = detected,
                ResiduesRemoved = removed,
                Skipped = skipped,
                Summary = detected > 0
                    ? $"{removed} raccourci(s) mis en quarantaine sur {detected} détecté(s)"
                    : "aucun raccourci cassé",
                Details = details,
            };
        }

        private static string QuarantineShortcut(
            string shortcutPath,
            string sourceRoot,
            string scope,
            string quarantineRoot)
        {
            string relativePath = Path.GetRelativePath(sourceRoot, shortcutPath);
            if (Path.IsPathFullyQualified(relativePath)
                || relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Chemin de raccourci hors de la racine autorisée.");

            string destination = Path.Combine(quarantineRoot, scope, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    Path.GetDirectoryName(destination)!,
                    Path.GetFileNameWithoutExtension(destination) + "-" + Guid.NewGuid().ToString("N") + ".lnk");
            }

            File.Move(shortcutPath, destination);
            return destination;
        }

        private static string ReadShortcutTarget(string shortcutPath)
        {
            Type shellLinkType = Type.GetTypeFromCLSID(ShellLinkClsid)
                ?? throw new InvalidOperationException("ShellLink indisponible.");
            object shellLink = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("ShellLink indisponible.");
            IShellLinkW link = (IShellLinkW)shellLink;
            try
            {
                ((IPersistFile)link).Load(shortcutPath, 0);
                var target = new StringBuilder(32_768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                return target.ToString();
            }
            finally
            {
                ReleaseComObject(shellLink);
            }
        }

        internal static bool IsBrokenShortcutTarget(string target)
        {
            string expanded = Environment.ExpandEnvironmentVariables(target).Trim().Trim('"');
            if (!Path.IsPathFullyQualified(expanded))
                return false;

            string? drive = Path.GetPathRoot(expanded);
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
                return false;

            return !File.Exists(expanded) && !Directory.Exists(expanded);
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }

        private static EnumerationOptions CreateEnumerationOptions(bool recursive) => new()
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                int cchMaxPath,
                IntPtr pfd,
                uint fFlags);

            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

    }
}
