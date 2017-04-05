using LELEngine;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
public sealed class MeshRenderer : Behaviour
{
    private VertexBuffer<Vertex> vertexBuffer;
    private VertexArray<Vertex> vertexArray;
    public Material material;
    public Mesh Mesh;
    public string materialPath;
    public string meshPath;
    private ShaderProgram Shader;

    public override void Awake()
    {            
        // Get the mesh
        Mesh = InternalStorage.GetOrCreateMesh(meshPath);

        vertexBuffer = new VertexBuffer<Vertex>(Vertex.Size);

        foreach (var vert in Mesh.Verticies)
        {
            vertexBuffer.AddVertex(vert);
        }
        
        // Set indicies to be used in the vertex buffer
        //vertexBuffer.SetIndices(Mesh.indicies.ToArray());

        // load material
        material = InternalStorage.GetOrCreateMaterial(materialPath);

        // load shader
        Shader = InternalStorage.GetOrCreateShader(material.Shader);

        // IMPORTANT: Set the shader to be used in the main loop
        UsingShader = Shader;

        // create vertex array to specify vertex layout
        this.vertexArray = new VertexArray<Vertex>(
            this.vertexBuffer, Shader,
            new VertexAttribute("vPosition", 3, VertexAttribPointerType.Float, Vertex.Size, 0),
            new VertexAttribute("vColor", 4, VertexAttribPointerType.Float, Vertex.Size, 3 * 4),
            new VertexAttribute("vTexCoord", 2, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4)),
            new VertexAttribute("vNormal", 3, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4) + (2 * 4)),
            new VertexAttribute("vTangent", 3, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4) + (2 * 4) + (3 * 4)),
            new VertexAttribute("vBitangent", 3, VertexAttribPointerType.Float, Vertex.Size, (3 * 4) + (4 * 4) + (2 * 4) + (3 * 4) + (3 * 4))
            );
    }

    public override void OnRender()
    {
        // set transformation uniforms
        transform.SetModelMatrix(Shader);
        Camera.main.SetUniforms(Shader);

        // set uniforms for lights
        Lighting.SetUniforms(Shader);

        // set uniforms in material
        material.SetUniforms(Shader);

        // bind vertex buffer and array objects
        this.vertexArray.Bind();
        this.vertexBuffer.Bind();
        
        // upload vertices to GPU and draw them
        this.vertexBuffer.BufferData();
        this.vertexBuffer.Draw();
    }
}
