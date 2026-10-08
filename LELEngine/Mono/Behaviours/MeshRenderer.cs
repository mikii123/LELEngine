using LELEngine;
using LELEngine.Shaders;

[ExecuteAlways]
public sealed class MeshRenderer : Behaviour
{
	#region PublicFields

	public ShaderProgram UsingShader
	{
		get => Material?.UsingShader;
		internal set
		{
			if (Material == null)
			{
				return;
			}

			Material.SetShader(value);
		}
	}

	/// <summary>The mesh drawn; it uploads into the shared geometry buffers (Rendering.GeometryPool) on first use.</summary>
	public Mesh Mesh
	{
		get => mesh;
		private set => mesh = value;
	}

	public Material Material
	{
		get => material;
		private set => material = value;
	}

	/// <summary>Asset path of the material (null for a material created in code).</summary>
	public string MaterialPath => material?.AssetPath;

	/// <summary>Asset path of the mesh.</summary>
	public string MeshPath => mesh?.AssetPath;

	public bool CastShadows = true;
	public bool ReceiveShadows = true;

	/// <summary>Voxelized into the GI volume (emits / bounces light onto others).</summary>
	public bool ContributesToGI = true;

	/// <summary>Samples indirect light from the GI volume.</summary>
	public bool ReceiveGI = true;

	/// <summary>
	///     Transform, mesh and material do not change at runtime. Static renderers are voxelized once into a
	///     cached volume instead of every frame. Call Lighting.GI.InvalidateStatic() after changing one.
	/// </summary>
	public bool IsStatic;

	#endregion

	#region PrivateFields

	[SerializeField] private Mesh mesh;
	[SerializeField] private Material material;

	#endregion

	#region PublicMethods

	/// <summary>
	///     Set the material by asset path or file name. Automatically set shader.
	/// </summary>
	public void SetMaterial(string path)
	{
		Material = InternalStorage.GetOrCreateMaterial(path);
	}

	public void SetMaterial(Material material)
	{
		Material = material;
	}

	/// <summary>
	///     Set the mesh by asset path or file name.
	/// </summary>
	public void SetMesh(string path)
	{
		Mesh = InternalStorage.GetOrCreateMesh(path);
	}

	public void SetMesh(Mesh _mesh)
	{
		if (_mesh == null)
		{
			return;
		}

		Mesh = _mesh;
	}

	/// <summary>
	///     Set the shader. Updates the material.
	/// </summary>
	public void SetShader(string path)
	{
		Material.SetShader(path);
	}

	/// <summary>
	///     Uploads the mesh into the shared geometry buffers now instead of at its first draw. The vertex layout uses
	///     fixed attribute locations, so it works with any shader program.
	/// </summary>
	public void BufferVerticies()
	{
		LELEngine.Rendering.GeometryPool.Prepare(Mesh);
	}

	/// <summary>
	///     Full material draw: sets camera, lighting and material uniforms on the material shader, then draws.
	///     The material shader must already be active.
	/// </summary>
	public override void Render()
	{
		if (Mesh == null || UsingShader == null)
		{
			return;
		}

		transform.SetModelMatrix(UsingShader);
		(Camera.current ?? Camera.main)?.SetUniforms(UsingShader);
		Lighting.SetUniforms(UsingShader, ReceiveShadows, ReceiveGI);
		Material.SetUniforms();

		DrawGeometry();
	}

	/// <summary>
	///     Per-object part of <see cref="Render" />: model matrix and material uniforms, then the draw. The caller
	///     has already set the camera and lighting uniforms on the (active) material shader for this object's
	///     ReceiveShadows / ReceiveGI flags, so draws grouped by shader skip re-sending them per object.
	/// </summary>
	public void RenderObject()
	{
		if (Mesh == null || UsingShader == null)
		{
			return;
		}

		transform.SetModelMatrix(UsingShader);
		Material.SetUniforms();

		DrawGeometry();
	}

	/// <summary>
	///     Draws geometry with an externally provided program (depth prepass, shadow map, voxelization).
	///     Only the model matrix is set; the caller sets everything else. The program must be active.
	/// </summary>
	public void RenderWith(ShaderProgram program)
	{
		if (Mesh == null)
		{
			return;
		}

		transform.SetModelMatrix(program);
		DrawGeometry();
	}

	/// <summary>
	///     Alias of <see cref="RenderWith" /> kept for depth-only passes.
	/// </summary>
	public void RenderDepth(ShaderProgram program)
	{
		RenderWith(program);
	}

	/// <summary>
	///     Binds the shared geometry and issues the draw call. No uniforms are touched.
	/// </summary>
	public void DrawGeometry()
	{
		LELEngine.Rendering.GeometryPool.Draw(mesh);
	}

	#endregion
}
