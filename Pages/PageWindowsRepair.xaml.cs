using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Optimisation_Tool.Helpers;

namespace Optimisation_Tool.Pages
{
    public partial class PageWindowsRepair : UserControl
    {
        private sealed record RepairStep(
            string Title,
            string Command,
            string Arguments,
            Border Dot,
            TextBlock Num,
            TextBlock Text,
            Grid Bar,
            TextBlock Percent,
            string IdleText,
            string RunningText,
            TimeSpan Timeout);

        private sealed record CommandResult(
            int ExitCode,
            string StdOut,
            string StdErr,
            bool Cancelled = false,
            bool TimedOut = false);

        private enum StepVisual { Idle, Running, Done, Skipped, Failed }

        private readonly MainWindow _main;
        private readonly StringBuilder _raw = new();
        private readonly StringBuilder _report = new();
        private bool _running;
        private CancellationTokenSource? _repairCts;
        private Process? _activeProcess;

        private static readonly Regex PercentRx = new(@"(\d{1,3}(?:[,.]\d+)?)\s*%", RegexOptions.Compiled);
        private static readonly Encoding OutputEncoding;

        static PageWindowsRepair()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // DISM/SFC écrivent généralement avec le codepage console OEM.
            // Le codepage ANSI transforme certains accents en caractères cassés.
            OutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }

        public PageWindowsRepair(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            ResetUi();
        }

        private async void BtnRunRepair_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                BtnRunRepair.IsEnabled = false;
                BtnRunRepair.Content = "ANNULATION...";
                _repairCts?.Cancel();
                return;
            }
            await RunRepairAsync();
        }

        public void CancelForAppShutdown()
        {
            _repairCts?.Cancel();
            TryKillActiveProcess();
        }

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(_report.Length > 0 ? _report.ToString() : TxtLog.Text);
            BtnCopyReport.Content = "RAPPORT COPI\u00c9";
        }

        private async Task RunRepairAsync()
        {
            _running = true;
            _repairCts = new CancellationTokenSource();
            BtnRunRepair.IsEnabled = true;
            BtnRunRepair.Content = "ANNULER";
            BtnCopyReport.IsEnabled = false;
            BtnCopyReport.Content = "COPIER LE RAPPORT";

            try
            {
            _raw.Clear();
            _report.Clear();
            ResetUi();

            var started = DateTime.Now;
            AddReportLine($"R\u00e9paration Windows d\u00e9marr\u00e9e : {started:yyyy-MM-dd HH:mm:ss}");
            AddReportLine("Ordre : DISM CheckHealth -> DISM ScanHealth -> RestoreHealth si n\u00e9cessaire -> SFC /scannow");
            AddReportLine("");

            SetSummary("Tweakly v\u00e9rifie d'abord si une r\u00e9paration compl\u00e8te est n\u00e9cessaire.");
            AddDetail("D\u00e9marrage de la r\u00e9paration Windows.");

            var all = Steps();
            bool failed = false;
            bool restoreNeeded = true;

            for (int i = 0; i < all.Count; i++)
            {
                var step = all[i];

                if (IsRestoreHealth(step) && !restoreNeeded)
                {
                    SetStepVisual(step, StepVisual.Skipped, i + 1);
                    step.Text.Text = "Image Windows propre : RestoreHealth n'est pas lanc\u00e9.";
                    AddDetail("RestoreHealth saut\u00e9 : l'image Windows est propre.");
                    AddReportLine("DISM RestoreHealth : saut\u00e9, aucune corruption \u00e0 r\u00e9parer.");
                    SetTotalProgress(((i + 1) * 100.0) / all.Count);
                    continue;
                }

                SetStepVisual(step, StepVisual.Running, i + 1);
                SetCurrent(step.Title, step.RunningText);
                SetSummary(step.RunningText);
                AddDetail($"{step.Title} lanc\u00e9.");
                SetStepProgress(step, 0);
                SetTotalProgress(i * 100.0 / all.Count);

                var sw = Stopwatch.StartNew();
                var result = await RunCommandAsync(step, line =>
                {
                    _raw.AppendLine(line);
                    var pct = TryReadPercent(line);
                    if (pct.HasValue)
                    {
                        SetStepProgress(step, pct.Value);
                        SetTotalProgress(((i + pct.Value / 100.0) * 100.0) / all.Count);
                    }
                }, _repairCts.Token);
                sw.Stop();

                if (!result.Cancelled && !result.TimedOut)
                {
                    SetStepProgress(step, 100);
                    SetTotalProgress(((i + 1) * 100.0) / all.Count);
                }

                string human = ExplainResult(step, result, sw.Elapsed);
                step.Text.Text = human;
                SetSummary(human);

                if (result.ExitCode == 0)
                {
                    SetStepVisual(step, StepVisual.Done, i + 1);
                    AddDetail(human);
                    AddReportLine($"{step.Title} : {human}");
                }
                else
                {
                    SetStepVisual(step, StepVisual.Failed, i + 1);
                    failed = true;
                    SetCurrent(
                        result.Cancelled ? "R\u00e9paration annul\u00e9e" : "R\u00e9paration interrompue",
                        human);
                    AddDetail(human);
                    AddReportLine($"{step.Title} : {human}");
                    break;
                }

                if (IsScanHealth(step))
                    restoreNeeded = NeedsRestoreHealth(result);
            }

            if (!failed)
            {
                SetCurrent("R\u00e9paration termin\u00e9e", "Windows a termin\u00e9 DISM et SFC. Red\u00e9marre le PC si une \u00e9tape a r\u00e9par\u00e9 quelque chose.");
                SetSummary("Termin\u00e9. Si une \u00e9tape a r\u00e9par\u00e9 quelque chose, red\u00e9marre le PC avant de refaire un diagnostic.");
                AddDetail($"Termin\u00e9 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                AddReportLine("");
                AddReportLine($"Termin\u00e9 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }

            _main.Log("R\u00e9paration Windows : s\u00e9quence DISM/SFC termin\u00e9e.");
            }
            catch (Exception ex)
            {
                AppLog.Error("R\u00e9paration Windows : s\u00e9quence", ex);
                SetCurrent("R\u00e9paration interrompue", "Une erreur interne a interrompu l'op\u00e9ration.");
                SetSummary($"La r\u00e9paration a \u00e9t\u00e9 interrompue : {ex.Message}");
                AddReportLine($"Erreur interne : {ex.Message}");
            }
            finally
            {
                TryKillActiveProcess();
                _repairCts.Dispose();
                _repairCts = null;
                _running = false;
                BtnRunRepair.Content = "R\u00c9PARER WINDOWS";
                BtnRunRepair.IsEnabled = true;
                BtnCopyReport.IsEnabled = _report.Length > 0;
            }
        }

        private List<RepairStep> Steps() => new()
        {
            new("\u00c9tat connu de Windows",
                "dism.exe",
                "/Online /English /Cleanup-Image /CheckHealth",
                DotCheck,
                NumCheck,
                TxtCheck,
                StepBarCheck,
                TxtPctCheck,
                "V\u00e9rification rapide. Si Windows n'a rien d\u00e9j\u00e0 marqu\u00e9 comme ab\u00eem\u00e9, cette \u00e9tape peut durer seulement quelques s.",
                "Windows fait une v\u00e9rification rapide de l'\u00e9tat connu de son image syst\u00e8me.",
                TimeSpan.FromMinutes(5)),

            new("Analyse de l'image Windows",
                "dism.exe",
                "/Online /English /Cleanup-Image /ScanHealth",
                DotScan,
                NumScan,
                TxtScan,
                StepBarScan,
                TxtPctScan,
                "Analyse compl\u00e8te du magasin de composants utilis\u00e9 pour r\u00e9parer Windows.",
                "Windows analyse compl\u00e8tement le magasin de composants. Cette \u00e9tape peut prendre plusieurs min.",
                TimeSpan.FromMinutes(45)),

            new("R\u00e9paration de l'image Windows",
                "dism.exe",
                "/Online /English /Cleanup-Image /RestoreHealth",
                DotRestore,
                NumRestore,
                TxtRestore,
                StepBarRestore,
                TxtPctRestore,
                "R\u00e9cup\u00e8re les fichiers propres n\u00e9cessaires puis r\u00e9pare l'image Windows.",
                "Windows r\u00e9pare son image syst\u00e8me avant de lancer SFC.",
                TimeSpan.FromMinutes(120)),

            new("V\u00e9rification des fichiers syst\u00e8me",
                "sfc.exe",
                "/scannow",
                DotSfc,
                NumSfc,
                TxtSfc,
                StepBarSfc,
                TxtPctSfc,
                "V\u00e9rifie les fichiers syst\u00e8me et remplace ceux qui sont corrompus.",
                "Windows v\u00e9rifie et r\u00e9pare les fichiers syst\u00e8me avec SFC.",
                TimeSpan.FromMinutes(60))
        };

        private async Task<CommandResult> RunCommandAsync(
            RepairStep step,
            Action<string> onLine,
            CancellationToken cancellationToken)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            if (cancellationToken.IsCancellationRequested)
                return new CommandResult(-1, "", "", Cancelled: true);

            var psi = new ProcessStartInfo
            {
                FileName = step.Command,
                Arguments = step.Arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            using var timeoutCts = new CancellationTokenSource(step.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            Task outTask = Task.CompletedTask;
            Task errTask = Task.CompletedTask;

            try
            {
                p.Start();
                _activeProcess = p;
                outTask = ReadConsoleStreamAsync(p.StandardOutput.BaseStream, output, onLine);
                errTask = ReadConsoleStreamAsync(p.StandardError.BaseStream, error, onLine);
                await p.WaitForExitAsync(linkedCts.Token);
                await Task.WhenAll(outTask, errTask);
                return new CommandResult(p.ExitCode, output.ToString(), error.ToString());
            }
            catch (OperationCanceledException)
            {
                bool cancelled = cancellationToken.IsCancellationRequested;
                TryKillProcess(p);
                await WaitForExitBoundedAsync(p, TimeSpan.FromSeconds(5));
                // L'annulation a deja ete traitee : on laisse au maximum 2 s aux
                // flux du processus tue pour se vider avant de rendre la main.
                await Task.WhenAny(
                    Task.WhenAll(outTask, errTask),
                    Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
                AppLog.Write(cancelled
                    ? $"Réparation Windows : {step.Title} annulée par l'utilisateur."
                    : $"Réparation Windows : {step.Title} arrêtée après le délai de {FormatDuration(step.Timeout)}.");
                return new CommandResult(
                    -1,
                    output.ToString(),
                    error.ToString(),
                    Cancelled: cancelled,
                    TimedOut: !cancelled);
            }
            catch (Exception ex)
            {
                return new CommandResult(-1, output.ToString(), ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_activeProcess, p)) _activeProcess = null;
            }
        }

        private void TryKillActiveProcess()
        {
            var process = _activeProcess;
            if (process != null) TryKillProcess(process);
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Réparation Windows : arrêt du processus", ex);
            }
        }

        private static async Task WaitForExitBoundedAsync(Process process, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }

        private async Task ReadConsoleStreamAsync(Stream stream, StringBuilder sink, Action<string> onLine)
        {
            var buffer = new byte[4096];
            var chars = new char[4096];
            var decoder = OutputEncoding.GetDecoder();
            var pending = new StringBuilder();

            while (true)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (n <= 0) break;

                int charCount = decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
                string chunk = new(chars, 0, charCount);
                sink.Append(chunk);
                pending.Append(chunk);
                FlushCompleteLines(pending, onLine);
            }

            int tail = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
            if (tail > 0)
            {
                string chunk = new(chars, 0, tail);
                sink.Append(chunk);
                pending.Append(chunk);
            }

            string last = CleanConsoleLine(pending.ToString());
            if (last.Length > 0)
                Dispatcher.Invoke(() => onLine(last));
        }

        private static void FlushCompleteLines(StringBuilder pending, Action<string> onLine)
        {
            string text = pending.ToString().Replace('\r', '\n');
            var lines = text.Split('\n');
            pending.Clear();

            for (int i = 0; i < lines.Length - 1; i++)
            {
                string line = CleanConsoleLine(lines[i]);
                if (line.Length > 0)
                    Application.Current.Dispatcher.Invoke(() => onLine(line));
            }

            pending.Append(lines[^1]);
        }

        private string ExplainResult(RepairStep step, CommandResult result, TimeSpan elapsed)
        {
            string raw = Normalize(result.StdOut + "\n" + result.StdErr);
            string duration = FormatDuration(elapsed);

            if (result.Cancelled)
                return $"{step.Title} annul\u00e9e apr\u00e8s {duration}.";
            if (result.TimedOut)
                return $"{step.Title} interrompue : d\u00e9lai maximal de {FormatDuration(step.Timeout)} d\u00e9pass\u00e9.";
            if (result.ExitCode != 0)
                return $"{step.Title} a \u00e9chou\u00e9 en {duration}. Code erreur : {result.ExitCode}.";

            if (step.Command.Equals("sfc.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsAny(raw, "did not find any integrity violations", "aucune violation d'integrite", "aucune violation d.integrite"))
                    return $"SFC n'a trouv\u00e9 aucune corruption de fichiers syst\u00e8me. Dur\u00e9e : {duration}.";
                if (ContainsAny(raw, "successfully repaired", "a repare"))
                    return $"SFC a trouv\u00e9 et r\u00e9par\u00e9 des fichiers syst\u00e8me. Dur\u00e9e : {duration}. Red\u00e9marrage conseill\u00e9.";
                if (ContainsAny(raw, "unable to fix", "n'a pas pu reparer"))
                    return $"SFC a trouv\u00e9 des fichiers corrompus mais n'a pas tout r\u00e9par\u00e9. Dur\u00e9e : {duration}. Consulte CBS.log.";

                return $"SFC termin\u00e9 sans erreur bloquante. Dur\u00e9e : {duration}.";
            }

            var health = DismOutputClassifier.Parse(raw);
            if (health == DismHealthState.Clean)
                return $"{step.Title} : aucune corruption d\u00e9tect\u00e9e. Dur\u00e9e : {duration}.";
            if (health == DismHealthState.NonRepairable)
                return $"{step.Title} : le magasin de composants est endommag\u00e9 et DISM ne le d\u00e9clare pas r\u00e9parable. Dur\u00e9e : {duration}.";
            if (health == DismHealthState.Repairable)
                return $"{step.Title} : corruption r\u00e9parable d\u00e9tect\u00e9e. Dur\u00e9e : {duration}.";
            if (IsRestoreHealth(step)
                && (health == DismHealthState.Repaired
                    || ContainsAny(raw, "the operation completed successfully", "operation completed successfully")))
                return $"{step.Title} : r\u00e9paration termin\u00e9e correctement. Dur\u00e9e : {duration}.";

            return $"{step.Title} termin\u00e9 sans erreur bloquante. Dur\u00e9e : {duration}.";
        }

        private static bool NeedsRestoreHealth(CommandResult result)
        {
            string raw = Normalize(result.StdOut + "\n" + result.StdErr);

            return DismOutputClassifier.NeedsRestoreHealth(result.ExitCode, result.StdOut, result.StdErr);
        }

        private static bool IsScanHealth(RepairStep step)
            => step.Arguments.Contains("/ScanHealth", StringComparison.OrdinalIgnoreCase);

        private static bool IsRestoreHealth(RepairStep step)
            => step.Arguments.Contains("/RestoreHealth", StringComparison.OrdinalIgnoreCase);

        private void ResetUi()
        {
            var steps = Steps();
            for (int i = 0; i < steps.Count; i++)
            {
                SetStepVisual(steps[i], StepVisual.Idle, i + 1);
                steps[i].Text.Text = steps[i].IdleText;
                SetStepProgress(steps[i], 0);
            }

            SetCurrent("Pr\u00eat", "Clique sur R\u00c9PARER WINDOWS pour lancer DISM puis SFC dans le bon ordre.");
            SetSummary("Aucune r\u00e9paration lanc\u00e9e.");
            TxtLog.Text = "Le suivi appara\u00eetra ici pendant l'op\u00e9ration.";
            SetTotalProgress(0);
        }

        private void SetStepVisual(RepairStep step, StepVisual visual, int number)
        {
            switch (visual)
            {
                case StepVisual.Running:
                    step.Dot.SetResourceReference(Border.BackgroundProperty, "ThTabSel");
                    step.Dot.SetResourceReference(Border.BorderBrushProperty, "ThAccentIcon");
                    step.Num.Text = number.ToString(CultureInfo.InvariantCulture);
                    step.Num.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
                    break;
                case StepVisual.Done:
                    step.Dot.SetResourceReference(Border.BackgroundProperty, "ThOk");
                    step.Dot.SetResourceReference(Border.BorderBrushProperty, "ThOk");
                    step.Num.Text = "\u2713";
                    step.Num.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
                    break;
                case StepVisual.Skipped:
                    step.Dot.SetResourceReference(Border.BackgroundProperty, "ThTrack");
                    step.Dot.SetResourceReference(Border.BorderBrushProperty, "ThAccentIcon");
                    step.Num.Text = "-";
                    step.Num.SetResourceReference(TextBlock.ForegroundProperty, "ThAccentIcon");
                    break;
                case StepVisual.Failed:
                    step.Dot.SetResourceReference(Border.BackgroundProperty, "ThCrit");
                    step.Dot.SetResourceReference(Border.BorderBrushProperty, "ThCrit");
                    step.Num.Text = "!";
                    step.Num.SetResourceReference(TextBlock.ForegroundProperty, "ThWhite");
                    break;
                default:
                    step.Dot.SetResourceReference(Border.BackgroundProperty, "ThTrack");
                    step.Dot.SetResourceReference(Border.BorderBrushProperty, "ThBorder");
                    step.Num.Text = number.ToString(CultureInfo.InvariantCulture);
                    step.Num.SetResourceReference(TextBlock.ForegroundProperty, "ThTextDim");
                    break;
            }
        }

        private void SetCurrent(string title, string detail)
        {
            TxtCurrentTitle.Text = title;
            TxtCurrentDetail.Text = detail;
        }

        private void SetSummary(string text) => TxtSummary.Text = text;

        private void SetStepProgress(RepairStep step, double pct)
        {
            pct = ClampPct(pct);
            step.Bar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            step.Bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            step.Percent.Text = $"{pct:F0} %";
        }

        private void SetTotalProgress(double pct)
        {
            pct = ClampPct(pct);
            TotalBar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            TotalBar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            TxtTotalPct.Text = $"{pct:F0} %";
        }

        private void AddDetail(string line)
        {
            if (IsDisplayNoise(line)) return;

            TxtLog.Text = TxtLog.Text.StartsWith("Le suivi ", StringComparison.Ordinal)
                ? line
                : TxtLog.Text + Environment.NewLine + line;
            Dispatcher.BeginInvoke(() => LogScroll.ScrollToEnd());
        }

        private void AddReportLine(string line) => _report.AppendLine(line);

        private static double? TryReadPercent(string line)
        {
            var m = PercentRx.Match(line);
            if (!m.Success) return null;
            var s = m.Groups[1].Value.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                ? ClampPct(pct)
                : null;
        }

        private static double ClampPct(double pct) => Math.Max(0, Math.Min(100, pct));

        private static string Normalize(string text)
            => text.Normalize(NormalizationForm.FormD)
                .ToLowerInvariant()
                .Replace("\u0301", "")
                .Replace("\u0300", "")
                .Replace("\u0302", "")
                .Replace("\u0308", "");

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (var n in needles)
                if (text.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string CleanConsoleLine(string line)
            => line.Replace("\0", "").Trim();

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 120)
                return $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";
            if (t.TotalMinutes >= 1)
                return $"{(int)t.TotalMinutes} min {t.Seconds:00}s";
            return "1 s";
        }

        private static bool IsDisplayNoise(string line)
        {
            string s = CleanConsoleLine(line);
            return s.Length == 1 && char.IsLetter(s[0]);
        }

    }
}
