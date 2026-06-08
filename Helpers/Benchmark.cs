using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Un résultat de bench complet (sous-scores 0-100 + chiffres bruts).
    /// </summary>
    public sealed class BenchmarkResult
    {
        public DateTime Timestamp;
        public int      CpuScore;      // 0-100
        public int      SysScore;      // 0-100 (réactivité système : timer jitter inverse)
        public int      NetScore;      // 0-100 (ping médian + jitter + perte)
        public int      TotalScore;    // 0-100 (moyenne pondérée)

        // Chiffres bruts (affichés sous le score, pour la transparence)
        public double CpuMonoMops;     // millions d'ops/sec mono-thread
        public double CpuMultiMops;    // millions d'ops/sec multi-thread
        public double SysJitterMicroSec; // µs (plus bas = mieux)
        public double NetPingMs;       // ms (médiane)
        public double NetJitterMs;     // ms
        public double NetLossPct;      // %

        // Contexte
        public string  AppVersion    = "";
        public string  CpuName       = "";
        public int     CpuThreads;
        public bool    Noisy;          // bruit système détecté pendant le bench
        public string  Note           = "";   // libre (ex. « après tweaks CPU »)

        // Référence externe (base CPU livrée). Null si CPU non répertorié → on retombe sur réf perso.
        public bool    HasNominalRef;            // true si le CPU est dans la base
        public string  NominalRefModel = "";    // libellé canonique trouvé
        public double  NominalMonoMops;         // Mio/s nominal mono pour ce modèle
        public double  NominalMultiMops;        // Mio/s nominal multi pour ce modèle
        public int     NominalPctMono;          // mesuré / nominal * 100 (mono)
        public int     NominalPctMulti;         // mesuré / nominal * 100 (multi)
    }

    /// <summary>
    /// Moteur de bench LOCAL et reproductible. Aucune dépendance, aucun driver, aucune mesure noyau.
    /// 3 sondes :
    ///   • CPU       : SHA256 d'un buffer fixe, mono puis multi-thread → ops/s.
    ///   • SYSTÈME   : jitter d'un sleep court répété sur thread haute priorité → réactivité OS.
    ///   • RÉSEAU   : 30 pings espacés vers 1.1.1.1 → médiane + jitter + perte.
    /// Durées : ~5s mono + ~10s multi + ~10s timer + ~30s réseau = ~55s.
    /// </summary>
    public static class Benchmark
    {
        /// <summary>Sous-étape en cours, pour l'UI (progression honnête).</summary>
        public enum Phase { Idle, CpuMono, CpuMulti, System, Network, Done }

        public static async Task<BenchmarkResult> RunAsync(
            IProgress<(Phase phase, double pct)>? progress = null,
            CancellationToken ct = default)
        {
            var r = new BenchmarkResult { Timestamp = DateTime.Now };

            // Détection de bruit (CPU > 25% sur la première seconde = baseline trop occupé)
            r.Noisy = await DetectNoiseAsync(ct);

            progress?.Report((Phase.CpuMono, 0));
            (r.CpuMonoMops, r.CpuMultiMops, r.CpuThreads, r.CpuName) =
                await RunCpuAsync(progress, ct);

            progress?.Report((Phase.System, 0));
            r.SysJitterMicroSec = await RunSystemAsync(progress, ct);

            progress?.Report((Phase.Network, 0));
            (r.NetPingMs, r.NetJitterMs, r.NetLossPct) = await RunNetworkAsync(progress, ct);

            // ── Scores 0-100 ─────────────────────────────────────────────────────
            // CPU : on tente d'abord la référence EXTERNE (base CPU livrée) → score = % du nominal.
            //       Sinon (CPU non répertorié) → fallback sur la référence personnelle auto-calibrée
            //       (1er bench = 100). Évolution attendue : les CPU inconnus deviendront rares au fil
            //       des MAJ de la base.
            double cpuComposite = r.CpuMonoMops * 0.3 + r.CpuMultiMops * 0.7;
            var nominal = CpuReference.Lookup(r.CpuName);
            if (nominal.Found)
            {
                r.HasNominalRef    = true;
                r.NominalRefModel  = nominal.MatchedModel;
                r.NominalMonoMops  = nominal.NominalMono;
                r.NominalMultiMops = nominal.NominalMulti;
                r.NominalPctMono   = nominal.NominalMono  > 0 ? (int)Math.Round(r.CpuMonoMops  / nominal.NominalMono  * 100) : 0;
                r.NominalPctMulti  = nominal.NominalMulti > 0 ? (int)Math.Round(r.CpuMultiMops / nominal.NominalMulti * 100) : 0;
                double nominalComposite = nominal.NominalMono * 0.3 + nominal.NominalMulti * 0.7;
                r.CpuScore = ScoreVsReference(cpuComposite, nominalComposite);
            }
            else
            {
                r.CpuScore = ScoreVsReference(cpuComposite, BenchmarkReference.GetCpu(r.CpuName, cpuComposite));
            }

            // SYS : 100 ≈ jitter < 200 µs (timer haute précision actif) ; 0 ≈ jitter > 5000 µs (système lent)
            r.SysScore   = ScoreInverse(r.SysJitterMicroSec, best: 200, worst: 5000);
            // NET : 100 ≈ ping < 15ms + jitter < 3ms + loss 0%
            int pScore = ScoreInverse(r.NetPingMs,   best: 15, worst: 150);
            int jScore = ScoreInverse(r.NetJitterMs, best: 3,  worst: 30);
            int lScore = ScoreInverse(r.NetLossPct,  best: 0,  worst: 10);
            r.NetScore  = (int)Math.Round(pScore * 0.5 + jScore * 0.3 + lScore * 0.2);
            // Total : pondération du « ressenti perf » utilisateur
            r.TotalScore = (int)Math.Round(r.CpuScore * 0.45 + r.SysScore * 0.35 + r.NetScore * 0.20);

            r.AppVersion = Pages.PageReglages.AppVersion;
            progress?.Report((Phase.Done, 100));
            return r;
        }

        // ── CPU : SHA512 d'un buffer fixe, mono puis multi-thread ────────────
        // SHA512 (pas SHA256) car SHA256 bénéficie de l'accélération matérielle SHA-NI (Intel/AMD
        // récents) → mesure le silicium dédié, pas la perf CPU généraliste. SHA512 reste un calcul
        // ALU/registre pur, comparable de machine à machine et sensible aux tweaks (plan d'alim,
        // throttling, etc.).
        private static async Task<(double mono, double multi, int threads, string name)>
            RunCpuAsync(IProgress<(Phase, double)>? prog, CancellationToken ct)
        {
            const int  bufSize  = 64 * 1024;
            const long monoMs   = 5000;
            const long multiMs  = 10000;
            int threads = Environment.ProcessorCount;
            string cpuName = SafeCpuName();

            // Mono-thread
            double monoOps = await Task.Run(() =>
            {
                var buf = new byte[bufSize];
                new Random(42).NextBytes(buf);
                using var sha = SHA512.Create();
                var sw = Stopwatch.StartNew();
                long n = 0;
                while (sw.ElapsedMilliseconds < monoMs)
                {
                    if (ct.IsCancellationRequested) break;
                    sha.ComputeHash(buf);
                    n++;
                    if ((n & 0xFFF) == 0) prog?.Report((Phase.CpuMono, sw.ElapsedMilliseconds * 100.0 / monoMs));
                }
                sw.Stop();
                return n * (bufSize / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds;   // Mio/s ≈ Mops/s
            }, ct);

            prog?.Report((Phase.CpuMulti, 0));

            // Multi-thread : tous les cœurs en parallèle, somme des ops
            double multiOps = await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                long total = 0;
                var tasks = new Task[threads];
                for (int t = 0; t < threads; t++)
                {
                    tasks[t] = Task.Run(() =>
                    {
                        var buf = new byte[bufSize];
                        new Random(42).NextBytes(buf);
                        using var sha = SHA512.Create();
                        long n = 0;
                        while (sw.ElapsedMilliseconds < multiMs)
                        {
                            if (ct.IsCancellationRequested) break;
                            sha.ComputeHash(buf);
                            n++;
                        }
                        Interlocked.Add(ref total, n);
                    }, ct);
                }
                while (sw.ElapsedMilliseconds < multiMs && !Task.WhenAll(tasks).IsCompleted)
                {
                    Thread.Sleep(150);
                    prog?.Report((Phase.CpuMulti, sw.ElapsedMilliseconds * 100.0 / multiMs));
                    if (ct.IsCancellationRequested) break;
                }
                Task.WhenAll(tasks).GetAwaiter().GetResult();
                sw.Stop();
                return total * (bufSize / (1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds;
            }, ct);

            return (monoOps, multiOps, threads, cpuName);
        }

        // ── SYSTÈME : jitter d'un timer haute précision en boucle ──────────────
        // Proxy de la « réactivité OS » (capte l'effet des tweaks plan d'alim / HVCI /
        // Power Throttling / SystemResponsiveness). Pas la VRAIE DPC latency, mais sensible
        // aux mêmes facteurs et 100% userspace.
        // IMPORTANT : Thread.Sleep(1) ne respecte PAS la milliseconde sur Windows par défaut
        // (timer kernel à 15.6 ms). On force la résolution timer à 1 ms via timeBeginPeriod(1),
        // sinon on mesure la résolution timer et pas la réactivité système.
        private static async Task<double> RunSystemAsync(
            IProgress<(Phase, double)>? prog, CancellationToken ct)
        {
            const int  durMs    = 10000;
            const int  targetMs = 1;
            const int  warmup   = 100;
            long freq = Stopwatch.Frequency;

            return await Task.Run(() =>
            {
                bool periodSet = false;
                try { if (timeBeginPeriod(1) == 0) periodSet = true; } catch { }

                var th = Thread.CurrentThread;
                var oldPri = th.Priority;
                try { th.Priority = ThreadPriority.Highest; } catch { }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var samples = new System.Collections.Generic.List<double>(50000);
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
                        if ((i & 0x1FF) == 0) prog?.Report((Phase.System, sw.ElapsedMilliseconds * 100.0 / durMs));
                    }
                    // 95e percentile du dépassement = « réactivité du pire cas »
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
        }

        [DllImport("winmm.dll", SetLastError = true)] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", SetLastError = true)] private static extern uint timeEndPeriod(uint uPeriod);

        // ── RÉSEAU : 30 pings espacés (300ms) vers 1.1.1.1 ────────────────────
        private static async Task<(double ping, double jitter, double loss)>
            RunNetworkAsync(IProgress<(Phase, double)>? prog, CancellationToken ct)
        {
            const int  count = 30;
            var samples = new System.Collections.Generic.List<long>(count);
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
            double jitter = 0; int n = 0;
            for (int i = 1; i < samples.Count; i++) { jitter += Math.Abs(samples[i] - samples[i-1]); n++; }
            jitter = n > 0 ? jitter / n : 0;
            double lossPct = lost * 100.0 / count;
            return (median, jitter, lossPct);
        }

        // ── Détection de bruit système (1 s d'observation) ────────────────────
        private static async Task<bool> DetectNoiseAsync(CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var p = Process.GetCurrentProcess();
                var t0 = p.TotalProcessorTime;
                await Task.Delay(1000, ct);
                var t1 = Process.GetCurrentProcess().TotalProcessorTime;
                // Si l'app elle-même prend > 5% sur 1s alors qu'on dort, c'est qu'on a du bruit ailleurs
                // qui force notre thread pool. Heuristique simple, à affiner.
                double pct = (t1 - t0).TotalMilliseconds / (1000.0 * Environment.ProcessorCount) * 100;
                return pct > 5;
            }
            catch { return false; }
        }

        // ── Helpers de scoring (échelle linéaire bornée 0-100) ────────────────
        private static int ScoreInverse(double value, double best, double worst)
        {
            if (worst <= best) return 0;
            double t = (worst - value) / (worst - best);
            return Math.Max(0, Math.Min(100, (int)Math.Round(t * 100)));
        }

        /// <summary>
        /// Score CPU RELATIF à la référence personnelle de la machine. 100 = la référence (1er bench),
        /// >100 = mieux que la référence, &lt;100 = moins bien. Borné [0..150] pour éviter les chiffres
        /// abusifs en cas de référence pourrie (ex. mesure faite avec Chrome ouvert).
        /// </summary>
        private static int ScoreVsReference(double value, double reference)
        {
            if (reference <= 0) return 100;
            int s = (int)Math.Round(value / reference * 100.0);
            return Math.Max(0, Math.Min(150, s));
        }

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
