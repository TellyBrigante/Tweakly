using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Lecture de la température CPU via LibreHardwareMonitorLib, qui s'appuie sur le pilote
    /// PawnIO (ring-0). Tout est best-effort et entièrement blindé : si le pilote est absent,
    /// PawnIOLib introuvable, ou l'app non élevée → renvoie null, JAMAIS d'exception (pour ne
    /// pas casser le sampler du monitoring ni le démarrage de l'app).
    ///
    /// • Opt-in : <see cref="Enabled"/> est false par défaut → Read() ne fait rien tant que
    ///   l'utilisateur n'a pas activé la fonction (et que le pilote n'est pas en place).
    /// • Le Computer LHM est ouvert UNE fois (coûteux), puis seulement rafraîchi à chaque lecture.
    /// </summary>
    public static class CpuTemperature
    {
        private static readonly object _lock = new();
        private static Computer?  _computer;
        private static IHardware? _cpu;
        private static bool       _opened;
        private static bool       _failed;   // évite de retenter en boucle si l'ouverture a planté

        /// <summary>Active/désactive la lecture. Mettre à false ferme LHM proprement.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value) Close();
            }
        }
        private static bool _enabled;

        /// <summary>Ouvre LHM (idempotent). Renvoie true si un CPU exploitable est trouvé.</summary>
        private static bool Open()
        {
            if (_opened) return _cpu != null;
            if (_failed) return false;
            try
            {
                // PawnIOLib.dll (user-mode) est livré dans data\drivers\ → on ajoute ce dossier au
                // chemin de recherche des DLL natives pour que le P/Invoke de LHM la trouve.
                if (!SetDllDirectory(PathLayout.DataDrv))
                {
                    AppLog.ErrorOnce("cpu-temperature-dll-directory",
                        "Température CPU : dossier PawnIO non enregistré",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                var c = new Computer { IsCpuEnabled = true };
                c.Open();
                _computer = c;
                foreach (var h in c.Hardware)
                    if (h.HardwareType == HardwareType.Cpu) { _cpu = h; break; }
                _opened = true;
                return _cpu != null;
            }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("cpu-temperature-open", "Température CPU : initialisation impossible", ex);
                _failed = true;
                CloseCore(resetFailure: false);
                return false;
            }
        }

        /// <summary>Ferme LHM et réinitialise l'état (réouverture possible ensuite).</summary>
        public static void Close()
        {
            lock (_lock)
            {
                CloseCore(resetFailure: true);
            }
        }

        private static void CloseCore(bool resetFailure)
        {
            try { _computer?.Close(); }
            catch (Exception ex)
            {
                AppLog.ErrorOnce("cpu-temperature-close", "Température CPU : fermeture de la sonde impossible", ex);
            }
            _computer = null;
            _cpu      = null;
            _opened   = false;
            if (resetFailure) _failed = false;
        }

        /// <summary>
        /// Température CPU (°C) : « CPU Package » si dispo, sinon « Core Max », sinon le plus
        /// chaud des cœurs. Renvoie null si désactivé ou indisponible. Best-effort, ne lève jamais.
        /// </summary>
        public static double? Read()
        {
            if (!_enabled) return null;
            lock (_lock)
            {
                try
                {
                    if (_cpu == null && !Open()) return null;
                    _cpu!.Update();

                    double? pkg = null, coreMax = null, anyCore = null;
                    foreach (var s in _cpu.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                        var v    = s.Value.Value;
                        var name = s.Name ?? "";
                        if (name.IndexOf("Package",  StringComparison.OrdinalIgnoreCase) >= 0) pkg = v;
                        else if (name.IndexOf("Core Max", StringComparison.OrdinalIgnoreCase) >= 0) coreMax = v;
                        else if (name.IndexOf("Core",  StringComparison.OrdinalIgnoreCase) >= 0 &&
                                 name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) < 0)
                            anyCore = anyCore.HasValue ? Math.Max(anyCore.Value, v) : v;
                    }

                    var t = pkg ?? coreMax ?? anyCore;
                    return (t is > 0 and < 130) ? t : null;   // garde-fou contre les valeurs aberrantes
                }
                catch (Exception ex)
                {
                    AppLog.ErrorOnce("cpu-temperature-read", "Température CPU : lecture impossible", ex);
                    return null;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
