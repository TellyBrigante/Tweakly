using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using Optimisation_Tool.Pages;

namespace Optimisation_Tool
{
    public partial class MainWindow
    {
        private string? _pendingBat;
        private DispatcherTimer? _updateWatcher;
        private CancellationTokenSource? _updCts;
        private string _declinedTag = "";
        private string _updTag = "";

        private async Task CheckUpdateSilentAsync()
        {
            try
            {
                await Task.Delay(2500);
                await TryOfferUpdateAsync();
            }
            catch (Exception ex) { Log($"MAJ auto : erreur — {ex.Message}"); }
        }

        private void StartUpdateWatcher()
        {
            _updateWatcher = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _updateWatcher.Tick += async (_, _) => await TryOfferUpdateAsync();
            _updateWatcher.Start();
        }

        private async Task TryOfferUpdateAsync()
        {
            if (!Settings.AutoUpdate || UpdateOverlay.Visibility == Visibility.Visible) return;
            try
            {
                var (hasUpdate, tag, _, assetUrl, sha256, notes) = await PageReglages.CheckForUpdateAsync();
                if (hasUpdate && !string.IsNullOrEmpty(assetUrl))
                {
                    if (tag == _declinedTag) return;
                    Log($"Mise à jour disponible : {tag}.");
                    StartUpdate(assetUrl, tag, sha256, notes);
                }
            }
            catch (Exception ex) { Log($"MAJ auto : erreur — {ex.Message}"); }
        }

        public async void StartUpdate(string assetUrl, string tag, string sha256 = "", string notes = "")
        {
            _updTag = tag;
            _updCts?.Dispose();
            _updCts = new CancellationTokenSource();

            try
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                ForceForeground();
            }
            catch { }

            UpdateOverlay.Visibility = Visibility.Visible;
            TxtUpdTitle.Text = "Une mise à jour est disponible";
            RunUpdFrom.Text = $"v{PageReglages.AppVersion}";
            RunUpdTo.Text = $"v{tag.TrimStart('v', 'V')}";
            TxtUpdStatus.Text = "Téléchargement de la mise à jour…";
            BtnUpdContinue.Visibility = Visibility.Collapsed;
            BtnUpdLater.Visibility = Visibility.Visible;
            SetUpdateBar(0);
            SetUpdateNotes(notes);

            try
            {
                var progress = new Progress<double>(SetUpdateBar);
                _pendingBat = await PageReglages.PrepareUpdateAsync(assetUrl, progress, sha256, _updCts.Token);
                SetUpdateBar(100);
                TxtUpdStatus.Text = "Téléchargement terminé — prête à installer.";
                BtnUpdContinue.Visibility = Visibility.Visible;
                Log($"Mise à jour {tag} téléchargée — en attente de redémarrage.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                TxtUpdStatus.Text = $"Échec du téléchargement : {ex.Message}";
                Log($"MAJ : erreur téléchargement — {ex.Message}");
            }
        }

        private void BtnUpdLater_Click(object sender, RoutedEventArgs e)
        {
            try { _updCts?.Cancel(); } catch { }
            _pendingBat = null;
            _declinedTag = _updTag;
            UpdateOverlay.Visibility = Visibility.Collapsed;
            ClearTaskbarProgress();
            try
            {
                var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Tweakly_update");
                if (System.IO.Directory.Exists(temp)) System.IO.Directory.Delete(temp, true);
            }
            catch { }
            Log($"Mise à jour {_updTag} reportée — tu restes sur la v{PageReglages.AppVersion}. " +
                "Elle sera re-proposée au prochain démarrage (ou via Réglages > Vérifier).");
        }

        private void SetUpdateNotes(string notes)
        {
            TxtUpdNotes.Inlines.Clear();
            if (string.IsNullOrWhiteSpace(notes))
            {
                UpdNotesCard.Visibility = Visibility.Collapsed;
                return;
            }

            bool first = true;
            foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                if (!first) TxtUpdNotes.Inlines.Add(new System.Windows.Documents.LineBreak());
                first = false;

                var text = line.TrimStart();
                if (text.StartsWith("- ") || text.StartsWith("* ") || text.StartsWith("• "))
                {
                    var bullet = new System.Windows.Documents.Run("•  ") { FontWeight = FontWeights.Bold };
                    bullet.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ThAccentIcon");
                    TxtUpdNotes.Inlines.Add(bullet);
                    TxtUpdNotes.Inlines.Add(new System.Windows.Documents.Run(text.Substring(2).TrimStart()));
                }
                else
                {
                    TxtUpdNotes.Inlines.Add(new System.Windows.Documents.Run(line));
                }
            }
            UpdNotesCard.Visibility = Visibility.Visible;
        }

        private void SetUpdateBar(double percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            UpdBar.ColumnDefinitions[0].Width = new GridLength(percent, GridUnitType.Star);
            UpdBar.ColumnDefinitions[1].Width = new GridLength(100 - percent, GridUnitType.Star);
            TxtUpdPct.Text = $"{percent:F0} %";
        }

        private void UpdateOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && WindowState == WindowState.Normal)
            {
                try { DragMove(); } catch { }
            }
        }

        private void BtnUpdContinue_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingBat))
                PageReglages.LaunchUpdaterAndExit(_pendingBat);
        }

        public void SetTaskbarProgress(double percent)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                TaskbarInfo.ProgressValue = Math.Max(0, Math.Min(1, percent / 100.0));
            });
        }

        public void ClearTaskbarProgress()
        {
            Dispatcher.BeginInvoke(() =>
            {
                TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                TaskbarInfo.ProgressValue = 0;
            });
        }
    }
}
