using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

/// <summary>
///     Perspective camera. <see cref="main" /> is the first enabled camera of the active scene (also in edit
///     mode); <see cref="current" /> is the camera the renderer is drawing with right now (the editor's scene view
///     camera is not part of any scene).
/// </summary>
[ExecuteAlways]
public sealed class Camera : Behaviour
{
	#region PublicFields

	public static Camera main => SceneManager.ActiveScene?.MainCamera;

	/// <summary>Camera of the frame being rendered (set by the renderer).</summary>
	public static Camera current { get; internal set; }

	/// <summary>Vertical field of view in degrees.</summary>
	public float FoV = 60f;

	public float NearClip = 0.1f;

	public float FarClip = 1000f;

	/// <summary>Width / height of the view; set by the renderer every frame.</summary>
	[System.NonSerialized] public float Aspect = 4f / 3f;

	public Matrix4 ViewMatrix { get; private set; } = Matrix4.Identity;
	public Matrix4 ProjectionMatrix { get; private set; } = Matrix4.Identity;
	public Matrix4 ViewProjectionMatrix { get; private set; } = Matrix4.Identity;

	#endregion

	#region PublicMethods

	/// <summary>
	///     Recomputes view and projection from the current transform. Called by the renderer once per frame.
	/// </summary>
	public void UpdateMatrices(float aspect)
	{
		Aspect = aspect;
		ProjectionMatrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
		ViewMatrix = Matrix4.LookAt(transform.position, transform.position + transform.forward, transform.up);
		ViewProjectionMatrix = ViewMatrix * ProjectionMatrix;
	}

	public void SetViewUniform(ShaderProgram shader)
	{
		shader.SetMatrix4("viewMatrix", ViewMatrix);
	}

	public void SetProjectionUniform(ShaderProgram shader)
	{
		shader.SetMatrix4("projectionMatrix", ProjectionMatrix);
	}

	public void SetUniforms(ShaderProgram shader)
	{
		shader.SetMatrix4("projectionMatrix", ProjectionMatrix);
		shader.SetMatrix4("viewMatrix", ViewMatrix);
	}

	#endregion
}
