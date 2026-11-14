using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace cameraTools.Replay
{
    /// <summary>The main menu's Replays button and the window that lists, checks and opens recordings.</summary>
    public class ReplayBrowser : MonoBehaviour
    {
        private const int WindowId = 98236;
        private static ReplayBrowser? _instance;
        private static bool _visible;
        private Rect _window;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private readonly List<Entry> _entries = new();
        private Entry? _selected;
        private Task<ReplayFile>? _loading;
        private string _message = "";

        private sealed class Entry
        {
            public string Path = "";
            public string Name = "";
            public long Bytes;
            public ReplayFile? Header;
            public string Error = "";
            public Texture2D? Preview;
            public List<string> MissingMods = new();
            public List<string> ChangedMods = new();
        }

        // The byte[] overload, called through a delegate: the compiler also sees a ReadOnlySpan overload
        // that net48 cannot resolve.
        private static readonly Func<Texture2D, byte[], bool, bool> LoadImage = (Func<Texture2D, byte[], bool, bool>)Delegate.CreateDelegate(
            typeof(Func<Texture2D, byte[], bool, bool>), typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }));

        private void Awake() => _instance = this;

        internal static void Open()
        {
            if (_instance == null) return;
            _visible = true;
            _instance._message = "";
            _instance.Refresh();
            SetMenuVisible(false);
        }

        internal static void Close()
        {
            _visible = false;
            SetMenuVisible(true);
        }

        private static void SetMenuVisible(bool visible)
        {
            if (FejdStartup.instance != null && FejdStartup.instance.m_menuList != null) FejdStartup.instance.m_menuList.SetActive(visible);
        }

        /// <summary>Starts a session for a recording file; used by the browser and the replay command.</summary>
        internal static string OpenSession(string path)
        {
            if (FejdStartup.instance == null) return "Open replays from the main menu";
            ReplayFile replay = ReplayFileIO.Read(path);
            string error = ReplaySession.Begin(replay);
            if (error.Length == 0) _visible = false;
            return error;
        }

        private void Refresh()
        {
            foreach (Entry entry in _entries) if (entry.Preview != null) Destroy(entry.Preview);
            _entries.Clear();
            _selected = null;
            foreach (string path in Directory.GetFiles(ReplayFileIO.GetReplayDirectory(), "*.valreplay").OrderByDescending(File.GetLastWriteTimeUtc))
                _entries.Add(new Entry { Path = path, Name = Path.GetFileNameWithoutExtension(path), Bytes = new FileInfo(path).Length });
            if (_entries.Count > 0) Select(_entries[0]);
        }

        private void Select(Entry entry)
        {
            _selected = entry;
            _detailScroll = Vector2.zero;
            if (entry.Header != null || entry.Error.Length > 0) return;
            try
            {
                entry.Header = ReplayFileIO.ReadInfo(entry.Path);
                if (entry.Header.Thumbnail.Length > 0)
                {
                    entry.Preview = new Texture2D(2, 2);
                    LoadImage(entry.Preview, entry.Header.Thumbnail, false);
                }
                CompareMods(entry.Header.Info, entry.MissingMods, entry.ChangedMods);
            }
            catch (Exception error) { entry.Error = error.Message; }
        }

        /// <summary>Lists the recording's mods that are not loaded, and those loaded in a different version.</summary>
        internal static void CompareMods(ReplayInfo info, List<string> missing, List<string> changed)
        {
            Dictionary<string, BepInEx.PluginInfo> loaded = BepInEx.Bootstrap.Chainloader.PluginInfos;
            foreach (ReplayMod mod in info.Mods)
            {
                if (!loaded.TryGetValue(mod.Guid, out BepInEx.PluginInfo plugin)) missing.Add($"{mod.Name} {mod.Version}");
                else if (plugin.Metadata.Version.ToString() != mod.Version) changed.Add($"{mod.Name} {mod.Version} → {plugin.Metadata.Version}");
            }
        }

        internal static bool Visible => _visible && FejdStartup.instance != null && !ReplaySession.Active;

        /// <summary>True while the Watch button reads a recording before its session starts.</summary>
        internal static bool Loading => _instance != null && _instance._loading != null;

        private void Update()
        {
            if (_loading == null || !_loading.IsCompleted) return;
            Task<ReplayFile> loading = _loading;
            _loading = null;
            if (loading.IsFaulted) { _message = "Could not read the recording: " + loading.Exception?.GetBaseException().Message; return; }
            string error = ReplaySession.Begin(loading.Result);
            if (error.Length > 0) _message = error;
            else _visible = false;
        }

        private void OnGUI()
        {
            if (!_visible || FejdStartup.instance == null || ReplaySession.Active) return;
            GUISkin previous = GUI.skin;
            GUI.skin = CameraPanelStyle.Skin;
            Matrix4x4 matrix = CameraPanelStyle.BeginScaled();
            try
            {
                Vector2 screen = CameraPanelStyle.ReferenceScreen;
                float width = Mathf.Min(1000f, screen.x - 40f), height = Mathf.Min(660f, screen.y - 40f);
                _window = new Rect((screen.x - width) / 2f, (screen.y - height) / 2f, width, height);
                GUILayout.Window(WindowId, _window, Draw, "Replays");
            }
            finally { GUI.matrix = matrix; GUI.skin = previous; }
        }

        private void Draw(int id)
        {
            if (_loading != null)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Loading {_selected?.Name}…");
                GUILayout.FlexibleSpace();
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(_window.width * 0.36f));
            _listScroll = GUILayout.BeginScrollView(_listScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            if (_entries.Count == 0) GUILayout.Label("No recordings yet. In a world, type 'record' in the console to start one and 'record' again to save it.");
            foreach (Entry entry in _entries)
            {
                string label = (entry == _selected ? "▶ " : "") + entry.Name;
                if (GUILayout.Button(label)) Select(entry);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.Space(16);
            GUILayout.BeginVertical();
            _detailScroll = GUILayout.BeginScrollView(_detailScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            if (_selected != null) DrawDetails(_selected);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (_message.Length > 0) GUILayout.Label(_message);
            GUILayout.BeginHorizontal();
            bool canWatch = _selected?.Header != null && _selected.Header.Info.CanLoadAlone;
            GUI.enabled = canWatch;
            if (GUILayout.Button("Watch", GUILayout.Width(130)) && _selected != null)
            {
                string path = _selected.Path;
                _message = "";
                _loading = Task.Run(() => ReplayFileIO.Read(path));
            }
            GUI.enabled = true;
            if (GUILayout.Button("Refresh", GUILayout.Width(110))) Refresh();
            if (GUILayout.Button("Open folder", GUILayout.Width(130))) Application.OpenURL(new Uri(ReplayFileIO.GetReplayDirectory()).AbsoluteUri);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(110))) Close();
            GUILayout.EndHorizontal();
        }

        private static void DrawDetails(Entry entry)
        {
            if (entry.Error.Length > 0) { GUILayout.Label("Could not read this file: " + entry.Error); return; }
            if (entry.Header == null) return;
            ReplayInfo info = entry.Header.Info;
            if (entry.Preview != null) GUILayout.Label(entry.Preview, GUILayout.Width(384), GUILayout.Height(216));
            DateTime created = DateTimeOffset.FromUnixTimeSeconds(info.CreatedUnix).LocalDateTime;
            GUILayout.Label($"{created:yyyy-MM-dd HH:mm}    {(int)(info.Duration / 60)}:{info.Duration % 60:00}    {entry.Bytes / (1024f * 1024f):F1} MB");
            if (!info.CanLoadAlone)
            {
                GUILayout.Label("This recording was made before cameraTools saved surroundings. Load its world and type 'replay " + entry.Name + "' in the console to watch it there.");
                return;
            }
            GUILayout.Label($"World: {info.WorldName} (seed {info.SeedName})");
            GUILayout.Label($"Captured: {info.Zones} zones, {info.SceneObjects} world objects, {info.Actors} characters and objects in motion");
            string version = global::Version.GetVersionString();
            if (info.GameVersion != version) GUILayout.Label($"Recorded in Valheim {info.GameVersion}; this game is {version}. Objects whose data changed between versions can look different.");
            if (entry.MissingMods.Count > 0)
                GUILayout.Label($"{entry.MissingMods.Count} mods used in the recording are not loaded. Their objects, creatures and effects will be left out: {string.Join(", ", entry.MissingMods)}");
            if (entry.ChangedMods.Count > 0)
                GUILayout.Label($"{entry.ChangedMods.Count} mods have a different version: {string.Join(", ", entry.ChangedMods)}");
            if (entry.MissingMods.Count == 0 && entry.ChangedMods.Count == 0) GUILayout.Label("All mods from the recording are loaded.");
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Start))]
        private static class MenuButtonPatch
        {
            private static void Postfix(FejdStartup __instance)
            {
                _visible = false;
                Button[] buttons = __instance.m_menuList.GetComponentsInChildren<Button>(true);
                if (buttons.Length == 0) return;
                Button template = buttons.FirstOrDefault(button => button.name.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0) ?? buttons[buttons.Length - 1];
                GameObject copy = Instantiate(template.gameObject, template.transform.parent);
                copy.name = "cameraTools_Replays";
                copy.transform.SetSiblingIndex(template.transform.GetSiblingIndex());
                foreach (Localize localize in copy.GetComponentsInChildren<Localize>(true)) DestroyImmediate(localize);
                TMP_Text? text = copy.GetComponentInChildren<TMP_Text>(true);
                if (text != null) text.text = "Replays";
                Button button = copy.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(Open);
                __instance.m_menuButtons = __instance.m_menuList.GetComponentsInChildren<Button>();
                cameraToolsPlugin.TemplateLogger.LogInfo($"[ReplayBrowser] Added Replays to the main menu beside {template.name} ({string.Join(", ", buttons.Select(b => b.name))})");
            }
        }
    }
}
