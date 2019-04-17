using LELEngine;

public sealed class DirectionalLight : Behaviour
{
	#region PublicFields

	public static DirectionalLight This;

	#endregion

	#region UnityMethods

	public override void Awake()
	{
		This = this;
	}

	#endregion
}
