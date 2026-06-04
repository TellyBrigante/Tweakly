using System;
using System.Collections.Generic;
using System.Text;
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace Optimisation_Tool.Helpers
{
    /// <summary>Une option d'un menu déroulant : valeur brute pilote + libellé FR.</summary>
    public sealed class NvOption
    {
        public uint   Value;
        public string Label;
        public NvOption(uint value, string label) { Value = value; Label = label; }
        public override string ToString() => Label;
    }

    /// <summary>Un réglage NVIDIA exposé (catalogue curé façon NVIDIA App) + ses options éditables.</summary>
    public sealed class NvCatalogEntry
    {
        public uint           Id;
        public string         Category;
        public string         Name;
        public List<NvOption> Options;
        public NvCatalogEntry(uint id, string category, string name, List<NvOption> options)
        {
            Id = id; Category = category; Name = name; Options = options;
        }
    }

    /// <summary>
    /// Lecture ET écriture des réglages du pilote NVIDIA via NVAPI (NvAPIWrapper, LGPL v3).
    /// Tout en try/catch : sans GPU NVIDIA / pilote incompatible → dégradé propre (jamais
    /// d'exception qui remonte). L'onglet Nvidia étant chargé à la demande, ça ne peut pas
    /// toucher le démarrage ni la MAJ.
    /// </summary>
    public static class NvidiaDriverSettings
    {
        private static List<NvOption> O(params NvOption[] opts) => new(opts);
        private static NvOption V(uint v, string l) => new(v, l);

        // Catalogue curé façon NVIDIA App. Les VALEURS proviennent des enums autoritaires de NvAPIWrapper
        // (NvAPIWrapper.DRS.SettingValues.*) → garanties valides par le pilote (plus de « Personnalisé »).
        // Les libellés FR suivent la terminologie NVIDIA App. Réglages dont la valeur n'était pas vérifiable
        // RETIRÉS volontairement (G-SYNC, limite FPS, qualité textures, CUDA, OpenGL/GDI, Vulkan) → à rajouter
        // plus tard une fois leurs valeurs validées.
        public static readonly List<NvCatalogEntry> Catalog = new()
        {
            // ── Latence & synchronisation ──
            new(8102046u, "Latence & synchronisation", "Mode faible latence",
                O(V(0,"Désactivé"), V(1,"Activé"), V(2,"Ultra"))),
            new(11041231u, "Latence & synchronisation", "Synchronisation verticale (V-Sync)",
                O(V(138504007,"Désactivée"), V(1199655232,"Activée"), V(411601032,"Rapide"))),
            new(553505273u, "Latence & synchronisation", "Triple buffering (OpenGL)",
                O(V(0,"Désactivé"), V(1,"Activé"))),

            // ── Performance & FPS ──
            new(274197361u, "Performance & FPS", "Mode de gestion de l'alimentation",
                O(V(0,"Normal"), V(1,"Privilégier les performances maximales"))),
            new(6600001u, "Performance & FPS", "Fréquence de rafraîchissement préférée",
                O(V(0,"Par défaut de l'application"), V(1,"La plus élevée disponible"))),
            new(549528094u, "Performance & FPS", "Optimisation multithread",
                O(V(0,"Auto"), V(1,"Activée"), V(2,"Désactivée"))),

            // ── DLSS & upscaling ──
            new(283385345u, "DLSS & upscaling", "DLSS — override des préréglages",
                O(V(0,"Désactivé"), V(1,"Activé"))),
            new(283385331u, "DLSS & upscaling", "DLSS — préréglage Super Resolution",
                O(V(0,"Par défaut (dernier)"), V(10,"Preset J"), V(11,"Preset K"), V(12,"Preset L"), V(13,"Preset M"))),

            // ── Qualité d'image & textures ──
            new(282245910u, "Qualité d'image & textures", "Filtrage anisotrope — mode",
                O(V(0,"Réglage de l'application 3D"), V(1,"Remplacer le paramètre de l'application"))),
            new(270426537u, "Qualité d'image & textures", "Filtrage anisotrope — niveau",
                O(V(1,"Désactivé"), V(2,"2x"), V(4,"4x"), V(8,"8x"), V(16,"16x"))),
            new(1686376u, "Qualité d'image & textures", "Biais LOD négatif",
                O(V(0,"Autoriser"), V(1,"Limiter"))),
            new(3066610u, "Qualité d'image & textures", "Optimisation trilinéaire",
                O(V(0,"Désactivée"), V(1,"Activée"))),
            new(15151633u, "Qualité d'image & textures", "Optimisation d'échantillons anisotropes",
                O(V(0,"Désactivée"), V(1,"Activée"))),

            // ── Anticrénelage ──
            new(276757595u, "Anticrénelage", "Antialiasing — mode",
                O(V(0,"Contrôlé par l'application 3D"), V(2,"Améliorer le paramètre de l'application"), V(1,"Remplacer le paramètre de l'application"))),
            new(276652957u, "Anticrénelage", "Antialiasing — correction gamma",
                O(V(0,"Désactivée"), V(2,"Activée"))),
            new(282364549u, "Anticrénelage", "Antialiasing — transparence",
                O(V(0,"Désactivée"), V(4,"Activée"))),
            new(276089202u, "Anticrénelage", "FXAA",
                O(V(0,"Désactivé"), V(1,"Activé"))),
            new(10011052u, "Anticrénelage", "MFAA",
                O(V(0,"Désactivé"), V(1,"Activé"))),
        };

        /// <summary>Indique si un GPU NVIDIA + pilote exploitable est présent.</summary>
        public static bool IsAvailable()
        {
            try { NVIDIA.Initialize(); return true; }
            catch { return false; }
            finally { try { NVIDIA.Unload(); } catch { } }
        }

        /// <summary>Lit les valeurs courantes du profil GLOBAL (ID → valeur uint). Vide si indisponible.</summary>
        public static Dictionary<uint, uint> ReadCurrentValues()
        {
            var result = new Dictionary<uint, uint>();
            try
            {
                NVIDIA.Initialize();
                try
                {
                    using var session = DriverSettingsSession.CreateAndLoad();
                    foreach (var s in session.CurrentGlobalProfile.Settings)
                    {
                        try { result[(uint)s.SettingId] = Convert.ToUInt32(s.CurrentValue); } catch { }
                    }
                }
                finally { try { NVIDIA.Unload(); } catch { } }
            }
            catch { }
            return result;
        }

        /// <summary>Écrit les réglages donnés (ID → valeur) dans le profil GLOBAL et sauvegarde. Renvoie true si OK.</summary>
        public static bool WriteGlobal(Dictionary<uint, uint> values)
        {
            if (values.Count == 0) return true;
            try
            {
                NVIDIA.Initialize();
                try
                {
                    using var session = DriverSettingsSession.CreateAndLoad();
                    var g = session.CurrentGlobalProfile;
                    foreach (var kv in values) g.SetSetting(kv.Key, kv.Value);
                    session.Save();
                    return true;
                }
                finally { try { NVIDIA.Unload(); } catch { } }
            }
            catch { return false; }
        }

        /// <summary>
        /// Génère le contenu d'un profil .nip (importable par nvidiaProfileInspector) à partir des
        /// réglages donnés (ID → valeur). Profil GLOBAL = ProfileName « Base Profile », sans exécutable.
        /// </summary>
        public static string BuildGlobalNip(Dictionary<uint, uint> settings)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n");
            sb.Append("<ArrayOfProfile>\r\n");
            sb.Append("  <Profile>\r\n");
            sb.Append("    <ProfileName>Base Profile</ProfileName>\r\n");
            sb.Append("    <Executeables />\r\n");
            sb.Append("    <Settings>\r\n");
            foreach (var e in Catalog)
            {
                if (!settings.TryGetValue(e.Id, out var v)) continue;
                sb.Append("      <ProfileSetting>\r\n");
                sb.Append($"        <SettingNameInfo>{System.Security.SecurityElement.Escape(e.Name)}</SettingNameInfo>\r\n");
                sb.Append($"        <SettingID>{e.Id}</SettingID>\r\n");
                sb.Append($"        <SettingValue>{v}</SettingValue>\r\n");
                sb.Append("        <ValueType>Dword</ValueType>\r\n");
                sb.Append("      </ProfileSetting>\r\n");
            }
            sb.Append("    </Settings>\r\n");
            sb.Append("  </Profile>\r\n");
            sb.Append("</ArrayOfProfile>\r\n");
            return sb.ToString();
        }
    }
}
