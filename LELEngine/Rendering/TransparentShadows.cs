using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Shadows of the transparent renderers (translucent shadow maps). Every shadow map target gets a second
	///     attachment, the transmittance map: rgb = the light that passes the transparent casters at that texel
	///     (1 where there are none; a tinted pane multiplies in mix(1, albedo, opacity), two panes multiply twice),
	///     a = the nearest transparent caster's depth (1 where there are none). The casters draw into it after the
	///     opaque casters, depth tested against them but not writing depth, with multiplicative colour blending and
	///     a minimum blend on the alpha. Receivers (Engine/Shadows.glsl) multiply their opaque shadow by the
	///     transmittance when they lie behind that depth: a 35% glass casts a 35% shadow of its own colour, smooth,
	///     in the camera shadow map and in the shadow maps that light the voxels and the surface cache alike.
	/// </summary>
	internal static class TransparentShadows
	{
		#region PublicMethods

		/// <summary>The frame has transparent renderers that cast shadows (the transmittance map is worth drawing).</summary>
		public static bool HasCasters(RenderContext context)
		{
			IReadOnlyList<MeshRenderer> casters = context.TransparentRenderers;
			if (casters == null)
			{
				return false;
			}

			foreach (MeshRenderer renderer in casters)
			{
				if (!renderer.CastShadows || renderer.Mesh == null)
				{
					continue;
				}

				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					if (renderer.IsSubMeshTransparent(i) && renderer.IsSubMeshShadowCaster(i))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		///     Gives a shadow map target its transmittance attachment when it has none yet. A pass draws into the
		///     attachments its target had when it began: called before the shadow map's pass begins, in a frame that
		///     has casters (<see cref="HasCasters" />).
		/// </summary>
		public static void EnsureTransmittance(Framebuffer target)
		{
			if (target.ColorAttachments.Count == 0)
			{
				// 32-bit: a holds a depth that receivers compare with their own (half floats round it by ~1/2048, more than
				// the shadow bias: a transparent receiver then shadows itself in bands along the light's depth).
				target.AddColorAttachment(RenderTextureFormat.RGBA32F);
				target.Validate();
			}
		}

		/// <summary>
		///     Draws the frame's transparent casters into the transmittance attachment of <paramref name="target" />
		///     (the shadow map whose pass is open, begun after <see cref="EnsureTransmittance" />, its opaque casters
		///     already drawn) within the current viewport (a cascade's tile): the attachment is cleared when
		///     <paramref name="clear" /> is set (the first tile). Returns it, or null when nothing was drawn (receivers
		///     then ignore it). Leaves the colour writes off, as the opaque casters had them.
		/// </summary>
		public static RenderTexture Draw(RenderContext context, Framebuffer target, Matrix4 lightView, Matrix4 lightProjection, bool clear = true)
		{
			return HasCasters(context) ? DrawCasters(context, target, lightView, lightProjection, clear) : null;
		}

		/// <summary>
		///     <see cref="Draw" /> for a frame known to have casters (<see cref="HasCasters" />, asked once by a pass that
		///     draws several tiles: the question walks every transparent renderer). <paramref name="culling" />: the
		///     light's view, begun by the caller (null: every caster draws); casters outside it are left out.
		///     <paramref name="tile" />: the tile's pixels in the map (the viewport the caller set). Given, the map is not
		///     cleared whole each frame: the first tile clears what the casters drew into the map the frame before, each
		///     tile the rectangle its casters' bounds cover, and the casters draw within that rectangle; the map holds the
		///     clear value everywhere else already, so it ends as a whole clear would leave it. A whole clear writes every
		///     texel on GPUs that do not clear colour by its compression (a 2048 x 2048 map of 32-bit floats: 64 MB).
		/// </summary>
		public static RenderTexture DrawCasters(RenderContext context, Framebuffer target, Matrix4 lightView, Matrix4 lightProjection, bool clear = true, ViewCulling culling = null,
			Box2i tile = default)
		{
			ShaderProgram program = context.TransparentShadowProgram;
			if (program == null || !program.IsLinked)
			{
				return null;
			}

			if (target.ColorAttachments.Count == 0)
			{
				// The open pass has no transmittance attachment to draw into.
				return null;
			}

			CommandList commands = Gfx.Commands;
			RenderTexture transmittance = target.ColorAttachments[0];
			commands.State.ColorMask = ColorWriteMask.All;
			bool tiled = tile.Size.X > 0 && tile.Size.Y > 0 && transmittance.Gpu != null && !(context.Renderer != null && context.Renderer.ABOff && Renderer.ABExperiment == "transmittance");
			Coverage cover = tiled ? coverage.GetValue(transmittance.Gpu, _ => new Coverage()) : null;
			if (clear)
			{
				if (cover == null || !cover.Cleared)
				{
					commands.ClearColor(0, NoCaster);
					if (cover != null)
					{
						cover.Cleared = true;
						cover.ClearedWhole = true;
						cover.Drawn.Clear();
					}
				}
				else
				{
					// Only where last frame's casters drew: the rest still holds the clear value.
					foreach (Box2i drawn in cover.Drawn)
					{
						commands.ClearColor(0, NoCaster, drawn.Min.X, drawn.Min.Y, drawn.Size.X, drawn.Size.Y);
					}

					cover.ClearedWhole = false;
					cover.Drawn.Clear();
				}
			}

			Box2i area = default;
			if (tiled)
			{
				if (!CasterArea(context, lightView * lightProjection, culling, tile, out area))
				{
					// No caster reaches into the tile: nothing of it changes.
					commands.State.ColorMask = ColorWriteMask.None;
					return transmittance;
				}

				if (!cover.ClearedWhole)
				{
					commands.ClearColor(0, NoCaster, area.Min.X, area.Min.Y, area.Size.X, area.Size.Y);
				}

				cover.Drawn.Add(area);
				commands.SetScissor(area.Min.X, area.Min.Y, area.Size.X, area.Size.Y);
			}

			// Behind an opaque caster the light is gone already: depth tested, not written (casters do not hide each other).
			// The colour multiplies what is there; the alpha keeps the smaller depth.
			ref RenderState state = ref commands.State;
			state.DepthWrite = false;
			state.DepthCompare = CompareFunction.Less;
			state.SetBlend(BlendFactor.Zero, BlendFactor.SrcColor, BlendFactor.One, BlendFactor.One);
			state.ColorOperation = BlendOperation.Add;
			state.AlphaOperation = BlendOperation.Min;

			program.Use();
			program.SetMatrix4("viewMatrix", lightView);
			program.SetMatrix4("projectionMatrix", lightProjection);
			foreach (MeshRenderer renderer in context.TransparentRenderers)
			{
				if (!renderer.CastShadows || renderer.Mesh == null)
				{
					continue;
				}

				// Asked once it is known to cast: most transparent renderers (effects) do not.
				bool asked = false;
				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					Material material = renderer.GetMaterial(i);
					if (material == null || !material.IsTransparent || !material.RenderState.CastShadows)
					{
						continue;
					}

					if (!asked)
					{
						asked = true;
						if (culling != null && !culling.IsVisible(renderer))
						{
							break;
						}
					}

					material.GetStandardParameters(out Vector4 color, out Vector4 unusedEmissive, out float unusedRoughness);
					program.SetVector4("shadowTint", color);
					// A double-sided pane shadows from both sides; a closed object multiplies its entry and exit.
					material.RenderState.ApplyCull();
					renderer.RenderSubMeshWith(program, i);
				}
			}

			ref RenderState after = ref commands.State;
			after.ColorOperation = BlendOperation.Add;
			after.AlphaOperation = BlendOperation.Add;
			after.Blend = false;
			after.DepthWrite = true;
			after.Culling = FaceCulling.Back;
			after.ColorMask = ColorWriteMask.None;
			if (tiled)
			{
				commands.DisableScissor();
			}

			return transmittance;
		}

		/// <summary>
		///     The pixels of a tile the frame's transparent casters in the light's view may draw into: their world bounds
		///     projected by the tile's view-projection, two pixels wider on every side; false when none reaches into the tile.
		/// </summary>
		private static bool CasterArea(RenderContext context, in Matrix4 viewProjection, ViewCulling culling, Box2i tile, out Box2i area)
		{
			float minX = float.MaxValue;
			float minY = float.MaxValue;
			float maxX = float.MinValue;
			float maxY = float.MinValue;
			bool whole = false;
			foreach (MeshRenderer renderer in context.TransparentRenderers)
			{
				if (!renderer.CastShadows || renderer.Mesh == null || !CastsTransparentShadow(renderer) || (culling != null && !culling.IsVisible(renderer)))
				{
					continue;
				}

				Mesh mesh = renderer.Mesh;
				Frustum.WorldBox(renderer.transform.LocalToWorld, mesh.BoundsMin, mesh.BoundsMax, out Vector3 center, out Vector3 extent);
				for (int corner = 0; corner < 8 && !whole; corner++)
				{
					Vector3 point = center + new Vector3((corner & 1) != 0 ? extent.X : -extent.X, (corner & 2) != 0 ? extent.Y : -extent.Y, (corner & 4) != 0 ? extent.Z : -extent.Z);
					Vector4 clip = new Vector4(point, 1f) * viewProjection;
					if (clip.W <= 1e-6f)
					{
						// Behind a perspective view's eye: anywhere in the tile.
						whole = true;
						break;
					}

					float x = clip.X / clip.W;
					float y = clip.Y / clip.W;
					minX = Math.Min(minX, x);
					minY = Math.Min(minY, y);
					maxX = Math.Max(maxX, x);
					maxY = Math.Max(maxY, y);
				}
			}

			if (whole)
			{
				area = tile;
				return true;
			}

			if (minX > maxX)
			{
				area = default;
				return false;
			}

			// Clip space to the tile's pixels (rows from the bottom, as the viewport), with a margin for the rasterizer.
			int x0 = Math.Max(tile.Min.X, (int)MathF.Floor(tile.Min.X + (Math.Max(minX, -2f) * 0.5f + 0.5f) * tile.Size.X) - 2);
			int y0 = Math.Max(tile.Min.Y, (int)MathF.Floor(tile.Min.Y + (Math.Max(minY, -2f) * 0.5f + 0.5f) * tile.Size.Y) - 2);
			int x1 = Math.Min(tile.Max.X, (int)MathF.Ceiling(tile.Min.X + (Math.Min(maxX, 2f) * 0.5f + 0.5f) * tile.Size.X) + 2);
			int y1 = Math.Min(tile.Max.Y, (int)MathF.Ceiling(tile.Min.Y + (Math.Min(maxY, 2f) * 0.5f + 0.5f) * tile.Size.Y) + 2);
			area = new Box2i(x0, y0, Math.Max(x0, x1), Math.Max(y0, y1));
			return x1 > x0 && y1 > y0;
		}

		/// <summary>Whether a sub-mesh of the renderer draws into the transmittance (a transparent material that casts shadows).</summary>
		private static bool CastsTransparentShadow(MeshRenderer renderer)
		{
			for (int i = 0; i < renderer.SubMeshCount; i++)
			{
				Material material = renderer.GetMaterial(i);
				if (material != null && material.IsTransparent && material.RenderState.CastShadows)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>What a transmittance map holds where no transparent caster drew: all light passes, no caster's depth.</summary>
		private static readonly ClearValue NoCaster = new ClearValue(1f, 1f, 1f, 1f);

		/// <summary>The rectangles each transmittance map's casters drew into, by the map's texture (a map made anew starts cleared whole).</summary>
		private static readonly ConditionalWeakTable<GpuTexture, Coverage> coverage = new ConditionalWeakTable<GpuTexture, Coverage>();

		/// <summary>
		///     What the casters of a transmittance map drew into since its last clear: the rectangles (pixels of the map)
		///     the first tile of the next frame clears; everywhere else the map holds the clear value.
		/// </summary>
		private sealed class Coverage
		{
			public readonly List<Box2i> Drawn = new List<Box2i>();

			/// <summary>The map was cleared whole once: from then on only what was drawn into it is cleared.</summary>
			public bool Cleared;

			/// <summary>The frame being drawn cleared the map whole: its tiles need no clear of their own.</summary>
			public bool ClearedWhole;
		}

		/// <summary>
		///     Gives a program the transmittance map for Engine/Shadows.glsl (null: receivers use the opaque shadow only,
		///     and the sampler reads no texture).
		/// </summary>
		public static void SetUniforms(ShaderProgram program, RenderTexture transmittance)
		{
			program.SetInt("shadowTransmittanceEnabled", transmittance != null ? 1 : 0);
			program.SetTexture("ShadowTransmittance", transmittance);
		}

		#endregion
	}
}
