using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
public sealed class Camera : Behaviour
{
	#region PublicFields

	public static Camera main;

	public float Aspect { get; set; } = 4f / 3f;

	public float NearClip { get; set; } = 0.1f;

	public float FarClip { get; set; } = 1000f;

	/// <summary>Vertical field of view in degrees.</summary>
	public float FoV { get; set; } = 60f;

	public Matrix4 ViewMatrix { get; private set; } = Matrix4.Identity;
	public Matrix4 ProjectionMatrix { get; private set; } = Matrix4.Identity;
	public Matrix4 ViewProjectionMatrix { get; private set; } = Matrix4.Identity;

	#endregion

	#region UnityMethods

	public override void Awake()
	{
		main = this;
		UpdateMatrices(Aspect);
	}

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
