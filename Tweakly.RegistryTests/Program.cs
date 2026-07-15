using Optimisation_Tool.Helpers;
using Optimisation_Tool.Pages;
using Microsoft.Win32;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

if (args.Contains("--optimization-roundtrip-audit", StringComparer.OrdinalIgnoreCase))
{
    string reportPath = ArgumentValue(args, "--report") ??
        Path.Combine(Path.GetTempPath(), "tweakly-optimization-roundtrip.txt");
    try
    {
        OptimizationAuditResult result = OptimizationRoundTripAudit.Run();
        File.WriteAllText(reportPath, result.Report, new System.Text.UTF8Encoding(false));
        Console.WriteLine(result.Report);
        return result.Success ? 0 : 1;
    }
    catch (Exception ex)
    {
        string failure = $"Audit interrompu proprement : {ex.GetBaseException().Message}";
        try { File.WriteAllText(reportPath, failure, new System.Text.UTF8Encoding(false)); } catch { }
        Console.Error.WriteLine(failure);
        return 1;
    }
}

if (args.Contains("--optimization-probe", StringComparer.OrdinalIgnoreCase))
{
    bool msiReadable = GpuMsiMode.TryRead(out bool msiEnabled, out string msiError);
    bool powerReadable = PowerPlanManager.TryReadUltimateState(out bool ultimateActive, out string powerError);
    bool adapterReadable = NetworkAdapterPower.TryRead(out bool adapterPowerDisabled, out string adapterError);
    bool nagleReadable = NetworkOptimizationSettings.TryReadNagle(out bool nagleDisabled, out string nagleError);
    bool dnsReadable = NetworkOptimizationSettings.TryReadDns(out bool optimizedDns, out string dnsError);
    Console.WriteLine($"MSI GPU : readable={msiReadable} | enabled={msiEnabled} | error={msiError}");
    Console.WriteLine($"Power plan : readable={powerReadable} | ultimate={ultimateActive} | error={powerError}");
    Console.WriteLine($"Network power : readable={adapterReadable} | disabled={adapterPowerDisabled} | error={adapterError}");
    Console.WriteLine($"Nagle : readable={nagleReadable} | disabled={nagleDisabled} | error={nagleError}");
    Console.WriteLine($"DNS : readable={dnsReadable} | optimized={optimizedDns} | error={dnsError}");
    return msiReadable && powerReadable && adapterReadable && nagleReadable && dnsReadable ? 0 : 1;
}

if (args.Contains("--restore-balanced-probe", StringComparer.OrdinalIgnoreCase))
{
    bool restored = PowerPlanManager.TrySetUltimate(false, out string message);
    Console.WriteLine(message);
    return restored ? 0 : 1;
}

if (args.Contains("--optimization-state-audit", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        DumpRawOptimizationInputs();
        DumpOptimizationState(typeof(PageWindows), "ReadState", new[]
        {
            "HAGS", "Désactiver Game Bar", "Désactiver DVR", "Priorité GPU",
            "Mode MSI", "Désactiver accélération Discord", "Désactiver overlay Steam"
        });
        DumpOptimizationState(typeof(PageWindows), "ReadState2", new[]
        {
            "Mode Jeu", "Optimisations jeux fenêtrés", "Désactiver popups accessibilité"
        });
        DumpOptimizationState(typeof(PageCPU), "ReadState", new[]
        {
            "Performances ultimes", "Désactiver Power Throttling",
            "SystemResponsiveness jeux", "Désactiver HVCI"
        });
        DumpOptimizationState(typeof(PageReseau), "ReadState", new[]
        {
            "Désactiver Nagle", "DNS optimisé", "Désactiver veille adaptateur",
            "Désactiver WPAD", "Désactiver bridage réseau"
        });
        DumpOptimizationState(typeof(PagePrivacy), "ReadState", new[]
        {
            "Désactiver télémétrie", "Désactiver ID publicitaire",
            "Désactiver historique activité", "Désactiver recherche Bing",
            "Désactiver personnalisation saisie", "Désactiver localisation",
            "Désactiver WER", "Désactiver expériences personnalisées",
            "Désactiver inventaire applications"
        });
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Audit des optimisations interrompu : {ex.GetBaseException().Message}");
        return 1;
    }
}

void DumpRawOptimizationInputs()
{
    Console.WriteLine($"[Contexte] utilisateur={Environment.UserName} | processus64={Environment.Is64BitProcess}");
    DumpRawRegistryValue(
        "HAGS",
        Registry.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
        "HwSchMode");
    DumpRawRegistryValue(
        "DVR",
        Registry.CurrentUser,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
        "HistoricalCaptureEnabled");
}

void DumpRawRegistryValue(string label, RegistryKey root, string subKey, string name)
{
    using RegistryKey? key = root.OpenSubKey(subKey, writable: false);
    object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    string rendered = value == null ? "<absent>" : $"{value} ({value.GetType().Name})";
    Console.WriteLine($"[Registre brut] {label} = {rendered}");
}

if (args.Contains("--live-events", StringComparer.OrdinalIgnoreCase))
{
    var (_, incidents) = EventLogDecoder.ScanAll(7);
    Console.WriteLine($"Incidents: {incidents.Count}");
    foreach (var incident in incidents)
    {
        Console.WriteLine($"[{incident.Start:yyyy-MM-dd HH:mm:ss}] {incident.Title}");
        Console.WriteLine($"  Cause: {incident.CauseState} | {incident.Conclusion}");
        Console.WriteLine($"  Repair: {incident.Repair?.Kind.ToString() ?? "NONE"} | {incident.Repair?.Target ?? ""}");
        Console.WriteLine($"  Investigation: {incident.Investigation?.Kind.ToString() ?? "NONE"} | {incident.Investigation?.Status ?? ""}");
        foreach (string evidence in incident.Evidence.Take(4))
            Console.WriteLine("  - " + evidence);
    }
    return 0;
}

if (args.Contains("--health-scan", StringComparer.OrdinalIgnoreCase))
{
    List<HealthItem> items = HealthCheck.Scan();
    foreach (HealthItem item in items)
        Console.WriteLine($"{item.Category} | {item.Title} | {item.Status} | {item.Message}");
    return items.Count > 0 ? 0 : 1;
}

if (args.Contains("--system-context-scan", StringComparer.OrdinalIgnoreCase))
{
    SystemContextSnap context = SystemContextSnap.Capture();
    Console.WriteLine($"RAM : {context.TotalRamGb:0.0} Go");
    Console.WriteLine($"Plan : {context.ActivePowerPlan} | {context.ActivePowerPlanGuid}");
    Console.WriteLine($"HAGS : {context.HagsEnabled} | HVCI : {context.HvciEnabled} | VBS : {context.VbsRunning}");
    Console.WriteLine($"Mode Jeu : {context.GameModeEnabled}");
    Console.WriteLine($"CPU : {context.CpuName}");
    Console.WriteLine($"GPU : {context.GpuName}");
    Console.WriteLine($"Écran principal : {context.MonitorRefreshRate} Hz");
    Console.WriteLine($"Processus localisés : {context.ExePaths.Count}");
    Console.WriteLine($"Volumes typés : {context.DiskByLetter.Count}");
    return context.TotalRamGb > 0 && context.CpuName.Length > 0 && context.GpuName.Length > 0
        ? 0
        : 1;
}

if (args.Contains("--battery-power-scan", StringComparer.OrdinalIgnoreCase))
{
    BatteryPowerPlanGuard.Snapshot snapshot = BatteryPowerPlanGuard.Read();
    Console.WriteLine($"Action batterie critique : {snapshot.DcCriticalAction?.ToString() ?? "indisponible"}");
    Console.WriteLine($"Action batterie faible : {snapshot.DcLowAction?.ToString() ?? "indisponible"}");
    if (snapshot.Error.Length > 0)
        Console.WriteLine("Erreur : " + snapshot.Error);
    return snapshot.DcCriticalAction.HasValue && snapshot.DcLowAction.HasValue ? 0 : 1;
}

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
Check("Feedback - libellé rapports d'erreurs n'est pas un échec", false,
    FeedbackMessageClassifier.IsFailure("Rapport d'erreurs Windows : DÉSACTIVÉ."));
Check("Feedback - erreur explicite détectée", true,
    FeedbackMessageClassifier.IsFailure("WER : erreur — accès refusé."));
Check("Plan - nom FR reconnu", true, PowerPlanManager.IsUltimateSchemeName("Performances ultimes"));
Check("Plan - nom EN reconnu", true, PowerPlanManager.IsUltimateSchemeName("Ultimate Performance"));
Check("Plan - nom Tweakly reconnu", true, PowerPlanManager.IsUltimateSchemeName("Tweakly - Performances ultimes"));
Check("Plan - nom Windows FR officiel reconnu", true, PowerPlanManager.IsUltimateSchemeName("Performances optimales"));

ProcessCommandResult commandOk = ProcessCommand.Run("cmd.exe", "/d /c echo tweakly", 5000);
Check("Commande - succès", true, commandOk.Success);
Check("Commande - sortie", true, commandOk.Output.Contains("tweakly", StringComparison.OrdinalIgnoreCase));
ProcessCommandResult commandFailure = ProcessCommand.Run("cmd.exe", "/d /c exit 7", 5000);
Check("Commande - code d'échec", 7, commandFailure.ExitCode);
Check("Commande - motif code d'échec", true,
    commandFailure.FailureDescription.Contains("code 7", StringComparison.OrdinalIgnoreCase));
ProcessCommandResult commandTimeout = ProcessCommand.Run(
    "cmd.exe", "/d /c ping 127.0.0.1 -n 5 >nul", 100);
Check("Commande - délai réel", true, commandTimeout.TimedOut);
Check("Commande - motif délai", true,
    commandTimeout.FailureDescription.Contains("délai", StringComparison.OrdinalIgnoreCase));
Check("Winget - ID valide", true, WingetCli.IsValidPackageId("Microsoft.PowerToys"));
Check("Winget - pseudo ID ARP refusé", false, WingetCli.IsValidPackageId(@"ARP\Machine\X64\Test.App"));
Check("Winget - injection refusée", false, WingetCli.IsValidPackageId("Test.App\" & calc.exe"));

string jsonSettingsPath = Path.Combine(Path.GetTempPath(), $"tweakly-json-{Guid.NewGuid():N}.json");
try
{
    File.WriteAllText(jsonSettingsPath,
        "{\"enableHardwareAcceleration\":true,\"window\":{\"x\":12},\"plugins\":[\"a\",\"b\"]}");
    JsonSettingsEditor.SetBooleanAtomically(jsonSettingsPath, "enableHardwareAcceleration", false);
    using var jsonSettings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonSettingsPath));
    Check("JSON Discord - booleen modifie", false,
        jsonSettings.RootElement.GetProperty("enableHardwareAcceleration").GetBoolean());
    Check("JSON Discord - objet preserve", 12,
        jsonSettings.RootElement.GetProperty("window").GetProperty("x").GetInt32());
    Check("JSON Discord - tableau preserve", 2,
        jsonSettings.RootElement.GetProperty("plugins").GetArrayLength());
}
finally
{
    try { File.Delete(jsonSettingsPath); } catch { }
}

try
{
    File.WriteAllText(jsonSettingsPath,
        "{\"enableHardwareAcceleration\":{\"legacy\":true},\"keep\":7}");
    JsonSettingsEditor.SetBooleanAtomically(jsonSettingsPath, "enableHardwareAcceleration", false);
    using var replacedJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonSettingsPath));
    Check("JSON Discord - ancienne valeur objet remplacee", false,
        replacedJson.RootElement.GetProperty("enableHardwareAcceleration").GetBoolean());
    Check("JSON Discord - valeur voisine preservee", 7,
        replacedJson.RootElement.GetProperty("keep").GetInt32());
}
finally
{
    try { File.Delete(jsonSettingsPath); } catch { }
}

string registryTestPath = $@"Software\Tweakly.RegistryTests\{Guid.NewGuid():N}";
try
{
    VerifiedRegistry.SetDword(Registry.CurrentUser, registryTestPath, "Dword", 42);
    Check("Registre verifie - DWORD", true,
        VerifiedRegistry.IsDword(Registry.CurrentUser, registryTestPath, "Dword", 42));
    VerifiedRegistry.SetString(Registry.CurrentUser, registryTestPath, "Text", "Tweakly");
    using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(registryTestPath))
        Check("Registre verifie - texte", "Tweakly", Convert.ToString(key?.GetValue("Text")));
    VerifiedRegistry.DeleteValue(Registry.CurrentUser, registryTestPath, "Dword");
    Check("Registre verifie - suppression", true,
        VerifiedRegistry.IsMissing(Registry.CurrentUser, registryTestPath, "Dword"));
}
finally
{
    try { Registry.CurrentUser.DeleteSubKeyTree(registryTestPath, throwOnMissingSubKey: false); } catch { }
}

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

void DumpOptimizationState(Type pageType, string methodName, IReadOnlyList<string> labels)
{
    MethodInfo method = pageType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{pageType.Name}.{methodName} introuvable.");
    object state = method.Invoke(null, null)
        ?? throw new InvalidOperationException($"{pageType.Name}.{methodName} a retourné null.");
    List<object?> values = FlattenTupleValues(state);
    if (values.Count != labels.Count)
        throw new InvalidOperationException(
            $"{pageType.Name}.{methodName} expose {values.Count} valeur(s), {labels.Count} attendue(s).");

    Console.WriteLine($"[{pageType.Name}.{methodName}]");
    for (int i = 0; i < labels.Count; i++)
        Console.WriteLine($"  {labels[i]} = {values[i]}");
}

List<object?> FlattenTupleValues(object tuple)
{
    var values = new List<object?>();
    Type type = tuple.GetType();
    for (int i = 1; i <= 7; i++)
    {
        FieldInfo? item = type.GetField($"Item{i}");
        if (item == null) break;
        values.Add(item.GetValue(tuple));
    }

    FieldInfo? rest = type.GetField("Rest");
    object? nested = rest?.GetValue(tuple);
    if (nested != null)
        values.AddRange(FlattenTupleValues(nested));
    return values;
}

string? ArgumentValue(string[] values, string name)
{
    int index = Array.FindIndex(values, value =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
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

const string vssFrench = """
Nom du rédacteur : 'System Writer'
   ID du rédacteur : {e8132975-6f93-4464-a53e-1050253ae220}
   État : [1] Stable
   Dernière erreur : Pas d'erreur
Nom du rédacteur : 'WMI Writer'
   ID du rédacteur : {a6ad56c2-b509-4e6c-bb19-49d8f43532f0}
   État : [9] Échec
   Dernière erreur : Erreur non renouvelable
""";
var parsedVssFrench = WindowsIncidentRemediator.ParseVssWriters(vssFrench);
Check("VSS FR - nombre de writers", 2, parsedVssFrench.Count);
Check("VSS FR - System Writer stable", true, parsedVssFrench[0].IsStable);
Check("VSS FR - WMI Writer défaillant", false, parsedVssFrench[1].IsStable);
Check("VSS FR - ID normalisé", "a6ad56c2-b509-4e6c-bb19-49d8f43532f0", parsedVssFrench[1].Id);
Check("VSS FR - erreur conservée", "Erreur non renouvelable", parsedVssFrench[1].LastError);

const string vssEnglish = """
Writer name: 'Registry Writer'
   Writer Id: {afbab4a2-367d-4d15-a586-71dbb18f8485}
   State: [1] Stable
   Last error: No error
""";
var parsedVssEnglish = WindowsIncidentRemediator.ParseVssWriters(vssEnglish);
Check("VSS EN - nombre de writers", 1, parsedVssEnglish.Count);
Check("VSS EN - writer stable", true, parsedVssEnglish[0].IsStable);

var tdrIncident = new Incident { Title = "TDR" };
IncidentDiagnosticEngine.Enrich(tdrIncident, new[]
{
    new RawEvent
    {
        Time = DateTime.Now,
        Provider = "Display",
        Id = 4101,
        RawFull = "Le pilote d'affichage nvlddmkm ne répondait plus.",
    },
});
Check("Diagnostic TDR - cause non inventée", IncidentCauseState.Insufficient, tdrIncident.CauseState);
Check<IncidentRepairPlan?>("Diagnostic TDR - aucune fausse correction", null, tdrIncident.Repair);
Check("Diagnostic TDR - reset confirmé", true,
    tdrIncident.Conclusion.Contains("reset est confirmé", StringComparison.OrdinalIgnoreCase));
Check("Diagnostic TDR - investigation active disponible", IncidentInvestigationKind.FreezeTrace,
    tdrIncident.Investigation?.Kind ?? throw new InvalidOperationException("Investigation TDR absente."));

var ntfsIncident = new Incident { Title = "NTFS" };
var ntfsEvent = new RawEvent
{
    Time = DateTime.Now,
    Provider = "Microsoft-Windows-Ntfs",
    Id = 55,
};
ntfsEvent.Data["DriveName"] = "C:\\";
IncidentDiagnosticEngine.Enrich(ntfsIncident, new[] { ntfsEvent });
Check("Diagnostic NTFS - cause établie", IncidentCauseState.Established, ntfsIncident.CauseState);
Check("Diagnostic NTFS - volume exact", "C:", ntfsIncident.Repair?.Target ?? "");
Check("Diagnostic NTFS - plan ciblé", IncidentRepairKind.NtfsVolume, ntfsIncident.Repair?.Kind ?? IncidentRepairKind.VssWriters);

var analyzeMethod = typeof(EventLogDecoder).GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("EventLogDecoder.Analyze introuvable.");
var singleVssIncident = (Incident?)analyzeMethod.Invoke(null, new object?[]
{
    new List<RawEvent>
    {
        new()
        {
            Time = DateTime.Now,
            Provider = "VSS",
            Id = 8193,
            Raw = "Erreur VSS",
            RawFull = "Erreur VSS",
        },
    },
    null,
    null,
    null,
});
Check("Incident VSS isolé conservé", true, singleVssIncident != null);
Check("Incident VSS isolé diagnostic disponible", IncidentRepairKind.VssWriters,
    singleVssIncident?.Repair?.Kind ?? IncidentRepairKind.NtfsVolume);

var appOnlyCluster = new List<RawEvent>
{
    new()
    {
        Time = DateTime.Now,
        Provider = ".NET Runtime",
        Id = 1026,
        Raw = "Application: Tweakly.exe",
        RawFull = "Application: Tweakly.exe\nException Info: System.DllNotFoundException",
        Data = { ["param1"] = "Application: Tweakly.exe" },
    },
    new()
    {
        Time = DateTime.Now.AddSeconds(1),
        Provider = "Application Error",
        Id = 1000,
        Raw = "Tweakly.exe",
        RawFull = "Application défaillante : Tweakly.exe\nModule défaillant : Tweakly.exe",
        Data = { ["#0"] = "1781648583", ["FaultingApplicationName"] = "Tweakly.exe", ["FaultingModuleName"] = "Tweakly.exe" },
    },
};
var appOnlyIncident = (Incident?)analyzeMethod.Invoke(null, new object?[]
{
    appOnlyCluster,
    null,
    null,
    null,
});
Check("Application Error numérique non classée BSOD", false,
    appOnlyIncident?.Title.Contains("BSOD", StringComparison.OrdinalIgnoreCase) ?? false);
Check("Crash .NET - exception réelle nommée", true,
    appOnlyIncident?.Title.Contains("DllNotFoundException", StringComparison.OrdinalIgnoreCase) ?? false);

var hangIncident = new Incident { Title = "WER" };
var hangEvent = new RawEvent
{
    Time = DateTime.Now,
    Provider = "Windows Error Reporting",
    Id = 1001,
    RawFull = "Nom d’événement : AppHangB1\nP1 : DemoGame.exe",
};
hangEvent.Data["EventName"] = "AppHangB1";
hangEvent.Data["P1"] = "DemoGame.exe";
IncidentDiagnosticEngine.Enrich(hangIncident, new[] { hangEvent });
Check("AppHang - application nommée", "DemoGame.exe ne répondait plus", hangIncident.Title);
Check("AppHang - investigation active disponible", IncidentInvestigationKind.FreezeTrace,
    hangIncident.Investigation?.Kind ?? throw new InvalidOperationException("Investigation AppHang absente."));

var updateIncident = new Incident { Title = "Windows Update" };
var updateEvent = new RawEvent
{
    Time = DateTime.Now,
    Provider = "Microsoft-Windows-WindowsUpdateClient",
    Id = 20,
};
updateEvent.Data["errorCode"] = "0x80073d02";
updateEvent.Data["updateTitle"] = "9NMPJ99VJBWV-Microsoft.YourPhone";
IncidentDiagnosticEngine.Enrich(updateIncident, new[] { updateEvent });
Check("Windows Update - code 0x80073D02 établi", IncidentCauseState.Established, updateIncident.CauseState);
Check("Windows Update - package nommé", true,
    updateIncident.Conclusion.Contains("Microsoft.YourPhone", StringComparison.OrdinalIgnoreCase));
Check("Windows Update - correction Store ciblée", IncidentRepairKind.StorePackagesInUse,
    updateIncident.Repair?.Kind ?? throw new InvalidOperationException("Correction Store absente."));
Check("Windows Update - ID Store exact", "9NMPJ99VJBWV|Microsoft.YourPhone", updateIncident.Repair?.Target ?? "");

var resolveWerTime = typeof(EventLogDecoder).GetMethod(
    "ResolveOriginalEventTime",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("ResolveOriginalEventTime introuvable.");
var resolvedWerTime = (DateTime)(resolveWerTime.Invoke(null, new object[]
{
    "Windows Error Reporting",
    1001,
    new DateTime(2026, 7, 13, 2, 9, 57),
    @"Nom d’événement : LiveKernelEvent C:\WINDOWS\LiveKernelReports\WATCHDOG\WATCHDOG-20260705-0053.dmp",
}) ?? throw new InvalidOperationException("Date WER nulle."));
Check("WER utilise la date réelle du dump", new DateTime(2026, 7, 5, 0, 53, 0), resolvedWerTime);

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

var availableProbe = ProbeResult<bool>.Available(true);
Check("Sonde - valeur disponible conservee", true, availableProbe.Value);
Check("Sonde - succes disponible", true, availableProbe.Success);
var unavailableProbe = ProbeResult<bool>.Unavailable(false, "lecture refusee");
Check("Sonde - echec explicite", false, unavailableProbe.Success);
Check("Sonde - erreur conservee", "lecture refusee", unavailableProbe.Error);

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
