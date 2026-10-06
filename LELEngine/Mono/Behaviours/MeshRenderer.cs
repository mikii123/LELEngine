using LELEngine;
using LELEngine.Shaders;

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

	public Mesh Mesh
	{
		get => mesh;
		private set
		{
			if (value == null)
			{
				mesh = null;
				return;
			}

			if (mesh != value)
			{
				mesh = value;
				BufferVerticies();
			}
		}
	}

	public Material Material { get; private set; }
	public string MaterialPath { get; private set; }
	public string MeshPath { get; private set; }

	public bool CastShadows = true;
	public bool ReceiveShadows = true;

	/// <summary>Voxelized into the GI volume (emits / bounces light onto others).</summary>
	public bool ContributesToGI = true;

	/// <summary>Samples indirect light from the GI volume.</summary>
	public bool ReceiveGI = true;

	#endregion

	#region PrivateFields

	private Mesh mesh;
	private VertexBuffer<Vertex> vertexBuffer;
	private VertexArray<Vertex> vertexArray;

	#endregion

	#region PublicMethods

	/// <summary>
	///     Set the material. Automatically set shader.
	/// </summary>
	public void SetMaterial(string path)
	{
		MaterialPath = path;
		Material = InternalStorage.GetOrCreateMaterial(MaterialPath);
	}

	public void SetMaterial(Material material)
	{
		Material = material;
		MaterialPath = null;
	}

	/// <summary>
	///     Set the mesh.
	/// </summary>
	public void SetMesh(string path)
	{
		MeshPath = path;

		// Set the mesh
		Mesh = InternalStorage.GetOrCreateMesh(MeshPath);
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
	///     (Re)creates GPU buffers and the vertex layout. Automatically called on every mesh change.
	///     The layout uses fixed attribute locations, so it works with any shader program.
	/// </summary>
	public void BufferVerticies()
	{
		if (Mesh == null)
		{
			return;
		}

		vertexBuffer?.Delete();
		vertexArray?.Delete();

		vertexBuffer = new VertexBuffer<Vertex>(Vertex.Size);

		foreach (Vertex vertex in Mesh.Verticies)
		{
			vertexBuffer.AddVertex(vertex);
		}

		vertexArray = new VertexArray<Vertex>(vertexBuffer, VertexLayout.CreateStandardAttributes());

		// Upload once; later draws only bind.
		vertexArray.Bind();
		vertexBuffer.Bind();
		vertexBuffer.BufferData();
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
		Camera.main.SetUniforms(UsingShader);
		Lighting.SetUniforms(UsingShader, ReceiveShadows, ReceiveGI);
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
	///     Binds the buffers and issues the draw call. No uniforms are touched.
	/// </summary>
	public void DrawGeometry()
	{
		if (vertexArray == null || vertexBuffer == null)
		{
			return;
		}

		vertexArray.Bind();
		vertexBuffer.Bind();
		vertexBuffer.BufferData();
		vertexBuffer.Draw();
	}

	#endregion
}
