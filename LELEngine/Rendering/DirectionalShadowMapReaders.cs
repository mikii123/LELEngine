using System;
using System.Collections.Generic;
using LELEngine.Shaders;

namespace LELEngine.Rendering
{
	/// <summary>What the directional lights' shadow maps hold in a frame (<see cref="DirectionalShadowMapReaders.Decide" />).</summary>
	internal enum DirectionalShadowMaps
	{
		/// <summary>Every caster: something of the frame reads the maps for what the rays trace too.</summary>
		All,

		/// <summary>Only the casters the rays do not see (transparent ones, particles, casters that draw their own shadow).</summary>
		Untraced,

		/// <summary>Not drawn: the traced shadows are all there is.</summary>
		None
	}

	/// <summary>
	///     Which of the directional lights' shadow maps a frame needs while their shadows on opaque surfaces are traced
	///     (<see cref="Lighting.RayTracedDirectionalShadows" /> with <see cref="RayTracingSettings.SkipUnneededShadowMaps" />):
	///     the surfaces of the lighting camera's view read the traced mask, transparent surfaces with a ray query variant
	///     trace their own rays (<see cref="Lighting.TransparentShadowRays" />), and both take on top what only the maps
	///     hold: the shadows of the casters the rays do not see. Every caster goes into the maps when something reads them
	///     for the opaque casters' shadows too: clouds (volumetric objects), lit particle outputs, transparent renderers
	///     whose program does not trace, opaque ones whose program reads only the maps (a game's own shaders), and the
	///     other cameras of the stack (their pixels are not in the mask). Without either the maps are not drawn. Decided
	///     every frame before the shadow pass draws (a reader that appears gets its maps in the frame it appears); the
	///     console says when the answer changes, and why.
	/// </summary>
	internal static class DirectionalShadowMapReaders
	{
		#region PrivateFields

		private static readonly List<Camera> stack = new List<Camera>();

		// What was last said per renderer (an editor's scene and game views decide each for its own frame).
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Renderer, Said> said = new System.Runtime.CompilerServices.ConditionalWeakTable<Renderer, Said>();

		private sealed class Said
		{
			public DirectionalShadowMaps Decision = DirectionalShadowMaps.All;
			public string Reason;
		}

		#endregion

		#region PublicMethods

		/// <summary>The maps the frame of <paramref name="context" /> (the lighting camera's) needs; <see cref="DirectionalShadowMaps.All" /> unless the shadows are traced and the project skips what nobody reads.</summary>
		public static DirectionalShadowMaps Decide(RenderContext context)
		{
			DirectionalShadowMaps decision;
			string reason;
			if (!Lighting.RayTracedDirectionalShadows || !Lighting.RayTracing.SkipUnneededShadowMaps || context.Settings == null || !context.Settings.DepthPrepass)
			{
				decision = DirectionalShadowMaps.All;
				reason = null;
			}
			else if ((reason = FullReader(context)) != null)
			{
				decision = DirectionalShadowMaps.All;
			}
			else if ((reason = UntracedCasters(context)) != null)
			{
				decision = DirectionalShadowMaps.Untraced;
			}
			else
			{
				decision = DirectionalShadowMaps.None;
			}

			Report(context.Renderer, decision, reason);
			return decision;
		}

		#endregion

		#region PrivateMethods

		// What of the frame reads the maps for the opaque casters' shadows (null: nothing).
		private static string FullReader(RenderContext context)
		{
			if (!context.IsBaseCamera || (context.Camera != null && StackHasOthers(context.Camera)))
			{
				return "the other cameras of the stack";
			}

			if (Volumetrics.VolumetricRuntime.Objects.Count > 0)
			{
				return "clouds (volumetric objects)";
			}

			foreach (VFX.VfxInstance instance in VFX.VfxRuntime.Instances)
			{
				if (instance.ReadsShadowMaps)
				{
					return "lit particles";
				}
			}

			IReadOnlyList<MeshRenderer> transparent = context.TransparentRenderers;
			if (transparent != null)
			{
				foreach (MeshRenderer renderer in transparent)
				{
					if (renderer.ReceiveShadows && ReadsMapsOnly(renderer, true))
					{
						return "transparent surfaces whose shader does not trace";
					}
				}
			}

			IReadOnlyList<MeshRenderer> opaque = context.Renderers;
			if (opaque != null)
			{
				foreach (MeshRenderer renderer in opaque)
				{
					if (renderer.ReceiveShadows && ReadsMapsOnly(renderer, false))
					{
						return "surfaces whose shader reads only the maps";
					}
				}
			}

			return null;
		}

		// The casters of the frame the rays do not see (null: none).
		private static string UntracedCasters(RenderContext context)
		{
			if (TransparentShadows.HasCasters(context))
			{
				return "transparent casters";
			}

			foreach (VFX.VfxInstance instance in VFX.VfxRuntime.Instances)
			{
				if (instance.HasShadowCasters)
				{
					return "particle casters";
				}
			}

			GpuScene gpu = context.Renderer?.GpuScene;
			if (gpu != null && gpu.Active)
			{
				return gpu.FirstOwnPassBucket < gpu.Buckets.Count || gpu.OwnPassParts.Count > 0 ? "casters that draw their own shadow" : null;
			}

			IReadOnlyList<MeshRenderer> opaque = context.Renderers;
			if (opaque != null)
			{
				foreach (MeshRenderer renderer in opaque)
				{
					if (!renderer.CastShadows)
					{
						continue;
					}

					for (int s = 0; s < renderer.SubMeshCount; s++)
					{
						Material material = renderer.GetMaterial(s);
						if (material != null && !material.IsTransparent && material.RenderState.CastShadows && material.RenderState.CustomShadowCaster)
						{
							return "casters that draw their own shadow";
						}
					}
				}
			}

			return null;
		}

		// A sub-mesh of the renderer draws with a program that reads the maps and not the traced shadows.
		private static bool ReadsMapsOnly(MeshRenderer renderer, bool transparent)
		{
			for (int s = 0; s < renderer.SubMeshCount; s++)
			{
				Material material = renderer.GetMaterial(s);
				if (material == null || material.IsTransparent != transparent)
				{
					continue;
				}

				ShaderProgram program = material.Program;
				if (transparent && Lighting.TransparentShadowRays && program != null && program.RenderState.RayQueries)
				{
					// Its ray query variant traces this frame's shadows (Lighting.RayQueryProgram).
					program = program.GetRayQueryVariant() ?? program;
				}

				if (program != null && program.ReadsShadowMapsOnly)
				{
					return true;
				}
			}

			return false;
		}

		private static bool StackHasOthers(Camera camera)
		{
			camera.GetRenderStack(stack);
			bool others = stack.Count > 1;
			stack.Clear();
			return others;
		}

		private static void Report(Renderer renderer, DirectionalShadowMaps decision, string reason)
		{
			if (renderer == null)
			{
				return;
			}

			Said last = said.GetValue(renderer, _ => new Said());
			if (decision == last.Decision && string.Equals(reason, last.Reason, StringComparison.Ordinal))
			{
				return;
			}

			last.Decision = decision;
			last.Reason = reason;
			if (!Lighting.RayTracedDirectionalShadows)
			{
				return;
			}

			string what = decision switch
			{
				DirectionalShadowMaps.All => reason != null ? "every caster (read by " + reason + ")" : "every caster",
				DirectionalShadowMaps.Untraced => "only what the rays do not see (" + reason + ")",
				_ => "not drawn (the traced shadows are all there is)"
			};
			Console.WriteLine("[RayTracing] Directional shadow maps: " + what);
		}

		#endregion
	}
}
