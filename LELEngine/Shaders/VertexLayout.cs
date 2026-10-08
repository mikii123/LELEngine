using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Fixed vertex attribute locations shared by every shader program.
	///     Binding attributes to fixed slots before linking makes vertex array objects
	///     independent from the shader they are drawn with, so one VAO can be used by
	///     the material shader, the depth-only shader, the shadow pass, etc.
	/// </summary>
	public static class VertexLayout
	{
		#region PublicFields

		public const int PositionLocation = 0;
		public const int ColorLocation = 1;
		public const int TexCoordLocation = 2;
		public const int NormalLocation = 3;
		public const int TangentLocation = 4;
		public const int BitangentLocation = 5;

		/// <summary>
		///     Per-instance index of GPU-driven draws when the context has no shader draw parameters (an identity buffer
		///     read through the draw's base instance, see GeometryPool).
		/// </summary>
		public const int InstanceIndexLocation = 6;

		public const string PositionName = "vPosition";
		public const string ColorName = "vColor";
		public const string TexCoordName = "vTexCoord";
		public const string NormalName = "vNormal";
		public const string TangentName = "vTangent";
		public const string BitangentName = "vBitangent";
		public const string InstanceIndexName = "vInstanceIndex";

		#endregion

		#region PublicMethods

		/// <summary>
		///     Binds the standard attribute names to their fixed locations. Must be called before linking.
		/// </summary>
		public static void BindStandardLocations(int programHandle)
		{
			GL.BindAttribLocation(programHandle, PositionLocation, PositionName);
			GL.BindAttribLocation(programHandle, ColorLocation, ColorName);
			GL.BindAttribLocation(programHandle, TexCoordLocation, TexCoordName);
			GL.BindAttribLocation(programHandle, NormalLocation, NormalName);
			GL.BindAttribLocation(programHandle, TangentLocation, TangentName);
			GL.BindAttribLocation(programHandle, BitangentLocation, BitangentName);
			GL.BindAttribLocation(programHandle, InstanceIndexLocation, InstanceIndexName);
		}

		/// <summary>
		///     Standard attribute layout matching <see cref="Vertex" />.
		/// </summary>
		internal static VertexAttribute[] CreateStandardAttributes()
		{
			const int f = sizeof(float);
			return new[]
			{
				new VertexAttribute(PositionLocation, 3, VertexAttribPointerType.Float, Vertex.Size, 0),
				new VertexAttribute(ColorLocation, 4, VertexAttribPointerType.Float, Vertex.Size, 3 * f),
				new VertexAttribute(TexCoordLocation, 2, VertexAttribPointerType.Float, Vertex.Size, (3 + 4) * f),
				new VertexAttribute(NormalLocation, 3, VertexAttribPointerType.Float, Vertex.Size, (3 + 4 + 2) * f),
				new VertexAttribute(TangentLocation, 3, VertexAttribPointerType.Float, Vertex.Size, (3 + 4 + 2 + 3) * f),
				new VertexAttribute(BitangentLocation, 3, VertexAttribPointerType.Float, Vertex.Size, (3 + 4 + 2 + 3 + 3) * f)
			};
		}

		#endregion
	}
}
