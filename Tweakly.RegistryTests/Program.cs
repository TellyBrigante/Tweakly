using Optimisation_Tool.Helpers;
using Optimisation_Tool.Pages;
using Optimisation_Tool.Controls;
using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FanControl.Core;

if (args.Contains("--fan-curve-render-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new Optimisation_Tool.App();
            app.InitializeComponent();
            if (args.Contains("--light", StringComparer.OrdinalIgnoreCase))
                ThemeManager.Apply(ThemeManager.Mode.Light);
            double width = ArgumentDouble(args, "--width", 1180);
            double height = ArgumentDouble(args, "--height", 900);
            var page = new PageVentilation(null!) { Width = width, Height = height };
            ((TextBlock)page.FindName("TxtInventoryStatus")).Text =
                "2 canaux de carte mere pilotables avec retour de vitesse detectes.";
            ((TextBlock)page.FindName("TxtMotherboard")).Text = "ASUS TUF GAMING B860-PLUS WIFI";
            ((TextBlock)page.FindName("TxtController")).Text = "Nuvoton NCT6701D";
            ((TextBlock)page.FindName("TxtChannelCount")).Text = "2";
            var cpuFan = new FanChannelItem
            {
                Channel = new DetectedFanChannel
                {
                    Id = "cpu",
                    DisplayName = "Ventilateur processeur (CPU_FAN)",
                    HardwareName = "Nuvoton NCT6701D",
                    Index = 1,
                    Rpm = 548,
                    ControlPercent = 31,
                    MinimumControlPercent = 0,
                    MaximumControlPercent = 100,
                    SuggestedRole = FanRole.Cpu,
                    DriveMode = FanDriveMode.Pwm,
                    RequiresRoleConfirmation = false,
                    RoleSource = "Identifie automatiquement"
                }
            };
            cpuFan.InitializeTelemetry();
            var caseFan = new FanChannelItem
            {
                Channel = new DetectedFanChannel
                {
                    Id = "case",
                    DisplayName = "Ventilateurs boitier (CHA_FAN2)",
                    HardwareName = "Nuvoton NCT6701D",
                    Index = 2,
                    Rpm = 682,
                    ControlPercent = 33,
                    MinimumControlPercent = 0,
                    MaximumControlPercent = 100,
                    SuggestedRole = FanRole.Chassis,
                    DriveMode = FanDriveMode.Pwm,
                    RequiresRoleConfirmation = false,
                    RoleSource = "Identifie automatiquement"
                }
            };
            caseFan.InitializeTelemetry();
            ((ItemsControl)page.FindName("FanList")).ItemsSource = new[] { cpuFan, caseFan };
            var curveSection = (StackPanel)page.FindName("CurveSection");
            var curveList = (ItemsControl)page.FindName("CurveList");
            curveList.ItemsSource = new[]
            {
                new FanCurveItem
                {
                    ChannelId = "cpu",
                    Name = "Ventilateur processeur",
                    SourceText = "Pilotee par la temperature CPU",
                    CalibrationText = "Plancher mesure : 30 %  |  Maximum mesure : 1 780 tr/min",
                    MinimumDuty = 30,
                    Source = ThermalSource.Cpu,
                    Points = new System.Collections.ObjectModel.ObservableCollection<FanCurvePoint>(
                    [new(35, 30), new(55, 48), new(78, 72), new(87, 100)]),
                    AutomaticPoints = [new(35, 30), new(55, 48), new(78, 72), new(87, 100)]
                },
                new FanCurveItem
                {
                    ChannelId = "case",
                    Name = "Ventilateurs boitier / hub",
                    SourceText = "Pilotee par la temperature la plus haute (CPU ou GPU)",
                    CalibrationText = "Plancher mesure : 35 %  |  Maximum mesure : 1 420 tr/min",
                    MinimumDuty = 35,
                    Source = ThermalSource.Mixed,
                    Points = new System.Collections.ObjectModel.ObservableCollection<FanCurvePoint>(
                    [new(35, 35), new(55, 45), new(78, 68), new(87, 100)]),
                    AutomaticPoints = [new(35, 35), new(55, 45), new(78, 68), new(87, 100)]
                }
            };
            curveSection.Visibility = Visibility.Visible;
            page.Measure(new Size(page.Width, double.PositiveInfinity));
            page.Arrange(new Rect(0, 0, page.Width, Math.Max(page.Height, page.DesiredSize.Height)));
            page.UpdateLayout();

            if (args.Contains("--switch-theme-after-layout", StringComparer.OrdinalIgnoreCase))
            {
                ThemeManager.Apply(args.Contains("--light", StringComparer.OrdinalIgnoreCase)
                    ? ThemeManager.Mode.Dark
                    : ThemeManager.Mode.Light);
                page.RefreshThemeVisuals();

                Brush expectedLine = (Brush)app.FindResource("ThBlueLine");
                FanCurveEditor[] editors = FindVisualChildren<FanCurveEditor>(page).ToArray();
                if (editors.Length != 2)
                    throw new InvalidOperationException($"Deux graphiques attendus, {editors.Length} trouve(s).");

                var frame = new DispatcherFrame();
                page.Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                page.Measure(new Size(page.Width, double.PositiveInfinity));
                page.Arrange(new Rect(0, 0, page.Width, Math.Max(page.Height, page.DesiredSize.Height)));
                page.UpdateLayout();
                if (editors.Any(editor => !BrushColorsEqual(editor.LineBrush, expectedLine)))
                    throw new InvalidOperationException("Le graphique n'a pas applique le nouveau theme au cycle de rendu suivant.");
            }

            int renderWidth = (int)Math.Ceiling(page.ActualWidth);
            int renderHeight = (int)Math.Ceiling(page.ActualHeight);
            var bitmap = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string fileName = args.Contains("--light", StringComparer.OrdinalIgnoreCase)
                ? "_fan-curve-render-light.png"
                : "_fan-curve-render.png";
            string outputPath = ArgumentValue(args, "--output")
                ?? Path.Combine(Path.GetTempPath(), fileName);
            using FileStream stream = File.Create(outputPath);
            encoder.Save(stream);
            Console.WriteLine(outputPath);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        Console.Error.WriteLine(failure);
        return 1;
    }
    Console.WriteLine("Fan curve visual render: OK");
    return 0;
}

if (args.Contains("--registry-page-render-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? failure = null;
    string? renderedPath = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new Optimisation_Tool.App();
            app.InitializeComponent();
            ThemeManager.Apply(args.Contains("--light", StringComparer.OrdinalIgnoreCase)
                ? ThemeManager.Mode.Light
                : ThemeManager.Mode.Dark);

            var page = new PageRegistryInspection();
            double width = ArgumentDouble(args, "--width", 1000);
            double height = ArgumentDouble(args, "--height", 760);
            var root = new Border
            {
                Width = width,
                Height = height,
                Child = page
            };
            root.SetResourceReference(Border.BackgroundProperty, "ThBg");
            root.Measure(new Size(root.Width, root.Height));
            root.Arrange(new Rect(0, 0, root.Width, root.Height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                (int)root.Width,
                (int)root.Height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string fileName = args.Contains("--light", StringComparer.OrdinalIgnoreCase)
                ? "_registry-page-render-light.png"
                : "_registry-page-render.png";
            renderedPath = ArgumentValue(args, "--output")
                ?? Path.Combine(Path.GetTempPath(), fileName);
            using FileStream stream = File.Create(renderedPath);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        Console.Error.WriteLine(failure);
        return 1;
    }

    Console.WriteLine(renderedPath);
    Console.WriteLine("Registry page visual render: OK");
    return 0;
}

if (args.Contains("--fan-page-xaml-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new Optimisation_Tool.App();
            app.InitializeComponent();
            _ = new PageVentilation(null!);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        Console.Error.WriteLine(failure);
        return 1;
    }

    Console.WriteLine("PageVentilation XAML: OK");
    return 0;
}

if (args.Contains("--fan-profile-validation-tests", StringComparer.OrdinalIgnoreCase))
{
    var calibration = new FanCalibrationResult
    {
        IsValid = true,
        FailureReason = "",
        DriveMode = FanDriveMode.Pwm,
        MinimumStableDutyPercent = 30,
        RestartDutyPercent = 35,
        MaximumObservedRpm = 1_500
    };
    var channel = new SavedFanChannel
    {
        Id = "board/fan/1",
        DisplayName = "CPU_FAN",
        Role = FanRole.Cpu,
        DriveMode = FanDriveMode.Pwm,
        Calibration = calibration,
        Source = ThermalSource.Cpu,
        IdleTemperatureC = 40,
        ThermalTrials =
        [
            new ThermalTrial
            {
                Workload = WorkloadLevel.Heavy,
                DutyPercent = 70,
                MaximumTemperatureC = 72,
                Stable = true,
                ObservedRpm = 1_200
            }
        ],
        AutomaticCurve = [new(40, 35), new(70, 70), new(80, 100)],
        Curve = [new(40, 35), new(70, 70), new(80, 100)]
    };
    var valid = new FanProfileDocument
    {
        MotherboardName = "Test board",
        AutomaticControlEnabled = true,
        StartWithTweakly = true,
        Channels = [channel]
    };

    if (!FanProfileStore.TryValidate(valid, out string validError))
    {
        Console.Error.WriteLine("Valid profile rejected: " + validError);
        return 1;
    }
    if (FanProfileStore.TryValidate(
            valid with { Channels = [channel with { Curve = [new(40, 70), new(60, 60), new(80, 100)] }] },
            out _))
    {
        Console.Error.WriteLine("Non-monotonic profile accepted.");
        return 1;
    }
    if (FanProfileStore.TryValidate(
            valid with { Channels = [channel with { Curve = [new(40, 35), new(80, 90)] }] },
            out _))
    {
        Console.Error.WriteLine("Profile without a 100 percent safety point accepted.");
        return 1;
    }
    if (FanProfileStore.TryValidate(
            valid with { Channels = [channel with { Calibration = null }] },
            out _))
    {
        Console.Error.WriteLine("Automatic profile without calibration accepted.");
        return 1;
    }

    Console.WriteLine("Fan profile validation tests: 4/4 OK");
    return 0;
}

if (args.Contains("--fan-vendor-metadata-probe", StringComparer.OrdinalIgnoreCase))
{
    string board = ArgumentValue(args, "--board") ?? "";
    IReadOnlyDictionary<int, FanChannelMetadata> metadata = FanVendorMetadataResolver.Read(board);
    foreach ((int index, FanChannelMetadata channel) in metadata.OrderBy(item => item.Key))
        Console.WriteLine($"{index}|{channel.DisplayName}|{channel.Role}|{channel.Source}");
    return metadata.Count > 0 ? 0 : 2;
}

if (args.Contains("--fan-label-classifier-smoke", StringComparer.OrdinalIgnoreCase))
{
    (string Label, FanControl.Core.FanRole Expected)[] cases =
    [
        ("CPU Fan", FanControl.Core.FanRole.Cpu),
        ("CPU_FAN", FanControl.Core.FanRole.Cpu),
        ("CPU_OPT", FanControl.Core.FanRole.Cpu),
        ("System Fan #2", FanControl.Core.FanRole.Chassis),
        ("SYS_FAN3", FanControl.Core.FanRole.Chassis),
        ("Chassis Fan #1", FanControl.Core.FanRole.Chassis),
        ("CHA_FAN2", FanControl.Core.FanRole.Chassis),
        ("AIO_PUMP", FanControl.Core.FanRole.Pump),
        ("Pump Fan #1", FanControl.Core.FanRole.Pump),
        ("Radiator Fan", FanControl.Core.FanRole.Radiator),
        ("Fan #2", FanControl.Core.FanRole.Unknown),
        ("Chipset Fan", FanControl.Core.FanRole.Unknown)
    ];

    foreach ((string label, FanControl.Core.FanRole expected) in cases)
    {
        FanControl.Core.FanRole actual = FanChannelLabelClassifier.InferRole(label);
        if (actual != expected)
        {
            Console.Error.WriteLine($"{label}: expected {expected}, got {actual}");
            return 1;
        }
    }

    Console.WriteLine($"Fan label classifier: {cases.Length}/{cases.Length} OK");
    return 0;
}

if (args.Contains("--fan-hardware-probe", StringComparer.OrdinalIgnoreCase))
{
    return FanHardwareProbe.Run();
}

if (args.Contains("--fan-inventory-probe", StringComparer.OrdinalIgnoreCase))
{
    FanHardwareInventoryResult inventory = FanHardwareInventory.Read();
    var lines = new List<string>
    {
        $"Available={inventory.Available}",
        $"Motherboard={inventory.MotherboardName}",
        $"Controller={inventory.ControllerName}",
        $"Channels={inventory.Channels.Count}",
        $"Message={inventory.Message}"
    };
    lines.AddRange(inventory.Channels.Select(channel =>
        $"{channel.Index}|{channel.DisplayName}|{channel.Rpm:0} RPM|{channel.ControlPercent:0.0}%|{channel.SuggestedRole}|confirm={channel.RequiresRoleConfirmation}"));
    string report = string.Join(Environment.NewLine, lines);
    string? reportPath = ArgumentValue(args, "--report");
    if (!string.IsNullOrWhiteSpace(reportPath))
        File.WriteAllText(reportPath, report, new System.Text.UTF8Encoding(false));
    Console.WriteLine(report);
    return inventory.Available ? 0 : 2;
}

if (args.Contains("--fan-restore-defaults-probe", StringComparer.OrdinalIgnoreCase))
{
    FanHardwareInventoryResult inventory = FanHardwareInventory.Read();
    if (!inventory.Available || inventory.Channels.Count == 0)
    {
        Console.Error.WriteLine(inventory.Message);
        return 1;
    }

    FanHardwareRestoreReport report = FanHardwareSession.RestoreControlsToDefault(
        inventory.Channels.Select(static channel => channel.Id).ToArray());
    Console.WriteLine(
        $"Requested={report.RequestedControls} Matched={report.MatchedControls} Restored={report.RestoredControls}");
    foreach (string error in report.Errors)
        Console.Error.WriteLine(error);
    return report.Success ? 0 : 1;
}

if (args.Contains("--registry-live-probe", StringComparer.OrdinalIgnoreCase))
{
    int build = Environment.OSVersion.Version.Build;
    string edition = "Unknown";
    using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(
               @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
               writable: false))
    {
        if (int.TryParse(Convert.ToString(key?.GetValue("CurrentBuildNumber")), out int parsedBuild))
            build = parsedBuild;
        edition = Convert.ToString(key?.GetValue("EditionID"))?.Trim() ?? edition;
    }

    var inspector = new RegistryRepair.Core.RegistryContextInspector(
        new RegistryRepair.Windows.WindowsRegistryBackend());
    var progress = new List<RegistryRepair.Core.RegistryInspectionProgress>();
    IReadOnlyList<RegistryRepair.Core.RegistryInspectionFinding> findings = inspector.Inspect(
        new RegistryRepair.Core.WindowsIdentity(build, edition, Environment.Is64BitOperatingSystem),
        progress.Add);
    Console.WriteLine($"Build={build} Edition={edition} Stages={progress.Count} Findings={findings.Count}");
    foreach (RegistryRepair.Core.RegistryInspectionFinding finding in findings)
        Console.WriteLine($"{finding.Status}|{finding.Code}|{finding.Address.Hive}|{finding.Address.KeyPath}");
    return progress.Count > 0 ? 0 : 1;
}

if (args.Contains("--monitor-timing-probe", StringComparer.OrdinalIgnoreCase))
{
    return await MonitoringTimingProbe.RunAsync();
}

if (args.Contains("--disk-activity-tests", StringComparer.OrdinalIgnoreCase))
{
    (long PreviousIdle, long PreviousQuery, long CurrentIdle, long CurrentQuery,
        bool ExpectedSuccess, double ExpectedUsage)[] cases =
    [
        (1_000, 10_000, 1_900, 11_000, true, 10),
        (1_000, 10_000, 1_500, 11_000, true, 50),
        (1_000, 10_000, 1_000, 11_000, true, 100),
        (1_000, 10_000, 2_500, 11_000, true, 0),
        (1_000, 10_000, 900, 11_000, false, 0),
        (1_000, 10_000, 1_100, 10_000, false, 0),
    ];

    foreach (var test in cases)
    {
        bool success = DiskActivitySampler.TryCalculateUsage(
            test.PreviousIdle,
            test.PreviousQuery,
            test.CurrentIdle,
            test.CurrentQuery,
            out double usage);
        if (success != test.ExpectedSuccess ||
            success && Math.Abs(usage - test.ExpectedUsage) > 0.001)
        {
            Console.Error.WriteLine(
                $"Disk activity: expected success={test.ExpectedSuccess}, usage={test.ExpectedUsage}; " +
                $"got success={success}, usage={usage:0.###}.");
            return 1;
        }
    }

    Console.WriteLine($"Disk activity calculation: {cases.Length}/{cases.Length} OK");
    return 0;
}

if (args.Contains("--disk-activity-probe", StringComparer.OrdinalIgnoreCase))
{
    int deviceNumber = int.TryParse(ArgumentValue(args, "--device"), out int parsedDevice)
        ? parsedDevice
        : 0;
    bool first = DiskActivitySampler.TrySample(deviceNumber, out double firstUsage);
    await Task.Delay(1_100);
    bool second = DiskActivitySampler.TrySample(deviceNumber, out double secondUsage);
    Console.WriteLine(
        $"PhysicalDrive{deviceNumber}: first={first}/{firstUsage:0.0}% " +
        $"second={second}/{secondUsage:0.0}%");
    return second ? 0 : 1;
}

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

if (args.Contains("--cleanup-estimate-scan", StringComparer.OrdinalIgnoreCase))
{
    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string eventLogs = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "winevt", "Logs");
    var targets = new (string Name, Func<CleanupEstimateResult> Measure)[]
    {
        ("Temp utilisateur", () => CleanupOperations.EstimateFolder(Path.GetTempPath())),
        ("Temp Windows", () => CleanupOperations.EstimateFolder(@"C:\Windows\Temp")),
        ("Prefetch", () => CleanupOperations.EstimateFolder(@"C:\Windows\Prefetch")),
        ("Cache DirectX", () => CleanupOperations.EstimateFolder(Path.Combine(local, "D3DSCache"))),
        ("Caches NVIDIA", () => CleanupEstimateResult.Combine(
            CleanupOperations.EstimateFolder(Path.Combine(local, "NVIDIA", "DXCache")),
            CleanupOperations.EstimateFolder(Path.Combine(roaming, "NVIDIA", "GLCache")))),
        ("Journaux Windows", () => CleanupOperations.EstimateFolder(eventLogs, "*.evtx", recursive: false)),
    };

    foreach ((string name, Func<CleanupEstimateResult> measure) in targets)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        CleanupEstimateResult result = measure();
        timer.Stop();
        Console.WriteLine($"{name} : {result.Bytes} octet(s) | {result.Files} fichier(s) | "
            + $"disponible={result.Available} | {result.Skipped} ignoré(s) | {timer.ElapsedMilliseconds} ms");
    }

    return 0;
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
using (var commandCancellation = new CancellationTokenSource(100))
{
    ExpectThrows<OperationCanceledException>("Commande - annulation réelle", () =>
        ProcessCommand.Run(
            "cmd.exe",
            "/d /c ping 127.0.0.1 -n 5 >nul",
            10_000,
            commandCancellation.Token));
}
Check("Winget - ID valide", true, WingetCli.IsValidPackageId("Microsoft.PowerToys"));
Check("Winget - pseudo ID ARP refusé", false, WingetCli.IsValidPackageId(@"ARP\Machine\X64\Test.App"));
Check("Winget - injection refusée", false, WingetCli.IsValidPackageId("Test.App\" & calc.exe"));
Check("Winget - source communautaire fixe", "winget", WingetCli.CommunitySource);
var combinedResidues = CleanupOperationResult.Combine(
    new CleanupOperationResult { Ops = 1, Residues = 2, ResiduesRemoved = 1 },
    new CleanupOperationResult { Ops = 2, Residues = 3, ResiduesRemoved = 2 });
Check("Résidus - détection cumulée", 5, combinedResidues.Residues);
Check("Résidus - traitements cumulés", 3, combinedResidues.ResiduesRemoved);

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
string missingDirectory = Path.Combine(Path.GetTempPath(), "Tweakly-Missing-" + Guid.NewGuid().ToString("N"));
string missingExecutable = Path.Combine(missingDirectory, "missing.exe");
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

    using (RegistryKey orphanUninstall = Registry.CurrentUser.CreateSubKey(registryTestPath + @"\OrphanUninstall"))
    {
        orphanUninstall.SetValue("DisplayName", "Tweakly residue test");
        orphanUninstall.SetValue("InstallLocation", missingDirectory);
        orphanUninstall.SetValue("UninstallString", '"' + missingExecutable + '"');
        Check("Résidus - entrée de désinstallation orpheline", true,
            CleanupOperations.IsProvablyOrphanedUninstallEntry(orphanUninstall));
        orphanUninstall.SetValue("WindowsInstaller", 1, RegistryValueKind.DWord);
        Check("Résidus - entrée MSI protégée", false,
            CleanupOperations.IsProvablyOrphanedUninstallEntry(orphanUninstall));
    }

    using (RegistryKey orphanAppPath = Registry.CurrentUser.CreateSubKey(registryTestPath + @"\OrphanAppPath"))
    {
        orphanAppPath.SetValue(null, missingExecutable);
        Check("Résidus - App Path orphelin", true,
            CleanupOperations.IsProvablyOrphanedAppPath(orphanAppPath));
    }

}
catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
{
    // Certains runners isolés refusent même une sous-clé HKCU. Ce n'est pas
    // une défaillance du moteur de registre : la vérification est inconclusive
    // dans cet environnement et doit être signalée sans masquer les autres
    // régressions du protocole.
    Console.WriteLine("Registre vérifié - contrôle HKCU ignoré dans l'environnement isolé : " + ex.Message);
}
finally
{
    try { Registry.CurrentUser.DeleteSubKeyTree(registryTestPath, throwOnMissingSubKey: false); } catch { }
}

Check("Résidus - démarrage orphelin", true,
    CleanupOperations.IsProvablyOrphanedStartupCommand('"' + missingExecutable + '"' + " --silent"));
Check("Résidus - démarrage existant conservé", false,
    CleanupOperations.IsProvablyOrphanedStartupCommand('"' + Environment.ProcessPath + '"'));
Check("Résidus - raccourci cassé", true,
    CleanupOperations.IsBrokenShortcutTarget(missingExecutable));
Check("Résidus - raccourci existant conservé", false,
    CleanupOperations.IsBrokenShortcutTarget(Environment.ProcessPath ?? ""));

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

double ArgumentDouble(string[] values, string name, double fallback) =>
    double.TryParse(
        ArgumentValue(values, name),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out double parsed)
        ? parsed
        : fallback;

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

IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
{
    for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
        DependencyObject child = VisualTreeHelper.GetChild(parent, index);
        if (child is T match)
            yield return match;
        foreach (T descendant in FindVisualChildren<T>(child))
            yield return descendant;
    }
}

bool BrushColorsEqual(Brush left, Brush right) =>
    left is SolidColorBrush leftSolid &&
    right is SolidColorBrush rightSolid &&
    leftSolid.Color == rightSolid.Color;
