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
        private string? _csvPath;
        private readonly List<SysSample> _samples = new();
        private System.Threading.Timer? _sampler;
        private DateTime _startedUtc;
        private SystemContextSnap? _sysContext;
        private GpuTelemetry? _gpu;

        public bool IsRecording => _pm != null && !_pm.HasExited;

        /// <summary>Dernier échantillon (pour l'affichage LIVE pendant la capture). null avant le 1er tick.</summary>
        public SysSample? LastSample { get; private set; }

        /// <summary>Démarre la capture. exeName = null → tous les process (on filtre au parsing).</summary>
        public bool Start(string? exeName, out string error)
        {
            error = "";
            if (IsRecording) { error = "Une capture est déjà en cours."; return false; }
            try
            {
                if (!File.Exists(PathLayout.PresentMon)) { error = "PresentMon.exe introuvable dans data\\tools."; return false; }

                _csvPath = Path.Combine(Path.GetTempPath(), $"tweakly_session_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                // --timed 3600 = filet de sécurité : si PresentMon devenait orphelin (app
                // fermée en pleine capture), il s'auto-termine au bout d'1 h au lieu de
                // tourner indéfiniment. Sans effet en usage normal (sessions = minutes,
                // arrêtées par Ctrl+C bien avant).
                string args = $"--output_file \"{_csvPath}\" --no_console_stats --stop_existing_session --timed 3600 --session_name TweaklySession";
                if (!string.IsNullOrWhiteSpace(exeName))
                    args += $" --process_name \"{exeName}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = PathLayout.PresentMon,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _pm = Process.Start(psi);
                if (_pm == null) { error = "Impossible de lancer PresentMon."; return false; }

                // Priorité basse : l'enregistreur ne doit JAMAIS voler du temps CPU au jeu.
                try { _pm.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                _startedUtc = DateTime.UtcNow;
                _samples.Clear();
                // Snapshot UNE FOIS au démarrage (~100 ms) : pris pendant la fenêtre de
                // trim (15 s de tête, hors mesure), donc invisible. Donne disque du jeu,
                // RAM totale, plan d'alim, refresh moniteur — du contexte VRAI, pas de
                // questions de bot. Process.GetProcesses ici = OK (one-shot, hors jeu).
                _sysContext = SystemContextSnap.Capture();
                LastSample = null;
                try { _gpu = new GpuTelemetry(); } catch { _gpu = null; }
                // Sampler 1 Hz STRICTEMENT cheap (GetSystemTimes + GlobalMemoryStatusEx,
                // microsecondes, en process). Donne le % CPU global au moment des drops
                // = contexte VRAI ("CPU global à 22 % → pas une saturation"). Rien d'autre.
                _sampler = new System.Threading.Timer(_ => TakeSample(), null, 1000, 1000);
                AppLog.Write($"GameSession : capture demarree ({(exeName ?? "tous process")}) -> {_csvPath}");
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
        /// Arrête proprement la capture (Ctrl+C envoyé à la console de PresentMon pour
        /// qu'il ferme la session ETW et flush le CSV) puis parse le résultat.
        /// </summary>
        public async Task<SessionCapture?> StopAsync()
        {
            if (_pm == null) return null;
            try { _sampler?.Dispose(); _sampler = null; } catch { }
            try { _gpu?.Dispose(); _gpu = null; } catch { }

            var pm = _pm;
            _pm = null;
            try
            {
                if (!pm.HasExited)
                {
                    // Ctrl+C « à distance » : on s'attache à sa console, on ignore le
                    // signal nous-mêmes, on l'émet, puis on se détache. C'est la seule
                    // façon de stopper un PresentMon caché SANS perdre la fin du CSV.
                    if (AttachConsole((uint)pm.Id))
                    {
                        SetConsoleCtrlHandler(null, true);
                        GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                        bool exited = await Task.Run(() => pm.WaitForExit(5000));
                        FreeConsole();
                        SetConsoleCtrlHandler(null, false);
                        if (!exited) { try { pm.Kill(); } catch { } }
                    }
                    else
                    {
                        try { pm.Kill(); } catch { }
                    }
                    await Task.Run(() => pm.WaitForExit(3000));
                }
            }
            catch { try { pm.Kill(); } catch { } }
            finally { try { pm.Dispose(); } catch { } }

            string? csv = _csvPath;
            _csvPath = null;
            if (csv == null || !File.Exists(csv)) return null;

            var capture = await Task.Run(() => FrameCsvParser.Parse(csv, _startedUtc, _samples.ToList()));
            if (capture != null) capture.SystemContext = _sysContext;
            try { File.Delete(csv); } catch { }
            AppLog.Write($"GameSession : capture terminee — {capture?.Processes.Count ?? 0} process, " +
                         $"{capture?.Processes.Sum(p => p.Frames.Count) ?? 0} frames.");
            return capture;
        }

        // Sampler 1 Hz, UNIQUEMENT des lectures P/Invoke en microsecondes. Aucune
        // énumération de process, aucun LHM, aucun WMI. Coût négligeable, 0 hitch.
        private void TakeSample()
        {
            try
            {
                var g = _gpu?.Read();   // ~2,5 ms in-process (NvAPI), sur ce thread de fond
                var s = new SysSample
                {
                    ElapsedMs = (DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                    CpuLoadPct = CheapProbes.CpuLoadPercent(),
                    RamAvailMb = CheapProbes.AvailableRamMb(),
                    GpuTempC = g?.TempC ?? double.NaN,
                    GpuUsagePct = g?.UsagePct ?? double.NaN,
                    GpuCoreMhz = g?.CoreMhz ?? double.NaN,
                    GpuVramUsedMB = g?.VramUsedMB ?? double.NaN,
                };
                _samples.Add(s);
                LastSample = s;
            }
            catch { }
        }

        private void Cleanup()
        {
            try { _sampler?.Dispose(); } catch { }
            _sampler = null;
            try { _gpu?.Dispose(); } catch { }
            _gpu = null;
            try { if (_pm != null && !_pm.HasExited) _pm.Kill(); } catch { }
            _pm = null;
        }

        /// <summary>
        /// Arrêt BRUTAL sans analyse : tue PresentMon + le timer + supprime le CSV partiel.
        /// À appeler si la page est quittée ou l'app fermée pendant une capture (sinon
        /// PresentMon resterait à tourner et à grossir le CSV en arrière-plan).
        /// </summary>
        public void Abort()
        {
            Cleanup();
            try { if (_csvPath != null && File.Exists(_csvPath)) File.Delete(_csvPath); } catch { }
            _csvPath = null;
        }

        // ─────────────── P/Invoke Ctrl+C console ───────────────
        private const uint CTRL_C_EVENT = 0;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);
        private delegate bool ConsoleCtrlDelegate(uint ctrlType);
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
        public double GpuVramUsedMB = double.NaN;
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

    /// <summary>
    /// Parseur du CSV PresentMon v2. Colonnes repérées PAR NOM d'en-tête (jamais par
    /// index : l'ordre peut changer entre versions — même famille de piège que winget).
    /// </summary>
    public static class FrameCsvParser
    {
        public static SessionCapture? Parse(string csvPath, DateTime startedUtc, List<SysSample> samples)
        {
            try
            {
                using var sr = new StreamReader(csvPath);
                string? header = sr.ReadLine();
                if (header == null) return null;
                var cols = header.Split(',');
                int iApp  = Array.IndexOf(cols, "Application");
                int iTime = Array.IndexOf(cols, "TimeInMs");
                int iFt   = Array.IndexOf(cols, "MsBetweenPresents");
                int iCpu  = Array.IndexOf(cols, "MsCPUBusy");
                int iCpuW = Array.IndexOf(cols, "MsCPUWait");
                int iGpuB = Array.IndexOf(cols, "MsGPUBusy");
                int iGpuW = Array.IndexOf(cols, "MsGPUWait");
                int iDisp = Array.IndexOf(cols, "MsUntilDisplayed");
                int iInp  = Array.IndexOf(cols, "MsAllInputToPhotonLatency");
                int iMode = Array.IndexOf(cols, "PresentMode");
                if (iApp < 0 || iFt < 0) return null;   // format inattendu → on ne devine pas

                var byExe = new Dictionary<string, ProcessFrames>(StringComparer.OrdinalIgnoreCase);
                var modeCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var f = line.Split(',');
                    if (f.Length <= iFt) continue;
                    string exe = f[iApp];
                    if (!byExe.TryGetValue(exe, out var pf))
                    {
                        pf = new ProcessFrames { Exe = exe };
                        byExe[exe] = pf;
                        modeCount[exe] = new Dictionary<string, int>();
                    }
                    pf.Frames.Add(new FrameRecord
                    {
                        TimeMs         = Num(f, iTime),
                        FrameTimeMs    = Num(f, iFt),
                        CpuBusyMs      = Num(f, iCpu),
                        CpuWaitMs      = Num(f, iCpuW),
                        GpuBusyMs      = Num(f, iGpuB),
                        GpuWaitMs      = Num(f, iGpuW),
                        DisplayedMs    = Num(f, iDisp),
                        InputLatencyMs = Num(f, iInp),
                    });
                    if (iMode >= 0 && iMode < f.Length)
                    {
                        var mc = modeCount[exe];
                        mc[f[iMode]] = mc.TryGetValue(f[iMode], out int n) ? n + 1 : 1;
                    }
                }
                foreach (var (exe, pf) in byExe)
                    if (modeCount[exe].Count > 0)
                        pf.PresentMode = modeCount[exe].OrderByDescending(kv => kv.Value).First().Key;

                return new SessionCapture
                {
                    StartedUtc = startedUtc,
                    Processes = byExe.Values.Where(p => p.Frames.Count >= 30).ToList(),
                    Samples = samples,
                };
            }
            catch { return null; }
        }

        private static double Num(string[] f, int i)
        {
            if (i < 0 || i >= f.Length) return double.NaN;
            return double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
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
