using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Thin wrapper over a compute-only <see cref="ShaderProgram" /> with dispatch and barrier helpers.
	///     Requires OpenGL 4.3.
	/// </summary>
	public sealed class ComputeShader
	{
		#region PublicFields

		public ShaderProgram Program { get; }

		#endregion

		#region Constructors

		/// <summary>
		///     Loads a .shader file containing a single "/////Compute" stage from the game's Shaders directory.
		/// </summary>
		public ComputeShader(string path)
		{
			Program = new ShaderProgram(path);
		}

		/// <summary>
		///     Compiles compute shader source code directly.
		/// </summary>
		public static ComputeShader FromSource(string name, string source)
		{
			Shader stage = new Shader(source, ShaderType.ComputeShader, name);
			ShaderProgram program = new ShaderProgram(name, stage);
			stage.Delete();
			return new ComputeShader(program);
		}

		private ComputeShader(ShaderProgram program)
		{
			Program = program;
		}

		#endregion

		#region PublicMethods

		public void Use()
		{
			Program.Use();
		}

		/// <summary>
		///     Dispatches the given number of work groups. The program must be in use.
		/// </summary>
		public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
		{
			GL.DispatchCompute(groupsX, groupsY, groupsZ);
		}

		/// <summary>
		///     Dispatches enough work groups to cover the given number of threads with the given local size.
		/// </summary>
		public void DispatchThreads(int threadsX, int threadsY, int threadsZ, int localX, int localY, int localZ)
		{
			Dispatch(
				DivideRoundUp(threadsX, localX),
				DivideRoundUp(threadsY, localY),
				DivideRoundUp(threadsZ, localZ));
		}

		/// <summary>
		///     Makes image/buffer writes visible to following passes.
		/// </summary>
		public static void Barrier(MemoryBarrierFlags flags = MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit)
		{
			GL.MemoryBarrier(flags);
		}

		public void Delete()
		{
			Program.Delete();
		}

		#endregion

		#region PrivateMethods

		private static int DivideRoundUp(int value, int divisor)
		{
			return Math.Max(1, (value + divisor - 1) / divisor);
		}

		#endregion
	}
}
