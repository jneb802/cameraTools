using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace cameraTools.Markers
{
    public enum MarkerKind
    {
        Area,
        Point,
        Shot,
        Path
    }

    public enum AreaShape
    {
        Circle,
        Rectangle
    }

    [Serializable]
    [JsonObject(MemberSerialization.Fields)]
    public class Waypoint
    {
        public Vector3 position;
        public Vector3 forward;
        public float hold;

        public Quaternion Rotation => forward.sqrMagnitude < 0.0001f
            ? Quaternion.identity
            : Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    [Serializable]
    [JsonObject(MemberSerialization.Fields)]
    public class Marker
    {
        public string id = "";
        public string kind = "point";
        public string label = "";
        public string note = "";
        public string tag = "";
        public string created = "";

        // Area and Point
        public Vector3 position;

        // Area
        public string shape = "circle";
        public float radius = 6f;
        public float width = 10f;
        public float depth = 10f;
        public float rotation;

        // Shot
        public Vector3 cameraPosition;
        public Vector3 lookDirection;

        // Path
        public List<Waypoint> waypoints = new();

        [NonSerialized] [JsonIgnore] public List<Vector3>? CachedOutline;
        [NonSerialized] [JsonIgnore] public List<Vector3>? CachedSpline;
        [NonSerialized] [JsonIgnore] public float CacheTime;

        public MarkerKind Kind
        {
            get => ParseKind(kind);
            set => kind = KindName(value);
        }

        public AreaShape Shape
        {
            get => string.Equals(shape, "rectangle", StringComparison.OrdinalIgnoreCase) ? AreaShape.Rectangle : AreaShape.Circle;
            set => shape = value == AreaShape.Rectangle ? "rectangle" : "circle";
        }

        public Vector3 Anchor
        {
            get
            {
                switch (Kind)
                {
                    case MarkerKind.Shot:
                        return cameraPosition;
                    case MarkerKind.Path:
                        return waypoints.Count > 0 ? waypoints[0].position : position;
                    case MarkerKind.Area:
                        return position + Vector3.up * 1.5f;
                    default:
                        return position + Vector3.up * 1.5f;
                }
            }
        }

        public void InvalidateCache()
        {
            CachedOutline = null;
            CachedSpline = null;
            CacheTime = 0f;
        }

        public static MarkerKind ParseKind(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "area": return MarkerKind.Area;
                case "shot": return MarkerKind.Shot;
                case "path": return MarkerKind.Path;
                default: return MarkerKind.Point;
            }
        }

        public static string KindName(MarkerKind value)
        {
            switch (value)
            {
                case MarkerKind.Area: return "area";
                case MarkerKind.Shot: return "shot";
                case MarkerKind.Path: return "path";
                default: return "point";
            }
        }

        public string Describe()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string head = $"{id} {kind} \"{label}\"";
            switch (Kind)
            {
                case MarkerKind.Area:
                    string size = Shape == AreaShape.Circle
                        ? $"r={radius.ToString("F1", ci)}"
                        : $"w={width.ToString("F1", ci)} d={depth.ToString("F1", ci)} rot={rotation.ToString("F0", ci)}";
                    return $"{head} {shape} pos={Fmt(position)} {size} tag={tag}";
                case MarkerKind.Shot:
                    return $"{head} cam={Fmt(cameraPosition)} look={Fmt(cameraPosition + lookDirection.normalized * 10f)}";
                case MarkerKind.Path:
                    float holds = 0f;
                    foreach (Waypoint wp in waypoints) holds += wp.hold;
                    return $"{head} waypoints={waypoints.Count} length={PathLength().ToString("F1", ci)}m holds={holds.ToString("F1", ci)}s start={Fmt(waypoints.Count > 0 ? waypoints[0].position : Vector3.zero)}";
                default:
                    return $"{head} pos={Fmt(position)} note=\"{note}\"";
            }
        }

        public float PathLength()
        {
            float total = 0f;
            for (int i = 1; i < waypoints.Count; i++)
            {
                total += Vector3.Distance(waypoints[i - 1].position, waypoints[i].position);
            }

            return total;
        }

        public static string Fmt(Vector3 v)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            return $"{v.x.ToString("F1", ci)},{v.y.ToString("F1", ci)},{v.z.ToString("F1", ci)}";
        }
    }

    public class Vector3JsonConverter : JsonConverter<Vector3>
    {
        public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x"); writer.WriteValue(value.x);
            writer.WritePropertyName("y"); writer.WriteValue(value.y);
            writer.WritePropertyName("z"); writer.WriteValue(value.z);
            writer.WriteEndObject();
        }

        public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            Vector3 result = Vector3.zero;
            if (reader.TokenType != JsonToken.StartObject)
            {
                return result;
            }

            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                string name = (string)reader.Value!;
                reader.Read();
                float value = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
                if (name == "x") result.x = value;
                else if (name == "y") result.y = value;
                else if (name == "z") result.z = value;
            }

            return result;
        }
    }

    public static class MarkerJson
    {
        public static readonly JsonSerializerSettings Settings = new()
        {
            Formatting = Formatting.Indented,
            Converters = { new Vector3JsonConverter() }
        };
    }

    [Serializable]
    [JsonObject(MemberSerialization.Fields)]
    public class MarkerFile
    {
        public int version = 1;
        public string world = "";
        public string updated = "";
        public List<Marker> markers = new();
    }
}
