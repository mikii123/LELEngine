using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     One set of directional shadow maps as Engine/Shadows.glsl reads it: a uniform buffer holding the atlas's tiles
	///     (matrix, the sphere a receiver uses the cascade inside, the tile's rectangle), which tiles are each directional
	///     light's, the fade toward the shadow distance and the biases. Uploaded when its maps are rendered and bound to
	///     <see cref="Binding" /> by whoever draws with those maps: the camera's set (<see cref="Lighting.CameraShadows" />,
	///     <see cref="Lighting.SetShadowUniforms" />) or a GI pass's grid-covering set (<see cref="GridShadowMap" />). A
	///     program takes no per-cascade uniform uploads.
	/// </summary>
	public sealed class ShadowUniforms
	{
		#region PublicFields

		/// <summary>The uniform buffer binding of the shadow maps' layout.</summary>
		public const int Binding = 5;

		#endregion

		#region PrivateFields

		// fade, bias, the lights' tiles; then per tile a matrix, a sphere and a rectangle.
		private const int HeaderFloats = 8 + Lighting.ShadowCascadeData.MaxLights * 4;
		private const int FloatsPerTile = 24;

		private readonly float[] data = new float[HeaderFloats + Lighting.ShadowCascadeData.MaxTiles * FloatsPerTile];
		private int buffer;

		#endregion

		#region PublicMethods

		/// <summary>Uploads a set (its tiles, lights, fade) with the project's biases.</summary>
		public void Upload(Lighting.ShadowCascadeData cascades)
		{
			ShadowSettings settings = Lighting.Shadows;
			Array.Clear(data, 0, HeaderFloats);
			data[0] = cascades.FadeCenter.X;
			data[1] = cascades.FadeCenter.Y;
			data[2] = cascades.FadeCenter.Z;
			data[3] = cascades.FadeDistance;
			data[4] = settings.NormalBiasTexels;
			data[5] = settings.DepthBiasTexels;
			for (int i = 0; i < Lighting.ShadowCascadeData.MaxLights; i++)
			{
				data[8 + i * 4] = BitConverter.Int32BitsToSingle(cascades.LightFirst[i]);
				data[8 + i * 4 + 1] = BitConverter.Int32BitsToSingle(cascades.LightCount[i]);
			}

			int tiles = Math.Min(cascades.TileCount, Lighting.ShadowCascadeData.MaxTiles);
			for (int i = 0; i < tiles; i++)
			{
				int at = HeaderFloats + i * FloatsPerTile;
				Matrix4 m = cascades.Matrices[i];
				// Rows in memory order: what a matrix uniform upload sends as the columns.
				data[at + 0] = m.M11;
				data[at + 1] = m.M12;
				data[at + 2] = m.M13;
				data[at + 3] = m.M14;
				data[at + 4] = m.M21;
				data[at + 5] = m.M22;
				data[at + 6] = m.M23;
				data[at + 7] = m.M24;
				data[at + 8] = m.M31;
				data[at + 9] = m.M32;
				data[at + 10] = m.M33;
				data[at + 11] = m.M34;
				data[at + 12] = m.M41;
				data[at + 13] = m.M42;
				data[at + 14] = m.M43;
				data[at + 15] = m.M44;
				Vector4 sphere = cascades.Spheres[i];
				data[at + 16] = sphere.X;
				data[at + 17] = sphere.Y;
				data[at + 18] = sphere.Z;
				data[at + 19] = sphere.W;
				Vector4 rect = cascades.Rects[i];
				data[at + 20] = rect.X;
				data[at + 21] = rect.Y;
				data[at + 22] = rect.Z;
				data[at + 23] = rect.W;
			}

			bool created = buffer == 0;
			if (created)
			{
				buffer = GL.GenBuffer();
			}

			GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
			if (created)
			{
				GL.BufferData(BufferTarget.UniformBuffer, data.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
			}

			// Only the header and the tiles in use.
			GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, (HeaderFloats + tiles * FloatsPerTile) * sizeof(float), data);
			GL.BindBuffer(BufferTarget.UniformBuffer, 0);
		}

		/// <summary>
		///     Binds the set to <see cref="Binding" /> (a program reads it there); made empty first when nothing was uploaded
		///     yet, so a program drawn before the first shadow pass never reads an unbound block.
		/// </summary>
		public void Bind()
		{
			if (buffer == 0)
			{
				buffer = GL.GenBuffer();
				GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
				GL.BufferData(BufferTarget.UniformBuffer, data.Length * sizeof(float), new float[data.Length], BufferUsageHint.DynamicDraw);
				GL.BindBuffer(BufferTarget.UniformBuffer, 0);
			}

			// Always bound again: binding points belong to the GL context, and a set drawn with before may be another's.
			GL.BindBufferBase(BufferRangeTarget.UniformBuffer, Binding, buffer);
		}

		public void Delete()
		{
			if (buffer == 0)
			{
				return;
			}

			GL.DeleteBuffer(buffer);
			buffer = 0;
		}

		#endregion
	}
}
