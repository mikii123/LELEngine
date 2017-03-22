using OpenTK;
using LELEngine;
using LELEngine.Shaders;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
class Camera : Behaviour
{
    public static Camera main;
    private LELEngine.Shaders.Uniforms.Matrix4 viewMatrix;
    private LELEngine.Shaders.Uniforms.Matrix4 projectionMatrix;

    public float FoV;
    public float Aspect;
    public float nearClip;
    public float farClip;

    public void SetViewUniform(ShaderProgram shader)
    {
        viewMatrix.Matrix = OpenTK.Matrix4.LookAt(transform.position, transform.position + transform.forward * 2, transform.up);
        viewMatrix.Set(shader);
    }

    public void SetProjectionUniform(ShaderProgram shader)
    {
        projectionMatrix.Matrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.DegToRad2, Aspect, nearClip, farClip);
        projectionMatrix.Set(shader);
    }

    public void SetUniforms(ShaderProgram shader)
    {
        projectionMatrix.Matrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.DegToRad2, Aspect, nearClip, farClip);
        viewMatrix.Matrix = Matrix4.LookAt(transform.position, transform.position + transform.forward, transform.up);
        projectionMatrix.Set(shader);
        viewMatrix.Set(shader);
    }

    public override void Awake()
    {
        main = this;
        projectionMatrix = new LELEngine.Shaders.Uniforms.Matrix4("projectionMatrix");
        projectionMatrix.Matrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.DegToRad2, Aspect, nearClip, farClip);
        viewMatrix = new LELEngine.Shaders.Uniforms.Matrix4("viewMatrix");
        viewMatrix.Matrix = Matrix4.CreateFromQuaternion(transform.rotation) * Matrix4.CreateTranslation(transform.position) * Matrix4.CreateScale(transform.scale);
    }
}
