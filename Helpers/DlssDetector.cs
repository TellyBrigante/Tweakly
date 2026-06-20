using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Détection des DLL DLSS dans le dossier du jeu, façon DLSS Swapper :
    ///   - Version UTILISÉE = la DLL active (« nvngx_dlss.dll ») = ce que le jeu charge.
    ///   - Version DU JEU   = la sauvegarde de l'ORIGINALE posée par DLSS Swapper au moment du
    ///                        swap (extensions .dlsss, .bak, .original, .old…). Si pas de
    ///                        sauvegarde trouvée → aucun swap fait → version du jeu = utilisée.
    ///
    /// Recherche bornée en profondeur (BFS profondeur 5) pour ne pas scanner tout un dossier
    /// Steam Library. Best-effort, jamais d'exception qui remonte.
    /// </summary>
    public static class DlssDetector
    {
        // (DLL active, libellé affiché)
        private static readonly (string activeFile, string name)[] DllMap =
        {
            ("nvngx_dlss.dll",  "DLSS Super Resolution"),
            ("nvngx_dlssg.dll", "DLSS Frame Generation"),
            ("nvngx_dlssd.dll", "DLSS Ray Reconstruction"),
        };

        // Extensions de sauvegarde reconnues (créées par DLSS Swapper / swap manuel).
        // ⚠️ « .dlsss » est la convention officielle de DLSS Swapper (sauvegarde de la DLL
        // originale au moment du swap). Les autres sont des conventions humaines fréquentes.
        private static readonly string[] BackupSuffixes =
        {
            ".dlsss", ".bak", ".original", ".orig", ".old",
        };

        public enum DlssStatus { Detected, NotPresent, UnknownPath, Error }

        public sealed class DlssComponent
        {
            public string Name = "";            // libellé (ex. « DLSS Super Resolution »)
            public string ActivePath = "";      // chemin de la DLL active (utilisée)
            public string ActiveVersion = "";   // FileVersion de la DLL active
            public string OriginalPath = "";    // chemin de la sauvegarde (vide si aucune)
            public string OriginalVersion = ""; // FileVersion de la sauvegarde
            public bool Swapped => OriginalPath.Length > 0
                                  && !string.Equals(ActiveVersion, OriginalVersion, StringComparison.OrdinalIgnoreCase);
        }

        public sealed class DlssInfo
        {
            public DlssStatus Status;
            public List<DlssComponent> Components = new();
        }

        public static DlssInfo Detect(string? gameExePath)
        {
            var info = new DlssInfo();
            if (string.IsNullOrWhiteSpace(gameExePath) || !File.Exists(gameExePath))
            {
                info.Status = DlssStatus.UnknownPath;
                return info;
            }
            string? gameDir = Path.GetDirectoryName(gameExePath);
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                info.Status = DlssStatus.UnknownPath;
                return info;
            }

            try
            {
                foreach (var (active, displayName) in DllMap)
                {
                    string? activeHit = FirstMatch(gameDir, active, maxDepth: 5);
                    if (activeHit == null) continue;

                    var c = new DlssComponent
                    {
                        Name = displayName,
                        ActivePath = activeHit,
                        ActiveVersion = ReadFileVersion(activeHit),
                    };

                    // Cherche une sauvegarde dans le MÊME dossier que la DLL active.
                    // Convention DLSS Swapper : « <DLL>.<suffix> » (ex. nvngx_dlss.dll.dlsss).
                    string? dir = Path.GetDirectoryName(activeHit);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        foreach (var suf in BackupSuffixes)
                        {
                            string candidate = Path.Combine(dir, active + suf);
                            if (File.Exists(candidate))
                            {
                                c.OriginalPath = candidate;
                                c.OriginalVersion = ReadFileVersion(candidate);
                                break;
                            }
                        }
                    }

                    info.Components.Add(c);
                }
            }
            catch
            {
                info.Status = DlssStatus.Error;
                return info;
            }

            info.Status = info.Components.Count > 0 ? DlssStatus.Detected : DlssStatus.NotPresent;
            return info;
        }

        // BFS bornée : limite la profondeur de récursion et s'arrête au 1er fichier trouvé.
        private static string? FirstMatch(string root, string fileName, int maxDepth)
        {
            var queue = new Queue<(string path, int depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                try
                {
                    string candidate = Path.Combine(current, fileName);
                    if (File.Exists(candidate)) return candidate;
                    if (depth < maxDepth)
                    {
                        foreach (var dir in Directory.EnumerateDirectories(current))
                            queue.Enqueue((dir, depth + 1));
                    }
                }
                catch { /* dossier inaccessible, on continue */ }
            }
            return null;
        }

        // FileVersion d'une DLL NGX (ex. « 3.7.20.0 »). Vide si lecture impossible.
        private static string ReadFileVersion(string path)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                var v = (fvi.FileVersion ?? "").Trim();
                if (v.Length > 0) return v;
                if (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0)
                    return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}";
                return "";
            }
            catch { return ""; }
        }
    }
}
