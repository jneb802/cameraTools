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
                    default:
                        return MarkerMode.Placing
                               && !CreativeZoneCameraController.IsMoving
                               && !CreativeZoneCameraController.IsOrbiting
                               && !PathPlayer.IsPlaying;
                }
            }
        }
    }

    public static class MarkerStore
    {
        private static MarkerFile _file = new();
        private static string _loadedWorld = "";
        private static readonly System.Random Random = new();

        public static event Action? Changed;

        public static MarkerFile File => _file;

        /// <summary>All markers in the file, every build.</summary>
        public static List<Marker> AllMarkers => _file.markers;

        /// <summary>Markers of the active build only. This is what the overlay, hover and panel use.</summary>
        public static List<Marker> Markers => _file.markers.Where(m => m.build == _file.activeBuild).ToList();

        public static List<Build> Builds => _file.builds;

        public static Build ActiveBuild
        {
            get
            {
                EnsureBuild();
                return _file.builds.First(b => b.id == _file.activeBuild);
            }
        }

        private static void EnsureBuild()
        {
            _file.builds ??= new List<Build>();
            if (_file.builds.Count == 0)
            {
                _file.builds.Add(new Build { id = NewId(), name = "Build 1", created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
            }

            if (_file.builds.All(b => b.id != _file.activeBuild))
            {
                _file.activeBuild = _file.builds[0].id;
            }

            foreach (Marker marker in _file.markers)
            {
                if (string.IsNullOrEmpty(marker.build) || _file.builds.All(b => b.id != marker.build))
                {
                    marker.build = _file.activeBuild;
                }
            }
        }

        public static Build NewBuild(string name)
        {
            EnsureLoaded();
            Build build = new() { id = NewId(), name = string.IsNullOrWhiteSpace(name) ? $"Build {_file.builds.Count + 1}" : name.Trim(), created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
            _file.builds.Add(build);
            _file.activeBuild = build.id;
            Save();
            return build;
        }

        public static Build? FindBuild(string idOrName)
        {
            EnsureLoaded();
            string needle = idOrName.Trim();
            return _file.builds.FirstOrDefault(b => string.Equals(b.id, needle, StringComparison.OrdinalIgnoreCase))
                   ?? _file.builds.FirstOrDefault(b => string.Equals(b.name, needle, StringComparison.OrdinalIgnoreCase))
                   ?? _file.builds.FirstOrDefault(b => b.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static void UseBuild(Build build)
        {
            _file.activeBuild = build.id;
            Save();
        }

        public static void CycleBuild(int direction)
        {
            EnsureBuild();
            int index = _file.builds.FindIndex(b => b.id == _file.activeBuild);
            index = (index + direction + _file.builds.Count) % _file.builds.Count;
            UseBuild(_file.builds[index]);
        }

        public static void RenameBuild(Build build, string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                build.name = name.Trim();
                Save();
            }
        }

        public static void DeleteBuild(Build build)
        {
            if (_file.builds.Count <= 1)
            {
                return;
            }

            _file.markers.RemoveAll(m => m.build == build.id);
            _file.builds.Remove(build);
            EnsureBuild();
            Save();
        }

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
            EnsureBuild();
            Changed?.Invoke();
        }

        public static string Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            _file.world = WorldName;
            _file.updated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string path = FilePath;
            System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(_file, MarkerJson.Settings));
            Changed?.Invoke();
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
            EnsureBuild();
            marker.build = _file.activeBuild;
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
            string active = _file.activeBuild;
            int removed = _file.markers.RemoveAll(m => m.build == active && (kind == null || m.Kind == kind.Value));

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

            List<Marker> active = Markers;
            return active.FirstOrDefault(m => string.Equals(m.label, needle, StringComparison.OrdinalIgnoreCase))
                   ?? active.FirstOrDefault(m => m.label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string DefaultLabel(MarkerKind kind)
        {
            int count = Markers.Count(m => m.Kind == kind) + 1;
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
            return _file.markers.Any(m => m.id == id) || _file.builds.Any(b => b.id == id) ? NewId() : id;
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
