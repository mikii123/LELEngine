using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Per-camera data shared by the render passes: one per camera of the stack a frame renders, built by
	///     <see cref="Renderer" />. The frame and final passes get the lighting camera's.
	/// </summary>
	public sealed class RenderContext
	{
		#region PublicFields

		public Renderer Renderer;
		public RenderSettings Settings;

		public Camera Camera;

		/// <summary>The layers the camera draws (bit n = layer n): <see cref="Renderers" /> are filtered by it, the GPU culling tests it.</summary>
		public int CullingMask = -1;

		/// <summary>The camera lights the world: its view has the screen-space GI, the shadow maps and GI volumes follow it.</summary>
		public bool IsLightingCamera = true;

		/// <summary>The first camera of the stack: it cleared the image, the sky is drawn with it.</summary>
		public bool IsBaseCamera = true;
		public Matrix4 View;
		public Matrix4 Projection;
		public Matrix4 ViewProjection;
		public Vector3 CameraPosition;

		/// <summary>Window (backbuffer) size.</summary>
		public int Width;
		public int Height;

		/// <summary>
		///     The camera's renderers (on its layers) with opaque sub-meshes: the depth prepass and opaque pass draw them;
		///     the lighting camera's are the world that casts shadows and makes up the GI.
		/// </summary>
		public IReadOnlyList<MeshRenderer> Renderers;

		/// <summary>The camera's renderers with transparent sub-meshes (<see cref="MeshRenderer.HasTransparentParts" />), drawn by the transparent pass only.</summary>
		public IReadOnlyList<MeshRenderer> TransparentRenderers;

		public IReadOnlyList<Behaviour> Behaviours;
		public RenderQueue RenderQueue;

		/// <summary>HDR color + depth the scene is rendered into before post-processing.</summary>
		public Framebuffer SceneTarget;
		public RenderTexture SceneColor => SceneTarget.ColorAttachments[0];
		public RenderTexture SceneDepth => SceneTarget.DepthAttachment;

		/// <summary>Destination of the post-processed image; null = the window's default framebuffer.</summary>
		public Framebuffer OutputTarget;

		/// <summary>Engine depth-only program (shadow pass, depth prepass).</summary>
		public ShaderProgram DepthOnlyProgram;

		/// <summary>Program of the transparent shadow casters: their transmittance and depth (<see cref="TransparentShadows" />).</summary>
		public ShaderProgram TransparentShadowProgram;
		public FullscreenQuad Fullscreen;

		/// <summary>Set by the geometry prepass so the opaque pass can switch to EQUAL depth testing.</summary>
		public bool DepthPrepassDone;

		/// <summary>World normal (xyz) + roughness (w) per pixel from the geometry prepass; null when it did not run.</summary>
		public RenderTexture NormalRoughness;

		#endregion
	}
}
