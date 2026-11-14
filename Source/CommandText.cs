using System;
using System.Globalization;

namespace cameraTools
{
    /// <summary>
    /// Shared parsing and replies for the console commands, so an agent can tell results apart:
    /// "OK:" done, "ACCEPTED:" started and still running, "PENDING:" still running,
    /// "ERROR: invalid-argument:" bad input, "ERROR: unavailable:" not possible in the current state.
    /// </summary>
    internal static class CommandText
    {
        internal static string Error(Exception error) => error switch
        {
            ArgumentException or FormatException or OverflowException => "ERROR: invalid-argument: " + error.Message,
            InvalidOperationException => "ERROR: unavailable: " + error.Message,
            _ => "ERROR: " + error.Message
        };

        internal static string Arg(Terminal.ConsoleEventArgs args, int index, string usage)
        {
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index])) throw new ArgumentException("Usage: " + usage);
            return args[index];
        }

        internal static float Number(string value, float min, float max, string name)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || float.IsInfinity(number))
                throw new ArgumentException($"{name} must be a number, not '{value}'");
            if (number < min || number > max) throw new ArgumentException($"{name} must be from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");
            return number;
        }

        /// <summary>Reads on/off style values; toggle inverts the current value.</summary>
        internal static bool Switch(string value, bool current)
        {
            switch (value.ToLowerInvariant())
            {
                case "on": case "show": case "true": case "1": return true;
                case "off": case "hide": case "false": case "0": return false;
                case "toggle": return !current;
                default: throw new ArgumentException($"Use on, off or toggle, not '{value}'");
            }
        }

        internal static (long User, uint Id) Actor(string text)
        {
            string[] parts = text.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out long user) || !uint.TryParse(parts[1], out uint id))
                throw new ArgumentException($"An actor is user:id as ct_replay targets prints it, not '{text}'");
            return (user, id);
        }
    }
}
