using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The shaders' list of directional lights (Engine/DirectionalLights.glsl): the <see cref="DirectionalLight" />s of
	///     the loaded scenes that shine this frame, at most <see cref="DirectionalLight.MaxLights" />: the main light
	///     first (<see cref="SceneManager.MainLight" />, when it shines), then the others, the brightest first. A uniform
	///     buffer bound to <see cref="Binding" /> for every program (lit materials, particles, the GI's injection and card
	///     lighting): filled and uploaded once a frame (<see cref="Lighting.BeginFrame" />), so a program costs no uniform
	///     upload per light. It also says which of the lights cast shadows this frame (the project's budget,
	///     <see cref="ShadowSettings.ShadowedLights" />): the same lights in the camera's shadow maps and the GI's.
	/// </summary>
	public static class DirectionalLightBuffer
	{
		#region PublicFields

		/// <summary>The uniform buffer binding of the list.</summary>
		public const int Binding = 6;

		/// <summary>Flags of a light in the list (direction.w in the shaders).</summary>
		public const int FlagGI = 1;

		public const int FlagVolumes = 2;
		public const int FlagShadows = 4;

		#endregion

		#region PrivateFields

		private const int Vec4PerLight = 2;

		private static readonly float[] data = new float[4 + DirectionalLight.MaxLights * Vec4PerLight * 4];
		private static readonly List<DirectionalLight> shining = new List<DirectionalLight>();
		private static readonly List<DirectionalLight> candidates = new List<DirectionalLight>();
		private static readonly bool[] shadowed = new bool[DirectionalLight.MaxLights];
		private static GpuBuffer buffer;
		private static int giHash;
		private static bool uploaded;

		#endregion

		#region PublicProperties

		/// <summary>The lights in the list this frame, in its order (the shaders' indices).</summary>
		public static IReadOnlyList<DirectionalLight> Shining => shining;

		/// <summary>The first light of the list (what shaders written for one sun read as LDirectional), or null when none shines.</summary>
		public static DirectionalLight Main => shining.Count > 0 ? shining[0] : null;

		/// <summary>How many of the lights cast shadows this frame.</summary>
		public static int ShadowedCount { get; private set; }

		/// <summary>
		///     Goes up whenever what the directional lights give the GI changes: a light turns on or off, turns, changes its
		///     brightness, colour, share in the GI or whether it casts shadows. GI caches that rest while nothing changes
		///     (Lumen's) wake up for it.
		/// </summary>
		public static int GIVersion { get; private set; }

		#endregion

		#region PublicMethods

		/// <summary>The light's index in the list, or -1 when it does not shine this frame.</summary>
		public static int IndexOf(DirectionalLight light)
		{
			for (int i = 0; i < shining.Count; i++)
			{
				if (ReferenceEquals(shining[i], light))
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>
		///     Light <paramref name="index" /> of the list casts shadows this frame: it asks to
		///     (<see cref="DirectionalLight.CastShadows" />), shadows are on, and it is one of the first
		///     <see cref="ShadowSettings.ShadowedLights" /> of the list that ask.
		/// </summary>
		public static bool CastsShadows(int index)
		{
			return index >= 0 && index < shining.Count && shadowed[index];
		}

		/// <summary>Bit i set: light i of the list casts shadows this frame (<see cref="CastsShadows" />).</summary>
		public static int ShadowedMask
		{
			get
			{
				int mask = 0;
				for (int i = 0; i < shining.Count; i++)
				{
					if (shadowed[i])
					{
						mask |= 1 << i;
					}
				}

				return mask;
			}
		}

		/// <summary>
		///     Gathers the lights that shine, picks the ones that cast shadows, and uploads and binds the list. Once a
		///     frame, before anything draws.
		/// </summary>
		public static void Update()
		{
			SceneManager.GetDirectionalLights(candidates);
			shining.Clear();

			// The main light leads when it shines; the others follow, the brightest first (equal ones in the scenes' order).
			for (int i = 0; i < candidates.Count; i++)
			{
				DirectionalLight light = candidates[i];
				if (light == null || !light.Shines)
				{
					continue;
				}

				int at = shining.Count;
				if (i > 0)
				{
					float brightness = Brightness(light);
					int first = shining.Count > 0 && ReferenceEquals(shining[0], candidates[0]) ? 1 : 0;
					while (at > first && Brightness(shining[at - 1]) < brightness)
					{
						at--;
					}
				}

				shining.Insert(at, light);
			}

			// A scratch list: it keeps no light until the next frame.
			candidates.Clear();
			if (shining.Count > DirectionalLight.MaxLights)
			{
				shining.RemoveRange(DirectionalLight.MaxLights, shining.Count - DirectionalLight.MaxLights);
			}

			ShadowSettings shadows = Lighting.Shadows;
			int budget = shadows.Enabled ? Math.Clamp(shadows.ShadowedLights, 0, DirectionalLight.MaxLights) : 0;
			ShadowedCount = 0;
			Array.Clear(shadowed, 0, shadowed.Length);
			Array.Clear(data, 0, data.Length);
			data[0] = BitConverter.Int32BitsToSingle(shining.Count);
			var hash = new HashCode();
			hash.Add(shining.Count);
			for (int i = 0; i < shining.Count; i++)
			{
				DirectionalLight light = shining[i];
				if (light.CastShadows && ShadowedCount < budget)
				{
					shadowed[i] = true;
					ShadowedCount++;
				}

				Vector3 towards = light.Towards;
				Vector3 radiance = light.Radiance;
				float gi = light.AffectsGI ? MathF.Max(light.IndirectStrength, 0f) : 0f;
				int flags = (gi > 0f ? FlagGI : 0) | (light.AffectsVolumes ? FlagVolumes : 0) | (shadowed[i] ? FlagShadows : 0);
				int at = 4 + i * Vec4PerLight * 4;
				data[at + 0] = towards.X;
				data[at + 1] = towards.Y;
				data[at + 2] = towards.Z;
				data[at + 3] = flags;
				data[at + 4] = radiance.X;
				data[at + 5] = radiance.Y;
				data[at + 6] = radiance.Z;
				data[at + 7] = gi;

				// What the GI takes of the light: nothing of one that stays out of it.
				hash.Add(gi > 0f);
				if (gi > 0f)
				{
					hash.Add(towards);
					hash.Add(radiance * gi);
					hash.Add(shadowed[i]);
				}
			}

			int newHash = hash.ToHashCode();
			if (newHash != giHash)
			{
				giHash = newHash;
				GIVersion++;
			}

			buffer ??= CreateBuffer();
			buffer.SetSubData(0, data);
			uploaded = true;
			Gfx.Commands.SetUniformBuffer(Binding, buffer);
		}

		/// <summary>
		///     Binds the list, made empty first when no frame has filled it yet: a lit program drawn before the first frame
		///     (previews, warm-up) never reads an unbound block.
		/// </summary>
		public static void EnsureBound()
		{
			if (buffer == null || !uploaded)
			{
				buffer ??= CreateBuffer();
				buffer.SetData(new float[data.Length]);
				uploaded = true;
			}

			Gfx.Commands.SetUniformBuffer(Binding, buffer);
		}

		/// <summary>
		///     A light disabled or destroyed (<see cref="DirectionalLight.OnDisable" />, <see cref="DirectionalLight.OnDestroy" />):
		///     out of the frame's list at once. A static list holding such a light held its unloaded scene, and the scripts of
		///     a reload, until another frame rendered.
		/// </summary>
		internal static void Forget(DirectionalLight light)
		{
			int index = IndexOf(light);
			if (index < 0)
			{
				return;
			}

			shining.RemoveAt(index);
			if (shadowed[index])
			{
				ShadowedCount--;
			}

			for (int i = index; i < shadowed.Length - 1; i++)
			{
				shadowed[i] = shadowed[i + 1];
			}

			shadowed[shadowed.Length - 1] = false;
		}

		#endregion

		#region PrivateMethods

		// The list's buffer, with room for every light (its contents come with the first upload).
		private static GpuBuffer CreateBuffer()
		{
			return Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Uniform, data.Length * sizeof(float), true, "Directional lights"));
		}

		// What the lights are ordered by: the luminance of colour x strength.
		private static float Brightness(DirectionalLight light)
		{
			return (0.2126f * light.Color.R + 0.7152f * light.Color.G + 0.0722f * light.Color.B) * light.Strength;
		}

		#endregion
	}
}
