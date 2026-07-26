using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using RegistryRepair.Core;
using RegistryRepair.Windows;
using RegistryWindowsIdentity = RegistryRepair.Core.WindowsIdentity;

namespace Optimisation_Tool.Pages;

public partial class PageRegistryInspection : UserControl
{
    private bool _scanRunning;

    public PageRegistryInspection()
    {
        InitializeComponent();
    }

    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning)
            return;

        _scanRunning = true;
        BtnAnalyze.IsEnabled = false;
        ResultsPanel.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        PbScan.Visibility = Visibility.Visible;
        PbScan.Value = 0;
        TxtScanPercent.Text = "0 %";
        TxtScanTitle.Text = "Analyse du registre en cours";
        TxtScanStage.Text = "Préparation de l'analyse…";
        ResetCounters();

        try
        {
            RegistryWindowsIdentity windows = ReadWindowsIdentity();
            var backend = new WindowsRegistryBackend();
            var inspector = new RegistryContextInspector(backend);
            IReadOnlyList<RegistryInspectionFinding> findings = await Task.Run(() =>
                inspector.Inspect(windows, ReportProgress));

            RenderResults(findings);
            TxtScanTitle.Text = findings.Count == 0
                ? "Aucun écart détecté dans le périmètre analysé"
                : $"{findings.Count} constat{(findings.Count > 1 ? "s" : string.Empty)} à examiner";
            TxtScanStage.Text = "6 zones analysées · aucune valeur modifiée";
            PbScan.Value = 100;
            TxtScanPercent.Text = "100 %";
        }
        catch (Exception exception)
        {
            TxtScanTitle.Text = "Analyse interrompue";
            TxtScanStage.Text = "Le registre n'a pas été modifié. " + exception.Message;
            TxtScanStage.SetResourceReference(ForegroundProperty, "ThCrit");
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            _scanRunning = false;
            BtnAnalyze.IsEnabled = true;
            PbScan.Visibility = Visibility.Collapsed;
        }
    }

    private void ReportProgress(RegistryInspectionProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            double percent = progress.TotalStages == 0
                ? 0
                : progress.CompletedStages * 100d / progress.TotalStages;
            PbScan.Value = percent;
            TxtScanPercent.Text = $"{percent:0} %";
            TxtScanStage.Text = StageName(progress.Stage);
        });
    }

    private void RenderResults(IReadOnlyList<RegistryInspectionFinding> findings)
    {
        int malformed = findings.Count(item => item.Status == RegistryInspectionStatus.Malformed);
        int review = findings.Count(item => item.Status == RegistryInspectionStatus.Review);
        int unreadable = findings.Count(item => item.Status == RegistryInspectionStatus.Unreadable);
        TxtTotal.Text = findings.Count.ToString();
        TxtMalformed.Text = malformed.ToString();
        TxtReview.Text = review.ToString();
        TxtUnreadable.Text = unreadable.ToString();

        if (findings.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            SetEmptyState(
                "Aucun écart détecté",
                "Ce résultat couvre uniquement les zones documentées analysées par Tweakly.");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        foreach (IGrouping<RegistryInspectionCategory, RegistryInspectionFinding> group in
                 findings.GroupBy(item => item.Category))
        {
            var category = new TextBlock
            {
                Text = CategoryName(group.Key).ToUpperInvariant(),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, ResultsPanel.Children.Count == 0 ? 0 : 12, 0, 7),
            };
            category.SetResourceReference(ForegroundProperty, "ThTextDim");
            ResultsPanel.Children.Add(category);

            foreach (RegistryInspectionFinding finding in group)
                ResultsPanel.Children.Add(BuildFindingCard(finding));
        }
    }

    private Border BuildFindingCard(RegistryInspectionFinding finding)
    {
        var card = new Border
        {
            Style = (Style)FindResource("RegistryCard"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var content = new StackPanel();
        card.Child = content;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border status = BuildStatusPill(finding.Status);
        header.Children.Add(status);

        var title = new TextBlock
        {
            Text = FindingTitle(finding.Code),
            FontFamily = (FontFamily)FindResource("AppFont"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(ForegroundProperty, "ThTextTitle");
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var readOnly = new TextBlock
        {
            Text = "LECTURE SEULE",
            FontFamily = (FontFamily)FindResource("AppFont"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        readOnly.SetResourceReference(ForegroundProperty, "ThAccentIcon");
        readOnly.SetResourceReference(BackgroundProperty, "ThPill");
        Grid.SetColumn(readOnly, 2);
        header.Children.Add(readOnly);
        content.Children.Add(header);

        var address = new TextBlock
        {
            Text = FormatAddress(finding.Address),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 9, 0, 0),
        };
        address.SetResourceReference(ForegroundProperty, "ThTextDim");
        content.Children.Add(address);

        foreach (string evidence in finding.Evidence.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var line = new TextBlock
            {
                Text = "• " + EvidenceText(evidence),
                FontFamily = (FontFamily)FindResource("AppFont"),
                FontSize = 10.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            line.SetResourceReference(ForegroundProperty, "ThTextBody");
            content.Children.Add(line);
        }

        var source = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("AppFont"),
            FontSize = 10,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var link = new Hyperlink(new Run("Documentation Microsoft"))
        {
            NavigateUri = finding.Source,
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "ThAccentIcon");
        link.RequestNavigate += (_, args) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TxtScanStage.Text = "Impossible d'ouvrir la documentation Microsoft.";
                TxtScanStage.SetResourceReference(ForegroundProperty, "ThWarn");
                Helpers.AppLog.Error("Registre Windows : ouverture de la documentation", ex);
            }
            args.Handled = true;
        };
        source.Inlines.Add(link);
        content.Children.Add(source);
        return card;
    }

    private Border BuildStatusPill(RegistryInspectionStatus status)
    {
        string text;
        string foreground;
        string background;
        string border;
        switch (status)
        {
            case RegistryInspectionStatus.Malformed:
                text = "DONNÉE INVALIDE";
                foreground = "ThCrit";
                background = "ThCritTint";
                border = "ThCritBorderTint";
                break;
            case RegistryInspectionStatus.Review:
                text = "À VÉRIFIER";
                foreground = "ThWarn";
                background = "ThWarnTint";
                border = "ThWarnBorderTint";
                break;
            default:
                text = "NON LU";
                foreground = "ThTextBody";
                background = "ThPill";
                border = "ThBorder";
                break;
        }

        var label = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)FindResource("AppFont"),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
        };
        label.SetResourceReference(ForegroundProperty, foreground);
        var pill = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4, 8, 4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pill.SetResourceReference(BackgroundProperty, background);
        pill.SetResourceReference(BorderBrushProperty, border);
        return pill;
    }

    private void ResetCounters()
    {
        TxtTotal.Text = "—";
        TxtMalformed.Text = "—";
        TxtReview.Text = "—";
        TxtUnreadable.Text = "—";
        TxtScanStage.SetResourceReference(ForegroundProperty, "ThTextDim");
    }

    private void SetEmptyState(string title, string detail)
    {
        if (EmptyState.Child is not StackPanel panel || panel.Children.Count < 3)
            return;
        if (panel.Children[1] is TextBlock titleBlock)
            titleBlock.Text = title;
        if (panel.Children[2] is TextBlock detailBlock)
            detailBlock.Text = detail;
    }

    private static RegistryWindowsIdentity ReadWindowsIdentity()
    {
        int build = Environment.OSVersion.Version.Build;
        string edition = "Unknown";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                writable: false);
            string? buildText = Convert.ToString(key?.GetValue("CurrentBuildNumber"));
            if (int.TryParse(buildText, out int parsedBuild))
                build = parsedBuild;
            edition = Convert.ToString(key?.GetValue("EditionID"))?.Trim() ?? edition;
        }
        catch
        {
            // The contextual inspector only needs the build metadata for display/rule scope.
        }

        return new RegistryWindowsIdentity(build, edition, Environment.Is64BitOperatingSystem);
    }

    private static string StageName(string stage) => stage switch
    {
        "Startup entries" => "Entrées de démarrage",
        "AppInit" => "Chargement AppInit",
        "Image File Execution Options" => "Options d'exécution des applications",
        "Winlogon" => "Ouverture de session Windows",
        "Windows services" => "Services Windows",
        "File associations" => "Associations de fichiers",
        "Complete" => "Analyse terminée",
        _ => stage,
    };

    private static string CategoryName(RegistryInspectionCategory category) => category switch
    {
        RegistryInspectionCategory.Startup => "Démarrage automatique",
        RegistryInspectionCategory.AppInit => "AppInit",
        RegistryInspectionCategory.ImageFileExecutionOptions => "Options d'exécution",
        RegistryInspectionCategory.Winlogon => "Ouverture de session",
        RegistryInspectionCategory.Service => "Services Windows",
        RegistryInspectionCategory.FileAssociation => "Associations de fichiers",
        _ => category.ToString(),
    };

    private static string FindingTitle(string code) => code switch
    {
        "STARTUP_VALUE_MALFORMED" => "Entrée de démarrage illisible",
        "STARTUP_COMMAND_EMPTY" => "Commande de démarrage vide",
        "STARTUP_KEY_UNREADABLE" => "Zone de démarrage inaccessible",
        "APPINIT_LOAD_FLAG_MALFORMED" => "Activation AppInit invalide",
        "APPINIT_DLL_LIST_MALFORMED" => "Liste AppInit illisible",
        "APPINIT_SIGNATURE_FLAG_MALFORMED" => "Contrôle de signature AppInit invalide",
        "APPINIT_DLLS_ACTIVE" => "Chargement AppInit actif",
        "APPINIT_KEY_UNREADABLE" => "Configuration AppInit inaccessible",
        "IFEO_DEBUGGER_MALFORMED" => "Débogueur IFEO illisible",
        "IFEO_DEBUGGER_CONFIGURED" => "Débogueur IFEO configuré",
        "IFEO_KEY_UNREADABLE" or "IFEO_IMAGE_KEY_UNREADABLE" => "Configuration IFEO inaccessible",
        "WINLOGON_VALUE_MALFORMED" => "Valeur Winlogon illisible",
        "WINLOGON_SHELL_REVIEW" => "Shell Windows personnalisé",
        "WINLOGON_USERINIT_REVIEW" => "Commande Userinit personnalisée",
        "WINLOGON_KEY_UNREADABLE" => "Configuration Winlogon inaccessible",
        "SERVICE_CONTROL_SET_MALFORMED" => "ControlSet actif invalide",
        "SERVICE_START_MALFORMED" => "Mode de démarrage de service invalide",
        "SERVICE_TYPE_MALFORMED" => "Type de service invalide",
        "SERVICE_IMAGE_PATH_MALFORMED" => "Chemin de service illisible",
        "SERVICE_CONTROL_SET_UNREADABLE" or "SERVICE_ROOT_UNREADABLE" or "SERVICE_KEY_UNREADABLE" => "Configuration de service inaccessible",
        "FILE_ASSOCIATION_VALUE_MALFORMED" => "Association de fichier illisible",
        "FILE_ASSOCIATION_PROGID_MISSING" => "Programme associé introuvable",
        "FILE_ASSOCIATION_PROGID_UNREADABLE" => "Programme associé non vérifiable",
        "FILE_ASSOCIATION_ROOT_UNREADABLE" or "FILE_ASSOCIATION_KEY_UNREADABLE" => "Associations de fichiers inaccessibles",
        _ => code,
    };

    private static string EvidenceText(string evidence) => evidence
        .Replace("Type=", "Type : ", StringComparison.Ordinal)
        .Replace("Bytes=", "Taille : ", StringComparison.Ordinal)
        .Replace("Service=", "Service : ", StringComparison.Ordinal)
        .Replace("Extension=", "Extension : ", StringComparison.Ordinal)
        .Replace("ProgID=", "ProgID : ", StringComparison.Ordinal)
        .Replace("Image=", "Application : ", StringComparison.Ordinal)
        .Replace("Debugger=", "Débogueur : ", StringComparison.Ordinal)
        .Replace("Configured value=", "Valeur configurée : ", StringComparison.Ordinal)
        .Replace("Configured DLLs=", "DLL configurées : ", StringComparison.Ordinal);

    private static string FormatAddress(RegistryAddress address)
    {
        string hive = address.Hive == RegistryHiveId.LocalMachine ? "HKLM" : "HKCU";
        string view = address.View == RegistryViewId.Registry64 ? "64 bits" : "32 bits";
        string value = string.IsNullOrEmpty(address.ValueName) ? "(par défaut)" : address.ValueName;
        return $"{hive}\\{address.KeyPath}  ·  {value}  ·  {view}";
    }
}
