using System;
using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Cornell-box style test room for lighting work: open-front room with colored side walls,
	///     a ceiling over most of it (dark interior for GI to fill), a few occluders, a sphere,
	///     a directional "sun" casting shadows and dynamic colored emitters orbiting inside.
	///     Walk around with <see cref="FpsController" />; see <see cref="GIDebugControls" /> for toggles.
	/// </summary>
	public static class GITestScene
	{
		#region PublicFields

		public const float RoomHalfSize = 10f;
		public const float WallHeight = 6f;
		public const float WallThickness = 0.25f;

		/// <summary>Towards the sun; the directional light shines the opposite way and the procedural sky puts its disc there.</summary>
		public static readonly Vector3 SunDirection = new Vector3(0.45f, 0.75f, -0.35f).Normalized();

		/// <summary>Startup switches, mainly for comparing renders from the command line.</summary>
		public sealed class Options
		{
			public bool GI = true;

			/// <summary>Lumen-style pipeline (surface cache + screen probes); false = voxel cone tracing.</summary>
			public bool Lumen = true;

			public bool VoxelView;

			/// <summary>Mip level the voxel view shows (0 = the voxels).</summary>
			public float VoxelViewMip;

			public bool SdfView;
			public bool SurfaceCacheView;
			public bool ProbeView;
			public bool Emitters = true;

			/// <summary>Animate the emitters (orbit, bobbing, pulsing). Off freezes them for stability measurements.</summary>
			public bool Animate = true;

			public bool Stats;

			/// <summary>A procedural skybox (gradient sky with a sun) as the environment: the GI samples it for rays that leave the room.</summary>
			public bool Sky;

			/// <summary>Skybox from a project asset instead of the procedural sky: a .material, a cubemap or a panorama image (skybox=path).</summary>
			public string SkyboxAsset;

			/// <summary>Scripted camera motion (slow yaw and strafe) for recording temporal stability tests.</summary>
			public bool AutoCamera;

			/// <summary>Mouse look; off for measurement runs so a moving mouse cannot turn the camera.</summary>
			public bool MouseLook = true;

			/// <summary>GPU timer queries (per-pass times in the stats line).</summary>
			public bool Profiler = true;

			/// <summary>Start in fullscreen (the desktop compositor then stops sharing the GPU with other windows).</summary>
			public bool Fullscreen;

			/// <summary>Dump this many consecutive frames (BMP) to <see cref="DumpDirectory" /> after a warm-up.</summary>
			public int DumpFrames;

			public string DumpDirectory = "framedump";
			public float DumpAfterSeconds = 5f;
			public int DumpAfterFrames;

			/// <summary>Per-frame probe jitter (off for diagnostics).</summary>
			public bool ProbeJitter = true;

			/// <summary>Half-resolution screen-space cone tracing (GIResolvePass) instead of per-fragment.</summary>
			public bool Resolve = true;

			/// <summary>Mark room and props static so they are voxelized once.</summary>
			public bool StaticCache = true;

			/// <summary>Indirect bounces in the voxel volume (off = direct light and emission only).</summary>
			public bool Bounce = true;

			/// <summary>Voxel GI bounce iterations per frame (see GlobalIlluminationSettings.BounceIterations).</summary>
			public int BounceIterations = 2;

			/// <summary>Cones per voxel for the volume's bounces (6 or 1) when the bounce is not per 2x2x2 voxels.</summary>
			public int BounceCones = 6;

			/// <summary>Bounces per 2x2x2 voxels instead of cones from every voxel.</summary>
			public bool BounceCells = true;

			/// <summary>World-anchored rotation and step jitter of the voxel GI cone sets.</summary>
			public bool ConeJitter = true;

			/// <summary>World size of the resolve's jitter cells (0 = half a voxel).</summary>
			public float ConeJitterCellSize;

			/// <summary>Coverage voxelization: occupancy = splatted weight times this.</summary>
			public float CoverageOccupancyScale = 2f;

			/// <summary>Coverage voxelization: voxels a surface is splatted over (2 tent, 3 B-spline).</summary>
			public int CoverageSplatWidth = 2;
			/// <summary>Edge-aware a-trous passes over the voxel GI resolve (0 = off).</summary>
			public int ResolveFilterPasses = 4;

			/// <summary>Sun shadow samples per voxel when lighting the volume (1 or 4).</summary>
			public int InjectShadowTaps = 4;

			/// <summary>Coverage-weighted, order-independent voxelization (off = one sample per voxel, last writer wins).</summary>
			public bool VoxelCoverage = true;

			/// <summary>What the voxel radiance mips keep of direction.</summary>
			public VoxelMipMode VoxelMips = VoxelMipMode.Anisotropic;

			/// <summary>Mip levels above the voxels with directional views (isotropic above; 16 = all).</summary>
			public int AnisotropicMipLevels = 16;

			/// <summary>How voxel GI cones find geometry (default: visibility from the distance field).</summary>
			public GITraceMode TraceMode = GITraceMode.SdfCones;

			public int VoxelResolution = 128;
			public int SdfResolution = 128;

			// Lumen quality knobs (defaults mirror GlobalIlluminationSettings).
			public float ProbeAnchorJitter = 0f;
			public bool ProbeDirectionJitter = true;
			public int ProbeSpacing = 16;
			public bool ProbeAdaptivePlacement = true;
			public int RadiosityRays = 4;
			public float RadiosityBlend = 0.9f;
			public float RadiosityMaxHistorySamples = 64f;
			public float RadiosityMovingHistorySamples = 16f;
			public float ProbeHistoryWeight = 0.5f;
			public float ProbeMaxHistorySamples = 32f;
			public float ProbeChangeThreshold = 0.5f;
			public float ProbeChangeSigma = 4f;
			public float ProbeChangeFloor = 0.15f;
			public float ProbeMovingHistorySamples = 2f;
			public float ProbeMovingFrames = 4f;
			public float ProbeImportanceMergeFraction = 0.5f;
			public float ProbeJumpThreshold = 0.4f;
			public float ProbeTemporalBlend = 0.9f;
			public float ProbeTemporalMovingFrames = 3f;
			public bool ProbeImportanceSampling = true;
			public bool ProbeSpatialFilter = true;
			public int ProbeSpatialFilterPasses = 1;
			public int ProbeFilterRadius = 1;
			public int SurfaceCacheTexelsPerMeter = 6;
			public bool RadianceCache = true;
			public bool RadianceCacheForRadiosity = true;
			public bool SdfObjectIdLookup = true;
			public bool RadianceCacheFarShortcut = true;
			public bool ProbeAdaptiveFlaggedRefine;
			public int RadiosityIdleDivisor = 4;
			public float RadianceCacheNearDistance = 2f;
			public float RadianceCacheHistoryFrames = 20f;
			public float RadianceCacheMaxHistorySamples = 32f;
			public float RadianceCacheChangeThreshold = 0.25f;
			public int RadianceCacheProbesPerFrame = 160;
			public int RadianceCacheChangeProbesPerFrame = 64;
			public float RadianceCacheChangeRadius = 2f;
			public int RadianceCacheProbeResolution = 16;
			public int RadianceCacheTraceResolution = 32;

			/// <summary>GPU-driven geometry (culling + multi-draw indirect); off draws every object from the CPU.</summary>
			public bool GpuDriven = true;

			/// <summary>Extra small cubes on the floor, outside the GI (draw submission measurements).</summary>
			public int StressObjects;

			/// <summary>Strength of the indirect specular term (0 isolates the diffuse GI).</summary>
			public float GISpecularStrength = 1f;

			/// <summary>Resolution of the voxel GI resolve relative to the screen.</summary>
			public float ResolveScale = 0.5f;

			/// <summary>Resolution of the Lumen screen probe integration relative to the screen.</summary>
			public float ScreenProbeResolveScale = 1f;

			/// <summary>Adaptive screen probe capacity as a fraction of the uniform probe grid.</summary>
			public float ProbeAdaptiveFraction = 0.5f;

			/// <summary>Glass props (transparent materials: sorting, blending, GI from the volume); off for the plain room.</summary>
			public bool Glass = true;

			/// <summary>
			///     Refraction props (GrabPass materials) close to the player's start; off by default, so the room's reference
			///     frames stay as they were.
			/// </summary>
			public bool Refraction;

			/// <summary>Trail props (TrailRenderer) close to the player's start; off by default.</summary>
			public bool Trails;

			/// <summary>Engine/LitTextured.shader props (workflows, maps, tiling) close to the player's start; off by default.</summary>
			public bool Materials;

			/// <summary>Start position of the player's eye (null = the usual spot by the open side of the room).</summary>
			public Vector3? CameraPosition;

			/// <summary>The point the camera looks at when <see cref="CameraPosition" /> is set (default: straight ahead).</summary>
			public Vector3? CameraLookAt;

			/// <summary>No sun, no ambient, a black sky: the emitters are the only light, so their GI (and any lag of it) shows plainly.</summary>
			public bool Night;

			// ---- Ghosting measurements (see GhostTest): scripted events, logged screen regions, a noise window ----

			/// <summary>Frame at which the orb Orb0 jumps half its orbit (0 = never).</summary>
			public int EmitterStepFrame;

			/// <summary>Frame at which Orb0 goes dark (0 = never).</summary>
			public int EmitterOffFrame;

			/// <summary>Frame at which a dynamic box starts crossing the room (0 = no box).</summary>
			public int MoverFrame;

			/// <summary>Depth (world z) of the box's path across the room.</summary>
			public float MoverZ = -1.6f;

			/// <summary>Emission intensity of the moving box (0 = not emissive).</summary>
			public float MoverEmission;

			/// <summary>World points "x,y,z;x,y,z;..." whose screen squares are logged every frame.</summary>
			public string RegionPoints;

			public int RegionSize = 16;
			public int RegionStartFrame = 1;

			/// <summary>What the regions and the noise window read: "scene" (HDR scene color) or "gi" (indirect diffuse).</summary>
			public string RegionSource;

			/// <summary>Frames of per-pixel temporal statistics, starting at <see cref="NoiseStartFrame" /> (0 = none).</summary>
			public int NoiseFrames;

			public int NoiseStartFrame = 1;
		}

		#endregion

		#region PublicMethods

		public static void Load(Scene scene)
		{
			Load(scene, new Options());
		}

		/// <summary>Builds the room in code, sets its look and quality from the options, then applies the harness options.</summary>
		public static void Load(Scene scene, Options options)
		{
			markStatic = options.StaticCache;
			BuildRoom(scene);
			BuildProps(scene);
			if (options.Glass)
			{
				BuildGlass(scene);
			}
			if (options.Refraction)
			{
				BuildRefraction(scene);
			}
			if (options.Trails)
			{
				BuildTrails(scene);
			}
			if (options.Materials)
			{
				BuildMaterials(scene);
			}
			BuildLight(scene);
			if (options.Emitters)
			{
				BuildEmitters(scene, options.Animate);
			}
			BuildPlayer(scene);
			ApplySettings(options);
			if (options.Sky)
			{
				scene.Settings.Environment.Skybox = CreateSkybox(options, SunDirection);
			}

			if (options.Night)
			{
				ApplyNight(scene);
			}

			ApplyHarness(scene, options);
		}

		/// <summary>
		///     The view of the ghosting tests (view=ghost): from under the ceiling at the open side, looking down at the
		///     floor the orbs circle over, the back wall and both coloured walls, past the pillars.
		/// </summary>
		public static readonly Vector3 GhostViewPosition = new Vector3(0f, 5.3f, -3.6f);

		public static readonly Vector3 GhostViewLookAt = new Vector3(0f, 0f, 4.5f);

		/// <summary>Adds the frame dump / stats controls to a scene without them (other test scenes), then applies the harness.</summary>
		public static void InstallHarness(Scene scene, Options options)
		{
			if (scene.FindObjectOfType<GIDebugControls>() == null)
			{
				GameObject host = scene.CreateGameObject("Harness");
				host.AddComponent<GIDebugControls>();
			}

			ApplyHarness(scene, options);
		}

		#endregion

		#region InternalMethods

		/// <summary>The skybox the options ask for: a project asset (skybox=path), else the procedural sky with its sun at <paramref name="sunDirection" />.</summary>
		internal static Material CreateSkybox(Options options, Vector3 sunDirection)
		{
			if (!string.IsNullOrEmpty(options.SkyboxAsset))
			{
				Material asset = LoadSkyboxAsset(options.SkyboxAsset);
				if (asset != null)
				{
					return asset;
				}
			}

			return CreateProceduralSky(sunDirection);
		}

		/// <summary>A gradient sky with a sun disc towards <paramref name="sunDirection" />, as a skybox material on the engine's cubemap shader (sky=1).</summary>
		internal static Material CreateProceduralSky(Vector3 sunDirection)
		{
			Vector3 sun = sunDirection.Normalized();
			Cubemap cubemap = Cubemap.Generate(64, direction =>
			{
				float up = Math.Clamp(direction.Y, -1f, 1f);
				Vector3 zenith = new Vector3(0.18f, 0.4f, 0.95f);
				Vector3 horizon = new Vector3(0.75f, 0.85f, 1f);
				Vector3 ground = new Vector3(0.16f, 0.13f, 0.1f);
				Vector3 color = up >= 0f ? Vector3.Lerp(horizon, zenith, (float)Math.Pow(up, 0.6)) : Vector3.Lerp(horizon, ground, Math.Min(1f, -up * 4f));
				float disc = Vector3.Dot(direction, sun);
				color += new Vector3(1f, 0.95f, 0.85f) * (40f * (float)Math.Clamp((disc - 0.996) / 0.004, 0.0, 1.0) + 0.6f * (float)Math.Pow(Math.Max(0f, disc), 16.0));
				return new Color4(color.X, color.Y, color.Z, 1f);
			}, "procedural sky");

			var material = new Material(InternalStorage.GetOrCreateShader("Engine/Skybox.shader")) { name = "Procedural Sky" };
			material.SetTexture("Cubemap", cubemap);
			material.SetFloat("Exposure", 1f);
			return material;
		}

		/// <summary>
		///     A skybox material from the project assets: a .material as it is, a cubemap on the engine's cubemap
		///     shader, any other image as an equirectangular panorama. Null (with a warning) when nothing fits.
		/// </summary>
		private static Material LoadSkyboxAsset(string nameOrPath)
		{
			string path = AssetDatabase.Resolve(nameOrPath, "Materials") ?? AssetDatabase.Resolve(nameOrPath, "Textures");
			if (path == null)
			{
				Debug.LogWarning("[Sky] Skybox asset not found: " + nameOrPath);
				return null;
			}

			if (path.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
			{
				return InternalStorage.GetOrCreateMaterial(path);
			}

			Texture texture = AssetDatabase.Load<Texture>(path);
			Material material;
			switch (texture)
			{
				case Cubemap cubemap:
					material = new Material(InternalStorage.GetOrCreateShader("Engine/Skybox.shader")) { name = cubemap.name + " sky" };
					material.SetTexture("Cubemap", cubemap);
					break;
				case Texture2D panorama:
					material = new Material(InternalStorage.GetOrCreateShader("Engine/SkyboxPanoramic.shader")) { name = panorama.name + " sky" };
					material.SetTexture("Panorama", panorama);
					break;
				default:
					Debug.LogWarning("[Sky] " + path + " is not a cubemap, a panorama image or a material");
					return null;
			}

			material.SetFloat("Exposure", 1f);
			return material;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Measurement / debugging switches that are not part of the scene: debug views, profiler, fullscreen,
		///     camera autopilot and mouse look, stats and frame dumps. Works on a built or a loaded scene.
		/// </summary>
		public static void ApplyHarness(Scene scene, Options options)
		{
			if (options.VoxelView)
			{
				LELEngine.Rendering.Passes.VoxelDebugPass voxelView = Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.VoxelDebugPass>();
				voxelView.Enabled = true;
				voxelView.Mip = options.VoxelViewMip;
			}
			if (options.SdfView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.SdfDebugPass>().Enabled = true;
			}
			if (options.SurfaceCacheView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.SurfaceCacheDebugPass>().Enabled = true;
			}
			if (options.ProbeView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.ProbeDebugPass>().Enabled = true;
			}

			Game.Mono.Renderer.Profiler.Enabled = options.Profiler;
			Game.Mono.Renderer.Settings.GpuDriven = options.GpuDriven;
			if (options.StressObjects > 0)
			{
				BuildStress(scene, options.StressObjects);
			}

			if (options.Fullscreen)
			{
				Game.Mono.WindowState = OpenTK.Windowing.Common.WindowState.Fullscreen;
			}

			FpsController controller = scene.FindObjectOfType<FpsController>();
			if (controller != null)
			{
				controller.AutoPilot = options.AutoCamera;
				controller.MouseLook = options.MouseLook;
				if (options.CameraPosition.HasValue)
				{
					PlaceCamera(controller, options.CameraPosition.Value, options.CameraLookAt ?? options.CameraPosition.Value + Vector3.UnitZ);
				}
			}

			GIDebugControls controls = scene.FindObjectOfType<GIDebugControls>();
			if (controls != null)
			{
				controls.PrintStats = options.Stats;
				controls.DumpFrames = options.DumpFrames;
				controls.DumpDirectory = options.DumpDirectory;
				controls.DumpAfterSeconds = options.DumpAfterSeconds;
				controls.DumpAfterFrames = options.DumpAfterFrames;
			}

			if (options.EmitterStepFrame > 0 || options.EmitterOffFrame > 0 || options.MoverFrame > 0 || !string.IsNullOrWhiteSpace(options.RegionPoints) || options.NoiseFrames > 0)
			{
				GhostTest ghost = scene.CreateGameObject("GhostTest").AddComponent<GhostTest>();
				ghost.EmitterStepFrame = options.EmitterStepFrame;
				ghost.EmitterOffFrame = options.EmitterOffFrame;
				ghost.MoverFrame = options.MoverFrame;
				ghost.MoverStart.Z = options.MoverZ;
				ghost.MoverEnd.Z = options.MoverZ;
				ghost.MoverEmission = options.MoverEmission;
				ghost.ParseRegionPoints(options.RegionPoints);
				ghost.RegionSize = Math.Max(1, options.RegionSize);
				ghost.RegionStartFrame = options.RegionStartFrame;
				ghost.RegionSource = string.Equals(options.RegionSource, "gi", StringComparison.OrdinalIgnoreCase) ? LELEngine.Rendering.FrameStatistics.Source.IndirectDiffuse
					: string.Equals(options.RegionSource, "motion", StringComparison.OrdinalIgnoreCase) ? LELEngine.Rendering.FrameStatistics.Source.Motion
					: LELEngine.Rendering.FrameStatistics.Source.SceneColor;
				ghost.NoiseFrames = options.NoiseFrames;
				ghost.NoiseStartFrame = options.NoiseStartFrame;
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>Puts the player's eye at <paramref name="position" />, looking at <paramref name="lookAt" /> (yaw on the player, pitch on the camera).</summary>
		private static void PlaceCamera(FpsController controller, Vector3 position, Vector3 lookAt)
		{
			Vector3 direction = lookAt - position;
			float horizontal = (float)Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
			float yaw = horizontal > 1e-4f ? (float)(Math.Atan2(direction.X, direction.Z) * QuaternionHelper.Rad2Deg) : 0f;
			controller.transform.position = position;
			controller.transform.rotation = QuaternionHelper.Euler(0f, yaw, 0f);
			controller.EyeHeight = position.Y;
			controller.StartPitch = (float)(Math.Atan2(-direction.Y, Math.Max(horizontal, 1e-4f)) * QuaternionHelper.Rad2Deg);
		}

		/// <summary>
		///     Night (night=1): the sun and the ambient off, the sky black, the fixed lamp and strip dimmed so the moving
		///     orbs light most of the room; exposure up so the emitters' light reads.
		/// </summary>
		private static void ApplyNight(Scene scene)
		{
			DirectionalLight sun = scene.FindObjectOfType<DirectionalLight>();
			if (sun != null)
			{
				sun.Strength = 0f;
			}

			foreach (EmissiveOrbiter emitter in scene.FindObjectsOfType<EmissiveOrbiter>())
			{
				if (emitter.Radius <= 0f)
				{
					emitter.Intensity *= 0.2f;
				}
			}

			Lighting.Environment.AmbientStrength = 0f;
			Lighting.GI.SkyRadiance = new Vector3(0.004f, 0.005f, 0.008f);
			Lighting.Environment.BackgroundColor = new Color4(0.004f, 0.005f, 0.008f, 1f);
			Lighting.Environment.Exposure = 1.6f;
		}

		/// <summary>Environment, shadow and GI settings of the room (written into the active scene's settings).</summary>
		internal static void ApplySettings(Options options)
		{
			// With GI on, the flat ambient only has to cover leaks; the bounce does the rest.
			Lighting.Environment.AmbientColor = new Color4(0.55f, 0.6f, 0.75f, 1f);
			Lighting.Environment.AmbientStrength = 0.06f;
			Lighting.Environment.SpecularStrength = 0.25f;
			Lighting.Environment.SpecularShine = 48f;

			Lighting.Shadows.Enabled = true;
			Lighting.Shadows.MapSize = 2048;
			Lighting.Shadows.Distance = 30f;
			Lighting.Shadows.DepthRange = 60f;

			// Voxel volume tightly around the room: 26 m / 128 = ~0.2 m voxels.
			Lighting.GI.Enabled = options.GI;
			Lighting.GI.Mode = options.Lumen ? GIMode.Lumen : GIMode.VoxelConeTracing;
			Lighting.GI.Resolution = options.VoxelResolution;
			Lighting.GI.GridSize = 26f;
			Lighting.GI.Center = new Vector3(0f, 3.5f, 0f);
			Lighting.GI.FollowCamera = false;
			Lighting.GI.DiffuseStrength = 1.0f;
			Lighting.GI.SpecularStrength = options.GISpecularStrength;
			Lighting.GI.OcclusionStrength = 1.0f;
			Lighting.GI.ProbeJitter = options.ProbeJitter;
			Lighting.GI.ProbeAnchorJitter = options.ProbeAnchorJitter;
			Lighting.GI.ProbeDirectionJitter = options.ProbeDirectionJitter;
			Lighting.GI.ProbeSpacing = options.ProbeSpacing;
			Lighting.GI.ProbeAdaptivePlacement = options.ProbeAdaptivePlacement;
			Lighting.GI.RadiosityRays = options.RadiosityRays;
			Lighting.GI.RadiosityBlend = options.RadiosityBlend;
			Lighting.GI.RadiosityMaxHistorySamples = options.RadiosityMaxHistorySamples;
			Lighting.GI.RadiosityMovingHistorySamples = options.RadiosityMovingHistorySamples;
			Lighting.GI.ProbeHistoryWeight = options.ProbeHistoryWeight;
			Lighting.GI.ProbeMaxHistorySamples = options.ProbeMaxHistorySamples;
			Lighting.GI.ProbeChangeThreshold = options.ProbeChangeThreshold;
			Lighting.GI.ProbeChangeSigma = options.ProbeChangeSigma;
			Lighting.GI.ProbeChangeFloor = options.ProbeChangeFloor;
			Lighting.GI.ProbeMovingHistorySamples = options.ProbeMovingHistorySamples;
			Lighting.GI.ProbeMovingFrames = options.ProbeMovingFrames;
			Lighting.GI.ProbeImportanceMergeFraction = options.ProbeImportanceMergeFraction;
			Lighting.GI.ProbeJumpThreshold = options.ProbeJumpThreshold;
			Lighting.GI.ProbeTemporalBlend = options.ProbeTemporalBlend;
			Lighting.GI.ProbeTemporalMovingFrames = options.ProbeTemporalMovingFrames;
			Lighting.GI.ProbeImportanceSampling = options.ProbeImportanceSampling;
			Lighting.GI.ProbeSpatialFilter = options.ProbeSpatialFilter;
			Lighting.GI.ProbeSpatialFilterPasses = options.ProbeSpatialFilterPasses;
			Lighting.GI.ProbeFilterRadius = options.ProbeFilterRadius;
			Lighting.GI.RadianceCacheEnabled = options.RadianceCache;
			Lighting.GI.SdfObjectIdLookup = options.SdfObjectIdLookup;
			Lighting.GI.RadianceCacheFarShortcut = options.RadianceCacheFarShortcut;
			Lighting.GI.ProbeAdaptiveFlaggedRefine = options.ProbeAdaptiveFlaggedRefine;
			Lighting.GI.RadiosityIdleDivisor = options.RadiosityIdleDivisor;
			Lighting.GI.RadianceCacheForRadiosity = options.RadianceCacheForRadiosity;
			Lighting.GI.RadianceCacheNearDistance = options.RadianceCacheNearDistance;
			Lighting.GI.RadianceCacheHistoryFrames = options.RadianceCacheHistoryFrames;
			Lighting.GI.RadianceCacheMaxHistorySamples = options.RadianceCacheMaxHistorySamples;
			Lighting.GI.RadianceCacheChangeThreshold = options.RadianceCacheChangeThreshold;
			Lighting.GI.RadianceCacheProbesPerFrame = options.RadianceCacheProbesPerFrame;
			Lighting.GI.RadianceCacheChangeProbesPerFrame = options.RadianceCacheChangeProbesPerFrame;
			Lighting.GI.RadianceCacheChangeRadius = options.RadianceCacheChangeRadius;
			Lighting.GI.RadianceCacheProbeResolution = options.RadianceCacheProbeResolution;
			Lighting.GI.RadianceCacheTraceResolution = options.RadianceCacheTraceResolution;
			Lighting.GI.SurfaceCacheTexelsPerMeter = options.SurfaceCacheTexelsPerMeter;
			Lighting.GI.ScreenSpaceResolve = options.Resolve;
			Lighting.GI.ResolveScale = options.ResolveScale;
			Lighting.GI.ScreenProbeResolveScale = options.ScreenProbeResolveScale;
			Lighting.GI.ProbeAdaptiveFraction = options.ProbeAdaptiveFraction;
			Lighting.GI.DynamicUpdateInterval = 1;
			Lighting.GI.LightingUpdateInterval = 1;
			Lighting.GI.BounceStrength = options.Bounce ? 1f : 0f;
			Lighting.GI.BounceIterations = options.Bounce ? Math.Max(0, options.BounceIterations) : 0;
			Lighting.GI.BounceCones = options.BounceCones;
			Lighting.GI.BounceCells = options.BounceCells;
			Lighting.GI.ConeJitter = options.ConeJitter;
			Lighting.GI.ConeJitterCellSize = options.ConeJitterCellSize;
			Lighting.GI.CoverageOccupancyScale = options.CoverageOccupancyScale;
			Lighting.GI.CoverageSplatWidth = options.CoverageSplatWidth;			Lighting.GI.ResolveFilterPasses = options.ResolveFilterPasses;
			Lighting.GI.InjectShadowTaps = options.InjectShadowTaps;
			Lighting.GI.VoxelCoverage = options.VoxelCoverage;
			Lighting.GI.VoxelMips = options.VoxelMips;
			Lighting.GI.AnisotropicMipLevels = options.AnisotropicMipLevels;

			// Global SDF over the same grid; sky light enters through the open side of the room.
			Lighting.GI.DistanceFieldEnabled = true;
			Lighting.GI.SdfResolution = options.SdfResolution;
			Lighting.GI.SdfBandVoxels = 8;
			Lighting.GI.SdfMaxSteps = 48;
			Lighting.GI.TraceMode = options.TraceMode;
			Lighting.GI.SdfDetailDistance = 1.5f;
			// The sky the GI sees must be the sky the camera sees: same radiance as the clear color.
			Lighting.GI.SkyRadiance = new Vector3(0.45f, 0.6f, 0.85f);

			Lighting.Environment.BackgroundColor = new Color4(0.45f, 0.6f, 0.85f, 1f);
			Lighting.Environment.Exposure = 1.0f;
		}

		#endregion

		#region PrivateFields

		private static bool markStatic = true;

		#endregion

		#region PrivateMethods

		private static void BuildRoom(Scene scene)
		{
			float s = RoomHalfSize;
			float h = WallHeight;
			float t = WallThickness;

			// cube.obj spans -1..1, so scale is half-size.
			Box(scene, "Floor", "LitWhite.material", new Vector3(0f, -t, 0f), new Vector3(s + t, t, s + t));
			Box(scene, "BackWall", "LitWhite.material", new Vector3(0f, h * 0.5f, s), new Vector3(s + t, h * 0.5f, t));

			// Screen-right is -X when looking down +Z in this engine, so the classic
			// "red left / green right" Cornell layout puts red at +X.
			Box(scene, "LeftWall", "LitRed.material", new Vector3(s, h * 0.5f, 0f), new Vector3(t, h * 0.5f, s));
			Box(scene, "RightWall", "LitGreen.material", new Vector3(-s, h * 0.5f, 0f), new Vector3(t, h * 0.5f, s));

			// Ceiling over the back 70% of the room: a dark interior that only indirect light reaches.
			Box(scene, "Ceiling", "LitWhite.material", new Vector3(0f, h, s * 0.3f), new Vector3(s + t, t, s * 0.7f));
		}

		private static void BuildProps(Scene scene)
		{
			GameObject tall = Box(scene, "TallBox", "LitGray.material", new Vector3(-3.5f, 2f, 4f), new Vector3(1f, 2f, 1f));
			tall.transform.rotation = QuaternionHelper.Euler(0f, 20f, 0f);

			GameObject shortBox = Box(scene, "ShortBox", "LitGray.material", new Vector3(3f, 1f, 1.5f), new Vector3(1f, 1f, 1f));
			shortBox.transform.rotation = QuaternionHelper.Euler(0f, -15f, 0f);

			Box(scene, "Slab", "LitBlue.material", new Vector3(0f, 0.25f, -3f), new Vector3(2.5f, 0.25f, 1f));

			GameObject sphere = SceneModels.Instantiate(scene, SceneModels.Sphere, "Sphere");
			sphere.transform.position = new Vector3(0f, 1.75f, 5f);
			sphere.transform.scale = Vector3.One * 1.25f;
			MeshRenderer sphereRenderer = sphere.GetComponent<MeshRenderer>();
			sphereRenderer.SetMaterials(SceneModels.Material("LitYellow.material"));
			sphereRenderer.IsStatic = markStatic;

			// Pillars under the ceiling edge; long thin shadows are a good test for bias settings.
			for (int i = -1; i <= 1; i++)
			{
				Box(scene, "Pillar" + i, "LitWhite.material", new Vector3(i * 6f, WallHeight * 0.5f, -4f), new Vector3(0.35f, WallHeight * 0.5f, 0.35f));
			}

			// Thin railing in front of the slab: a few pixels tall on screen, too thin for the uniform probe
			// grid, so it exercises adaptive probe placement.
			Box(scene, "RailBar", "LitWhite.material", new Vector3(0f, 1f, -5.5f), new Vector3(3f, 0.03f, 0.03f));
			for (int i = -1; i <= 1; i++)
			{
				Box(scene, "RailPost" + i, "LitWhite.material", new Vector3(i * 3f, 0.5f, -5.5f), new Vector3(0.03f, 0.5f, 0.03f));
			}
		}

		/// <summary>
		///     Transparent props in front of the player's start: a tinted pane over the short box, a glass sphere over
		///     the tall box, a second pane that overlaps the first on screen from further back (a sorting check: the
		///     amber pane must show through the cyan one, never over it), and a flat pane near the player whose
		///     shadow falls on the floor in view (a translucent, amber-tinted shadow as strong as the pane's opacity).
		/// </summary>
		private static void BuildGlass(Scene scene)
		{
			GameObject pane = Box(scene, "GlassPane", "GlassCyan.material", new Vector3(2.5f, 1.5f, -2f), new Vector3(1.5f, 1.2f, 0.02f));
			pane.transform.rotation = QuaternionHelper.Euler(0f, -20f, 0f);

			Box(scene, "GlassPaneBehind", "GlassAmber.material", new Vector3(1.5f, 1.3f, 0f), new Vector3(1f, 0.9f, 0.02f));

			Box(scene, "GlassTable", "GlassAmber.material", new Vector3(-3.2f, 0.9f, -6.3f), new Vector3(1.2f, 0.02f, 0.7f));

			GameObject sphere = SceneModels.Instantiate(scene, SceneModels.Sphere, "GlassSphere");
			sphere.transform.position = new Vector3(-2.5f, 1.1f, -2.5f);
			sphere.transform.scale = Vector3.One * 0.9f;
			MeshRenderer renderer = sphere.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material("GlassRed.material"));
		}

		/// <summary>
		///     Refraction props (refraction=1, GrabPass materials) in front of the player's start: a pane of bumpy glass in
		///     front of the rail and the slab, with a thin red post between the player and the pane that must stay
		///     sharp (nothing in front of a refracting surface is shifted into it); a bumpy glass sphere beside it, drawn
		///     after the pane (it takes a copy of its own: it reads where the pane drew); and particle quads
		///     (Engine/ParticlesUnlit) refracting through a ring normal map: pure refraction (alpha blending) and additive
		///     with an orange glow (it shares the sphere's copy).
		/// </summary>
		private static void BuildRefraction(Scene scene)
		{
			Texture2D bumps = BumpNormalMap(256, 6, 0.35f);
			Texture2D ring = RingNormalMap(128);

			Material paneMaterial = SceneModels.Material("GlassCyan.material").Clone();
			paneMaterial.SetFloat("Refraction", 1f);
			paneMaterial.SetTexture("NormalMap", bumps);
			paneMaterial.SetFloat("RefractionStrength", 0.06f);
			GameObject pane = Box(scene, "RefractionPane", "GlassCyan.material", new Vector3(0.35f, 1.3f, -6.3f), new Vector3(0.75f, 0.55f, 0.01f));
			pane.GetComponent<MeshRenderer>().SetMaterials(paneMaterial);

			Box(scene, "RefractionPost", "LitRed.material", new Vector3(0.12f, 1.3f, -7.2f), new Vector3(0.025f, 0.35f, 0.025f));

			Material sphereMaterial = SceneModels.Material("GlassRed.material").Clone();
			sphereMaterial.SetFloat("Refraction", 1f);
			sphereMaterial.SetTexture("NormalMap", bumps);
			sphereMaterial.SetFloat("RefractionStrength", 0.1f);
			GameObject sphere = SceneModels.Instantiate(scene, SceneModels.Sphere, "RefractionSphere");
			sphere.transform.position = new Vector3(-0.95f, 1.35f, -6.55f);
			sphere.transform.scale = Vector3.One * 0.3f;
			sphere.GetComponent<MeshRenderer>().SetMaterials(sphereMaterial);

			ShaderProgram particles = InternalStorage.GetOrCreateShader("Engine/ParticlesUnlit.shader");
			var haze = new Material(particles);
			haze.SetTexture("BaseMap", LELEngine.VFX.VfxDefaults.SoftDisc);
			haze.SetTexture("NormalMap", ring);
			haze.SetFloat("Refraction", 1f);
			haze.SetFloat("RefractionStrength", 0.2f);
			haze.SetFloat("RefractionBlend", 1f);
			RefractionQuad(scene, "RefractionHaze", haze, new Vector3(-0.45f, 1.55f, -6.8f), 0.3f);

			var glow = new Material(particles);
			glow.SetTexture("BaseMap", LELEngine.VFX.VfxDefaults.SoftDisc);
			glow.SetTexture("NormalMap", ring);
			glow.SetColor("Color", new Color4(1f, 0.45f, 0.1f, 1f));
			glow.SetFloat("BlendMode", 2f);
			glow.SetFloat("Refraction", 1f);
			glow.SetFloat("RefractionStrength", 0.12f);
			glow.SetFloat("RefractionBlend", 0.6f);
			RefractionQuad(scene, "RefractionGlow", glow, new Vector3(1.05f, 1.5f, -6.9f), 0.25f);
		}

		/// <summary>
		///     Trail props (trails=1, TrailRenderer, Engine/ParticlesUnlit) in front of the player's start: a tapering
		///     additive figure eight (white to cyan to transparent blue, a soft band across it), a looping alpha blended trail
		///     with round corners and caps that stops emitting every 1.5 s, a twisting ribbon facing its transform's Z axis
		///     (opaque, both faces), and a checker trail in Tile mode (two checks per world unit).
		/// </summary>
		private static void BuildTrails(Scene scene)
		{
			ShaderProgram particles = InternalStorage.GetOrCreateShader("Engine/ParticlesUnlit.shader");

			var additive = new Material(particles);
			additive.SetTexture("BaseMap", SoftBand(64));
			additive.SetFloat("BlendMode", 2f);
			TrailRenderer taper = TrailProp(scene, "TrailTaper", new Vector3(-1.1f, 1.45f, -5.6f), new Vector2(0.55f, 0.35f), 1.3f, 2.6f, 0f, additive);
			taper.time = 1.2f;
			taper.minVertexDistance = 0.02f;
			taper.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
			taper.widthMultiplier = 0.25f;
			var taperColors = new Gradient();
			taperColors.SetKeys(
				new[] { new GradientColorKey(Color4.White, 0f), new GradientColorKey(new Color4(0.2f, 0.9f, 1f, 1f), 0.4f), new GradientColorKey(new Color4(0.1f, 0.2f, 1f, 1f), 1f) },
				new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
			taper.colorGradient = taperColors;

			var alpha = new Material(particles);
			TrailRenderer round = TrailProp(scene, "TrailRound", new Vector3(1.1f, 1.45f, -5.6f), new Vector2(0.45f, 0.45f), 2f, 2f, MathF.PI * 0.5f, alpha);
			round.time = 2f;
			round.minVertexDistance = 0.15f;
			round.widthMultiplier = 0.12f;
			round.numCornerVertices = 8;
			round.numCapVertices = 8;
			round.colorGradient = Gradient.Linear(new Color4(1f, 0.6f, 0.1f, 1f), new Color4(1f, 0.1f, 0.1f, 0.3f));
			round.GetComponent<TrailMover>().EmitToggle = 1.5f;

			var opaque = new Material(particles);
			opaque.SetFloat("BlendMode", 5f);
			TrailRenderer ribbon = TrailProp(scene, "TrailRibbon", new Vector3(0f, 2.15f, -5.2f), new Vector2(1.2f, 0.15f), 1.1f, 3.3f, 0f, opaque);
			ribbon.alignment = LineAlignment.TransformZ;
			ribbon.time = 1.5f;
			ribbon.minVertexDistance = 0.03f;
			ribbon.widthMultiplier = 0.2f;
			ribbon.colorGradient = Gradient.Linear(new Color4(1f, 0.9f, 0.1f, 1f), new Color4(0.8f, 0.1f, 0.9f, 1f));
			ribbon.GetComponent<TrailMover>().SpinSpeed = 120f;

			var checker = new Material(particles);
			checker.SetTexture("BaseMap", Checker(32));
			TrailRenderer tiled = TrailProp(scene, "TrailTiled", new Vector3(0f, 0.85f, -5.2f), new Vector2(1.3f, 0.2f), 0.9f, 1.8f, 0f, checker);
			tiled.time = 1.5f;
			tiled.minVertexDistance = 0.03f;
			tiled.widthMultiplier = 0.15f;
			tiled.textureMode = LineTextureMode.Tile;
			tiled.textureScale = new Vector2(2f, 1f);

			// Soft particles: two additive glows half sunk into a slab; the left one fades out towards the slab (near 0,
			// far 0.3 as Unity's legacy InvFade 3.33), the right one is cut off along a hard line.
			Box(scene, "SoftParticlesSlab", "LitRed.material", new Vector3(0f, 0.45f, -5f), new Vector3(0.9f, 0.025f, 0.4f));
			var soft = new Material(particles);
			soft.SetTexture("BaseMap", LELEngine.VFX.VfxDefaults.SoftDisc);
			soft.SetColor("Color", new Color4(1f, 0.55f, 0.15f, 1f));
			soft.SetFloat("BlendMode", 2f);
			soft.SetFloat("SoftParticles", 1f);
			soft.SetFloat("SoftParticlesFarFadeDistance", 0.3f);
			Material hard = soft.Clone();
			hard.SetFloat("SoftParticles", 0f);
			// The quad faces the player's start, so +X is on the screen's left.
			RefractionQuad(scene, "SoftParticlesOn", soft, new Vector3(0.35f, 0.45f, -5f), 0.25f);
			RefractionQuad(scene, "SoftParticlesOff", hard, new Vector3(-0.35f, 0.45f, -5f), 0.25f);
		}

		// A small sphere moved by a TrailMover, with a trail of the given material.
		private static TrailRenderer TrailProp(Scene scene, string name, Vector3 center, Vector2 size, float frequencyX, float frequencyY, float phase, Material material)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Sphere, name);
			go.transform.position = center;
			go.transform.scale = Vector3.One * 0.04f;
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.ContributesToGI = false;
			TrailMover mover = go.AddComponent<TrailMover>();
			mover.Center = center;
			mover.Size = size;
			mover.FrequencyX = frequencyX;
			mover.FrequencyY = frequencyY;
			mover.Phase = phase;
			TrailRenderer trail = go.AddComponent<TrailRenderer>();
			trail.SetMaterials(material);
			return trail;
		}

		// White, opaque in the middle row and fading to transparent at the top and bottom (across a trail).
		private static Texture2D SoftBand(int height)
		{
			const int width = 4;
			var pixels = new byte[width * height * 4];
			for (int y = 0; y < height; y++)
			{
				float d = MathF.Abs((y + 0.5f) / height * 2f - 1f);
				float a = Math.Clamp(1f - d, 0f, 1f);
				a = a * a * (3f - 2f * a);
				for (int x = 0; x < width; x++)
				{
					int i = (y * width + x) * 4;
					pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
					pixels[i + 3] = (byte)(a * 255f + 0.5f);
				}
			}

			return new Texture2D(width, height, pixels, "trail-soft-band", true);
		}

		// Two by two checks of white and dark gray.
		private static Texture2D Checker(int size)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					byte value = (x < size / 2) == (y < size / 2) ? (byte)255 : (byte)40;
					int i = (y * size + x) * 4;
					pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
					pixels[i + 3] = 255;
				}
			}

			return new Texture2D(size, size, pixels, "trail-checker");
		}

		// A built-in quad (2 x 2 in the model's YZ plane) facing the player's start.
		private static void RefractionQuad(Scene scene, string name, Material material, Vector3 position, float halfSize)
		{
			GameObject quad = SceneModels.Instantiate(scene, "Engine/Meshes/Quad.obj", name);
			quad.transform.position = position;
			quad.transform.rotation = QuaternionHelper.Euler(0f, 90f, 0f);
			quad.transform.scale = Vector3.One * halfSize;
			MeshRenderer renderer = quad.GetComponent<MeshRenderer>();
			renderer.SetMaterials(material);
			renderer.ContributesToGI = false;
		}

		/// <summary>
		///     Engine/LitTextured.shader props (materials=1) in front of the player's start, one feature each, left to right:
		///     the Scene workflow with a normal map (the lighting it always had), a smooth gold metal and a smooth red
		///     dielectric of the Metallic workflow, a mask map over a checker tiled 2 x 2 with a normal map (metal on the
		///     left half of each tile, occlusion grooves, smoothness rising upwards), the Specular workflow with a copper
		///     checker specular map, and an emission map with its own tiling (4 x 1). Behind them a rough dielectric wall
		///     piece (wide dim highlight) and, on the right, a panel yellow above and blue below with its upper half glowing:
		///     the GI captures must read it the same way up (cacheview=1, voxels=1).
		/// </summary>
		private static void BuildMaterials(Scene scene)
		{
			ShaderProgram litTextured = InternalStorage.GetOrCreateShader("Engine/LitTextured.shader");
			Texture2D bumps = BumpNormalMap(256, 6, 0.35f);
			Texture2D checker = Checker(256, 8, new Color4(0.8f, 0.8f, 0.8f, 1f), new Color4(0.2f, 0.2f, 0.22f, 1f));
			const float y = 1.45f;
			const float z = -5.9f;

			var sceneWorkflow = new Material(litTextured);
			sceneWorkflow.SetTexture("DiffuseMap", checker);
			sceneWorkflow.SetTexture("NormalMap", bumps);
			sceneWorkflow.SetFloat("Roughness", 0.4f);
			MaterialSphere(scene, "SceneWorkflow", sceneWorkflow, new Vector3(1.55f, y, z));

			var gold = new Material(litTextured);
			gold.SetFloat("Workflow", 1f);
			gold.SetColor("Color", new Color4(1f, 0.77f, 0.34f, 1f));
			gold.SetFloat("Metallic", 1f);
			gold.SetFloat("Roughness", 0.2f);
			MaterialSphere(scene, "Gold", gold, new Vector3(0.93f, y, z));

			var plastic = new Material(litTextured);
			plastic.SetFloat("Workflow", 1f);
			plastic.SetColor("Color", new Color4(0.6f, 0.05f, 0.04f, 1f));
			plastic.SetFloat("Roughness", 0.2f);
			MaterialSphere(scene, "RedPlastic", plastic, new Vector3(0.31f, y, z));

			var masked = new Material(litTextured);
			masked.SetFloat("Workflow", 1f);
			masked.SetTexture("DiffuseMap", checker);
			masked.SetVector("DiffuseMap_ST", new Vector4(2f, 2f, 0f, 0f));
			masked.SetTexture("NormalMap", bumps);
			masked.SetTexture("MaskMap", MaskMap(256));
			MaterialSphere(scene, "MaskMap", masked, new Vector3(-0.31f, y, z));

			var copper = new Material(litTextured);
			copper.SetFloat("Workflow", 2f);
			copper.SetColor("Color", new Color4(0.1f, 0.1f, 0.1f, 1f));
			copper.SetColor("SpecularColor", Color4.White);
			copper.SetTexture("SpecularMap", Checker(256, 4, new Color4(0.95f, 0.64f, 0.54f, 1f), new Color4(0.04f, 0.04f, 0.04f, 1f)));
			copper.SetFloat("Roughness", 0.3f);
			MaterialSphere(scene, "SpecularMap", copper, new Vector3(-0.93f, y, z));

			var glow = new Material(litTextured);
			glow.SetTexture("DiffuseMap", checker);
			glow.SetColor("Emissive", new Color4(0.2f, 0.8f, 1f, 3f));
			glow.SetTexture("EmissionMap", Stripes(128));
			glow.SetVector("EmissionMap_ST", new Vector4(4f, 1f, 0f, 0f));
			MaterialSphere(scene, "EmissionMap", glow, new Vector3(-1.55f, y, z));

			var rough = new Material(litTextured);
			rough.SetFloat("Workflow", 1f);
			rough.SetTexture("DiffuseMap", checker);
			rough.SetFloat("Roughness", 0.85f);
			Box(scene, "RoughBlock", "LitGray.material", new Vector3(0f, 0.75f, -4.6f), new Vector3(1.8f, 0.25f, 0.2f)).GetComponent<MeshRenderer>().SetMaterials(rough);

			var panel = new Material(litTextured);
			panel.SetTexture("DiffuseMap", TwoTone(64, new Color4(1f, 0.85f, 0.1f, 1f), new Color4(0.1f, 0.25f, 1f, 1f)));
			panel.SetColor("Emissive", new Color4(1f, 0.85f, 0.1f, 2f));
			panel.SetTexture("EmissionMap", TwoTone(64, Color4.White, Color4.Black));
			Box(scene, "VPanel", "LitGray.material", new Vector3(-2.4f, 1f, -4.6f), new Vector3(0.6f, 0.6f, 0.05f)).GetComponent<MeshRenderer>().SetMaterials(panel);
		}

		private static void MaterialSphere(Scene scene, string name, Material material, Vector3 position)
		{
			GameObject sphere = scene.CreateGameObject(name);
			sphere.transform.position = position;
			sphere.transform.scale = Vector3.One * 0.28f;
			MeshRenderer renderer = sphere.AddComponent<MeshRenderer>();
			renderer.SetMesh(UVSphere());
			renderer.SetMaterials(material);
			renderer.IsStatic = markStatic;
		}

		private static Mesh uvSphere;

		// A smooth UV sphere of radius 1 (the harness spheres are icospheres of 42 vertices): u once around (to the right
		// seen from outside), v from the lower pole up, tangents from the UV layout.
		private static Mesh UVSphere()
		{
			if (uvSphere != null)
			{
				return uvSphere;
			}

			const int segments = 64;
			const int rings = 32;
			var vertices = new System.Collections.Generic.List<Vertex>();
			for (int r = 0; r <= rings; r++)
			{
				float v = (float)r / rings;
				float latitude = MathF.PI * (v - 0.5f);
				for (int s = 0; s <= segments; s++)
				{
					float u = (float)s / segments;
					float longitude = 2f * MathF.PI * u;
					var normal = new Vector3(MathF.Cos(latitude) * MathF.Cos(longitude), MathF.Sin(latitude), MathF.Cos(latitude) * MathF.Sin(longitude));
					vertices.Add(new Vertex(normal, normal, new Vector2(1f - u, v), Vector3.Zero, Vector3.Zero));
				}
			}

			var indices = new System.Collections.Generic.List<uint>();
			for (int r = 0; r < rings; r++)
			{
				for (int s = 0; s < segments; s++)
				{
					uint a = (uint)(r * (segments + 1) + s);
					uint b = a + segments + 1;
					indices.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
				}
			}

			uint[] triangles = indices.ToArray();
			uvSphere = new Mesh("UVSphere", Mesh.WithTangents(vertices.ToArray(), triangles), triangles);
			return uvSphere;
		}

		// An sRGB checker of cells x cells squares.
		private static Texture2D Checker(int size, int cells, Color4 a, Color4 b)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					bool odd = ((x * cells / size) + (y * cells / size)) % 2 == 1;
					WriteColor(pixels, (y * size + x) * 4, odd ? b : a);
				}
			}

			return new Texture2D(size, size, pixels, "checker", true, true);
		}

		// A mask map (linear): r metallic on the left half, g occlusion grooves along the edges of 4 x 4 cells, a smoothness
		// from 0.2 at the bottom to 0.95 at the top.
		private static Texture2D MaskMap(int size)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float u = (x + 0.5f) / size;
					float v = (y + 0.5f) / size;
					float cellU = u * 4f - MathF.Floor(u * 4f);
					float cellV = v * 4f - MathF.Floor(v * 4f);
					float edge = MathF.Min(MathF.Min(cellU, 1f - cellU), MathF.Min(cellV, 1f - cellV));
					float occlusion = Math.Clamp(edge * 12f, 0.15f, 1f);
					int i = (y * size + x) * 4;
					pixels[i] = u < 0.5f ? (byte)255 : (byte)0;
					pixels[i + 1] = (byte)MathF.Round(occlusion * 255f);
					pixels[i + 2] = 0;
					pixels[i + 3] = (byte)MathF.Round((0.2f + 0.75f * v) * 255f);
				}
			}

			return new Texture2D(size, size, pixels, "mask", true);
		}

		// Two lit bands across u (an emission map: white bands on black).
		private static Texture2D Stripes(int size)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					WriteColor(pixels, (y * size + x) * 4, x * 4 / size % 2 == 0 ? Color4.White : Color4.Black);
				}
			}

			return new Texture2D(size, size, pixels, "stripes", true, true);
		}

		// The upper half of the image (v >= 0.5) one colour, the lower half another.
		private static Texture2D TwoTone(int size, Color4 upper, Color4 lower)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					WriteColor(pixels, (y * size + x) * 4, y >= size / 2 ? upper : lower);
				}
			}

			return new Texture2D(size, size, pixels, "two-tone", true, true);
		}

		private static void WriteColor(byte[] pixels, int index, Color4 color)
		{
			pixels[index] = (byte)MathF.Round(Math.Clamp(color.R, 0f, 1f) * 255f);
			pixels[index + 1] = (byte)MathF.Round(Math.Clamp(color.G, 0f, 1f) * 255f);
			pixels[index + 2] = (byte)MathF.Round(Math.Clamp(color.B, 0f, 1f) * 255f);
			pixels[index + 3] = 255;
		}

		// A tangent-space normal map of round bumps, cells x cells per tile; tilt is the slope scale.
		private static Texture2D BumpNormalMap(int size, int cells, float tilt)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float u = (x + 0.5f) / size * cells;
					float v = (y + 0.5f) / size * cells;
					float fx = u - MathF.Floor(u) - 0.5f;
					float fy = v - MathF.Floor(v) - 0.5f;
					float r = MathF.Sqrt(fx * fx + fy * fy);
					// Height cos^2(pi r) inside the cell's disc: slope -pi sin(2 pi r) along the radius.
					float slope = r < 0.5f && r > 1e-5f ? -MathF.PI * MathF.Sin(2f * MathF.PI * r) * tilt / r : 0f;
					WriteNormal(pixels, (y * size + x) * 4, -slope * fx, -slope * fy);
				}
			}

			return new Texture2D(size, size, pixels, "refraction-bumps", true);
		}

		// A tangent-space normal map of one round wave around the centre (a shockwave ring).
		private static Texture2D RingNormalMap(int size)
		{
			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float fx = (x + 0.5f) / size - 0.5f;
					float fy = (y + 0.5f) / size - 0.5f;
					float r = MathF.Sqrt(fx * fx + fy * fy);
					// Height exp(-((r - 0.3) / 0.1)^2): its slope along the radius, scaled to tilts up to ~0.6.
					float d = (r - 0.3f) / 0.1f;
					float slope = r > 1e-5f ? -2f * d / 0.1f * MathF.Exp(-d * d) * 0.07f / r : 0f;
					WriteNormal(pixels, (y * size + x) * 4, -slope * fx, -slope * fy);
				}
			}

			return new Texture2D(size, size, pixels, "refraction-ring", true);
		}

		private static void WriteNormal(byte[] pixels, int index, float nx, float ny)
		{
			var normal = new Vector3(nx, ny, 1f).Normalized();
			pixels[index] = (byte)Math.Clamp((int)MathF.Round((normal.X * 0.5f + 0.5f) * 255f), 0, 255);
			pixels[index + 1] = (byte)Math.Clamp((int)MathF.Round((normal.Y * 0.5f + 0.5f) * 255f), 0, 255);
			pixels[index + 2] = (byte)Math.Clamp((int)MathF.Round((normal.Z * 0.5f + 0.5f) * 255f), 0, 255);
			pixels[index + 3] = 255;
		}

		private static void BuildLight(Scene scene)
		{
			GameObject light = scene.CreateGameObject("Sun");
			Vector3 lightDirection = -SunDirection;
			light.transform.rotation = QuaternionHelper.LookRotation(lightDirection, Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			sun.Strength = 1.25f;

			// Visible sun disc far along the opposite of the light direction. Not a shadow caster, not voxelized.
			GameObject sunDisc = SceneModels.Instantiate(scene, SceneModels.Sphere, "SunDisc");
			sunDisc.transform.position = -lightDirection * 300f;
			sunDisc.transform.scale = Vector3.One * 12f;
			MeshRenderer sunRenderer = sunDisc.GetComponent<MeshRenderer>();
			sunRenderer.SetMaterials(SceneModels.Material("Sun.material"));
			sunRenderer.CastShadows = false;
			sunRenderer.ReceiveShadows = false;
			sunRenderer.ContributesToGI = false;
			sunRenderer.ReceiveGI = false;
		}

		private static void BuildEmitters(Scene scene, bool animate)
		{
			// Three colored orbs circling under the ceiling; their bounce light should tint ceiling and floor.
			Vector3 orbitCenter = new Vector3(0f, 0f, 3f);
			Color4[] colors = { new Color4(1f, 0.25f, 0.1f, 1f), new Color4(0.2f, 1f, 0.3f, 1f), new Color4(0.25f, 0.4f, 1f, 1f) };
			EmissiveOrbiter[] emitters = new EmissiveOrbiter[colors.Length + 2];
			for (int i = 0; i < colors.Length; i++)
			{
				EmissiveOrbiter orbiter = Emitter(scene, "Orb" + i, SceneModels.Sphere, Vector3.One * 0.6f);
				orbiter.Center = orbitCenter;
				orbiter.Radius = 6f;
				orbiter.Height = 1.4f;
				orbiter.Bobbing = 0.4f;
				orbiter.AngularSpeed = 0.5f;
				orbiter.PhaseOffset = i * MathHelper.TwoPi / colors.Length;
				orbiter.Color = colors[i];
				// Radiance units: a sunlit white surface is ~1, so a few times that reads as a lamp.
				orbiter.Intensity = 4f;
				orbiter.PulseSpeed = 1.5f + i * 0.4f;
				orbiter.PulseAmount = 0.4f;
				emitters[i] = orbiter;
			}

			// Static ceiling lamp panel, slowly breathing.
			EmissiveOrbiter lamp = Emitter(scene, "CeilingLamp", SceneModels.Cube, new Vector3(2f, 0.08f, 1f));
			lamp.Center = new Vector3(0f, WallHeight - WallThickness - 0.1f, 5f);
			lamp.Radius = 0f;
			lamp.Height = 0f;
			lamp.Bobbing = 0f;
			lamp.Color = new Color4(1f, 0.92f, 0.8f, 1f);
			lamp.Intensity = 2.5f;
			lamp.PulseSpeed = 0.7f;
			lamp.PulseAmount = 0.6f;
			emitters[colors.Length] = lamp;

			// Low wall-washer strip along the back wall.
			EmissiveOrbiter strip = Emitter(scene, "FloorStrip", SceneModels.Cube, new Vector3(6f, 0.06f, 0.15f));
			strip.Center = new Vector3(0f, 0.1f, RoomHalfSize - 0.6f);
			strip.Radius = 0f;
			strip.Height = 0f;
			strip.Bobbing = 0f;
			strip.Color = new Color4(0.3f, 0.9f, 1f, 1f);
			strip.Intensity = 3f;
			strip.PulseSpeed = 1.1f;
			strip.PulseAmount = 0.5f;
			emitters[colors.Length + 1] = strip;

			if (!animate)
			{
				// Frozen at their phase-0 positions and full intensity: a static scene with emissive objects.
				foreach (EmissiveOrbiter emitter in emitters)
				{
					emitter.AngularSpeed = 0f;
					emitter.Bobbing = 0f;
					emitter.PulseAmount = 0f;
				}
			}
		}

		/// <summary>The FPS player with the camera and the debug controls at the usual start (also used by the other test scenes).</summary>
		internal static void BuildPlayer(Scene scene)
		{
			GameObject player = scene.CreateGameObject("Player");
			player.transform.position = new Vector3(0f, 1.7f, -8f);
			player.transform.rotation = Quaternion.Identity;

			GameObject cameraObject = scene.CreateGameObject("MainCamera");
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 75f;
			camera.NearClip = 0.05f;
			camera.FarClip = 500f;
			cameraObject.transform.SetParent(player.transform);
			cameraObject.transform.localPosition = Vector3.Zero;
			cameraObject.transform.localRotation = Quaternion.Identity;

			FpsController controller = player.AddComponent<FpsController>();
			controller.CameraTransform = cameraObject.transform;
			controller.EyeHeight = 1.7f;

			player.AddComponent<GIDebugControls>();
		}

		/// <summary>A grid of small cubes on the floor; they stay out of the GI, so only the draw cost grows.</summary>
		private static void BuildStress(Scene scene, int count)
		{
			int side = (int)System.Math.Ceiling(System.Math.Sqrt(count));
			float spacing = RoomHalfSize * 1.8f / side;
			for (int i = 0; i < count; i++)
			{
				GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, "Stress" + i);
				go.transform.position = new Vector3(-RoomHalfSize * 0.9f + (i % side + 0.5f) * spacing, 0.06f, -RoomHalfSize * 0.9f + (i / side + 0.5f) * spacing);
				go.transform.scale = new Vector3(0.06f);

				MeshRenderer renderer = go.GetComponent<MeshRenderer>();
				renderer.SetMaterials(SceneModels.Material("LitGray.material"));
				renderer.ContributesToGI = false;
			}
		}

		private static GameObject Box(Scene scene, string name, string material, Vector3 center, Vector3 halfSize)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, name);
			go.transform.position = center;
			go.transform.scale = halfSize;

			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material(material));
			renderer.IsStatic = markStatic;
			return go;
		}

		private static EmissiveOrbiter Emitter(Scene scene, string name, string model, Vector3 halfSize)
		{
			GameObject go = SceneModels.Instantiate(scene, model, name);
			go.transform.scale = halfSize;

			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material("Emitter.material"));
			renderer.CastShadows = false;

			return go.AddComponent<EmissiveOrbiter>();
		}

		#endregion
	}
}
