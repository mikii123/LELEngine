using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Renders the scene depth from the directional lights that cast shadows (<see cref="DirectionalLightBuffer.CastsShadows" />)
	///     into one atlas of cascaded shadow maps: for each light the view up to the shadow distance is split into slices
	///     (<see cref="ShadowSettings.Cascades" /> for the first light, <see cref="ShadowSettings.AdditionalLightCascades" />
	///     for the others), each rendered into its own tile, so the slices near the camera get small, sharp texels. A
	///     cascade's orthographic frustum encloses the bounding sphere of its slice (its size does not change as the camera
	///     turns) and is snapped to its texels (the shadows do not shimmer as the camera moves). The first light's tiles
	///     fill the map as before (one fills it, two sit side by side, four in 2 x 2); the other lights' maps are stacked
	///     beside it, so a scene with one shadowed light has the atlas it always had. Results are published through
	///     <see cref="Lighting.ShadowCascades" /> (uploaded as <see cref="Lighting.CameraShadows" />) and
	///     <see cref="Lighting.ShadowMap" />.
	///     GPU-driven: per tile the opaque casters are culled against its view on the GPU and drawn by one indirect
	///     call; the transparent casters follow into the map's transmittance attachment (<see cref="TransparentShadows" />,
	///     published as <see cref="Lighting.ShadowTransmittance" />).
	///     Casters whose shader draws its own shadow (render state <c>ShadowCaster Custom</c>: impostors, anything whose
	///     silhouette is not its mesh's) follow the plain ones with the shader's <see cref="ShaderPass.ShadowCaster" />
	///     variant and the material's values: a call per bucket GPU-driven, a draw per sub-mesh otherwise.
	/// </summary>
	public sealed class ShadowPass : RenderPass
	{
		#region PublicFields

		public override string Name => "Shadows";

		public override RenderPassScope Scope => RenderPassScope.Frame;
		public Framebuffer ShadowTarget => shadowTarget;

		/// <summary>Tiles (cascades of every shadowed light) the last frame rendered.</summary>
		public int TileCount { get; private set; }

		#endregion

		#region PrivateFields

		private readonly Matrix4[] projections = new Matrix4[Lighting.ShadowCascadeData.MaxTiles];
		private readonly Matrix4[] views = new Matrix4[Lighting.ShadowCascadeData.MaxTiles];
		private readonly Vector3i[] viewports = new Vector3i[Lighting.ShadowCascadeData.MaxTiles];
		private Framebuffer shadowTarget;
		private ShaderProgram instancedDepth;

		// The tile being drawn: what it draws one by one is culled by its view.
		private readonly ViewCulling tileCulling = new ViewCulling();

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			instancedDepth = new ShaderProgram("Engine/DepthOnly.shader", new[] { ShaderProgram.InstancedDefine });
		}

		public override void Execute(RenderContext context)
		{
			ShadowSettings settings = Lighting.Shadows;
			Lighting.ShadowCascadeData cascades = Lighting.ShadowCascades;
			IReadOnlyList<DirectionalLight> lights = DirectionalLightBuffer.Shining;
			cascades.Clear();
			TileCount = 0;

			if (!settings.Enabled || context.Camera == null || DirectionalLightBuffer.ShadowedCount == 0)
			{
				Lighting.ShadowMap = null;
				Lighting.ShadowTransmittance = null;
				Lighting.CameraShadows.Upload(cascades);
				return;
			}

			// The first shadowed light's map as it always was; the other lights' maps in a column beside it.
			Layout(settings.Cascades, settings.MapSize, out int mainCount, out int mainColumns, out int mainRows, out int mainTile);
			Layout(settings.AdditionalLightCascades, settings.AdditionalLightMapSize, out int otherCount, out int otherColumns, out int otherRows, out int otherTile);
			int others = DirectionalLightBuffer.ShadowedCount - 1;
			int mainWidth = mainTile * mainColumns;
			int mainHeight = mainTile * mainRows;
			int otherHeight = otherTile * otherRows;
			int atlasWidth = mainWidth + (others > 0 ? otherTile * otherColumns : 0);
			int atlasHeight = Math.Max(mainHeight, others * otherHeight);
			EnsureTarget(atlasWidth, atlasHeight);

			int tiles = 0;
			int shadowed = 0;
			for (int i = 0; i < lights.Count; i++)
			{
				if (!DirectionalLightBuffer.CastsShadows(i))
				{
					continue;
				}

				bool main = shadowed == 0;
				int count = main ? mainCount : otherCount;
				int columns = main ? mainColumns : otherColumns;
				int tileSize = main ? mainTile : otherTile;
				var origin = new Vector2i(main ? 0 : mainWidth, main ? 0 : (shadowed - 1) * otherHeight);
				Matrix4 lightView = LightView(lights[i].Towards);
				cascades.LightFirst[i] = tiles;
				cascades.LightCount[i] = count;
				ComputeCascades(context, settings, lightView, count, columns, tileSize, origin, new Vector2(atlasWidth, atlasHeight), cascades, tiles);
				tiles += count;
				shadowed++;
			}

			cascades.TileCount = tiles;
			cascades.FadeCenter = context.CameraPosition;
			cascades.FadeDistance = Math.Max(settings.Distance, Math.Max(context.Camera.NearClip, 0.01f) + 0.1f);
			Lighting.CameraShadows.Upload(cascades);
			TileCount = tiles;

			GpuScene gpu = context.Renderer.GpuScene;
			bool gpuDriven = gpu.Active && instancedDepth != null && instancedDepth.IsLinked;
			ShaderProgram program = gpuDriven ? instancedDepth : context.DepthOnlyProgram;

			// Whether the frame has transparent casters is asked once, not per tile; their transmittance attachment has
			// to be there when the map's pass begins.
			bool transparentCasters = TransparentShadows.HasCasters(context);
			if (transparentCasters)
			{
				TransparentShadows.EnsureTransmittance(shadowTarget);
			}

			// What is drawn one by one with a program of its own.
			if (gpuDriven)
			{
				foreach (RendererPart part in gpu.OwnPassParts)
				{
					if (part.Renderer.CastShadows && part.Renderer.IsSubMeshShadowCaster(part.SubMesh))
					{
						PrepareOwnCaster(part.Renderer, part.SubMesh);
					}
				}
			}
			else
			{
				PrepareOwnCasters(context.Renderers);
			}

			CommandList commands = Gfx.Commands;
			ref RenderState state = ref commands.State;
			state.SetDepth(true, true);
			state.Culling = FaceCulling.Back;
			state.ColorMask = ColorWriteMask.None;

			bool culled = context.Settings.FrustumCulling;
			for (int i = 0; i < tiles; i++)
			{
				// What a tile draws one by one is culled by the tile's own view, as the GPU culls the tables' casters: a
				// caster the camera does not see still throws its shadow into the picture.
				Matrix4 tileViewProjection = views[i] * projections[i];
				tileCulling.Begin(tileViewProjection, culled);
				if (gpuDriven)
				{
					// The culling dispatch binds its own program: the depth program is bound after it. The casters are
					// the lighting camera's layers (the tables hold every camera's renderers).
					gpu.CullShadows(tileViewProjection, context.CullingMask);
				}

				// The map's pass: the first tile clears the depth; a culling dispatch ended the pass of the tile before.
				if (i == 0)
				{
					shadowTarget.Begin(LoadOperation.Load, default, LoadOperation.Clear, 1f);
				}

				ContinueTile(i);
				program.Use();
				program.SetMatrix4("viewMatrix", views[i]);
				program.SetMatrix4("projectionMatrix", projections[i]);

				if (gpuDriven)
				{
					gpu.DrawShadowCasters();
					DrawOwnPassCasters(gpu, context, i);
				}
				else
				{
					foreach (MeshRenderer renderer in context.Renderers)
					{
						if (renderer.CastShadows && tileCulling.IsVisible(renderer))
						{
							renderer.RenderShadowCastersWith(context.DepthOnlyProgram);
						}
					}

					// The casters whose shader draws them itself (ShadowCaster Custom), each with its own program.
					bool own = false;
					foreach (MeshRenderer renderer in context.Renderers)
					{
						if (renderer.CastShadows && tileCulling.IsVisible(renderer))
						{
							own |= renderer.RenderOwnShadowCasters(views[i], projections[i]);
						}
					}

					if (own)
					{
						commands.State.Culling = FaceCulling.Back;
					}
				}
			}

			// Transparent casters: what they let through, in the map's transmittance attachment (cleared by the first tile).
			RenderTexture transmittance = null;
			if (transparentCasters)
			{
				for (int i = 0; i < tiles; i++)
				{
					ContinueTile(i);
					tileCulling.Begin(views[i] * projections[i], culled);
					transmittance = TransparentShadows.DrawCasters(context, shadowTarget, views[i], projections[i], i == 0, tileCulling) ?? transmittance;
				}
			}

			// Particle outputs that cast shadows (alpha tested, depth only).
			if (VFX.VfxRuntime.Instances.Count > 0)
			{
				commands.State.ColorMask = ColorWriteMask.None;
				for (int i = 0; i < tiles; i++)
				{
					ContinueTile(i);
					VFX.VfxRuntime.DrawShadowCasters(context, views[i], projections[i]);
				}
			}

			commands.State.ColorMask = ColorWriteMask.All;
			// The map is sampled from here on.
			commands.EndRenderPass();

			Lighting.ShadowMap = shadowTarget.DepthAttachment;
			Lighting.ShadowTransmittance = transmittance;
		}

		public override void Dispose()
		{
			shadowTarget?.Delete();
			shadowTarget = null;
			instancedDepth?.Delete();
			instancedDepth = null;
			Lighting.ShadowCascades.Clear();
			Lighting.ShadowMap = null;
			Lighting.ShadowTransmittance = null;
		}

		#endregion

		#region PrivateMethods

		// The casters of the buckets whose shader draws a depth pass itself (the last of the tables: billboards standing
		// for a mesh, surfaces cut out by alpha), each bucket with its shader's shadow caster variant, or the engine's
		// depth program when the shader only draws the geometry prepass itself; then such sub-meshes outside the tables,
		// one by one (those in the tile's view). Leaves the engine's depth program unbound.
		private void DrawOwnPassCasters(GpuScene gpu, RenderContext context, int tile)
		{
			Matrix4 view = views[tile];
			Matrix4 projection = projections[tile];
			IReadOnlyList<OpaqueBucket> buckets = gpu.Buckets;
			bool cullChanged = false;
			for (int i = gpu.FirstOwnPassBucket; i < buckets.Count; i++)
			{
				OpaqueBucket bucket = buckets[i];
				ShaderProgram variant = bucket.CustomShadowCaster ? bucket.Program.GetPassVariant(ShaderPass.ShadowCaster) : null;
				bool own = variant != null && variant.IsLinked;
				if (!own)
				{
					variant = instancedDepth;
				}

				variant.Use();
				variant.SetMatrix4("viewMatrix", view);
				variant.SetMatrix4("projectionMatrix", projection);
				if (own)
				{
					if (bucket.Material != null)
					{
						bucket.Material.SetUniforms(variant);
					}
					else
					{
						Material.SetDefaultTextures(variant);
					}

					variant.RenderState.ApplyCull();
					cullChanged = true;
				}
				else if (cullChanged)
				{
					Gfx.Commands.State.Culling = FaceCulling.Back;
					cullChanged = false;
				}

				// A material's texture used for the first time is uploaded with its mip levels made, which ends the pass.
				ContinueTile(tile);
				gpu.DrawShadowBucket(i);
			}

			foreach (RendererPart part in gpu.OwnPassParts)
			{
				MeshRenderer renderer = part.Renderer;
				if (!renderer.CastShadows || !renderer.IsSubMeshShadowCaster(part.SubMesh) || (context.CullingMask & (1 << (renderer.gameObject.layer & 31))) == 0 || !tileCulling.IsVisible(renderer))
				{
					continue;
				}

				ContinueTile(tile);
				if (renderer.RenderSubMeshPass(part.SubMesh, ShaderPass.ShadowCaster, view, projection))
				{
					cullChanged = true;
					continue;
				}

				if (cullChanged)
				{
					Gfx.Commands.State.Culling = FaceCulling.Back;
					cullChanged = false;
				}

				ShaderProgram depth = context.DepthOnlyProgram;
				depth.Use();
				depth.SetMatrix4("viewMatrix", view);
				depth.SetMatrix4("projectionMatrix", projection);
				renderer.RenderSubMeshWith(depth, part.SubMesh);
			}

			if (cullChanged)
			{
				Gfx.Commands.State.Culling = FaceCulling.Back;
			}
		}

		// A light's map: one cascade fills it, two sit side by side (the map half as tall), four in 2 x 2.
		private static void Layout(ShadowCascadeCount cascades, int mapSize, out int count, out int columns, out int rows, out int tileSize)
		{
			count = cascades == ShadowCascadeCount.Four ? 4 : cascades == ShadowCascadeCount.Two ? 2 : 1;
			columns = count == 1 ? 1 : 2;
			rows = count == 4 ? 2 : 1;
			tileSize = Math.Max(16, count == 1 ? mapSize : mapSize / 2);
		}

		private void EnsureTarget(int width, int height)
		{
			if (shadowTarget != null && shadowTarget.Width == width && shadowTarget.Height == height)
			{
				return;
			}

			shadowTarget?.Delete();
			shadowTarget = new Framebuffer(width, height);
			RenderTexture depth = shadowTarget.AddDepthAttachment(RenderTextureFormat.Depth32F, true, SamplerAddress.ClampToBorder);
			depth.SetBorderColor(1f, 1f, 1f, 1f);
			depth.SetShadowCompare(true);
			shadowTarget.Validate();
		}

		/// <summary>
		///     Gives the casters that are drawn one by one with their shader's own shadow caster variant their material's
		///     values before a shadow map's pass begins. A texture used for the first time is uploaded then, with its mip
		///     levels made: inside the pass that would end it between the material's values and the caster's draw.
		/// </summary>
		internal static void PrepareOwnCasters(IReadOnlyList<MeshRenderer> casters)
		{
			foreach (MeshRenderer renderer in casters)
			{
				if (!renderer.CastShadows || renderer.Mesh == null || !renderer.HasOwnShadowCasters)
				{
					continue;
				}

				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					if (!renderer.IsSubMeshTransparent(i) && renderer.IsSubMeshShadowCaster(i))
					{
						PrepareOwnCaster(renderer, i);
					}
				}
			}
		}

		private static void PrepareOwnCaster(MeshRenderer renderer, int subMesh)
		{
			ShaderProgram variant = renderer.GetPassProgram(subMesh, ShaderPass.ShadowCaster);
			if (variant != null)
			{
				renderer.GetMaterial(subMesh)?.SetUniforms(variant);
			}
		}

		// Goes on drawing into a tile: the map's pass is begun again (keeping what it holds) when something that is not
		// a draw has ended it.
		private void ContinueTile(int tile)
		{
			if (!Gfx.Commands.InRenderPass)
			{
				shadowTarget.Begin();
			}

			SetTileViewport(tile);
		}

		private void SetTileViewport(int tile)
		{
			Vector3i viewport = viewports[tile];
			Gfx.Commands.SetViewport(viewport.X, viewport.Y, viewport.Z, viewport.Z);
		}

		/// <summary>View from a light (the unit vector towards it), origin at the world origin: the cascades are positioned by their projections.</summary>
		private static Matrix4 LightView(Vector3 towards)
		{
			Vector3 up = Math.Abs(towards.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
			return Matrix4.LookAt(towards, Vector3.Zero, up);
		}

		/// <summary>
		///     Splits the view between the near plane and the shadow distance for one light, fits every cascade's
		///     projection around its slice and writes the cascades as tiles <paramref name="firstTile" /> on: tile k of the
		///     light sits at column k % <paramref name="columns" /> of its map, whose bottom left texel is
		///     <paramref name="origin" />.
		/// </summary>
		private void ComputeCascades(RenderContext context, ShadowSettings settings, Matrix4 lightView, int count, int columns, int tileSize, Vector2i origin, Vector2 atlasSize, Lighting.ShadowCascadeData cascades,
			int firstTile)
		{
			Camera camera = context.Camera;

			// Half extents of the view per unit of depth, from the projection itself (whatever its field of view convention).
			Matrix4 cameraProjection = camera.ProjectionMatrix;
			float tanX = 1f / cameraProjection.M11;
			float tanY = 1f / cameraProjection.M22;
			float halfDiagonal2 = tanX * tanX + tanY * tanY;

			Vector3 forward = camera.transform.forward;
			forward = forward.LengthSquared > 1e-6f ? forward.Normalized() : -Vector3.UnitZ;

			float near = Math.Max(camera.NearClip, 0.01f);
			float distance = Math.Max(settings.Distance, near + 0.1f);

			Span<float> bounds = stackalloc float[Lighting.ShadowCascadeData.MaxCount + 1];
			bounds[0] = near;
			if (count == 2)
			{
				bounds[1] = distance * settings.TwoCascadeSplit;
			}
			else if (count == 4)
			{
				bounds[1] = distance * settings.FourCascadeSplits.X;
				bounds[2] = distance * settings.FourCascadeSplits.Y;
				bounds[3] = distance * settings.FourCascadeSplits.Z;
			}

			for (int i = 1; i < count; i++)
			{
				// Increasing and inside the distance, whatever the settings say.
				bounds[i] = Math.Max(Math.Min(bounds[i], distance - 0.01f * (count - i)), bounds[i - 1] + 0.01f);
			}

			bounds[count] = distance;

			float depthRange = settings.DepthRange;
			for (int i = 0; i < count; i++)
			{
				// Smallest sphere around the slice: its center on the view axis, as far from the near corners as from the
				// far ones, or on the far plane when the near corners are inside the far plane's circle anyway.
				float n = bounds[i];
				float f = bounds[i + 1];
				float near2 = n * n * halfDiagonal2;
				float far2 = f * f * halfDiagonal2;
				float z = 0.5f * (n + f) + (far2 - near2) / (2f * (f - n));
				float radius;
				if (z >= f)
				{
					z = f;
					radius = MathF.Sqrt(far2);
				}
				else
				{
					radius = MathF.Sqrt((f - z) * (f - z) + far2);
				}

				Vector3 center = context.CameraPosition + forward * z;

				// The radius only depends on the view's shape, so the texel size is constant: snapping the center to the
				// texel grid keeps a static scene on the same texels while the camera moves.
				float texel = 2f * radius / tileSize;
				Vector3 lightCenter = Vector3.TransformPosition(center, lightView);
				lightCenter.X = MathF.Floor(lightCenter.X / texel) * texel;
				lightCenter.Y = MathF.Floor(lightCenter.Y / texel) * texel;

				float depth = Math.Max(depthRange, radius);
				int tile = firstTile + i;
				projections[tile] = Matrix4.CreateOrthographicOffCenter(
					lightCenter.X - radius, lightCenter.X + radius,
					lightCenter.Y - radius, lightCenter.Y + radius,
					-lightCenter.Z - depth, -lightCenter.Z + depth);
				views[tile] = lightView;

				int x = origin.X + i % columns * tileSize;
				int y = origin.Y + i / columns * tileSize;
				viewports[tile] = new Vector3i(x, y, tileSize);
				var rect = new Vector4(x / atlasSize.X, y / atlasSize.Y, (x + tileSize) / atlasSize.X, (y + tileSize) / atlasSize.Y);
				cascades.Rects[tile] = rect;
				cascades.Matrices[tile] = Lighting.ShadowTextureMatrix(lightView, projections[tile], rect);

				// A receiver uses the first cascade it is well inside of: room for the filter, the snap and the normal offset.
				float inner = Math.Max(radius - texel * (5f + 2.5f * Math.Max(settings.NormalBiasTexels, 0f)), radius * 0.5f);
				cascades.Spheres[tile] = new Vector4(center, inner * inner);
			}
		}

		#endregion
	}
}
