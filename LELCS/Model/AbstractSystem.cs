using System;

namespace LELCS.Model
{
	public abstract class AbstractSystem
	{
		#region PublicFields

		public abstract Type[] Subtractive { get; }

		#endregion

		#region AllOtherMembers

		internal abstract void ValidateAndExecute(ECSEntity ecsEntity, ECSManager manager);

		#endregion
	}
}
