using System;
using System.Numerics;

namespace stewart_platform.Kinematics
{
    public sealed class PlatformPoseTransformer
    {
        public void Transform(PlatformGeometry geometry, Vector3[] result,
                              float tx, float ty, float tz, float rx, float ry, float rz)
        {
            float cx = MathF.Cos(rx), sx = MathF.Sin(rx);
            float cy = MathF.Cos(ry), sy = MathF.Sin(ry);
            float cz = MathF.Cos(rz), sz = MathF.Sin(rz);
            Vector3 translation = new Vector3(tx, ty, tz + geometry.InitialHeight);
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = geometry.BodyPlatformPoints[i];
                float qx = (cz * cy) * p.X + (cz * sy * sx - sz * cx) * p.Y
                           + (cz * sy * cx + sz * sx) * p.Z;
                float qy = (sz * cy) * p.X + (cz * cx + sz * sy * sx) * p.Y
                           + (-cz * sx + sz * sy * cx) * p.Z;
                float qz = (-sy) * p.X + (cy * sx) * p.Y + (cy * cx) * p.Z;
                result[i] = new Vector3(qx, qy, qz) + translation;
            }
        }
    }
}
