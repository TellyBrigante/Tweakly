using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Resultat de bench Tweakly Score v2 (gaming-oriented, multifactoriel).
    ///
    /// 7 sondes mesurees, chacune x3 runs avec mediane retenue (la mediane ecarte
    /// les pics, contrairement a la moyenne) :
    ///   CPU         - SingleThread (Mandelbrot 1024x1024 = FPU+branch+int mixed)
    ///                 -> proxy game loop / AI / logique de jeu
    ///                 MultiThread  (Mandelbrot parallele sur min(8, cores) threads)
    ///                 -> proxy moteur jeu (les jeux scalent rarement >12t)
    ///                 MemAccess    (pointer-chase aleatoire sur 16 Mo)
    ///                 -> proxy acces structures de jeu (world state, entites)
    ///   SYSTEME     - FrameStability (Sleep(16.67ms) x600, jitter)
    ///                 -> proxy frame time stability 60 FPS
    ///                 InputLatency   (Sleep(1ms), jitter 95p)
    ///                 -> proxy reactivite scheduler / input
    ///   RAM         - Bandwidth   (STREAM Copy + Triad sur tableau 64 Mo)
    ///                 -> bande passante reelle RAM (GB/s)
    ///                 Latency     (pointer-chase random sur 16 Mo)
    ///                 -> latence acces aleatoire (ns)
    ///   RESEAU      - Ping mediane + jitter + perte vers 1.1.1.1 (inchange)
    ///
    /// Variance control : 3 runs/sonde, mediane, warmup 500ms, priority High,
    /// detection outliers (badge "instable" si ecart >30% entre runs).
    /// Scoring global = moyenne geometrique (penalise les goulots).
    /// </summary>
    public sealed class BenchmarkResult
    {
        public DateTime Timestamp;

        // ── Scores 0-150 (100 = perf nominale attendue pour ce CPU) ──────────
        public int      CpuScore;       // moyenne geometrique des 3 sondes CPU
        public int      SysScore;       // moyenne des 2 sondes systeme
        public int      RamScore;       // moyenne des 2 sondes RAM
        public int      NetScore;       // pondere ping+jitter+perte
        public int      TotalScore;     // global, moyenne geometrique ponderee

        // ── Sous-scores CPU ──────────────────────────────────────────────────
        public int      CpuSingleScore;
        public int      CpuMultiScore;
        public int      CpuMemScore;

        // ── Sous-scores SYS ──────────────────────────────────────────────────
        public int      SysFrameScore;
        public int      SysInputScore;

        // ── Sous-scores RAM ──────────────────────────────────────────────────
        public int      RamBandwidthScore;
        public int      RamLatencyScore;

        // ── Mesures brutes (affichees pour transparence) ─────────────────────
        public double CpuSingleMops;
        public double CpuMultiMops;
        public double CpuMemMops;
        public double SysFrameJitterMs;
        public double SysInputJitterUs;
        public double RamBandwidthGBs;
        public double RamLatencyNs;
        public double NetPingMs;
        public double NetJitterMs;
        public double NetLossPct;

        // ── Compat affichage v1 (ne pas casser ce qui lit l'ancien format) ──
        public double CpuMonoMops    => CpuSingleMops;
        public double SysJitterMicroSec => SysInputJitterUs;

        // ── Contexte ─────────────────────────────────────────────────────────
        public string  AppVersion    = "";
        public string  CpuName       = "";
        public int     CpuThreads;
        public bool    Noisy;
        public bool    Unstable;       // ecart >30% entre runs sur une sonde
        public string  Note           = "";

        // ── Reference externe (table CPU livree) ─────────────────────────────
        public bool    HasNominalRef;
        public string  NominalRefModel = "";
        public string  CpuTier         = "";   // ex: "Enthusiast desktop 2024-2026"

        // Comparatif (CPUs voisins du meme tier + leurs scores attendus)
        public List<(string Name, int Score)> Neighbors = new();

        // Compat (anciens champs encore lus par PageBenchmark)
        public double  NominalMonoMops;
        public double  NominalMultiMops;
        public int     NominalPctMono;
        public int     NominalPctMulti;
    }

    public static class Benchmark
    {
        public enum Phase
        {
            Idle, CpuSingle, CpuMulti, CpuMem,
            SysFrame, SysInput, RamBand, RamLat,
            Network, Done,
            // alias compat v1
            CpuMono = CpuSingle, System = SysInput
        }

        public static async Task<BenchmarkResult> RunAsync(
            IProgress<(Phase phase, double pct)>? progress = null,
            CancellationToken ct = default)
        {
            var r = new BenchmarkResult { Timestamp = DateTime.Now };
            r.CpuName    = SafeCpuName();
            r.CpuThreads = Environment.ProcessorCount;
            r.Noisy      = await DetectNoiseAsync(ct);

            // Priorite process High pendant tout le bench (max les chances d'avoir
            // une mesure clean, sans aller en RealTime qui peut figer la machine)
            var proc = Process.GetCurrentProcess();
            var oldPrio = proc.PriorityClass;
            try { proc.PriorityClass = ProcessPriorityClass.High; } catch { }

            try
            {
                // ── CPU : 3 sondes x 3 runs ──
                progress?.Report((Phase.CpuSingle, 0));
                var (single, singleUnstable) = await Run3xAsync(
                    () => CpuSingleThreadAsync(ct), progress, Phase.CpuSingle, ct);
                r.CpuSingleMops = single;
                if (singleUnstable) r.Unstable = true;

                progress?.Report((Phase.CpuMulti, 0));
                var (multi, multiUnstable) = await Run3xAsync(
                    () => CpuMultiThreadAsync(ct), progress, Phase.CpuMulti, ct);
                r.CpuMultiMops = multi;
                if (multiUnstable) r.Unstable = true;

                progress?.Report((Phase.CpuMem, 0));
                var (mem, memUnstable) = await Run3xAsync(
                    () => CpuMemAccessAsync(ct), progress, Phase.CpuMem, ct);
                r.CpuMemMops = mem;
                if (memUnstable) r.Unstable = true;

                // ── Systeme : 2 sondes x 3 runs ──
                progress?.Report((Phase.SysFrame, 0));
                var (frame, _) = await Run3xAsync(
                    () => SysFrameStabilityAsync(ct), progress, Phase.SysFrame, ct);
                r.SysFrameJitterMs = frame;

                progress?.Report((Phase.SysInput, 0));
                var (input, _) = await Run3xAsync(
                    () => SysInputLatencyAsync(ct), progress, Phase.SysInput, ct);
                r.SysInputJitterUs = input;

                // ── RAM : 2 sondes x 3 runs ──
                progress?.Report((Phase.RamBand, 0));
                var (band, _) = await Run3xAsync(
                    () => RamBandwidthAsync(ct), progress, Phase.RamBand, ct);
                r.RamBandwidthGBs = band;

                progress?.Report((Phase.RamLat, 0));
                var (lat, _) = await Run3xAsync(
                    () => RamLatencyAsync(ct), progress, Phase.RamLat, ct);
                r.RamLatencyNs = lat;

                // ── Reseau : 30 pings (inchange) ──
                progress?.Report((Phase.Network, 0));
                (r.NetPingMs, r.NetJitterMs, r.NetLossPct) = await RunNetworkAsync(progress, ct);
            }
            finally
            {
                try { proc.PriorityClass = oldPrio; } catch { }
            }

            // ── SCORING ──────────────────────────────────────────────────────
            // Look up CPU dans la table de reference (cpu_reference.json enrichie).
            // Chaque CPU a des scores ATTENDUS pour chaque sonde (calibres sur le
            // Core Ultra 7 265K = 100). On compare mesure / attendu * 100.
            var nominal = CpuReference.LookupV2(r.CpuName, r.CpuThreads);
            r.HasNominalRef   = nominal.Found;
            r.NominalRefModel = nominal.MatchedModel;
            r.CpuTier         = nominal.Tier;
            r.Neighbors       = nominal.Neighbors;

            // CPU : si on a une ref, on compare; sinon score relatif autocalibre
            if (nominal.Found)
            {
                r.CpuSingleScore = ScoreVs(r.CpuSingleMops, nominal.ExpectedSingleMops);
                r.CpuMultiScore  = ScoreVs(r.CpuMultiMops,  nominal.ExpectedMultiMops);
                r.CpuMemScore    = ScoreVs(r.CpuMemMops,    nominal.ExpectedMemMops);
                r.NominalMonoMops  = nominal.ExpectedSingleMops;
                r.NominalMultiMops = nominal.ExpectedMultiMops;
                r.NominalPctMono   = r.CpuSingleScore;
                r.NominalPctMulti  = r.CpuMultiScore;
            }
            else
            {
                // Fallback : pas de ref pour ce CPU. Score brut autocalibre via BenchmarkReference.
                r.CpuSingleScore = ScoreVs(r.CpuSingleMops, BenchmarkReference.GetCpu(r.CpuName + ":single", r.CpuSingleMops));
                r.CpuMultiScore  = ScoreVs(r.CpuMultiMops,  BenchmarkReference.GetCpu(r.CpuName + ":multi",  r.CpuMultiMops));
                r.CpuMemScore    = ScoreVs(r.CpuMemMops,    BenchmarkReference.GetCpu(r.CpuName + ":mem",    r.CpuMemMops));
            }

            // Moyenne geometrique des 3 sondes CPU (penalise les goulots)
            r.CpuScore = GeoMean3(r.CpuSingleScore, r.CpuMultiScore, r.CpuMemScore);

            // Systeme : scores inverses (moins de jitter = mieux)
            // Frame jitter : excellent < 1ms, mauvais > 8ms
            r.SysFrameScore = ScoreInverse(r.SysFrameJitterMs, best: 1, worst: 8);
            // Input jitter : excellent < 200us, mauvais > 5000us (inchange)
            r.SysInputScore = ScoreInverse(r.SysInputJitterUs, best: 200, worst: 5000);
            r.SysScore      = (int)Math.Round((r.SysFrameScore + r.SysInputScore) / 2.0);

            // RAM : bandwidth direct, latency inverse
            // Bandwidth : 4 GB/s = mauvais (DDR3 single-channel),
            //             16 GB/s = excellent (DDR5 dual high freq, plafond C# sans SIMD).
            // Note : la mesure single-thread C# plafonne ~15 GB/s meme sur DDR5 64Go
            //        dual channel 6400 MHz, parce qu'on n'utilise pas AVX-512. C'est OK,
            //        on mesure le throughput pratique d'une appli normale, pas du STREAM
            //        avec instructions vectorielles.
            r.RamBandwidthScore = ScoreDirect(r.RamBandwidthGBs, worst: 4, best: 16);
            // Latency : excellent < 60ns, mauvais > 150ns
            r.RamLatencyScore   = ScoreInverse(r.RamLatencyNs, best: 60, worst: 150);
            r.RamScore          = (int)Math.Round((r.RamBandwidthScore + r.RamLatencyScore) / 2.0);

            // Reseau (inchange)
            int pScore = ScoreInverse(r.NetPingMs,   best: 15, worst: 150);
            int jScore = ScoreInverse(r.NetJitterMs, best: 3,  worst: 30);
            int lScore = ScoreInverse(r.NetLossPct,  best: 0,  worst: 10);
            r.NetScore = (int)Math.Round(pScore * 0.5 + jScore * 0.3 + lScore * 0.2);

            // TOTAL : moyenne GEOMETRIQUE ponderee CPU 50% / SYS 20% / RAM 20% / NET 10%
            r.TotalScore = GeoMeanWeighted(
                (r.CpuScore, 0.50),
                (r.SysScore, 0.20),
                (r.RamScore, 0.20),
                (r.NetScore, 0.10));

            r.AppVersion = Pages.PageReglages.AppVersion;
            progress?.Report((Phase.Done, 100));
            return r;
        }

        // ── Runner generique : 3 runs + mediane + detection outliers ────────
        private static async Task<(double median, bool unstable)> Run3xAsync(
            Func<Task<double>> probe, IProgress<(Phase, double)>? prog, Phase phase, CancellationToken ct)
        {
            const int runs = 3;
            var vals = new List<double>(runs);
            for (int i = 0; i < runs; i++)
            {
                if (ct.IsCancellationRequested) break;
                // Warmup 200ms entre les runs pour eviter les caches chauds qui faussent
                await Task.Delay(200, ct);
                vals.Add(await probe());
                prog?.Report((phase, (i + 1) * 100.0 / runs));
            }
            if (vals.Count == 0) return (0, false);
            vals.Sort();
            double median = vals[vals.Count / 2];
            // Outlier detection : ecart entre min et max > 30% du median
            bool unstable = vals.Count >= 3
                && (vals[^1] - vals[0]) / Math.Max(1e-9, median) > 0.30;
            return (median, unstable);
        }

        // ══════ SONDES CPU ═══════════════════════════════════════════════════

        // CPU single-thread : Mandelbrot 512x512 (FPU + branches + int conditional)
        // Le rendu Mandelbrot est un workload classique gaming-like : maths flottants
        // intensifs + branches conditionnelles + integer mixed.
        private static Task<double> CpuSingleThreadAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int width   = 512;
                const int height  = 512;
                const int durMs   = 3000;
                var sw = Stopwatch.StartNew();
                long pixels = 0;
                while (sw.ElapsedMilliseconds < durMs)
                {
                    if (ct.IsCancellationRequested) break;
                    pixels += MandelbrotPixels(width, height, 100);
                }
                sw.Stop();
                // Mops/s = millions de pixels evalues par seconde
                return pixels / 1_000_000.0 / sw.Elapsed.TotalSeconds;
            }, ct);

        // CPU multi-thread : Mandelbrot sur min(8, cores) threads.
        // Limite a 8 car les jeux scalent rarement au-dela (au-dela = CPU server
        // ou benchmark synthetique, pas representatif gaming).
        private static Task<double> CpuMultiThreadAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                int threads = Math.Min(8, Environment.ProcessorCount);
                const int width  = 512;
                const int height = 512;
                const int durMs  = 5000;
                long totalPixels = 0;
                var sw = Stopwatch.StartNew();
                var tasks = new Task[threads];
                for (int t = 0; t < threads; t++)
                {
                    tasks[t] = Task.Run(() =>
                    {
                        long local = 0;
                        while (sw.ElapsedMilliseconds < durMs)
                        {
                            if (ct.IsCancellationRequested) break;
                            local += MandelbrotPixels(width, height, 100);
                        }
                        Interlocked.Add(ref totalPixels, local);
                    }, ct);
                }
                Task.WhenAll(tasks).GetAwaiter().GetResult();
                sw.Stop();
                return totalPixels / 1_000_000.0 / sw.Elapsed.TotalSeconds;
            }, ct);

        // Mandelbrot : calcule N pixels, retourne le compte.
        // Discard du resultat (on mesure le throughput, pas l'image).
        private static long MandelbrotPixels(int width, int height, int maxIter)
        {
            long ops = 0;
            for (int py = 0; py < height; py++)
            {
                double y0 = (py / (double)height) * 2.0 - 1.0;
                for (int px = 0; px < width; px++)
                {
                    double x0 = (px / (double)width) * 3.0 - 2.0;
                    double x = 0, y = 0;
                    int iter = 0;
                    while (x * x + y * y <= 4 && iter < maxIter)
                    {
                        double xt = x * x - y * y + x0;
                        y = 2 * x * y + y0;
                        x = xt;
                        iter++;
                    }
                    ops += iter;
                }
            }
            return ops;
        }

        // CPU memory access : pointer-chase aleatoire sur 16 Mo (depasse le L2)
        // Proxy pour les acces RAM dans une game loop (entites, world state).
        // Le pattern aleatoire empeche le prefetcher de masquer la latence.
        private static Task<double> CpuMemAccessAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int sizeBytes = 16 * 1024 * 1024;
                const int count     = sizeBytes / sizeof(int);
                var arr = new int[count];
                // Construit un cycle aleatoire (permutation): arr[i] = next index
                var rng = new Random(42);
                var indices = Enumerable.Range(0, count).OrderBy(_ => rng.Next()).ToArray();
                for (int i = 0; i < count - 1; i++) arr[indices[i]] = indices[i + 1];
                arr[indices[count - 1]] = indices[0];

                const int durMs = 3000;
                var sw = Stopwatch.StartNew();
                long hops = 0;
                int idx = 0;
                while (sw.ElapsedMilliseconds < durMs)
                {
                    if (ct.IsCancellationRequested) break;
                    // 1 million de pointer-chase par bloc, sinon le check du sw dominerait
                    for (int i = 0; i < 1_000_000; i++) idx = arr[idx];
                    hops += 1_000_000;
                }
                sw.Stop();
                // Discard idx pour ne pas que le JIT optimise tout
                GC.KeepAlive(idx);
                return hops / 1_000_000.0 / sw.Elapsed.TotalSeconds;
            }, ct);

        // ══════ SONDES SYSTEME ═══════════════════════════════════════════════

        // Frame stability 60 Hz : Sleep(16.67ms) x600 fois. Mesure le 95p de jitter
        // en ms. Proxy direct du frame time stability a 60 FPS dans un jeu.
        private static Task<double> SysFrameStabilityAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int targetMs   = 16;   // ~60 Hz
                const int iterations = 400;
                bool periodSet = false;
                try { if (timeBeginPeriod(1) == 0) periodSet = true; } catch { }
                var th = Thread.CurrentThread;
                var oldPri = th.Priority;
                try { th.Priority = ThreadPriority.Highest; } catch { }
                try
                {
                    long freq = Stopwatch.Frequency;
                    var samples = new List<double>(iterations);
                    long last = Stopwatch.GetTimestamp();
                    for (int i = 0; i < iterations; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        Thread.Sleep(targetMs);
                        long now = Stopwatch.GetTimestamp();
                        double elapsedMs = (now - last) * 1000.0 / freq;
                        last = now;
                        if (i > 20) samples.Add(Math.Max(0, elapsedMs - targetMs));
                    }
                    if (samples.Count == 0) return 0.0;
                    samples.Sort();
                    return samples[(int)(samples.Count * 0.95)];
                }
                finally
                {
                    try { th.Priority = oldPri; } catch { }
                    if (periodSet) { try { timeEndPeriod(1); } catch { } }
                }
            }, ct);

        // Input latency : sonde existante v1, gardee. Sleep(1ms) en boucle,
        // 95p du depassement (us). Proxy reactivite scheduler.
        private static Task<double> SysInputLatencyAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int durMs = 3000;
                const int targetMs = 1;
                const int warmup = 100;
                long freq = Stopwatch.Frequency;
                bool periodSet = false;
                try { if (timeBeginPeriod(1) == 0) periodSet = true; } catch { }
                var th = Thread.CurrentThread;
                var oldPri = th.Priority;
                try { th.Priority = ThreadPriority.Highest; } catch { }
                try
                {
                    var sw = Stopwatch.StartNew();
                    var samples = new List<double>(16000);
                    long last = Stopwatch.GetTimestamp();
                    int i = 0;
                    while (sw.ElapsedMilliseconds < durMs)
                    {
                        if (ct.IsCancellationRequested) break;
                        Thread.Sleep(targetMs);
                        long now = Stopwatch.GetTimestamp();
                        double elapsedUs = (now - last) * 1_000_000.0 / freq;
                        last = now;
                        if (++i > warmup) samples.Add(Math.Max(0, elapsedUs - targetMs * 1000.0));
                    }
                    if (samples.Count == 0) return 0.0;
                    samples.Sort();
                    return samples[(int)(samples.Count * 0.95)];
                }
                finally
                {
                    try { th.Priority = oldPri; } catch { }
                    if (periodSet) { try { timeEndPeriod(1); } catch { } }
                }
            }, ct);

        // ══════ SONDES RAM ═══════════════════════════════════════════════════

        // STREAM-like Copy + Triad sur 64 Mo (assez gros pour saturer les caches).
        // Returns GB/s.
        private static Task<double> RamBandwidthAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int n = 8 * 1024 * 1024;   // 8M doubles = 64 Mo
                var a = new double[n];
                var b = new double[n];
                var c = new double[n];
                var rng = new Random(42);
                for (int i = 0; i < n; i++) { a[i] = rng.NextDouble(); b[i] = rng.NextDouble(); }

                // Warmup
                for (int i = 0; i < n; i++) c[i] = a[i] + b[i] * 2.0;

                const int iterations = 8;
                var sw = Stopwatch.StartNew();
                double sum = 0;
                for (int it = 0; it < iterations; it++)
                {
                    if (ct.IsCancellationRequested) break;
                    // Copy: c = a (8 byte / element, 1 write + 1 read = 16 byte/elt)
                    for (int i = 0; i < n; i++) c[i] = a[i];
                    // Triad: a = b + 2*c (3 read + 1 write = 32 byte/elt)
                    for (int i = 0; i < n; i++) a[i] = b[i] + 2.0 * c[i];
                    sum += a[0]; // empeche elision JIT
                }
                sw.Stop();
                GC.KeepAlive(sum);
                // Bytes transferes : iterations * (Copy 16B + Triad 32B) * n
                double totalBytes = (double)iterations * (16 + 32) * n;
                return totalBytes / sw.Elapsed.TotalSeconds / 1e9; // GB/s
            }, ct);

        // Memory latency : pointer-chase aleatoire sur 16 Mo (similaire au CPU mem
        // mais on convertit en ns/access au lieu de Mops/s).
        private static Task<double> RamLatencyAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int sizeBytes = 16 * 1024 * 1024;
                const int count = sizeBytes / sizeof(int);
                var arr = new int[count];
                var rng = new Random(42);
                var indices = Enumerable.Range(0, count).OrderBy(_ => rng.Next()).ToArray();
                for (int i = 0; i < count - 1; i++) arr[indices[i]] = indices[i + 1];
                arr[indices[count - 1]] = indices[0];

                const int hops = 50_000_000;
                int idx = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < hops; i++) idx = arr[idx];
                sw.Stop();
                GC.KeepAlive(idx);
                // ns / hop
                return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / hops;
            }, ct);

        // ══════ RESEAU (inchange v1) ═══════════════════════════════════════
        private static async Task<(double ping, double jitter, double loss)>
            RunNetworkAsync(IProgress<(Phase, double)>? prog, CancellationToken ct)
        {
            const int count = 30;
            var samples = new List<long>(count);
            int lost = 0;
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var p = new Ping();
                    var reply = await p.SendPingAsync("1.1.1.1", 1000);
                    if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime);
                    else lost++;
                }
                catch { lost++; }
                prog?.Report((Phase.Network, (i + 1) * 100.0 / count));
                await Task.Delay(200, ct);
            }
            if (samples.Count == 0) return (-1, -1, 100);
            samples.Sort();
            double median = samples[samples.Count / 2];
            double jitter = 0; int nj = 0;
            for (int i = 1; i < samples.Count; i++) { jitter += Math.Abs(samples[i] - samples[i-1]); nj++; }
            jitter = nj > 0 ? jitter / nj : 0;
            return (median, jitter, lost * 100.0 / count);
        }

        // ══════ Detection bruit systeme (inchange v1) ════════════════════════
        private static async Task<bool> DetectNoiseAsync(CancellationToken ct)
        {
            try
            {
                var p = Process.GetCurrentProcess();
                var t0 = p.TotalProcessorTime;
                await Task.Delay(1000, ct);
                var t1 = Process.GetCurrentProcess().TotalProcessorTime;
                double pct = (t1 - t0).TotalMilliseconds / (1000.0 * Environment.ProcessorCount) * 100;
                return pct > 5;
            }
            catch { return false; }
        }

        // ══════ HELPERS scoring ══════════════════════════════════════════════

        /// <summary>Score = mesure / reference * 100, borne [0..150].</summary>
        private static int ScoreVs(double value, double reference)
        {
            if (reference <= 0) return 100;
            int s = (int)Math.Round(value / reference * 100.0);
            return Math.Max(0, Math.Min(150, s));
        }

        /// <summary>Score 0-100 ou 'best' donne 100 et 'worst' donne 0 (inverse).</summary>
        private static int ScoreInverse(double value, double best, double worst)
        {
            if (worst <= best) return 0;
            double t = (worst - value) / (worst - best);
            return Math.Max(0, Math.Min(100, (int)Math.Round(t * 100)));
        }

        /// <summary>Score 0-100 ou 'best' donne 100 et 'worst' donne 0 (direct, grand=mieux).</summary>
        private static int ScoreDirect(double value, double worst, double best)
        {
            if (best <= worst) return 0;
            double t = (value - worst) / (best - worst);
            return Math.Max(0, Math.Min(100, (int)Math.Round(t * 100)));
        }

        /// <summary>Moyenne geometrique de 3 scores (penalise les goulots vs arithmetique).</summary>
        private static int GeoMean3(int a, int b, int c)
        {
            double prod = Math.Max(1, a) * Math.Max(1, b) * Math.Max(1, c);
            return (int)Math.Round(Math.Pow(prod, 1.0 / 3.0));
        }

        /// <summary>Moyenne geometrique ponderee.</summary>
        private static int GeoMeanWeighted(params (int score, double weight)[] parts)
        {
            double logSum = 0, weightSum = 0;
            foreach (var (s, w) in parts)
            {
                logSum    += w * Math.Log(Math.Max(1, s));
                weightSum += w;
            }
            return (int)Math.Round(Math.Exp(logSum / weightSum));
        }

        [DllImport("winmm.dll", SetLastError = true)] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", SetLastError = true)] private static extern uint timeEndPeriod(uint uPeriod);

        private static string SafeCpuName()
        {
            try
            {
                using var q = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    var n = (o["Name"]?.ToString() ?? "").Trim();
                    o.Dispose();
                    return n;
                }
            }
            catch { }
            return "";
        }
    }
}
