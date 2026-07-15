using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
        public double RamBandwidthGBs;   // = Read (métrique headline, comparable AIDA)
        public double RamReadGBs;
        public double RamWriteGBs;
        public double RamCopyGBs;
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
            try { proc.PriorityClass = ProcessPriorityClass.High; }
            catch (Exception ex) { AppLog.ErrorOnce("benchmark-process-priority", "Benchmark : priorité de processus inchangée", ex); }

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

                // ── RAM : bande passante (Read/Write/Copy comme AIDA) + latence ──
                // La sonde bande passante fait deja 6 repetitions internes (stable) → pas
                // besoin du wrapper 3x. Elle renvoie les 3 valeurs comme AIDA.
                progress?.Report((Phase.RamBand, 0));
                var (rd, wr, cp) = await RamBandwidthAllAsync(ct);
                r.RamReadGBs = rd; r.RamWriteGBs = wr; r.RamCopyGBs = cp;
                r.RamBandwidthGBs = rd;   // Read = métrique headline (comparable AIDA)
                progress?.Report((Phase.RamBand, 1));

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
                try { proc.PriorityClass = oldPrio; }
                catch (Exception ex) { AppLog.ErrorOnce("benchmark-process-priority-restore", "Benchmark : restauration de la priorité impossible", ex); }
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

            // RAM : bandwidth = READ multi-thread AVX2 (comparable AIDA), latency inverse.
            // Bareme recale sur la VRAIE bande passante Read : ~20 GB/s = DDR4 lente,
            // ~95 GB/s = DDR5 dual 6400 (mesure 265K ~88, AIDA ~99).
            r.RamBandwidthScore = ScoreDirect(r.RamBandwidthGBs, worst: 20, best: 95);
            // Latency : recalibre sur la VRAIE DRAM (buffer 256 Mo). Mesure managee
            // (bounds-checks → ~20-30 % au-dessus d'AIDA qui est en AVX) : un bon kit
            // DDR5 mesure ~100-115 ns ici (265K DDR5-6400 = ~110 ns). best 90 / worst 165.
            r.RamLatencyScore   = ScoreInverse(r.RamLatencyNs, best: 90, worst: 165);
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
                try { if (timeBeginPeriod(1) == 0) periodSet = true; }
                catch (Exception ex) { AppLog.ErrorOnce("benchmark-frame-timer-period", "Benchmark : précision du minuteur inchangée", ex); }
                var th = Thread.CurrentThread;
                var oldPri = th.Priority;
                try { th.Priority = ThreadPriority.Highest; }
                catch (Exception ex) { AppLog.ErrorOnce("benchmark-frame-thread-priority", "Benchmark : priorité du thread de mesure inchangée", ex); }
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
                    try { th.Priority = oldPri; }
                    catch (Exception ex) { AppLog.ErrorOnce("benchmark-frame-thread-priority-restore", "Benchmark : restauration de la priorité du thread impossible", ex); }
                    if (periodSet) EndTimerPeriod();
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
                try { if (timeBeginPeriod(1) == 0) periodSet = true; }
                catch (Exception ex) { AppLog.ErrorOnce("benchmark-input-timer-period", "Benchmark : précision du minuteur inchangée", ex); }
                var th = Thread.CurrentThread;
                var oldPri = th.Priority;
                try { th.Priority = ThreadPriority.Highest; }
                catch (Exception ex) { AppLog.ErrorOnce("benchmark-input-thread-priority", "Benchmark : priorité du thread de mesure inchangée", ex); }
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
                    try { th.Priority = oldPri; }
                    catch (Exception ex) { AppLog.ErrorOnce("benchmark-input-thread-priority-restore", "Benchmark : restauration de la priorité du thread impossible", ex); }
                    if (periodSet) EndTimerPeriod();
                }
            }, ct);

        private static void EndTimerPeriod()
        {
            try
            {
                uint result = timeEndPeriod(1);
                if (result != 0)
                    AppLog.Write($"Benchmark : timeEndPeriod(1) a échoué avec le code {result}.");
            }
            catch (Exception ex)
            {
                AppLog.Error("Benchmark : restauration de la résolution timer", ex);
            }
        }

        // ══════ SONDES RAM ═══════════════════════════════════════════════════

        // Bande passante mémoire façon AIDA64 : Read / Write / Copy, multi-thread, AVX2 +
        // stores NON-TEMPORELS (streaming, bypass cache write-allocate) + lecture déroulée
        // sur 4 accumulateurs. Buffers NATIFS alignés 32 o. Validé sur 265K DDR5-6400 :
        // Read ~88, Write ~81, Copy ~82 GB/s (AIDA Read=99 ; l'écart = asm hand-tuné).
        // Fallback boucle managée si AVX2 absent (CPU pré-2013).
        private static Task<(double read, double write, double copy)> RamBandwidthAllAsync(CancellationToken ct)
            => Task.Run<(double, double, double)>(() =>
            {
                const long BYTES = 256L * 1024 * 1024;          // 256 Mo/buffer (DRAM-bound)
                int threads = Math.Clamp(Environment.ProcessorCount, 4, 16);

                if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                {
                    double g = RamBandwidthManagedFallback(BYTES, threads, ct);
                    return (g, g, g);
                }

                nint rawS = Marshal.AllocHGlobal((nint)BYTES + 64);
                nint rawD = Marshal.AllocHGlobal((nint)BYTES + 64);
                try
                {
                    nint s = (rawS + 31) & ~(nint)31;
                    nint d = (rawD + 31) & ~(nint)31;
                    unsafe
                    {
                        System.Runtime.CompilerServices.Unsafe.InitBlock((void*)s, 1, (uint)BYTES);
                        System.Runtime.CompilerServices.Unsafe.InitBlock((void*)d, 2, (uint)BYTES);
                    }

                    // PIC (meilleure passe sur 6), pas la moyenne : la bande passante est
                    // une métrique de pic (comme AIDA), et ça immunise contre une passe
                    // ralentie par une charge transitoire (CPU chaud après le bench CPU,
                    // app de fond) — c'est ce qui donnait un « Read 45 » aberrant.
                    double Bench(double bytesPerRep, Action body)
                    {
                        body();                                  // warmup
                        double best = 0;
                        for (int i = 0; i < 6; i++)
                        {
                            var sw = Stopwatch.StartNew();
                            body();
                            sw.Stop();
                            double g = bytesPerRep / sw.Elapsed.TotalSeconds / 1e9;
                            if (g > best) best = g;
                        }
                        return best;
                    }

                    // LECTURE en ENTIER (Avx2.Add sur long) : addition 1 cycle, jamais le
                    // goulot → mesure PUREMENT memory-bound, insensible à la chauffe des
                    // cœurs (l'addition FP l'était, d'où le read < write absurde). Garantit
                    // aussi lecture ≥ écriture.
                    double read = Bench(BYTES, () => ParallelChunks(threads, BYTES, (lo, hi) =>
                    {
                        unsafe
                        {
                            byte* p = (byte*)s;
                            var a0 = Vector256<long>.Zero; var a1 = a0; var a2 = a0; var a3 = a0;
                            long i = lo;
                            for (; i + 128 <= hi; i += 128)
                            {
                                a0 = System.Runtime.Intrinsics.X86.Avx2.Add(a0, System.Runtime.Intrinsics.X86.Avx.LoadVector256((long*)(p + i)));
                                a1 = System.Runtime.Intrinsics.X86.Avx2.Add(a1, System.Runtime.Intrinsics.X86.Avx.LoadVector256((long*)(p + i + 32)));
                                a2 = System.Runtime.Intrinsics.X86.Avx2.Add(a2, System.Runtime.Intrinsics.X86.Avx.LoadVector256((long*)(p + i + 64)));
                                a3 = System.Runtime.Intrinsics.X86.Avx2.Add(a3, System.Runtime.Intrinsics.X86.Avx.LoadVector256((long*)(p + i + 96)));
                            }
                            var sm = System.Runtime.Intrinsics.X86.Avx2.Add(System.Runtime.Intrinsics.X86.Avx2.Add(a0, a1), System.Runtime.Intrinsics.X86.Avx2.Add(a2, a3));
                            if (sm.GetElement(0) == 123456789L) GC.KeepAlive(sm);
                        }
                    }));

                    var one = Vector256.Create(1.5);
                    double write = Bench(BYTES, () => ParallelChunks(threads, BYTES, (lo, hi) =>
                    {
                        unsafe
                        {
                            byte* p = (byte*)d;
                            for (long i = lo; i + 32 <= hi; i += 32)
                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(p + i), one);
                        }
                    }));

                    double copy = Bench(BYTES * 2, () => ParallelChunks(threads, BYTES, (lo, hi) =>
                    {
                        unsafe
                        {
                            byte* ps = (byte*)s; byte* pd = (byte*)d;
                            for (long i = lo; i + 32 <= hi; i += 32)
                                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pd + i),
                                    System.Runtime.Intrinsics.X86.Avx.LoadVector256((double*)(ps + i)));
                        }
                    }));

                    return (read, write, copy);
                }
                finally { Marshal.FreeHGlobal(rawS); Marshal.FreeHGlobal(rawD); }
            }, ct);

        /// <summary>Découpe [0,total) en blocs contigus de 64 o alignés, un par thread.</summary>
        private static void ParallelChunks(int threads, long total, Action<long, long> body)
            => Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                long lo = (total * t / threads) & ~63L;
                long hi = (total * (t + 1) / threads) & ~63L;
                body(lo, hi);
            });

        /// <summary>Fallback managé (sans AVX2) : STREAM copy mono-bloc multi-thread.</summary>
        private static double RamBandwidthManagedFallback(long bytes, int threads, CancellationToken ct)
        {
            int n = (int)(bytes / sizeof(double));
            var a = new double[n]; var b = new double[n];
            for (int i = 0; i < n; i++) a[i] = i;
            var sw = Stopwatch.StartNew();
            const int reps = 6;
            for (int r = 0; r < reps; r++)
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    int lo = (int)((long)n * t / threads), hi = (int)((long)n * (t + 1) / threads);
                    Array.Copy(a, lo, b, lo, hi - lo);
                });
            sw.Stop();
            return (double)bytes * 2 * reps / sw.Elapsed.TotalSeconds / 1e9;
        }

        // Memory latency : pointer-chase aleatoire sur 256 Mo.
        // ⚠️ CORRECTION (bug vecu) : l'ancien buffer de 16 Mo TENAIT DANS LE CACHE L3
        // (jusqu'a 36 Mo sur Arrow Lake) → on mesurait la latence du L3 (~34 ns), PAS la
        // DRAM. Mesure reelle validee sur 265K : ~70 ns a 16 Mo, ~110-120 ns des 64 Mo
        // (regime DRAM stable). 256 Mo garantit qu'on depasse tout cache, sur toute machine.
        private static Task<double> RamLatencyAsync(CancellationToken ct)
            => Task.Run(() =>
            {
                const int sizeBytes = 256 * 1024 * 1024;   // >> tout L3 → vraie DRAM
                const int count = sizeBytes / sizeof(int);
                var arr = new int[count];
                var rng = new Random(42);
                // ⚠️ Permutation via Fisher-Yates O(n) EN PLACE — surtout PAS OrderBy(random)
                // qui ferait un tri O(n log n) de 64 M éléments (dizaines de s + alloc géante).
                var indices = new int[count];
                for (int i = 0; i < count; i++) indices[i] = i;
                for (int i = count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (indices[i], indices[j]) = (indices[j], indices[i]);
                }
                for (int i = 0; i < count - 1; i++) arr[indices[i]] = indices[i + 1];
                arr[indices[count - 1]] = indices[0];

                int idx = 0;
                for (int i = 0; i < 2_000_000; i++) idx = arr[idx];   // warmup (TLB/caches froids)
                const int hops = 30_000_000;
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
            catch (Exception ex)
            {
                AppLog.ErrorOnce("benchmark-cpu-name", "Benchmark : nom du processeur indisponible", ex);
            }
            return "";
        }
    }
}
