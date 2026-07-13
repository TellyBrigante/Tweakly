using System;
using System.Globalization;
using System.Text;

namespace Optimisation_Tool.Helpers
{
    public enum DismHealthState { Unknown, Clean, Repairable, NonRepairable, Repaired }

    public static class DismOutputClassifier
    {
        public static DismHealthState Parse(string output)
        {
            string raw = Normalize(output);
            if (ContainsAny(raw,
                "no component store corruption detected",
                "component store corruption was not detected",
                "aucune corruption du magasin",
                "aucun endommagement"))
                return DismHealthState.Clean;

            if (ContainsAny(raw,
                "the component store cannot be repaired",
                "the component store is not repairable",
                "magasin de composants ne peut pas etre repare",
                "magasin de composants n'est pas reparable"))
                return DismHealthState.NonRepairable;

            if (ContainsAny(raw,
                "the component store is repairable",
                "component store corruption detected",
                "magasin de composants est reparable",
                "corruption reparable"))
                return DismHealthState.Repairable;

            if (ContainsAny(raw,
                "the restore operation completed successfully",
                "restore operation completed successfully",
                "restauration a ete effectuee"))
                return DismHealthState.Repaired;

            return DismHealthState.Unknown;
        }

        public static bool NeedsRestoreHealth(int exitCode, string stdOut, string stdErr)
        {
            return exitCode != 0 || Parse(stdOut + "\n" + stdErr) != DismHealthState.Clean;
        }

        private static string Normalize(string text)
        {
            var normalized = (text ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant();
            var result = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    result.Append(c);
            }
            return result.ToString();
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
