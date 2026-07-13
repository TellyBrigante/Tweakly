using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Optimisation_Tool.Pages
{
    public partial class PageSpecs
    {
        private static readonly string[] FirmwareTrackedKeys =
        {
            "Carte mère|BIOS",
            "Démarrage|Mode firmware",
            "Démarrage|Secure Boot",
            "Sécurité|TPM",
            "CPU|Virtualisation firmware",
            "Windows|Hyperviseur actif",
            "Windows|VBS / Device Guard",
            "Windows|Intégrité mémoire (HVCI)",
            "Mémoire|Fréquence RAM actuelle",
            "Mémoire|Profil mémoire",
        };

        private static Helpers.FirmwareSnapshot CreateFirmwareSnapshot(List<FirmwareSettingItem> items)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                values[SnapshotKey(item)] = item.Value;

            return new Helpers.FirmwareSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Values = values,
            };
        }

        private void RenderFirmwareChanges(
            Helpers.FirmwareSnapshot? previous,
            Helpers.FirmwareSnapshot current,
            List<FirmwareSettingItem> items)
        {
            if (previous == null)
            {
                BiosChangeList.ItemsSource = Array.Empty<FirmwareChangeItem>();
                TxtBiosChangesTitle.Text = "Référence créée";
                TxtBiosChangesSub.Text = "La prochaine lecture affichera les changements BIOS/firmware détectés depuis cette base.";
                return;
            }

            var roleByKey = items.ToDictionary(SnapshotKey, item => item.Role, StringComparer.OrdinalIgnoreCase);
            var changes = new List<FirmwareChangeItem>();

            foreach (string key in FirmwareTrackedKeys)
            {
                previous.Values.TryGetValue(key, out string? before);
                current.Values.TryGetValue(key, out string? after);
                before = CleanSnapshotValue(before);
                after = CleanSnapshotValue(after);
                if (before.Length == 0 || after.Length == 0) continue;
                if (before.Equals(after, StringComparison.OrdinalIgnoreCase)) continue;

                string role = roleByKey.TryGetValue(key, out var value) ? value : "ThAccentIcon";
                changes.Add(new FirmwareChangeItem
                {
                    Title = SnapshotTitle(key),
                    Before = before,
                    After = after,
                    Role = role,
                });
            }

            BiosChangeList.ItemsSource = changes;
            string previousLocal = previous.CapturedAtUtc.ToLocalTime()
                .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            TxtBiosChangesTitle.Text = changes.Count == 0
                ? "Aucun changement détecté"
                : $"{changes.Count} changement(s) détecté(s)";
            TxtBiosChangesSub.Text = $"Comparé à la lecture du {previousLocal}.";
        }

        private static string SnapshotKey(FirmwareSettingItem item) => $"{item.Group}|{item.Title}";

        private static string SnapshotTitle(string key)
        {
            int separator = key.IndexOf('|');
            return separator >= 0 && separator + 1 < key.Length ? key[(separator + 1)..] : key;
        }

        private static string CleanSnapshotValue(string? value)
            => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        private static bool StartsActive(string value)
            => value.StartsWith("Activé", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Activée", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Actif", StringComparison.OrdinalIgnoreCase);

        private static string Shorten(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars) return value;
            return value[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";
        }
    }
}
