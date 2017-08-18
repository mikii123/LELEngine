using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace LELEngine
{
    static class Extensions
    {
        public static Vector3 ToEulerAngles(this Quaternion q)
        {
            // Store the Euler angles in radians
            Vector3 pitchYawRoll = new Vector3();

            double sqw = q.W * q.W;
            double sqx = q.X * q.X;
            double sqy = q.Y * q.Y;
            double sqz = q.Z * q.Z;

            // If quaternion is normalised the unit is one, otherwise it is the correction factor
            double unit = sqx + sqy + sqz + sqw;
            double test = q.X * q.Y + q.Z * q.W;

            if (test > 0.499f * unit)
            {
                // Singularity at north pole
                pitchYawRoll.Y = 2f * (float)Math.Atan2(q.X, q.W);  // Yaw
                pitchYawRoll.X = (float)Math.PI * 0.5f;             // Pitch
                pitchYawRoll.Z = 0f;                                // Roll
                return pitchYawRoll;
            }
            else if (test < -0.499f * unit)
            {
                // Singularity at south pole
                pitchYawRoll.Y = -2f * (float)Math.Atan2(q.X, q.W); // Yaw
                pitchYawRoll.X = (float)-Math.PI * 0.5f;            // Pitch
                pitchYawRoll.Z = 0f;                                // Roll
                return pitchYawRoll;
            }

            pitchYawRoll.Y = (float)Math.Atan2(2 * q.Y * q.W - 2 * q.X * q.Z, sqx - sqy - sqz + sqw);       // Yaw
            pitchYawRoll.X = (float)Math.Asin(2 * test / unit);                                             // Pitch
            pitchYawRoll.Z = (float)Math.Atan2(2 * q.X * q.W - 2 * q.Y * q.Z, -sqx + sqy - sqz + sqw);      // Roll

            return pitchYawRoll;
        }

        public static Quaternion QuaternionFromEulerAngles(this Quaternion q, float yaw, float pitch, float roll)
        {
            // Heading = Yaw
            // Attitude = Pitch
            // Bank = Roll

            yaw *= 0.5f;
            pitch *= 0.5f;
            roll *= 0.5f;

            // Assuming the angles are in radians.
            float c1 = (float)Math.Cos(yaw);
            float s1 = (float)Math.Sin(yaw);
            float c2 = (float)Math.Cos(pitch);
            float s2 = (float)Math.Sin(pitch);
            float c3 = (float)Math.Cos(roll);
            float s3 = (float)Math.Sin(roll);
            float c1c2 = c1 * c2;
            float s1s2 = s1 * s2;

            q.W = c1c2 * c3 - s1s2 * s3;
            q.X = c1c2 * s3 + s1s2 * c3;
            q.Y = s1 * c2 * c3 + c1 * s2 * s3;
            q.Z = c1 * s2 * c3 - s1 * c2 * s3;

            return q;
        }

        public static string[] SplitAndReduceDup(this string input, char reduceChar)
        {
            List<string> end = new List<string>();
            string temp = "";
            foreach(var ob in input)
            {
                if (ob == reduceChar)
                {
                    if(temp.Count() != 0)
                    {
                        end.Add(temp);
                        temp = "";
                    }
                }
                else
                {
                    temp += ob;
                }
            }
            if(end.Count == 0)
            {
                end.Add(" ");
            }
            return end.ToArray();
        }
    }
}
