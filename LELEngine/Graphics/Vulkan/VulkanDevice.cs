using System;
using LELEngine.Windowing;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>The device on Vulkan (being written: the OpenGL device is complete first).</summary>
	internal sealed class VulkanDevice : GraphicsDevice
	{
		public VulkanDevice(NativeWindow window)
		{
			throw new NotSupportedException("The Vulkan device is not available in this build yet.");
		}

		public override GraphicsApi Api => GraphicsApi.Vulkan;
		public override CommandList Commands => throw new NotSupportedException();
		public override GpuTexture Backbuffer => throw new NotSupportedException();
		public override bool HasCurrentContext => false;
		public override GpuTexture CreateTexture(in TextureDescriptor descriptor) => throw new NotSupportedException();
		public override GpuBuffer CreateBuffer(in BufferDescriptor descriptor) => throw new NotSupportedException();
		public override GpuSampler GetSampler(in SamplerDescriptor descriptor) => throw new NotSupportedException();
		public override GpuProgram CreateProgram(ProgramDescriptor descriptor) => throw new NotSupportedException();
		public override GpuProgram CreateProgramFromBinary(ProgramDescriptor descriptor, byte[] binary) => throw new NotSupportedException();
		public override GpuFence CreateFence() => throw new NotSupportedException();
		public override GpuTimestamp CreateTimestamp() => throw new NotSupportedException();
		public override void Flush() => throw new NotSupportedException();
		public override void Finish() => throw new NotSupportedException();
		internal override GraphicsContext CreateWindowContext(NativeWindow window, NativeWindowSettings settings) => throw new NotSupportedException();
	}
}
