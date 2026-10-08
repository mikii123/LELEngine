using System;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Scene-wide lighting parameters and the uniforms every lit shader receives.
	/// </summary>
	public sealed class Lighting
	{
		#region PublicFields

		public static LightProperties Directional = new LightProperties("LDirectional.dirColor", "LDirectional.dirStrength", "LDirectional.dirDirection");
		public static LightProperties Ambient = new LightProperties("LAmbient.ambColor", "LAmbient.ambStrength");
		public static LightProperties Specular = new LightProperties("LSpecular.viewPos");

		/// <summary>Live settings of the active scene (see <see cref="SceneSettings.Activate" />).</summary>
		public static EnvironmentSettings Environment = new EnvironmentSettings();

		public static ShadowSettings Shadows = new ShadowSettings();
		public static GlobalIlluminationSettings GI = new GlobalIlluminationSettings();

		/// <summary>Texture unit reserved for the directional shadow map (material textures use 0..N).</summary>
		public const int ShadowMapTextureUnit = 15;

		/// <summary>Texture unit reserved for the voxel radiance volume.</summary>
		public const int VoxelTextureUnit = 14;

		/// <summary>Texture units reserved for the screen-space GI resolve buffers.</summary>
		public const int IndirectDiffuseTextureUnit = 13;
		public const int IndirectSpecularTextureUnit = 12;

		/// <summary>Texture unit reserved for the global signed distance field.</summary>
		public const int GlobalSdfTextureUnit = 11;

		/// <summary>Texture unit reserved for the global distance field's object-id volume.</summary>
		public const int GlobalSdfObjectIdTextureUnit = 8;

		/// <summary>Texture unit reserved for the G-buffer normals of the GI resolve's upsampling.</summary>
		public const int ResolveNormalTextureUnit = 9;

		/// <summary>World -> light clip space. Written by the shadow pass every frame.</summary>
		public static Matrix4 LightSpaceMatrix = Matrix4.Identity;

		/// <summary>Depth texture of the directional light, or null when shadows are off.</summary>
		public static RenderTexture ShadowMap;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Per-frame sync before rendering: the sun's color and strength from the scene's directional light, the
		///     environment into the uniform carriers and the renderer's clear color / exposure / tonemapping.
		/// </summary>
		public static void BeginFrame(RenderSettings settings)
		{
			DirectionalLight light = DirectionalLight.This;
			if (light != null)
			{
				Directional.Color = light.Color;
				Directional.Strength = light.Strength;
			}

			Environment.ApplyTo(settings);
		}

		public static void SetUniforms(ShaderProgram program)
		{
			SetUniforms(program, true);
		}

		public static void SetUniforms(ShaderProgram program, bool receiveShadows)
		{
			SetUniforms(program, receiveShadows, true);
		}

		public static void SetUniforms(ShaderProgram program, bool receiveShadows, bool receiveGI)
		{
			Camera camera = Camera.current ?? Camera.main;
			Vector3 camPos = camera != null ? camera.transform.position : Vector3.Zero;
			Vector3 lightDir = DirectionalLight.This != null ? DirectionalLight.This.transform.forward : -Vector3.UnitY;

			// Legacy names used by older shaders (PBR / black hole)
			program.SetVector3("lightPosition", lightDir);
			program.SetVector3("CamPosition", camPos);
			program.SetColor("lightColor", Directional.Color);

			program.SetColor(Ambient.ColorName, Ambient.Color);
			program.SetFloat(Ambient.StrengthName, Ambient.Strength);

			program.SetFloat(Directional.StrengthName, Directional.Strength);
			program.SetVector3(Directional.DirName, lightDir);
			program.SetColor(Directional.ColorName, Directional.Color);

			program.SetFloat(Specular.StrengthName, Specular.Strength);
			program.SetFloat(Specular.ShineName, Specular.Shine);
			program.SetVector3(Specular.DirName, camPos);

			SetShadowUniforms(program, receiveShadows);
			SetGIUniforms(program, receiveGI);
		}

		public static void SetGIUniforms(ShaderProgram program, bool receiveGI)
		{
			// Either pipeline must have produced something this frame: a voxel volume or resolved screen buffers.
			bool enabled = receiveGI && GI.Enabled && (GI.VoxelTexture != 0 || GI.ResolveActive);
			program.SetInt("giEnabled", enabled ? 1 : 0);
			if (!enabled)
			{
				return;
			}

			program.SetVector3("voxelGridMin", GI.GridMin);
			program.SetFloat("voxelGridSize", GI.GridSize);
			program.SetInt("voxelResolution", GI.Resolution);
			program.SetFloat("giDiffuseStrength", GI.DiffuseStrength);
			program.SetFloat("giSpecularStrength", GI.SpecularStrength);
			program.SetFloat("giOcclusionStrength", GI.OcclusionStrength);
			program.SetFloat("giConeMaxDistance", GI.ConeMaxDistance);
			program.SetTexture("VoxelRadiance", TextureTarget.Texture3D, GI.VoxelTexture, VoxelTextureUnit);

			program.SetVector3("giSkyRadiance", GI.SkyRadiance);
			bool sdf = GI.DistanceFieldEnabled && GI.GlobalSdf != 0;
			program.SetInt("giTraceMode", TraceModeUniform(sdf));
			program.SetFloat("giSdfDetailDistance", GI.SdfDetailDistance);
			if (sdf)
			{
				SetSdfUniforms(program);
			}

			bool resolve = GI.ResolveActive && GI.ResolvedDiffuse != 0;
			program.SetInt("giResolveMode", resolve ? 1 : 0);
			if (resolve)
			{
				program.SetVector2("giResolveSize", new Vector2(GI.ResolveWidth, GI.ResolveHeight));
				program.SetVector2("giScreenSize", new Vector2(GI.ScreenWidth, GI.ScreenHeight));
				program.SetTexture("IndirectDiffuse", TextureTarget.Texture2D, GI.ResolvedDiffuse, IndirectDiffuseTextureUnit);
				program.SetTexture("IndirectSpecular", TextureTarget.Texture2D, GI.ResolvedSpecular, IndirectSpecularTextureUnit);
				program.SetInt("giResolveNormals", GI.ResolvedNormals != 0 ? 1 : 0);				program.SetTexture("GINormalRoughness", TextureTarget.Texture2D, GI.ResolvedNormals, ResolveNormalTextureUnit);
			}
		}

		public static void SetShadowUniforms(ShaderProgram program, bool receiveShadows)
		{
			bool enabled = receiveShadows && Shadows.Enabled && ShadowMap != null;
			program.SetInt("shadowsEnabled", enabled ? 1 : 0);
			if (!enabled)
			{
				return;
			}

			program.SetMatrix4("lightSpaceMatrix", LightSpaceMatrix);
			program.SetFloat("shadowNormalBias", Shadows.NormalBias);
			program.SetFloat("shadowDepthBias", Shadows.DepthBias);
			program.SetTexture("ShadowMap", TextureTarget.Texture2D, ShadowMap.Handle, ShadowMapTextureUnit);
		}

		/// <summary>giTraceMode of Engine/VoxelConeTracingCore.glsl: 0 voxel cones, 1 SDF detail, 2 SDF cones (voxel cones without the field).</summary>
		public static int TraceModeUniform(bool sdfAvailable)
		{
			if (!sdfAvailable)
			{
				return 0;
			}

			switch (GI.TraceMode)
			{
				case GITraceMode.SdfDetail: return 1;
				case GITraceMode.SdfCones: return 2;
				default: return 0;
			}
		}

		/// <summary>
		///     Global distance field parameters for Engine/DistanceField.glsl. Shares the GI grid placement.
		/// </summary>
		public static void SetSdfUniforms(ShaderProgram program)
		{
			program.SetVector3("sdfGridMin", GI.GridMin);
			program.SetFloat("sdfGridSize", GI.GridSize);
			program.SetInt("sdfResolution", GI.SdfResolution);
			program.SetInt("sdfMaxSteps", GI.SdfMaxSteps);
			program.SetFloat("sdfMaxDistance", GI.SdfBandVoxels * GI.SdfVoxelSize);
			program.SetTexture("GlobalSdf", TextureTarget.Texture3D, GI.GlobalSdf, GlobalSdfTextureUnit);
			// Without the id volume the shaders fall back to testing every object (A/B switch, measurement).
			program.SetTexture("GlobalSdfObjectIds", TextureTarget.Texture3D, GI.SdfObjectIdLookup ? GI.GlobalSdfObjectIds : 0, GlobalSdfObjectIdTextureUnit);
		}

		#endregion

		#region NestedTypes

		public class LightProperties
		{
			#region PublicFields

			public string ColorName;
			public string StrengthName;
			public string ShineName;
			public string DirName;
			public string PosName;
			public Color4 Color = Color4.White;
			public Vector3 Color3;
			public float Strength;
			public float Shine;
			public Vector3 Direction;
			public Vector3 Position;

			#endregion

			#region Constructors

			public LightProperties(string viewPos)
			{
				StrengthName = "LSpecular.specStrength";
				ShineName = "LSpecular.specShine";
				DirName = viewPos;
			}

			public LightProperties(string colorName, string strengthName)
			{
				ColorName = colorName;
				StrengthName = strengthName;
			}

			public LightProperties(string colorName, string strengthName, string posName)
			{
				ColorName = colorName;
				StrengthName = strengthName;
				DirName = posName;
			}

			#endregion
		}

		#endregion
	}

	/// <summary>
	///     Directional shadow map configuration.
	/// </summary>
	[Serializable]
	public sealed class ShadowSettings
	{
		#region PublicFields

		public bool Enabled = true;

		/// <summary>Shadow map resolution (square).</summary>
		public int MapSize = 2048;

		/// <summary>Half extent of the orthographic shadow frustum around the camera, in world units.</summary>
		public float Distance = 60f;

		/// <summary>Half depth range of the shadow frustum along the light direction.</summary>
		public float DepthRange = 200f;

		/// <summary>Offset along the surface normal before projecting into the shadow map (world units).</summary>
		public float NormalBias = 0.05f;

		/// <summary>Constant depth bias in shadow-map depth units.</summary>
		public float DepthBias = 0.0005f;

		public float PolygonOffsetFactor = 2f;
		public float PolygonOffsetUnits = 4f;

		#endregion
	}

	/// <summary>
	///     Voxel cone tracing configuration (see VoxelGIPass) plus the runtime volume it publishes.
	///     Configuration fields are saved with the scene; runtime state is [NonSerialized].
	/// </summary>
	[Serializable]
	public sealed class GlobalIlluminationSettings
	{
		#region PublicFields

		public bool Enabled = true;

		/// <summary>Voxels per axis. Memory is Resolution^3 * 8 bytes (RGBA16F) plus mips.</summary>
		public int Resolution = 128;

		/// <summary>World-space edge length of the cubic voxel volume.</summary>
		public float GridSize = 32f;

		/// <summary>Volume center when <see cref="FollowCamera" /> is off.</summary>
		public Vector3 Center = Vector3.Zero;

		/// <summary>Center the volume on the camera (snapped to voxels) instead of <see cref="Center" />.</summary>
		public bool FollowCamera;

		public float DiffuseStrength = 1f;
		public float SpecularStrength = 1f;
		public float OcclusionStrength = 1f;

		/// <summary>Maximum cone length in world units. 0 uses the grid size.</summary>
		public float ConeMaxDistance;

		/// <summary>
		///     Trace cones once per reduced-resolution pixel (GIResolvePass) instead of per material fragment.
		///     Requires the geometry prepass. Roughly 1 / ResolveScale^2 fewer cone traces.
		/// </summary>
		public bool ScreenSpaceResolve = true;

		/// <summary>Resolution of the voxel GI resolve relative to the screen (0.5 = half width and height).</summary>
		public float ResolveScale = 0.5f;
		/// <summary>
		///     Resolution of the Lumen screen probe integration relative to the screen. 1 = per pixel, as in Lumen: at
		///     lower values detail smaller than two pixels (distant small objects) gets the blended lighting of its
		///     neighbours and turns into mush. Costs about 0.7 ms more than 0.5 on the Intel iGPU at 1280x720.
		/// </summary>
		public float ScreenProbeResolveScale = 1f;

		/// <summary>Re-voxelize dynamic (non-static) objects every N frames. 1 = every frame.</summary>
		public int DynamicUpdateInterval = 1;

		/// <summary>Recompute voxel lighting (sun, shadows, bounce) every N frames. 1 = every frame.</summary>
		public int LightingUpdateInterval = 1;

		/// <summary>Resolution of the shadow map covering the whole voxel volume, used when lighting voxels.</summary>
		public int GridShadowMapSize = 1024;

		/// <summary>
		///     Weight of indirect light fed back from the previous frame's radiance volume when lighting voxels.
		///     1 gives multi-bounce lighting converging over a few frames; 0 keeps a single bounce.
		/// </summary>
		public float BounceStrength = 1f;

		/// <summary>Cones traced per voxel for the bounce: 6 (hemisphere layout) or 1 (single wide cone, cheaper).</summary>
		public int BounceCones = 6;

		/// <summary>
		///     Incremented by <see cref="InvalidateStatic" /> (call it after moving or re-materialing a static
		///     renderer). Every pass with a static cache (voxel geometry, global SDF) rebuilds it when the version
		///     differs from the one it last built, so several renderers (editor views) each rebuild their own.
		///     Grid and resolution changes are detected by the passes themselves. Lighting changes never require a
		///     rebuild: light is injected every frame.
		/// </summary>
		[NonSerialized] public int StaticVersion;

		// ---- Which GI pipeline runs ----

		public GIMode Mode = GIMode.Lumen;

		// ---- Lumen surface cache (SurfaceCachePass) ----

		public int SurfaceCacheAtlasSize = 1024;

		/// <summary>Card texels per world meter.</summary>
		public int SurfaceCacheTexelsPerMeter = 6;

		public int SurfaceCacheMaxCardSize = 96;

		/// <summary>Kept for command-line compatibility; radiosity now traces 16 rays per 4x4-texel probe per frame.</summary>
		public int RadiosityRays = 16;

		/// <summary>Constant history weight of the radiosity probes per update when RadiosityMaxHistorySamples is 0.</summary>
		public float RadiosityBlend = 0.9f;

		/// <summary>
		///     Sample-count accumulation of the radiosity probes once the scene has been quiet for
		///     RadianceCacheIdleAfterFrames: the history weight grows with the samples held up to this cap (0 = the
		///     constant RadiosityBlend instead).
		/// </summary>
		public float RadiosityMaxHistorySamples = 64f;

		/// <summary>Cap of the radiosity probe history while the scene is changing or has just changed (short lag).</summary>
		public float RadiosityMovingHistorySamples = 16f;

		/// <summary>
		///     While the scene is quiet (see RadianceCacheIdleAfterFrames) only 1 / this of the radiosity probe rows
		///     is refreshed per frame, round robin. 1 = always refresh everything.
		/// </summary>
		public int RadiosityIdleDivisor = 4;

		/// <summary>
		///     Radiance cache lookups whose target is at least 4 probe spacings away skip the per-probe target
		///     visibility pass (parallax below a texel): a third fewer atlas fetches for far-field rays.
		/// </summary>
		public bool RadianceCacheFarShortcut = true;

		// ---- Lumen screen probe gather (ScreenProbeGatherPass) ----

		/// <summary>Pixels between screen probes.</summary>
		public int ProbeSpacing = 16;

		/// <summary>
		///     Adaptive probe placement (Lumen): extra probes where the uniform grid cannot be interpolated (thin
		///     geometry, silhouettes), placed hierarchically at ProbeSpacing / 2 and / 4 pixel steps.
		/// </summary>
		public bool ProbeAdaptivePlacement = true;

		/// <summary>Capacity of adaptive probes as a fraction of the uniform probe count.</summary>
		public float ProbeAdaptiveFraction = 0.5f;

		/// <summary>A candidate pixel gets an adaptive probe when its interpolation weight from existing probes is below this.</summary>
		public float ProbeAdaptiveMinCoverage = 0.05f;

		/// <summary>
		///     The finest adaptive placement level only visits screen tiles the coarser level flagged (a candidate
		///     below 0.5 coverage, or a finer candidate on another surface). Pays off in views dominated by large flat
		///     surfaces; in the test scene the flagging costs as much as it saves, so it is off by default.
		/// </summary>
		public bool ProbeAdaptiveFlaggedRefine = false;

		public bool ProbeSpatialFilter = true;

		/// <summary>Per-frame jitter of probe anchors and ray directions (needed for convergence; off = diagnostics).</summary>
		public bool ProbeJitter = true;

		/// <summary>
		///     How far a probe's anchor pixel moves inside its cell from frame to frame, as a fraction of the cell
		///     (1 = anywhere in the cell, 0 = always the cell centre). Lumen jitters the whole cell; here the
		///     default is off because the history a probe carries over then comes from the same surface point,
		///     which measured 2-3x less temporal variation in a static view. Set to 1 to average probe placement
		///     over time instead (thin geometry gets covered more often, lighting breathes more).
		/// </summary>
		public float ProbeAnchorJitter = 0f;

		/// <summary>Jitter ray directions inside their octahedral texel every frame.</summary>
		public bool ProbeDirectionJitter = true;

		/// <summary>
		///     Structured importance sampling: directions ranked by last frame's radiance times cosine; the 16 most
		///     important ones get 2x2 rays, the other 48 reuse their history. Ray budget stays 64 per probe.
		/// </summary>
		public bool ProbeImportanceSampling = true;

		/// <summary>Radius of the spatial probe filter, in probes (1 = 3x3 neighbourhood).</summary>
		public int ProbeFilterRadius = 1; // the filter kernel is fixed at 3x3 probes; kept for CLI compatibility

		/// <summary>
		///     A neighbour probe's sample is reused only when its hit point lies within this many degrees of the
		///     centre probe's own ray direction (Lumen's angle-error rejection).
		/// </summary>
		public float ProbeFilterMaxAngle = 10f;

		/// <summary>
		///     Temporal probe filter (Lumen's TemporalFilterProbes): weight of the reprojected previous radiance when a
		///     direction is retraced. Reduces the frame-to-frame colour noise of probes that see small bright surfaces.
		/// </summary>
		public float ProbeHistoryWeight = 0.5f;

		/// <summary>A probe's history is used only when the previous probe lay within this distance (world units).</summary>
		public float ProbeHistoryDistance = 0.3f;

		/// <summary>
		///     Sample-count accumulation of probe radiance: a texel's history weight is n / (n + 1) up to this many
		///     samples, so static lighting converges to a noise-free value; 0 falls back to the constant
		///     <see cref="ProbeHistoryWeight" />. Probes whose lighting changed restart their counts (see below).
		/// </summary>
		public float ProbeMaxHistorySamples = 32f;

		/// <summary>
		///     Relative change of a probe's summed traced radiance versus its history that counts as a lighting
		///     change and restarts its accumulation (the change detector in ProbeCompose.shader).
		/// </summary>
		public float ProbeChangeThreshold = 0.5f;

		/// <summary>History weight of the per-pixel temporal accumulation (0 = off).</summary>
		public float ProbeTemporalBlend = 0.9f;

		/// <summary>Length of the depth-buffer march at the start of every probe ray (world units, 0 = off).</summary>
		public float ScreenTraceDistance = 1.5f;

		public int ScreenTraceSteps = 16;

		/// <summary>Depth-buffer surfaces are assumed this thick when deciding whether a ray passed behind them.</summary>
		public float ScreenTraceThickness = 0.3f;

		/// <summary>Maximum plane distance (world units) for a probe to be used by a pixel or a neighbour.</summary>
		public float ProbePlaneTolerance = 0.3f;

		// ---- Lumen world-space radiance cache (RadianceCachePass) ----

		public bool RadianceCacheEnabled = true;

		/// <summary>Probes per axis over the GI grid; probe spacing is GridSize / (n - 1).</summary>
		public int RadianceCacheProbesPerAxis = 17;

		/// <summary>Stored octahedral map resolution per probe: 8 or 16 (Lumen stores 16).</summary>
		public int RadianceCacheProbeResolution = 16;

		/// <summary>
		///     Rays per axis traced per probe update, a multiple of 16 and of the stored resolution (Lumen traces
		///     32x32). The rays are averaged into the stored texels, so 16 -> 8 gives 4 rays per texel per update.
		/// </summary>
		public int RadianceCacheTraceResolution = 32;

		/// <summary>
		///     Probes inside the static scene bounds re-traced per frame (round robin; all of them when the budget
		///     allows). Probes outside the scene get an eighth of this on top. Cost scales with this times the
		///     trace resolution squared; for a given ray budget and accumulation time the noise is the same
		///     however the budget is split, so fewer probes with more rays each is the cheaper option.
		/// </summary>
		public int RadianceCacheProbesPerFrame = 160;

		/// <summary>
		///     Time constant of a probe's accumulation, in frames: an update blends with weight exp(-dt / this), dt
		///     being the frames since the probe's previous update. Rarely updated probes therefore converge as fast
		///     as frequently updated ones. 0 = no accumulation.
		/// </summary>
		public float RadianceCacheHistoryFrames = 20f;

		/// <summary>
		///     Sample-count accumulation of the radiance cache probes: the history weight grows with the number of
		///     updates up to this cap (0 = the time-constant blend of RadianceCacheHistoryFrames instead).
		/// </summary>
		public float RadianceCacheMaxHistorySamples = 32f;

		/// <summary>
		///     Cap of the probe history until the grid has been swept RadianceCacheIdleAfterSweeps times since the last
		///     change: the cache then tracks the surface cache's convergence instead of averaging the transient in.
		/// </summary>
		public float RadianceCacheMovingHistorySamples = 4f;

		/// <summary>
		///     Relative change of a probe's summed radiance between its fresh estimate and its history that counts as a
		///     lighting change and restarts the probe's accumulation.
		/// </summary>
		public float RadianceCacheChangeThreshold = 0.25f;

		/// <summary>After this many frames without object, material or sun changes the probe budget drops to a trickle.</summary>
		public int RadianceCacheIdleAfterFrames = 60;

		/// <summary>Budget divisor while idle (converged probes only need an occasional refresh).</summary>
		public int RadianceCacheIdleDivisor = 8;

		/// <summary>
		///     Idle also requires this many full passes over the probe grid at the full budget since the last change:
		///     a quiet scene is not a converged one until the multi-bounce chain (cache -> cards -> cache) has settled.
		/// </summary>
		public int RadianceCacheIdleAfterSweeps = 40;

		/// <summary>
		///     Screen probe and radiosity rays trace the distance field this far (world units) and read the
		///     radiance cache beyond it. 0 traces the whole field.
		/// </summary>
		public float RadianceCacheNearDistance = 2f;

		/// <summary>Let the surface cache radiosity rays read the radiance cache too (off = radiosity traces fully).</summary>
		public bool RadianceCacheForRadiosity = true;

		// ---- Global distance field (GlobalDistanceFieldPass) ----

		public bool DistanceFieldEnabled = true;

		/// <summary>Texels per axis of the global distance field (shares the GI grid extent).</summary>
		public int SdfResolution = 128;

		/// <summary>Distances are clamped to this many SDF voxels; also bounds how far dynamic objects reach.</summary>
		public int SdfBandVoxels = 8;

		/// <summary>Maximum sphere tracing iterations per ray.</summary>
		public int SdfMaxSteps = 48;

		/// <summary>
		///     Identify the object at a ray hit from the distance field's object-id volume (one fetch) instead of
		///     testing every scene object. Off only for A/B measurements.
		/// </summary>
		public bool SdfObjectIdLookup = true;

		/// <summary>
		///     How voxel GI cones find geometry (see <see cref="GITraceMode" />). SDF cones are the default: voxel
		///     cones leak through walls about one voxel thick and give stepped corners, the SDF detail trace is
		///     binary per cone (banded corners). Falls back to voxel cones without the distance field.
		/// </summary>
		public GITraceMode TraceMode = GITraceMode.SdfCones;

		/// <summary>Length of the SDF detail trace at the start of every cone (world units).</summary>
		public float SdfDetailDistance = 1.5f;

		/// <summary>Radiance from outside the scene seen by cones that escape (linear RGB).</summary>
		public Vector3 SkyRadiance = Vector3.Zero;

		// ---- Runtime data written by VoxelGIPass / GIResolvePass ----

		/// <summary>GL handle of the 3D radiance texture, 0 when unavailable.</summary>
		[NonSerialized] public int VoxelTexture;

		/// <summary>World-space minimum corner of the volume for the current frame.</summary>
		[NonSerialized] public Vector3 GridMin;

		/// <summary>GL handle of the global distance field texture, 0 when unavailable.</summary>
		[NonSerialized] public int GlobalSdf;

		/// <summary>GL handle of the matching object-id volume (R16UI, index + 1 of the nearest object), 0 when unavailable.</summary>
		[NonSerialized] public int GlobalSdfObjectIds;

		[NonSerialized] public bool ResolveActive;
		[NonSerialized] public int ResolvedDiffuse;
		[NonSerialized] public int ResolvedSpecular;

		/// <summary>Full-resolution G-buffer normals the resolve was computed with (normal-aware upsampling).</summary>
		[NonSerialized] public int ResolvedNormals;
		[NonSerialized] public int ResolveWidth;
		[NonSerialized] public int ResolveHeight;
		[NonSerialized] public int ScreenWidth;
		[NonSerialized] public int ScreenHeight;

		#endregion

		#region PublicMethods

		public float VoxelSize => GridSize / Resolution;
		public float SdfVoxelSize => GridSize / SdfResolution;

		public void InvalidateStatic()
		{
			StaticVersion++;
		}

		/// <summary>
		///     Positions the GI grid for this frame: around <see cref="Center" />, or around the camera snapped
		///     to voxels when <see cref="FollowCamera" /> is set. Called by the volume passes.
		/// </summary>
		public void UpdateGridPlacement(Vector3 cameraPosition)
		{
			Vector3 center = Center;
			if (FollowCamera)
			{
				float voxel = VoxelSize;
				center = new Vector3(
					(float)Math.Floor(cameraPosition.X / voxel) * voxel,
					(float)Math.Floor(cameraPosition.Y / voxel) * voxel,
					(float)Math.Floor(cameraPosition.Z / voxel) * voxel);
			}

			GridMin = center - new Vector3(GridSize * 0.5f);
		}

		#endregion
	}

	public enum GIMode
	{
		/// <summary>Voxel cone tracing: radiance volume + cones (steps 1-3 of the roadmap).</summary>
		VoxelConeTracing,

		/// <summary>Lumen-style: surface cache cards lit with radiosity, screen probes traced through the global SDF.</summary>
		Lumen
	}

	public enum GITraceMode
	{
		/// <summary>Occlusion from the voxel alpha mip chain. Cheapest, leaks through thin geometry.</summary>
		VoxelCones,

		/// <summary>
		///     The first <see cref="GlobalIlluminationSettings.SdfDetailDistance" /> of each cone is sphere traced
		///     through the global distance field (radiance read from the voxel volume at the hit), then the
		///     voxel cone continues. Removes leaks through nearby thin geometry.
		/// </summary>
		SdfDetail,

		/// <summary>
		///     The whole cone's visibility from the global distance field (distance field AO: continuous, never
		///     through a wall); radiance from the voxel volume where the cone runs into surfaces. Smooth corners,
		///     no leaks through walls.
		/// </summary>
		SdfCones
	}
}
