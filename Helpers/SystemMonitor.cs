using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Collecte des métriques système en temps réel (sans driver kernel).
    /// CPU : usage + fréquence live (estimée). RAM : usage. GPU NVIDIA : via NvAPI en priorité.
    /// </summary>
    public sealed class MonSnapshot
    {
        public double CpuUsage;     // %
        public double CpuMHz;       // fréquence live
        public int    CpuBaseMHz;   // fréquence de base
        public double? CpuTempC;    // température CPU (°C) si activée + pilote présent, sinon null
        public string CpuName     = "";
        public int    CpuCores;
        public int    CpuThreads;
        public int    Processes;    // nombre de processus actifs
        public string TopCpuName  = "";   // process le + gourmand CPU
        public double TopCpuPct;
        public string TopRamName  = "";   // process le + gourmand RAM
        public double TopRamMB;

        public double RamUsedGB;
        public double RamFreeGB;
        public double RamTotalGB;       // utilisable (vue OS)
        public double RamInstalledGB;   // physiquement installée (somme barrettes)
        public double RamPct;
        public string RamType   = "";   // DDR4 / DDR5
        public int    RamSpeed;         // MHz
        public int    RamSticks;

        public bool   GpuOk;
        public string GpuName        = "";
        public double GpuUsage;      // %
        public double GpuVramUsedMB;
        public double GpuVramTotalMB;
        public double GpuTemp;       // °C
        public double GpuWatts;
        public double GpuMHz;
        public bool   GpuIsIntegrated;   // true = IGP (Intel/AMD intégré) affiché faute de carte dédiée

        public List<NvmeInfo> Nvmes = new();   // disques NVMe + température
    }

    public sealed class NvmeInfo
    {
        public string Name     = "";
        public int    TempC;
        public double UsagePct;   // % d'activité disque
    }

    [Flags]
    public enum MonCollectParts
    {
        Cpu      = 1,
        Processes= 2,
        Ram      = 4,
        Gpu      = 8,
        GpuWatts = 16,
        Nvme     = 32,

        Light = Cpu | Ram | Gpu,
        All   = Cpu | Processes | Ram | Gpu | GpuWatts | Nvme,
    }

    public static class SystemMonitor
    {
        // ── RAM (GlobalMemoryStatusEx) ────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ── Collecte complète (à appeler sur un thread de fond) ──────────────
        private static readonly object _collectLock = new();

        public static MonSnapshot Collect(MonCollectParts parts = MonCollectParts.All)
        {
            // v1.3.5 (retour utilisateur : « les valeurs mettent longtemps à arriver ») :
            // les 5 sections tournent en PARALLÈLE. Au premier appel, chacune paie son
            // démarrage à froid (WMI perf CPU, init LibreHardwareMonitor 1-3 s, spawn
            // nvidia-smi, namespace WMI stockage) — en série ça s'additionnait en
            // plusieurs secondes, en parallèle on ne paie que la plus lente.
            // Sans risque : chaque section écrit des champs DISTINCTS du snapshot et
            // ne touche qu'à SES caches statiques.
            lock (_collectLock)
            {
                var s = new MonSnapshot();
                var tasks = new List<System.Threading.Tasks.Task>(6);

                bool Has(MonCollectParts p) => (parts & p) != 0;

                if (Has(MonCollectParts.Cpu))
                    tasks.Add(System.Threading.Tasks.Task.Run(() => CollectCpu(s)));
                if (Has(MonCollectParts.Processes))
                    tasks.Add(System.Threading.Tasks.Task.Run(() => CollectProcesses(s)));
                if (Has(MonCollectParts.Ram))
                    tasks.Add(System.Threading.Tasks.Task.Run(() => CollectRam(s)));
                if (Has(MonCollectParts.Gpu))
                    tasks.Add(System.Threading.Tasks.Task.Run(() => CollectGpu(s, Has(MonCollectParts.GpuWatts))));
                if (Has(MonCollectParts.Nvme))
                    tasks.Add(System.Threading.Tasks.Task.Run(() => CollectNvme(s)));

                System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
                return s;
            }
        }

        // ── Processus : compte + plus gros consommateurs CPU & RAM ────────────
        // Le balayage de TOUS les process (WorkingSet64 + TotalProcessorTime) est
        // l'opération la plus coûteuse → on la rafraîchit toutes les ~2 s seulement
        // (le compte/top n'a pas besoin d'une granularité 1 s) ; entre-temps : cache.
        private static System.Collections.Generic.Dictionary<int, TimeSpan> _prevCpu = new();
        private static DateTime _prevStamp;
        private static DateTime _procCacheTime;
        private static (int procs, string topCpu, double topCpuPct, string topRam, double topRamMB) _procCache;

        private static void CollectProcesses(MonSnapshot s)
        {
            // Cache 2 s : le balayage de tous les process coûte plus cher que CPU/GPU/RAM.
            // Le graphe principal reste à 1 Hz ; seuls "top CPU/top RAM" bougent moins vite.
            if (_procCacheTime != default &&
                (DateTime.UtcNow - _procCacheTime).TotalMilliseconds < 2000)
            {
                s.Processes  = _procCache.procs;
                s.TopCpuName = _procCache.topCpu;
                s.TopCpuPct  = _procCache.topCpuPct;
                s.TopRamName = _procCache.topRam;
                s.TopRamMB   = _procCache.topRamMB;
                return;
            }

            try
            {
                var procs = Process.GetProcesses();
                s.Processes = procs.Length;

                var now     = DateTime.UtcNow;
                double secs = (now - _prevStamp).TotalSeconds;
                int    cores = Environment.ProcessorCount;
                var    cur   = new System.Collections.Generic.Dictionary<int, TimeSpan>(procs.Length);

                double bestCpu = 0;  string bestCpuName = "";
                long   bestRam = 0;  string bestRamName = "";

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.Id == 0) continue;   // "Idle" fausse le CPU

                        // RAM (working set physique)
                        long ws = p.WorkingSet64;
                        if (ws > bestRam) { bestRam = ws; bestRamName = p.ProcessName; }

                        // CPU : delta de temps processeur / temps écoulé / cœurs
                        var ct = p.TotalProcessorTime;
                        cur[p.Id] = ct;
                        if (secs > 0 && _prevCpu.TryGetValue(p.Id, out var prev))
                        {
                            double pct = (ct - prev).TotalSeconds / (secs * cores) * 100.0;
                            if (pct > bestCpu) { bestCpu = pct; bestCpuName = p.ProcessName; }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                _prevCpu   = cur;
                _prevStamp = now;

                s.TopCpuName = Short(bestCpuName);
                s.TopCpuPct  = Math.Max(0, Math.Min(100, bestCpu));
                s.TopRamName = Short(bestRamName);
                s.TopRamMB   = bestRam / (1024.0 * 1024.0);

                // Mémoriser pour les ticks intermédiaires
                _procCache = (s.Processes, s.TopCpuName, s.TopCpuPct, s.TopRamName, s.TopRamMB);
                _procCacheTime = DateTime.UtcNow;
            }
            catch { }
        }

        private static string Short(string n)
            => string.IsNullOrEmpty(n) ? "" : (n.Length > 16 ? n.Substring(0, 15) + "…" : n);

        // Infos CPU statiques (lues une seule fois)
        private static string _cpuName = "";
        private static int    _cpuCores, _cpuThreads;

        private static void EnsureCpuStatic()
        {
            if (_cpuName.Length > 0) return;
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (ManagementObject o in q.Get())
                {
                    _cpuName    = (o["Name"]?.ToString() ?? "").Trim();
                    _cpuCores   = Convert.ToInt32(o["NumberOfCores"] ?? 0);
                    _cpuThreads = Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                    o.Dispose();
                    break;
                }
            }
            catch { }
            if (_cpuName.Length == 0) _cpuName = "Processeur";
            // Nettoyer le nom (retirer (R), (TM), CPU @ x.xGHz)
            _cpuName = _cpuName
                .Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
                .Replace("CPU", "").Trim();
            var at = _cpuName.IndexOf('@');
            if (at > 0) _cpuName = _cpuName.Substring(0, at).Trim();
        }

        // ManagementObjectSearcher RÉUTILISÉS (v1.4.3) : l'init WMI coûte plus que la requête
        // elle-même. Avant on en créait 2 neufs à chaque tick CPU → ~30-80 ms de cadeau au CPU
        // pour rien. Ces searchers sont appelés en série dans CollectCpu (qui tourne sur UNE
        // section parallèle de Collect, jamais simultanément avec lui-même grâce au _busy de
        // PageMonitoring/MiniMonitor) → safe sans lock.
        private static readonly ManagementObjectSearcher _cpuUsageQuery = new(
            "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
        private static readonly ManagementObjectSearcher _cpuFreqQuery = new(
            "SELECT PercentProcessorPerformance, ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name LIKE '%_Total'");

        private static void CollectCpu(MonSnapshot s)
        {
            EnsureCpuStatic();
            s.CpuName    = _cpuName;
            s.CpuCores   = _cpuCores;
            s.CpuThreads = _cpuThreads;

            // Utilisation %
            try
            {
                using var coll = _cpuUsageQuery.Get();
                foreach (ManagementObject o in coll)
                {
                    s.CpuUsage = Convert.ToDouble(o["PercentProcessorTime"]);
                    o.Dispose();
                    break;
                }
            }
            catch { }

            // Fréquence live = base × (% performance / 100) → capture le turbo
            try
            {
                using var coll = _cpuFreqQuery.Get();
                foreach (ManagementObject o in coll)
                {
                    var baseMhz = Convert.ToDouble(o["ProcessorFrequency"]);
                    var perf    = Convert.ToDouble(o["PercentProcessorPerformance"]);
                    s.CpuBaseMHz = (int)baseMhz;
                    s.CpuMHz     = perf > 0 ? baseMhz * perf / 100.0 : baseMhz;
                    o.Dispose();
                    break;
                }
            }
            catch { }

            // Température CPU (opt-in) : null si désactivée / pilote PawnIO absent / non élevé.
            s.CpuTempC = CpuTemperature.Read();
        }

        // Infos RAM statiques (type, fréquence, nb de barrettes, capacité) — lues une fois
        private static string _ramType = "";
        private static int    _ramSpeed, _ramSticks;
        private static double _ramInstalledGB;

        private static void EnsureRamStatic()
        {
            if (_ramSticks > 0) return;
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
                var typeMap = new System.Collections.Generic.Dictionary<int, string>
                    { {20,"DDR2"}, {24,"DDR3"}, {26,"DDR4"}, {34,"DDR5"} };
                ulong totalBytes = 0;
                foreach (ManagementObject o in q.Get())
                {
                    _ramSticks++;
                    if (o["Capacity"] != null) totalBytes += Convert.ToUInt64(o["Capacity"]);
                    if (_ramSpeed == 0 && o["Speed"] != null) _ramSpeed = Convert.ToInt32(o["Speed"]);
                    if (_ramType.Length == 0 &&
                        typeMap.TryGetValue(Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0), out var tname))
                        _ramType = tname;
                    o.Dispose();
                }
                _ramInstalledGB = totalBytes / (1024.0 * 1024.0 * 1024.0);
            }
            catch { }
            if (_ramType == null || _ramType.Length == 0) _ramType = "DDR";
        }

        private static void CollectRam(MonSnapshot s)
        {
            EnsureRamStatic();
            s.RamType        = _ramType;
            s.RamSpeed       = _ramSpeed;
            s.RamSticks      = _ramSticks;
            s.RamInstalledGB = _ramInstalledGB;
            try
            {
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    double gb = 1024.0 * 1024.0 * 1024.0;
                    s.RamTotalGB = mem.ullTotalPhys / gb;
                    s.RamFreeGB  = mem.ullAvailPhys / gb;
                    s.RamUsedGB  = (mem.ullTotalPhys - mem.ullAvailPhys) / gb;
                    s.RamPct     = mem.dwMemoryLoad;
                }
            }
            catch { }
        }

        // ── Températures NVMe (namespace Storage WMI) ────────────────────────
        // BusType 17 = NVMe. La température (°C) vient de MSFT_StorageReliabilityCounter.
        // Nécessite l'élévation admin (l'app tourne en requireAdministrator).
        // Cache 4 s : la température disque évolue lentement et la requête WMI est coûteuse.
        private static DateTime        _nvmeCacheTime;
        private static List<NvmeInfo>  _nvmeCache = new();

        private static void CollectNvme(MonSnapshot s)
        {
            // Cache 3 s : WMI stockage est coûteux et la température NVMe évolue lentement.
            // La page Monitoring ne demande déjà cette section qu'un tick sur 3.
            if (_nvmeCacheTime != default &&
                (DateTime.UtcNow - _nvmeCacheTime).TotalMilliseconds < 3000)
            {
                s.Nvmes = _nvmeCache;
                return;
            }

            var list      = new List<NvmeInfo>();
            var byDevice  = new Dictionary<int, NvmeInfo>();   // DeviceId → info (pour mapper l'usage)
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();

                // ObjectId (la clé) est indispensable : sans elle, l'objet n'a pas de __PATH
                // valide et GetRelated() échoue ("Operation is not valid due to the current state").
                var query = new ObjectQuery(
                    "SELECT ObjectId, DeviceId, FriendlyName, BusType FROM MSFT_PhysicalDisk WHERE BusType = 17");
                using var searcher = new ManagementObjectSearcher(scope, query);

                foreach (ManagementObject disk in searcher.Get())
                {
                    try
                    {
                        var name = disk["FriendlyName"]?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(name)) name = "NVMe";

                        int temp = 0;
                        foreach (ManagementObject rc in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            if (rc["Temperature"] != null)
                                temp = Convert.ToInt32(rc["Temperature"]);
                            rc.Dispose();
                            break;
                        }

                        if (temp > 0 && temp < 200)   // garde-fou contre les valeurs aberrantes
                        {
                            var info = new NvmeInfo { Name = name, TempC = temp };
                            list.Add(info);
                            if (int.TryParse(disk["DeviceId"]?.ToString(), out int devId))
                                byDevice[devId] = info;
                        }
                    }
                    catch { }
                    finally { disk.Dispose(); }
                }
            }
            catch { }

            // % d'activité disque via compteur de perf (root\CIMV2). Name = "<num> <lettres>".
            // util% = 100 - PercentIdleTime (plus fiable que PercentDiskTime qui peut dépasser 100).
            try
            {
                using var perf = new ManagementObjectSearcher(
                    "SELECT Name, PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
                foreach (ManagementObject o in perf.Get())
                {
                    try
                    {
                        var nm = o["Name"]?.ToString() ?? "";
                        var numStr = nm.Split(' ')[0];
                        if (int.TryParse(numStr, out int dn) && byDevice.TryGetValue(dn, out var info))
                        {
                            var idle = Convert.ToDouble(o["PercentIdleTime"]);
                            info.UsagePct = Math.Max(0, Math.Min(100, 100 - idle));
                        }
                    }
                    catch { }
                    finally { o.Dispose(); }
                }
            }
            catch { }

            _nvmeCache     = list;
            _nvmeCacheTime = DateTime.UtcNow;
            s.Nvmes        = list;
        }

        // ── GPU : carte DÉDIÉE prioritaire (Nvidia via NvAPI = données riches), sinon
        //    AMD dédiée / IGP intégré via WMI (nom + VRAM registre) + compteurs « GPU Engine » (usage).
        //    NvAPI in-process = ~2,5 ms → ZÉRO cache (valeur fraîche chaque tick, courbe vivante).
        //    nvidia-smi (fallback) + WMI GPU Engine sont lents → cache 1,5 s.
        private static DateTime _gpuCacheTime;
        private static (bool ok, string name, double usage, double vu, double vt,
                        double temp, double watts, double mhz, bool igp) _gpuCache;

        private static void CollectGpu(MonSnapshot s, bool includeWatts)
        {
            try
            {
                var gpu = SelectDisplayGpu();   // dédiée d'abord, IGP en dernier recours ; cache interne
                if (gpu == null) { s.GpuOk = false; return; }

                // Carte Nvidia → NvAPI IN-PROCESS (~2,5 ms : usage/temp/horloge/VRAM).
                // PAS de cache : c'est assez rapide pour lire à chaque tick → la valeur GPU et
                // sa courbe bougent seconde par seconde (le cache 1,8 s d'avant figeait 1 tick sur 2).
                // Watts via EnsureWattsFetch (nvidia-smi lent, refresh non bloquant 5 s en arrière-plan).
                if (gpu.Vendor == GpuVendor.Nvidia && TryNvapiGpu(s, gpu, includeWatts))
                {
                    s.GpuIsIntegrated = false;
                    return;
                }

                // Collecte légère : si NvAPI ne répond pas, ne jamais lancer nvidia-smi.
                // On se rabat sur le compteur GPU Engine, moins riche mais sans process externe.
                if (gpu.Vendor == GpuVendor.Nvidia && !includeWatts)
                {
                    if (TryReuseGpuCache(s)) return;
                    s.GpuName         = gpu.Name;
                    s.GpuIsIntegrated = false;
                    s.GpuUsage        = GpuEngineBusiestUsage();
                    s.GpuVramTotalMB  = gpu.DedicatedVramMB;
                    s.GpuVramUsedMB   = 0;
                    s.GpuTemp = 0; s.GpuWatts = 0; s.GpuMHz = 0;
                    s.GpuOk           = true;
                    CacheGpu(s);
                    return;
                }

                // Fallback Nvidia complet : nvidia-smi (spawn lent, jusqu'à 4 s) → on cache 1,5 s.
                if (gpu.Vendor == GpuVendor.Nvidia && TryReuseGpuCache(s)) return;
                if (gpu.Vendor == GpuVendor.Nvidia && TryNvidiaSmi(s))
                {
                    s.GpuIsIntegrated = false;
                    CacheGpu(s);
                    return;
                }

                // AMD dédiée ou IGP : nom (WMI) + VRAM totale (registre, 0 pour un IGP) + usage (compteurs).
                // WMI GpuEngine reste lent → cache 1,5 s aussi.
                if (TryReuseGpuCache(s)) return;
                s.GpuName         = gpu.Name;
                s.GpuIsIntegrated = gpu.IsIntegrated;
                s.GpuUsage        = GpuEngineBusiestUsage();   // best-effort, source invariante
                s.GpuVramTotalMB  = gpu.DedicatedVramMB;       // 0 = IGP (pas de VRAM dédiée)
                s.GpuVramUsedMB   = 0;                          // indispo hors Nvidia → « — »
                s.GpuTemp = 0; s.GpuWatts = 0; s.GpuMHz = 0;   // idem → « — »
                s.GpuOk           = true;
                CacheGpu(s);
            }
            catch { s.GpuOk = false; }
        }

        private static bool TryReuseGpuCache(MonSnapshot s)
        {
            if (_gpuCacheTime == default ||
                (DateTime.UtcNow - _gpuCacheTime).TotalMilliseconds >= 1500) return false;
            s.GpuOk = _gpuCache.ok; s.GpuName = _gpuCache.name; s.GpuUsage = _gpuCache.usage;
            s.GpuVramUsedMB = _gpuCache.vu; s.GpuVramTotalMB = _gpuCache.vt;
            s.GpuTemp = _gpuCache.temp; s.GpuWatts = _gpuCache.watts; s.GpuMHz = _gpuCache.mhz;
            s.GpuIsIntegrated = _gpuCache.igp;
            return true;
        }

        private static void CacheGpu(MonSnapshot s)
        {
            _gpuCache = (s.GpuOk, s.GpuName, s.GpuUsage, s.GpuVramUsedMB, s.GpuVramTotalMB,
                         s.GpuTemp, s.GpuWatts, s.GpuMHz, s.GpuIsIntegrated);
            _gpuCacheTime = DateTime.UtcNow;
        }

        // ── GPU Nvidia via NvAPI in-process (rapide) + watts en arrière-plan ──────────────
        private static GpuTelemetry? _nvapi;
        private static bool _nvapiTried;
        private static double _gpuWattsCached;          // dernier watts connu (nvidia-smi lent)
        private static DateTime _gpuWattsTime;
        private static int _gpuWattsFetching;           // garde anti-empilement (Interlocked)

        // Remplit s via NvAPI (usage/temp/horloge/VRAM en process, ~2,5 ms). Watts = valeur
        // mise en cache par EnsureWattsFetch (refresh nvidia-smi lent, non bloquant). Renvoie
        // false si NvAPI indisponible → l'appelant retombe sur nvidia-smi.
        private static bool TryNvapiGpu(MonSnapshot s, GpuInfo gpu, bool includeWatts)
        {
            try
            {
                if (!_nvapiTried) { _nvapiTried = true; try { _nvapi = new GpuTelemetry(); } catch { _nvapi = null; } }
                if (_nvapi == null || !_nvapi.Available) return false;

                var r = _nvapi.Read();
                // Si rien d'exploitable (ni usage ni temp), NvAPI ne sert à rien → fallback.
                if (double.IsNaN(r.UsagePct) && double.IsNaN(r.TempC)) return false;

                s.GpuName        = gpu.Name;
                s.GpuUsage       = double.IsNaN(r.UsagePct)    ? 0 : r.UsagePct;
                s.GpuTemp        = double.IsNaN(r.TempC)       ? 0 : r.TempC;
                s.GpuMHz         = double.IsNaN(r.CoreMhz)     ? 0 : r.CoreMhz;
                s.GpuVramUsedMB  = double.IsNaN(r.VramUsedMB)  ? 0 : r.VramUsedMB;
                s.GpuVramTotalMB = double.IsNaN(r.VramTotalMB) ? gpu.DedicatedVramMB : r.VramTotalMB;
                s.GpuWatts       = includeWatts ? _gpuWattsCached : 0;   // watts = nvidia-smi, évité en mode léger
                s.GpuOk          = true;
                if (includeWatts) EnsureWattsFetch();          // refresh watts lent si nécessaire
                return true;
            }
            catch { return false; }
        }

        // Refresh des watts via nvidia-smi, au plus toutes les 5 s, sur un thread de fond →
        // ne bloque JAMAIS le tick du monitoring (c'était la cause du lag).
        private static void EnsureWattsFetch()
        {
            if (_gpuWattsTime != default && (DateTime.UtcNow - _gpuWattsTime).TotalMilliseconds < 5000) return;
            if (System.Threading.Interlocked.CompareExchange(ref _gpuWattsFetching, 1, 0) != 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                        "--query-gpu=power.draw --format=csv,noheader,nounits")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    });
                    if (p != null)
                    {
                        var line = p.StandardOutput.ReadLine();
                        p.WaitForExit(4000);
                        if (double.TryParse((line ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var w))
                            _gpuWattsCached = w;
                    }
                }
                catch { }
                finally
                {
                    _gpuWattsTime = DateTime.UtcNow;
                    System.Threading.Interlocked.Exchange(ref _gpuWattsFetching, 0);
                }
            });
        }

        // Remplit s via nvidia-smi. Renvoie false si nvidia-smi absent / muet (pas de carte Nvidia active).
        private static bool TryNvidiaSmi(MonSnapshot s)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,clocks.gr " +
                    "--format=csv,noheader,nounits")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                if (p == null) return false;

                var line = p.StandardOutput.ReadLine();
                p.WaitForExit(4000);
                if (string.IsNullOrWhiteSpace(line)) return false;

                var parts = line.Split(',');
                if (parts.Length < 7) return false;

                double D(string v)
                {
                    v = v.Trim();
                    return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
                }

                s.GpuName        = parts[0].Trim();
                s.GpuUsage       = D(parts[1]);
                s.GpuVramUsedMB  = D(parts[2]);
                s.GpuVramTotalMB = D(parts[3]);
                s.GpuTemp        = D(parts[4]);
                s.GpuWatts       = D(parts[5]);
                s.GpuMHz         = D(parts[6]);
                s.GpuOk          = true;
                return true;
            }
            catch { return false; }
        }

        // ── Énumération des GPU physiques (stable → mise en cache au 1er appel) ────
        private enum GpuVendor { Nvidia, Amd, Intel, Other }

        private sealed class GpuInfo
        {
            public string    Name = "";
            public GpuVendor  Vendor;
            public bool       IsIntegrated;
            public double     DedicatedVramMB;
        }

        private static List<GpuInfo>? _gpuList;

        private static List<GpuInfo> EnumGpus()
        {
            if (_gpuList != null) return _gpuList;
            var list = new List<GpuInfo>();
            try
            {
                // VRAM dédiée réelle par carte (le WMI AdapterRAM est plafonné à 4 Go → inutilisable).
                var vram = ReadDedicatedVram();   // DriverDesc -> Mo

                using var q = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject o in q.Get())
                {
                    var name = o["Name"] as string ?? "";
                    var pnp  = (o["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                    o.Dispose();

                    // On ne garde QUE les vraies cartes PCI → exclut les adaptateurs virtuels
                    // (Parsec, Bureau à distance, Microsoft Basic Render… en ROOT\… ou autre).
                    if (!pnp.StartsWith("PCI\\")) continue;

                    var vendor =
                        pnp.Contains("VEN_10DE") ? GpuVendor.Nvidia :
                        pnp.Contains("VEN_1002") ? GpuVendor.Amd    :
                        pnp.Contains("VEN_8086") ? GpuVendor.Intel  : GpuVendor.Other;

                    double dedMb = 0;
                    foreach (var kv in vram)
                        if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { dedMb = kv.Value; break; }

                    // Intégré : Intel (toujours), ou AMD/autre sans VRAM dédiée. Nvidia = toujours dédiée.
                    bool igp = vendor == GpuVendor.Intel ||
                               (vendor != GpuVendor.Nvidia && dedMb < 512);

                    list.Add(new GpuInfo { Name = name, Vendor = vendor, IsIntegrated = igp, DedicatedVramMB = dedMb });
                }
            }
            catch { }
            _gpuList = list;
            return list;
        }

        // Lit la VRAM dédiée réelle (qwMemorySize) dans le registre, indexée par DriverDesc.
        private static Dictionary<string, double> ReadDedicatedVram()
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (classKey == null) return map;
                foreach (var sub in classKey.GetSubKeyNames())
                {
                    if (sub.Length != 4) continue;   // 0000, 0001, …
                    using var k = classKey.OpenSubKey(sub);
                    var desc = k?.GetValue("DriverDesc") as string;
                    var memObj = k?.GetValue("HardwareInformation.qwMemorySize");
                    if (desc == null || memObj == null) continue;
                    try
                    {
                        double mb = Convert.ToInt64(memObj) / (1024.0 * 1024.0);
                        if (mb > 0) map[desc] = mb;
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        // Choisit la carte à afficher : dédiée (plus grosse VRAM) en priorité, sinon l'IGP.
        private static GpuInfo? SelectDisplayGpu()
        {
            var gpus = EnumGpus();
            if (gpus.Count == 0) return null;

            GpuInfo? best = null;
            foreach (var g in gpus)
                if (!g.IsIntegrated && (best == null || g.DedicatedVramMB > best.DedicatedVramMB))
                    best = g;
            return best ?? gpus[0];   // aucune dédiée → premier IGP
        }

        /// <summary>
        /// True s'il faut VERROUILLER l'onglet Nvidia : au moins un GPU énuméré, et aucun n'est Nvidia.
        /// Fail-open : si l'énumération est vide (échec WMI), on NE verrouille PAS (on ne prive pas
        /// un utilisateur de l'onglet à tort).
        /// </summary>
        public static bool ShouldLockNvidiaTab()
        {
            var gpus = EnumGpus();
            if (gpus.Count == 0) return false;
            foreach (var g in gpus)
                if (g.Vendor == GpuVendor.Nvidia) return false;
            return true;
        }

        // Usage % de l'adaptateur le plus sollicité, via la classe WMI INVARIANTE (pas de piège de
        // localisation §5). On groupe par luid puis on prend le moteur le plus chargé (façon
        // Gestionnaire des tâches). Best-effort : 0 si indispo. Searcher RÉUTILISÉ (v1.4.3).
        private static readonly ManagementObjectSearcher _gpuEngineQuery = new("root\\CIMV2",
            "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

        private static double GpuEngineBusiestUsage()
        {
            try
            {
                // luid -> (engtype -> somme d'utilisation)
                var perLuid = new Dictionary<string, Dictionary<string, double>>();
                using var coll = _gpuEngineQuery.Get();
                foreach (ManagementObject o in coll)
                {
                    var name = o["Name"] as string;
                    double u  = o["UtilizationPercentage"] != null ? Convert.ToDouble(o["UtilizationPercentage"]) : 0;
                    o.Dispose();
                    if (name == null || u <= 0) continue;

                    var (luid, eng) = ParseEngineInstance(name);
                    if (luid == null) continue;
                    if (!perLuid.TryGetValue(luid, out var engMap)) { engMap = new(); perLuid[luid] = engMap; }
                    engMap[eng] = engMap.TryGetValue(eng, out var cur) ? cur + u : u;
                }

                double best = 0;
                foreach (var engMap in perLuid.Values)
                {
                    double luidUsage = 0;
                    foreach (var v in engMap.Values) if (v > luidUsage) luidUsage = v;   // moteur le + chargé
                    if (luidUsage > best) best = luidUsage;
                }
                return Math.Min(100, best);
            }
            catch { return 0; }
        }

        // "pid_24532_luid_0x00000000_0x00017A9C_phys_0_eng_3_engtype_VideoDecode" -> (luid, engtype)
        private static (string? luid, string eng) ParseEngineInstance(string name)
        {
            var t = name.Split('_');
            string? luid = null; string eng = "?";
            for (int i = 0; i < t.Length - 1; i++)
            {
                if (t[i] == "luid" && i + 2 < t.Length) luid = t[i + 1] + "_" + t[i + 2];
                if (t[i] == "engtype" && i + 1 < t.Length) eng = t[i + 1];
            }
            return (luid, eng);
        }
    }
}
