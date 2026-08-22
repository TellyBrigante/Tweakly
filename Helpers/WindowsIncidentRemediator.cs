using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    public sealed class IncidentRepairResult
    {
        public IncidentRepairPhase Phase;
        public string Message = "";
        public List<string> Evidence = new();
        public List<string> VerifiedTargets = new();
    }

    public sealed class VssWriterState
    {
        public string Name = "";
        public string Id = "";
        public int StateCode;
        public string StateLabel = "";
        public string LastError = "";
        public bool IsStable => StateCode == 1;
    }

    public static class WindowsIncidentRemediator
    {
        private static readonly Encoding ConsoleEncoding;

        private static readonly Dictionary<string, string> VssServiceByWriterId =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["e8132975-6f93-4464-a53e-1050253ae220"] = "CryptSvc",
                ["a6ad56c2-b509-4e6c-bb19-49d8f43532f0"] = "Winmgmt",
                ["afbab4a2-367d-4d15-a586-71dbb18f8485"] = "VSS",
                ["542da469-d3e1-473c-9f4f-7847f01fc64f"] = "VSS",
                ["4dc3bdd4-ab48-4d07-adb0-3bee2926fd7f"] = "VSS",
                ["2a40fd15-dfca-4aa8-a654-1f8c654603f6"] = "AppHostSvc",
                ["59b1f0cf-90ef-465f-9609-6ca8b2938366"] = "IISADMIN",
                ["a65faa63-5ea8-4ebc-9dbd-a0c4db26912a"] = "SQLWriter",
            };

        static WindowsIncidentRemediator()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ConsoleEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }

        public static Task<IncidentRepairResult> DiagnoseAsync(
            IncidentRepairPlan plan,
            CancellationToken cancellationToken = default)
            => plan.Kind switch
            {
                IncidentRepairKind.VssWriters => DiagnoseVssAsync(cancellationToken),
                IncidentRepairKind.NtfsVolume => DiagnoseNtfsAsync(plan.Target, cancellationToken),
                _ => Task.FromResult(Blocked("Ce diagnostic n'a pas encore d'exécuteur vérifiable.")),
            };

        public static Task<IncidentRepairResult> RepairAsync(
            IncidentRepairPlan plan,
            CancellationToken cancellationToken = default)
            => plan.Kind switch
            {
                IncidentRepairKind.VssWriters => RepairVssAsync(plan.VerifiedTargets, cancellationToken),
                IncidentRepairKind.StorePackagesInUse => RepairStorePackagesAsync(plan.Target, cancellationToken),
                _ => Task.FromResult(Blocked("Aucune correction automatique vérifiable n'est disponible pour cet incident.")),
            };

        public static List<VssWriterState> ParseVssWriters(string output)
        {
            var writers = new List<VssWriterState>();
            VssWriterState? current = null;

            foreach (string rawLine in NormalizeLines(output))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (IsWriterNameLine(line))
                {
                    if (current != null && current.Name.Length > 0) writers.Add(current);
                    current = new VssWriterState { Name = ExtractQuoted(line) };
                    continue;
                }

                if (current == null) continue;

                if (current.Id.Length == 0 && IsWriterIdLine(line))
                {
                    current.Id = ExtractGuid(line);
                    continue;
                }

                var state = Regex.Match(line, @"\[(\d+)\]\s*(.*)$");
                if (state.Success && IsStateLine(line))
                {
                    current.StateCode = int.TryParse(state.Groups[1].Value, out int code) ? code : 0;
                    current.StateLabel = state.Groups[2].Value.Trim();
                    continue;
                }

                if (IsLastErrorLine(line))
                    current.LastError = ValueAfterColon(line);
            }

            if (current != null && current.Name.Length > 0) writers.Add(current);
            return writers;
        }

        private static async Task<IncidentRepairResult> DiagnoseVssAsync(CancellationToken cancellationToken)
        {
            var command = await RunAsync("vssadmin.exe", "list writers", TimeSpan.FromSeconds(45), cancellationToken);
            if (command.ExitCode != 0)
            {
                string reason = command.Output.Contains("privil", StringComparison.OrdinalIgnoreCase)
                    || command.Error.Contains("privil", StringComparison.OrdinalIgnoreCase)
                    ? "Le contrôle VSS exige les droits administrateur."
                    : $"VSS n'a pas pu être interrogé (code {command.ExitCode}).";
                return Blocked(reason);
            }

            var writers = ParseVssWriters(command.Output);
            if (writers.Count == 0)
                return Blocked("VSS n'a renvoyé aucun writer exploitable.");

            var failed = writers.Where(writer => !writer.IsStable).ToList();
            if (failed.Count == 0)
            {
                return new IncidentRepairResult
                {
                    Phase = IncidentRepairPhase.NotPresent,
                    Message = $"Les {writers.Count} writers VSS sont stables. L'erreur historique n'est plus présente.",
                    Evidence = { $"{writers.Count} writer(s) contrôlé(s), 0 défaillant." },
                };
            }

            var result = new IncidentRepairResult
            {
                Phase = IncidentRepairPhase.Ready,
                Message = $"{failed.Count} writer(s) VSS défaillant(s) identifié(s).",
            };

            foreach (var writer in failed)
            {
                string state = writer.StateLabel.Length > 0 ? writer.StateLabel : $"état {writer.StateCode}";
                string error = writer.LastError.Length > 0 ? $" · {writer.LastError}" : "";
                result.Evidence.Add($"{writer.Name} : {state}{error}");

                if (!VssServiceByWriterId.TryGetValue(NormalizeGuid(writer.Id), out string? service))
                {
                    result.Phase = IncidentRepairPhase.Blocked;
                    result.Message = $"Le writer « {writer.Name} » est défaillant, mais son service ne peut pas être identifié de façon sûre.";
                    result.VerifiedTargets.Clear();
                    return result;
                }
                if (!result.VerifiedTargets.Contains(service, StringComparer.OrdinalIgnoreCase))
                    result.VerifiedTargets.Add(service);
            }

            return result;
        }

        private static async Task<IncidentRepairResult> RepairVssAsync(
            IReadOnlyList<string> services,
            CancellationToken cancellationToken)
        {
            if (services.Count == 0)
                return Blocked("Aucun service VSS n'a été validé par le diagnostic.");

            var stoppedByTweakly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string service in services)
                {
                    var state = QueryService(service);
                    if (state == null)
                        return Blocked($"Le service {service} n'existe pas sur cette installation de Windows.");
                    if (state.Value.StartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                        return Blocked($"Le service {service} est désactivé. Tweakly ne modifie pas son type de démarrage sans preuve supplémentaire.");
                    if (!state.Value.State.Equals("Running", StringComparison.OrdinalIgnoreCase))
                        return Blocked($"Le service {service} était arrêté avant l'opération. Tweakly conserve cet état et refuse de le démarrer automatiquement.");

                    var stop = await RunAsync("sc.exe", $"stop \"{service}\"", TimeSpan.FromSeconds(30), cancellationToken);
                    if (stop.ExitCode != 0 && !stop.Output.Contains("SERVICE_NOT_ACTIVE", StringComparison.OrdinalIgnoreCase))
                        return Blocked($"Windows a refusé l'arrêt contrôlé du service {service}.");
                    if (!await WaitForServiceStateAsync(service, "Stopped", TimeSpan.FromSeconds(20), cancellationToken))
                        return Blocked($"Le service {service} ne s'est pas arrêté dans le délai prévu.");
                    stoppedByTweakly.Add(service);

                    var start = await RunAsync("sc.exe", $"start \"{service}\"", TimeSpan.FromSeconds(30), cancellationToken);
                    if (start.ExitCode != 0 && !start.Output.Contains("SERVICE_ALREADY_RUNNING", StringComparison.OrdinalIgnoreCase))
                        return Blocked($"Windows a refusé le redémarrage du service {service}.");
                    if (!await WaitForServiceStateAsync(service, "Running", TimeSpan.FromSeconds(20), cancellationToken))
                        return Blocked($"Le service {service} n'est pas revenu à l'état actif.");
                    stoppedByTweakly.Remove(service);
                }
            }
            finally
            {
                foreach (string service in stoppedByTweakly)
                {
                    try
                    {
                        _ = await RunAsync(
                            "sc.exe", $"start \"{service}\"", TimeSpan.FromSeconds(30), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Réparation VSS : restauration du service " + service, ex);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var verification = await DiagnoseVssAsync(cancellationToken);
            if (verification.Phase != IncidentRepairPhase.NotPresent)
            {
                verification.Phase = IncidentRepairPhase.Blocked;
                verification.Message = "La vérification VSS détecte encore un writer défaillant. L'incident n'est pas marqué comme corrigé.";
                return verification;
            }

            verification.Phase = IncidentRepairPhase.Corrected;
            verification.Message = "Les services responsables ont été relancés et tous les writers VSS sont maintenant stables.";
            verification.VerifiedTargets.AddRange(services);
            return verification;
        }

        private static async Task<IncidentRepairResult> DiagnoseNtfsAsync(
            string target,
            CancellationToken cancellationToken)
        {
            if (!Regex.IsMatch(target ?? "", @"^[A-Za-z]:$"))
                return Blocked("Le volume NTFS n'est pas identifié par une lettre de lecteur fiable.");

            string volume = (target ?? "").ToUpperInvariant();
            var command = await RunAsync("chkdsk.exe", $"{volume} /scan", TimeSpan.FromMinutes(20), cancellationToken);
            string normalized = NormalizeText(command.Output + "\n" + command.Error);

            if (command.ExitCode == 0 && ContainsAny(normalized,
                    "windows has scanned the file system and found no problems",
                    "windows a analyse le systeme de fichiers sans trouver de probleme",
                    "aucun probleme n'a ete detecte"))
            {
                return new IncidentRepairResult
                {
                    Phase = IncidentRepairPhase.NotPresent,
                    Message = $"Le contrôle complet de {volume} ne trouve plus aucune corruption NTFS.",
                    Evidence = { $"CHKDSK {volume} /scan : aucune erreur." },
                };
            }

            if (command.ExitCode is 0 or 1 && ContainsAny(normalized,
                    "windows has made corrections to the file system",
                    "windows a effectue des corrections sur le systeme de fichiers"))
            {
                var verify = await RunAsync("chkdsk.exe", $"{volume} /scan", TimeSpan.FromMinutes(20), cancellationToken);
                string verifyText = NormalizeText(verify.Output + "\n" + verify.Error);
                if (verify.ExitCode == 0 && ContainsAny(verifyText,
                        "found no problems", "sans trouver de probleme", "aucun probleme n'a ete detecte"))
                {
                    return new IncidentRepairResult
                    {
                        Phase = IncidentRepairPhase.Corrected,
                        Message = $"Windows a réparé {volume}, puis le second contrôle a confirmé que le volume est propre.",
                        Evidence = { $"Premier contrôle : corrections appliquées.", $"Second contrôle : aucune erreur sur {volume}." },
                    };
                }
            }

            return new IncidentRepairResult
            {
                Phase = IncidentRepairPhase.Blocked,
                Message = $"Le contrôle de {volume} n'a pas permis de confirmer une réparation complète. Une réparation hors ligne est nécessaire avant de marquer l'incident comme corrigé.",
                Evidence = { $"CHKDSK terminé avec le code {command.ExitCode}." },
            };
        }

        private static async Task<IncidentRepairResult> RepairStorePackagesAsync(
            string target,
            CancellationToken cancellationToken)
        {
            var packages = (target ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split('|', 2))
                .Where(parts => parts.Length > 0 && Regex.IsMatch(parts[0], "^[A-Za-z0-9]+$"))
                .Select(parts => (StoreId: parts[0], PackageName: parts.Length > 1 ? parts[1] : ""))
                .Distinct()
                .ToList();
            if (packages.Count == 0)
                return Blocked("Aucun identifiant Microsoft Store fiable n'a été extrait de l'événement.");

            string winget = FindWinget();
            if (winget.Length == 0)
                return Blocked("Windows Package Manager (winget) est introuvable sur ce PC.");

            var result = new IncidentRepairResult();
            foreach (string packageName in packages.Select(item => item.PackageName)
                         .Where(value => value.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string stopped in StopPackageProcesses(packageName))
                    result.Evidence.Add($"Processus fermé : {stopped}");
            }

            int updated = 0;
            int alreadyCurrent = 0;
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string arguments = $"upgrade --id {package.StoreId} --source msstore --accept-source-agreements --accept-package-agreements --disable-interactivity --silent";
                CommandResult command = await RunAsync(winget, arguments, TimeSpan.FromMinutes(10), cancellationToken);
                string output = NormalizeText(command.Output + "\n" + command.Error);
                if (command.ExitCode == 0)
                {
                    if (ContainsAny(output,
                            "no applicable upgrade found",
                            "aucune mise a niveau applicable",
                            "aucune mise a jour applicable"))
                    {
                        alreadyCurrent++;
                        result.Evidence.Add($"{package.StoreId} : déjà à jour.");
                    }
                    else
                    {
                        updated++;
                        result.Evidence.Add($"{package.StoreId} : mise à jour terminée.");
                    }
                    continue;
                }

                result.Phase = IncidentRepairPhase.Blocked;
                result.Message = $"La mise à jour {package.StoreId} a échoué avec le code {command.ExitCode}. L'incident n'est pas marqué comme corrigé.";
                result.Evidence.Add($"{package.StoreId} : code {command.ExitCode}.");
                return result;
            }

            result.Phase = updated > 0 ? IncidentRepairPhase.Corrected : IncidentRepairPhase.NotPresent;
            result.Message = updated > 0
                ? $"{updated} package(s) mis à jour. Chaque commande Microsoft Store s'est terminée sans erreur."
                : $"Les {alreadyCurrent} package(s) concernés sont déjà à jour ; le blocage historique n'est plus présent.";
            return result;
        }

        private static string FindWinget()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string alias = System.IO.Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            if (System.IO.File.Exists(alias)) return alias;

            try
            {
                var command = RunAsync("where.exe", "winget.exe", TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult();
                return command.ExitCode == 0
                    ? NormalizeLines(command.Output).FirstOrDefault(System.IO.File.Exists) ?? ""
                    : "";
            }
            catch { return ""; }
        }

        private static IEnumerable<string> StopPackageProcesses(string packageName)
        {
            var stopped = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, ExecutablePath FROM Win32_Process");
                using var results = searcher.Get();
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        int pid = Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture);
                        if (pid == Environment.ProcessId) continue;
                        string path = Convert.ToString(item["ExecutablePath"], CultureInfo.InvariantCulture) ?? "";
                        if (!path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)
                            || !path.Contains($"\\{packageName}_", StringComparison.OrdinalIgnoreCase)) continue;

                        string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? $"PID {pid}";
                        try
                        {
                            using Process process = Process.GetProcessById(pid);
                            if (process.CloseMainWindow() && process.WaitForExit(2500))
                            {
                                stopped.Add(name);
                                continue;
                            }
                            process.Kill(entireProcessTree: true);
                            if (process.WaitForExit(5000)) stopped.Add(name);
                        }
                        catch (ArgumentException) { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Diagnostic incident : processus AppX non lisibles — {ex.Message}");
            }
            return stopped;
        }

        private static async Task<bool> WaitForServiceStateAsync(
            string service,
            string expected,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = QueryService(service);
                if (state != null && state.Value.State.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
                await Task.Delay(500, cancellationToken);
            }
            return false;
        }

        private static (string State, string StartMode)? QueryService(string name)
        {
            try
            {
                string escaped = name.Replace("'", "''", StringComparison.Ordinal);
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT State, StartMode FROM Win32_Service WHERE Name='{escaped}'");
                using var results = searcher.Get();
                foreach (ManagementObject service in results)
                {
                    using (service)
                    {
                        return (
                            Convert.ToString(service["State"], CultureInfo.InvariantCulture) ?? "",
                            Convert.ToString(service["StartMode"], CultureInfo.InvariantCulture) ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Diagnostic incident : lecture du service {name} impossible — {ex.Message}");
            }
            return null;
        }

        private static async Task<CommandResult> RunAsync(
            string fileName,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!System.IO.Path.IsPathFullyQualified(fileName))
                fileName = WindowsSystemTools.PathFor(fileName);
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding,
            };
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return new CommandResult(-1, "", "Le processus n'a pas pu démarrer.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                string stopError = await StopProcessBoundedAsync(process);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                string output = await ReadCompletedOrEmptyAsync(stdout);
                string error = await ReadCompletedOrEmptyAsync(stderr);
                string suffix = string.IsNullOrWhiteSpace(stopError)
                    ? "Délai dépassé."
                    : $"Délai dépassé. {stopError}";
                return new CommandResult(-1, output,
                    string.IsNullOrWhiteSpace(error)
                        ? suffix
                        : error.Trim() + Environment.NewLine + suffix);
            }

            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }

        private static async Task<string> StopProcessBoundedAsync(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                return "Arrêt forcé impossible : " + ex.Message;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return "";
            }
            catch (OperationCanceledException)
            {
                return "Le processus ne s'est pas arrêté sous 5 s.";
            }
            catch (InvalidOperationException)
            {
                return "";
            }
        }

        private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> readTask)
        {
            Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));
            return completed == readTask && readTask.IsCompletedSuccessfully
                ? readTask.Result
                : "";
        }

        private static IncidentRepairResult Blocked(string message)
            => new() { Phase = IncidentRepairPhase.Blocked, Message = message };

        private static IEnumerable<string> NormalizeLines(string value)
            => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Split('\n');

        private static bool IsWriterNameLine(string line)
        {
            string normalized = NormalizeText(line);
            return normalized.Contains("writer name", StringComparison.Ordinal)
                || normalized.Contains("nom du redacteur", StringComparison.Ordinal)
                || normalized.Contains("nom de l'enregistreur", StringComparison.Ordinal);
        }

        private static bool IsWriterIdLine(string line)
        {
            string normalized = NormalizeText(line);
            return normalized.Contains("writer id", StringComparison.Ordinal)
                || normalized.Contains("id du redacteur", StringComparison.Ordinal)
                || normalized.Contains("id de l'enregistreur", StringComparison.Ordinal);
        }

        private static bool IsStateLine(string line)
        {
            string normalized = NormalizeText(line);
            return normalized.StartsWith("state", StringComparison.Ordinal)
                || normalized.StartsWith("etat", StringComparison.Ordinal);
        }

        private static bool IsLastErrorLine(string line)
        {
            string normalized = NormalizeText(line);
            return normalized.StartsWith("last error", StringComparison.Ordinal)
                || normalized.StartsWith("derniere erreur", StringComparison.Ordinal);
        }

        private static string ExtractQuoted(string line)
        {
            var match = Regex.Match(line, "['\u2018\u2019\u201C\u201D\"]([^'\u2018\u2019\u201C\u201D\"]+)['\u2018\u2019\u201C\u201D\"]");
            return match.Success ? match.Groups[1].Value.Trim() : ValueAfterColon(line);
        }

        private static string ExtractGuid(string value)
        {
            var match = Regex.Match(value, @"\{?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}?");
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string NormalizeGuid(string value) => ExtractGuid(value).ToLowerInvariant();

        private static string ValueAfterColon(string line)
        {
            int index = line.IndexOf(':');
            return index >= 0 ? line[(index + 1)..].Trim() : line.Trim();
        }

        private static string NormalizeText(string value)
        {
            string decomposed = (value ?? "").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(char.ToLowerInvariant(c));
            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        private static bool ContainsAny(string text, params string[] values)
            => values.Any(value => text.Contains(value, StringComparison.Ordinal));

        private readonly record struct CommandResult(int ExitCode, string Output, string Error);
    }
}
