using Optimisation_Tool.Helpers;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

var failures = new List<string>();
var checks = 0;

Check("PnP active les bits geres", 0x119,
    RegistryValueLogic.SetMaskedBits(0x101, 0x18, enabled: true));
Check("PnP restaure sans ecraser les autres bits", 0x101,
    RegistryValueLogic.SetMaskedBits(0x119, 0x18, enabled: false));
Check("Flags accessibilite preserves", 0x207,
    RegistryValueLogic.EnsureBits(0x203, 0x4));

var forced = RegistryValueLogic.GpuPriority(forced: true);
Check("GPU force - GPU Priority", 8, forced.GpuPriority);
Check("GPU force - Priority", 6, forced.Priority);
Check("GPU force - Scheduling", "High", forced.SchedulingCategory);
Check("GPU force - SFIO", "High", forced.SfioPriority);
Check("GPU force detecte", true, RegistryValueLogic.IsForcedGpuPriority(
    forced.GpuPriority, forced.Priority, forced.SchedulingCategory, forced.SfioPriority));

var defaults = RegistryValueLogic.GpuPriority(forced: false);
Check("GPU defaut - GPU Priority", 8, defaults.GpuPriority);
Check("GPU defaut - Priority", 2, defaults.Priority);
Check("GPU defaut - Scheduling", "Medium", defaults.SchedulingCategory);
Check("GPU defaut - SFIO", "Normal", defaults.SfioPriority);
Check("GPU defaut non force", false, RegistryValueLogic.IsForcedGpuPriority(
    defaults.GpuPriority, defaults.Priority, defaults.SchedulingCategory, defaults.SfioPriority));

const string directX = "SwapEffectUpgradeEnable=0;VRROptimizeEnable=1;";
var enabled = RegistryValueLogic.SetSemicolonValue(directX, "SwapEffectUpgradeEnable", "1");
Check("DirectX remplace la paire cible", true,
    RegistryValueLogic.HasSemicolonValue(enabled, "SwapEffectUpgradeEnable", "1"));
Check("DirectX preserve VRR", true,
    RegistryValueLogic.HasSemicolonValue(enabled, "VRROptimizeEnable", "1"));
Check("DirectX retire seulement la paire cible", "VRROptimizeEnable=1;",
    RegistryValueLogic.SetSemicolonValue(enabled, "SwapEffectUpgradeEnable", null));
Check<string?>("DirectX supprime la valeur devenue vide", null,
    RegistryValueLogic.SetSemicolonValue("SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable", null));

Check("SystemResponsiveness Windows", 20, RegistryValueLogic.SystemResponsivenessDefault);
Check("NetworkThrottlingIndex Windows", 10, RegistryValueLogic.NetworkThrottlingDefault);
Check("Updater - verification bornee", TimeSpan.FromSeconds(15), UpdateTransferPolicy.CheckTimeout);
Check("Updater - telechargement lent autorise", TimeSpan.FromMinutes(30), UpdateTransferPolicy.DownloadTimeout);

var decodeMethod = typeof(EventLogDecoder).GetMethod("Decode", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("EventLogDecoder.Decode introuvable.");

LogEntry DecodeEvent(string provider, int id, string raw = "", string rawFull = "")
    => (LogEntry)(decodeMethod.Invoke(null, new object[] { provider, id, raw, rawFull })
        ?? throw new InvalidOperationException($"Décodage nul pour {provider}/{id}."));

void CheckDecoded(
    string name,
    string provider,
    int id,
    string expectedTitle,
    LogSev expectedSeverity,
    string raw = "",
    string rawFull = "")
{
    var entry = DecodeEvent(provider, id, raw, rawFull);
    Check(name + " - reconnue", true, entry.Known);
    Check(name + " - gravité", expectedSeverity, entry.Sev);
    Check(name + " - titre", true,
        entry.Title.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase));
    Check(name + " - explication", true, !string.IsNullOrWhiteSpace(entry.What));
    Check(name + " - cause", true, !string.IsNullOrWhiteSpace(entry.Cause));
}

CheckDecoded("IPF Dynamic Tuning", "IPFUMDF", 17, "Intel Dynamic Tuning", LogSev.Warning,
    rawFull: "Error <pipe://dptf_participant> [ESIF_E_NOT_FOUND]");
CheckDecoded("LiveKernelEvent 141", "Windows Error Reporting", 1001, "LiveKernelEvent 141", LogSev.Serious,
    rawFull: "Nom d’événement du problème : LiveKernelEvent\nP1 : 141\nC:\\WINDOWS\\LiveKernelReports\\WATCHDOG\\WATCHDOG-test.dmp");
CheckDecoded("TDR NVIDIA", "Display", 4101, "NVIDIA", LogSev.Serious,
    rawFull: "Le pilote d’affichage nvlddmkm ne répondait plus.");
CheckDecoded("TDR AMD", "amdkmdag", 4101, "AMD", LogSev.Serious);

var appCrash = DecodeEvent("Application Error", 1000, "Application défaillante",
    "Application défaillante : game.exe, version 1.0\nModule défaillant : ntdll.dll");
Check("Application Error lit l’exécutable dans le message complet", true,
    appCrash.Title.Contains("game.exe", StringComparison.OrdinalIgnoreCase));
Check("Application Error lit le module dans le message complet", true,
    appCrash.What.Contains("ntdll.dll", StringComparison.OrdinalIgnoreCase));

CheckDecoded("Runtime .NET", ".NET Runtime", 1026, ".NET", LogSev.Warning);
foreach (int serviceId in new[] { 7000, 7009, 7011, 7031, 7034 })
    CheckDecoded($"Service Control Manager {serviceId}", "Service Control Manager", serviceId,
        "Service", LogSev.Warning);
CheckDecoded("VolSnap", "VolSnap", 25, "Clichés", LogSev.Warning);
CheckDecoded("VSS", "VSS", 8193, "VSS", LogSev.Warning);
foreach (var whea in new[] { (17, "CORRIGÉE"), (18, "NON CORRIGÉE"), (19, "CORRIGÉE") })
    CheckDecoded($"WHEA {whea.Item1}", "Microsoft-Windows-WHEA-Logger", whea.Item1,
        whea.Item2, LogSev.Serious);
CheckDecoded("Kernel-Power 41", "Microsoft-Windows-Kernel-Power", 41,
    "Kernel-Power 41", LogSev.Serious);
CheckDecoded("Kernel-Power générique", "Microsoft-Windows-Kernel-Power", 172,
    "alimentation noyau", LogSev.Warning);
CheckDecoded("Disque", "Disk", 51, "disque", LogSev.Serious);
CheckDecoded("NTFS", "Microsoft-Windows-Ntfs", 55, "disque", LogSev.Serious);
CheckDecoded("DCOM", "Microsoft-Windows-DistributedCOM", 10016, "DCOM", LogSev.Benign);
CheckDecoded("Perflib", "Microsoft-Windows-Perflib", 1023, "performance", LogSev.Benign);
CheckDecoded("Profil utilisateur", "Microsoft-Windows-User Profiles Service", 1534,
    "profils", LogSev.Benign);
CheckDecoded("BugCheck", "BugCheck", 1001, "BSOD", LogSev.Serious);
CheckDecoded("WER SystemErrorReporting", "Microsoft-Windows-WER-SystemErrorReporting", 1001,
    "BSOD", LogSev.Serious, rawFull: "The computer has rebooted from a bugcheck. BugcheckCode 0x00000124.");
CheckDecoded("Mémoire épuisée", "Microsoft-Windows-Resource-Exhaustion-Detector", 2004,
    "Mémoire physique", LogSev.Warning);
CheckDecoded("WLAN", "Microsoft-Windows-WLAN-AutoConfig", 8003, "Wi-Fi", LogSev.Warning);
CheckDecoded("DNS", "Microsoft-Windows-DNS-Client", 1014, "DNS", LogSev.Benign);
CheckDecoded("Service de temps", "Microsoft-Windows-Time-Service", 134, "horloge", LogSev.Benign);
CheckDecoded("Schannel", "Schannel", 36887, "TLS", LogSev.Warning);
foreach (string provider in new[] { "storahci", "iaStorAVC", "iaStorA", "iaStore" })
    CheckDecoded("Contrôleur SATA " + provider, provider, 129, "SATA", LogSev.Serious);
foreach (string provider in new[] { "stornvme", "nvme" })
    CheckDecoded("Contrôleur NVMe " + provider, provider, 11, "NVMe", LogSev.Serious);
CheckDecoded("Volmgr dump", "volmgr", 5, "Crash dump", LogSev.Warning);
CheckDecoded("BITS", "Microsoft-Windows-Bits-Client", 16392, "BITS", LogSev.Benign);
CheckDecoded("Wininit", "Microsoft-Windows-Wininit", 11, "Windows", LogSev.Benign);
CheckDecoded("Réveil Windows", "Microsoft-Windows-Power-Troubleshooter", 1, "Réveil", LogSev.Benign);
CheckDecoded("Spouleur", "Microsoft-Windows-PrintService", 808, "Spouleur", LogSev.Warning);

var unknownEvent = DecodeEvent("Vendor-Unknown", 9876, "raw", "raw full");
Check("Source inconnue reste inconnue", false, unknownEvent.Known);
Check("Source inconnue garde le fournisseur", "Vendor-Unknown", unknownEvent.Title);

var tempRoot = Path.Combine(Path.GetTempPath(), "Tweakly-RegistryTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    var payload = Path.Combine(tempRoot, "payload.bin");
    await File.WriteAllTextAsync(payload, "tweakly-update-test");
    var correctHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload))).ToLowerInvariant();

    await ExpectThrowsAsync<InvalidDataException>("Updater refuse le hash absent", () =>
        UpdatePackageValidator.VerifySha256Async(payload, ""));
    await ExpectThrowsAsync<InvalidDataException>("Updater refuse le hash faux", () =>
        UpdatePackageValidator.VerifySha256Async(payload, new string('0', 64)));
    checks++;
    await UpdatePackageValidator.VerifySha256Async(payload, correctHash);

    var invalidZip = Path.Combine(tempRoot, "invalid.zip");
    await File.WriteAllTextAsync(invalidZip, "not-a-zip");
    ExpectThrows<InvalidDataException>("Updater refuse une archive invalide", () =>
        UpdatePackageValidator.ExtractAndFindSource(invalidZip, Path.Combine(tempRoot, "invalid-out")));

    var noExeZip = Path.Combine(tempRoot, "no-exe.zip");
    using (var archive = ZipFile.Open(noExeZip, ZipArchiveMode.Create))
        archive.CreateEntry("Tweakly/data/readme.txt");
    ExpectThrows<InvalidDataException>("Updater refuse une archive sans exe", () =>
        UpdatePackageValidator.ExtractAndFindSource(noExeZip, Path.Combine(tempRoot, "no-exe-out")));

    var validZip = Path.Combine(tempRoot, "valid.zip");
    using (var archive = ZipFile.Open(validZip, ZipArchiveMode.Create))
        archive.CreateEntry("Tweakly/Tweakly.exe");
    var source = UpdatePackageValidator.ExtractAndFindSource(
        validZip, Path.Combine(tempRoot, "valid-out"));
    Check("Updater retrouve le dossier source", "Tweakly", Path.GetFileName(source));

    var script = UpdatePackageValidator.BuildUpdaterScript(
        @"C:\Temp Source\Tweakly", @"C:\Program Files\Tweakly", @"C:\Program Files\Tweakly\Tweakly.exe");
    var expectedScript =
        "@echo off\r\n" +
        ":wait\r\n" +
        "tasklist /fi \"imagename eq Tweakly.exe\" 2>nul | find /i \"Tweakly.exe\" >nul\r\n" +
        "if not errorlevel 1 (\r\n" +
        "  timeout /t 1 /nobreak >nul\r\n" +
        "  goto wait\r\n" +
        ")\r\n" +
        "timeout /t 1 /nobreak >nul\r\n" +
        "robocopy \"C:\\Temp Source\\Tweakly\" \"C:\\Program Files\\Tweakly\" /E /R:10 /W:2 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
        "start \"\" \"C:\\Program Files\\Tweakly\\Tweakly.exe\" --after-update\r\n" +
        "del \"%~f0\"\r\n";
    Check("Updater conserve le batch exact", expectedScript, script);
    Check("Updater conserve robocopy et les retries", true,
        script.Contains("robocopy \"C:\\Temp Source\\Tweakly\" \"C:\\Program Files\\Tweakly\" /E /R:10 /W:2"));
    Check("Updater relance avec after-update", true,
        script.Contains("start \"\" \"C:\\Program Files\\Tweakly\\Tweakly.exe\" --after-update"));
    Check("Updater attend la fermeture", true, script.Contains(":wait\r\n"));
    Check("Updater supprime son batch", true, script.EndsWith("del \"%~f0\"\r\n"));
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

var lastBatteryPoint = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc);
var restTarget = TimeSpan.FromHours(8);

var afterFullShutdown = BatteryResumeEvaluator.Evaluate(
    BatteryCalibrationPhase.Drain, lastBatteryPoint, 0,
    BatteryCalibrationPhase.Drain, lastBatteryPoint,
    lastBatteryPoint.AddHours(8.5), lastBatteryPoint.AddHours(8.25), restTarget);
Check("Batterie - extinction 8 h passe en recharge", BatteryResumeAction.RestComplete, afterFullShutdown.Action);
Check("Batterie - phase recharge apres extinction", BatteryCalibrationPhase.Recharge, afterFullShutdown.Phase);
Check("Batterie - repos hors tension mesure", TimeSpan.FromHours(8.25).TotalSeconds, afterFullShutdown.VerifiedRestSeconds);

var afterSimpleClose = BatteryResumeEvaluator.Evaluate(
    BatteryCalibrationPhase.Drain, lastBatteryPoint, 0,
    BatteryCalibrationPhase.Drain, lastBatteryPoint,
    lastBatteryPoint.AddHours(2), lastBatteryPoint.AddHours(-3), restTarget);
Check("Batterie - fermeture simple ne simule pas une extinction", BatteryResumeAction.TelemetryGapWithoutRestart, afterSimpleClose.Action);
Check("Batterie - fermeture simple conserve Drain", BatteryCalibrationPhase.Drain, afterSimpleClose.Phase);

var afterCrash = BatteryResumeEvaluator.Evaluate(
    BatteryCalibrationPhase.ChargeToFull, lastBatteryPoint.AddHours(-1), 0,
    BatteryCalibrationPhase.Drain, lastBatteryPoint,
    lastBatteryPoint.AddHours(1), lastBatteryPoint.AddHours(-4), restTarget);
Check("Batterie - crash recupere la phase du dernier point", true, afterCrash.RecoveredPhase);
Check("Batterie - crash reprend Drain", BatteryCalibrationPhase.Drain, afterCrash.Phase);

var afterClockJump = BatteryResumeEvaluator.Evaluate(
    BatteryCalibrationPhase.Drain, lastBatteryPoint, 0,
    BatteryCalibrationPhase.Drain, lastBatteryPoint,
    lastBatteryPoint.AddHours(10), lastBatteryPoint.AddHours(-2), restTarget);
Check("Batterie - changement d'heure meme boot refuse", BatteryResumeAction.TelemetryGapWithoutRestart, afterClockJump.Action);
Check("Batterie - changement d'heure conserve Drain", BatteryCalibrationPhase.Drain, afterClockJump.Phase);

var afterShortShutdown = BatteryResumeEvaluator.Evaluate(
    BatteryCalibrationPhase.Drain, lastBatteryPoint, 0,
    BatteryCalibrationPhase.Drain, lastBatteryPoint,
    lastBatteryPoint.AddHours(2.25), lastBatteryPoint.AddHours(2), restTarget);
Check("Batterie - extinction courte signalee incomplete", BatteryResumeAction.RestIncomplete, afterShortShutdown.Action);
Check("Batterie - extinction courte passe en repos", BatteryCalibrationPhase.Rest, afterShortShutdown.Phase);
Check("Batterie - debut du repos place au redemarrage", lastBatteryPoint.AddHours(2.25), afterShortShutdown.PhaseStartedAt);

var resumedRest = BatteryResumeEvaluator.Evaluate(
    BatteryCalibrationPhase.Rest, lastBatteryPoint, TimeSpan.FromHours(2).TotalSeconds,
    BatteryCalibrationPhase.Rest, lastBatteryPoint,
    lastBatteryPoint.AddHours(9), lastBatteryPoint.AddHours(8.5), restTarget);
Check("Batterie - second repos complet reprend en recharge", BatteryResumeAction.RestComplete, resumedRest.Action);
Check("Batterie - second repos remplace l'ancienne duree", TimeSpan.FromHours(8.5).TotalSeconds, resumedRest.VerifiedRestSeconds);

if (failures.Count == 0)
{
    Console.WriteLine($"Tweakly tests: {checks}/{checks} OK");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
Console.Error.WriteLine($"Registry tests: {failures.Count} echec(s)");
return 1;

void Check<T>(string name, T expected, T actual)
{
    checks++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        failures.Add($"{name}: attendu={expected}, obtenu={actual}");
}

void ExpectThrows<TException>(string name, Action action) where TException : Exception
{
    checks++;
    try
    {
        action();
        failures.Add($"{name}: aucune exception");
    }
    catch (TException)
    {
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: exception {ex.GetType().Name}");
    }
}

async Task ExpectThrowsAsync<TException>(string name, Func<Task> action) where TException : Exception
{
    checks++;
    try
    {
        await action();
        failures.Add($"{name}: aucune exception");
    }
    catch (TException)
    {
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: exception {ex.GetType().Name}");
    }
}
