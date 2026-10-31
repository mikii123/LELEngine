using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Shader values of one renderer on top of its materials (Unity's MaterialPropertyBlock): numbers, vectors, colors,
	///     matrices, textures and float / vector arrays by uniform name, uploaded right after the material's own values at
	///     every draw of the renderer, so they win over the material's. Renderers sharing a material keep values of their
	///     own without a copy of the material each (<see cref="MeshRenderer.SetPropertyBlock" />; a
	///     <see cref="Rendering.ICustomRenderer" /> hands one with each draw). Values only: keywords, render state and queue
	///     stay the material's.
	///     Setting a name again writes in place: a block updated every frame allocates nothing (arrays keep their storage
	///     while they do not grow). Names the shader does not have are ignored.
	///     A program keeps uniform values between draws, and a material uploads only what its shader declares: a value for
	///     a name outside the shader's Properties block stays in the program for the next renderer drawn with it. Declare
	///     the shader's per-renderer values ([PerRendererData], with the default draws without a block get).
	/// </summary>
	public sealed class MaterialPropertyBlock
	{
		#region PublicFields

		/// <summary>No value is set.</summary>
		public bool isEmpty => entries.Count == 0;

		/// <summary>Number of values set.</summary>
		public int Count => entries.Count;

		#endregion

		#region PrivateFields

		private enum Kind
		{
			Float,
			Int,
			Vector,
			Matrix,
			Texture,
			FloatArray,
			VectorArray
		}

		private sealed class Entry
		{
			public string Name;
			public Kind Kind;
			public Vector4 Value;
			public Matrix4 Matrix;
			public Texture Texture;

			// Arrays: the elements (vectors as four floats each), Count of them in use.
			public float[] Data;
			public int Count;
		}

		private readonly List<Entry> entries = new List<Entry>();

		#endregion

		#region PublicMethods

		/// <summary>Removes every value (the materials' own show again).</summary>
		public void Clear()
		{
			entries.Clear();
		}

		public bool HasProperty(string name)
		{
			return Find(name) != null;
		}

		/// <summary>Removes one value.</summary>
		public void RemoveProperty(string name)
		{
			Entry entry = Find(name);
			if (entry != null)
			{
				entries.Remove(entry);
			}
		}

		public void SetFloat(string name, float value)
		{
			Put(name, Kind.Float).Value = new Vector4(value, 0f, 0f, 0f);
		}

		public void SetInt(string name, int value)
		{
			Put(name, Kind.Int).Value = new Vector4(value, 0f, 0f, 0f);
		}

		/// <summary>A vector (vec2 / vec3 uniforms take the first components).</summary>
		public void SetVector(string name, Vector4 value)
		{
			Put(name, Kind.Vector).Value = value;
		}

		public void SetColor(string name, Color4 value)
		{
			Put(name, Kind.Vector).Value = new Vector4(value.R, value.G, value.B, value.A);
		}

		public void SetMatrix(string name, Matrix4 value)
		{
			Put(name, Kind.Matrix).Matrix = value;
		}

		/// <summary>A texture (null: the sampler kind's neutral default, as a material without one).</summary>
		public void SetTexture(string name, Texture value)
		{
			Put(name, Kind.Texture).Texture = value;
		}

		/// <summary>A float[] uniform from its first element ("name" or "name[0]"); copied.</summary>
		public void SetFloatArray(string name, ReadOnlySpan<float> values)
		{
			Entry entry = Put(name, Kind.FloatArray);
			Store(entry, values.Length);
			values.CopyTo(entry.Data);
		}

		/// <summary>A vec4[] uniform from its first element; copied.</summary>
		public void SetVectorArray(string name, ReadOnlySpan<Vector4> values)
		{
			Entry entry = Put(name, Kind.VectorArray);
			Store(entry, values.Length * 4);
			entry.Count = values.Length;
			for (int i = 0; i < values.Length; i++)
			{
				entry.Data[i * 4] = values[i].X;
				entry.Data[i * 4 + 1] = values[i].Y;
				entry.Data[i * 4 + 2] = values[i].Z;
				entry.Data[i * 4 + 3] = values[i].W;
			}
		}

		public float GetFloat(string name, float fallback = 0f)
		{
			Entry entry = Find(name);
			return entry != null && (entry.Kind == Kind.Float || entry.Kind == Kind.Int || entry.Kind == Kind.Vector) ? entry.Value.X : fallback;
		}

		public int GetInt(string name, int fallback = 0)
		{
			Entry entry = Find(name);
			return entry != null && (entry.Kind == Kind.Float || entry.Kind == Kind.Int) ? (int)entry.Value.X : fallback;
		}

		public Vector4 GetVector(string name, Vector4 fallback = default)
		{
			Entry entry = Find(name);
			return entry != null && (entry.Kind == Kind.Vector || entry.Kind == Kind.Float || entry.Kind == Kind.Int) ? entry.Value : fallback;
		}

		public Color4 GetColor(string name, Color4 fallback = default)
		{
			Entry entry = Find(name);
			return entry != null && entry.Kind == Kind.Vector ? new Color4(entry.Value.X, entry.Value.Y, entry.Value.Z, entry.Value.W) : fallback;
		}

		public Matrix4 GetMatrix(string name)
		{
			Entry entry = Find(name);
			return entry != null && entry.Kind == Kind.Matrix ? entry.Matrix : Matrix4.Identity;
		}

		public Texture GetTexture(string name)
		{
			Entry entry = Find(name);
			return entry != null && entry.Kind == Kind.Texture ? entry.Texture : null;
		}

		/// <summary>A copy of a float array value (empty when there is none).</summary>
		public float[] GetFloatArray(string name)
		{
			Entry entry = Find(name);
			return entry != null && entry.Kind == Kind.FloatArray ? entry.Data.AsSpan(0, entry.Count).ToArray() : Array.Empty<float>();
		}

		/// <summary>A copy of a vector array value (empty when there is none).</summary>
		public Vector4[] GetVectorArray(string name)
		{
			Entry entry = Find(name);
			if (entry == null || entry.Kind != Kind.VectorArray)
			{
				return Array.Empty<Vector4>();
			}

			var values = new Vector4[entry.Count];
			for (int i = 0; i < values.Length; i++)
			{
				values[i] = new Vector4(entry.Data[i * 4], entry.Data[i * 4 + 1], entry.Data[i * 4 + 2], entry.Data[i * 4 + 3]);
			}

			return values;
		}

		/// <summary>Replaces every value with a copy of another block's.</summary>
		public void CopyFrom(MaterialPropertyBlock source)
		{
			if (ReferenceEquals(source, this))
			{
				return;
			}

			entries.Clear();
			if (source == null)
			{
				return;
			}

			foreach (Entry from in source.entries)
			{
				var entry = new Entry { Name = from.Name, Kind = from.Kind, Value = from.Value, Matrix = from.Matrix, Texture = from.Texture, Count = from.Count };
				if (from.Data != null)
				{
					entry.Data = (float[])from.Data.Clone();
				}

				entries.Add(entry);
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Uploads the values to the active program; textures go on the material texture units from
		///     <paramref name="textureUnit" /> on (after the material's own).
		/// </summary>
		internal void Apply(ShaderProgram program, ref int textureUnit)
		{
			foreach (Entry entry in entries)
			{
				switch (entry.Kind)
				{
					case Kind.Float:
					case Kind.Int:
						FloatProperty.UploadFloat(program, entry.Name, entry.Value.X);
						break;
					case Kind.Vector:
						VectorProperty.UploadVector(program, entry.Name, entry.Value);
						break;
					case Kind.Matrix:
						int matrixLocation = program.GetUniformLocation(entry.Name);
						if (matrixLocation >= 0)
						{
							GL.UniformMatrix4(matrixLocation, false, ref entry.Matrix);
						}

						break;
					case Kind.Texture:
						TextureProperty.Bind(program, entry.Name, entry.Texture, ref textureUnit);
						break;
					case Kind.FloatArray:
						int floatsLocation = program.GetUniformLocation(entry.Name);
						if (floatsLocation >= 0 && entry.Count > 0)
						{
							GL.Uniform1(floatsLocation, entry.Count, entry.Data);
						}

						break;
					case Kind.VectorArray:
						int vectorsLocation = program.GetUniformLocation(entry.Name);
						if (vectorsLocation >= 0 && entry.Count > 0)
						{
							GL.Uniform4(vectorsLocation, entry.Count, entry.Data);
						}

						break;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private Entry Find(string name)
		{
			foreach (Entry entry in entries)
			{
				if (entry.Name == name)
				{
					return entry;
				}
			}

			return null;
		}

		// The entry of a name, made or turned into the kind (a value of another kind under the name is replaced).
		private Entry Put(string name, Kind kind)
		{
			if (string.IsNullOrEmpty(name))
			{
				throw new ArgumentException("A property needs a name", nameof(name));
			}

			Entry entry = Find(name);
			if (entry == null)
			{
				entry = new Entry { Name = name };
				entries.Add(entry);
			}

			if (entry.Kind != kind)
			{
				entry.Kind = kind;
				entry.Texture = null;
			}

			return entry;
		}

		private static void Store(Entry entry, int floats)
		{
			if (entry.Data == null || entry.Data.Length < floats)
			{
				entry.Data = new float[floats];
			}

			entry.Count = floats;
		}

		#endregion
	}
}
