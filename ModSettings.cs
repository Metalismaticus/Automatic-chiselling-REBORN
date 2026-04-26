using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Client;

namespace AutomaticChiselling
{
    /// <summary>
    /// User-tunable mod settings, persisted to autochisel/settings.json.
    /// Loaded on first access; call Save() after any change.
    /// </summary>
    public class ModSettings
    {
        // === Speed control (separate SP / MP tracks) ===
        // In multiplayer every chisel packet goes to a remote server, which can lag
        // or desync — so defaults are more conservative there. In singleplayer the
        // "server" is in-process and tolerates higher rates.

        /// <summary>Upper bound on ops-per-tick in SINGLEPLAYER.</summary>
        public int MaxOpsPerTickSP { get; set; } = 12;

        /// <summary>Starting ops-per-tick in SINGLEPLAYER (ramp-up target).</summary>
        public int InitialOpsPerTickSP { get; set; } = 3;

        /// <summary>Upper bound on ops-per-tick in MULTIPLAYER.</summary>
        public int MaxOpsPerTickMP { get; set; } = 6;

        /// <summary>Starting ops-per-tick in MULTIPLAYER.</summary>
        public int InitialOpsPerTickMP { get; set; } = 1;

        /// <summary>
        /// If true, the conveyor adjusts speed based on success streak and lag.
        /// If false, speed stays fixed at InitialOpsPerTick (useful for slow
        /// servers or manual rate-limiting). Shared between SP and MP.
        /// </summary>
        public bool AdaptiveSpeed { get; set; } = true;

        // === Singleton / persistence ===

        private static ModSettings _instance;
        public static ModSettings Instance
        {
            get
            {
                if (_instance == null) _instance = Load();
                return _instance;
            }
        }

        private static string FilePath => Path.Combine(ModPaths.Root, "settings.json");

        private static ModSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonConvert.DeserializeObject<ModSettings>(json);
                    if (loaded != null) return Clamp(loaded);
                }
            }
            catch { /* corrupt or unreadable — fall through to defaults */ }
            return new ModSettings();
        }

        public void Save(ICoreClientAPI capi = null)
        {
            try
            {
                Clamp(this);
                Directory.CreateDirectory(ModPaths.Root);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi?.Logger.Warning("[AutoChisel] Failed to save settings: " + e.Message);
            }
        }

        private static ModSettings Clamp(ModSettings s)
        {
            if (s.MaxOpsPerTickSP < 1) s.MaxOpsPerTickSP = 1;
            if (s.MaxOpsPerTickSP > 64) s.MaxOpsPerTickSP = 64;
            if (s.InitialOpsPerTickSP < 1) s.InitialOpsPerTickSP = 1;
            if (s.InitialOpsPerTickSP > s.MaxOpsPerTickSP) s.InitialOpsPerTickSP = s.MaxOpsPerTickSP;

            if (s.MaxOpsPerTickMP < 1) s.MaxOpsPerTickMP = 1;
            if (s.MaxOpsPerTickMP > 64) s.MaxOpsPerTickMP = 64;
            if (s.InitialOpsPerTickMP < 1) s.InitialOpsPerTickMP = 1;
            if (s.InitialOpsPerTickMP > s.MaxOpsPerTickMP) s.InitialOpsPerTickMP = s.MaxOpsPerTickMP;
            return s;
        }
    }
}
