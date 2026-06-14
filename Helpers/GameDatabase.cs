using System;
using System.Collections.Generic;
using System.Linq;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Base des jeux les plus joués (FR + monde) — utilisée pour :
    /// (a) reconnaître le jeu capturé via le nom de son exe ;
    /// (b) appliquer des seuils/conseils spécifiques (UE3 = shader hitches normaux,
    ///     jeux compétitifs = sensibles aux 1 % low, etc.) ;
    /// (c) afficher un nom convivial dans l'historique.
    ///
    /// ⚠ Ne JAMAIS deviner un jeu — si l'exe n'est pas dans la base, l'analyseur
    /// utilise les seuils génériques et le DIT honnêtement.
    /// </summary>
    public static class GameDatabase
    {
        public sealed class Game
        {
            public string Exe = "";          // nom d'exe (sans dossier, sensible casse OFF)
            public string Display = "";      // nom convivial
            public GameEngine Engine = GameEngine.Unknown;
            public GameKind Kind = GameKind.Generic;
            /// <summary>fps cible « jouable » (au-dessous = ressenti immédiat).</summary>
            public int PlayableFps = 60;
            /// <summary>fps de référence sur un joueur compétitif (au-dessous = on perd des coups).</summary>
            public int CompetitiveFps = 144;
            /// <summary>Solutions COMMUNAUTAIRES DOCUMENTÉES spécifiques à ce jeu, ressorties quand
            /// l'analyse détecte des drops massifs sans coupable applicatif identifié.</summary>
            public List<KnownFix> KnownFixes = new();
        }

        public sealed class KnownFix
        {
            public string Title = "";
            public string Steps = "";        // marche à suivre concrète
            public string? Url;              // source officielle / guide de référence
        }

        public enum GameEngine { Unknown, UnrealEngine3, UnrealEngine4, UnrealEngine5, Source, Source2, Frostbite, IW, REDengine, Custom }
        public enum GameKind { Generic, CompetitiveShooter, Moba, BattleRoyale, Racing, Sports, Sandbox, Survival, RPG }

        // Liste volontairement courte et triée par popularité réelle (Steam Charts /
        // ActivePlayer / déclarations utilisateur). Pas un dictionnaire des 10 000 jeux
        // existants : seulement ceux qui justifient un traitement spécifique.
        public static readonly List<Game> Games = new()
        {
            new() { Exe="cs2.exe",          Display="Counter-Strike 2",     Engine=GameEngine.Source2,        Kind=GameKind.CompetitiveShooter, PlayableFps=120, CompetitiveFps=240 },
            new() { Exe="RocketLeague.exe", Display="Rocket League",        Engine=GameEngine.UnrealEngine3,  Kind=GameKind.Sports,             PlayableFps=120, CompetitiveFps=240 },
            new() { Exe="VALORANT-Win64-Shipping.exe", Display="Valorant",  Engine=GameEngine.UnrealEngine4,  Kind=GameKind.CompetitiveShooter, PlayableFps=120, CompetitiveFps=240 },
            new() { Exe="FortniteClient-Win64-Shipping.exe", Display="Fortnite", Engine=GameEngine.UnrealEngine5, Kind=GameKind.BattleRoyale,   PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="r5apex.exe",       Display="Apex Legends",         Engine=GameEngine.Source,         Kind=GameKind.BattleRoyale,       PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="r5apex_dx12.exe",  Display="Apex Legends (DX12)",  Engine=GameEngine.Source,         Kind=GameKind.BattleRoyale,       PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="LeagueofLegends.exe", Display="League of Legends", Engine=GameEngine.Custom,         Kind=GameKind.Moba,               PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="Overwatch.exe",    Display="Overwatch 2",          Engine=GameEngine.Custom,         Kind=GameKind.CompetitiveShooter, PlayableFps=120, CompetitiveFps=240 },
            new() { Exe="ModernWarfare.exe",Display="Call of Duty",         Engine=GameEngine.IW,             Kind=GameKind.BattleRoyale,       PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="cod.exe",          Display="Call of Duty",         Engine=GameEngine.IW,             Kind=GameKind.BattleRoyale,       PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="RainbowSix.exe",   Display="Rainbow Six Siege",    Engine=GameEngine.Custom,         Kind=GameKind.CompetitiveShooter, PlayableFps=120, CompetitiveFps=240 },
            new() { Exe="GTA5.exe",         Display="GTA V",                Engine=GameEngine.Custom,         Kind=GameKind.Sandbox,            PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="GTA5_Enhanced.exe",Display="GTA V Enhanced",       Engine=GameEngine.Custom,         Kind=GameKind.Sandbox,            PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="Minecraft.Windows.exe", Display="Minecraft",       Engine=GameEngine.Custom,         Kind=GameKind.Sandbox,            PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="javaw.exe",        Display="Minecraft (Java)",     Engine=GameEngine.Custom,         Kind=GameKind.Sandbox,            PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="RustClient.exe",   Display="Rust",                 Engine=GameEngine.UnrealEngine4,  Kind=GameKind.Survival,           PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="dota2.exe",        Display="Dota 2",               Engine=GameEngine.Source2,        Kind=GameKind.Moba,               PlayableFps=120, CompetitiveFps=144 },
            new() { Exe="PUBG.exe",         Display="PUBG: Battlegrounds",  Engine=GameEngine.UnrealEngine4,  Kind=GameKind.BattleRoyale,       PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="TslGame.exe",      Display="PUBG: Battlegrounds",  Engine=GameEngine.UnrealEngine4,  Kind=GameKind.BattleRoyale,       PlayableFps=60,  CompetitiveFps=144 },
            new() { Exe="WutheringWaves.exe",Display="Wuthering Waves",     Engine=GameEngine.UnrealEngine4,  Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="GenshinImpact.exe",Display="Genshin Impact",       Engine=GameEngine.Custom,         Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="HD2.exe",          Display="Helldivers 2",         Engine=GameEngine.Custom,         Kind=GameKind.Generic,            PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="EldenRing.exe",    Display="Elden Ring",           Engine=GameEngine.Custom,         Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=60 },
            new() { Exe="Marvel-Win64-Shipping.exe", Display="Marvel Rivals", Engine=GameEngine.UnrealEngine5, Kind=GameKind.CompetitiveShooter, PlayableFps=60, CompetitiveFps=144 },
            new() { Exe="Cyberpunk2077.exe",Display="Cyberpunk 2077",       Engine=GameEngine.REDengine,      Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="WoW.exe",          Display="World of Warcraft",    Engine=GameEngine.Custom,         Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="Wow-64.exe",       Display="World of Warcraft",    Engine=GameEngine.Custom,         Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=120 },
            new() { Exe="FFXIV_dx11.exe",   Display="Final Fantasy XIV",    Engine=GameEngine.Custom,         Kind=GameKind.RPG,                PlayableFps=60,  CompetitiveFps=120 },

            // ── Path of Exile 2 — célèbre pour ses freezes / spikes CPU dus au moteur ──
            new()
            {
                Exe = "PathOfExileSteam.exe", Display = "Path of Exile 2",
                Engine = GameEngine.Custom, Kind = GameKind.RPG,
                PlayableFps = 60, CompetitiveFps = 100,
                KnownFixes = new()
                {
                    new() {
                        Title = "Vider le cache de shaders PoE 2",
                        Steps = "Ferme le jeu, supprime le dossier %LOCALAPPDATA%\\PathOfExile2\\ShaderCache (ou ShaderCacheDX12 / ShaderCacheVulkan selon le renderer). Les shaders se recompileront lors de ta prochaine session — les premières minutes saccaderont (normal) puis ça devient fluide. Le cache se corrompt après changement de pilote GPU.",
                        Url = "https://attractmo.de/path-of-exile-2/path-of-exile-2-pc-stuttering-shader-cache-fix",
                    },
                    new() {
                        Title = "Renderer DirectX 12 (pas Vulkan, pas DX11)",
                        Steps = "Dans le launcher PoE 2 ou Options > Vidéo, force le renderer DirectX 12. Vulkan a des spikes CPU connus sur Nvidia, DX11 est obsolète et limite le multithreading.",
                    },
                    new() {
                        Title = "Désactiver la culling dynamique + ombres dynamiques + motion blur",
                        Steps = "Options > Graphismes : Dynamic Culling OFF, Dynamic Shadows OFF (ou Low), Motion Blur OFF, Bloom OFF. Ce sont les 4 réglages historiquement responsables des spikes CPU sur ce moteur — testé par la commu.",
                    },
                    new() {
                        Title = "Plan d'alim Performance ou Performances ultimes",
                        Steps = "Dans Tweakly > Optimisations > CPU, active « Mode Performances Ultimes ». PoE 2 est très sensible aux down-clock CPU agressifs des plans d'alim Équilibré.",
                    },
                },
            },
        };

        /// <summary>Trouve un jeu par nom d'exe (insensible à la casse). null = inconnu.</summary>
        public static Game? Lookup(string exe)
            => Games.FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));

        /// <summary>Apps « bruyantes » à signaler comme coupables potentiels d'interférence.</summary>
        public static readonly HashSet<string> NoisyApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "brave.exe", "chrome.exe", "firefox.exe", "msedge.exe", "opera.exe", "vivaldi.exe",
            "discord.exe", "discordcanary.exe", "discordptb.exe",
            "Spotify.exe", "obs64.exe", "obs32.exe", "obs.exe", "Streamlabs OBS.exe",
            "Telegram.exe", "WhatsApp.exe", "msteams.exe", "Teams.exe",
            "msedgewebview2.exe", "RuntimeBroker.exe",
            "MsMpEng.exe",             // Defender real-time scan — voir CauseEngine
            "SearchIndexer.exe",       // Windows Search — pic d'I/O périodique
            "WmiPrvSE.exe",            // WMI provider — pic CPU connu
            "TiWorker.exe",            // Windows Modules Installer Worker
        };

        /// <summary>Process à exclure du candidat « jeu principal » (utilitaires, jamais des jeux).</summary>
        public static readonly HashSet<string> NeverGameApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "dwm.exe", "explorer.exe", "applicationframehost.exe", "shellexperiencehost.exe",
            "startmenuexperiencehost.exe", "searchhost.exe", "systemsettings.exe",
            "msedgewebview2.exe", "msedge.exe", "chrome.exe", "brave.exe", "firefox.exe",
            "discord.exe", "spotify.exe", "obs64.exe", "obs.exe",
            "steamwebhelper.exe", "EpicGamesLauncher.exe", "Origin.exe", "GalaxyClient.exe",
            "Battle.net.exe", "RiotClientServices.exe", "RiotClientUx.exe",
            "tweakly.exe", "Tweakly.exe", "claude.exe", "Code.exe", "devenv.exe",
            "WindowsTerminal.exe", "powershell.exe", "cmd.exe", "conhost.exe",
        };
    }
}
