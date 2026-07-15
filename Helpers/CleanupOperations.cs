using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace Optimisation_Tool.Helpers
{
    internal sealed class CleanupOperationResult
    {
        public long Freed { get; init; }
        public int Ops { get; init; }
        public int Residues { get; init; }
        public int Skipped { get; init; }
        public int Errors { get; init; }
        public string Summary { get; init; } = "";
        public List<string> Details { get; init; } = new();

        public static CleanupOperationResult Combine(params CleanupOperationResult[] results)
        {
            long freed = results.Sum(result => result.Freed);
            int ops = results.Sum(result => result.Ops);
            int residues = results.Sum(result => result.Residues);
            int skipped = results.Sum(result => result.Skipped);
            int errors = results.Sum(result => result.Errors);

            return new CleanupOperationResult
            {
                Freed = freed,
                Ops = ops,
                Residues = residues,
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
        public static CleanupOperationResult CleanFolder(string path)
        {
            if (!Directory.Exists(path))
                return new CleanupOperationResult { Summary = "Introuvable" };

            long freed = 0;
            int ops = 0;
            int skipped = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };

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
                foreach (string directory in Directory.EnumerateDirectories(path).ToList())
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
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
            CleanupOperationResult? shortcuts = null;
            Exception? threadError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    shortcuts = CleanBrokenShortcuts();
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
                AppLog.WriteOnce("cleanup-shortcuts-timeout", "Nettoyage : l'analyse des raccourcis a dépassé 20 000 ms.");
                shortcuts = new CleanupOperationResult
                {
                    Errors = 1,
                    Summary = "Analyse des raccourcis interrompue",
                    Details = { "Analyse des raccourcis : délai de 20 000 ms dépassé" },
                };
            }
            else if (threadError != null)
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
                shortcuts ?? new CleanupOperationResult { Errors = 1, Summary = "Analyse des raccourcis impossible" });
            return new CleanupOperationResult
            {
                Freed = combined.Freed,
                Ops = combined.Ops,
                Residues = combined.Ops,
                Skipped = combined.Skipped,
                Errors = combined.Errors,
                Summary = combined.Ops > 0 ? $"{combined.Ops} résidu(s)" : "0 résidu sûr",
                Details = combined.Details,
            };
        }

        private static CleanupOperationResult CleanOrphanUninstallEntries()
        {
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
                    if (root == null)
                    {
                        skipped++;
                        continue;
                    }

                    foreach (string subKeyName in root.GetSubKeyNames())
                    {
                        try
                        {
                            string? displayName;
                            string? installLocation;
                            using (RegistryKey? key = root.OpenSubKey(subKeyName))
                            {
                                if (key == null) continue;
                                displayName = key.GetValue("DisplayName") as string;
                                installLocation = key.GetValue("InstallLocation") as string;
                            }

                            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation)) continue;
                            installLocation = installLocation.Trim().Trim('"').TrimEnd('\\');
                            if (installLocation.Length < 4 || !installLocation.Contains(":\\", StringComparison.Ordinal)) continue;
                            string? drive = Path.GetPathRoot(installLocation);
                            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive) || Directory.Exists(installLocation)) continue;

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
                Residues = removed,
                Skipped = skipped,
                Summary = removed > 0 ? $"{removed} résidu(s)" : "0 résidu sûr",
                Details = details,
            };
        }

        private static CleanupOperationResult CleanBrokenShortcuts()
        {
            int removed = 0;
            int skipped = 0;
            var directories = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }.Where(directory => !string.IsNullOrEmpty(directory)).Distinct();

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return new CleanupOperationResult { Skipped = 1, Summary = "Raccourcis non analysés" };

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return new CleanupOperationResult { Skipped = 1, Summary = "Raccourcis non analysés" };

            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (string directory in directories)
                {
                    if (!Directory.Exists(directory)) continue;

                    List<string> shortcuts;
                    try
                    {
                        shortcuts = Directory.EnumerateFiles(directory, "*.lnk", options).ToList();
                    }
                    catch
                    {
                        skipped++;
                        continue;
                    }

                    foreach (string shortcutPath in shortcuts)
                    {
                        object? shortcut = null;
                        try
                        {
                            shortcut = shellType.InvokeMember(
                                "CreateShortcut",
                                System.Reflection.BindingFlags.InvokeMethod,
                                binder: null,
                                target: shell,
                                args: new object[] { shortcutPath });
                            string target = shortcut?.GetType().InvokeMember(
                                "TargetPath",
                                System.Reflection.BindingFlags.GetProperty,
                                binder: null,
                                target: shortcut,
                                args: null) as string ?? "";
                            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string? drive = Path.GetPathRoot(target);
                            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive) || File.Exists(target)) continue;

                            File.Delete(shortcutPath);
                            removed++;
                        }
                        catch
                        {
                            skipped++;
                        }
                        finally
                        {
                            ReleaseComObject(shortcut);
                        }
                    }
                }
            }
            finally
            {
                ReleaseComObject(shell);
            }

            return new CleanupOperationResult
            {
                Ops = removed,
                Residues = removed,
                Skipped = skipped,
                Summary = removed > 0 ? $"{removed} raccourci(s) retiré(s)" : "0 raccourci cassé",
            };
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }

    }
}
