using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace cameraTools.Staging
{
    internal enum StageMode { Free, Hold, Sleep, Walk, Face, Attack, Block }

    /// <summary>
    /// Drives one staged creature or the player. A creature's own AI is replaced while it is staged
    /// (StageAIPatch calls <see cref="StepCreature"/> in its place); it moves with the game's own path finding and
    /// controls, so walk, run and attack animations play as in normal play. The player is driven through
    /// Player.SetControls with its PlayerController turned off, the same input path a person uses.
    /// </summary>
    internal sealed class StagedActor : MonoBehaviour
    {
        private const float WalkArrive = 0.6f;
        private const float RunArrive = 1.1f;
        private const float ToArrive = 0.35f;
        private const float AttackStartTimeout = 3f;
        private const float FaceTimeout = 2f;

        private static readonly Dictionary<BaseAI, StagedActor> ByAI = new();

        public string Id = "";
        public Character Character = null!;
        public BaseAI? AI;
        public bool IsPlayer;
        public StageMode Mode = StageMode.Hold;
        public StageAction? Action;
        public bool Spawned;
        /// <summary>
        /// ZDO strings kept at these values while staged. Other mods can tag a creature a moment after it spawns (an
        /// Endgame room tags every creature that starts inside it with its run), so the values are checked every step.
        /// </summary>
        public readonly Dictionary<string, string> ZdoStrings = new(StringComparer.Ordinal);

        private Vector3? _look;
        private int _index;
        private float _deadline;
        private float _pauseUntil;
        private bool _waitingForEnd;
        private float _nextStart;
        private float _pressStart;
        private Attack? _attackBefore;
        private bool _attackStarted;
        private float _actionStart;
        private PlayerController? _controller;

        private void KeepZdoStrings()
        {
            if (ZdoStrings.Count == 0) return;
            ZNetView view = Character != null ? Character.GetComponent<ZNetView>() : null!;
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            ZDO zdo = view.GetZDO();
            foreach (KeyValuePair<string, string> pair in ZdoStrings)
                if (zdo.GetString(pair.Key) != pair.Value) zdo.Set(pair.Key, pair.Value);
        }

        public static StagedActor? Of(BaseAI ai) => ByAI.TryGetValue(ai, out StagedActor actor) ? actor : null;

        public static StagedActor Attach(Character character, string id, bool spawned)
        {
            StagedActor actor = character.gameObject.GetComponent<StagedActor>() ?? character.gameObject.AddComponent<StagedActor>();
            actor.Id = id;
            actor.Character = character;
            actor.AI = character.GetComponent<BaseAI>();
            actor.IsPlayer = character is Player;
            actor.Spawned = spawned;
            if (actor.AI != null) ByAI[actor.AI] = actor;
            actor.Hold(character.transform.forward);
            return actor;
        }

        public Vector3 Position => Character.transform.position;

        private void OnDestroy()
        {
            if (AI != null) ByAI.Remove(AI);
            if (IsPlayer) GiveBackControls();
            StageRunner.Instance?.ActorGone(this);
        }

        // ---- state changes ----

        public void Hold(Vector3? look)
        {
            Mode = StageMode.Hold;
            Action = null;
            _look = Flat(look);
            TakeControls();
        }

        public void Sleep()
        {
            Mode = StageMode.Sleep;
            Action = null;
            if (AI is MonsterAI monster && !monster.IsSleeping()) monster.Sleep();
        }

        public void Wake()
        {
            if (AI is MonsterAI monster && monster.IsSleeping()) monster.Wakeup();
            if (Mode == StageMode.Sleep) Hold(Character.transform.forward);
        }

        /// <summary>Gives the actor back to its own AI (or the player back to the person playing).</summary>
        public void Release(bool hunt, Character? target)
        {
            Mode = StageMode.Free;
            Action = null;
            if (IsPlayer)
            {
                GiveBackControls();
                return;
            }
            if (AI is MonsterAI monster)
            {
                if (monster.IsSleeping()) monster.Wakeup();
                if (hunt) monster.SetHuntPlayer(true);
                if (target != null) monster.m_targetCreature = target;
                if (hunt || target != null) monster.SetAlerted(true);
            }
            else if (AI != null && hunt) AI.SetAlerted(true);
        }

        public void Place(Vector3 position, Vector3? facing)
        {
            Quaternion rotation = facing is Vector3 f && Flat(f) is Vector3 flat ? Quaternion.LookRotation(flat) : Character.transform.rotation;
            if (IsPlayer && Vector3.Distance(Position, position) > 100f)
            {
                Character.TeleportTo(position, rotation, distantTeleport: true);
            }
            else
            {
                Character.transform.SetPositionAndRotation(position, rotation);
                if (Character.m_body != null)
                {
                    Character.m_body.position = position;
                    Character.m_body.rotation = rotation;
                    Character.m_body.linearVelocity = Vector3.zero;
                }
            }
            Character.SetLookDir(rotation * Vector3.forward);
            if (Character is Player player) player.FaceLookDirection();
            if (Mode == StageMode.Hold) _look = rotation * Vector3.forward;
        }

        public void Begin(StageAction action)
        {
            Action = action;
            _actionStart = Time.time;
            _index = 0;
            _pauseUntil = 0f;
            _waitingForEnd = false;
            _nextStart = 0f;
            switch (action.Do)
            {
                case "walk":
                    action.ResolvePath();
                    Mode = StageMode.Walk;
                    StartLeg();
                    TakeControls();
                    break;
                case "face":
                    Mode = StageMode.Face;
                    _look = Flat(action.Toward != null ? action.Toward.Ground() - Position : Compass(action.Facing ?? 0f));
                    _deadline = Time.time + FaceTimeout;
                    TakeControls();
                    break;
                case "attack":
                    if (!IsPlayer && Character is not Humanoid) throw new InvalidOperationException($"{Id} is not a humanoid creature and has no weapons to attack with");
                    Mode = StageMode.Attack;
                    _deadline = Time.time + AttackStartTimeout;
                    TakeControls();
                    break;
                case "block":
                    if (!IsPlayer) throw new InvalidOperationException("Only the player can block on cue");
                    Mode = StageMode.Block;
                    _deadline = Time.time + action.Hold;
                    TakeControls();
                    break;
                default:
                    throw new InvalidOperationException($"{action.Do} is not a running action");
            }
        }

        public bool Busy => Mode == StageMode.Walk || Mode == StageMode.Face || Mode == StageMode.Attack || Mode == StageMode.Block;

        // ---- creature steps (called from the AI update in place of the creature's own AI) ----

        public void StepCreature(float dt)
        {
            if (AI == null || Character.IsDead()) return;
            switch (Mode)
            {
                case StageMode.Sleep:
                    AI.StopMoving();
                    break;
                case StageMode.Hold:
                    AI.StopMoving();
                    if (_look is Vector3 look && !Character.InAttack()) AI.LookTowards(look);
                    break;
                case StageMode.Face:
                    AI.StopMoving();
                    if (_look is Vector3 face)
                    {
                        AI.LookTowards(face);
                        if (AI.IsLookingTowards(face, 5f) || Time.time > _deadline) Finish("facing", face);
                    }
                    else Finish("facing", null);
                    break;
                case StageMode.Walk:
                    StepCreatureWalk(dt);
                    break;
                case StageMode.Attack:
                    StepCreatureAttack();
                    break;
            }
        }

        private void StepCreatureWalk(float dt)
        {
            StageAction action = Action!;
            if (Time.time < _pauseUntil)
            {
                AI!.StopMoving();
                return;
            }
            Vector3 point = PointAt(_index);
            bool run = action.Pace != "walk";
            float distance = Utils.DistanceXZ(point, Position);
            if (distance < (_index == action.ToIndex ? ToArrive : run ? RunArrive : WalkArrive))
            {
                NextLeg();
                return;
            }
            if (Time.time > _deadline)
            {
                StageRunner.Instance?.Log(this, $"walk stuck before point {_index + 1}/{action.Points.Count}, {distance:F1} m short");
                Finish("stuck", null);
                return;
            }
            // The game's MoveTo counts itself arrived within 1 m running (0.5 m walking), so the last metre to a 'to'
            // point is a straight step at walking speed, which also keeps a runner from overshooting a 0.35 m target.
            if (_index == action.ToIndex && distance < 1.5f) AI!.MoveTowards((point - Position).normalized, false);
            // The game's path finding when it finds a path; straight at the point when it does not (off the nav mesh).
            else if (AI!.FindPath(point) && AI.m_path.Count > 0) AI.MoveTo(dt, point, 0f, run);
            else AI.MoveTowards((point - Position).normalized, run);
        }

        private void StepCreatureAttack()
        {
            StageAction action = Action!;
            Humanoid humanoid = (Humanoid)Character;
            Vector3? aim = action.Toward?.AimPoint();
            if (aim is Vector3 aimPoint) AI!.LookAt(aimPoint);
            AI!.StopMoving();
            if (_waitingForEnd)
            {
                if (Character.InAttack()) return;
                _waitingForEnd = false;
                _nextStart = Time.time + action.Gap;
                _deadline = _nextStart + AttackStartTimeout;
            }
            if (Time.time < _nextStart) return;
            int total = action.Attacks.Count > 0 ? action.Attacks.Count : action.Count;
            if (_index >= total)
            {
                Finish("attacks done", aim is Vector3 a ? a - Position : null);
                return;
            }
            if (action.Attacks.Count > 0)
            {
                string name = action.Attacks[_index];
                ItemDrop.ItemData? item = FindWeapon(humanoid, name);
                if (item == null)
                {
                    StageRunner.Instance?.Log(this, $"has no attack '{name}' (ct_stage attacks {Id} lists them)");
                    _index++;
                    return;
                }
                if (humanoid.GetCurrentWeapon() != item) humanoid.EquipItem(item, triggerEquipEffects: false);
            }
            if (aim is Vector3 target && !AI.IsLookingAt(target, 20f) && Time.time < _deadline - AttackStartTimeout * 0.5f) return;
            Character? victim = action.Toward?.Character;
            if (humanoid.StartAttack(victim, false))
            {
                StageRunner.Instance?.Log(this, $"attack {AttackName(humanoid)} started");
                _index++;
                _waitingForEnd = true;
                return;
            }
            if (Time.time > _deadline)
            {
                StageRunner.Instance?.Log(this, $"attack {AttackName(humanoid)} could not start in {AttackStartTimeout:F0} s");
                _index++;
                _nextStart = Time.time;
                _deadline = Time.time + AttackStartTimeout;
            }
        }

        // ---- player steps ----

        private void FixedUpdate()
        {
            KeepZdoStrings();
            if (!IsPlayer || Mode == StageMode.Free || Character is not Player player || player != Player.m_localPlayer) return;
            Vector3 move = Vector3.zero;
            bool attack = false, attackHold = false, secondary = false, secondaryHold = false, block = false, run = false;
            switch (Mode)
            {
                case StageMode.Hold:
                    if (_look is Vector3 look) player.SetLookDir(look);
                    break;
                case StageMode.Face:
                    if (_look is Vector3 face)
                    {
                        player.SetLookDir(face);
                        player.FaceLookDirection();
                    }
                    Finish("facing", _look);
                    break;
                case StageMode.Walk:
                {
                    StageAction action = Action!;
                    if (Time.time < _pauseUntil) break;
                    Vector3 point = PointAt(_index);
                    Vector3 to = point - Position;
                    to.y = 0f;
                    if (to.magnitude < (_index == action.ToIndex ? ToArrive : WalkArrive))
                    {
                        NextLeg();
                        break;
                    }
                    if (Time.time > _deadline)
                    {
                        StageRunner.Instance?.Log(this, $"walk stuck before point {_index + 1}/{action.Points.Count}, {to.magnitude:F1} m short");
                        Finish("stuck", null);
                        break;
                    }
                    player.SetLookDir(to.normalized);
                    player.SetWalk(action.Pace == "walk");
                    move = Vector3.forward;
                    run = action.Pace == "sprint";
                    break;
                }
                case StageMode.Attack:
                {
                    StageAction action = Action!;
                    if (action.Toward != null) player.SetLookDir((action.Toward.AimPoint() - player.GetEyePoint()).normalized);
                    if (Time.time < _nextStart) break;
                    if (_index >= action.Count)
                    {
                        if (!player.InAttack()) Finish("attacks done", null);
                        break;
                    }
                    string kind = action.Secondary ? "secondary" : "primary";
                    if (!_waitingForEnd)
                    {
                        // A new press: hold the button until the game starts a new swing. A press made during a swing
                        // stays queued while the button is held, so a combo chains when the swing's chain window opens.
                        _waitingForEnd = true;
                        _attackStarted = false;
                        _pressStart = Time.time;
                        _attackBefore = player.m_currentAttack;
                        if (action.Secondary) secondary = true; else attack = true;
                        StageRunner.Instance?.Log(this, $"{kind} attack {_index + 1}/{action.Count} pressed");
                    }
                    if (!_attackStarted && player.m_currentAttack != null && player.m_currentAttack != _attackBefore)
                    {
                        _attackStarted = true;
                        StageRunner.Instance?.Log(this, $"{kind} attack {_index + 1}/{action.Count} started after {Time.time - _pressStart:F2} s");
                    }
                    bool timedOut = !_attackStarted && Time.time - _pressStart > AttackStartTimeout;
                    if (timedOut) StageRunner.Instance?.Log(this, $"{kind} attack {_index + 1}/{action.Count} did not start in {AttackStartTimeout:F0} s");
                    if (timedOut || (_attackStarted && Time.time - _pressStart >= action.Hold))
                    {
                        _waitingForEnd = false;
                        _index++;
                        _nextStart = Time.time + Mathf.Max(action.Gap, Time.fixedDeltaTime);
                        break;
                    }
                    if (action.Secondary) secondaryHold = true; else attackHold = true;
                    break;
                }
                case StageMode.Block:
                    if (Action!.Toward != null) player.SetLookDir((Action.Toward.AimPoint() - player.GetEyePoint()).normalized);
                    block = true;
                    if (Time.time > _deadline) Finish("block done", null);
                    break;
            }
            player.SetControls(move, attack, attackHold, secondary, secondaryHold, block, block, false, false, run, false);
        }

        private void TakeControls()
        {
            if (!IsPlayer || _controller != null) return;
            _controller = Character.GetComponent<PlayerController>();
            if (_controller != null) _controller.enabled = false;
        }

        private void GiveBackControls()
        {
            if (Character is Player player)
            {
                player.SetWalk(false);
                player.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false);
            }
            if (_controller != null) _controller.enabled = true;
            _controller = null;
        }

        // ---- shared ----

        /// <summary>A walk point; the point that follows 'to' stops 'stop' metres short of it, on the side the actor comes from.</summary>
        private Vector3 PointAt(int index)
        {
            StageAction action = Action!;
            if (index != action.ToIndex || action.To == null) return action.Points[index];
            Vector3 goal = action.To.Ground();
            float stop = action.Stop ?? (action.To.Point == null && action.To.Character != null ? 1.5f : 0f);
            Vector3 from = goal - Position;
            from.y = 0f;
            return from.magnitude <= stop ? Position : goal - from.normalized * stop;
        }

        private void StartLeg()
        {
            StageAction action = Action!;
            float distance = Utils.DistanceXZ(PointAt(_index), Position);
            float speed = action.Pace == "walk" ? 1f : 2.5f;
            _deadline = Time.time + Mathf.Max(6f, distance / speed * 2f + 4f);
        }

        private void NextLeg()
        {
            StageAction action = Action!;
            float hold = action.PointHolds.Count > _index ? action.PointHolds[_index] : 0f;
            _index++;
            if (_index >= action.Points.Count)
            {
                Vector3 last = PointAt(action.Points.Count - 1);
                Vector3 previous = action.Points.Count > 1 ? PointAt(action.Points.Count - 2) : Position;
                Vector3 endLook = action.Toward != null ? action.Toward.Ground() - Position
                    : action.To != null ? action.To.Ground() - Position
                    : action.Facing is float facing ? Compass(facing)
                    : last - previous;
                Finish("arrived", Flat(endLook));
                return;
            }
            _pauseUntil = Time.time + hold;
            StartLeg();
            _deadline += hold;
        }

        private void Finish(string result, Vector3? look)
        {
            StageAction? action = Action;
            if (action == null) return;
            StageRunner.Instance?.Log(this, $"{action.Do} {result} after {Time.time - _actionStart:F2} s");
            if (action.Do == "walk" && action.Then == "release") Release(action.Hunt, action.Target?.Character);
            else Hold(look ?? Character.transform.forward);
            StageRunner.Instance?.ActionFinished(action);
        }

        private static Vector3? Flat(Vector3? direction)
        {
            if (direction is not Vector3 d) return null;
            d.y = 0f;
            return d.sqrMagnitude < 0.0001f ? null : d.normalized;
        }

        public static Vector3 Compass(float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        }

        public static ItemDrop.ItemData? FindWeapon(Humanoid humanoid, string name) =>
            humanoid.GetInventory().GetAllItems().FirstOrDefault(i => i.m_dropPrefab != null && string.Equals(i.m_dropPrefab.name, name, StringComparison.OrdinalIgnoreCase))
            ?? humanoid.GetInventory().GetAllItems().FirstOrDefault(i => string.Equals(i.m_shared.m_name, name, StringComparison.OrdinalIgnoreCase));

        public static string AttackName(Humanoid humanoid)
        {
            ItemDrop.ItemData? weapon = humanoid.GetCurrentWeapon();
            return weapon == null ? "none" : weapon.m_dropPrefab != null ? weapon.m_dropPrefab.name : weapon.m_shared.m_name;
        }

        public string StatusLine()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            Vector3 p = Position;
            Vector3 f = Character.transform.forward;
            float heading = (Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + 360f) % 360f;
            string detail = Mode switch
            {
                StageMode.Walk when Action != null => $" point={_index + 1}/{Action.Points.Count}",
                StageMode.Attack when Action != null => $" attack={_index}/{(Action.Attacks.Count > 0 ? Action.Attacks.Count : Action.Count)}",
                _ => ""
            };
            string sleeping = AI is MonsterAI monster && monster.IsSleeping() ? " sleeping" : "";
            return $"actor {Id} {(IsPlayer ? "player" : Utils.GetPrefabName(Character.gameObject))} mode={Mode.ToString().ToLowerInvariant()}{detail}{sleeping} " +
                   $"pos={p.x.ToString("F1", ci)},{p.y.ToString("F1", ci)},{p.z.ToString("F1", ci)} facing={heading.ToString("F0", ci)} " +
                   $"health={Character.GetHealth().ToString("F0", ci)}/{Character.GetMaxHealth().ToString("F0", ci)}";
        }
    }
}
