using System;
using OpenTK;

namespace LELEngine
{
    public class QuaternionHelper
    {
        public const float Deg2Rad = 0.00872665f;
        public const float Deg2Rad2 = Deg2Rad * 2;
        public const float Rad2Deg = (float)(360 / (Math.PI * 2));

        /// <summary>
        /// Creates rotation from y, z and x (in that order)
        /// </summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            // Heading = Yaw
            // Attitude = Pitch
            // Bank = Roll

            x *= Deg2Rad;
            y *= Deg2Rad;
            z *= Deg2Rad;

            // Assuming the angles are in radians.
            float c1 = (float)Math.Cos(y);
            float s1 = (float)Math.Sin(y);
            float c2 = (float)Math.Cos(z);
            float s2 = (float)Math.Sin(z);
            float c3 = (float)Math.Cos(x);
            float s3 = (float)Math.Sin(x);
            float c1c2 = c1 * c2;
            float s1s2 = s1 * s2;

            Quaternion q = new Quaternion();
            q.W = c1c2 * c3 - s1s2 * s3;
            q.X = c1c2 * s3 + s1s2 * c3;
            q.Y = s1 * c2 * c3 + c1 * s2 * s3;
            q.Z = c1 * s2 * c3 - s1 * c2 * s3;

            return q;
        }

        public static Quaternion Euler(Vector3 vector3)
        {
            // Heading = Yaw
            // Attitude = Pitch
            // Bank = Roll

            float x = vector3.X;
            float y = vector3.Y;
            float z = vector3.Z;

            x *= Deg2Rad;
            y *= Deg2Rad;
            z *= Deg2Rad;

            // Assuming the angles are in radians.
            float c1 = (float)Math.Cos(y);
            float s1 = (float)Math.Sin(y);
            float c2 = (float)Math.Cos(z);
            float s2 = (float)Math.Sin(z);
            float c3 = (float)Math.Cos(x);
            float s3 = (float)Math.Sin(x);
            float c1c2 = c1 * c2;
            float s1s2 = s1 * s2;

            Quaternion q = new Quaternion();
            q.W = c1c2 * c3 - s1s2 * s3;
            q.X = c1c2 * s3 + s1s2 * c3;
            q.Y = s1 * c2 * c3 + c1 * s2 * s3;
            q.Z = c1 * s2 * c3 - s1 * c2 * s3;

            return q;
        }

        public static Quaternion QuaternionFromMatrix(Matrix4 m)
        {
            Quaternion q = new Quaternion();
            q.W = (float)Math.Sqrt(Math.Max(0, 1 + m.M11 + m.M22 + m.M33)) / 2;
            q.X = (float)Math.Sqrt(Math.Max(0, 1 + m.M11 - m.M22 - m.M33)) / 2;
            q.Y = (float)Math.Sqrt(Math.Max(0, 1 - m.M11 + m.M22 - m.M33)) / 2;
            q.Z = (float)Math.Sqrt(Math.Max(0, 1 - m.M11 - m.M22 + m.M33)) / 2;
            q.X *= Math.Sign(q.X * (m.M32 - m.M23));
            q.Y *= Math.Sign(q.Y * (m.M13 - m.M31));
            q.Z *= Math.Sign(q.Z * (m.M21 - m.M12));
            return q.Normalized();
        }
        /*
        public static Quaternion LookRotation(Vector3 direction, Vector3 up)
        {
            if (direction == Vector3.Zero)
            {
                return Quaternion.Identity;
            }

            if (up != direction)
            {
                up.Normalize();
                var v = direction + up * -Vector3.Dot(up, direction);
                var q = FromTwoVectors(Vector3.UnitZ, v);
                return FromTwoVectors(v, direction) * q;
            }
            else
            {
                return FromTwoVectors(Vector3.UnitZ, direction);
            }
        }

        public static Quaternion LookRotation(Vector3 direction)
        {
            Vector3 up = Vector3.UnitY;
            if (direction == Vector3.Zero)
            {
                return Quaternion.Identity;
            }

            if (up != direction)
            {
                up.Normalize();
                var v = direction + up * -Vector3.Dot(up, direction);
                var q = FromTwoVectors(Vector3.UnitZ, v);
                return FromTwoVectors(v, direction) * q;
            }
            else
            {
                return FromTwoVectors(Vector3.UnitZ, direction);
            }
        }
        */
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            forward.Normalize();

            Vector3 vector = Vector3.Normalize(forward);
            Vector3 vector2 = Vector3.Normalize(Vector3.Cross(up, vector));
            Vector3 vector3 = Vector3.Cross(vector, vector2);
            var m00 = vector2.X;
            var m01 = vector2.Y;
            var m02 = vector2.Z;
            var m10 = vector3.X;
            var m11 = vector3.Y;
            var m12 = vector3.Z;
            var m20 = vector.X;
            var m21 = vector.Y;
            var m22 = vector.Z;


            float num8 = (m00 + m11) + m22;
            var quaternion = new Quaternion();
            if (num8 > 0f)
            {
                var num = (float)Math.Sqrt(num8 + 1f);
                quaternion.W = num * 0.5f;
                num = 0.5f / num;
                quaternion.X = (m12 - m21) * num;
                quaternion.Y = (m20 - m02) * num;
                quaternion.Z = (m01 - m10) * num;
                return quaternion;
            }
            if ((m00 >= m11) && (m00 >= m22))
            {
                var num7 = (float)Math.Sqrt(((1f + m00) - m11) - m22);
                var num4 = 0.5f / num7;
                quaternion.X = 0.5f * num7;
                quaternion.Y = (m01 + m10) * num4;
                quaternion.Z = (m02 + m20) * num4;
                quaternion.W = (m12 - m21) * num4;
                return quaternion;
            }
            if (m11 > m22)
            {
                var num6 = (float)Math.Sqrt(((1f + m11) - m00) - m22);
                var num3 = 0.5f / num6;
                quaternion.X = (m10 + m01) * num3;
                quaternion.Y = 0.5f * num6;
                quaternion.Z = (m21 + m12) * num3;
                quaternion.W = (m20 - m02) * num3;
                return quaternion;
            }
            var num5 = (float)Math.Sqrt(((1f + m22) - m00) - m11);
            var num2 = 0.5f / num5;
            quaternion.X = (m20 + m02) * num2;
            quaternion.Y = (m21 + m12) * num2;
            quaternion.Z = 0.5f * num5;
            quaternion.W = (m01 - m10) * num2;
            return quaternion;
        }

        public static Quaternion FromTwoVectors(Vector3 u, Vector3 v)
        {
            u.Normalize();
            v.Normalize();

            float norm_u_norm_v = (float)Math.Sqrt(Vector3.Dot(u, u) * Vector3.Dot(v, v));
            float real_part = norm_u_norm_v + Vector3.Dot(u, v);
            Vector3 w;

            if (real_part < 0.000001f * norm_u_norm_v)
            {
                real_part = 0.0f;
                w = (Math.Abs(u.X) > Math.Abs(u.Z)) ? 
                    new Vector3(-u.Y, u.X, 0f) :
                    new Vector3(0f, -u.Z, u.Y);
            }
            else
            {
                w = Vector3.Cross(u, v);
            }

            Quaternion q = new Quaternion(w.X, w.Y, w.Z, real_part);

            return q.Normalized();
        }

        public static Vector3 ToEulerAngles(Quaternion q)
        {
            // Store the Euler angles in radians
            Vector3 pitchYawRoll = new Vector3();
            q.Normalize();

            double sqw = q.W * q.W;
            double sqx = q.X * q.X;
            double sqy = q.Y * q.Y;
            double sqz = q.Z * q.Z;

            // If quaternion is normalised the unit is one, otherwise it is the correction factor
            double unit = sqx + sqy + sqz + sqw;
            double test = q.X * q.Y + q.Z * q.W;

            pitchYawRoll.Y = (float)Math.Atan2(2 * q.Y * q.W - 2 * q.X * q.Z, sqx - sqy - sqz + sqw);
            pitchYawRoll.X = (float)Math.Asin(2f * (q.X * q.Z - q.W * q.Y));
            pitchYawRoll.Z = (float)Math.Atan2(2 * q.X * q.W - 2 * q.Y * q.Z, -sqx + sqy - sqz + sqw);

            if (test > 0.499f * unit)
            {
                // Singularity at north pole
                pitchYawRoll.Y = 2f * (float)Math.Atan2(q.X, q.W);
                pitchYawRoll.X = (float)Math.PI * 0.5f;
                pitchYawRoll.Z = 0f;
            }
            else if (test < -0.499f * unit)
            {
                // Singularity at south pole
                pitchYawRoll.Y = -2f * (float)Math.Atan2(q.X, q.W);
                pitchYawRoll.X = (float)-Math.PI * 0.5f;
                pitchYawRoll.Z = 0f;
            }

            return NormalizeAngles(pitchYawRoll * Rad2Deg);
        }

        static Vector3 NormalizeAngles(Vector3 angles)
        {
            angles.X = NormalizeAngle(angles.Z);
            angles.Y = NormalizeAngle(angles.Y);
            angles.Z = NormalizeAngle(angles.X);
            return angles;
        }

        static float NormalizeAngle(float angle)
        {
            while (angle > 360)
                angle -= 360;
            while (angle < 0)
                angle += 360;
            return angle;
        }
    }
}
