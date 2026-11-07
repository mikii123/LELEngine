using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The shaders' light list (Engine/Lights.glsl): the <see cref="Light" />s that shine this frame, at most
	///     <see cref="Light.MaxLights" /> (with more, the ones whose range comes nearest to the camera), in a uniform buffer
	///     bound to <see cref="Binding" /> for every program: lit materials, the GI's injection and card lighting, the
	///     volumetric objects. Rebuilt every frame (<see cref="Renderer.RenderFrame" />); with no lights the list is empty,
	///     never unbound.
	/// </summary>
	public static class LightBuffer
	{
		#region PublicFields

		/// <summary>The uniform buffer binding of the light list.</summary>
		public const int Binding = 7;

		/// <summary>Flags of a light in the list (params.y in the shaders).</summary>
		public const int FlagGI = 1;

		public const int FlagVolumes = 2;
		public const int FlagGIShadows = 4;

		#endregion

		#region PrivateFields

		private const int Vec4PerLight = 6;

		private static readonly float[] data = new float[4 + Light.MaxLights * Vec4PerLight * 4];
		private static readonly List<Light> shining = new List<Light>();
		private static readonly List<(Light light, float score)> candidates = new List<(Light, float)>();
		private static Dictionary<Light, GIState> giLights = new Dictionary<Light, GIState>();
		private static Dictionary<Light, GIState> giSeen = new Dictionary<Light, GIState>();
		private static readonly List<(Vector3 min, Vector3 max)> giChanges = new List<(Vector3, Vector3)>();
		private static int buffer;

		#endregion

		#region PublicProperties

		/// <summary>The lights in the list this frame, in its order.</summary>
		public static IReadOnlyList<Light> Shining => shining;

		/// <summary>
		///     Goes up whenever what the lights give the GI changes: a light with <see cref="Light.AffectsGI" /> turns on or
		///     off, moves, or changes its brightness, colour, reach, cone or clouds' transmittance. GI caches that rest while
		///     nothing changes (Lumen's) wake up for it.
		/// </summary>
		public static int GIVersion { get; private set; }

		/// <summary>World boxes of the latest <see cref="GIVersion" /> change: the reach of every light that changed, where it was and where it is.</summary>
		public static IReadOnlyList<(Vector3 min, Vector3 max)> GIChanges => giChanges;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Gathers the lights that shine (enabled, bright, with a range), the ones reaching nearest to the camera's
		///     position first, lets the volumetric objects work out the clouds' transmittance around them, and uploads and
		///     binds the list.
		/// </summary>
		public static void Update(Camera camera)
		{
			Vector3 eye = camera != null ? camera.transform.position : Vector3.Zero;
			candidates.Clear();
			foreach (Light light in Light.Active)
			{
				if (light == null || light.IsDestroyed || !light.isActiveAndEnabled || light.Intensity <= 0f || light.Range <= 0f
					|| light.Color.R <= 0f && light.Color.G <= 0f && light.Color.B <= 0f)
				{
					continue;
				}

				// How far the camera is outside the light's reach (0 inside it): the nearest reach first.
				float outside = MathF.Max(0f, Vector3.Distance(light.transform.position, eye) - light.Range);
				candidates.Add((light, outside));
			}

			if (candidates.Count > Light.MaxLights)
			{
				candidates.Sort((a, b) => a.score.CompareTo(b.score));
			}

			shining.Clear();
			for (int i = 0; i < candidates.Count && shining.Count < Light.MaxLights; i++)
			{
				shining.Add(candidates[i].light);
			}

			Volumetrics.VolumetricRuntime.UpdateLightTransmittance(shining);

			Array.Clear(data, 0, data.Length);
			data[0] = BitConverter.Int32BitsToSingle(shining.Count);
			for (int i = 0; i < shining.Count; i++)
			{
				Write(shining[i], 4 + i * Vec4PerLight * 4);
			}

			TrackGIChanges();

			if (buffer == 0)
			{
				buffer = GL.GenBuffer();
				GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
				GL.BufferData(BufferTarget.UniformBuffer, data.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
			}

			GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
			// Only the lights in use (and the count) are uploaded.
			GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, (4 + shining.Count * Vec4PerLight * 4) * sizeof(float), data);
			GL.BindBuffer(BufferTarget.UniformBuffer, 0);
			Bind();
		}

		/// <summary>Binds the list to <see cref="Binding" /> (a program reads it there).</summary>
		public static void Bind()
		{
			if (buffer != 0)
			{
				GL.BindBufferBase(BufferRangeTarget.UniformBuffer, Binding, buffer);
			}
		}

		/// <summary>
		///     Binds the list, made empty first when no frame has filled it yet: a lit program drawn before the first frame
		///     (previews, warm-up) never reads an unbound block.
		/// </summary>
		public static void EnsureBound()
		{
			if (buffer == 0)
			{
				buffer = GL.GenBuffer();
				GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
				GL.BufferData(BufferTarget.UniformBuffer, data.Length * sizeof(float), new float[data.Length], BufferUsageHint.DynamicDraw);
				GL.BindBuffer(BufferTarget.UniformBuffer, 0);
			}

			GL.BindBufferBase(BufferRangeTarget.UniformBuffer, Binding, buffer);
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Compares what each GI light gives the GI (its entry in the list) with the last frame's: a light that is new,
		///     changed or gone adds the box of its reach (before and now) and moves <see cref="GIVersion" /> on. The boxes stay
		///     until the next change, so every renderer of the frame (the editor's views) reads the same ones.
		/// </summary>
		private static void TrackGIChanges()
		{
			bool changed = false;
			giSeen.Clear();
			for (int i = 0; i < shining.Count; i++)
			{
				Light light = shining[i];
				if (!light.AffectsGI || light.IndirectStrength <= 0f)
				{
					continue;
				}

				int at = 4 + i * Vec4PerLight * 4;
				var hash = new HashCode();
				for (int k = 0; k < Vec4PerLight * 4; k++)
				{
					hash.Add(data[at + k]);
				}

				var state = new GIState(hash.ToHashCode(), light.transform.position, light.Range);
				giSeen[light] = state;
				bool known = giLights.TryGetValue(light, out GIState last);
				if (known && last.Hash == state.Hash)
				{
					continue;
				}

				if (!changed)
				{
					giChanges.Clear();
					changed = true;
				}

				if (known)
				{
					giChanges.Add(last.Reach);
				}

				giChanges.Add(state.Reach);
			}

			foreach (KeyValuePair<Light, GIState> pair in giLights)
			{
				if (giSeen.ContainsKey(pair.Key))
				{
					continue;
				}

				if (!changed)
				{
					giChanges.Clear();
					changed = true;
				}

				giChanges.Add(pair.Value.Reach);
			}

			(giLights, giSeen) = (giSeen, giLights);
			if (changed)
			{
				GIVersion++;
			}
		}

		// One light: position + range, radiance + GI strength, spot direction + cos outer, spot scale / flags / source
		// radius, the clouds' transmittance towards the six directions.
		private static void Write(Light light, int at)
		{
			Vector3 position = light.transform.position;
			Vector3 radiance = light.Radiance;
			Vector3 forward = light.transform.forward;
			float cosOuter = -2f;
			float spotScale = 0f;
			if (light.Type == LightType.Spot)
			{
				float outer = MathHelper.DegreesToRadians(Math.Clamp(light.SpotAngle, 1f, 179f) * 0.5f);
				float inner = outer * Math.Clamp(light.InnerSpot, 0f, 0.999f);
				cosOuter = MathF.Cos(outer);
				spotScale = 1f / MathF.Max(MathF.Cos(inner) - cosOuter, 1e-4f);
			}

			int flags = (light.AffectsGI ? FlagGI : 0) | (light.AffectsVolumes ? FlagVolumes : 0) | (light.GIShadows ? FlagGIShadows : 0);
			float[] t = light.CloudTransmittance;
			bool clouds = light.VolumetricShadows;

			data[at + 0] = position.X;
			data[at + 1] = position.Y;
			data[at + 2] = position.Z;
			data[at + 3] = light.Range;
			data[at + 4] = radiance.X;
			data[at + 5] = radiance.Y;
			data[at + 6] = radiance.Z;
			data[at + 7] = light.AffectsGI ? MathF.Max(light.IndirectStrength, 0f) : 0f;
			data[at + 8] = forward.X;
			data[at + 9] = forward.Y;
			data[at + 10] = forward.Z;
			data[at + 11] = cosOuter;
			data[at + 12] = spotScale;
			data[at + 13] = flags;
			data[at + 14] = MathF.Max(light.SourceRadius, 1e-3f);
			data[at + 15] = 0f;
			for (int k = 0; k < 6; k++)
			{
				// Two vec4: (+x, -x, +y, 0) and (-y, +z, -z, 0).
				data[at + 16 + k + (k >= 3 ? 1 : 0)] = clouds ? t[k] : 1f;
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>A GI light as the last frame had it: its list entry's hash and where it reached.</summary>
		private readonly struct GIState
		{
			public GIState(int hash, Vector3 position, float range)
			{
				Hash = hash;
				Reach = (position - new Vector3(range), position + new Vector3(range));
			}

			public int Hash { get; }

			public (Vector3 min, Vector3 max) Reach { get; }
		}

		#endregion
	}
}
