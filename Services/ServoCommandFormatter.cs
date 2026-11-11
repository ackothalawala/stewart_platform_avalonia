using System;
using System.Globalization;

namespace stewart_platform.Services
{
    public static class ServoCommandFormatter
    {
        // Preserves the working Avalonia USB format: six degree*100 integers plus a newline
        // appended by the transport. No header bytes are added.
        public static string Format(StewartPlatform platform)
        {
            var values = new string[6];
            for (int i = 0; i < 6; i++)
            {
                float degrees = platform.GetAlphaDegree(i);
                if (!float.IsFinite(degrees) || MathF.Abs(degrees) > 90f)
                    throw new InvalidOperationException($"Unsafe angle for servo {i}: {degrees}");
                values[i] = ((int)(degrees * 100f)).ToString(CultureInfo.InvariantCulture);
            }
            return string.Join(",", values);
        }
    }
}
