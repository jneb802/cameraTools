using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>Plays the recorded weather, time of day and wind through EnvMan's debug overrides.</summary>
    internal sealed class EnvironmentPlayer
    {
        private readonly List<EnvironmentSample> _samples;

        internal EnvironmentPlayer(List<EnvironmentSample> samples) { _samples = samples; }

        /// <summary>After a seek, show the weather at the new time at once instead of blending to it.</summary>
        internal void Seek(float time)
        {
            EnvMan.instance?.ForceInstantEnvironmentSwitch();
            Update(time);
        }

        internal void Update(float time)
        {
            EnvMan env = EnvMan.instance;
            if (env == null || _samples.Count == 0) return;
            int index = 0;
            int low = 0, high = _samples.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (_samples[middle].Time <= time) { index = middle; low = middle + 1; }
                else high = middle - 1;
            }
            EnvironmentSample current = _samples[index];
            EnvironmentSample next = index + 1 < _samples.Count ? _samples[index + 1] : current;
            float span = next.Time - current.Time;
            float fraction = span > 0.0001f ? Mathf.Clamp01((time - current.Time) / span) : 0f;
            float nextDay = next.DayFraction;
            if (nextDay < current.DayFraction - 0.5f) nextDay += 1f; // midnight between the samples
            env.m_debugEnv = current.Environment;
            env.m_debugTimeOfDay = true;
            env.m_debugTime = Mathf.Repeat(Mathf.Lerp(current.DayFraction, nextDay, fraction), 1f);
            env.SetDebugWind(Mathf.LerpAngle(current.WindAngle, next.WindAngle, fraction), Mathf.Lerp(current.WindIntensity, next.WindIntensity, fraction));
        }

        internal static void Restore()
        {
            EnvMan env = EnvMan.instance;
            if (env == null) return;
            env.m_debugEnv = "";
            env.m_debugTimeOfDay = false;
            env.ResetDebugWind();
        }
    }
}
