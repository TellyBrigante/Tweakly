using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisation_Tool.Helpers
{
    public static class BatteryPowerPlanGuard
    {
        private const string Scheme = "SCHEME_CURRENT";
        private const string SubBattery = "SUB_BATTERY";
        private const string CriticalAction = "BATACTIONCRIT";
        private const string LowAction = "BATACTIONLOW";
        private const int DoNothing = 0;

        public sealed record Snapshot(int? DcCriticalAction, int? DcLowAction, string Error);

        public static Snapshot Read()
            => new(ReadDcIndex(SubBattery, CriticalAction, out var errCrit),
                   ReadDcIndex(SubBattery, LowAction, out var errLow),
                   FirstNonEmpty(errCrit, errLow));

        public static bool ApplyDrainSettings(out string error)
        {
            error = "";
            bool okCrit = SetDcIndex(SubBattery, CriticalAction, DoNothing, out var errCrit);
            bool okLow = SetDcIndex(SubBattery, LowAction, DoNothing, out var errLow);
            bool okActive = RunPowerCfg($"/setactive {Scheme}", out var errActive);
            error = FirstNonEmpty(errCrit, errLow, errActive);
            return okCrit && okLow && okActive;
        }

        public static bool RestoreDrainSettings(int? dcCriticalAction, int? dcLowAction, out string error)
        {
            error = "";
            bool ok = true;
            string errCrit = "";
            string errLow = "";

            if (dcCriticalAction.HasValue)
                ok &= SetDcIndex(SubBattery, CriticalAction, dcCriticalAction.Value, out errCrit);

            if (dcLowAction.HasValue)
                ok &= SetDcIndex(SubBattery, LowAction, dcLowAction.Value, out errLow);

            ok &= RunPowerCfg($"/setactive {Scheme}", out var errActive);
            error = FirstNonEmpty(errCrit, errLow, errActive);
            return ok;
        }

        private static int? ReadDcIndex(string subgroup, string setting, out string error)
        {
            error = "";
            if (!RunPowerCfg($"/query {Scheme} {subgroup} {setting}", out var commandError, out var output))
            {
                error = commandError;
                return null;
            }

            var matches = Regex.Matches(output, @"0x([0-9a-fA-F]+)");
            if (matches.Count == 0)
            {
                error = $"powercfg : valeur DC introuvable pour {setting}.";
                return null;
            }

            var hex = matches[^1].Groups[1].Value;
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return value;

            error = $"powercfg : valeur DC illisible pour {setting}.";
            return null;
        }

        private static bool SetDcIndex(string subgroup, string setting, int value, out string error)
            => RunPowerCfg($"/setdcvalueindex {Scheme} {subgroup} {setting} {value}", out error);

        private static bool RunPowerCfg(string arguments, out string error)
            => RunPowerCfg(arguments, out error, out _);

        private static bool RunPowerCfg(string arguments, out string error, out string output)
        {
            error = "";
            output = "";

            ProcessCommandResult result = ProcessCommand.Run("powercfg", arguments, 15_000);
            output = result.Output;
            if (result.Success) return true;

            string detail = !string.IsNullOrWhiteSpace(result.Error)
                ? result.Error
                : $"code {result.ExitCode}";
            error = $"powercfg : {detail}";
            return false;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }
    }
}
