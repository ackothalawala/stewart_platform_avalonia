using System;
using System.Numerics;

namespace stewart_platform.Kinematics
{
    public sealed class PlatformGeometry
    {
        public Vector3[] BasePoints { get; } = new Vector3[6];
        public Vector3[] BodyPlatformPoints { get; } = new Vector3[6];
        public float[] Beta { get; } = new float[6];
        public float InitialHeight { get; }
        public float HornLength { get; }
        public float RodLength { get; }

        public PlatformGeometry(RobotConfig config)
        {
            InitialHeight = (float)config.InitialHeight;
            HornLength = (float)config.HornLength;
            RodLength = (float)config.RodLength;
            float yawOffset = (float)(config.HomeYawOffsetDeg * Math.PI / 180.0);
            for (int i = 0; i < 6; i++)
            {
                float br = (float)config.BaseAngles[i] * MathF.PI / 180f;
                float pr = (float)config.PlatformAngles[i] * MathF.PI / 180f + yawOffset;
                BasePoints[i] = new Vector3((float)config.BaseRadius * MathF.Cos(br),
                    (float)config.BaseRadius * MathF.Sin(br), 0);
                BodyPlatformPoints[i] = new Vector3((float)config.PlatformRadius * MathF.Cos(pr),
                    (float)config.PlatformRadius * MathF.Sin(pr), 0);
                Beta[i] = (float)config.BetaAngles[i];
            }
        }
    }
}
