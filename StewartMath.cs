using System;
using System.Numerics;

namespace stewart_platform
{
    /// <summary>
    /// Inverse-kinematics engine for a 6-DOF Stewart Platform.
    /// Uses System.Numerics (float) — fully compatible with Avalonia / Silk.NET.
    /// </summary>
    public class StewartPlatform
    {
        // --- Dimensions ---
        private float BaseRadius;
        private float PlatformRadius;
        private float HornLength;
        private float RodLength;
        private float InitialHeight;

        // --- Geometry tables (radians) ---
        private float[] BaseAngles;
        private float[] PlatformAngles;
        private float[] Beta;

        // --- Home yaw offset applied to platform layout ---
        private float HomeYawOffsetRad;

        // --- Points exposed to the renderer ---
        public Vector3[] BasePoints { get; private set; } = new Vector3[6];
        public Vector3[] PlatformPoints { get; private set; } = new Vector3[6];
        public Vector3[] HornEndPoints { get; private set; } = new Vector3[6];

        // --- Internal kinematics vectors ---
        private Vector3[] b = new Vector3[6];   // base attachment points
        private Vector3[] p = new Vector3[6];   // platform attachment points (body frame)
        private Vector3 h0;                   // home height offset

        // --- Computed servo angles (radians) ---
        public float[] Alpha { get; private set; } = new float[6];

        // --- Current pose (stored for reference) ---
        private Vector3 Translation;
        private Vector3 Rotation;

        // -------------------------------------------------------------------------
        public StewartPlatform(RobotConfig config)
        {
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

        // -------------------------------------------------------------------------
        private void InitializePoints()
        {
            for (int i = 0; i < 6; i++)
            {
                float baseRad = BaseAngles[i] * MathF.PI / 180.0f;
                float platRad = PlatformAngles[i] * MathF.PI / 180.0f;

                b[i] = new Vector3(
                    BaseRadius * MathF.Cos(baseRad),
                    BaseRadius * MathF.Sin(baseRad),
                    0f);
                BasePoints[i] = b[i];

                // The home yaw offset is baked into the body-frame layout of the platform points
                p[i] = new Vector3(
                    PlatformRadius * MathF.Cos(platRad + HomeYawOffsetRad),
                    PlatformRadius * MathF.Sin(platRad + HomeYawOffsetRad),
                    0f);
            }
        }

        // -------------------------------------------------------------------------
        /// <summary>
        /// Compute full inverse kinematics for the given 6-DOF pose.
        /// tx/ty/tz  — translation in mm.
        /// rx/ry/rz  — rotation in RADIANS (roll, pitch, yaw).
        /// </summary>
        public void CalculatePose(float tx, float ty, float tz,
                                  float rx, float ry, float rz)
        {
            Translation = new Vector3(tx, ty, tz);
            Rotation = new Vector3(rx, ry, rz);

            // Pre-compute rotation matrix trig
            float cx = MathF.Cos(rx), sx = MathF.Sin(rx);
            float cy = MathF.Cos(ry), sy = MathF.Sin(ry);
            float cz = MathF.Cos(rz), sz = MathF.Sin(rz);

            for (int i = 0; i < 6; i++)
            {
                // Rotate platform body-frame point by ZYX Euler matrix
                float qx = (cz * cy) * p[i].X
                         + (cz * sy * sx - sz * cx) * p[i].Y
                         + (cz * sy * cx + sz * sx) * p[i].Z;

                float qy = (sz * cy) * p[i].X
                         + (cz * cx + sz * sy * sx) * p[i].Y
                         + (-cz * sx + sz * sy * cx) * p[i].Z;

                float qz = (-sy) * p[i].X
                         + (cy * sx) * p[i].Y
                         + (cy * cx) * p[i].Z;

                // World-space position of platform attach point
                Vector3 q = new Vector3(qx, qy, qz) + Translation + h0;
                PlatformPoints[i] = q;

                // IK: find servo angle alpha[i]
                Vector3 l = q - b[i];

                float L = l.LengthSquared() - (RodLength * RodLength) + (HornLength * HornLength);
                float M = 2.0f * HornLength * (q.Z - b[i].Z);
                float N = 2.0f * HornLength * (MathF.Cos(Beta[i]) * (q.X - b[i].X)
                                             + MathF.Sin(Beta[i]) * (q.Y - b[i].Y));

                float val = L / MathF.Sqrt(M * M + N * N);
                val = Math.Clamp(val, -1.0f, 1.0f);   // guard against unreachable targets

                Alpha[i] = MathF.Asin(val) - MathF.Atan2(N, M);

                // Horn end-point in world space
                HornEndPoints[i] = new Vector3(
                    HornLength * MathF.Cos(Alpha[i]) * MathF.Cos(Beta[i]) + b[i].X,
                    HornLength * MathF.Cos(Alpha[i]) * MathF.Sin(Beta[i]) + b[i].Y,
                    HornLength * MathF.Sin(Alpha[i]) + b[i].Z);
            }
        }

        // -------------------------------------------------------------------------
        /// <summary>Returns servo angle in degrees for servo <paramref name="index"/>.</summary>
        public float GetAlphaDegree(int index)
        {
            if (index < 0 || index >= 6) return 0f;
            return Alpha[index] * 180.0f / MathF.PI;
        }
    }
}