namespace LELEngine
{
    sealed class Mathf
    {
        public static float Clamp(float value, float min, float max)
        {
            if (value >= max)
                return max;
            else if (value <= min)
                return min;
            else
                return value;
        }
    }
}