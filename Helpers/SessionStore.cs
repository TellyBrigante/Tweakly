using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Historique local des sessions de jeu mesurées (JSON dans config\, jamais
    /// dans le ZIP MAJ). Stocke uniquement le RAPPORT analysé, pas le CSV brut
    /// (qui pèse plusieurs Mo pour 3 minutes — on le jette après analyse).
    /// </summary>
    public static class SessionStore
    {
        public static string FilePath => PathLayout.SessionsFile;

        private static readonly JsonSerializerOptions _opt = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            // Indispensable : un rapport contient des NaN/Infinity légitimes (latence
            // input non mesurée, CPU global sans sample…). Sans ça, System.Text.Json
            // jette à l'écriture → aucune session sauvegardée (bug de persistance vécu).
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static List<SessionAnalyzer.Report> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<SessionAnalyzer.Report>>(File.ReadAllText(FilePath), _opt) ?? new();
            }
            catch (Exception ex)
            {
                AppLog.Write("SessionStore.Load : " + ex.Message);
                return new();
            }
        }

        public static void Save(List<SessionAnalyzer.Report> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? "");
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, _opt));
                AppLog.Write($"SessionStore.Save : {list.Count} sessions ecrites -> {FilePath}");
            }
            catch (Exception ex)
            {
                AppLog.Write("SessionStore.Save ERREUR : " + ex.Message + " | path=" + FilePath);
            }
        }

        public static void Append(SessionAnalyzer.Report r)
        {
            var list = Load();
            list.Add(r);
            // Garde les 30 dernières (les rapports incluent la liste des drops → ça peut grossir)
            if (list.Count > 30) list = list.OrderBy(x => x.CapturedAtUtc).TakeLast(30).ToList();
            Save(list);
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }
}
