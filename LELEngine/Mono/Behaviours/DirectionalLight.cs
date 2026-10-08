using LELEngine;
using OpenTK.Mathematics;

/// <summary>The sun: shines along its transform's forward axis. <see cref="This" /> is the active scene's first one.</summary>
[ExecuteAlways]
public sealed class DirectionalLight : Behaviour
{
	#region PublicFields

	public static DirectionalLight This => SceneManager.ActiveScene?.MainLight;

	public Color4 Color = Color4.White;

	/// <summary>Radiance units: a white surface facing the sun at strength 1 reflects about 1.</summary>
	public float Strength = 1f;

	#endregion
}
