using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>
	///     A volumetric object: a cloud (fog, smoke, a storm, a nebula) filling a shape (box, cylinder, sphere) in the
	///     object's local space, cut out of procedural noise. The engine makes the cloud on the GPU when the object
	///     appears and again when its shape or noise change, and works out how the sun, the sky and the lights inside light
	///     it whenever the sun turns against the object. A cloud made once is kept for the next object of the same settings
	///     (<see cref="VolumeCache" />: on the GPU across a scene's reload, in files the editor writes and builds ship, read on
	///     worker threads while the scene loads). Two presets draw it, each a component:
	///     <see cref="VolumetricObject" /> for clouds seen from outside (cheap: marched at half resolution with even steps)
	///     and <see cref="VolumetricObjectHQ" /> for clouds the camera also goes into (full resolution, fine steps near the
	///     camera, a temporal filter, the scene's shadows). It draws in the transparent pass, sorted with the other
	///     transparent objects at the centre of its bounds, and ends at the opaque scene. Moving, turning and scaling the
	///     object moves, turns and scales the same cloud; growing <see cref="Size" /> shows more of it (the noise is in
	///     local units).
	/// </summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.Rendering)]
	public abstract class VolumetricObjectBase : Behaviour, Serialization.ISerializationCallbackReceiver
	{
		#region PublicFields

		/// <summary>Most voxels a cloud may have (256^3, 16 MB of density); past it the voxels grow.</summary>
		public const int MaxVoxels = 256 * 256 * 256;

		/// <summary>Most voxels along one axis.</summary>
		public const int MaxVoxelsAlong = 512;

		/// <summary>Most lights inside (<see cref="Lights" />).</summary>
		public const int MaxLights = 8;

		[Header("Shape")]
		public VolumeShape Shape = VolumeShape.Box;

		[Tooltip("The shape's extents in local units: the box; the cylinder's diameters along X and Z and its height along Y; the sphere's diameters")]
		public Vector3 Size = new Vector3(100f, 40f, 100f);

		[Tooltip("How far in from the side the cloud fades out, as a share of the half size (the radius)")]
		[Range(0.01f, 1f)]
		public float SideSoftness = 0.2f;

		[Tooltip("How far in from the top and the bottom the cloud fades out, as a share of the height (box and cylinder)")]
		[Range(0.01f, 0.5f)]
		public float CapSoftness = 0.15f;

		[Tooltip("How far the top dips in places, as a share of the height: towers instead of a flat lid (box and cylinder)")]
		[Range(0f, 1f)]
		public float TopRoughness = 0.3f;

		[Tooltip("How far the bottom rises in places, as a share of the height (box and cylinder)")]
		[Range(0f, 1f)]
		public float BottomRoughness = 0.2f;

		[Tooltip("How far the side pulls in in places, as a share of the half size")]
		[Range(0f, 1f)]
		public float SideRoughness = 0.2f;

		[Header("Noise")]
		public VolumeNoise Noise = VolumeNoise.PerlinWorley;

		[Tooltip("Size of the large cloud shapes in local units. A bigger shape holds more of them; it does not stretch them")]
		public float NoiseSize = 20f;

		[Tooltip("Octaves of the noise (fewer are used when the voxels cannot hold the finer ones: the detail adds those)")]
		[Range(1f, 8f)]
		public int Octaves = 5;

		[Tooltip("How much each finer octave counts against the one before (0.5: the usual fractal)")]
		[Range(0.1f, 0.9f)]
		public float Roughness = 0.5f;

		[Tooltip("How much the noise is twisted by noise (swirls instead of round blobs)")]
		[Range(0f, 2f)]
		public float Warp = 0.6f;

		public int Seed = 1;

		[Tooltip("How much of the shape the cloud fills (0: nothing, 1: nearly all of it)")]
		[Range(0f, 1f)]
		public float Coverage = 0.6f;

		[Tooltip("How soon the cloud is dense past its edge: small values give crisp, defined clouds, large ones soft haze")]
		[Range(0.02f, 1f)]
		public float Softness = 0.2f;

		[Tooltip("Size of the clumps that decide which parts of the shape hold cloud at all, as a share of the noise size (0: no clumps, the cloud fills the shape evenly)")]
		[Range(0f, 10f)]
		public float ClumpSize = 2f;

		[Tooltip("How much of the shape the clumps fill (higher: more)")]
		[Range(-1f, 2f)]
		public float ClumpCoverage = 0.8f;

		[Range(0.05f, 1f)]
		public float ClumpSoftness = 0.35f;

		[Header("Density")]
		[Tooltip("How opaque the cloud is: its optical depth through one noise size of full cloud (1: light haze, 10: thick, 50: solid)")]
		[Range(0.1f, 200f)]
		public float Density = 20f;

		[Tooltip("How far the cloud shadows itself, as a share of the noise size (short: soft, glowing insides; long: dark cores)")]
		[Range(0.05f, 10f)]
		public float ShadowReach = 2.6f;

		[Header("Detail")]
		[Tooltip("How much the fine tiling detail eats into the cloud's edges (the relief the voxels are too coarse for)")]
		[Range(0f, 1f)]
		public float DetailStrength = 0.5f;

		[Tooltip("Size of one tile of the detail (four puffs across), as a share of the noise size")]
		[Range(0.05f, 3f)]
		public float DetailSize = 0.6f;

		[Tooltip("Densities below this count as the cloud's edge, where the detail eats in (wider: deeper billows)")]
		[Range(0.1f, 1f)]
		public float EdgeBand = 0.6f;

		[Tooltip("How much darker the pockets the detail eats are")]
		[Range(0f, 1f)]
		public float DetailShade = 0.35f;

		[Tooltip("How fast the detail drifts through the cloud (local units per second)")]
		public Vector3 DetailWind;

		[Header("Lighting")]
		[Tooltip("The directional light that lights the cloud (its colour, strength and direction, live); empty: the scene's main light. A disabled light lights only the cloud (it never becomes the scene's main light): a background's own sun")]
		public DirectionalLight Sun;

		[Range(0f, 10f)]
		public float SunStrength = 1f;

		[Tooltip("The scene's sun shadows fall into the cloud (ships shading a fog bank, a cloud layer below them). The shadow map is the scene's main light's: with a light of the cloud's own as its sun there are none")]
		public bool ReceiveShadows = true;

		[Tooltip("How dark those shadows are in the cloud: 1 blocks the sun fully, less lets some through (light scattered in from the lit cloud around)")]
		[Range(0f, 1f)]
		public float ShadowStrength = 1f;

		[Tooltip("How much of the sky (the environment's skybox, or the GI's sky colour) lights the cloud")]
		[Range(0f, 10f)]
		public float SkyStrength = 1f;

		[Tooltip("How much of the flat ambient light (the scene's, or the cloud's own with Own Ambient) lights the cloud")]
		[Range(0f, 10f)]
		public float AmbientStrength = 1f;

		[Tooltip("An ambient light of its own in place of the scene's flat ambient light: for a volume lit apart from the scene (a backdrop with its own sun), which changes of the scene's ambient light (a script lighting the ships with it) must not reach")]
		public bool OwnAmbient;

		[Tooltip("The cloud's own ambient light (linear), with Own Ambient on")]
		public Color4 AmbientColor = new Color4(0.05f, 0.05f, 0.06f, 1f);

		[Tooltip("How much of the light the cloud gives back, per colour")]
		public Color4 Albedo = new Color4(0.9f, 0.9f, 0.92f, 1f);

		[Tooltip("How much of the sun's light goes on forward: bright edges with the sun behind the cloud (0: even in every direction)")]
		[Range(-0.9f, 0.9f)]
		public float PhaseG = 0.55f;

		[Tooltip("Light scattered on through the cloud (softer, brighter insides; 0: single scattering only)")]
		[Range(0f, 2f)]
		public float MultipleScattering = 1f;

		[Tooltip("Light the cloud gives off itself, where it is dense")]
		public Color4 EmissionColor = new Color4(0f, 0f, 0f, 1f);

		[Range(0f, 10f)]
		public float EmissionStrength;

		[Range(0f, 10f)]
		public float Exposure = 1f;

		[Range(0f, 1f)]
		public float Opacity = 1f;

		[Header("Global illumination")]
		[Tooltip("The scene's GI lights the cloud: the light bounced off everything around it, from every side, dimmed by the cloud towards each side and scattered towards the eye by the phase (forward scattering shows what lies behind the cloud). It takes the place of the sky light (a cone leaving the GI's volume sees the sky). Off, or where the scene has no GI: the sky (Sky Strength)")]
		public bool ReceiveGI = true;

		[Tooltip("How much of the GI lights the cloud")]
		[Range(0f, 10f)]
		public float GIStrength = 1f;

		[Tooltip("Size of a cell of the cloud's GI, in voxels: the light around a cloud changes slowly, a coarse grid of it is enough (at most 64^3 cells)")]
		[Range(2, 32)]
		public int GICellSize = 8;

		[Tooltip("Frames a full update of the cloud's GI is spread over (1: all of it every frame; more: cheaper, slower to follow changes)")]
		[Range(1, 16)]
		public int GIUpdateFrames = 4;

		[Header("Lights")]
		[Tooltip("The scene's Light components that reach the shape (with Affects Volumes) light the cloud, through it: up to 8 lights with its own, the nearest first. Moving one works its light volume out again; intensity and colour are free")]
		public bool SceneLights = true;

		[Tooltip("Lights of its own inside the cloud (flashes, lamps, fires), lighting only the cloud. Moving one works the light volume out again; intensity and colour are free")]
		public List<VolumeLight> Lights = new List<VolumeLight>();

		[Tooltip("Light scattered far through the cloud around each light (its own and the scene's)")]
		[Range(0f, 2f)]
		public float LightGlow = 0.7f;

		[Header("Quality")]
		[Tooltip("Size of one voxel of the cloud in local units. It stays when the shape grows (more voxels, not bigger ones), up to 256^3 voxels")]
		public float VoxelSize = 1f;

		[Tooltip("Marching step through the cloud, in voxels")]
		[Range(0.25f, 4f)]
		public float StepSize = 1f;

		[Tooltip("Most steps a pixel's ray takes")]
		[Range(8f, 512f)]
		public int MaxSteps = 64;

		[Tooltip("Resolution the cloud is marched at (then upsampled, keeping silhouettes in front of it sharp)")]
		public VolumetricResolution Resolution = VolumetricResolution.Half;

		[Tooltip("Blend the frames: the march's sampling pattern changes every frame and the frames before (moved with the camera and the object) fill in, so the cloud is smooth even marched at a reduced resolution with few steps. Off: a fixed pattern, grainy at reduced resolutions")]
		public bool TemporalFilter = true;

		[Tooltip("How much of the frames before a settled frame keeps (higher: smoother, slower to follow fast changes such as flashes). Cloud that just came into view (from behind an object, past the screen's edge) starts afresh and blends its first frames evenly")]
		[Range(0f, 0.98f)]
		public float HistoryWeight = 0.9f;

		[Tooltip("Draw order against the other transparent objects (their material queues)")]
		public int RenderQueue = 3000;

		#endregion

		#region PrivateFields

		private VolumetricData data;
		private readonly List<VolumeLightSlot> hashSlots = new List<VolumeLightSlot>();
		private readonly List<(global::Light light, float distance)> sceneCandidates = new List<(global::Light, float)>();

		#endregion

		#region PublicProperties

		/// <summary>The cloud has been made and lit: it draws (and <see cref="DensityAt" /> answers).</summary>
		public bool Ready => data != null && data.Ready;

		/// <summary>
		///     Changes whenever another cloud is drawn (its settings changed, it was made again, read from its file or kept from
		///     an object of the same settings): scripts that placed things by the cloud's density (flash points in it) compare
		///     it to know when to place them again. 0 before the first cloud.
		/// </summary>
		public int CloudVersion => data != null && data.Ready ? data.DensityVersion : 0;

		/// <summary>Voxels of the cloud as the shape and voxel size give them (what the next build makes).</summary>
		public Vector3i Grid => GridFor(Size, VoxelSize, out _, out _);

		/// <summary>The GPU side of this object (made on its first frame, released when it is destroyed).</summary>
		internal VolumetricData Data => data ??= new VolumetricData(this);

		/// <summary>The preset: high quality (full resolution, temporal filter, fine steps near the camera).</summary>
		internal abstract bool HighQuality { get; }

		#endregion

		#region PublicMethods

		/// <summary>
		///     The voxels a shape of <paramref name="size" /> gets at <paramref name="voxelSize" /> local units each: the voxel
		///     size stays and a bigger shape gets more voxels, up to <see cref="MaxVoxels" /> and <see cref="MaxVoxelsAlong" />,
		///     past which they grow (<paramref name="voxel" /> is the size used, <paramref name="capped" /> says it grew).
		/// </summary>
		public static Vector3i GridFor(Vector3 size, float voxelSize, out float voxel, out bool capped)
		{
			size = new Vector3(MathF.Max(size.X, 1e-3f), MathF.Max(size.Y, 1e-3f), MathF.Max(size.Z, 1e-3f));
			voxel = MathF.Max(voxelSize, 1e-4f);
			capped = false;
			double total = (double)(size.X / voxel) * (size.Y / voxel) * (size.Z / voxel);
			if (total > MaxVoxels)
			{
				voxel *= (float)Math.Pow(total / MaxVoxels, 1.0 / 3.0);
				capped = true;
			}

			float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
			if (longest / voxel > MaxVoxelsAlong)
			{
				voxel = longest / MaxVoxelsAlong;
				capped = true;
			}

			return new Vector3i(
				Math.Max(4, (int)MathF.Ceiling(size.X / voxel - 1e-3f)),
				Math.Max(4, (int)MathF.Ceiling(size.Y / voxel - 1e-3f)),
				Math.Max(4, (int)MathF.Ceiling(size.Z / voxel - 1e-3f)));
		}

		/// <summary>
		///     The cloud's density (0..1, without the detail) at a world position: 0 outside the shape or before the cloud is
		///     made. The first call after a build reads the density back from the GPU (once).
		/// </summary>
		public float DensityAt(Vector3 worldPosition)
		{
			return data != null ? data.DensityAt(transform.InverseTransformPoint(worldPosition)) : 0f;
		}

		/// <summary>The cloud's density (0..1) at a point in local space (see <see cref="DensityAt" />).</summary>
		public float DensityAtLocal(Vector3 localPosition)
		{
			return data != null ? data.DensityAt(localPosition) : 0f;
		}

		/// <summary>
		///     The cloud's density (0..1, without the detail) exactly at points in local space, from the noise it is made of,
		///     not read from its voxels as <see cref="DensityAtLocal" /> does (no interpolation, no 8-bit steps): for picking
		///     places in the cloud by their density. Worked out on the GPU, waiting for the result: an occasional query, on the
		///     main thread. False (nothing written) before the cloud is made.
		/// </summary>
		public bool EvaluateDensity(ReadOnlySpan<Vector3> localPoints, Span<float> densities)
		{
			return data != null && data.EvaluateDensity(localPoints, densities);
		}

		/// <summary>
		///     How much light gets through the cloud on the way between two world points (1: no cloud between them), from the
		///     cloud's density without the detail, at <paramref name="samples" /> points along the way (CPU; the first call
		///     after a build reads the density back once). Line of sight through fog, a bolt seen through its cloud.
		/// </summary>
		public float Transmittance(Vector3 worldFrom, Vector3 worldTo, int samples = 16)
		{
			if (data == null || !data.Ready || samples <= 0)
			{
				return 1f;
			}

			Matrix4 worldToLocal = transform.WorldToLocal;
			Vector3 from = Vector3.TransformPosition(worldFrom, worldToLocal);
			Vector3 to = Vector3.TransformPosition(worldTo, worldToLocal);
			float step = Vector3.Distance(from, to) / samples;
			float density = 0f;
			for (int i = 0; i < samples; i++)
			{
				density += data.DensityAt(Vector3.Lerp(from, to, (i + 0.5f) / samples));
			}

			return MathF.Exp(-density * step * data.Sigma);
		}

		/// <summary>
		///     Where a ray first meets the cloud as something to look at: the distance along the ray (world units, from
		///     <paramref name="worldOrigin" />) at which the light through the cloud falls below <paramref name="transmittance" />,
		///     from the cloud's density without the detail (CPU; the first call after a build reads the voxels back once). The
		///     editor's scene view pans and zooms at it as at a mesh's surface. False when the ray passes through less cloud
		///     than that within <paramref name="maxDistance" />, or before the cloud is made.
		/// </summary>
		public bool Raycast(Vector3 worldOrigin, Vector3 worldDirection, float maxDistance, out float distance, float transmittance = 0.5f)
		{
			distance = 0f;
			if (data == null || !data.Ready || worldDirection.LengthSquared < 1e-12f || maxDistance <= 0f)
			{
				return false;
			}

			// The same ray parameter in both spaces: the local direction keeps the scale of a world unit.
			Vector3 dir = worldDirection.Normalized();
			Matrix4 worldToLocal = transform.WorldToLocal;
			Vector3 origin = Vector3.TransformPosition(worldOrigin, worldToLocal);
			Vector3 direction = Vector3.TransformVector(dir, worldToLocal);
			float localPerWorld = direction.Length;
			if (localPerWorld < 1e-12f)
			{
				return false;
			}

			// The shape's box: where the ray is inside it.
			Vector3 half = new Vector3(MathF.Max(Size.X, 1e-3f), MathF.Max(Size.Y, 1e-3f), MathF.Max(Size.Z, 1e-3f)) * 0.5f;
			float enter = 0f;
			float exit = maxDistance;
			for (int axis = 0; axis < 3; axis++)
			{
				if (MathF.Abs(direction[axis]) < 1e-12f)
				{
					if (origin[axis] < -half[axis] || origin[axis] > half[axis])
					{
						return false;
					}

					continue;
				}

				float a = (-half[axis] - origin[axis]) / direction[axis];
				float b = (half[axis] - origin[axis]) / direction[axis];
				enter = MathF.Max(enter, MathF.Min(a, b));
				exit = MathF.Min(exit, MathF.Max(a, b));
			}

			if (enter >= exit)
			{
				return false;
			}

			// Half a voxel a step (in world units along the ray), the optical depth summed until the light is down to the
			// threshold.
			float sigma = data.Sigma;
			float threshold = -MathF.Log(Math.Clamp(transmittance, 1e-4f, 0.9999f));
			float step = MathF.Max(data.Voxel * 0.5f / localPerWorld, (exit - enter) / 4096f);
			float tau = 0f;
			for (float t = enter + step * 0.5f; t < exit; t += step)
			{
				tau += data.DensityAt(origin + direction * t) * sigma * step * localPerWorld;
				if (tau >= threshold)
				{
					distance = t;
					return true;
				}
			}

			return false;
		}

		/// <summary>Makes the cloud again at once (it is made again by itself whenever its settings change).</summary>
		public void Rebuild()
		{
			data?.Invalidate();
		}

		/// <summary>A short line about the cloud: voxels, memory, state (the inspector shows it).</summary>
		public string Describe()
		{
			Vector3i grid = GridFor(Size, VoxelSize, out float voxel, out bool capped);
			long voxels = (long)grid.X * grid.Y * grid.Z;
			// Density r8 + lit rgba8, both with mips (a seventh more), the occupancy, the lights at half resolution.
			double megabytes = voxels * (5.0 * 8.0 / 7.0) / (1024.0 * 1024.0) + (Lights.Count > 0 ? voxels / 8.0 * 8.0 / (1024.0 * 1024.0) : 0.0);
			string state = data == null ? "not made yet" : data.Ready ? data.Pending ? "ready, being made again" : "ready" : "being made";
			string size = $"{grid.X} x {grid.Y} x {grid.Z} voxels of {voxel:0.###} units ({voxels / 1e6:0.##} M, about {megabytes:0} MB): {state}";
			return capped ? $"Too many voxels at {VoxelSize:0.###} units: {size}. Raise the voxel size or shrink the shape." : size;
		}

		#endregion

		#region LifecycleMethods

		public override void OnEnable()
		{
			VolumetricRuntime.Register(this);
		}

		void Serialization.ISerializationCallbackReceiver.OnBeforeSerialize()
		{
		}

		/// <summary>
		///     Read from a scene or prefab (the loading thread too): the file of its cloud starts loading on a worker thread now,
		///     in parallel with the rest of the load, and is uploaded when the object first draws.
		/// </summary>
		void Serialization.ISerializationCallbackReceiver.OnAfterDeserialize()
		{
			if (VolumeCache.Folder != null)
			{
				VolumeCache.Prefetch(DensityKey());
			}
		}

		public override void OnDisable()
		{
			VolumetricRuntime.Unregister(this);
		}

		public override void OnDestroy()
		{
			VolumetricRuntime.Unregister(this);
			data?.Dispose();
			data = null;
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     The light that lights the cloud: <see cref="Sun" /> (enabled or not: a disabled one is a light for the cloud
		///     alone), or the scene's main light.
		/// </summary>
		internal DirectionalLight EffectiveSun => Sun != null && !Sun.IsDestroyed ? Sun : DirectionalLight.This;

		/// <summary>
		///     The cloud's density as a key that is the same in every run and on every machine (made clouds are found by it,
		///     <see cref="VolumeCache" />): the version of what makes it, every setting it depends on and its grid. Unlike
		///     <see cref="ShapeHash" />: .NET's HashCode is seeded per process.
		/// </summary>
		internal string DensityKey()
		{
			Vector3i grid = GridFor(Size, VoxelSize, out _, out _);
			using var stream = new System.IO.MemoryStream(160);
			using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write("LELVOL density");
				writer.Write(VolumeFile.GeneratorVersion);
				writer.Write((int)Shape);
				writer.Write(Size.X);
				writer.Write(Size.Y);
				writer.Write(Size.Z);
				writer.Write(SideSoftness);
				writer.Write(CapSoftness);
				writer.Write(TopRoughness);
				writer.Write(BottomRoughness);
				writer.Write(SideRoughness);
				writer.Write((int)Noise);
				writer.Write(NoiseSize);
				writer.Write(Octaves);
				writer.Write(Roughness);
				writer.Write(Warp);
				writer.Write(Seed);
				writer.Write(Coverage);
				writer.Write(Softness);
				writer.Write(ClumpSize);
				writer.Write(ClumpCoverage);
				writer.Write(ClumpSoftness);
				writer.Write(VoxelSize);
				writer.Write(grid.X);
				writer.Write(grid.Y);
				writer.Write(grid.Z);
			}

			return KeyOf(stream);
		}

		/// <summary>The settings the lit voxels of a density are made with besides the sun (stable as <see cref="DensityKey" />).</summary>
		internal string LitKey(string densityKey)
		{
			using var stream = new System.IO.MemoryStream(96);
			using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write("LELVOL lit");
				writer.Write(densityKey);
				writer.Write(Density);
				writer.Write(ShadowReach);
				writer.Write(NoiseSize);
				writer.Write(VolumetricData.TauMax);
			}

			return KeyOf(stream);
		}

		private static string KeyOf(System.IO.MemoryStream stream)
		{
			byte[] hash = System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
			return Convert.ToHexString(hash, 0, 16);
		}

		/// <summary>A hash of everything the cloud's density depends on (a change makes it again).</summary>
		internal int ShapeHash()
		{
			var hash = new HashCode();
			hash.Add(Shape);
			hash.Add(Size);
			hash.Add(SideSoftness);
			hash.Add(CapSoftness);
			hash.Add(TopRoughness);
			hash.Add(BottomRoughness);
			hash.Add(SideRoughness);
			hash.Add(Noise);
			hash.Add(NoiseSize);
			hash.Add(Octaves);
			hash.Add(Roughness);
			hash.Add(Warp);
			hash.Add(Seed);
			hash.Add(Coverage);
			hash.Add(Softness);
			hash.Add(ClumpSize);
			hash.Add(ClumpCoverage);
			hash.Add(ClumpSoftness);
			hash.Add(VoxelSize);
			return hash.ToHashCode();
		}

		/// <summary>A hash of what the light volume depends on besides the density and the sun's direction.</summary>
		internal int LightHash()
		{
			return HashCode.Combine(Density, ShadowReach, NoiseSize);
		}

		/// <summary>
		///     A hash of the lights' transport: which lights, where, how far they reach (their colour and intensity are
		///     applied when the cloud is drawn).
		/// </summary>
		internal int LightsHash()
		{
			GatherLightSlots(hashSlots);
			var hash = new HashCode();
			hash.Add(LightGlow);
			hash.Add(Density);
			hash.Add(NoiseSize);
			hash.Add(hashSlots.Count);
			foreach (VolumeLightSlot slot in hashSlots)
			{
				hash.Add(slot.PositionSize);
				hash.Add(slot.Params);
				hash.Add(slot.Spot);
			}

			return hash.ToHashCode();
		}

		/// <summary>World units per local unit (the object's average scale): the scene lights' falloff is in world units.</summary>
		internal float WorldScale
		{
			get
			{
				Vector3 scale = transform.lossyScale;
				return MathF.Max((MathF.Abs(scale.X) + MathF.Abs(scale.Y) + MathF.Abs(scale.Z)) / 3f, 1e-6f);
			}
		}

		/// <summary>
		///     The lights the cloud's light transport is worked out for: its own lights first, then the scene's lights that
		///     reach the shape (with <see cref="SceneLights" />; nearest first), at most <see cref="MaxLights" />. A scene
		///     light is in the set whatever its intensity (a flash going dark and bright again keeps its transport).
		/// </summary>
		internal void GatherLightSlots(List<VolumeLightSlot> slots)
		{
			slots.Clear();
			int own = Math.Min(Lights.Count, MaxLights);
			for (int i = 0; i < own; i++)
			{
				VolumeLight light = Lights[i];
				if (light != null && light.Radius > 0f)
				{
					slots.Add(new VolumeLightSlot { PositionSize = new Vector4(light.Position, light.Radius), Params = new Vector4(0f, 0f, -2f, 0f), Spot = new Vector4(0f, 0f, 0f, 1f), Own = light });
				}
			}

			if (!SceneLights || slots.Count >= MaxLights || global::Light.Active.Count == 0)
			{
				return;
			}

			Matrix4 worldToLocal = transform.WorldToLocal;
			float worldScale = WorldScale;
			Vector3 half = Size * 0.5f;
			sceneCandidates.Clear();
			foreach (global::Light light in global::Light.Active)
			{
				if (light == null || light.IsDestroyed || !light.isActiveAndEnabled || !light.AffectsVolumes || light.VolumetricStrength <= 0f || light.Range <= 0f)
				{
					continue;
				}

				// How far the light is from the shape's box, in world units: it takes part when its range reaches the box.
				Vector3 local = Vector3.TransformPosition(light.transform.position, worldToLocal);
				Vector3 outside = Vector3.ComponentMax(Vector3.ComponentMax(-half - local, local - half), Vector3.Zero);
				float distance = outside.Length * worldScale;
				if (distance < light.Range)
				{
					sceneCandidates.Add((light, distance));
				}
			}

			sceneCandidates.Sort((a, b) => a.distance.CompareTo(b.distance));
			foreach ((global::Light light, float _) in sceneCandidates)
			{
				if (slots.Count >= MaxLights)
				{
					break;
				}

				Vector3 local = Vector3.TransformPosition(light.transform.position, worldToLocal);
				Vector3 spot = Vector3.TransformVector(light.transform.forward, worldToLocal);
				spot = spot.LengthSquared > 1e-12f ? spot.Normalized() : Vector3.UnitZ;
				float cosOuter = -2f;
				float spotScale = 0f;
				if (light.Type == LightType.Spot)
				{
					float outer = MathHelper.DegreesToRadians(Math.Clamp(light.SpotAngle, 1f, 179f) * 0.5f);
					float inner = outer * Math.Clamp(light.InnerSpot, 0f, 0.999f);
					cosOuter = MathF.Cos(outer);
					spotScale = 1f / MathF.Max(MathF.Cos(inner) - cosOuter, 1e-4f);
				}

				// The transport holds the falloff x (range / 4)^2: values about 1 across the light's reach, which half floats
				// keep from the light to its range.
				float normalization = light.Range * light.Range / 16f;
				slots.Add(new VolumeLightSlot
				{
					PositionSize = new Vector4(local, light.Range),
					Params = new Vector4(1f, MathF.Max(light.SourceRadius, 1e-3f), cosOuter, spotScale),
					Spot = new Vector4(spot, normalization),
					Scene = light
				});
			}
		}

		#endregion
	}
}
