using System;
using System.Numerics;

namespace stewart_platform.Kinematics
{
    public sealed class ServoKinematics
    {
        public void Calculate(PlatformGeometry geometry, Vector3[] topPoints,
                              float[] alpha, Vector3[] hornPoints)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 b = geometry.BasePoints[i];
                Vector3 q = topPoints[i];
                float beta = geometry.Beta[i];
                float horn = geometry.HornLength;
                Vector3 l = q - b;
                float L = l.LengthSquared() - geometry.RodLength * geometry.RodLength + horn * horn;
                float M = 2f * horn * (q.Z - b.Z);
                float N = 2f * horn * (MathF.Cos(beta) * (q.X - b.X)
                                     + MathF.Sin(beta) * (q.Y - b.Y));
                float val = L / MathF.Sqrt(M * M + N * N);
                val = Math.Clamp(val, -1f, 1f);
                alpha[i] = MathF.Asin(val) - MathF.Atan2(N, M);
                hornPoints[i] = new Vector3(
                    horn * MathF.Cos(alpha[i]) * MathF.Cos(beta) + b.X,
                    horn * MathF.Cos(alpha[i]) * MathF.Sin(beta) + b.Y,
                    horn * MathF.Sin(alpha[i]) + b.Z);
            }
        }
    }
}
