using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The shaders' light list (Engine/Lights.glsl): every <see cref="Light" /> that shines this frame, however many, in
	///     a storage buffer bound to <see cref="ListBinding" /> for every program (lit materials, the GI's injection and card
	///     lighting, the volumetric objects), with its header (the count, and the clusters of the view being drawn) in a
	///     uniform buffer on <see cref="Binding" />. No light takes another's place: a lit pixel visits only the lights of its
	///     cluster (<see cref="Passes.LightClusterPass" />, their masks on <see cref="ClusterBinding" />), a GI work group
	///     those that reach it, so a light costs only where it reaches. Rebuilt every frame (<see cref="Renderer.RenderFrame" />)
	///     with the header without clusters (every light is visited until a camera's clusters are made); with no lights the
	///     list is empty, never unbound.
	/// </summary>
	public static class LightBuffer
	{
		#region PublicFields

		/// <summary>The uniform buffer binding of the list's header (Engine/Lights.glsl's LelLightBlock).</summary>
		public const int Binding = 7;

		/// <summary>The storage buffer binding of the list.</summary>
		public const int ListBinding = 16;

		/// <summary>The storage buffer binding of the clusters' masks of the view being drawn.</summary>
		public const int ClusterBinding = 17;

		/// <summary>Bytes of the header (std140; <see cref="WriteHeader" />).</summary>
		public const int HeaderBytes = 128;

		/// <summary>Flags of a light in the list (params.y in the shaders).</summary>
		public const int FlagGI = 1;

		public const int FlagVolumes = 2;
		public const int FlagGIShadows = 4;

		#endregion

		#region PrivateFields

		// Six vec4 a light (Engine/Lights.glsl's LelLight).
		private const int FloatsPerLight = 24;

		private static float[] data = new float[16 * FloatsPerLight];
		private static readonly float[] header = new float[HeaderBytes / sizeof(float)];
		private static readonly List<Light> shining = new List<Light>();
		private static Dictionary<Light, GIState> giLights = new Dictionary<Light, GIState>();
		private static Dictionary<Light, GIState> giSeen = new Dictionary<Light, GIState>();

		// Lights disabled or destroyed since the last frame (Forget): their reach, which the GI still has to take their
		// light out of, without the light itself.
		private static readonly List<GIState> giGone = new List<GIState>();

		private static readonly List<(Vector3 min, Vector3 max)> giChanges = new List<(Vector3, Vector3)>();

		// The traced shadows' slots: their lights, and this frame's candidates (distance from the camera to the light's
		// reach, the light's index in the list).
		private static readonly Light[] shadowSlots = new Light[RayTracingSettings.MaxShadowedLocalLights];
		private static readonly List<(float Distance, int Index)> shadowCandidates = new List<(float, int)>();
		private static readonly HashSet<Light> shadowChosen = new HashSet<Light>();

		// The list (storage) and the lights it has room for; the lights uploaded into it (Forget takes a light out of
		// shining at once, the buffer keeps it until the next update).
		private static GpuBuffer listBuffer;
		private static int listCapacity;
		private static int uploaded;

		// The header without clusters, a mask for programs drawn without clusters (their mask block needs a buffer), and
		// the header and masks bound now (the plain ones, or a camera's clusters').
		private static GpuBuffer plainHeader;
		private static GpuBuffer emptyMasks;
		private static GpuBuffer boundHeader;
		private static GpuBuffer boundMasks;

		#endregion

		#region PublicProperties

		/// <summary>The lights that shine this frame, in the list's order: every one, however many.</summary>
		public static IReadOnlyList<Light> Shining => shining;

		/// <summary>Lights in the list on the GPU this frame (what the shaders and the clusters index).</summary>
		public static int Count => uploaded;

		/// <summary>
		///     Goes up whenever what the lights give the GI changes: a light with <see cref="Light.AffectsGI" /> turns on or
		///     off, moves, or changes its brightness, colour, reach, cone or clouds' transmittance. GI caches that rest while
		///     nothing changes (Lumen's) wake up for it.
		/// </summary>
		public static int GIVersion { get; private set; }

		/// <summary>World boxes of the latest <see cref="GIVersion" /> change: the reach of every light that changed, where it was and where it is.</summary>
		public static IReadOnlyList<(Vector3 min, Vector3 max)> GIChanges => giChanges;

		/// <summary>
		///     Slots of the traced shadows this frame (<see cref="Lighting.RayTracedLocalShadows" />): the lights whose shadows
		///     <see cref="Passes.RayTracedShadowPass" /> traces, by slot (null: free). A light keeps its slot while it stays
		///     among the <see cref="RayTracingSettings.ShadowedLocalLights" /> lights with <see cref="Light.CastShadows" />
		///     nearest to the camera; its slot is in the list (params.w, -1: none).
		/// </summary>
		public static IReadOnlyList<Light> ShadowSlots => shadowSlots;

		/// <summary>Slots up to the highest one in use (0: no light's shadows are traced).</summary>
		public static int ShadowSlotCount { get; private set; }

		#endregion

		#region PublicMethods

		/// <summary>
		///     Gathers every light that shines (enabled, bright, with a range), lets the volumetric objects work out the
		///     clouds' transmittance around them, uploads the list and binds it with its header without clusters.
		/// </summary>
		public static void Update()
		{
			shining.Clear();
			foreach (Light light in Light.Active)
			{
				if (light == null || light.IsDestroyed || !light.isActiveAndEnabled || light.Intensity <= 0f || light.Range <= 0f
					|| light.Color.R <= 0f && light.Color.G <= 0f && light.Color.B <= 0f)
				{
					continue;
				}

				shining.Add(light);
			}

			Volumetrics.VolumetricRuntime.UpdateLightTransmittance(shining);

			int floats = shining.Count * FloatsPerLight;
			if (data.Length < floats)
			{
				data = new float[Math.Max(floats, data.Length * 2)];
			}

			for (int i = 0; i < shining.Count; i++)
			{
				Write(shining[i], i * FloatsPerLight);
			}

			TrackGIChanges();
			AssignShadowSlots();

			EnsureBuffers();
			if (shining.Count > listCapacity)
			{
				// Doubling: a list that keeps changing its size is reallocated rarely.
				listCapacity = Math.Max(shining.Count, listCapacity * 2);
				listBuffer.Allocate((long)listCapacity * FloatsPerLight * sizeof(float));
			}

			if (floats > 0)
			{
				listBuffer.SetSubData(0, data, floats);
			}

			uploaded = shining.Count;
			WriteHeader(plainHeader, uploaded, 0, 0, 0, 0, Matrix4.Identity, Vector4.Zero, Vector4.Zero);
			UseNoClusters();
		}

		/// <summary>
		///     Writes a header (Engine/Lights.glsl's LelLightBlock, std140): the count, and the clusters' grid (mask words of
		///     a cluster, 0: none; tiles; slices), the camera's view-projection, its depth row (a world point's view depth:
		///     dot((p, 1), depth)) and the slicing (a depth's slice: floor(log(depth) x x + y); z, w: the depths covered).
		/// </summary>
		internal static void WriteHeader(GpuBuffer buffer, int count, int words, int tilesX, int tilesY, int slices, Matrix4 viewProjection, Vector4 depth, Vector4 slicing)
		{
			Array.Clear(header, 0, header.Length);
			header[0] = BitConverter.Int32BitsToSingle(count);
			header[1] = BitConverter.Int32BitsToSingle(words);
			header[2] = BitConverter.Int32BitsToSingle(tilesX);
			header[3] = BitConverter.Int32BitsToSingle(tilesY);
			header[4] = BitConverter.Int32BitsToSingle(slices);
			// The engine's rows are the shader's columns, as SetMatrix4 uploads a matrix: the shader's matrix x a point is
			// the point x the engine's matrix.
			WriteVector(viewProjection.Row0, 8);
			WriteVector(viewProjection.Row1, 12);
			WriteVector(viewProjection.Row2, 16);
			WriteVector(viewProjection.Row3, 20);
			WriteVector(depth, 24);
			WriteVector(slicing, 28);
			buffer.SetSubData(0, header);
		}

		/// <summary>Binds the list with its header without clusters: every light is visited, until a camera's clusters are made.</summary>
		public static void UseNoClusters()
		{
			EnsureBuffers();
			boundHeader = plainHeader;
			boundMasks = emptyMasks;
			Bind();
		}

		/// <summary>Binds the list with a camera's clusters (<see cref="Passes.LightClusterPass" />): their header and masks.</summary>
		internal static void UseClusters(GpuBuffer clusterHeader, GpuBuffer clusterMasks)
		{
			EnsureBuffers();
			boundHeader = clusterHeader;
			boundMasks = clusterMasks;
			Bind();
		}

		/// <summary>Clusters about to be deleted: when they are the ones bound, the list goes back to its header without them.</summary>
		internal static void ForgetClusters(GpuBuffer clusterHeader, GpuBuffer clusterMasks)
		{
			if (listBuffer != null && (boundHeader == clusterHeader || boundMasks == clusterMasks))
			{
				UseNoClusters();
			}
		}

		/// <summary>Binds the list, and the header and masks in use (a program reads them there).</summary>
		public static void Bind()
		{
			if (listBuffer == null)
			{
				return;
			}

			CommandList commands = Gfx.Commands;
			commands.SetUniformBuffer(Binding, boundHeader);
			commands.SetStorageBuffer(ListBinding, listBuffer);
			commands.SetStorageBuffer(ClusterBinding, boundMasks);
		}

		/// <summary>
		///     A light disabled or destroyed (<see cref="Light.OnDisable" />, <see cref="Light.OnDestroy" />): out of the list
		///     and the GI's tracking at once, its reach kept for the next frame's change. The static lists held such a light,
		///     and through it its unloaded scene and the scripts of a reload, until two more frames rendered: none does while
		///     the editor is not drawing (the scripts' unload check reported them).
		/// </summary>
		internal static void Forget(Light light)
		{
			shining.Remove(light);
			if (giLights.Remove(light, out GIState state))
			{
				giGone.Add(state);
			}

			giSeen.Remove(light);
			for (int s = 0; s < shadowSlots.Length; s++)
			{
				if (ReferenceEquals(shadowSlots[s], light))
				{
					shadowSlots[s] = null;
				}
			}
		}

		/// <summary>
		///     Binds the list, made empty first when no frame has filled it yet: a lit program drawn before the first frame
		///     (previews, warm-up) never reads an unbound block.
		/// </summary>
		public static void EnsureBound()
		{
			EnsureBuffers();
			Bind();
		}

		#endregion

		#region PrivateMethods

		// The buffers, made empty on first use: the list (room for 16 lights), the header without clusters, the empty mask.
		private static void EnsureBuffers()
		{
			if (listBuffer != null)
			{
				return;
			}

			GraphicsDevice device = Gfx.Device;
			listCapacity = 16;
			listBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "Light list"));
			listBuffer.SetData(new float[listCapacity * FloatsPerLight]);
			emptyMasks = device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, false, "Light clusters (none)"));
			emptyMasks.SetData(new uint[4]);
			plainHeader = device.CreateBuffer(new BufferDescriptor(BufferUsage.Uniform, 0, true, "Light list header"));
			plainHeader.SetData(new float[HeaderBytes / sizeof(float)]);
			boundHeader = plainHeader;
			boundMasks = emptyMasks;
		}

		private static void WriteVector(Vector4 value, int at)
		{
			header[at] = value.X;
			header[at + 1] = value.Y;
			header[at + 2] = value.Z;
			header[at + 3] = value.W;
		}

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

				int at = i * FloatsPerLight;
				var hash = new HashCode();
				for (int k = 0; k < FloatsPerLight; k++)
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

			// Lights gone since the last frame (Forget took them out of giLights): their reach changes too.
			foreach (GIState gone in giGone)
			{
				if (!changed)
				{
					giChanges.Clear();
					changed = true;
				}

				giChanges.Add(gone.Reach);
			}

			giGone.Clear();
			(giLights, giSeen) = (giSeen, giLights);
			if (changed)
			{
				GIVersion++;
			}
		}

		/// <summary>
		///     Gives the traced shadows' slots to the lights that cast shadows, nearest to the camera first (a light whose range
		///     holds the camera: distance 0), at most <see cref="RayTracingSettings.ShadowedLocalLights" />, none while local
		///     lights' shadows are not traced. A light keeps its slot while it stays chosen. Writes every light's slot into the
		///     list (params.w, -1: none) after the GI's change tracking (a slot changes nothing the GI takes).
		/// </summary>
		private static void AssignShadowSlots()
		{
			int wanted = Lighting.RayTracedLocalShadows ? Math.Clamp(Lighting.RayTracing.ShadowedLocalLights, 1, shadowSlots.Length) : 0;
			shadowCandidates.Clear();
			shadowChosen.Clear();
			if (wanted > 0)
			{
				Camera camera = Camera.main;
				Vector3 eye = camera != null && !camera.IsDestroyed ? camera.transform.position : Vector3.Zero;
				for (int i = 0; i < shining.Count; i++)
				{
					Light light = shining[i];
					if (light.CastShadows)
					{
						shadowCandidates.Add((MathF.Max(0f, (light.transform.position - eye).Length - light.Range), i));
					}
				}

				shadowCandidates.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Index.CompareTo(b.Index));
				for (int k = 0; k < shadowCandidates.Count && k < wanted; k++)
				{
					shadowChosen.Add(shining[shadowCandidates[k].Index]);
				}
			}

			// Lights that stay keep their slots; the others leave theirs, the new ones take free ones.
			for (int s = 0; s < shadowSlots.Length; s++)
			{
				if (shadowSlots[s] != null && !shadowChosen.Contains(shadowSlots[s]))
				{
					shadowSlots[s] = null;
				}
			}

			foreach (Light light in shadowChosen)
			{
				if (Array.IndexOf(shadowSlots, light) >= 0)
				{
					continue;
				}

				int free = Array.IndexOf(shadowSlots, null);
				shadowSlots[free] = light;
			}

			for (int i = 0; i < shining.Count; i++)
			{
				data[i * FloatsPerLight + 15] = -1f;
			}

			int count = 0;
			for (int s = 0; s < shadowSlots.Length; s++)
			{
				Light light = shadowSlots[s];
				if (light == null)
				{
					continue;
				}

				int index = shining.IndexOf(light);
				data[index * FloatsPerLight + 15] = s;
				count = s + 1;
			}

			ShadowSlotCount = count;
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

			data[at + 19] = 0f;
			data[at + 23] = 0f;
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
