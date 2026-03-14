using System;
using System.Numerics;

namespace stewart_platform
{
    public class StewartPlatform
    {
        // --- Config Values ---
        private float BaseRadius;
        private float PlatformRadius;
        private float HornLength;
        private float RodLength;
        private float InitialHeight;

        private float[] BaseAngles;
        private float[] PlatformAngles;
        private float[] Beta;

        // --- Home yaw offset (radians) ---
        private float HomeYawOffsetRad;

        // --- Public Drawing Points ---
        public Vector3[] BasePoints { get; private set; } = new Vector3[6];
        public Vector3[] PlatformPoints { get; private set; } = new Vector3[6];
        public Vector3[] HornEndPoints { get; private set; } = new Vector3[6];

        // --- Internal Math ---
        private Vector3[] b = new Vector3[6];
        private Vector3[] p = new Vector3[6];
        public float[] Alpha { get; private set; } = new float[6];

        // --- Current Pose ---
        private Vector3 Translation;
        private Vector3 Rotation;
        private Vector3 h0;

        // --- Constructor ---
        public StewartPlatform(RobotConfig config)
        {
            // Load dimensions and cast to float
            BaseRadius = (float)config.BaseRadius;
            PlatformRadius = (float)config.PlatformRadius;
            HornLength = (float)config.HornLength;
            RodLength = (float)config.RodLength;
            InitialHeight = (float)config.InitialHeight;

            HomeYawOffsetRad = (float)(config.HomeYawOffsetDeg * Math.PI / 180.0);

            BaseAngles = new float[6];
            PlatformAngles = new float[6];
            Beta = new float[6];

            for (int i = 0; i < 6; i++)
            {
                BaseAngles[i] = (float)config.BaseAngles[i];
                PlatformAngles[i] = (float)config.PlatformAngles[i];
                Beta[i] = (float)config.BetaAngles[i];
            }

            h0 = new Vector3(0, 0, InitialHeight);
            InitializePoints();
        }

        private void InitializePoints()
        {
            for (int i = 0; i < 6; i++)
            {
                // Convert degrees to radians for base and platform layout
                float baseRad = BaseAngles[i] * MathF.PI / 180.0f;
                float platRad = PlatformAngles[i] * MathF.PI / 180.0f;

                // Set Base points (b)
                b[i] = new Vector3(
                    BaseRadius * MathF.Cos(baseRad),
                    BaseRadius * MathF.Sin(baseRad),
                    0f
                );
                BasePoints[i] = b[i];

                // Set Platform attachment points (p)
                p[i] = new Vector3(
                    PlatformRadius * MathF.Cos(platRad + HomeYawOffsetRad),
                    PlatformRadius * MathF.Sin(platRad + HomeYawOffsetRad),
                    0f
                );
            }
        }

        public void CalculatePose(float tx, float ty, float tz, float rx, float ry, float rz)
        {
            Translation = new Vector3(tx, ty, tz);
            Rotation = new Vector3(rx, ry, rz);

            // Pre-calculate trig functions for rotation matrix using MathF
            float cx = MathF.Cos(rx);
            float sx = MathF.Sin(rx);
            float cy = MathF.Cos(ry);
            float sy = MathF.Sin(ry);
            float cz = MathF.Cos(rz);
            float sz = MathF.Sin(rz);

            for (int i = 0; i < 6; i++)
            {
                // Apply rotation matrix to platform points
                float qx = (cz * cy) * p[i].X
                         + (cz * sy * sx - sz * cx) * p[i].Y
                         + (cz * sy * cx + sz * sx) * p[i].Z;

                float qy = (sz * cy) * p[i].X
                         + (cz * cx + sz * sy * sx) * p[i].Y
                         + (-cz * sx + sz * sy * cx) * p[i].Z;

                float qz = (-sy) * p[i].X
                         + (cy * sx) * p[i].Y
                         + (cy * cx) * p[i].Z;

                // Translated point q
                Vector3 q = new Vector3(qx, qy, qz) + Translation + h0;
                PlatformPoints[i] = q;

                // Vector l from base point to translated platform point
                Vector3 lVector = q - b[i];

                // Inverse kinematics to find servo angle (Alpha)
                float L = lVector.LengthSquared() - (RodLength * RodLength) + (HornLength * HornLength);
                float M = 2.0f * HornLength * (q.Z - b[i].Z);
                float N = 2.0f * HornLength * (MathF.Cos(Beta[i]) * (q.X - b[i].X) + MathF.Sin(Beta[i]) * (q.Y - b[i].Y));

                float val = L / MathF.Sqrt(M * M + N * N);

                // Clamp to avoid NaN errors if target is unreachable
                val = Math.Clamp(val, -1.0f, 1.0f);

                Alpha[i] = MathF.Asin(val) - MathF.Atan2(N, M);

                // Calculate where the horn ends in 3D space
                float ax = HornLength * MathF.Cos(Alpha[i]) * MathF.Cos(Beta[i]) + b[i].X;
                float ay = HornLength * MathF.Cos(Alpha[i]) * MathF.Sin(Beta[i]) + b[i].Y;
                float az = HornLength * MathF.Sin(Alpha[i]) + b[i].Z;

                HornEndPoints[i] = new Vector3(ax, ay, az);
            }
        }

        public float GetAlphaDegree(int index)
        {
            if (index < 0 || index >= 6) return 0f;
            return Alpha[index] * 180.0f / MathF.PI;
        }
    }
}