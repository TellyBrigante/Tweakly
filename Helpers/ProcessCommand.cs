using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Optimisation_Tool.Helpers
{
    public readonly record struct ProcessCommandResult(
        bool Started,
        bool TimedOut,
        int ExitCode,
        string Output,
        string Error)
    {
        public bool Success => Started && !TimedOut && ExitCode == 0;

        public string FailureDescription
        {
            get
            {
                if (!Started)
                    return Compact(string.IsNullOrWhiteSpace(Error) ? "processus non démarré" : Error);
                if (TimedOut)
                    return Compact(string.IsNullOrWhiteSpace(Error) ? "délai dépassé" : Error);
                if (!string.IsNullOrWhiteSpace(Error))
                    return Compact(Error);
                if (!string.IsNullOrWhiteSpace(Output))
                    return Compact(Output);
                return $"code {ExitCode}";
            }
        }

        private static string Compact(string value)
        {
            string compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length <= 500 ? compact : compact[..500] + "…";
        }
    }

    /// <summary>
    /// Exécute une commande sans shell avec un délai réel. Les deux flux sont drainés
    /// pendant l'exécution afin qu'un processus bavard ne puisse pas se bloquer sur un pipe plein.
    /// </summary>
    public static class ProcessCommand
    {
        public static ProcessCommandResult Run(string fileName, string arguments, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("Le nom du processus est vide.", nameof(fileName));
            if (timeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(fileName, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };

                if (!process.Start())
                    return new ProcessCommandResult(false, false, -1, "", "processus non démarré");

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    string killError = "";
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(5000))
                        {
                            killError = " Arrêt forcé impossible : le processus ne s'est pas arrêté sous 5 s.";
                        }
                    }
                    catch (Exception ex)
                    {
                        killError = " Arrêt forcé impossible : " + ex.Message;
                    }

                    Observe(stdout);
                    Observe(stderr);
                    return new ProcessCommandResult(
                        true,
                        true,
                        -1,
                        CompletedValue(stdout),
                        "délai dépassé." + killError);
                }

                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                return new ProcessCommandResult(
                    true,
                    false,
                    process.ExitCode,
                    stdout.Result,
                    stderr.Result.Trim());
            }
            catch (Exception ex)
            {
                return new ProcessCommandResult(false, false, -1, "", ex.Message);
            }
        }

        private static string CompletedValue(Task<string> task)
            => task.IsCompletedSuccessfully ? task.Result : "";

        private static void Observe(Task task)
        {
            if (task.IsFaulted)
            {
                _ = task.Exception;
                return;
            }
            if (task.IsCompleted) return;
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
