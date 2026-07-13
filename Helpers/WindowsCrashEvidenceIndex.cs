using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Optimisation_Tool.Helpers
{
    public sealed class WindowsCrashEvidence
    {
        public DateTime Time;
        public string AppName = "";
        public string AppPath = "";
        public string ModuleName = "";
        public string ExceptionCode = "";
        public string ReportPath = "";
        public string DumpPath = "";

        public string Describe()
        {
            var parts = new List<string>();
            if (AppName.Length > 0) parts.Add(AppName);
            if (ModuleName.Length > 0) parts.Add("module " + ModuleName);
            if (ExceptionCode.Length > 0) parts.Add("code " + ExceptionCode);
            string file = ReportPath.Length > 0 ? ReportPath : DumpPath;
            if (file.Length > 0) parts.Add(ShortPath(file));
            return string.Join(" | ", parts);
        }

        private static string ShortPath(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                return "%USERPROFILE%" + path.Substring(home.Length);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (path.StartsWith(programData, StringComparison.OrdinalIgnoreCase))
                return "%PROGRAMDATA%" + path.Substring(programData.Length);
            return path;
        }
    }

    public static class WindowsCrashEvidenceIndex
    {
        public sealed class Snapshot
        {
            internal readonly List<WindowsCrashEvidence> Entries;
            internal Snapshot(List<WindowsCrashEvidence> entries) => Entries = entries;

            public IReadOnlyList<WindowsCrashEvidence> Find(DateTime start, DateTime end, string appName = "")
            {
                if (end < start) end = start;
                DateTime from = start.AddMinutes(-5);
                DateTime to = end.AddMinutes(5);
                string wanted = NormalizeApp(appName);

                var near = Entries.Where(e => e.Time >= from && e.Time <= to).ToList();
                if (wanted.Length > 0)
                {
                    var sameApp = near.Where(e => NormalizeApp(e.AppName) == wanted).ToList();
                    near = sameApp;
                }

                return near
                    .OrderBy(e => DistanceSeconds(e.Time, start, end))
                    .ThenByDescending(e => e.ModuleName.Length > 0)
                    .Take(6)
                    .ToList();
            }
        }

        public static Snapshot Create(DateTime start, DateTime end)
        {
            if (end < start) end = start;
            DateTime from = start.AddMinutes(-10);
            DateTime to = end.AddMinutes(10);
            var entries = new List<WindowsCrashEvidence>();

            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            AddWer(entries, Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"), from, to);
            AddWer(entries, Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"), from, to);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddWer(entries, Path.Combine(local, "Microsoft", "Windows", "WER"), from, to);
            AddDumps(entries, Path.Combine(local, "CrashDumps"), from, to);

            var indexed = entries
                .GroupBy(e => EvidenceKey(e), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(e => e.Time)
                .ToList();
            int werCount = indexed.Count(e => e.ReportPath.Length > 0);
            int dumpCount = indexed.Count(e => e.DumpPath.Length > 0);
            AppLog.Write($"Erreurs Windows : preuves locales indexées — {werCount} rapport(s) WER, {dumpCount} dump(s).");
            return new Snapshot(indexed);
        }

        private static void AddWer(List<WindowsCrashEvidence> output, string root, DateTime from, DateTime to)
        {
            if (!Directory.Exists(root)) return;
            int scanned = 0;
            foreach (string path in EnumerateFilesLimited(root, maxDepth: 3))
            {
                if (++scanned > 2000) break;
                if (!path.EndsWith(".wer", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime time = SafeLastWrite(path);
                if (time < from || time > to) continue;

                var values = ReadWer(path);
                string app = SignatureValue(values,
                    "application name",
                    "nom de l'application",
                    "nom de l’application");
                if (app.Length == 0) app = First(values, "AppName", "ApplicationName");
                string appPath = First(values, "AppPath", "ApplicationPath");
                if (app.Length == 0 && appPath.Length > 0) app = Path.GetFileName(appPath);

                output.Add(new WindowsCrashEvidence
                {
                    Time = time,
                    AppName = app,
                    AppPath = appPath,
                    ModuleName = SignatureValue(values,
                        "fault module name",
                        "nom du module defaillant",
                        "nom du module défaillant"),
                    ExceptionCode = SignatureValue(values,
                        "exception code",
                        "code exception",
                        "code de l'exception",
                        "code de l’exception",
                        "code d'exception"),
                    ReportPath = path,
                });
            }
        }

        private static void AddDumps(List<WindowsCrashEvidence> output, string root, DateTime from, DateTime to)
        {
            if (!Directory.Exists(root)) return;
            foreach (string path in SafeFiles(root)
                .Where(p => p.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(SafeLastWrite)
                .Take(500))
            {
                DateTime time = SafeLastWrite(path);
                if (time < from || time > to) continue;
                output.Add(new WindowsCrashEvidence
                {
                    Time = time,
                    AppName = AppFromDumpName(path),
                    DumpPath = path,
                });
            }
        }

        private static Dictionary<string, string> ReadWer(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadLines(path).Take(1000))
                {
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    if (key.Length == 0 || values.ContainsKey(key)) continue;
                    values[key] = line.Substring(equals + 1).Trim();
                }
            }
            catch { }
            return values;
        }

        private static string SignatureValue(Dictionary<string, string> values, params string[] labels)
        {
            foreach (var pair in values)
            {
                if (!pair.Key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase)) continue;
                if (!labels.Any(label => pair.Value.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                string valueKey = pair.Key.Substring(0, pair.Key.Length - 5) + ".Value";
                if (values.TryGetValue(valueKey, out string? value)) return value;
            }
            return "";
        }

        private static string First(Dictionary<string, string> values, params string[] keys)
        {
            foreach (string key in keys)
                if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            return "";
        }

        private static IEnumerable<string> EnumerateFilesLimited(string root, int maxDepth)
        {
            var stack = new Stack<(string path, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (path, depth) = stack.Pop();
                foreach (string file in SafeFiles(path)) yield return file;
                if (depth >= maxDepth) continue;
                foreach (string dir in SafeDirectories(path)) stack.Push((dir, depth + 1));
            }
        }

        private static IEnumerable<string> SafeFiles(string path)
        {
            try { return Directory.EnumerateFiles(path).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        private static string AppFromDumpName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int exe = name.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0) return name.Substring(0, exe + 4);
            int dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static string NormalizeApp(string app)
        {
            string name = Path.GetFileName(app ?? "").Trim();
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4).ToLowerInvariant()
                : name.ToLowerInvariant();
        }

        private static double DistanceSeconds(DateTime value, DateTime start, DateTime end)
        {
            if (value >= start && value <= end) return 0;
            return Math.Min(Math.Abs((value - start).TotalSeconds), Math.Abs((value - end).TotalSeconds));
        }

        private static string EvidenceKey(WindowsCrashEvidence evidence)
            => $"{evidence.Time:O}|{evidence.ReportPath}|{evidence.DumpPath}";
    }
}
