using LELEngine;
using LELEngine.Shaders;
using OpenTK;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
public sealed class Camera : Behaviour
{
    public static Camera main;

	public float Aspect
	{
		get
		{
			return _Aspect;
		}
		set
		{
			_Aspect = value;
		}
	}

	public float NearClip
	{
		get
		{
			return nearClip;
		}
		set
		{
			nearClip = value;
		}
	}

	public float FarClip
	{
		get
		{
			return farClip;
		}
		set
		{
			farClip = value;
		}
	}

	public float FoV
	{
		get
		{
			return foV;
		}
		set
		{
			foV = value;
		}
	}

	private LELEngine.Shaders.Uniforms.Matrix4 viewMatrix;
    private LELEngine.Shaders.Uniforms.Matrix4 projectionMatrix;

    private float foV;
    private float nearClip;
    private float farClip;
	private float _Aspect = 4 / 3;

	public void SetViewUniform(ShaderProgram shader)
    {
        viewMatrix.Matrix = Matrix4.LookAt(transform.position, transform.position + transform.forward * 2, transform.up);
        viewMatrix.Set(shader);
    }

    public void SetProjectionUniform(ShaderProgram shader)
    {
        projectionMatrix.Matrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
        projectionMatrix.Set(shader);
    }

    public void SetUniforms(ShaderProgram shader)
    {
        Aspect = (float)Game.Mono.Width / (float)Game.Mono.Height;
        projectionMatrix.Matrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
        viewMatrix.Matrix = Matrix4.LookAt(transform.position, transform.position + transform.forward, transform.up);

        projectionMatrix.Set(shader);
        viewMatrix.Set(shader);
    }

    public override void Awake()
    {
        main = this;
        projectionMatrix = new LELEngine.Shaders.Uniforms.Matrix4("projectionMatrix");
        projectionMatrix.Matrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
        viewMatrix = new LELEngine.Shaders.Uniforms.Matrix4("viewMatrix");
        viewMatrix.Matrix = Matrix4.CreateFromQuaternion(transform.rotation) * Matrix4.CreateTranslation(transform.position) * Matrix4.CreateScale(transform.scale);
    }
}
