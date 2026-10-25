using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace cameraTools.Markers
{
    public enum MarkerVisibilityMode
    {
        Auto,
        Show,
        Hide
    }

    public static class MarkerVisibility
    {
        public static MarkerVisibilityMode Mode = MarkerVisibilityMode.Auto;

        public static bool Visible
        {
            get
            {
                switch (Mode)
                {
                    case MarkerVisibilityMode.Show: return true;
                    case MarkerVisibilityMode.Hide: return false;
                    default: return MarkerMode.Active;
                }
            }
        }

        public static void HideForCapture()
        {
            Mode = MarkerVisibilityMode.Hide;
        }
    }

    public static class MarkerStore
    {
        private static MarkerFile _file = new();
        private static string _loadedWorld = "";
        private static readonly System.Random Random = new();

        public static MarkerFile File => _file;
        public static List<Marker> Markers => _file.markers;

        public static string Directory => Path.Combine(Paths.ConfigPath, "cameraTools", "markers");

        public static string WorldName
        {
            get
            {
                string? name = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                return string.IsNullOrEmpty(name) ? "no-world" : name!;
            }
        }

        public static string FilePath => Path.Combine(Directory, SanitizeFileName(WorldName) + ".json");

        public static void EnsureLoaded()
        {
            string world = WorldName;
            if (world == _loadedWorld)
            {
                return;
            }

            Load();
        }

        public static void Load()
        {
            string world = WorldName;
            _loadedWorld = world;
            string path = FilePath;
            if (System.IO.File.Exists(path))
            {
                try
                {
                    MarkerFile? parsed = JsonConvert.DeserializeObject<MarkerFile>(System.IO.File.ReadAllText(path), MarkerJson.Settings);
                    _file = parsed ?? new MarkerFile();
                    _file.markers ??= new List<Marker>();
                    foreach (Marker marker in _file.markers)
                    {
                        marker.waypoints ??= new List<Waypoint>();
                        marker.InvalidateCache();
                    }

                    cameraToolsPlugin.TemplateLogger.LogInfo($"Loaded {_file.markers.Count} markers for world '{world}' from {path}");
                }
                catch (Exception ex)
                {
                    cameraToolsPlugin.TemplateLogger.LogError($"Failed to read markers file {path}: {ex.Message}");
                    _file = new MarkerFile();
                }
            }
            else
            {
                _file = new MarkerFile();
            }

            _file.world = world;
        }

        public static string Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            _file.world = WorldName;
            _file.updated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string path = FilePath;
            System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(_file, MarkerJson.Settings));
            return path;
        }

        public static Marker Add(Marker marker)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(marker.id))
            {
                marker.id = NewId();
            }

            if (string.IsNullOrEmpty(marker.label))
            {
                marker.label = DefaultLabel(marker.Kind);
            }

            marker.created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            _file.markers.Add(marker);
            Save();
            return marker;
        }

        public static bool Remove(Marker marker)
        {
            bool removed = _file.markers.Remove(marker);
            if (removed)
            {
                Save();
            }

            return removed;
        }

        public static int Clear(MarkerKind? kind)
        {
            int removed = kind == null
                ? _file.markers.Count
                : _file.markers.Count(m => m.Kind == kind.Value);
            if (kind == null)
            {
                _file.markers.Clear();
            }
            else
            {
                _file.markers.RemoveAll(m => m.Kind == kind.Value);
            }

            Save();
            return removed;
        }

        public static Marker? Find(string idOrLabel)
        {
            EnsureLoaded();
            string needle = idOrLabel.Trim();
            Marker? byId = _file.markers.FirstOrDefault(m => string.Equals(m.id, needle, StringComparison.OrdinalIgnoreCase));
            if (byId != null)
            {
                return byId;
            }

            return _file.markers.FirstOrDefault(m => string.Equals(m.label, needle, StringComparison.OrdinalIgnoreCase))
                   ?? _file.markers.FirstOrDefault(m => m.label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string DefaultLabel(MarkerKind kind)
        {
            int count = _file.markers.Count(m => m.Kind == kind) + 1;
            return $"{Marker.KindName(kind)}-{count}";
        }

        private static string NewId()
        {
            const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
            char[] chars = new char[6];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[Random.Next(alphabet.Length)];
            }

            string id = new(chars);
            return _file.markers.Any(m => m.id == id) ? NewId() : id;
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }
    }
}
