using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Optimisation_Tool.Helpers
{
    public static class JsonSettingsEditor
    {
        public static void SetBooleanAtomically(string path, string propertyName, bool value)
        {
            JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"{Path.GetFileName(path)} ne contient pas un objet JSON valide.");
            root[propertyName] = value;

            string temp = path + ".tweakly.tmp";
            try
            {
                File.WriteAllText(
                    temp,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);

                JsonObject verify = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new InvalidDataException($"{Path.GetFileName(path)} est devenu illisible après l'écriture.");
                if (verify[propertyName]?.GetValue<bool>() != value)
                    throw new IOException($"La valeur {propertyName} n'a pas été conservée.");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
