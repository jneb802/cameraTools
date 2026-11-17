using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using cameraTools.Replay;
using UnityEngine;

namespace cameraTools.Staging
{
    /// <summary>
    /// Runs a scene: clears the area, spawns and places the actors, optionally starts a recording, then starts each
    /// action on time. When the scene records, every action and result is also written to the recording's
    /// highlights, so the replay tells exactly when each staged moment happened.
    /// </summary>
    internal sealed class StageRunner : MonoBehaviour
    {
        public const string PlayerId = "player";
        private const float MaxSceneSeconds = 300f;

        public static StageRunner? Instance { get; private set; }

        private readonly Dictionary<string, StagedActor> _actors = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<StageAction> _pending = new();
        private readonly Dictionary<string, float> _finished = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(float T, string Text)> _log = new();
        private readonly List<Highlight> _highlights = new();
        private StageScene? _scene;
        private float _start;
        private bool _running;
        private string _recording = "";
        private float _recordStart;
        private float _tail = 2f;
        private float? _idleSince;

        private void Awake() => Instance = this;

        public bool Running => _running;
        public IEnumerable<StagedActor> Actors => _actors.Values;
        public float SceneTime => Time.time - _start;

        public static Character? FindActorCharacter(string id)
        {
            if (string.Equals(id, PlayerId, StringComparison.OrdinalIgnoreCase)) return Player.m_localPlayer;
            return Instance != null && Instance._actors.TryGetValue(id, out StagedActor actor) && actor != null ? actor.Character : null;
        }

        public StagedActor Actor(string id)
        {
            if (_actors.TryGetValue(id, out StagedActor actor) && actor != null) return actor;
            if (string.Equals(id, PlayerId, StringComparison.OrdinalIgnoreCase))
            {
                Player player = Player.m_localPlayer ?? throw new InvalidOperationException("No local player");
                StagedActor staged = StagedActor.Attach(player, PlayerId, spawned: false);
                staged.Release(false, null);
                _actors[PlayerId] = staged;
                return staged;
            }
            throw new ArgumentException($"No staged actor '{id}' (ct_stage status lists them)");
        }

        // ---- scenes ----

        public string Run(StageScene scene, string? recordName, int? rate)
        {
            if (Player.m_localPlayer == null || ZNetScene.instance == null) throw new InvalidOperationException("Load a world first");
            if (ReplayPlayer.IsPlaying || ReplaySession.Active) throw new InvalidOperationException("Leave the replay first");
            if (ReplayRecorder.IsRecording) throw new InvalidOperationException("A recording is already running; record stop first");
            Stop("replaced by a new run");
            RemoveAll();
            _log.Clear();
            _highlights.Clear();
            _finished.Clear();
            _scene = scene;
            _start = Time.time;

            int cleared = 0;
            if (scene.Clear)
            {
                Vector3 centre = scene.ClearAt?.Ground() ?? scene.Actors.FirstOrDefault(a => a.At != null)?.At!.Ground() ?? Player.m_localPlayer.transform.position;
                cleared = ClearArea(centre, scene.ClearRadius, scene.ClearDrops);
            }
            foreach (StageActorSpec spec in scene.Actors) Create(spec);

            _pending.AddRange(scene.Actions);
            string name = recordName ?? scene.Record?.Name ?? "";
            float lead = scene.Record?.Lead ?? 1f;
            _tail = scene.Record?.Tail ?? 2f;
            if (name.Length > 0)
            {
                if (!ReplayRecorder.Instance.StartRecording(name, rate ?? scene.Record?.Rate))
                    throw new InvalidOperationException("The recording did not start (see the BepInEx log)");
                _recording = name;
                _recordStart = Time.time;
            }
            else
            {
                _recording = "";
                lead = 0f;
            }
            _start = Time.time + lead;
            _running = true;
            _idleSince = null;
            Log(null, $"scene {scene.Name}: {_actors.Count} actors, {_pending.Count} actions, {cleared} creatures cleared{(name.Length > 0 ? $", recording {name}" : "")}");
            return $"ACCEPTED: scene {scene.Name} running with {_actors.Count} actors and {_pending.Count} actions{(name.Length > 0 ? $", recording {name}" : "")}; poll ct_stage status until state=idle";
        }

        private void Update()
        {
            if (!_running) return;
            if (Player.m_localPlayer == null)
            {
                Stop("the player left the world");
                return;
            }
            float now = SceneTime;
            for (int i = 0; i < _pending.Count; i++)
            {
                StageAction action = _pending[i];
                bool due = action.After.Length > 0
                    ? _finished.TryGetValue(action.After, out float done) && now >= done + action.Delay
                    : now >= (action.T ?? 0f);
                if (!due) continue;
                _pending.RemoveAt(i--);
                Apply(action);
            }

            bool busy = _pending.Count > 0 || _actors.Values.Any(a => a != null && a.Busy);
            if (busy) _idleSince = null;
            else _idleSince ??= now;
            bool ended = _scene?.End is float end ? now >= end : _idleSince is float idle && now >= idle + _tail;
            if (ended) Stop("finished");
            else if (now > MaxSceneSeconds) Stop($"stopped after {MaxSceneSeconds:F0} s");
        }

        /// <summary>Stops the timeline; a recording the scene started is saved. Actors stay where they are, held.</summary>
        public string Stop(string reason)
        {
            if (!_running) return "no scene running";
            _running = false;
            _pending.Clear();
            if (_actors.TryGetValue(PlayerId, out StagedActor player) && player != null && player.Mode != StageMode.Free) player.Release(false, null);
            Log(null, $"scene {reason}");
            if (_recording.Length > 0 && ReplayRecorder.IsRecording)
            {
                SaveHighlights();
                ReplayRecorder.Instance.StopRecording();
            }
            string name = _recording;
            _recording = "";
            return name.Length > 0 ? $"scene {reason}; recording {name} saving (poll record status until state=idle)" : $"scene {reason}";
        }

        public void Apply(StageAction action)
        {
            try
            {
                StagedActor actor = Actor(action.Actor);
                Log(actor, action.Describe().Substring(action.Actor.Length + 1), highlight: true);
                switch (action.Do)
                {
                    case "hold":
                        actor.Hold(action.Toward != null ? action.Toward.Ground() - actor.Position : action.Facing is float f ? StagedActor.Compass(f) : actor.Character.transform.forward);
                        ActionFinished(action);
                        break;
                    case "release":
                        actor.Release(action.Hunt, action.Target?.Character);
                        ActionFinished(action);
                        break;
                    case "wake":
                        actor.Wake();
                        ActionFinished(action);
                        break;
                    case "sleep":
                        actor.Sleep();
                        ActionFinished(action);
                        break;
                    case "place":
                        Vector3 at = action.At!.Ground();
                        actor.Place(at, action.Toward != null ? action.Toward.Ground() - at : action.Facing is float facing ? StagedActor.Compass(facing) : null);
                        ActionFinished(action);
                        break;
                    case "equip":
                        Equip(actor, action.Item);
                        ActionFinished(action);
                        break;
                    default:
                        actor.Begin(action);
                        break;
                }
            }
            catch (Exception error)
            {
                Log(null, $"{action.Name} failed: {error.Message}");
                ActionFinished(action);
            }
        }

        public void ActionFinished(StageAction action)
        {
            if (action.Label.Length > 0) _finished[action.Label] = SceneTime;
        }

        public void ActorGone(StagedActor actor)
        {
            if (!_actors.TryGetValue(actor.Id, out StagedActor current) || current != actor) return;
            _actors.Remove(actor.Id);
            if (actor.Action != null) ActionFinished(actor.Action);
            if (actor.Character != null && actor.Character.IsDead()) Log(actor, "died", highlight: true);
            else Log(actor, "removed");
        }

        // ---- actors ----

        public StagedActor Create(StageActorSpec spec)
        {
            StagedActor actor;
            if (spec.IsPlayer)
            {
                actor = Actor(PlayerId);
                actor.Hold(actor.Character.transform.forward);
            }
            else if (spec.Take.Length > 0)
            {
                Vector3 centre = spec.At?.Ground() ?? Player.m_localPlayer.transform.position;
                Character found = Character.GetAllCharacters()
                    .Where(c => c != null && !c.IsPlayer() && !c.IsDead() && c.GetComponent<StagedActor>() == null && Matches(c, spec.Take) && Vector3.Distance(c.transform.position, centre) <= spec.TakeRadius)
                    .OrderBy(c => Vector3.Distance(c.transform.position, centre))
                    .FirstOrDefault() ?? throw new ArgumentException($"No {spec.Take} within {spec.TakeRadius:F0} m");
                actor = StagedActor.Attach(found, spec.Id, spawned: false);
            }
            else
            {
                GameObject prefab = FindPrefab(spec.Prefab);
                Vector3 at = spec.At!.Ground();
                GameObject spawned = Instantiate(prefab, at, Quaternion.identity);
                Character character = spawned.GetComponent<Character>() ?? throw new ArgumentException($"{spec.Prefab} is not a creature");
                if (spec.Level > 1) character.SetLevel(spec.Level);
                actor = StagedActor.Attach(character, spec.Id, spawned: true);
            }
            if (_actors.TryGetValue(spec.Id, out StagedActor old) && old != null && old != actor) Remove(old);
            _actors[spec.Id] = actor;

            if (spec.At != null)
            {
                Vector3 at = spec.At.Ground();
                actor.Place(at, spec.Toward != null ? spec.Toward.Ground() - at : spec.Facing is float f ? StagedActor.Compass(f) : null);
            }
            else if (spec.Toward != null || spec.Facing != null)
                actor.Place(actor.Position, spec.Toward != null ? spec.Toward.Ground() - actor.Position : StagedActor.Compass(spec.Facing ?? 0f));
            if (spec.Health is float health)
            {
                if (health > actor.Character.GetMaxHealth()) actor.Character.SetMaxHealth(health);
                actor.Character.SetHealth(health);
            }
            if (spec.Item.Length > 0) Equip(actor, spec.Item);
            if (spec.State == "sleep") actor.Sleep();
            else if (spec.State == "free") actor.Release(false, null);
            else actor.Hold(actor.Character.transform.forward);
            return actor;
        }

        public void Remove(StagedActor actor)
        {
            _actors.Remove(actor.Id);
            if (actor.Spawned && actor.Character != null) DestroyCreature(actor.Character.gameObject);
            else
            {
                actor.Release(false, null);
                Destroy(actor);
            }
        }

        public int RemoveAll()
        {
            int count = 0;
            foreach (StagedActor actor in _actors.Values.ToList())
            {
                if (actor == null) continue;
                Remove(actor);
                count++;
            }
            _actors.Clear();
            return count;
        }

        public static int ClearArea(Vector3 centre, float radius, bool drops)
        {
            int count = 0;
            foreach (Character character in Character.GetAllCharacters().ToList())
            {
                if (character == null || character.IsPlayer() || character.GetComponent<StagedActor>() != null) continue;
                if (Vector3.Distance(character.transform.position, centre) > radius) continue;
                DestroyCreature(character.gameObject);
                count++;
            }
            if (drops)
            {
                foreach (ItemDrop drop in ItemDrop.s_instances.ToList())
                    if (drop != null && Vector3.Distance(drop.transform.position, centre) <= radius) DestroyCreature(drop.gameObject);
            }
            return count;
        }

        private static void DestroyCreature(GameObject gameObject)
        {
            ZNetView view = gameObject.GetComponent<ZNetView>();
            if (view != null && view.IsValid())
            {
                if (!view.IsOwner()) view.ClaimOwnership();
                ZNetScene.instance.Destroy(gameObject);
            }
            else Destroy(gameObject);
        }

        public static GameObject FindPrefab(string name)
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(name)
                                 ?? ZNetScene.instance.m_prefabs.FirstOrDefault(p => p != null && string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase));
            if (prefab == null) throw new ArgumentException($"No prefab '{name}'");
            return prefab;
        }

        private static bool Matches(Character character, string name) =>
            string.Equals(Utils.GetPrefabName(character.gameObject), name, StringComparison.OrdinalIgnoreCase)
            || character.m_name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Equip(StagedActor actor, string item)
        {
            if (actor.Character is not Humanoid humanoid) throw new InvalidOperationException($"{actor.Id} cannot hold items");
            ItemDrop.ItemData found = StagedActor.FindWeapon(humanoid, item) ?? throw new ArgumentException($"{actor.Id} has no {item} in its inventory");
            if (!humanoid.IsItemEquiped(found)) humanoid.EquipItem(found);
        }

        // ---- log and highlights ----

        public void Log(StagedActor? actor, string text, bool highlight = false)
        {
            float t = _running || _scene != null ? SceneTime : 0f;
            string line = actor != null ? $"{actor.Id} {text}" : text;
            _log.Add((t, line));
            if (_log.Count > 400) _log.RemoveAt(0);
            if (_recording.Length > 0 && ReplayRecorder.IsRecording && (highlight || actor != null))
            {
                _highlights.Add(new Highlight
                {
                    id = _highlights.Count + 1,
                    t = (float)Math.Round(Time.time - _recordStart, 3),
                    label = "stage: " + line,
                    target = "",
                    createdUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
        }

        private void SaveHighlights()
        {
            if (_highlights.Count == 0) return;
            string path = Path.Combine(ReplayFileIO.GetReplayDirectory(), _recording + ".valreplay");
            try { Highlights.Save(path, _highlights.ToList()); }
            catch (Exception error) { cameraToolsPlugin.TemplateLogger.LogWarning("Stage highlights not saved: " + error.Message); }
        }

        public IEnumerable<string> LogLines(int last) =>
            _log.Skip(Math.Max(0, _log.Count - last)).Select(l => $"t={l.T.ToString("F2", CultureInfo.InvariantCulture)} {l.Text}");

        public string StatusLine()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string state = _running ? "running" : "idle";
            return $"state={state} scene={_scene?.Name ?? "none"} t={(_running ? SceneTime : 0f).ToString("F2", ci)} pending={_pending.Count} actors={_actors.Count} recording={(_recording.Length > 0 ? _recording : "none")}";
        }
    }
}
