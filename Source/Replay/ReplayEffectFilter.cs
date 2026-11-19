using System;
using System.Collections.Generic;
using System.Linq;

namespace cameraTools.Replay
{
    /// <summary>
    /// Effects and sounds that replays leave out: recorded effect prefabs (for example the burning flames a fire
    /// weapon puts on a creature) and recorded audio clips, by name. The list is the Replay.HiddenEffects setting, so
    /// it applies to every replay, replay session, shot preview and render. Recordings keep everything; showing a name
    /// again brings it back.
    /// </summary>
    internal static class ReplayEffectFilter
    {
        private static string _parsedFrom = "\0";
        private static HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> Hidden
        {
            get
            {
                string setting = cameraToolsPlugin.ReplayHiddenEffects?.Value ?? "";
                if (setting == _parsedFrom) return _hidden;
                _parsedFrom = setting;
                _hidden = new HashSet<string>(Split(setting), StringComparer.OrdinalIgnoreCase);
                return _hidden;
            }
        }

        public static bool IsHidden(string name) => name.Length > 0 && Hidden.Contains(name);

        public static IEnumerable<string> Split(string names) =>
            names.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).Where(n => n.Length > 0);

        public static void Hide(IEnumerable<string> names) => Save(Hidden.Union(names, StringComparer.OrdinalIgnoreCase));

        public static void Show(IEnumerable<string> names)
        {
            HashSet<string> remove = new(names, StringComparer.OrdinalIgnoreCase);
            Save(remove.Contains("all") ? Enumerable.Empty<string>() : Hidden.Where(n => !remove.Contains(n)));
        }

        private static void Save(IEnumerable<string> names)
        {
            if (cameraToolsPlugin.ReplayHiddenEffects == null) throw new InvalidOperationException("Settings are not loaded");
            cameraToolsPlugin.ReplayHiddenEffects.Value = string.Join(",", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }

        public static string Describe() => Hidden.Count == 0 ? "nothing hidden" : "hidden: " + string.Join(", ", Hidden.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }
}
