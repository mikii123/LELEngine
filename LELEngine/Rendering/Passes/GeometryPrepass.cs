using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Thin G-buffer prepass: writes scene depth plus world normal and roughness for all opaque geometry, and each
	///     pixel's world-space motion since the previous frame (the motion vectors the GI's temporal filters reproject
	///     with; camera motion is not in them, the consumers apply their own previous view).
	///     The opaque pass then shades with depth test LEQUAL and no depth writes (each pixel shaded once),
	///     and screen-space passes (GI resolve) get geometry without re-rasterizing the scene.
	///     A material whose variant samples a normal map or a mask map (NORMAL_MAP, MASK_MAP: Engine/LitTextured.shader)
	///     is drawn with the G-buffer's variant of the same keywords and the material's maps, so the screen-space GI sees
	///     the normals and roughness the shading uses; everything else is drawn with the vertex normal and the material's
	///     roughness. Shares the depth texture with the scene target. GPU-driven: every visible instance without maps in
	///     one indirect call, then a call per bucket with maps.
	///     A shader whose surface is not its mesh's (render state <c>GeometryPrepass Custom</c>: impostors write the depth
	///     of the surface they stand for) is drawn with its own <see cref="ShaderPass.GeometryPrepass" /> variant and the
	///     material's values, after the others: a call per bucket GPU-driven, a draw per sub-mesh otherwise.
	/// </summary>
	public sealed class GeometryPrepass : RenderPass
	{
		#region PublicFields

		public override string Name => "GeometryPrepass";
		public RenderTexture NormalRoughness => normalRoughness;

		/// <summary>
		///     The motion vectors of the last camera the prepass drew for: a frame texture (<see cref="FrameGraph" />), which
		///     after the passes that read it may hold another texture of the frame, unless <see cref="KeepMotion" /> keeps it
		///     to the frame's end.
		/// </summary>
		public RenderTexture Motion => motion;

		/// <summary>The motion vectors are read after the frame (measurements: the frame's statistics, probes): they keep their memory to its end.</summary>
		public bool KeepMotion;

		/// <summary>The frame texture of the motion vectors, read by the passes that reproject their histories with them.</summary>
		public const string MotionTexture = "GBuffer.motion";

		/// <summary>G-buffer map bits of a material's variant (<see cref="MapsOf" />).</summary>
		public const int NormalMapBit = 1;

		public const int MaskMapBit = 2;

		/// <summary>The detail normal map (DETAIL_NORMAL_MAP), set only with <see cref="NormalMapBit" /> (it tilts the normal map's normal).</summary>
		public const int DetailNormalMapBit = 4;

		#endregion

		#region PrivateFields

		// The G-buffer variant's keywords per map bits, sorted.
		private static readonly string[][] MapKeywords =
		{
			null, new[] { "NORMAL_MAP" }, new[] { "MASK_MAP" }, new[] { "MASK_MAP", "NORMAL_MAP" },
			new[] { "DETAIL_NORMAL_MAP" }, new[] { "DETAIL_NORMAL_MAP", "NORMAL_MAP" }, new[] { "DETAIL_NORMAL_MAP", "MASK_MAP" },
			new[] { "DETAIL_NORMAL_MAP", "MASK_MAP", "NORMAL_MAP" }
		};

		private Framebuffer gbuffer;
		private RenderTexture normalRoughness;
		private RenderTexture motion;
		private ShaderProgram program;
		private ShaderProgram instancedProgram;

		/// <summary>Draws the G-buffer's values where no geometry is (Engine/GBufferBackground.shader).</summary>
		private ShaderProgram backgroundProgram;

		// The variants per map bits (index = bits), made on first use; the base program owns and deletes them.
		private readonly ShaderProgram[] mapPrograms = new ShaderProgram[8];
		private readonly ShaderProgram[] mapInstancedPrograms = new ShaderProgram[8];

		#endregion

		#region PublicMethods

		/// <summary>
		///     The G-buffer maps a material's variant samples: <see cref="NormalMapBit" /> for NORMAL_MAP, <see cref="MaskMapBit" />
		///     for MASK_MAP (the mask's smoothness), <see cref="DetailNormalMapBit" /> for DETAIL_NORMAL_MAP with NORMAL_MAP; 0
		///     for none or no material.
		/// </summary>
		public static int MapsOf(Material material)
		{
			int maps = 0;
			if (material == null)
			{
				return maps;
			}

			foreach (string keyword in material.Keywords)
			{
				if (keyword == "NORMAL_MAP")
				{
					maps |= NormalMapBit;
				}
				else if (keyword == "MASK_MAP")
				{
					maps |= MaskMapBit;
				}
				else if (keyword == "DETAIL_NORMAL_MAP")
				{
					maps |= DetailNormalMapBit;
				}
			}

			// The material shader draws the detail normal on top of the normal map only.
			return (maps & NormalMapBit) != 0 ? maps : maps & ~DetailNormalMapBit;
		}

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/GBuffer.shader");
			instancedProgram = program.GetInstancedVariant();
			backgroundProgram = new ShaderProgram("Engine/GBufferBackground.shader");

			normalRoughness = new RenderTexture(renderer.RenderWidth, renderer.RenderHeight, RenderTextureFormat.RGBA16F, false);
		}

		public override void Resize(int width, int height)
		{
			// The shared depth is resized by the renderer, the motion by the frame graph; only the normal target is ours.
			normalRoughness.Resize(width, height);
			gbuffer = null;
		}

		public override void Setup(FrameGraph frame, RenderContext context)
		{
			if (!context.Settings.DepthPrepass)
			{
				return;
			}

			// The depth and the normals are the frame's for everyone: never skipped.
			frame.SideEffects();
			frame.CreateTexture(MotionTexture, new FrameTextureDescription(context.Width, context.Height, RenderTextureFormat.RGBA16F, false));
			FrameStatistics statistics = context.Renderer.Statistics;
			if (KeepMotion || (statistics.Active && statistics.ReadSource == FrameStatistics.Source.Motion))
			{
				frame.Export(MotionTexture);
			}
		}

		public override void Execute(RenderContext context)
		{
			RenderTexture frameMotion = context.Frame != null && context.Frame.Has(MotionTexture) ? context.Frame.Texture(MotionTexture) : null;
			if (!context.Settings.DepthPrepass || frameMotion == null)
			{
				context.DepthPrepassDone = false;
				context.NormalRoughness = null;
				context.Motion = null;
				return;
			}

			if (gbuffer == null || motion != frameMotion)
			{
				// The normals, the frame's motion texture (the same while the frame graph's plan is), the scene's depth.
				motion = frameMotion;
				gbuffer = new Framebuffer(normalRoughness.Width, normalRoughness.Height);
				gbuffer.AttachColor(normalRoughness);
				gbuffer.AttachColor(motion);
				gbuffer.AttachSharedDepth(context.SceneDepth);
				gbuffer.Validate();
			}

			GpuScene gpu = context.Renderer.GpuScene;
			bool gpuDriven = gpu.Active && instancedProgram != null;
			if (gpuDriven)
			{
				gpu.EnsureCameraCulled(context.ViewProjection, context.CullingMask);
			}

			// Depth was already cleared with the scene target (shared texture). Where no geometry is, the G-buffer holds
			// zeros (no normal, no motion). On a device whose colour clears write every texel (FastColorClears false), for
			// the base camera, whose depth holds nothing but that clear yet, the colour is not cleared but the background
			// drawn after the geometry where the depth is still at the far plane: the same values without writing the
			// covered texels twice. Elsewhere (an overlay camera's depth may hold the cameras' before it) it is cleared.
			CommandList commands = Gfx.Commands;
			bool background = !Gfx.Capabilities.FastColorClears && context.IsBaseCamera && backgroundProgram != null && backgroundProgram.IsLinked
				&& !(context.Renderer.ABOff && Renderer.ABExperiment == "prepass");
			gbuffer.Begin(background ? LoadOperation.DontCare : LoadOperation.Clear, ClearValue.Zero);

			commands.State.SetDepth(true, true, CompareFunction.Less);
			commands.State.Culling = FaceCulling.Back;
			commands.State.ColorMask = ColorWriteMask.All;

			if (gpuDriven)
			{
				Use(instancedProgram, context);
				gpu.DrawCameraPlainGeometry();
				IReadOnlyList<OpaqueBucket> buckets = gpu.Buckets;
				for (int i = gpu.PlainBucketCount; i < buckets.Count; i++)
				{
					OpaqueBucket bucket = buckets[i];
					ShaderProgram variant = null;
					bool own = false;
					if (bucket.CustomGeometryPrepass)
					{
						// The materials' shader draws the prepass itself: its variant of the pass (instanced, as the bucket's).
						variant = bucket.Program.GetPassVariant(ShaderPass.GeometryPrepass);
						own = variant != null && variant.IsLinked;
					}

					if (!own)
					{
						// A variant that does not link still draws the bucket's depth, with the vertex normals.
						variant = bucket.GeometryMaps != 0 ? MapProgram(bucket.GeometryMaps, true) ?? instancedProgram : instancedProgram;
					}

					Use(variant, context);
					if (variant != instancedProgram)
					{
						if (bucket.Material != null)
						{
							bucket.Material.SetUniforms(variant);
						}
						else
						{
							Material.SetDefaultTextures(variant);
						}
					}

					if (own)
					{
						variant.RenderState.ApplyCull();
					}

					gpu.DrawCameraBucket(i);
					if (own)
					{
						commands.State.Culling = FaceCulling.Back;
					}
				}

				// Sub-meshes of such shaders outside the tables (a property block, no instanced variant), one by one.
				IReadOnlyList<RendererPart> ownParts = gpu.OwnPassParts;
				if (ownParts.Count > 0)
				{
					DrawOwnPassParts(ownParts, context);
				}
			}
			else
			{
				ObjectMotion objectMotion = context.Renderer.Motion;
				ShaderProgram current = null;
				foreach (MeshRenderer renderer in context.Renderers)
				{
					Matrix4 model = renderer.transform.LocalToWorld;
					// Its motion is recorded while the camera does not see it too: it comes back into view with the motion it has.
					Matrix4 previousModel = objectMotion.GetPrevious(renderer, model);
					if (!context.Culling.IsVisible(renderer, model))
					{
						continue;
					}

					for (int i = 0; i < renderer.SubMeshCount; i++)
					{
						Material material = renderer.GetMaterial(i);
						if (material != null && material.IsTransparent)
						{
							continue;
						}

						if (material != null && material.RenderState.CustomGeometryPrepass && renderer.RenderSubMeshPass(i, ShaderPass.GeometryPrepass, context.View, context.Projection, previousModel))
						{
							// Its shader drew it; the next sub-mesh binds the engine's program again.
							current = null;
							commands.State.Culling = FaceCulling.Back;
							continue;
						}

						int maps = MapsOf(material);
						ShaderProgram target = maps != 0 ? MapProgram(maps, false) ?? program : program;
						if (target != current)
						{
							Use(target, context);
							current = target;
						}

						if (target == program)
						{
							Material.ResetStandardUniforms(program);
							float roughness;
							if (material != null && material.TryGetFloat("Roughness", out roughness))
							{
								program.SetFloat("Roughness", roughness);
							}
						}
						else
						{
							material.SetUniforms(target);
						}

						target.SetMatrix4("previousModelMatrix", previousModel);
						renderer.RenderSubMeshWith(target, i);
					}
				}
			}

			if (background)
			{
				ref RenderState state = ref commands.State;
				state.SetDepth(true, false, CompareFunction.Equal);
				state.Culling = FaceCulling.None;
				state.Blend = false;
				backgroundProgram.Use();
				context.Fullscreen.Draw();
				commands.State.SetDepth(true, true, CompareFunction.Less);
				commands.State.Culling = FaceCulling.Back;
			}

			context.DepthPrepassDone = true;
			context.NormalRoughness = normalRoughness;
			context.Motion = motion;
		}

		public override void Dispose()
		{
			// The normals are ours; the motion is the frame graph's, the depth the scene's.
			normalRoughness?.Delete();
			normalRoughness = null;
			gbuffer = null;
			motion = null;
			// Deletes the instanced variant and the map variants too.
			program?.Delete();
			program = null;
			backgroundProgram?.Delete();
			backgroundProgram = null;
			instancedProgram = null;
			Array.Clear(mapPrograms);
			Array.Clear(mapInstancedPrograms);
		}

		#endregion

		#region PrivateMethods

		private static void Use(ShaderProgram target, RenderContext context)
		{
			target.Use();
			target.SetMatrix4("viewMatrix", context.View);
			target.SetMatrix4("projectionMatrix", context.Projection);
		}

		// The sub-meshes on this camera's layers and in its view whose shader draws the prepass itself and that are in no
		// table, each with its own program; those of them whose shader only casts its own shadows, with the G-buffer program.
		private void DrawOwnPassParts(IReadOnlyList<RendererPart> ownParts, RenderContext context)
		{
			ObjectMotion objectMotion = context.Renderer.Motion;
			foreach (RendererPart part in ownParts)
			{
				MeshRenderer renderer = part.Renderer;
				if ((context.CullingMask & (1 << (renderer.gameObject.layer & 31))) == 0)
				{
					continue;
				}

				Matrix4 model = renderer.transform.LocalToWorld;
				// Its motion is recorded while the camera does not see it too: it comes back into view with the motion it has.
				Matrix4 previousModel = objectMotion.GetPrevious(renderer, model);
				if (!context.Culling.IsVisible(renderer, model))
				{
					continue;
				}

				if (renderer.RenderSubMeshPass(part.SubMesh, ShaderPass.GeometryPrepass, context.View, context.Projection, previousModel))
				{
					continue;
				}

				Use(program, context);
				Material.ResetStandardUniforms(program);
				Material material = part.Material;
				if (material != null && material.TryGetFloat("Roughness", out float roughness))
				{
					program.SetFloat("Roughness", roughness);
				}

				program.SetMatrix4("previousModelMatrix", previousModel);
				renderer.RenderSubMeshWith(program, part.SubMesh);
			}

			Gfx.Commands.State.Culling = FaceCulling.Back;
		}

		// The variant for the map bits (instanced or per object); null when it does not link.
		private ShaderProgram MapProgram(int maps, bool instanced)
		{
			ShaderProgram variant = mapPrograms[maps] ??= program.GetVariant(MapKeywords[maps]);
			if (!instanced)
			{
				return variant.IsLinked ? variant : null;
			}

			return mapInstancedPrograms[maps] ??= variant.GetInstancedVariant();
		}

		#endregion
	}
}
