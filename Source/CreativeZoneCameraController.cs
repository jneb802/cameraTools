using System;
using System.Globalization;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace cameraTools
{
    public class CreativeZoneCameraController : MonoBehaviour
    {
        private const string DefaultOriginText = "0 0 0";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static bool _commandsRegistered;
        private static CreativeZoneCameraController? _instance;

        private bool _orbiting;
        private Vector3 _orbitOrigin;
        private float _orbitRadius;
        private float _orbitHeight;
        private float _orbitStartAngle;
        private float _orbitDegrees;
        private float _orbitDuration;
        private float _orbitElapsed;

        public static bool IsOrbiting => _instance != null && _instance._orbiting;

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void LateUpdate()
        {
            if (!_orbiting)
            {
                return;
            }

            GameCamera camera = GameCamera.instance;
            if (camera == null)
            {
                _orbiting = false;
                return;
            }

            _orbitElapsed += Time.deltaTime;
            float t = _orbitDuration <= 0f ? 1f : Mathf.Clamp01(_orbitElapsed / _orbitDuration);
            float angle = (_orbitStartAngle + _orbitDegrees * t) * Mathf.Deg2Rad;
            Vector3 position = _orbitOrigin + new Vector3(
                Mathf.Sin(angle) * _orbitRadius,
                _orbitHeight,
                Mathf.Cos(angle) * _orbitRadius);

            Quaternion rotation = Quaternion.LookRotation((_orbitOrigin - position).normalized, Vector3.up);
            ApplyFreeFlyTransform(camera, position, rotation);

            if (t >= 1f)
            {
                _orbiting = false;
            }
        }

        public static void SetFreeFly(bool enabled)
        {
            if (GameCamera.instance == null)
            {
                throw new InvalidOperationException("GameCamera is not ready.");
            }

            if (GameCamera.InFreeFly() != enabled)
            {
                GameCamera.instance.ToggleFreeFly();
            }
        }

        public static void SetHudVisible(bool visible)
        {
            if (Hud.instance == null)
            {
                throw new InvalidOperationException("Hud is not ready.");
            }

            Hud.instance.m_userHidden = !visible;
        }

        public static string BuildStatus(Vector3 origin)
        {
            GameCamera camera = GameCamera.instance;
            if (camera == null)
            {
                return "ERROR: GameCamera is not ready.";
            }

            Vector3 position = camera.transform.position;
            Vector3 toOrigin = origin - position;
            Vector3 horizontal = new(toOrigin.x, 0f, toOrigin.z);
            float distance = toOrigin.magnitude;
            float horizontalDistance = horizontal.magnitude;
            float orbitAngle = Mathf.Atan2(position.x - origin.x, position.z - origin.z) * Mathf.Rad2Deg;
            float targetYaw = Mathf.Atan2(toOrigin.x, toOrigin.z) * Mathf.Rad2Deg;
            float targetPitch = Mathf.Atan2(toOrigin.y, horizontalDistance) * Mathf.Rad2Deg;
            float aimError = toOrigin.sqrMagnitude > 0.001f
                ? Vector3.Angle(camera.transform.forward, toOrigin.normalized)
                : 0f;

            return string.Join("\n", new[]
            {
                $"freefly={GameCamera.InFreeFly()} hudVisible={!(Hud.instance != null && Hud.instance.m_userHidden)} orbiting={IsOrbiting}",
                $"origin={Format(origin)} camera={Format(position)}",
                $"distance={distance:F2} horizontalDistance={horizontalDistance:F2}",
                $"orbitAngle={NormalizeDegrees(orbitAngle):F2} targetYaw={NormalizeDegrees(targetYaw):F2} targetPitch={targetPitch:F2} aimError={aimError:F2}"
            });
        }

        public static void StartOrbit(Vector3 origin, float duration, float degrees)
        {
            GameCamera camera = GameCamera.instance;
            if (camera == null)
            {
                throw new InvalidOperationException("GameCamera is not ready.");
            }

            if (_instance == null)
            {
                throw new InvalidOperationException("CreativeZoneCameraController is not ready.");
            }

            Vector3 offset = camera.transform.position - origin;
            Vector3 horizontal = new(offset.x, 0f, offset.z);
            if (horizontal.magnitude < 0.1f)
            {
                throw new InvalidOperationException("Move the camera away from the origin before starting an orbit.");
            }

            SetFreeFly(true);
            _instance._orbitOrigin = origin;
            _instance._orbitRadius = horizontal.magnitude;
            _instance._orbitHeight = offset.y;
            _instance._orbitStartAngle = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            _instance._orbitDegrees = degrees;
            _instance._orbitDuration = Mathf.Max(0.1f, duration);
            _instance._orbitElapsed = 0f;
            _instance._orbiting = true;
        }

        public static void StopOrbit()
        {
            if (_instance != null)
            {
                _instance._orbiting = false;
            }
        }

        public static void PlaceCamera(Vector3 position, Vector3 lookAt)
        {
            GameCamera camera = GameCamera.instance;
            if (camera == null)
            {
                throw new InvalidOperationException("GameCamera is not ready.");
            }

            Vector3 lookDirection = lookAt - position;
            if (lookDirection.sqrMagnitude < 0.001f)
            {
                throw new InvalidOperationException("Camera position and look target must differ.");
            }

            StopOrbit();
            SetFreeFly(true);
            Quaternion rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            ApplyFreeFlyTransform(camera, position, rotation);
        }

        private static void ApplyFreeFlyTransform(GameCamera camera, Vector3 position, Quaternion rotation)
        {
            camera.m_freeFlyTarget = null;
            camera.m_freeFlyLockon = null;
            camera.m_freeFlyVel = Vector3.zero;
            camera.m_freeFlyAcc = Vector3.zero;
            camera.m_freeFlySavedVel = Vector3.zero;
            camera.m_freeFlyTurnVel = Vector3.zero;
            camera.m_freeFlyYaw = NormalizeSignedAngle(rotation.eulerAngles.y);
            camera.m_freeFlyPitch = NormalizeSignedAngle(rotation.eulerAngles.x);
            camera.SetFreeFlySmoothness(0f);
            camera.transform.position = position;
            camera.transform.rotation = rotation;
        }

        public static string CaptureScreenshot(string? name)
        {
            string directory = Path.Combine(Paths.ConfigPath, "cameraTools", "screenshots");
            Directory.CreateDirectory(directory);

            string fileName = string.IsNullOrWhiteSpace(name)
                ? $"cameraTools_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png"
                : SanitizeFileName(name ?? "");
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".png";
            }

            string path = Path.Combine(directory, fileName);
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }

        internal static Vector3 ParseOrigin(Terminal.ConsoleEventArgs args, int startIndex)
        {
            if (args.Length < startIndex + 3)
            {
                return Vector3.zero;
            }

            return new Vector3(
                ParseFloat(args[startIndex]),
                ParseFloat(args[startIndex + 1]),
                ParseFloat(args[startIndex + 2]));
        }

        internal static float ParseFloat(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, Invariant, out float parsed) &&
                !float.TryParse(value, out parsed))
            {
                throw new ArgumentException($"Invalid number: {value}");
            }

            return parsed;
        }

        private static string Format(Vector3 value)
        {
            return $"{value.x:F2},{value.y:F2},{value.z:F2}";
        }

        private static float NormalizeDegrees(float value)
        {
            value %= 360f;
            return value < 0f ? value + 360f : value;
        }

        private static float NormalizeSignedAngle(float value)
        {
            value = NormalizeDegrees(value);
            return value > 180f ? value - 360f : value;
        }

        private static string SanitizeFileName(string value)
        {
            string fileName = Path.GetFileName(value.Trim());
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(fileName) ? "cameraTools.png" : fileName;
        }

        internal static void RegisterCommands()
        {
            if (_commandsRegistered)
            {
                return;
            }

            _commandsRegistered = true;

            _ = new Terminal.ConsoleCommand("ct_freefly", "Set camera freefly: ct_freefly <on|off|toggle|status>", args =>
            {
                try
                {
                    string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "status";
                    if (mode == "on")
                    {
                        SetFreeFly(true);
                    }
                    else if (mode == "off")
                    {
                        SetFreeFly(false);
                    }
                    else if (mode == "toggle")
                    {
                        if (GameCamera.instance == null)
                        {
                            throw new InvalidOperationException("GameCamera is not ready.");
                        }

                        GameCamera.instance.ToggleFreeFly();
                    }
                    else if (mode != "status")
                    {
                        args.Context.AddString("Usage: ct_freefly <on|off|toggle|status>");
                        return;
                    }

                    args.Context.AddString($"OK: freefly={GameCamera.InFreeFly()}");
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });

            _ = new Terminal.ConsoleCommand("ct_hud", "Set HUD visibility: ct_hud <show|hide|toggle|status>", args =>
            {
                try
                {
                    string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "status";
                    bool visible = !(Hud.instance != null && Hud.instance.m_userHidden);
                    if (mode == "show")
                    {
                        SetHudVisible(true);
                    }
                    else if (mode == "hide")
                    {
                        SetHudVisible(false);
                    }
                    else if (mode == "toggle")
                    {
                        SetHudVisible(!visible);
                    }
                    else if (mode != "status")
                    {
                        args.Context.AddString("Usage: ct_hud <show|hide|toggle|status>");
                        return;
                    }

                    args.Context.AddString($"OK: hudVisible={!(Hud.instance != null && Hud.instance.m_userHidden)}");
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });

            _ = new Terminal.ConsoleCommand("ct_status", $"Report camera position relative to origin. Usage: ct_status [x y z], default {DefaultOriginText}", args =>
            {
                try
                {
                    args.Context.AddString(BuildStatus(ParseOrigin(args, 1)));
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });

            _ = new Terminal.ConsoleCommand("ct_prepare", "Enable freefly and hide HUD. Usage: ct_prepare [x y z]", args =>
            {
                try
                {
                    SetFreeFly(true);
                    SetHudVisible(false);
                    args.Context.AddString("OK: camera prepared");
                    args.Context.AddString(BuildStatus(ParseOrigin(args, 1)));
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });

            _ = new Terminal.ConsoleCommand("ct_place", "Place free camera and look at target. Usage: ct_place <cameraX cameraY cameraZ> <lookX lookY lookZ>", args =>
            {
                try
                {
                    if (args.Length < 7)
                    {
                        args.Context.AddString("Usage: ct_place <cameraX cameraY cameraZ> <lookX lookY lookZ>");
                        return;
                    }

                    Vector3 position = ParseOrigin(args, 1);
                    Vector3 lookAt = ParseOrigin(args, 4);
                    PlaceCamera(position, lookAt);
                    args.Context.AddString($"OK: camera={Format(position)} lookAt={Format(lookAt)}");
                    args.Context.AddString(BuildStatus(lookAt));
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });

            _ = new Terminal.ConsoleCommand("ct_orbit", "Orbit the camera around origin. Usage: ct_orbit <start|stop|status> [duration] [degrees] [x y z]", args =>
            {
                try
                {
                    string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "status";
                    if (mode == "start")
                    {
                        float duration = args.Length >= 3 ? ParseFloat(args[2]) : 20f;
                        float degrees = args.Length >= 4 ? ParseFloat(args[3]) : 360f;
                        Vector3 origin = ParseOrigin(args, 4);
                        StartOrbit(origin, duration, degrees);
                        args.Context.AddString($"OK: orbit started duration={duration:F2} degrees={degrees:F2} origin={Format(origin)}");
                    }
                    else if (mode == "stop")
                    {
                        StopOrbit();
                        args.Context.AddString("OK: orbit stopped");
                    }
                    else if (mode == "status")
                    {
                        args.Context.AddString($"OK: orbiting={IsOrbiting}");
                    }
                    else
                    {
                        args.Context.AddString("Usage: ct_orbit <start|stop|status> [duration] [degrees] [x y z]");
                    }
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });

            _ = new Terminal.ConsoleCommand("ct_screenshot", "Capture a PNG screenshot. Usage: ct_screenshot [filename]", args =>
            {
                try
                {
                    string? fileName = args.Length >= 2 ? args[1] : null;
                    args.Context.AddString($"OK: screenshot={CaptureScreenshot(fileName)}");
                }
                catch (Exception ex)
                {
                    args.Context.AddString($"ERROR: {ex.Message}");
                }
            });
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class TerminalInitPatch
        {
            private static void Postfix() => RegisterCommands();
        }
    }
}
