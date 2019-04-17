namespace LELEngine
{
	internal sealed class Mathf
	{
		#region PublicMethods

		public static float Clamp(float value, float min, float max)
		{
			if (value >= max)
			{
				return max;
			}

			if (value <= min)
			{
				return min;
			}

			return value;
		}

		#endregion
	}
}
