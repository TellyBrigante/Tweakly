using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Enregistreur de session de jeu — pilote PresentMon (data\tools\PresentMon.exe,
    /// console Intel, MIT) qui capture par ETW les frames RÉELLES présentées par le jeu :
    /// frametime, part CPU/GPU de chaque frame, mode de présentation, latence input→photon.
    ///
    /// ⚠ LEÇON CARDINALE (2026-06-13) : ne JAMAIS rien faire de lourd pendant la capture.
    /// Une version précédente lisait LibreHardwareMonitor (IOCTL) toutes les 10 s et
    /// énumérait tous les process (Process.GetProcesses ≈ 14 ms) toutes les 2 s : ces
    /// hitches du sampler PROVOQUAIENT des drops dans le jeu (une frame jetée toutes les
    /// 2 s sur un jeu à 320 fps), et l'analyse « détectait » ensuite ces drops comme un
    /// pattern périodique du jeu → on diagnostiquait notre propre interférence. SUPPRIMÉ.
    ///
    /// Désormais : PresentMon (consommateur ETW passif, le producteur DXGI/DWM tourne
    /// déjà) + un sampler 1 Hz STRICTEMENT P/Invoke (GetSystemTimes + GlobalMemoryStatusEx,
    /// microsecondes, en process). Le corrélateur de coupables utilise les données de
    /// PresentMon lui-même (les autres process qui présentent des frames). Aucun WMI,
    /// aucun LHM, aucune énumération de process pendant la mesure.
    /// </summary>
    public sealed class GameSessionRecorder
    {
        // ───────────────────────── état ─────────────────────────
        private Process? _pm;
        private readonly List<SysSample> _samples = new();
        private System.Threading.Timer? _sampler;
        private DateTime _startedUtc;
        private SystemContextSnap? _sysContext;
        private GpuTelemetry? _gpu;

        public bool IsRecording => _pm != null && !_pm.HasExited;

        /// <summary>Dernier échantillon (pour l'affichage LIVE pendant la capture). null avant le 1er tick.</summary>
        public SysSample? LastSample { get; private set; }

        /// <summary>FPS instantané LIVE (médiane des frametimes récents du jeu, lus en direct sur le flux stdout). NaN tant que rien n'arrive.</summary>
        public double LiveFps { get; private set; } = double.NaN;

        // ── Streaming STDOUT de PresentMon (--output_stdout) : on lit le flux EN DIRECT sur un
        //    thread de fond → FPS live FIABLE (pas de fichier bufferisé) ET accumulation des
        //    frames pour l'analyse finale. Lecture d'un pipe, pas de sonde → 0 impact jeu. ──
        private Task? _reader;
        private volatile bool _reading;
        private readonly object _accLock = new();
        private readonly Dictionary<string, ProcessFrames> _byExe = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, int>> _modeCount = new(StringComparer.OrdinalIgnoreCase);
        private int _ciApp = -1, _ciTime, _ciFt, _ciCpu, _ciCpuW, _ciGpuB, _ciGpuW, _ciDisp, _ciInp, _ciMode = -1;
        private bool _hdrOk;
        // FPS live : appli (jeu) la plus présente + frametimes récents → médiane (calculée à 1 Hz).
        private readonly Dictionary<string, int> _appCounts = new(StringComparer.OrdinalIgnoreCase);
        private string _leadApp = "";
        private readonly Queue<double> _leadFt = new();
        private static readonly HashSet<string> _ignoreApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "dwm.exe", "explorer.exe", "msedgewebview2.exe", "chrome.exe", "brave.exe",
            "firefox.exe", "msedge.exe", "discord.exe", "steamwebhelper.exe",
            "gamebar.exe", "tweakly.exe", "claude.exe",
        };

        /// <summary>Démarre la capture. exeName = null → tous les process (on filtre au parsing).</summary>
        public bool Start(string? exeName, out string error)
        {
            error = "";
            if (IsRecording) { error = "Une capture est déjà en cours."; return false; }
            try
            {
                if (!File.Exists(PathLayout.PresentMon)) { error = "PresentMon.exe introuvable dans data\\tools."; return false; }

                // --output_stdout : PresentMon écrit le CSV (1 ligne par frame) sur STDOUT, qu'on
                // lit EN DIRECT → FPS live fiable + accumulation des frames. --timed 3600 = filet
                // anti-orphelin (auto-stop 1 h). --stop_existing_session : nettoie une session ETW
                // résiduelle d'un éventuel arrêt brutal précédent.
                string args = "--output_stdout --no_console_stats --stop_existing_session --timed 3600 --session_name TweaklySession";
                if (!string.IsNullOrWhiteSpace(exeName))
                    args += $" --process_name \"{exeName}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = PathLayout.PresentMon,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.Latin1,
                };

                // Reset de l'accumulateur + de l'état FPS live AVANT de lancer le lecteur.
                lock (_accLock) { _byExe.Clear(); _modeCount.Clear(); }
                _hdrOk = false; _ciApp = -1;
                _appCounts.Clear(); _leadApp = ""; _leadFt.Clear(); LiveFps = double.NaN;
                _samples.Clear();
                LastSample = null;

                _pm = Process.Start(psi);
                if (_pm == null) { error = "Impossible de lancer PresentMon."; return false; }
                // Priorité basse : l'enregistreur ne doit JAMAIS voler du temps CPU au jeu.
                try { _pm.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                _startedUtc = DateTime.UtcNow;
                _reading = true;
                _reader = Task.Run(ReadLoop);   // lecture du flux stdout sur un thread de fond

                // Snapshot contexte UNE FOIS (~100 ms, hors fenêtre de mesure) : disque du jeu,
                // RAM totale, plan d'alim, refresh moniteur. Process.GetProcesses ici = OK (one-shot).
                _sysContext = SystemContextSnap.Capture();
                try { _gpu = new GpuTelemetry(); } catch { _gpu = null; }
                // Sampler 1 Hz cheap (GetSystemTimes + GlobalMemoryStatusEx + GPU NvAPI) → SysSample
                // (% CPU global, RAM, télémétrie GPU) + médiane FPS live. Rien de lourd.
                _sampler = new System.Threading.Timer(_ => TakeSample(), null, 1000, 1000);
                AppLog.Write($"GameSession : capture demarree ({(exeName ?? "tous process")}) — stream stdout");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// Arrête la capture. Les frames sont DÉJÀ streamées en direct (pas de dépendance à un
        /// flush de fichier), donc on tue simplement PresentMon, on attend la fin du lecteur
        /// stdout, puis on construit le résultat à partir des frames accumulées.
        /// </summary>
        public async Task<SessionCapture?> StopAsync()
        {
            if (_pm == null) return null;
            try { _sampler?.Dispose(); _sampler = null; } catch { }
            try { _gpu?.Dispose(); _gpu = null; } catch { }

            var pm = _pm;
            _pm = null;
            _reading = false;
            try
            {
                if (!pm.HasExited) { try { pm.Kill(); } catch { } }
                await Task.Run(() => pm.WaitForExit(3000));
            }
            catch { }
            finally { try { pm.Dispose(); } catch { } }

            // Le pipe stdout se ferme à la mort de PresentMon → ReadLine renvoie null, le lecteur sort.
            try { if (_reader != null) await Task.WhenAny(_reader, Task.Delay(2500)); } catch { }
            _reader = null;

            SessionCapture cap;
            lock (_accLock)
            {
                foreach (var (exe, pf) in _byExe)
                    if (_modeCount.TryGetValue(exe, out var mc) && mc.Count > 0)
                        pf.PresentMode = mc.OrderByDescending(kv => kv.Value).First().Key;
                cap = new SessionCapture
                {
                    StartedUtc = _startedUtc,
                    Processes = _byExe.Values.Where(p => p.Frames.Count >= 30).ToList(),
                    Samples = _samples.ToList(),
                    SystemContext = _sysContext,
                };
            }
            AppLog.Write($"GameSession : capture terminee — {cap.Processes.Count} process, {cap.Processes.Sum(p => p.Frames.Count)} frames.");
            return cap.Processes.Count == 0 ? null : cap;
        }

        // Sampler 1 Hz, UNIQUEMENT des lectures P/Invoke en microsecondes. Aucune
        // énumération de process, aucun LHM, aucun WMI. Coût négligeable, 0 hitch.
        private void TakeSample()
        {
            try
            {
                var g = _gpu?.Read();   // ~2,5 ms in-process (NvAPI), sur ce thread de fond
                // FPS live = médiane des frametimes récents du jeu (alimentés par le lecteur stdout).
                double fps = double.NaN;
                lock (_accLock)
                {
                    if (_leadFt.Count >= 5)
                    {
                        var arr = _leadFt.ToArray();
                        Array.Sort(arr);
                        double med = arr[arr.Length / 2];
                        if (med > 0) fps = 1000.0 / med;
                    }
                }
                LiveFps = fps;
                var s = new SysSample
                {
                    ElapsedMs = (DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                    CpuLoadPct = CheapProbes.CpuLoadPercent(),
                    RamAvailMb = CheapProbes.AvailableRamMb(),
                    GpuTempC = g?.TempC ?? double.NaN,
                    GpuUsagePct = g?.UsagePct ?? double.NaN,
                    GpuCoreMhz = g?.CoreMhz ?? double.NaN,
                    GpuMemMhz = g?.MemMhz ?? double.NaN,
                    GpuVramUsedMB = g?.VramUsedMB ?? double.NaN,
                    Fps = LiveFps,
                };
                _samples.Add(s);
                LastSample = s;
            }
            catch { }
        }

        // Boucle de lecture du flux stdout de PresentMon (thread de fond). Bloque sur ReadLine ;
        // se termine quand le pipe se ferme (mort de PresentMon) ou _reading=false. Lecture de
        // pipe, pas de sonde → aucun impact sur le jeu.
        private void ReadLoop()
        {
            try
            {
                var sr = _pm?.StandardOutput;
                if (sr == null) return;
                string? line;
                while (_reading && (line = sr.ReadLine()) != null)
                    ProcessLine(line);
            }
            catch { }
        }

        // Une ligne CSV PresentMon (en-tête repérée PAR NOM de colonne, jamais par index — l'ordre
        // peut changer entre versions). Accumule la frame pour l'analyse + alimente le FPS live.
        private void ProcessLine(string line)
        {
            if (line.Length == 0) return;
            if (!_hdrOk)
            {
                var cols = line.Split(',');
                _ciApp  = Array.IndexOf(cols, "Application");
                _ciTime = Array.IndexOf(cols, "TimeInMs");
                _ciFt   = Array.IndexOf(cols, "MsBetweenPresents");
                _ciCpu  = Array.IndexOf(cols, "MsCPUBusy");
                _ciCpuW = Array.IndexOf(cols, "MsCPUWait");
                _ciGpuB = Array.IndexOf(cols, "MsGPUBusy");
                _ciGpuW = Array.IndexOf(cols, "MsGPUWait");
                _ciDisp = Array.IndexOf(cols, "MsUntilDisplayed");
                _ciInp  = Array.IndexOf(cols, "MsAllInputToPhotonLatency");
                _ciMode = Array.IndexOf(cols, "PresentMode");
                _hdrOk = _ciApp >= 0 && _ciFt >= 0;
                return;
            }
            var f = line.Split(',');
            if (_ciApp >= f.Length || _ciFt >= f.Length) return;
            string exe = f[_ciApp];
            double ft = Num(f, _ciFt);
            var fr = new FrameRecord
            {
                TimeMs         = Num(f, _ciTime),
                FrameTimeMs    = ft,
                CpuBusyMs      = Num(f, _ciCpu),
                CpuWaitMs      = Num(f, _ciCpuW),
                GpuBusyMs      = Num(f, _ciGpuB),
                GpuWaitMs      = Num(f, _ciGpuW),
                DisplayedMs    = Num(f, _ciDisp),
                InputLatencyMs = Num(f, _ciInp),
            };
            string? mode = (_ciMode >= 0 && _ciMode < f.Length) ? f[_ciMode] : null;

            lock (_accLock)
            {
                if (!_byExe.TryGetValue(exe, out var pf)) { pf = new ProcessFrames { Exe = exe }; _byExe[exe] = pf; _modeCount[exe] = new(); }
                pf.Frames.Add(fr);
                if (mode != null) { var mc = _modeCount[exe]; mc[mode] = mc.TryGetValue(mode, out int n) ? n + 1 : 1; }

                // FPS live : jeu = appli NON ignorée la plus présente ; on accumule SES frametimes.
                _appCounts[exe] = _appCounts.TryGetValue(exe, out int c) ? c + 1 : 1;
                string lead = ""; int best = -1;
                foreach (var kv in _appCounts)
                    if (!_ignoreApps.Contains(kv.Key) && kv.Value > best) { best = kv.Value; lead = kv.Key; }
                if (lead.Length > 0)
                {
                    if (!lead.Equals(_leadApp, StringComparison.OrdinalIgnoreCase)) { _leadApp = lead; _leadFt.Clear(); }
                    if (exe.Equals(_leadApp, StringComparison.OrdinalIgnoreCase) && ft > 0)
                    { _leadFt.Enqueue(ft); while (_leadFt.Count > 240) _leadFt.Dequeue(); }
                }
            }
        }

        private static double Num(string[] f, int i)
        {
            if (i < 0 || i >= f.Length) return double.NaN;
            return double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
        }

        private void Cleanup()
        {
            _reading = false;
            try { _sampler?.Dispose(); } catch { }
            _sampler = null;
            try { _gpu?.Dispose(); } catch { }
            _gpu = null;
            try { if (_pm != null && !_pm.HasExited) _pm.Kill(); } catch { }
            _pm = null;
        }

        /// <summary>
        /// Arrêt BRUTAL sans analyse : stoppe le lecteur stdout + tue PresentMon. À appeler si la
        /// page est quittée ou l'app fermée pendant une capture (sinon PresentMon resterait à tourner).
        /// </summary>
        public void Abort()
        {
            _reading = false;
            Cleanup();
            try { _reader?.Wait(1500); } catch { }
            _reader = null;
        }
    }

    /// <summary>Une frame présentée (sous-ensemble utile des colonnes PresentMon v2).</summary>
    public sealed class FrameRecord
    {
        public double TimeMs;          // horodatage depuis le début de capture
        public double FrameTimeMs;     // MsBetweenPresents
        public double CpuBusyMs;       // MsCPUBusy  — le thread du jeu CALCULE
        public double CpuWaitMs;       // MsCPUWait  — le thread du jeu ATTEND (bloqué/descheduled)
        public double GpuBusyMs;       // MsGPUBusy  — travail GPU de la frame
        public double GpuWaitMs;       // MsGPUWait  — GPU qui ATTEND (pas le bottleneck)
        public double DisplayedMs;     // MsUntilDisplayed
        public double InputLatencyMs;  // MsAllInputToPhotonLatency (NaN si NA)
    }

    /// <summary>Les frames d'un process présent à l'écran pendant la capture.</summary>
    public sealed class ProcessFrames
    {
        public string Exe = "";
        public string PresentMode = "";          // mode dominant
        public List<FrameRecord> Frames = new();
    }

    /// <summary>Échantillon système à 1 Hz : CPU/RAM (P/Invoke gratuit) + GPU (NvAPI ~2,5 ms).</summary>
    public sealed class SysSample
    {
        public double ElapsedMs;
        public double CpuLoadPct;
        public double RamAvailMb;
        public double GpuTempC = double.NaN;
        public double GpuUsagePct = double.NaN;
        public double GpuCoreMhz = double.NaN;
        public double GpuMemMhz = double.NaN;     // horloge mémoire GPU (déjà lue par NvAPI, gratuit)
        public double GpuVramUsedMB = double.NaN;
        public double Fps = double.NaN;           // FPS instantané (tail CSV PresentMon, passif) au moment de l'échantillon
    }

    /// <summary>Résultat brut d'une capture, prêt pour l'analyse.</summary>
    public sealed class SessionCapture
    {
        public DateTime StartedUtc;
        public List<ProcessFrames> Processes = new();
        public List<SysSample> Samples = new();
        public SystemContextSnap? SystemContext;

        /// <summary>
        /// Le process « jeu » le plus probable : celui qui a présenté le plus de frames,
        /// hors compositeur et hors apps de fond connues (overlay, navigateurs, launchers).
        /// </summary>
        public ProcessFrames? MainGame()
        {
            string[] ignore = { "dwm.exe", "explorer.exe", "msedgewebview2.exe", "chrome.exe",
                                "brave.exe", "firefox.exe", "msedge.exe", "discord.exe",
                                "steamwebhelper.exe", "gamebar.exe", "tweakly.exe", "claude.exe" };
            return Processes
                .Where(p => !ignore.Contains(p.Exe.ToLowerInvariant()))
                .OrderByDescending(p => p.Frames.Count)
                .FirstOrDefault();
        }
    }


    /// <summary>Sondes quasi gratuites (P/Invoke purs) pour le sampler de corrélation.</summary>
    internal static class CheapProbes
    {
        private static long _prevIdle, _prevKernel, _prevUser;

        public static double CpuLoadPercent()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return double.NaN;
            long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            long total = dKernel + dUser;
            if (total <= 0) return double.NaN;
            return Math.Clamp(100.0 * (total - dIdle) / total, 0, 100);
        }

        public static double AvailableRamMb()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys / (1024.0 * 1024.0) : double.NaN;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
