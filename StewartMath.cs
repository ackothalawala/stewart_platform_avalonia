using System;
using System.Numerics;
using stewart_platform.Kinematics;

namespace stewart_platform
{
    /// <summary>
    /// Stable public API for the Avalonia application and its Silk.NET renderer.
    /// Geometry, transformation, and servo IK live in separate OOP classes.
    /// Rotation arguments are in radians, translation in millimeters.
    /// </summary>
    public sealed class StewartPlatform
    {
        private readonly PlatformGeometry geometry;
        private readonly PlatformPoseTransformer transformer = new PlatformPoseTransformer();
        private readonly ServoKinematics inverseKinematics = new ServoKinematics();

        public Vector3[] BasePoints => geometry.BasePoints;
        public Vector3[] PlatformPoints { get; } = new Vector3[6];
        public Vector3[] HornEndPoints { get; } = new Vector3[6];
        public float[] Alpha { get; } = new float[6];

        public StewartPlatform(RobotConfig config)
        {
            geometry = new PlatformGeometry(config);
        }

        public void CalculatePose(float tx, float ty, float tz,
                                  float rx, float ry, float rz)
        {
            transformer.Transform(geometry, PlatformPoints, tx, ty, tz, rx, ry, rz);
            inverseKinematics.Calculate(geometry, PlatformPoints, Alpha, HornEndPoints);
        }

        public float GetAlphaDegree(int index)
        {
            if (index < 0 || index >= 6) return 0f;
            return Alpha[index] * 180f / MathF.PI;
        }
    }
}
