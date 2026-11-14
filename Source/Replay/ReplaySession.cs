using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Opens a recording from the main menu without its world. The game starts a temporary local world
    /// with the recording's seed, but generates nothing: every zone counts as already generated, the
    /// recorded terrain replaces the generated base terrain, and the recorded objects are the only
    /// objects. Nothing is saved, nothing spawns, and the hidden local player follows the camera so the
    /// game loads the zones the camera looks at.
    /// </summary>
    internal static class ReplaySession
    {
        private const string WorldName = "cameraToolsReplay";
        private const string ProfileName = "cameraTools_replay_session";
        private const SaveSystemSessionFlags NoSaves = SaveSystemSessionFlags.DontSaveCharacter | SaveSystemSessionFlags.DontSaveWorld;

        internal static bool Active { get; private set; }
        internal static ReplayFile? Replay { get; private set; }
        internal static bool Spawned { get; private set; }
        private static string? _previousProfile;
        private static FileHelpers.FileSource _previousProfileSource;
        private static SaveSystemSessionFlags _previousFlags;
        private static bool _previousTutorials;
        private static bool _previousAutoPickup;
        private static Dictionary<Vector2s, TerrainPatch> _terrain = new();
        private static World? _world;

        internal static string Begin(ReplayFile replay)
        {
            if (Active) return "A replay session is already open";
            if (FejdStartup.instance == null) return "Open replays from the main menu";
            if (!replay.Info.CanLoadAlone) return "This recording has no saved surroundings. Play it with 'replay <name>' inside its world";
            Replay = replay;
            _terrain = new Dictionary<Vector2s, TerrainPatch>();
            foreach (TerrainPatch patch in replay.Terrain) _terrain[new Vector2s(patch.ZoneX, patch.ZoneY)] = patch;
            World world = _world = new World(WorldName, replay.Info.SeedName)
            {
                m_seed = replay.Info.Seed,
                m_worldGenVersion = replay.Info.WorldGenVersion,
                m_fileSource = FileHelpers.FileSource.Local
            };
            _previousProfile = Game.m_profileFilename;
            _previousProfileSource = Game.m_profileFileSource;
            _previousFlags = SaveSystem.s_sessionFlags;
            _previousTutorials = Raven.m_tutorialsEnabled;
            _previousAutoPickup = Player.m_enableAutoPickup;
            SaveSystem.SetSessionFlags(NoSaves);
            Raven.m_tutorialsEnabled = false;
            // The hidden player follows the camera and must not pick up the replayed items.
            Player.m_enableAutoPickup = false;
            // No file has this name, so the game uses a blank character that is never saved.
            Game.SetProfile(ProfileName, FileHelpers.FileSource.Local);
            Active = true;
            Spawned = false;
            FejdStartup startup = FejdStartup.instance;
            startup.m_startingWorld = true;
            ZNet.SetServer(true, false, false, world.m_name, "", world);
            ZNet.ResetServerHost();
            startup.TransitionToMainScene();
            cameraToolsPlugin.TemplateLogger.LogInfo($"[ReplaySession] Opening {replay.Info.Name}: {replay.Scene.Count} objects, {replay.Terrain.Count} zones, seed {replay.Info.SeedName}");
            return "";
        }

        internal static void Exit()
        {
            if (!Active || Game.instance == null) return;
            ReplayPlayer.Instance.StopPlayback();
            // Saving is blocked by the session flags; Logout with save=false would not log out.
            Game.instance.Logout();
        }

        private static void End()
        {
            if (!Active) return;
            // The game's own Logout, a crash back to the menu or a lost connection also end up here.
            if (ReplayPlayer.IsPlaying) ReplayPlayer.Instance.StopPlayback();
            Active = false;
            Spawned = false;
            Replay = null;
            _terrain = new Dictionary<Vector2s, TerrainPatch>();
            SaveSystem.s_sessionFlags = _previousFlags;
            Raven.m_tutorialsEnabled = _previousTutorials;
            Player.m_enableAutoPickup = _previousAutoPickup;
            Game.SetProfile(_previousProfile, _previousProfileSource);
            RemoveEmptySaveFolder();
            cameraToolsPlugin.TemplateLogger.LogInfo("[ReplaySession] Closed");
        }

        // The minimap creates the world's save folder when the world starts; nothing is saved into it.
        private static void RemoveEmptySaveFolder()
        {
            string? folder = _world?.GetSaveDirectory(FileHelpers.FileSource.Local);
            _world = null;
            try
            {
                if (folder != null && System.IO.Directory.Exists(folder) && System.IO.Directory.GetFileSystemEntries(folder).Length == 0)
                    System.IO.Directory.Delete(folder);
            }
            catch (System.IO.IOException error) { cameraToolsPlugin.TemplateLogger.LogWarning("[ReplaySession] Could not remove " + folder + ": " + error.Message); }
        }

        private static Vector3 StartPosition => Replay!.Info.StartPosition;

        private static void OnPlayerSpawned(Player player)
        {
            player.m_godMode = true;
            player.m_ghostMode = true;
            player.m_debugFly = true;
            foreach (Collider collider in player.GetComponentsInChildren<Collider>()) collider.enabled = false;
            // The session player cannot die (see DamagePatch), but a respawn must still leave the running replay alone.
            if (Spawned) return;
            Spawned = true;
            CreativeZoneCameraController.SetHudVisible(false);
            CreativeZoneCameraController.SetFreeFly(true);
            CreativeZoneCameraController.ApplyExternalTransform(StartPosition, Replay!.Info.StartRotation);
            ReplayPlayer.Instance.StartPlayback(Replay, true);
            ReplayPlayer.Instance.SetPaused(true);
        }

        /// <summary>Keeps the hidden player at the camera, so zones, sounds and weather follow the camera.</summary>
        internal static void FollowCamera()
        {
            if (!Spawned || Player.m_localPlayer == null || GameCamera.m_instance == null) return;
            Player.m_localPlayer.transform.position = GameCamera.m_instance.transform.position;
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Start))]
        private static class MenuPatch
        {
            // Returning to the main menu ends the session, however the player left it.
            private static void Postfix() => End();
        }

        // Leaving any world, by any route, saves a running recording and stops a running replay, so
        // neither carries into the next world: a replay left running there would edit that world's objects.
        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
        private static class WorldShutdownPatch
        {
            private static void Prefix()
            {
                if (ReplayRecorder.IsRecording) ReplayRecorder.Instance.StopRecording();
                if (ReplayPlayer.IsPlaying) ReplayPlayer.Instance.StopPlayback();
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Awake))]
        private static class ProfilePatch
        {
            private static void Postfix(Game __instance)
            {
                if (!Active) return;
                // No intro, Valkyrie, arrival shout or join code for the session's blank character.
                __instance.m_playerProfile.m_firstSpawn = false;
                __instance.m_firstSpawn = false;
            }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.GenerateWorldMap))]
        private static class MinimapPatch
        {
            // The replay has no use for the world map, which takes seconds to draw and is cached to disk.
            private static bool Prefix() => !Active;
        }

        // Recorded objects must keep their absent owner. Without this, the host takes over any object
        // something interacts with or reconnects (portals whose partner was not recorded, for example)
        // and starts running its owner-only simulation.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
        private static class KeepOwnerPatch
        {
            private static bool Prefix(ZDO __instance, long uid) =>
                !Active || uid == SceneTimelinePlayer.ReplayOwner || __instance.GetOwner() != SceneTimelinePlayer.ReplayOwner;
        }

        [HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
        private static class PortalPatch
        {
            // Portal links are part of the recorded data; the host must not rewrite them.
            private static bool Prefix() => !Active;
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GenerateLocationsIfNeeded))]
        private static class LocationPatch
        {
            private static bool Prefix(ZoneSystem __instance)
            {
                if (!Active) return true;
                __instance.LocationsGenerated = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.IsZoneGenerated))]
        private static class GeneratedPatch
        {
            // Generated zones are not filled with vegetation, locations or spawners.
            private static bool Prefix(ref bool __result)
            {
                if (!Active) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.Build))]
        private static class HeightmapPatch
        {
            // Runs on the terrain builder thread; reads only the immutable recorded patches.
            private static void Postfix(HeightmapBuilder.HMBuildData data)
            {
                if (!Active || data.m_distantLod) return;
                Dictionary<Vector2s, TerrainPatch> terrain = _terrain;
                if (!terrain.TryGetValue(ZoneSystem.GetZone(data.m_center), out TerrainPatch patch)) return;
                if (patch.Width != data.m_width || !Mathf.Approximately(patch.Scale, data.m_scale) || patch.Heights.Length != data.m_baseHeights.Count) return;
                data.m_baseHeights = new List<float>(patch.Heights);
                Color[] mask = new Color[patch.Mask.Length];
                for (int index = 0; index < mask.Length; index++) mask[index] = patch.Mask[index];
                data.m_baseMask = mask;
                for (int corner = 0; corner < 4; corner++)
                {
                    Heightmap.Biome biome = (Heightmap.Biome)patch.CornerBiomes[corner];
                    if (data.m_cornerBiomes[corner].Biome != biome) data.m_cornerBiomes[corner] = new BiomeSector(null, biome);
                }
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.FindSpawnPoint))]
        private static class SpawnPointPatch
        {
            private static bool Prefix(Game __instance, out Vector3 point, out bool usedLogoutPoint, ref bool __result)
            {
                point = Vector3.zero;
                usedLogoutPoint = false;
                if (!Active) return true;
                point = StartPosition;
                ZNet.instance.SetReferencePosition(point);
                __result = ZNetScene.instance.IsAreaReady(point);
                return false;
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
        private static class SpawnPatch
        {
            private static void Postfix(Player __result)
            {
                if (Active) OnPlayerSpawned(__result);
            }
        }

        private static bool IsSessionPlayer(Character character) => Active && character == Player.m_localPlayer;

        // The hidden session player stands wherever the camera is: in freezing mountains, lava or deep water.
        // God mode does not protect it, because mods such as Server Devcommands decide the local player's god
        // mode from their own permissions. A death would show the death screen, respawn the player and pause
        // the replay, so the session player takes no damage and cannot die by any route.
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        private static class DamagePatch
        {
            private static bool Prefix(Character __instance) => !IsSessionPlayer(__instance);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.CheckDeath))]
        private static class DeathPatch
        {
            private static bool Prefix(Character __instance) => !IsSessionPlayer(__instance);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetReferencePosition))]
        private static class ReferencePatch
        {
            private static void Prefix(ref Vector3 pos)
            {
                if (Active && Spawned && GameCamera.m_instance != null) pos = GameCamera.m_instance.transform.position;
            }
        }

        // The replayed objects belong to an absent peer. A host normally takes over such objects;
        // the session must not, or the game would start simulating them.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ReleaseNearbyZDOS))]
        private static class OwnershipPatch
        {
            private static bool Prefix() => !Active;
        }

        [HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawning))]
        private static class SpawnSystemPatch
        {
            private static bool Prefix() => !Active;
        }

        [HarmonyPatch(typeof(RandEventSystem), nameof(RandEventSystem.FixedUpdate))]
        private static class EventPatch
        {
            private static bool Prefix() => !Active;
        }
    }
}
