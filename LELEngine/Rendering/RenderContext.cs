using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Per-frame data shared by all render passes. Built by <see cref="Renderer" /> at the start of a frame.
	/// </summary>
	public sealed class RenderContext
	{
		#region PublicFields

		public Renderer Renderer;
		public RenderSettings Settings;

		public Camera Camera;
		public Matrix4 View;
		public Matrix4 Projection;
		public Matrix4 ViewProjection;
		public Vector3 CameraPosition;

		/// <summary>Window (backbuffer) size.</summary>
		public int Width;
		public int Height;

		public IReadOnlyList<MeshRenderer> Renderers;
		public IReadOnlyList<Behaviour> Behaviours;
		public RenderQueue RenderQueue;

		/// <summary>HDR color + depth the scene is rendered into before post-processing.</summary>
		public Framebuffer SceneTarget;
		public RenderTexture SceneColor => SceneTarget.ColorAttachments[0];
		public RenderTexture SceneDepth => SceneTarget.DepthAttachment;

		/// <summary>Engine depth-only program (shadow pass, depth prepass).</summary>
		public ShaderProgram DepthOnlyProgram;
		public FullscreenQuad Fullscreen;

		/// <summary>Set by the geometry prepass so the opaque pass can switch to EQUAL depth testing.</summary>
		public bool DepthPrepassDone;

		/// <summary>World normal (xyz) + roughness (w) per pixel from the geometry prepass; null when it did not run.</summary>
		public RenderTexture NormalRoughness;

		#endregion
	}
}
