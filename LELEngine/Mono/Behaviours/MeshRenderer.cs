using LELEngine;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

public sealed class MeshRenderer : Behaviour
{
	public ShaderProgram UsingShader { get; private set; }
    public Material Material { get; private set; }
    public Mesh Mesh { get; private set; }
    public string MaterialPath { get; private set; }
    public string MeshPath { get; private set; }
	
	private VertexBuffer<Vertex> vertexBuffer;
	private VertexArray<Vertex> vertexArray;

	private bool verticiesBuffered;

	public void SetMaterial(string path)
	{
		MaterialPath = path;

		// Get the material
		Material = InternalStorage.GetOrCreateMaterial(MaterialPath);

		SetShader();
	}

	public void SetMesh(string path)
	{
		MeshPath = path;

		// Get the mesh
		Mesh = InternalStorage.GetOrCreateMesh(MeshPath);
	}

	public void SetShader(string materialPath)
	{
		SetMaterial(materialPath);
	}

	public void SetShader()
	{
		// Get the shader
		UsingShader = InternalStorage.GetOrCreateShader(Material.Shader);
	}

	public void BufferVerticies()
	{
		vertexBuffer = new VertexBuffer<Vertex>(Vertex.Size);

		foreach (var vert in Mesh.Verticies)
		{
			vertexBuffer.AddVertex(vert);
		}

		// create vertex array to specify vertex layout
		// TODO: Make this generic, and shader uniforms dependent
		this.vertexArray = new VertexArray<Vertex>(
			this.vertexBuffer, UsingShader,
			new VertexAttribute("vPosition", 3, VertexAttribPointerType.Float, Vertex.Size, 0),
			new VertexAttribute("vColor", 4, VertexAttribPointerType.Float, Vertex.Size, 3 * 4),
			new VertexAttribute("vTexCoord", 2, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4)),
			new VertexAttribute("vNormal", 3, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4) + (2 * 4)),
			new VertexAttribute("vTangent", 3, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4) + (2 * 4) + (3 * 4)),
			new VertexAttribute("vBitangent", 3, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4) + (2 * 4) + (3 * 4) + (3 * 4))
		);

		verticiesBuffered = true;
	}

    public override void Awake()
    {
		if (!verticiesBuffered)
		{
			BufferVerticies();
		}
    }

    public override void Render()
    {
        // set transformation uniforms
        transform.SetModelMatrix(UsingShader);
        Camera.main.SetUniforms(UsingShader);

        // set uniforms for lights
        Lighting.SetUniforms(UsingShader);

        // set uniforms in material
        Material.SetUniforms(UsingShader);

        // bind vertex buffer and array objects
        this.vertexArray.Bind();
        this.vertexBuffer.Bind();
        
        // upload vertices to GPU and draw them
        this.vertexBuffer.BufferData();
        this.vertexBuffer.Draw();
    }
}
