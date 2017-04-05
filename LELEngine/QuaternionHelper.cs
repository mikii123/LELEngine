using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenTK;

namespace LELEngine
{
    public class QuaternionHelper
    {
        public const float DegToRad = 0.00872665f;
        public const float DegToRad2 = DegToRad * 2;
        public const float Rad2Deg = (float)(360 / (Math.PI * 2));

        /// <summary>
        /// Creates rotation from y, z and x (in that order)
        /// </summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            // Heading = Yaw
            // Attitude = Pitch
            // Bank = Roll

            x *= DegToRad;
            y *= DegToRad;
            z *= DegToRad;

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

            x *= DegToRad;
            y *= DegToRad;
            z *= DegToRad;

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
