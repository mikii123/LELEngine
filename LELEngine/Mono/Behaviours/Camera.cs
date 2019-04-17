using LELEngine;
using LELEngine.Shaders;
using LELEngine.Shaders.Uniforms;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
public sealed class Camera : Behaviour
{
	#region PublicFields

	public static Camera main;

	public float Aspect { get; set; } = 4 / 3;

	public float NearClip { get; set; }

	public float FarClip { get; set; }

	public float FoV { get; set; }

	#endregion

	#region PrivateFields

	private Matrix4 viewMatrix;
	private Matrix4 projectionMatrix;

	#endregion

	#region UnityMethods

	public override void Awake()
	{
		main = this;
		projectionMatrix = new Matrix4("projectionMatrix");
		projectionMatrix.Matrix = OpenTK.Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
		viewMatrix = new Matrix4("viewMatrix");
		viewMatrix.Matrix = OpenTK.Matrix4.CreateFromQuaternion(transform.rotation) * OpenTK.Matrix4.CreateTranslation(transform.position) * OpenTK.Matrix4.CreateScale(transform.scale);
	}

	#endregion

	#region PublicMethods

	public void SetViewUniform(ShaderProgram shader)
	{
		viewMatrix.Matrix = OpenTK.Matrix4.LookAt(transform.position, transform.position + transform.forward * 2, transform.up);
		viewMatrix.Set(shader);
	}

	public void SetProjectionUniform(ShaderProgram shader)
	{
		projectionMatrix.Matrix = OpenTK.Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
		projectionMatrix.Set(shader);
	}

	public void SetUniforms(ShaderProgram shader)
	{
		Aspect = Game.Mono.Width / (float)Game.Mono.Height;
		projectionMatrix.Matrix = OpenTK.Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
		viewMatrix.Matrix = OpenTK.Matrix4.LookAt(transform.position, transform.position + transform.forward, transform.up);

		projectionMatrix.Set(shader);
		viewMatrix.Set(shader);
	}

	#endregion
}
