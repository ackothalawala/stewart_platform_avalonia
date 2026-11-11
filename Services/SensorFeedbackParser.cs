using System;
using System.Globalization;

namespace stewart_platform.Services
{
    public readonly struct SensorFeedback
    {
        public float Roll { get; }
        public float Pitch { get; }
        public float Yaw { get; }
        public SensorFeedback(float roll, float pitch, float yaw)
        { Roll = roll; Pitch = pitch; Yaw = yaw; }
    }

    public static class SensorFeedbackParser
    {
        public static bool TryParse(string? line, out SensorFeedback feedback)
        {
            feedback = default;
            if (string.IsNullOrWhiteSpace(line) || !line.Trim().StartsWith("FB:", StringComparison.Ordinal))
                return false;
            var parts = line.Trim().Substring(3).Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var roll) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pitch) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw) ||
                !float.IsFinite(roll) || !float.IsFinite(pitch) || !float.IsFinite(yaw)) return false;
            feedback = new SensorFeedback(roll, pitch, yaw);
            return true;
        }
    }
}
