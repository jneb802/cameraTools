using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Records the world around the recording: every networked object the client has in its loaded
    /// zones (as ZDO data, which already holds pieces, trees, rocks, location objects and terrain edits),
    /// the base terrain of those zones, and the weather and time of day.
    /// </summary>
    internal sealed class SceneRecorder
    {
        internal static SceneRecorder? Active;
        private const float ScanInterval = 0.2f;
        private const float ZoneSettleSeconds = 3f;
        private const float ZoneQuietSeconds = 2f;
        private const int LargeStateBytes = 16 * 1024;
        private const double CaptureBudgetMilliseconds = 2;

        private sealed class Tracked
        {
            public SceneObject Object = null!;
            public uint Revision;
            public Vector3 Position;
            public Vector3 Rotation;
            public float LastState;
            public bool Piece;
            public float Health;
            public int State;
        }

        private readonly ReplayFile _file;
        private readonly Dictionary<ZDOID, Tracked> _tracked = new();
        private readonly Dictionary<int, bool> _sceneKind = new();
        private readonly Dictionary<Vector2s, float> _zoneSince = new();
        private readonly Dictionary<Vector2s, float> _zoneLastArrival = new();
        private readonly Dictionary<ZDOID, float> _createdHere = new();
        private readonly Dictionary<Vector2s, TerrainPatch> _terrain = new();
        private readonly List<ZDO> _near = new();
        private readonly List<ZDO> _distant = new();
        private readonly ZPackage _package = new();
        private float _nextScan;
        private float _nextEnvironment;
        private float _time;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private int _scans;
        private double _slowestScan;
        private readonly List<KeyValuePair<Vector2s, ZoneSystem.ZoneData>> _pendingTerrain = new();
        private int _terrainCursor;
        private int _nearCursor;
        private int _distantCursor;
        private bool _pendingScan;
        private bool _firstScan = true;
        private float _scanTime;
        private long _deferredBytes;

        private double _slowestWork;
        private string _slowestWorkKind = "none";
        private int _gcSlices;
        private static bool _prepared;

        internal SceneRecorder(ReplayFile file, bool activate = true)
        {
            _file = file;
            if (activate) Active = this;
        }

        internal static void Prepare()
        {
            if (_prepared || Active != null || Player.m_localPlayer == null || ZNetScene.instance == null) return;
            // Exercise serialization and its first-use runtime setup after world loading, before a take.
            // This recorder never subscribes to events or changes the live object.
            SceneRecorder preparation = new(new ReplayFile(), false);
            foreach (KeyValuePair<ZDO, ZNetView> instance in ZNetScene.instance.m_instances)
            {
                ZDO zdo = instance.Key;
                if (!zdo.Persistent || !preparation.IsScene(zdo.GetPrefab())) continue;
                long bytes = 0;
                preparation.Observe(zdo, 0f, true, ref bytes);
                _prepared = true;
                break;
            }
        }

        internal void Update(float time, ref long bytes)
        {
            _time = time;
            if (time >= _nextEnvironment) { _nextEnvironment = time + 1f; SampleEnvironment(time); }
            bytes += _deferredBytes;
            _deferredBytes = 0;
            if ((!_pendingScan && time < _nextScan) || ZDOMan.instance == null || ZoneSystem.instance == null || ZNet.instance == null) return;
            _clock.Restart();
            int collections = System.GC.CollectionCount(0);
            if (!_pendingScan)
            {
                _scanTime = _firstScan ? 0f : time;
                _firstScan = false;
                _pendingTerrain.Clear();
                foreach (KeyValuePair<Vector2s, ZoneSystem.ZoneData> zone in ZoneSystem.instance.m_zones)
                {
                    if (!_zoneSince.ContainsKey(zone.Key)) _zoneSince[zone.Key] = _scanTime;
                    if (!_terrain.ContainsKey(zone.Key)) _pendingTerrain.Add(zone);
                }
                _near.Clear(); _distant.Clear();
                ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()),
                    ZNet.instance.GetSyncedSimulationDistance(), _near, _distant);
                _terrainCursor = _nearCursor = _distantCursor = 0;
                _pendingScan = true;
                TrackWork("enumeration", 0);
            }
            CapturePending(ref bytes, true);
            if (!_pendingScan) _nextScan = time + ScanInterval;
            _clock.Stop();
            if (System.GC.CollectionCount(0) != collections) _gcSlices++;
            _scans++;
            _scanTotal += _clock.Elapsed.TotalMilliseconds;
            _slowestScan = System.Math.Max(_slowestScan, _clock.Elapsed.TotalMilliseconds);
        }

        private double _scanTotal;

        private void TrackWork(string kind, long started)
        {
            double elapsed = (_clock.ElapsedTicks - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (elapsed > _slowestWork) { _slowestWork = elapsed; _slowestWorkKind = kind; }
        }

        private void CapturePending(ref long bytes, bool budgeted)
        {
            while (_pendingScan && (!budgeted || _clock.Elapsed.TotalMilliseconds < CaptureBudgetMilliseconds))
            {
                if (_terrainCursor < _pendingTerrain.Count)
                {
                    long started = _clock.ElapsedTicks;
                    KeyValuePair<Vector2s, ZoneSystem.ZoneData> zone = _pendingTerrain[_terrainCursor++];
                    CaptureTerrain(zone.Key, zone.Value, ref bytes);
                    TrackWork("terrain", started);
                }
                else if (_nearCursor < _near.Count)
                {
                    ZDO zdo = _near[_nearCursor++];
                    long started = _clock.ElapsedTicks;
                    if (zdo != null) Observe(zdo, _scanTime, true, ref bytes);
                    TrackWork("near object", started);
                }
                else if (_distantCursor < _distant.Count)
                {
                    ZDO zdo = _distant[_distantCursor++];
                    long started = _clock.ElapsedTicks;
                    if (zdo != null) Observe(zdo, _scanTime, false, ref bytes);
                    TrackWork("distant object", started);
                }
                else _pendingScan = false;
            }
        }

        private bool IsScene(int prefabHash)
        {
            if (_sceneKind.TryGetValue(prefabHash, out bool scene)) return scene;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            // Projectiles and ragdolls are recorded every frame as copies. Characters and dropped items are
            // recorded both ways: their object data lets the replay session rebuild them as real items, so
            // mods that decorate items (EpicLoot's loot beams) see the item's own data; the per-frame samples
            // move them and keep the overlay replay working.
            scene = prefab != null && prefab.GetComponent<Projectile>() == null &&
                prefab.GetComponent<Ragdoll>() == null;
            _sceneKind[prefabHash] = scene;
            if (prefab != null) _file.Info.Prefabs[prefabHash] = prefab.name;
            return scene;
        }

        private void Observe(ZDO zdo, float time, bool trackChanges, ref long bytes)
        {
            if (zdo == null || !IsScene(zdo.GetPrefab())) return;
            // Players are not persistent world objects, but their replicated equipment data is needed.
            if (!zdo.Persistent && ZNetScene.instance.GetPrefab(zdo.GetPrefab()).GetComponent<Character>() == null) return;
            if (_tracked.TryGetValue(zdo.m_uid, out Tracked tracked))
            {
                if (!float.IsPositiveInfinity(tracked.Object.Destroyed) || !trackChanges || (tracked.Revision == zdo.DataRevision && tracked.Position == zdo.GetPosition())) return;
                SceneState last = tracked.Object.States[tracked.Object.States.Count - 1];
                if (last.Data.Length > LargeStateBytes && time - tracked.LastState < 1f) return;
                AddState(tracked, zdo, time, ref bytes);
                PieceChanged(tracked, zdo, time);
                return;
            }
            bool existed = Existed(zdo, time, out float created);
            tracked = new Tracked { Object = new SceneObject { User = zdo.m_uid.UserID, Id = zdo.m_uid.ID, Prefab = zdo.GetPrefab(), Created = existed ? 0f : created } };
            _tracked[zdo.m_uid] = tracked;
            _file.Scene.Add(tracked.Object);
            AddState(tracked, zdo, existed ? 0f : created, ref bytes);
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            tracked.Piece = prefab.GetComponent<Piece>() != null;
            if (!tracked.Piece) return;
            tracked.Health = HealthFraction(prefab, zdo);
            tracked.State = zdo.GetInt(ZDOVars.s_state, 0);
            if (!existed) _file.WorldEvents.Add(PieceEvent(WorldEventType.Created, tracked, tracked.Position, tracked.Rotation, created));
        }

        // Objects this game creates during the take (pieces it builds, drops it makes) are new.
        // Objects received from the network may be new or may be existing objects arriving late,
        // so they count as new only once their zone has loaded and stopped receiving a burst of objects.
        private bool Existed(ZDO zdo, float time, out float created)
        {
            if (_createdHere.TryGetValue(zdo.m_uid, out created)) return false;
            created = time;
            Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
            if (!_zoneSince.TryGetValue(zone, out float since)) return true;
            bool settled = time - since >= ZoneSettleSeconds &&
                (!_zoneLastArrival.TryGetValue(zone, out float last) || time - last >= ZoneQuietSeconds);
            if (!settled) _zoneLastArrival[zone] = time;
            return !settled;
        }

        private void AddState(Tracked tracked, ZDO zdo, float time, ref long bytes)
        {
            _package.Clear();
            zdo.Serialize(_package);
            SceneState state = new() { Time = time, Position = zdo.GetPosition(), Data = _package.GetArray() };
            tracked.Object.States.Add(state);
            tracked.Revision = zdo.DataRevision;
            tracked.Position = state.Position;
            tracked.Rotation = zdo.GetRotation().eulerAngles;
            tracked.LastState = time;
            bytes += 64 + state.Data.Length;
        }

        private void PieceChanged(Tracked tracked, ZDO zdo, float time)
        {
            if (!tracked.Piece) return;
            float health = HealthFraction(ZNetScene.instance.GetPrefab(zdo.GetPrefab()), zdo);
            int state = zdo.GetInt(ZDOVars.s_state, 0);
            if (Mathf.Approximately(health, tracked.Health) && state == tracked.State) return;
            tracked.Health = health; tracked.State = state;
            _file.WorldEvents.Add(PieceEvent(WorldEventType.StateChanged, tracked, tracked.Position, tracked.Rotation, time));
        }

        private void Destroyed(ZDOID id)
        {
            // Preserve an initial object even if it is destroyed before its capture slice.
            if (_pendingScan && !_tracked.ContainsKey(id))
            {
                ZDO pending = ZDOMan.instance.GetZDO(id);
                if (pending != null && (_near.Contains(pending) || _distant.Contains(pending)))
                    Observe(pending, _scanTime, true, ref _deferredBytes);
            }
            // Destroyed ZDO instances can be recycled before the next slice.
            if (_pendingScan)
            {
                for (int index = _nearCursor; index < _near.Count; index++)
                    if (_near[index] != null && _near[index].m_uid == id) _near[index] = null!;
                for (int index = _distantCursor; index < _distant.Count; index++)
                    if (_distant[index] != null && _distant[index].m_uid == id) _distant[index] = null!;
            }
            if (!_tracked.TryGetValue(id, out Tracked tracked) || !float.IsPositiveInfinity(tracked.Object.Destroyed)) return;
            tracked.Object.Destroyed = Mathf.Max(_time, tracked.Object.Created);
            if (tracked.Piece)
                _file.WorldEvents.Add(PieceEvent(WorldEventType.Destroyed, tracked, tracked.Position, tracked.Rotation, tracked.Object.Destroyed));
        }

        private static WorldEvent PieceEvent(WorldEventType type, Tracked tracked, Vector3 position, Vector3 rotation, float time) => new()
        {
            Time = time, Type = type, ZdoUserID = tracked.Object.User, ZdoID = tracked.Object.Id, PrefabHash = tracked.Object.Prefab,
            Position = position, Rotation = rotation, HealthFraction = tracked.Health, State = tracked.State
        };

        private static float HealthFraction(GameObject prefab, ZDO zdo)
        {
            WearNTear wear = prefab.GetComponent<WearNTear>();
            if (wear == null || wear.m_health <= 0f) return 1f;
            return Mathf.Clamp01(zdo.GetFloat(ZDOVars.s_health, wear.m_health) / wear.m_health);
        }

        private void CaptureTerrain(Vector2s zone, ZoneSystem.ZoneData data, ref long bytes)
        {
            Heightmap? heightmap = data.m_root != null ? data.m_root.GetComponentInChildren<Heightmap>() : null;
            HeightmapBuilder.HMBuildData? build = heightmap?.m_buildData;
            if (heightmap == null || build == null || build.m_baseHeights == null || build.m_baseMask == null || build.m_cornerBiomes == null) return;
            TerrainPatch patch = new()
            {
                ZoneX = zone.x, ZoneY = zone.y, Width = build.m_width, Scale = build.m_scale,
                Heights = build.m_baseHeights.ToArray(), Mask = new Color32[build.m_baseMask.Length]
            };
            for (int index = 0; index < patch.Mask.Length; index++) patch.Mask[index] = build.m_baseMask[index];
            for (int corner = 0; corner < 4; corner++) patch.CornerBiomes[corner] = (int)build.m_cornerBiomes[corner].Biome;
            _terrain[zone] = patch;
            _file.Terrain.Add(patch);
            bytes += patch.Heights.Length * 8L;
        }

        private void SampleEnvironment(float time)
        {
            EnvMan env = EnvMan.instance;
            if (env == null) return;
            Vector3 wind = env.GetWindDir();
            _file.Environment.Add(new EnvironmentSample
            {
                Time = time, Environment = env.GetCurrentEnvironment()?.m_name ?? "", DayFraction = env.GetDayFraction(),
                WindAngle = Mathf.Atan2(wind.x, wind.z) * Mathf.Rad2Deg, WindIntensity = env.GetWindIntensity()
            });
        }

        internal void Finish()
        {
            // A short take must still contain the complete initial scene.
            CapturePending(ref _deferredBytes, false);
            Active = null;
            _file.WorldEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
            _file.Info.Zones = _terrain.Count;
            _file.Info.SceneObjects = _file.Scene.Count;
            cameraToolsPlugin.TemplateLogger.LogInfo($"[SceneRecorder] {_file.Scene.Count} objects, {_terrain.Count} zones, {_file.Environment.Count} weather samples, {_file.WorldEvents.Count} piece events; {_scans} scans, average {(_scans > 0 ? _scanTotal / _scans : 0):F2} ms, slowest {_slowestScan:F2} ms");
            cameraToolsPlugin.TemplateLogger.LogInfo($"[SceneRecorder] longest work: {_slowestWorkKind} {_slowestWork:F2} ms; {_gcSlices} capture slices included garbage collection");
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), typeof(Vector3), typeof(int))]
        private static class CreatePatch
        {
            // The overload the game uses when this client creates a new networked object.
            private static void Postfix(ZDO __result)
            {
                if (Active != null && __result != null && !_generatingZone) Active._createdHere[__result.m_uid] = Active._time;
            }
        }

        private static bool _generatingZone;

        // A host generating a zone the player walks into creates its trees and rocks; they were not built.
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
        private static class ZoneGenerationPatch
        {
            private static void Prefix() => _generatingZone = true;
            private static void Finalizer() => _generatingZone = false;
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        private static class DestroyPatch
        {
            private static void Prefix(ZDOID uid) => Active?.Destroyed(uid);
        }
    }
}
