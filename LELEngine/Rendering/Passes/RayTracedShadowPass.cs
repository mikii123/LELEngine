using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;
using LELEngine.Shaders;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Shadows traced through the scene's triangles for the opaque surfaces of the lighting camera's view
	///     (<see cref="Lighting.RayTracedLocalShadows" />, <see cref="Lighting.RayTracedDirectionalShadows" />; devices with
	///     ray queries), after the geometry prepass and before the opaque pass:
	///     1. the shadow casters (the view's renderers with <see cref="MeshRenderer.CastShadows" />, their meshes' bottom
	///        levels from <see cref="MeshAccelerationStructures" />, placed as drawn) as a top level of their own
	///        (<see cref="SceneTopLevel" />: built, updated or kept as they change);
	///     2. Engine/RayTracedShadowTrace.shader: per pixel of the prepass's depth, rays to each traced light that reaches
	///        it (the local lights with a slot of <see cref="LightBuffer.ShadowSlots" />, the shadowed directional lights):
	///        one to the light's centre (hard), or <see cref="RayTracingSettings.SoftShadowRays" /> to points of the light's
	///        disc or cone in a pattern fixed per pixel (<see cref="RayTracingSettings.SoftShadows" />);
	///     3. Engine/RayTracedShadowFilter.shader: soft shadows averaged over the pixels of the same surface.
	///     Nothing is kept from one frame to the next: no history, no reprojection, so nothing lags behind or ghosts (the
	///     engine's rule: no temporal accumulation beyond what was agreed). The result is published as
	///     <see cref="Lighting.RayShadows" /> for the lit materials (Engine/RayTracedShadows.glsl).
	///     Not traced: transparent surfaces and other views (local lights reach them unshadowed, directional lights through
	///     their maps), casters whose surface is not their mesh (impostors), alpha-tested casters.
	/// </summary>
	public sealed class RayTracedShadowPass : RenderPass
	{
		#region PublicFields

		public override string Name => "RayTracedShadows";

		/// <summary>The acceleration structure binding of the shadow casters (Engine/RayTracedShadowTrace.shader).</summary>
		public const int SceneBinding = 1;

		/// <summary>The spatial filter's radius in pixels (soft shadows).</summary>
		public const int FilterRadius = 2;

		/// <summary>How far a directional light's ray looks for casters (world units).</summary>
		public const float DirectionalRayLength = 10000f;

		/// <summary>Shadow casters in the top level this frame.</summary>
		public int CasterCount => scene.Count;

		#endregion

		#region PrivateFields

		private readonly SceneTopLevel scene = new SceneTopLevel("Shadow casters top level");
		private ComputeShader trace;
		private ComputeShader filter;

		// This frame's rays and the surface of each pixel (for the filter), and the filtered mask (soft shadows).
		private GpuTexture traced;
		private GpuTexture meta;
		private GpuTexture mask;
		private int width;
		private int height;
		private int layers;
		private int localLayers = -1;
		private int directionalLayer = -2;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			Lighting.RayShadows = default;
			bool tracing = Lighting.RayTracedLocalShadows || Lighting.RayTracedDirectionalShadows;
			if (!tracing)
			{
				Release();
				return;
			}

			int slots = Lighting.RayTracedLocalShadows ? LightBuffer.ShadowSlotCount : 0;
			int directional = 0;
			if (Lighting.RayTracedDirectionalShadows)
			{
				for (int i = 0; i < DirectionalLightBuffer.Shining.Count; i++)
				{
					if (DirectionalLightBuffer.CastsShadows(i))
					{
						directional |= 1 << i;
					}
				}
			}

			if ((slots == 0 && directional == 0) || !context.DepthPrepassDone || context.NormalRoughness == null)
			{
				return;
			}

			trace ??= new ComputeShader("Engine/RayTracedShadowTrace.shader", new[] { ShaderProgram.RayQueryDefine });
			filter ??= new ComputeShader("Engine/RayTracedShadowFilter.shader");

			localLayers = (slots + 3) / 4;
			directionalLayer = directional != 0 ? localLayers : -1;
			EnsureTargets(context.Width, context.Height, localLayers + (directionalLayer >= 0 ? 1 : 0));
			RayTracingSettings settings = Lighting.RayTracing;
			int softRays = settings.SoftShadows ? Math.Clamp(settings.SoftShadowRays, 1, RayTracingSettings.MaxSoftShadowRays) : 0;
			GpuProfiler profiler = context.Renderer.Profiler;
			CommandList commands = Gfx.Commands;

			// ---- 1. the shadow casters as drawn this frame
			profiler.Split("RayTracedShadows.scene");
			BuildScene(context);
			if (scene.TopLevel == null)
			{
				return;
			}

			// ---- 2. rays per pixel and traced light
			profiler.Split("RayTracedShadows.trace");
			ShaderProgram program = trace.Program;
			trace.Use();
			LightBuffer.Bind();
			DirectionalLightBuffer.EnsureBound();
			commands.SetAccelerationStructure(SceneBinding, scene.TopLevel);
			program.SetTexture("SceneDepth", context.SceneDepth);
			program.SetTexture("NormalRoughness", context.NormalRoughness);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("cameraForward", context.CameraForward);
			program.SetInt2("screenSize", width, height);
			program.SetInt("localLayers", localLayers);
			program.SetInt("directionalLayer", directionalLayer);
			program.SetInt("directionalLights", directional);
			program.SetVector4("directionalTanRadius", DirectionalTanRadius());
			program.SetInt("softRays", softRays);
			program.SetFloat("rayLength", DirectionalRayLength);
			commands.SetImage(0, traced, 0, -1, ImageAccess.Write);
			commands.SetImage(1, meta, 0, 0, ImageAccess.Write);
			trace.DispatchThreads(width, height, 1, 8, 8, 1);

			// ---- 3. soft shadows averaged over the surface's pixels (hard ones are exact as traced)
			GpuTexture published = traced;
			if (softRays > 0)
			{
				profiler.Split("RayTracedShadows.filter");
				ShaderProgram filterProgram = filter.Program;
				filter.Use();
				filterProgram.SetTexture("TracedMask", traced);
				filterProgram.SetTexture("Meta", meta);
				filterProgram.SetInt2("screenSize", width, height);
				filterProgram.SetInt("layers", layers);
				filterProgram.SetInt("radius", FilterRadius);
				commands.SetImage(0, mask, 0, -1, ImageAccess.Write);
				filter.DispatchThreads(width, height, 1, 8, 8, 1);
				published = mask;
			}

			Lighting.RayShadows = new Lighting.RayShadowBinding
			{
				Mask = published,
				LocalSlots = slots,
				DirectionalLights = directional,
				DirectionalLayer = Math.Max(directionalLayer, 0),
				TopLevel = scene.TopLevel
			};
			Lighting.RayShadowBinding.EverPublished = true;
		}

		public override void ReleaseSceneReferences()
		{
			// The top level names the old scene's meshes' structures only; it is built again from the new renderers.
			scene.Delete();
		}

		public override void Dispose()
		{
			Release();
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     The view's shadow casters as instances of their meshes' bottom levels, placed as drawn: every renderer that
		///     casts shadows and whose surface is its mesh (not an impostor's card). A mesh whose bottom level waits for a
		///     later frame's build is left out until then.
		/// </summary>
		private void BuildScene(RenderContext context)
		{
			IReadOnlyList<MeshRenderer> renderers = context.Renderers;
			for (int i = 0; i < renderers.Count; i++)
			{
				MeshRenderer renderer = renderers[i];
				if (Casts(renderer))
				{
					MeshAccelerationStructures.Get(renderer.Mesh);
				}
			}

			MeshAccelerationStructures.BuildPending();
			scene.Begin();
			for (int i = 0; i < renderers.Count; i++)
			{
				MeshRenderer renderer = renderers[i];
				if (!Casts(renderer))
				{
					continue;
				}

				GpuAccelerationStructure bottom = MeshAccelerationStructures.GetBuilt(renderer.Mesh);
				if (bottom != null)
				{
					scene.Add(bottom, renderer.transform.LocalToWorld, 0);
				}
			}

			scene.Build();
		}

		// A renderer the shadow rays meet: it casts shadows, has triangles, and its opaque materials draw its mesh as it is.
		private static bool Casts(MeshRenderer renderer)
		{
			if (!renderer.CastShadows || renderer.Mesh == null || renderer.Mesh.TriangleCount == 0 || renderer.IsDestroyed || !renderer.isActiveAndEnabled)
			{
				return false;
			}

			bool any = false;
			for (int s = 0; s < renderer.SubMeshCount; s++)
			{
				Material material = renderer.GetMaterial(s);
				if (material == null || material.IsTransparent || !material.RenderState.CastShadows)
				{
					continue;
				}

				if (material.RenderState.CustomGeometryPrepass)
				{
					return false;
				}

				any = true;
			}

			return any;
		}

		/// <summary>Per directional light the tangent of its angular radius.</summary>
		private static Vector4 DirectionalTanRadius()
		{
			Vector4 tan = Vector4.Zero;
			for (int i = 0; i < DirectionalLight.MaxLights && i < DirectionalLightBuffer.Shining.Count; i++)
			{
				float diameter = Math.Clamp(DirectionalLightBuffer.Shining[i].AngularDiameter, 0f, 10f);
				tan[i] = MathF.Tan(MathHelper.DegreesToRadians(diameter * 0.5f));
			}

			return tan;
		}

		private void EnsureTargets(int newWidth, int newHeight, int newLayers)
		{
			if (newWidth == width && newHeight == height && newLayers == layers && mask != null)
			{
				return;
			}

			DeleteTargets();
			width = newWidth;
			height = newHeight;
			layers = newLayers;
			GraphicsDevice device = Gfx.Device;
			const TextureUsage written = TextureUsage.Sampled | TextureUsage.Storage;
			// The rays' result in RGBA8 and the surface of each pixel in one 32-bit word (Engine/HistoryMeta.glsl).
			traced = device.CreateTexture(TextureDescriptor.Texture2DArray(TextureFormat.RGBA8, width, height, layers, 1, written, "Traced shadows' rays"));
			traced.Sampler = Samplers.PointClamp;
			meta = device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.R32UI, width, height, 1, written, "Traced shadows' surfaces"));
			meta.Sampler = Samplers.PointClamp;
			mask = device.CreateTexture(TextureDescriptor.Texture2DArray(TextureFormat.RGBA8, width, height, layers, 1, written, "Traced shadows"));
			mask.Sampler = Samplers.PointClamp;

			// Lit from the start: the trace leaves the pixels no traced light reaches as they were.
			CommandList commands = Gfx.Commands;
			ClearValue lit = new ClearValue(1f, 1f, 1f, 1f);
			commands.ClearTexture(traced, 0, lit);
			commands.ClearTexture(mask, 0, lit);
			long bytes = (long)width * height * (layers * 4 * 2 + 4);
			Console.WriteLine($"[RayTracing] Traced shadows' mask {width}x{height}, {layers} layer{(layers == 1 ? "" : "s")} (~{bytes / 1048576.0:0} MB with the rays and surfaces)");
		}

		private void DeleteTargets()
		{
			traced?.Dispose();
			traced = null;
			meta?.Dispose();
			meta = null;
			mask?.Dispose();
			mask = null;
			width = 0;
			height = 0;
			layers = 0;
		}

		private void Release()
		{
			if (trace == null && mask == null && scene.Count == 0)
			{
				return;
			}

			trace?.Delete();
			trace = null;
			filter?.Delete();
			filter = null;
			DeleteTargets();
			scene.Delete();
			localLayers = -1;
			directionalLayer = -2;
		}

		#endregion
	}
}
