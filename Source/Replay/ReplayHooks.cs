using System;
using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// The public contract other mods use to put objects they create only on this client into replays.
    /// Such objects have no ZDO, so the recorder cannot see them on its own: a mod's encounter rooms built
    /// on every client, or boss visuals each client draws from shared timing.
    ///
    /// The contract uses only Unity and system types, so a mod can bind to it by reflection and needs no
    /// reference to cameraTools. Neither side needs the other installed: a recording that names an object no
    /// loaded mod can build skips that object and logs one line.
    /// </summary>
    public static class ReplayHooks
    {
        /// <summary>Raised when the contract changes in a way a caller must handle.</summary>
        public const int Version = 1;

        private static readonly List<(GameObject Instance, string Name)> LiveVisuals = new();
        private static readonly List<(GameObject Instance, string Name)> LiveStructures = new();
        private static readonly List<(string Prefix, Func<string, Vector3, Quaternion, GameObject?> Build)> Builders = new();

        /// <summary>True while a replay plays, over a world or in its own session. Mods skip drawing their
        /// per-client visuals then, because the replay draws the recorded ones.</summary>
        public static bool Replaying => ReplayPlayer.IsPlaying || ReplaySession.Active;

        /// <summary>True while a replay session is open. Nothing in it is a live game: mods run no encounter
        /// logic, build no rooms of their own and apply no effects to the local player.</summary>
        public static bool InSession => ReplaySession.Active;

        /// <summary>
        /// Records a visual this client created for itself, such as a burst, a glow or a shield.
        /// While recording, the visual is sampled every frame like a game effect: its pose, scale, child nodes,
        /// particles, lights and trails (not LineRenderers), from now until it is destroyed or deactivated.
        /// A visual that is activated again starts a new track. Call it once per instance.
        /// </summary>
        /// <param name="name">The prefab name to rebuild it from, or a name a registered builder answers.</param>
        public static void Visual(GameObject instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name)) return;
            LiveVisuals.RemoveAll(entry => entry.Instance == null || entry.Instance == instance);
            LiveVisuals.Add((instance, name));
            ReplayMediaRecorder.Active?.Watch(instance, name);
        }

        /// <summary>
        /// Records a large object this client built for itself that stays still, such as an encounter room.
        /// The recording keeps its name, position and rotation and when it appears and is destroyed or
        /// deactivated; it is not sampled every frame. A replay session asks the builder registered for the
        /// name to build a presentation-only copy with its colliders, so camera checks see its walls.
        /// </summary>
        public static void Structure(GameObject instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name)) return;
            LiveStructures.RemoveAll(entry => entry.Instance == null || entry.Instance == instance);
            LiveStructures.Add((instance, name));
            StructureRecorder.Active?.Watch(instance, name);
        }

        /// <summary>
        /// Registers how to build a presentation copy of recorded visuals or structures whose names start with
        /// <paramref name="prefix"/>; the longest matching prefix wins. The builder receives the full recorded
        /// name and the recorded pose, and returns an active object with no gameplay scripts, or null when it
        /// does not know the name. Visual names without a builder are rebuilt from the prefab of that name.
        /// </summary>
        public static void Builder(string prefix, Func<string, Vector3, Quaternion, GameObject?> build)
        {
            if (string.IsNullOrEmpty(prefix) || build == null) return;
            Builders.RemoveAll(entry => entry.Prefix == prefix);
            Builders.Add((prefix, build));
        }

        /// <summary>Builds a recorded name with its mod's builder. Null when no builder answers or the builder fails.</summary>
        internal static GameObject? Build(string name, Vector3 position, Quaternion rotation)
        {
            Func<string, Vector3, Quaternion, GameObject?>? build = FindBuilder(name);
            if (build == null) return null;
            try { return build(name, position, rotation); }
            catch (Exception error)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning($"[ReplayHooks] Builder for {name} failed: {error.Message}");
                return null;
            }
        }

        private static Func<string, Vector3, Quaternion, GameObject?>? FindBuilder(string name)
        {
            Func<string, Vector3, Quaternion, GameObject?>? best = null;
            int length = -1;
            foreach ((string prefix, Func<string, Vector3, Quaternion, GameObject?> build) in Builders)
                if (prefix.Length > length && name.StartsWith(prefix, StringComparison.Ordinal)) { best = build; length = prefix.Length; }
            return best;
        }

        /// <summary>The reported objects that still exist, for a take that starts after they were created.</summary>
        internal static IEnumerable<(GameObject Instance, string Name)> Visuals()
        {
            LiveVisuals.RemoveAll(entry => entry.Instance == null);
            return LiveVisuals.ToArray();
        }

        internal static IEnumerable<(GameObject Instance, string Name)> Structures()
        {
            LiveStructures.RemoveAll(entry => entry.Instance == null);
            return LiveStructures.ToArray();
        }
    }
}
