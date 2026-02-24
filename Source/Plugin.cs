using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using cameraTools.Replay;
using HarmonyLib;
using UnityEngine;

namespace cameraTools
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class cameraToolsPlugin : BaseUnityPlugin
    {
        private const string ModName = "cameraTools";
        private const string ModVersion = "1.0.0";
        private const string Author = "modAuthorName";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = BepInEx.Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        private readonly Harmony HarmonyInstance = new(ModGUID);

        public static readonly ManualLogSource TemplateLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        public static ConfigEntry<KeyboardShortcut> PanelToggleKey = null!;
        public static ConfigEntry<float> DefaultSmoothness = null!;

        public void Awake()
        {
            PanelToggleKey = Config.Bind("UI", "PanelToggleKey", new KeyboardShortcut(KeyCode.F6),
                "Hotkey to toggle the camera tools panel");
            DefaultSmoothness = Config.Bind("Camera", "DefaultSmoothness", 0.5f,
                new ConfigDescription("Default free fly smoothness", new AcceptableValueRange<float>(0f, 1f)));

            Assembly assembly = Assembly.GetExecutingAssembly();
            HarmonyInstance.PatchAll(assembly);
            SetupWatcher();

            gameObject.AddComponent<FreeFlyPanel>();
            gameObject.AddComponent<ReplayRecorder>();
            gameObject.AddComponent<ReplayPlayer>();
            gameObject.AddComponent<ReplayTimelinePanel>();
            ReplayCommands.Register();
        }

        private void OnDestroy()
        {
            Config.Save();
        }
        
        private void SetupWatcher()
        {
            _lastReloadTime = DateTime.Now;
            FileSystemWatcher watcher = new(BepInEx.Paths.ConfigPath, ConfigFileName);
            // Due to limitations of technology this can trigger twice in a row
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
        }

        private DateTime _lastReloadTime;
        private const long RELOAD_DELAY = 10000000; // One second

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.Now;
            var time = now.Ticks - _lastReloadTime.Ticks;
            if (!File.Exists(ConfigFileFullPath) || time < RELOAD_DELAY) return;

            try
            {
                TemplateLogger.LogInfo("Attempting to reload configuration...");
                Config.Reload();
                TemplateLogger.LogInfo("Configuration reloaded successfully!");
            }
            catch
            {
                TemplateLogger.LogError($"There was an issue loading {ConfigFileName}");
                return;
            }

            _lastReloadTime = now;

            // Update any runtime configurations here
            if (ZNet.instance != null && !ZNet.instance.IsDedicated())
            {
                TemplateLogger.LogInfo("Updating runtime configurations...");
                // Add your configuration update logic here
            }
        }
    }
} 