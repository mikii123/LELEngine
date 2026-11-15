using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

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

			shadowTarget.Bind();
			GLState.SetDepth(true, true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			GL.Clear(ClearBufferMask.DepthBufferBit);

			for (int i = 0; i < tiles; i++)
			{
				SetTileViewport(i);
				if (gpuDriven)
				{
					// The culling dispatch binds its own program: the depth program is bound after it. The casters are
					// the lighting camera's layers (the tables hold every camera's renderers).
					gpu.CullShadows(views[i] * projections[i], context.CullingMask);
				}

				program.Use();
				program.SetMatrix4("viewMatrix", views[i]);
				program.SetMatrix4("projectionMatrix", projections[i]);

				if (gpuDriven)
				{
					gpu.DrawShadowCasters();
				}
				else
				{
					foreach (MeshRenderer renderer in context.Renderers)
					{
						if (renderer.CastShadows)
						{
							renderer.RenderShadowCastersWith(context.DepthOnlyProgram);
						}
					}
				}
			}

			// Transparent casters: what they let through, in the map's transmittance attachment (cleared by the first tile).
			// Whether the frame has any is asked once, not per tile.
			RenderTexture transmittance = null;
			if (TransparentShadows.HasCasters(context))
			{
				for (int i = 0; i < tiles; i++)
				{
					SetTileViewport(i);
					transmittance = TransparentShadows.DrawCasters(context, shadowTarget, views[i], projections[i], i == 0) ?? transmittance;
				}
			}

			// Particle outputs that cast shadows (alpha tested, depth only).
			if (VFX.VfxRuntime.Instances.Count > 0)
			{
				GLState.SetColorWrite(false);
				for (int i = 0; i < tiles; i++)
				{
					SetTileViewport(i);
					VFX.VfxRuntime.DrawShadowCasters(context, views[i], projections[i]);
				}
			}

			GLState.SetColorWrite(true);

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
			RenderTexture depth = shadowTarget.AddDepthAttachment(RenderTextureFormat.Depth32F, true, TextureWrapMode.ClampToBorder);
			depth.SetBorderColor(1f, 1f, 1f, 1f);
			depth.SetShadowCompare(true);
			shadowTarget.Validate();
		}

		private void SetTileViewport(int tile)
		{
			Vector3i viewport = viewports[tile];
			GL.Viewport(viewport.X, viewport.Y, viewport.Z, viewport.Z);
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
