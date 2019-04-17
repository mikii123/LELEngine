using LELEngine;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

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

	#endregion

	#region PrivateFields

	private Mesh mesh;
	private VertexBuffer<Vertex> vertexBuffer;
	private VertexArray<Vertex> vertexArray;

	private bool vertexBufferingDone;

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
	///     Buffer vertex layout and vertex attributes. Automatically called on every mesh change.
	/// </summary>
	public void BufferVerticies()
	{
		if (Mesh == null)
		{
			return;
		}

		if (vertexBuffer != null)
		{
			vertexBuffer.Delete();
		}
		if (vertexArray != null)
		{
			vertexArray.Delete();
		}

		vertexBuffer = new VertexBuffer<Vertex>(Vertex.Size);

		foreach (Vertex vertex in Mesh.Verticies)
		{
			vertexBuffer.AddVertex(vertex);
		}

		// create vertex array to specify vertex layout
		// TODO: Make this generic, and shader uniforms dependent
		int offset = 0;
		vertexArray = new VertexArray<Vertex>(
			vertexBuffer,
			UsingShader,
			new VertexAttribute("vPosition", 3, VertexAttribPointerType.Float, Vertex.Size, 0),
			new VertexAttribute("vColor", 4, VertexAttribPointerType.Float, Vertex.Size, 3 * 4),
			new VertexAttribute("vTexCoord", 2, VertexAttribPointerType.Float, Vertex.Size, 3 * 4 + 4 * 4),
			new VertexAttribute("vNormal", 3, VertexAttribPointerType.Float, Vertex.Size, 3 * 4 + 4 * 4 + 2 * 4),
			new VertexAttribute("vTangent", 3, VertexAttribPointerType.Float, Vertex.Size, 3 * 4 + 4 * 4 + 2 * 4 + 3 * 4),
			new VertexAttribute("vBitangent", 3, VertexAttribPointerType.Float, Vertex.Size, 3 * 4 + 4 * 4 + 2 * 4 + 3 * 4 + 3 * 4)
		);

		vertexBufferingDone = true;
	}

	public override void Render()
	{
		if (Mesh == null)
		{
			return;
		}

		// set transformation uniforms
		transform.SetModelMatrix(UsingShader);
		Camera.main.SetUniforms(UsingShader);

		// set uniforms for lights
		Lighting.SetUniforms(UsingShader);

		// set uniforms in material
		Material.SetUniforms();

		// bind vertex buffer and array objects
		vertexArray.Bind();
		vertexBuffer.Bind();

		// upload vertices to GPU and draw them
		vertexBuffer.BufferData();
		vertexBuffer.Draw();
	}

	#endregion
}
