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
		public RenderTexture Motion => motion;

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

			gbuffer = new Framebuffer(renderer.RenderWidth, renderer.RenderHeight);
			normalRoughness = gbuffer.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
			motion = gbuffer.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
			gbuffer.AttachSharedDepth(renderer.SceneDepth);
			gbuffer.Validate();
		}

		public override void Resize(int width, int height)
		{
			// The shared depth is resized by the renderer; only the normal target is ours.
			gbuffer.Resize(width, height);
		}

		public override void Execute(RenderContext context)
		{
			if (!context.Settings.DepthPrepass)
			{
				context.DepthPrepassDone = false;
				context.NormalRoughness = null;
				context.Motion = null;
				return;
			}

			GpuScene gpu = context.Renderer.GpuScene;
			bool gpuDriven = gpu.Active && instancedProgram != null;
			if (gpuDriven)
			{
				gpu.EnsureCameraCulled(context.ViewProjection, context.CullingMask);
			}

			// Depth was already cleared with the scene target (shared texture); clear only our color (no motion, no normal).
			gbuffer.Bind();
			GL.ClearColor(0f, 0f, 0f, 0f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(true);

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
						GLState.SetCull(true, TriangleFace.Back);
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
							GLState.SetCull(true, TriangleFace.Back);
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

			context.DepthPrepassDone = true;
			context.NormalRoughness = normalRoughness;
			context.Motion = motion;
		}

		public override void Dispose()
		{
			gbuffer?.Delete();
			gbuffer = null;
			// Deletes the instanced variant and the map variants too.
			program?.Delete();
			program = null;
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

			GLState.SetCull(true, TriangleFace.Back);
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
