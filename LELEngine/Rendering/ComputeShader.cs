using System;
using LELEngine.Shaders;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A program with a compute stage: <see cref="Use" /> it, give it its values, textures, images and buffers,
	///     then <see cref="Dispatch" /> its work groups. What a dispatch wrote is visible to everything given after it.
	/// </summary>
	public sealed class ComputeShader
	{
		#region PublicFields

		public ShaderProgram Program { get; }

		#endregion

		#region Constructors

		public ComputeShader(string path)
		{
			Program = new ShaderProgram(path);
		}

		public ComputeShader(string path, params string[] defines)
		{
			Program = new ShaderProgram(path, defines);
		}

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

		public static ComputeShader FromProgram(ShaderProgram program)
		{
			return new ComputeShader(program);
		}

		#endregion

		#region PublicMethods

		public void Use()
		{
			Program.Use();
		}

		/// <summary>Runs the program in use (<see cref="Use" />) over a grid of work groups.</summary>
		public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
		{
			GraphicsDevice.Current.Commands.Dispatch(groupsX, groupsY, groupsZ);
		}

		/// <summary>A dispatch whose three group counts (uints) the GPU reads from a buffer.</summary>
		public void DispatchIndirect(GpuBuffer arguments, long offsetBytes)
		{
			GraphicsDevice.Current.Commands.DispatchIndirect(arguments, offsetBytes);
		}

		/// <summary>The OpenGL way, with a buffer object's name: only for the code that still gives OpenGL commands itself.</summary>
		public void DispatchIndirect(int buffer, int offsetBytes)
		{
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, buffer);
			GL.DispatchComputeIndirect((IntPtr)offsetBytes);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);
		}

		public void DispatchThreads(int threadsX, int threadsY, int threadsZ, int localX, int localY, int localZ)
		{
			Dispatch(
				DivideRoundUp(threadsX, localX),
				DivideRoundUp(threadsY, localY),
				DivideRoundUp(threadsZ, localZ));
		}

		/// <summary>
		///     The OpenGL way of making a dispatch's writes visible: only for the code that still gives OpenGL commands
		///     itself (the device does it for its own commands).
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
