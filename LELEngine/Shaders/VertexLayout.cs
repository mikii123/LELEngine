using LELEngine.Graphics;

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

		/// <summary>The second texture coordinate set (<see cref="Vertex.texcoord2" />).</summary>
		public const int TexCoord2Location = 7;

		public const string PositionName = "vPosition";
		public const string ColorName = "vColor";
		public const string TexCoordName = "vTexCoord";
		public const string NormalName = "vNormal";
		public const string TangentName = "vTangent";
		public const string BitangentName = "vBitangent";
		public const string InstanceIndexName = "vInstanceIndex";
		public const string TexCoord2Name = "vTexCoord2";

		#endregion

		#region PublicMethods

		/// <summary>
		///     Binds the standard attribute names to their fixed locations. Must be called before linking.
		/// </summary>
		public static void AddStandardLocations(ProgramDescriptor program)
		{
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(PositionName, PositionLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(ColorName, ColorLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(TexCoordName, TexCoordLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(NormalName, NormalLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(TangentName, TangentLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(BitangentName, BitangentLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(InstanceIndexName, InstanceIndexLocation));
			program.AttributeLocations.Add(new System.Collections.Generic.KeyValuePair<string, int>(TexCoord2Name, TexCoord2Location));
		}

		/// <summary>The device's layout of a vertex buffer of <see cref="Vertex" />.</summary>
		public static readonly VertexInputLayout Standard = new VertexInputLayout(Vertex.Size,
			new VertexAttributeDescriptor(PositionLocation, VertexFormat.Float3, 0),
			new VertexAttributeDescriptor(ColorLocation, VertexFormat.Float4, 3 * sizeof(float)),
			new VertexAttributeDescriptor(TexCoordLocation, VertexFormat.Float2, (3 + 4) * sizeof(float)),
			new VertexAttributeDescriptor(NormalLocation, VertexFormat.Float3, (3 + 4 + 2) * sizeof(float)),
			new VertexAttributeDescriptor(TangentLocation, VertexFormat.Float3, (3 + 4 + 2 + 3) * sizeof(float)),
			new VertexAttributeDescriptor(BitangentLocation, VertexFormat.Float3, (3 + 4 + 2 + 3 + 3) * sizeof(float)),
			new VertexAttributeDescriptor(TexCoord2Location, VertexFormat.Float2, (3 + 4 + 2 + 3 + 3 + 3) * sizeof(float)));

		/// <summary>
		///     <see cref="Standard" /> plus a second buffer of one uint per instance at <see cref="InstanceIndexLocation" />:
		///     GPU-driven draws on a device without draw parameters.
		/// </summary>
		public static readonly VertexInputLayout StandardWithInstanceIndex = new VertexInputLayout(
			new[] { new VertexBufferDescriptor(Vertex.Size), new VertexBufferDescriptor(sizeof(uint), true) },
			new[]
			{
				new VertexAttributeDescriptor(PositionLocation, VertexFormat.Float3, 0),
				new VertexAttributeDescriptor(ColorLocation, VertexFormat.Float4, 3 * sizeof(float)),
				new VertexAttributeDescriptor(TexCoordLocation, VertexFormat.Float2, (3 + 4) * sizeof(float)),
				new VertexAttributeDescriptor(NormalLocation, VertexFormat.Float3, (3 + 4 + 2) * sizeof(float)),
				new VertexAttributeDescriptor(TangentLocation, VertexFormat.Float3, (3 + 4 + 2 + 3) * sizeof(float)),
				new VertexAttributeDescriptor(BitangentLocation, VertexFormat.Float3, (3 + 4 + 2 + 3 + 3) * sizeof(float)),
				new VertexAttributeDescriptor(TexCoord2Location, VertexFormat.Float2, (3 + 4 + 2 + 3 + 3 + 3) * sizeof(float)),
				new VertexAttributeDescriptor(InstanceIndexLocation, VertexFormat.UInt1, 0, 1)
			});

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
				new VertexAttribute(BitangentLocation, 3, VertexAttribPointerType.Float, Vertex.Size, (3 + 4 + 2 + 3 + 3) * f),
				new VertexAttribute(TexCoord2Location, 2, VertexAttribPointerType.Float, Vertex.Size, (3 + 4 + 2 + 3 + 3 + 3) * f)
			};
		}

		#endregion
	}
}
