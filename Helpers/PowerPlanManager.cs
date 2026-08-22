using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public static class PowerPlanManager
    {
        private static readonly Guid UltimateTemplate =
            Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");
        private static readonly Guid Balanced =
            Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        private const string TweaklySchemeName = "Tweakly - Performances ultimes";
        private const string GuidPattern =
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

        public static bool IsUltimateActive()
            => TryReadUltimateState(out bool active, out _) && active;

        public static bool TryReadActivePlan(out string name, out string guid, out string error)
        {
            name = "";
            guid = "";
            CommandResult current = RunPowerCfg("/getactivescheme");
            if (!current.Success || !TryExtractGuid(current.Output, out Guid activeGuid))
            {
                error = CurrentError("Lecture du plan actif impossible", current);
                return false;
            }

            PowerScheme? active = ParseSchemes(current.Output)
                .FirstOrDefault(item => item.Id == activeGuid);
            name = active?.Name ?? "";
            guid = activeGuid.ToString("D");
            error = "";
            return true;
        }

        public static bool TryReadUltimateState(out bool active, out string error)
        {
            active = false;
            if (!TryReadActiveGuid(out Guid activeGuid, out error))
                return false;

            if (activeGuid == UltimateTemplate)
            {
                active = true;
                return true;
            }

            CommandResult listed = RunPowerCfg("/list");
            if (!listed.Success)
            {
                error = CurrentError("Lecture des plans d'alimentation impossible", listed);
                return false;
            }

            PowerScheme? scheme = ParseSchemes(listed.Output)
                .FirstOrDefault(item => item.Id == activeGuid);
            active = scheme != null && IsUltimateSchemeName(scheme.Name);
            return true;
        }

        public static bool TrySetUltimate(bool enabled, out string message)
        {
            if (!enabled)
                return ActivateAndVerify(Balanced, false, "Mode Utilisation normale restauré.", out message);

            if (!TryReadActiveGuid(out Guid originalActiveGuid, out message))
                return false;

            CommandResult listed = RunPowerCfg("/list");
            if (!listed.Success)
            {
                message = CurrentError("Performances ultimes : liste des plans inaccessible", listed);
                return false;
            }

            List<PowerScheme> before = ParseSchemes(listed.Output);
            PowerScheme? activeScheme = before.FirstOrDefault(item => item.Id == originalActiveGuid);
            if (originalActiveGuid == UltimateTemplate ||
                (activeScheme != null && IsUltimateSchemeName(activeScheme.Name)))
            {
                message = "Mode Performances ultimes activé (déjà actif).";
                return true;
            }

            PowerScheme? target = before
                .Where(item => IsUltimateSchemeName(item.Name) || item.Id == UltimateTemplate)
                .OrderByDescending(item => item.Name.Equals(TweaklySchemeName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item =>
                    item.Name.Contains("ultim", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            Guid? createdScheme = null;

            if (target == null)
            {
                CommandResult duplicate = RunPowerCfg($"/duplicatescheme {UltimateTemplate:D}");
                if (!duplicate.Success)
                {
                    message = CurrentError("Performances ultimes : création du plan impossible", duplicate);
                    return false;
                }

                if (TryExtractGuid(duplicate.Output, out Guid duplicatedGuid))
                {
                    target = new PowerScheme(duplicatedGuid, "");
                }
                else
                {
                    CommandResult afterResult = RunPowerCfg("/list");
                    if (!afterResult.Success)
                    {
                        message = CurrentError("Performances ultimes : vérification du plan créé impossible", afterResult);
                        return false;
                    }

                    var previousIds = before.Select(item => item.Id).ToHashSet();
                    target = ParseSchemes(afterResult.Output).FirstOrDefault(item => !previousIds.Contains(item.Id));
                }
                if (target == null)
                {
                    message = "Performances ultimes : powercfg n'a créé aucun nouveau plan identifiable.";
                    return false;
                }

                createdScheme = target.Id;
                CommandResult rename = RunPowerCfg($"/changename {target.Id:D} \"{TweaklySchemeName}\"");
                if (!rename.Success)
                {
                    message = CurrentError("Performances ultimes : renommage du plan impossible", rename);
                    AppendCreatedSchemeCleanup(createdScheme.Value, originalActiveGuid, ref message);
                    return false;
                }

                target = target with { Name = TweaklySchemeName };
            }

            bool activated = ActivateAndVerify(
                target.Id,
                true,
                "Mode Performances ultimes activé.",
                out message);
            if (!activated && createdScheme.HasValue)
                AppendCreatedSchemeCleanup(createdScheme.Value, originalActiveGuid, ref message);
            return activated;
        }

        public static bool IsUltimateSchemeName(string name)
        {
            string normalized = name.Trim().ToLowerInvariant();
            return normalized.Equals(TweaklySchemeName.ToLowerInvariant(), StringComparison.Ordinal) ||
                   normalized.Contains("performances ultimes", StringComparison.Ordinal) ||
                   normalized.Contains("performances optimales", StringComparison.Ordinal) ||
                   normalized.Contains("ultimate performance", StringComparison.Ordinal);
        }

        private static bool TryReadActiveGuid(out Guid activeGuid, out string error)
        {
            activeGuid = Guid.Empty;
            CommandResult current = RunPowerCfg("/getactivescheme");
            if (!current.Success || !TryExtractGuid(current.Output, out activeGuid))
            {
                error = CurrentError("Lecture du plan actif impossible", current);
                return false;
            }

            error = "";
            return true;
        }

        private static void AppendCreatedSchemeCleanup(
            Guid createdScheme,
            Guid originalActiveGuid,
            ref string message)
        {
            CommandResult restore = RunPowerCfg($"/setactive {originalActiveGuid:D}");
            CommandResult delete = RunPowerCfg($"/delete {createdScheme:D}");
            if (restore.Success && delete.Success)
            {
                message += " Le plan incomplet a été supprimé.";
                return;
            }

            string restoreError = restore.Success ? "" : CurrentError("restauration du plan initial impossible", restore);
            string deleteError = delete.Success ? "" : CurrentError("suppression du plan incomplet impossible", delete);
            message += " Nettoyage incomplet : " +
                       string.Join(" | ", new[] { restoreError, deleteError }.Where(value => value.Length > 0));
        }

        private static bool ActivateAndVerify(Guid scheme, bool expectedUltimate,
                                              string successMessage, out string message)
        {
            CommandResult activation = RunPowerCfg($"/setactive {scheme:D}");
            if (!activation.Success)
            {
                message = CurrentError("Activation du plan d'alimentation impossible", activation);
                return false;
            }

            CommandResult current = RunPowerCfg("/getactivescheme");
            if (!current.Success || !TryExtractGuid(current.Output, out Guid activeGuid))
            {
                message = CurrentError("Plan appliqué, mais sa vérification est impossible", current);
                return false;
            }
            if (activeGuid != scheme)
            {
                message = $"Plan d'alimentation non appliqué : Windows utilise encore {activeGuid:D}.";
                return false;
            }

            if (!TryReadUltimateState(out bool actualUltimate, out string stateError))
            {
                message = stateError;
                return false;
            }
            if (actualUltimate != expectedUltimate)
            {
                message = "Plan actif vérifié, mais son type ne correspond pas à l'état demandé.";
                return false;
            }

            message = successMessage;
            return true;
        }

        private static List<PowerScheme> ParseSchemes(string output)
        {
            var result = new List<PowerScheme>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match guidMatch = Regex.Match(line, GuidPattern, RegexOptions.IgnoreCase);
                if (!guidMatch.Success || !Guid.TryParse(guidMatch.Value, out Guid id)) continue;

                int open = line.IndexOf('(', guidMatch.Index + guidMatch.Length);
                int close = open >= 0 ? line.LastIndexOf(')') : -1;
                string name = open >= 0 && close > open ? line[(open + 1)..close].Trim() : "";
                result.Add(new PowerScheme(id, name));
            }
            return result;
        }

        private static bool TryExtractGuid(string value, out Guid guid)
        {
            Match match = Regex.Match(value, GuidPattern, RegexOptions.IgnoreCase);
            return Guid.TryParse(match.Success ? match.Value : "", out guid);
        }

        private static CommandResult RunPowerCfg(string arguments)
        {
            ProcessCommandResult result = ProcessCommand.Run(WindowsSystemTools.PathFor("powercfg.exe"), arguments, 15_000);
            return new CommandResult(result.Success, result.Output, result.Error, result.ExitCode);
        }

        private static string CurrentError(string context, CommandResult result)
        {
            string detail = !string.IsNullOrWhiteSpace(result.Error)
                ? result.Error
                : !string.IsNullOrWhiteSpace(result.Output)
                    ? result.Output.Trim()
                    : $"code {result.ExitCode}";
            return $"{context} — {detail}";
        }

        private sealed record PowerScheme(Guid Id, string Name);
        private sealed record CommandResult(bool Success, string Output, string Error, int ExitCode);
    }
}
