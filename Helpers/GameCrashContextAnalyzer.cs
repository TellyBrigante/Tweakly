using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Optimisation_Tool.Helpers
{
    public sealed class GameCrashContext
    {
        public string AppName = "";
        public string Confidence = "";
        public List<string> Evidence = new();
        public List<string> Facts = new();

        public bool HasEvidence => AppName.Length > 0 && Evidence.Count > 0;
    }

    /// <summary>
    /// Enquete locale autour d'un crash graphique : cherche quel jeu/app a laisse des
    /// traces au meme moment, puis lit les configs/logs connus. Aucune source web, aucune
    /// ecriture disque, aucune conclusion inventee.
    /// </summary>
    public static class GameCrashContextAnalyzer
    {
        private sealed class Candidate
        {
            public string App = "";
            public string Path = "";
            public DateTime Time;
            public int Score;
        }

        private static readonly HashSet<string> InterestingExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".log", ".txt", ".ini", ".cfg", ".json", ".xml"
        };

        public static GameCrashContext Analyze(DateTime start, DateTime end)
        {
            var ctx = new GameCrashContext();
            if (end < start) end = start;

            DateTime from = start.AddMinutes(-5);
            DateTime to = end.AddMinutes(5);
            var candidates = new List<Candidate>();

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string myGames = Path.Combine(docs, "My Games");
            AddCandidates(candidates, myGames, from, to, rootMode: "MyGames", maxDepth: 4, maxFiles: 3000);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddCandidatesFromRecentTopDirs(candidates, local, from, to, maxDepth: 4, maxFilesPerDir: 600);

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddCandidatesFromRecentTopDirs(candidates, roaming, from, to, maxDepth: 3, maxFilesPerDir: 400);

            if (candidates.Count == 0) return ctx;

            var best = candidates
                .GroupBy(c => c.App, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    App = g.Key,
                    Score = g.Sum(x => x.Score),
                    Files = g.OrderByDescending(x => x.Score).ThenByDescending(x => x.Time).Take(5).ToList(),
                    Count = g.Count()
                })
                .OrderByDescending(g => g.Score)
                .ThenByDescending(g => g.Count)
                .FirstOrDefault();
            if (best == null || best.Score < 80) return ctx;

            ctx.AppName = best.App;
            ctx.Confidence = best.Score >= 180 ? "élevée" : best.Score >= 120 ? "moyenne" : "faible";

            foreach (var c in best.Files.Take(3))
            {
                int delta = (int)Math.Round(Math.Abs((c.Time - start).TotalSeconds));
                ctx.Evidence.Add($"{ShortPath(c.Path)} modifié à {c.Time:HH:mm:ss} ({delta} s du début de l'incident)");
            }

            AddKnownFacts(ctx, best.Files.Select(f => f.Path).ToList(), from, to);
            return ctx;
        }

        private static void AddCandidatesFromRecentTopDirs(
            List<Candidate> output, string root, DateTime from, DateTime to, int maxDepth, int maxFilesPerDir)
        {
            if (!Directory.Exists(root)) return;
            int scannedDirs = 0;
            foreach (var dir in SafeDirs(root).OrderByDescending(SafeLastWrite).Take(80))
            {
                if (++scannedDirs > 80) break;
                string name = Path.GetFileName(dir);
                if (SkipTopDir(name)) continue;

                DateTime dtime = SafeLastWrite(dir);
                bool near = dtime >= from.AddHours(-6) && dtime <= to.AddHours(6);
                bool knownGame = LooksLikeGameName(name);
                if (!near && !knownGame) continue;

                AddCandidates(output, dir, from, to, rootMode: "TopDir", maxDepth: maxDepth, maxFiles: maxFilesPerDir);
            }
        }

        private static void AddCandidates(
            List<Candidate> output, string root, DateTime from, DateTime to,
            string rootMode, int maxDepth, int maxFiles)
        {
            if (!Directory.Exists(root)) return;
            int count = 0;
            foreach (var file in EnumerateFilesLimited(root, maxDepth))
            {
                if (++count > maxFiles) break;
                string ext = Path.GetExtension(file);
                if (!InterestingExt.Contains(ext)) continue;

                DateTime t = SafeLastWrite(file);
                if (t < from.AddMinutes(-30) || t > to.AddMinutes(30)) continue;

                string app = GuessAppName(root, file, rootMode);
                if (app.Length == 0 || IsNoiseApp(app)) continue;

                int score = 0;
                if (t >= from && t <= to) score += 100;
                else score += 45;
                if (rootMode == "MyGames") score += 50;
                if (LooksLikeGameName(app)) score += 35;
                if (LooksLikeLogOrConfig(file)) score += 25;
                if (Path.GetFileName(file).Contains("crash", StringComparison.OrdinalIgnoreCase)) score += 30;

                output.Add(new Candidate { App = CleanAppName(app), Path = file, Time = t, Score = score });
            }
        }

        private static void AddKnownFacts(GameCrashContext ctx, List<string> files, DateTime from, DateTime to)
        {
            string poe2Cfg = files.FirstOrDefault(p =>
                p.EndsWith("poe2_production_Config.ini", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (poe2Cfg.Length == 0)
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string p = Path.Combine(docs, "My Games", "Path of Exile 2", "poe2_production_Config.ini");
                if (File.Exists(p) && SafeLastWrite(p) >= from.AddMinutes(-30) && SafeLastWrite(p) <= to.AddMinutes(30))
                    poe2Cfg = p;
            }

            if (poe2Cfg.Length > 0)
            {
                var keys = ReadIniKeys(poe2Cfg, new[]
                {
                    "renderer_type", "engine_multithreading_mode", "reflex_mode",
                    "fullscreen", "borderless_windowed_fullscreen", "adapter_name"
                });
                if (keys.Count > 0)
                {
                    ctx.AppName = "Path of Exile 2";
                    foreach (var kv in keys)
                        ctx.Facts.Add($"{kv.Key}={kv.Value}");
                }
            }

            if (files.Any(p => p.IndexOf(@"\Saved\Logs\", StringComparison.OrdinalIgnoreCase) >= 0))
                ctx.Facts.Add("log Unreal Engine modifie dans Saved\\Logs");
            if (files.Any(p => Path.GetFileName(p).Equals("Player.log", StringComparison.OrdinalIgnoreCase)))
                ctx.Facts.Add("log Unity Player.log modifie");
        }

        private static Dictionary<string, string> ReadIniKeys(string path, IEnumerable<string> keys)
        {
            var want = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!want.Contains(k) || found.ContainsKey(k)) continue;
                    found[k] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return found;
        }

        private static IEnumerable<string> EnumerateFilesLimited(string root, int maxDepth)
        {
            var stack = new Stack<(string dir, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                foreach (var f in SafeFiles(dir)) yield return f;
                if (depth >= maxDepth) continue;
                foreach (var d in SafeDirs(dir))
                {
                    string name = Path.GetFileName(d);
                    if (SkipTopDir(name)) continue;
                    stack.Push((d, depth + 1));
                }
            }
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.EnumerateDirectories(dir).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        private static string GuessAppName(string root, string file, string rootMode)
        {
            try
            {
                string rel = Path.GetRelativePath(root, file);
                string first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  .FirstOrDefault() ?? "";
                if (rootMode == "TopDir") return Path.GetFileName(root);
                return first;
            }
            catch { return ""; }
        }

        private static bool LooksLikeLogOrConfig(string path)
        {
            string name = Path.GetFileName(path);
            return name.Contains("log", StringComparison.OrdinalIgnoreCase)
                || name.Contains("config", StringComparison.OrdinalIgnoreCase)
                || name.Contains("settings", StringComparison.OrdinalIgnoreCase)
                || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeGameName(string name)
        {
            return name.Contains("game", StringComparison.OrdinalIgnoreCase)
                || name.Contains("steam", StringComparison.OrdinalIgnoreCase)
                || name.Contains("unreal", StringComparison.OrdinalIgnoreCase)
                || name.Contains("unity", StringComparison.OrdinalIgnoreCase)
                || name.Contains("exile", StringComparison.OrdinalIgnoreCase)
                || name.Contains("valorant", StringComparison.OrdinalIgnoreCase)
                || name.Contains("fortnite", StringComparison.OrdinalIgnoreCase)
                || name.Contains("apex", StringComparison.OrdinalIgnoreCase)
                || GameDatabase.Games.Any(g =>
                    name.IndexOf(Path.GetFileNameWithoutExtension(g.Exe), StringComparison.OrdinalIgnoreCase) >= 0
                 || name.IndexOf(g.Display, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool SkipTopDir(string name)
        {
            return name.StartsWith(".", StringComparison.Ordinal)
                || name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Packages", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase)
                || name.Equals("D3DSCache", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Google", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Mozilla", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNoiseApp(string app)
        {
            return app.Length < 3
                || app.Equals("ETrade", StringComparison.OrdinalIgnoreCase)
                || app.Equals("EShop", StringComparison.OrdinalIgnoreCase)
                || app.Equals("OnlineFilters", StringComparison.OrdinalIgnoreCase)
                || app.Equals("BuildPlanner", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanAppName(string app)
            => app.Replace("_", " ").Trim();

        private static string ShortPath(string path)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                return "%USERPROFILE%" + path.Substring(home.Length);
            return path;
        }
    }
}
