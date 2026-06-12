using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Optimisation_Tool
{
    /// <summary>
    /// Visualiseur intégré du journal technique (config\tweakly-log.txt) — v1.3.5.
    /// Affiche les ~800 dernières lignes (le fichier est borné à ~1 Mo par AppLog,
    /// mais inutile de charger 1 Mo dans un TextBox), défile en bas (le récent
    /// d'abord visible), Actualiser / Tout copier / Ouvrir le dossier.
    /// </summary>
    public partial class LogViewerWindow : Window
    {
        private const int MaxLines = 800;

        public LogViewerWindow(Window owner)
        {
            InitializeComponent();
            Owner = owner;
            LoadLog();
        }

        private void LoadLog()
        {
            try
            {
                var f = Helpers.AppLog.LogFile;
                if (!File.Exists(f))
                {
                    TxtLogContent.Text = "(aucun journal pour l'instant — il se remplit au fil de l'utilisation)";
                    TxtLogInfo.Text = "";
                    return;
                }

                // Lecture en partage (AppLog peut écrire en même temps)
                string[] lines;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    lines = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');

                int skip = Math.Max(0, lines.Length - MaxLines);
                TxtLogContent.Text = string.Join(Environment.NewLine, lines, skip, lines.Length - skip);
                TxtLogContent.ScrollToEnd();   // le plus récent est en bas

                var fi = new FileInfo(f);
                TxtLogInfo.Text = $"{fi.Length / 1024.0:F0} Ko"
                                + (skip > 0 ? $"  ·  {MaxLines} dernières lignes affichées" : "")
                                + $"  ·  {fi.LastWriteTime:dd/MM HH:mm}";
            }
            catch (Exception ex)
            {
                TxtLogContent.Text = "Impossible de lire le journal : " + ex.Message;
            }
        }

        private void BtnLogRefresh_Click(object sender, RoutedEventArgs e) => LoadLog();

        private void BtnLogCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtLogContent.Text);
                BtnLogCopy.Content = "Copié ✓";
            }
            catch { }
        }

        private void BtnLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe",
                    $"/select,\"{Helpers.AppLog.LogFile}\"");
            }
            catch { }
        }

        private void BtnCloseLog_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Fenêtre sans bordure → draggable partout (les boutons/TextBox gardent leurs clics)
            if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox)
            {
                try { DragMove(); } catch { }
            }
        }
    }
}
