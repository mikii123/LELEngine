using System;
using System.Reflection;
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

		/// <summary>
		///     Uniform every material receives with its lighting (vec4 lelTime, Unity's _Time): the game clock in seconds
		///     (<see cref="Time.timeD" />) / 20, x 1, x 2, x 3.
		/// </summary>
		public const string TimeName = "lelTime";

		/// <summary>Live settings of the active scene (see <see cref="SceneSettings.Activate" />).</summary>
		public static EnvironmentSettings Environment = new EnvironmentSettings();

		/// <summary>The active scene's shadow reach (see <see cref="SceneSettings.Activate" />).</summary>
		public static SceneShadowSettings SceneShadows = new SceneShadowSettings();

		/// <summary>The active scene's GI volume and strengths (see <see cref="SceneSettings.Activate" />).</summary>
		public static SceneGISettings SceneGI = new SceneGISettings();

		/// <summary>The shadow map: the project's quality settings with the active scene's reach.</summary>
		public static ShadowSettings Shadows => ProjectSettings.Current.Shadows;

		/// <summary>Global illumination: the project's method and tunables with the active scene's volume and strengths.</summary>
		public static GlobalIlluminationSettings GI => ProjectSettings.Current.GI;

		/// <summary>
		///     The GI method that runs: the project's <see cref="GlobalIlluminationSettings.Mode" /> as it was when the GI
		///     first ran. It does not change while the game runs (a player, the editor's play mode): a later change of the
		///     setting is ignored with a warning. Only the editor changes it, in edit mode (<see cref="AllowGIMethodChange" />).
		///     Only this method's passes run and hold GPU resources; the other method's release theirs.
		/// </summary>
		public static GIMode GIMethod => giMethod ??= GI.Mode;

		/// <summary>
		///     Set by the editor while in edit mode: a change of <see cref="GlobalIlluminationSettings.Mode" /> becomes
		///     <see cref="GIMethod" /> at the next frame. Off in play mode and in players.
		/// </summary>
		internal static bool AllowGIMethodChange;

		/// <summary>
		///     Raised by <see cref="BeginFrame" /> when what the GI uses changed (the method, the scene's GI switch, the
		///     distance field, the screen-space resolve): every renderer frees what is no longer used at once, also one
		///     that does not draw this frame (a hidden editor view).
		/// </summary>
		internal static event Action GIUsageChanged;

		/// <summary>Texture unit reserved for the directional shadow map (material textures use 0..N).</summary>
		public const int ShadowMapTextureUnit = 15;

		/// <summary>Texture unit reserved for the shadow map's transmittance attachment (transparent casters).</summary>
		public const int ShadowTransmittanceTextureUnit = 10;

		/// <summary>Texture unit reserved for the voxel radiance volume.</summary>
		public const int VoxelTextureUnit = 14;

		/// <summary>Texture units reserved for the screen-space GI resolve buffers.</summary>
		public const int IndirectDiffuseTextureUnit = 13;
		public const int IndirectSpecularTextureUnit = 12;

		/// <summary>
		///     Texture units of the radiance cache atlas and its per-probe SH for the GI volume path of transparent
		///     materials (Engine/VolumeLighting.glsl). The same units as the resolve buffers: a draw reads one or the other.
		/// </summary>
		public const int VolumeProbeTextureUnit = 13;
		public const int VolumeProbeSHTextureUnit = 12;

		/// <summary>Texture unit reserved for the global signed distance field.</summary>
		public const int GlobalSdfTextureUnit = 11;

		/// <summary>Texture unit reserved for the global distance field's object-id volume.</summary>
		public const int GlobalSdfObjectIdTextureUnit = 8;

		/// <summary>Texture unit reserved for the G-buffer normals of the GI resolve's upsampling.</summary>
		public const int ResolveNormalTextureUnit = 9;

		/// <summary>
		///     Texture unit of the skybox cubemap the GI samples for rays that leave the scene. Above the 16 units every
		///     context has, so it never collides with a pass's own textures; bound only when the context has it.
		/// </summary>
		public const int SkyboxTextureUnit = 16;

		/// <summary>
		///     Texture unit of the radiance cache SH (a 2D array) for opaque materials lit from Lumen's probes (the cameras
		///     of a stack other than its lighting camera): not one of the resolve buffers' units, whose samplers are 2D (a
		///     unit must not hold samplers of two types). The atlas, 2D, shares <see cref="VolumeProbeTextureUnit" /> with
		///     the resolve's diffuse. Used only when the context has the unit.
		/// </summary>
		public const int OpaqueProbeSHTextureUnit = 17;

		/// <summary>
		///     First texture unit of the voxel radiance's directional mips (<see cref="GlobalIlluminationSettings.VoxelVolume" />):
		///     one unit for the anisotropic views, this and the next two for the SH gradients. Used only when the context
		///     has the units; otherwise the voxel GI reads isotropic mips.
		/// </summary>
		public const int VoxelDirectionalTextureUnit = 18;

		/// <summary>
		///     The view being drawn is its stack's lighting camera (<see cref="Camera.GetStackLightingCamera" />): the
		///     materials read the screen-space GI its view computed (the resolve buffers). The other cameras of a stack
		///     light from the GI volume: voxel cones per fragment, or Lumen's radiance cache probes. Set by the renderer
		///     for every camera it draws.
		/// </summary>
		public static bool ScreenGIView { get; internal set; } = true;

		/// <summary>The cascades of <see cref="ShadowMap" />. Written by the shadow pass every frame.</summary>
		public static readonly ShadowCascadeData ShadowCascades = new ShadowCascadeData();

		/// <summary>Depth texture of the directional light (an atlas of its cascades), or null when shadows are off.</summary>
		public static RenderTexture ShadowMap;

		/// <summary>
		///     Transmittance of the transparent casters in the directional light's map (see <see cref="Rendering.TransparentShadows" />),
		///     or null when the frame drew none.
		/// </summary>
		public static RenderTexture ShadowTransmittance;

		/// <summary>
		///     The Lumen radiance cache of the frame being rendered, for shaders that light from the GI volume
		///     (Engine/RadianceCache.glsl, Engine/VolumeProbes.glsl). Published by RadianceCachePass every frame; a
		///     default (invalid) binding while the cache is off.
		/// </summary>
		public static RadianceCacheBinding RadianceCache;

		#endregion

		#region PrivateFields

		private static readonly string[] ShadowMatrixNames = { "shadowMatrices[0]", "shadowMatrices[1]", "shadowMatrices[2]", "shadowMatrices[3]" };

		private static GIMode? giMethod;
		private static bool giMethodWarned;
		private static (GIMode, bool, bool, bool) giUsage;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Per-frame sync before rendering: the sun's color and strength from the scene's directional light, the
		///     environment into the uniform carriers and the renderer's clear color / exposure / tonemapping, the GI
		///     method (<see cref="GIMethod" />) and the GI's sky.
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
			UpdateGIMethod();
			(GIMode, bool, bool, bool) usage = (GIMethod, GI.Enabled, GI.DistanceFieldActive, GI.VoxelConeTracingActive && GI.ScreenSpaceResolve);
			if (usage != giUsage)
			{
				giUsage = usage;
				GIUsageChanged?.Invoke();
			}

			// The GI's sky (a resampled cubemap) exists only while the scene uses GI.
			if (GI.Enabled)
			{
				SkyEnvironment.Update(Environment.Skybox);
			}
			else
			{
				SkyEnvironment.Clear();
			}
		}

		/// <summary>
		///     The sky the GI shaders see (Engine/Sky.glsl): the constant sky color and, when the environment has a
		///     skybox material with a cubemap or panorama, that cubemap with its tint, exposure and rotation.
		/// </summary>
		public static void SetSkyUniforms(ShaderProgram program)
		{
			program.SetVector3("giSkyRadiance", GI.SkyRadiance);
			bool hasUnit = SkyboxTextureUnit < GLCapabilities.MaxTextureUnits;
			bool enabled = SkyEnvironment.Enabled && hasUnit;
			program.SetInt("giSkyboxEnabled", enabled ? 1 : 0);
			if (!hasUnit)
			{
				return;
			}

			program.SetTexture("giSkybox", TextureTarget.TextureCubeMap, enabled ? SkyEnvironment.CubemapHandle : SkyEnvironment.BlackCubemapHandle, SkyboxTextureUnit);
			if (enabled)
			{
				program.SetVector4("giSkyboxTint", SkyEnvironment.TintExposure);
				program.SetVector2("giSkyboxRotation", SkyEnvironment.Rotation);
				program.SetVector2("giSkyboxTilt", SkyEnvironment.Tilt);
				program.SetFloat("giSkyboxLod", SkyEnvironment.Lod);
				program.SetFloat("giSkyboxFaceSize", SkyEnvironment.FaceSize);
			}
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

			// Unity's _Time: the game clock (Time.timeD) / 20, x 1, x 2, x 3.
			float time = (float)Time.timeD;
			program.SetVector4(TimeName, new Vector4(time / 20f, time, time * 2f, time * 3f));

			SetShadowUniforms(program, receiveShadows);
			SetGIUniforms(program, receiveGI);
		}

		/// <summary>
		///     GI of an opaque material (Engine/VoxelConeTracing.glsl) for the view being drawn: the lighting camera reads
		///     its screen-space resolve (giResolveMode 1); the other cameras of a stack trace the voxel cones per fragment
		///     (0) or, with Lumen, read the radiance cache probes (2). Off when the GI is (no method produced anything).
		/// </summary>
		public static void SetGIUniforms(ShaderProgram program, bool receiveGI)
		{
			// The radiance cache SH is a 2D array: in an opaque material its sampler keeps a unit of its own even while
			// unused, never one a 2D texture is bound to.
			bool probeUnit = OpaqueProbeSHTextureUnit < GLCapabilities.MaxTextureUnits;
			bool opaqueMaterial = program.GetUniformLocation("giResolveMode") >= 0;
			if (probeUnit && opaqueMaterial)
			{
				program.SetInt("RadianceCacheSH", OpaqueProbeSHTextureUnit);
			}

			// What this view can read this frame: its screen-space resolve (the lighting camera), the voxel volume, or
			// the Lumen probes.
			bool resolve = ScreenGIView && GI.ResolveActive && GI.ResolvedDiffuse != 0;
			bool voxels = GIMethod == GIMode.VoxelConeTracing && GI.VoxelTexture != 0;
			bool probes = !resolve && GIMethod == GIMode.Lumen && RadianceCache.Valid && probeUnit && opaqueMaterial;
			bool enabled = receiveGI && GI.Enabled && (resolve || voxels || probes);
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
			SetVoxelRadianceUniforms(program, GI.VoxelVolume, VoxelTextureUnit, VoxelDirectionalTextureUnit);

			SetSkyUniforms(program);
			bool sdf = GI.DistanceFieldEnabled && GI.GlobalSdf != 0;
			program.SetInt("giTraceMode", TraceModeUniform(sdf));
			program.SetFloat("giSdfDetailDistance", GI.SdfDetailDistance);
			if (sdf)
			{
				SetSdfUniforms(program);
			}

			program.SetInt("giResolveMode", resolve ? 1 : probes ? 2 : 0);
			if (resolve)
			{
				program.SetVector2("giResolveSize", new Vector2(GI.ResolveWidth, GI.ResolveHeight));
				program.SetVector2("giScreenSize", new Vector2(GI.ScreenWidth, GI.ScreenHeight));
				program.SetTexture("IndirectDiffuse", TextureTarget.Texture2D, GI.ResolvedDiffuse, IndirectDiffuseTextureUnit);
				program.SetTexture("IndirectSpecular", TextureTarget.Texture2D, GI.ResolvedSpecular, IndirectSpecularTextureUnit);
				program.SetInt("giResolveNormals", GI.ResolvedNormals != 0 ? 1 : 0);				program.SetTexture("GINormalRoughness", TextureTarget.Texture2D, GI.ResolvedNormals, ResolveNormalTextureUnit);
			}
			else if (probes)
			{
				SetRadianceCacheUniforms(program, VolumeProbeTextureUnit);
				program.SetTexture("RadianceCacheSH", TextureTarget.Texture2DArray, RadianceCache.SH, OpaqueProbeSHTextureUnit);
			}
		}

		/// <summary>
		///     GI for surfaces that are not in the depth buffer (transparent materials, Engine/VolumeLighting.glsl): the
		///     screen-space resolve is never read; voxel cone tracing traces its cones per fragment, Lumen interpolates
		///     the radiance cache probes (irradiance from their spherical harmonics, reflections from their maps).
		///     Call after <see cref="SetUniforms(ShaderProgram, bool, bool)" /> with receiveGI false.
		/// </summary>
		public static void SetVolumeGIUniforms(ShaderProgram program, bool receiveGI)
		{
			bool voxels = GIMethod == GIMode.VoxelConeTracing && GI.VoxelTexture != 0;
			bool probes = GIMethod == GIMode.Lumen && RadianceCache.Valid;
			bool enabled = receiveGI && GI.Enabled && (voxels || probes);
			program.SetInt("giEnabled", enabled ? 1 : 0);
			program.SetInt("giResolveMode", 0);
			program.SetInt("giVolumeMode", !enabled ? 0 : voxels ? 1 : 2);
			if (!enabled)
			{
				return;
			}

			program.SetFloat("giDiffuseStrength", GI.DiffuseStrength);
			program.SetFloat("giSpecularStrength", GI.SpecularStrength);
			program.SetFloat("giOcclusionStrength", GI.OcclusionStrength);
			SetSkyUniforms(program);
			if (voxels)
			{
				program.SetVector3("voxelGridMin", GI.GridMin);
				program.SetFloat("voxelGridSize", GI.GridSize);
				program.SetInt("voxelResolution", GI.Resolution);
				program.SetFloat("giConeMaxDistance", GI.ConeMaxDistance);
				SetVoxelRadianceUniforms(program, GI.VoxelVolume, VoxelTextureUnit, VoxelDirectionalTextureUnit);
				bool sdf = GI.DistanceFieldEnabled && GI.GlobalSdf != 0;
				program.SetInt("giTraceMode", TraceModeUniform(sdf));
				program.SetFloat("giSdfDetailDistance", GI.SdfDetailDistance);
				if (sdf)
				{
					SetSdfUniforms(program);
				}
			}
			else
			{
				SetRadianceCacheUniforms(program, VolumeProbeTextureUnit);
				program.SetTexture("RadianceCacheSH", TextureTarget.Texture2DArray, RadianceCache.SH, VolumeProbeSHTextureUnit);
			}
		}

		/// <summary>
		///     Binds a voxel radiance volume for Engine/VoxelRadiance.glsl: the voxels (and their isotropic mips) on
		///     <paramref name="unit" />, the directional mips on <paramref name="directionalUnit" /> and up (one unit for
		///     the anisotropic views, three for the SH gradients). Samplers of data the volume does not have share the
		///     voxels' unit (same sampler type) and go unused. Mode falls back to isotropic when the units are missing.
		/// </summary>
		public static void SetVoxelRadianceUniforms(ShaderProgram program, VoxelVolumeTextures volume, int unit, int directionalUnit)
		{
			VoxelMipMode mode = EffectiveVoxelMips(volume, directionalUnit);
			program.SetTexture("VoxelRadiance", TextureTarget.Texture3D, volume.Radiance, unit);
			program.SetInt("giVoxelMips", (int)mode);
			if (mode == VoxelMipMode.Anisotropic)
			{
				program.SetInt("giVoxelAnisoLevels", volume.AnisoLevels);
				program.SetTexture("VoxelRadianceAniso", TextureTarget.Texture3D, volume.Aniso, directionalUnit);
			}
			else
			{
				program.SetInt("VoxelRadianceAniso", unit);
			}

			if (mode == VoxelMipMode.SphericalHarmonics)
			{
				program.SetTexture("VoxelRadianceSHX", TextureTarget.Texture3D, volume.SHX, directionalUnit);
				program.SetTexture("VoxelRadianceSHY", TextureTarget.Texture3D, volume.SHY, directionalUnit + 1);
				program.SetTexture("VoxelRadianceSHZ", TextureTarget.Texture3D, volume.SHZ, directionalUnit + 2);
			}
			else
			{
				program.SetInt("VoxelRadianceSHX", unit);
				program.SetInt("VoxelRadianceSHY", unit);
				program.SetInt("VoxelRadianceSHZ", unit);
			}
		}

		/// <summary>The mip mode a volume is read with when its directional data starts at <paramref name="directionalUnit" />.</summary>
		public static VoxelMipMode EffectiveVoxelMips(VoxelVolumeTextures volume, int directionalUnit)
		{
			switch (volume.Mode)
			{
				case VoxelMipMode.Anisotropic:
					return volume.Aniso != 0 && directionalUnit < GLCapabilities.MaxTextureUnits ? VoxelMipMode.Anisotropic : VoxelMipMode.Isotropic;
				case VoxelMipMode.SphericalHarmonics:
					return volume.SHX != 0 && directionalUnit + 2 < GLCapabilities.MaxTextureUnits ? VoxelMipMode.SphericalHarmonics : VoxelMipMode.Isotropic;
				default:
					return VoxelMipMode.Isotropic;
			}
		}

		/// <summary>Binds the published radiance cache for Engine/RadianceCache.glsl (an unavailable cache sets rcNearDistance 0).</summary>
		public static void SetRadianceCacheUniforms(ShaderProgram program, int textureUnit)
		{
			RadianceCacheBinding cache = RadianceCache;
			if (!cache.Valid)
			{
				program.SetFloat("rcNearDistance", 0f);
				return;
			}

			program.SetTexture("RadianceCacheAtlas", TextureTarget.Texture2D, cache.Atlas, textureUnit);
			program.SetVector3("rcGridMin", cache.GridMin);
			program.SetFloat("rcProbeSpacing", cache.ProbeSpacing);
			program.SetInt("rcProbesPerAxis", cache.ProbesPerAxis);
			program.SetInt("rcProbesPerRow", cache.ProbesPerRow);
			program.SetInt("rcProbeResolution", cache.ProbeResolution);
			program.SetVector2("rcAtlasSize", cache.AtlasSize);
			program.SetFloat("rcNearDistance", GI.RadianceCacheNearDistance);
			program.SetInt("rcFarShortcut", GI.RadianceCacheFarShortcut ? 1 : 0);
		}

		public static void SetShadowUniforms(ShaderProgram program, bool receiveShadows)
		{
			ShadowCascadeData cascades = ShadowCascades;
			bool enabled = receiveShadows && Shadows.Enabled && ShadowMap != null && cascades.Count > 0;
			program.SetInt("shadowsEnabled", enabled ? 1 : 0);
			if (!enabled)
			{
				return;
			}

			program.SetInt("shadowCascadeCount", cascades.Count);
			for (int i = 0; i < cascades.Count; i++)
			{
				program.SetMatrix4(ShadowMatrixNames[i], cascades.Matrices[i]);
			}

			program.SetVector4Array("shadowCascadeSpheres", cascades.Spheres);
			program.SetVector4("shadowFade", new Vector4(cascades.FadeCenter, cascades.FadeDistance));
			SetShadowBiasUniforms(program);
			program.SetTexture("ShadowMap", TextureTarget.Texture2D, ShadowMap.Handle, ShadowMapTextureUnit);
			TransparentShadows.SetUniforms(program, ShadowTransmittance, ShadowTransmittanceTextureUnit);
		}

		/// <summary>
		///     Binds one shadow map that covers a region on its own (the GI passes' grid-covering maps) for
		///     Engine/Shadows.glsl: a single cascade filling the map, no fade. The transmittance is bound separately.
		/// </summary>
		public static void SetShadowMapUniforms(ShaderProgram program, Matrix4 lightView, Matrix4 lightProjection, int depthTexture, int textureUnit)
		{
			program.SetInt("shadowsEnabled", Shadows.Enabled ? 1 : 0);
			program.SetInt("shadowCascadeCount", 1);
			program.SetMatrix4(ShadowMatrixNames[0], ShadowTextureMatrix(lightView, lightProjection, 0, 1, 1));
			program.SetVector4("shadowFade", Vector4.Zero);
			SetShadowBiasUniforms(program);
			program.SetTexture("ShadowMap", TextureTarget.Texture2D, depthTexture, textureUnit);
		}

		/// <summary>
		///     World position to shadow map texture space for Engine/Shadows.glsl: xy inside tile <paramref name="tile" />
		///     of an atlas of <paramref name="columns" /> x <paramref name="rows" /> tiles (row by row from the bottom
		///     left), z the depth 0..1.
		/// </summary>
		public static Matrix4 ShadowTextureMatrix(Matrix4 lightView, Matrix4 lightProjection, int tile, int columns, int rows)
		{
			float sx = 1f / columns;
			float sy = 1f / rows;
			float ox = tile % columns * sx;
			float oy = tile / columns * sy;
			Matrix4 clipToTexture = Matrix4.CreateScale(0.5f * sx, 0.5f * sy, 0.5f) * Matrix4.CreateTranslation(ox + 0.5f * sx, oy + 0.5f * sy, 0.5f);
			return lightView * lightProjection * clipToTexture;
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

		#region PrivateMethods

		private static void SetShadowBiasUniforms(ShaderProgram program)
		{
			program.SetFloat("shadowDepthBias", Shadows.DepthBiasTexels);
			program.SetFloat("shadowNormalBias", Shadows.NormalBiasTexels);
		}

		// A changed GI.Mode becomes the method only where the editor allows it (edit mode); while the game runs it is
		// reported once and ignored.
		private static void UpdateGIMethod()
		{
			if (GI.Mode == GIMethod)
			{
				giMethodWarned = false;
				return;
			}

			if (AllowGIMethodChange)
			{
				Console.WriteLine($"[GI] Method {GIMethod} -> {GI.Mode}");
				giMethod = GI.Mode;
				giMethodWarned = false;
				return;
			}

			if (!giMethodWarned)
			{
				giMethodWarned = true;
				Debug.LogWarning($"[GI] The GI method cannot change while the game runs: {GIMethod} stays, {GI.Mode} applies at the next start");
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     The camera shadow map's cascades this frame (see <see cref="ShadowCascades" />): each covers a slice of the
		///     view up to the shadow distance, rendered into its own tile of <see cref="ShadowMap" />.
		/// </summary>
		public sealed class ShadowCascadeData
		{
			public const int MaxCount = 4;

			/// <summary>Cascades in use, 0 when there is no shadow map this frame.</summary>
			public int Count;

			/// <summary>World -> the cascade's tile of the map (<see cref="ShadowTextureMatrix" />).</summary>
			public readonly Matrix4[] Matrices = new Matrix4[MaxCount];

			/// <summary>xyz = center, w = squared radius inside which a receiver uses the cascade (world units).</summary>
			public readonly Vector4[] Spheres = new Vector4[MaxCount];

			/// <summary>Shadows fade out toward <see cref="FadeDistance" /> from <see cref="FadeCenter" /> (the camera).</summary>
			public Vector3 FadeCenter;

			public float FadeDistance;
		}

		/// <summary>GL handles and layout of a frame's radiance cache (see <see cref="RadianceCache" />).</summary>
		public struct RadianceCacheBinding
		{
			public bool Valid;

			/// <summary>Octahedral radiance maps of the probes (rgb radiance, a = hit distance + 1).</summary>
			public int Atlas;

			/// <summary>Second-order spherical harmonics of every probe, 7 RGBA layers (Engine/RadianceCacheSH.shader).</summary>
			public int SH;

			public Vector3 GridMin;
			public float ProbeSpacing;
			public int ProbesPerAxis;
			public int ProbesPerRow;
			public int ProbeResolution;
			public Vector2 AtlasSize;
		}

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
	///     Directional shadow map quality, project-wide (part of <see cref="ProjectSettings" />); its reach comes
	///     from the active scene (<see cref="Distance" />, <see cref="DepthRange" />). The camera's map is split into
	///     cascades, as in Unity: each covers a slice of the view up to the shadow distance, the near ones small and
	///     therefore sharp.
	/// </summary>
	[Serializable]
	public sealed class ShadowSettings
	{
		#region PublicFields

		public bool Enabled = true;

		/// <summary>
		///     Resolution of the camera shadow map. With cascades it is an atlas: two get half of it each (side by
		///     side, the map half as tall), four a quarter each (2 x 2).
		/// </summary>
		public int MapSize = 2048;

		/// <summary>
		///     Receivers move this many shadow-map texels toward the light before the depth comparison, against shadow
		///     acne (too much detaches the shadows from their casters). Scaled by the soft filter's radius, as in Unity.
		/// </summary>
		public float DepthBiasTexels = 1f;

		/// <summary>Receivers move this many texels along their normal, the more the more grazing the light (scaled like the depth bias).</summary>
		public float NormalBiasTexels = 1f;

		// The cascade count comes last: the editor draws the splits on a bar below it (the split fields are hidden).

		public ShadowCascadeCount Cascades = ShadowCascadeCount.Four;

		/// <summary>With two cascades: where the second begins, as a fraction of the shadow distance.</summary>
		[HideInInspector] public float TwoCascadeSplit = 0.333f;

		/// <summary>With four cascades: where the second, third and fourth begin, as increasing fractions of the shadow distance.</summary>
		[HideInInspector] public Vector3 FourCascadeSplits = new Vector3(0.067f, 0.2f, 0.467f);

		/// <summary>
		///     The active scene's shadow distance (<see cref="SceneShadowSettings" />): shadows reach this far from the
		///     camera and fade out over the last tenth.
		/// </summary>
		public float Distance
		{
			get => Lighting.SceneShadows.Distance;
			set => Lighting.SceneShadows.Distance = value;
		}

		/// <summary>The active scene's reach of the shadow casters toward the light from a cascade's center (and as far behind it).</summary>
		public float DepthRange
		{
			get => Lighting.SceneShadows.DepthRange;
			set => Lighting.SceneShadows.DepthRange = value;
		}

		#endregion
	}

	/// <summary>How many cascades the camera shadow map is split into (<see cref="ShadowSettings.Cascades" />).</summary>
	public enum ShadowCascadeCount
	{
		One = 1,
		Two = 2,
		Four = 4
	}

	/// <summary>
	///     Global illumination, project-wide (part of <see cref="ProjectSettings" />): the method (voxel cone tracing
	///     or Lumen) and its tunables, plus the runtime volume the passes publish ([NonSerialized]). Whether the
	///     active scene uses GI, its volume and its strengths come from the scene (<see cref="SceneGISettings" />,
	///     reached through the properties of the same names).
	/// </summary>
	[Serializable]
	public sealed class GlobalIlluminationSettings
	{
		#region PublicFields

		// ---- The active scene's part (SceneGISettings) ----

		/// <summary>The active scene uses global illumination.</summary>
		public bool Enabled
		{
			get => Lighting.SceneGI.Enabled;
			set => Lighting.SceneGI.Enabled = value;
		}

		/// <summary>World-space edge length of the active scene's cubic GI volume.</summary>
		public float GridSize
		{
			get => Lighting.SceneGI.GridSize;
			set => Lighting.SceneGI.GridSize = value;
		}

		/// <summary>The active scene's volume center when <see cref="FollowCamera" /> is off.</summary>
		public Vector3 Center
		{
			get => Lighting.SceneGI.Center;
			set => Lighting.SceneGI.Center = value;
		}

		/// <summary>Center the volume on the camera (snapped to voxels) instead of <see cref="Center" />.</summary>
		public bool FollowCamera
		{
			get => Lighting.SceneGI.FollowCamera;
			set => Lighting.SceneGI.FollowCamera = value;
		}

		/// <summary>With <see cref="FollowCamera" />: the volume's center this far along the camera's view direction.</summary>
		public float FollowCameraLookAhead
		{
			get => Lighting.SceneGI.FollowCameraLookAhead;
			set => Lighting.SceneGI.FollowCameraLookAhead = value;
		}

		public float DiffuseStrength
		{
			get => Lighting.SceneGI.DiffuseStrength;
			set => Lighting.SceneGI.DiffuseStrength = value;
		}

		public float SpecularStrength
		{
			get => Lighting.SceneGI.SpecularStrength;
			set => Lighting.SceneGI.SpecularStrength = value;
		}

		public float OcclusionStrength
		{
			get => Lighting.SceneGI.OcclusionStrength;
			set => Lighting.SceneGI.OcclusionStrength = value;
		}

		/// <summary>Weight of the voxel volume's bounces (see <see cref="SceneGISettings.BounceStrength" />).</summary>
		public float BounceStrength
		{
			get => Lighting.SceneGI.BounceStrength;
			set => Lighting.SceneGI.BounceStrength = value;
		}

		/// <summary>Radiance from outside the scene seen by cones that escape (linear RGB).</summary>
		public Vector3 SkyRadiance
		{
			get => Lighting.SceneGI.SkyRadiance;
			set => Lighting.SceneGI.SkyRadiance = value;
		}

		// ---- Project-wide ----

		// Fields marked [GIMethod] are read by one method only (the editor shows them while it is selected); the
		// others by both.

		/// <summary>Voxels per axis. Memory is Resolution^3 * 8 bytes (RGBA16F) plus mips.</summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int Resolution = 128;

		/// <summary>Maximum cone length in world units. 0 uses the grid size.</summary>
		[GIMethod(GIMode.VoxelConeTracing)] public float ConeMaxDistance;

		/// <summary>
		///     Trace cones once per reduced-resolution pixel (GIResolvePass) instead of per material fragment.
		///     Requires the geometry prepass. Roughly 1 / ResolveScale^2 fewer cone traces.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public bool ScreenSpaceResolve = true;

		/// <summary>Resolution of the voxel GI resolve relative to the screen (0.5 = half width and height).</summary>
		[GIMethod(GIMode.VoxelConeTracing)] public float ResolveScale = 0.5f;

		/// <summary>
		///     Turn the six-cone sets around the normal and move their samples along the cones by a hash of the world
		///     position (the screen-space resolve per <see cref="ConeJitterCellSize" /> cell, the volume's bounces per
		///     voxel or bounce cell). Six fixed cones print small bright emitters onto nearby surfaces as rings and blobs;
		///     jittered, the prints become noise the resolve filter (<see cref="ResolveFilterPasses" />) and the mips
		///     average away. Anchored in the world: the same for every frame of a still scene, carried along by the
		///     surfaces when the camera moves.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public bool ConeJitter = true;

		/// <summary>
		///     World size of the cells sharing one cone jitter in the screen-space resolve; 0 = half a voxel. Smaller cells
		///     cost more (neighbouring pixels trace different directions: 5 cm +1.2 ms, 10 cm +0.65 ms, 20 cm +0.4 ms on
		///     the test room), larger ones leave blotches the filter cannot reach.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public float ConeJitterCellSize;

		/// <summary>
		///     Passes of the edge-aware a-trous filter over the voxel GI resolve's diffuse (3x3 taps 1, 2, 4, 8, ...
		///     texels apart, weighted by normal agreement and distance from the centre's plane; spatial only). 0 = off.
		///     A fifth pass (taps 16 texels apart) copies shadow edges to 16-texel offsets: visible doubled, banded shadows.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int ResolveFilterPasses = 4;

		/// <summary>
		///     Re-voxelize dynamic (non-static) objects every N frames; Lumen rebuilds the dynamic part of the global
		///     distance field at the same rate. 1 = every frame.
		/// </summary>
		public int DynamicUpdateInterval = 1;

		/// <summary>Recompute voxel lighting (sun, shadows, bounce) every N frames. 1 = every frame.</summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int LightingUpdateInterval = 1;

		/// <summary>
		///     Resolution of the shadow map covering the whole GI volume, used when lighting voxels (voxel cone
		///     tracing) or surface cache cards (Lumen).
		/// </summary>
		public int GridShadowMapSize = 1024;

		/// <summary>Cones traced per voxel for the bounce: 6 (hemisphere layout) or 1 (single wide cone, cheaper).</summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int BounceCones = 6;

		/// <summary>
		///     Indirect bounces computed in the voxel volume every frame: the volume is lit (sun and emission), then lit
		///     again this many times, each pass tracing the one before. Nothing is carried over from earlier frames (no
		///     lag, no ghosting); every iteration costs one light injection and its mips. 0 = direct light and emission.
		///     The materials' own cone gather adds one more bounce on top.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int BounceIterations = 2;

		/// <summary>
		///     Bounces per 2x2x2 voxels: the hemisphere cones traced once per group of voxels with geometry, around their
		///     average normal (exact on flat surfaces, averaged at edges), several times cheaper than
		///     <see cref="BounceCones" /> cones from every voxel. Off traces from every voxel.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public bool BounceCells = true;

		/// <summary>
		///     Sun shadow samples per voxel when lighting the volume: 4 spread over the voxel's surface (a voxel half in
		///     shadow gets half the light, so GI shadow edges are smooth and do not crawl voxel by voxel) or 1.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int InjectShadowTaps = 4;

		/// <summary>
		///     Coverage voxelization: triangles are rasterized with multisampling and splatted over the voxels nearest
		///     along the projection axis, weighted by the covered area; albedo, normal and emission are weighted averages
		///     (order independent) and the occupancy follows the coverage. Objects then move through the volume smoothly
		///     instead of jumping voxel by voxel. Off = one sample per voxel column, the last fragment wins.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public bool VoxelCoverage = true;

		/// <summary>
		///     <see cref="VoxelCoverage" />: a voxel's occupancy is its splatted weight times this (clamped to 1). 2 makes a
		///     surface between two voxel centres fill both, so a wall always stops a cone like one opaque layer, but the
		///     amount of a moving object in the volume then swings with its position within a voxel (one to two layers:
		///     emitters pulse as they cross voxels). 1 keeps that amount constant.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public float CoverageOccupancyScale = 2f;

		/// <summary>
		///     <see cref="VoxelCoverage" />: voxels a surface is splatted over along its projection axis: 2 (linear tent) or
		///     3 (quadratic B-spline, smoother as objects move, a third more atomics).
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int CoverageSplatWidth = 2;

		/// <summary>
		///     What the voxel radiance mips keep of direction (see <see cref="VoxelMipMode" />). Isotropic mips average
		///     both sides of a wall thinner than a cell: a cone sees half the light of a lit interior wall, and the light
		///     of a sunlit exterior leaks in.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public VoxelMipMode VoxelMips = VoxelMipMode.Anisotropic;

		/// <summary>
		///     <see cref="VoxelMipMode.Anisotropic" />: directional views for this many mip levels above the voxels (each
		///     twice the size of the last, all of them by default); the cones read isotropic mips above them, blended over
		///     one level. Most of the light the directional views keep (a room's walls seen without their dark outer side)
		///     is in the coarse levels, but those also change in steps while objects move (which half of a cell metres
		///     wide an object is in decides what it hides): a faint pulse of the whole GI every few voxels of motion. 3
		///     keeps motion as smooth as isotropic mips, at most of the isotropic mips' loss of light.
		/// </summary>
		[GIMethod(GIMode.VoxelConeTracing)] public int AnisotropicMipLevels = 16;

		/// <summary>
		///     Incremented by <see cref="InvalidateStatic" /> (call it after moving or re-materialing a static
		///     renderer). Every pass with a static cache (voxel geometry, global SDF) rebuilds it when the version
		///     differs from the one it last built, so several renderers (editor views) each rebuild their own.
		///     Grid and resolution changes are detected by the passes themselves. Lighting changes never require a
		///     rebuild: light is injected every frame.
		/// </summary>
		[NonSerialized] public int StaticVersion;

		// ---- Which GI pipeline runs ----

		/// <summary>
		///     The project's GI method. It is chosen before the game runs: the passes read <see cref="Lighting.GIMethod" />,
		///     which keeps the method the game started with.
		/// </summary>
		public GIMode Mode = GIMode.Lumen;

		/// <summary>Voxel cone tracing runs: the active scene uses GI and it is the method (<see cref="Lighting.GIMethod" />).</summary>
		public bool VoxelConeTracingActive => Enabled && Lighting.GIMethod == GIMode.VoxelConeTracing;

		/// <summary>Lumen runs: the active scene uses GI and it is the method (<see cref="Lighting.GIMethod" />).</summary>
		public bool LumenActive => Enabled && Lighting.GIMethod == GIMode.Lumen;

		/// <summary>
		///     The global distance field is built: Lumen traces it, voxel cone tracing only in an SDF trace mode
		///     (<see cref="TraceMode" />; <see cref="GITraceMode.VoxelCones" /> never reads it).
		/// </summary>
		public bool DistanceFieldActive => Enabled && DistanceFieldEnabled && (Lighting.GIMethod == GIMode.Lumen || TraceMode != GITraceMode.VoxelCones);

		// ---- Lumen surface cache (SurfaceCachePass) ----

		[Header("Surface Cache")]
		[GIMethod(GIMode.Lumen)] public int SurfaceCacheAtlasSize = 1024;

		/// <summary>Card texels per world meter.</summary>
		[GIMethod(GIMode.Lumen)] public int SurfaceCacheTexelsPerMeter = 6;

		[GIMethod(GIMode.Lumen)] public int SurfaceCacheMaxCardSize = 96;

		/// <summary>Kept for command-line compatibility; radiosity now traces 16 rays per 4x4-texel probe per frame.</summary>
		[GIMethod(GIMode.Lumen)] public int RadiosityRays = 16;

		/// <summary>Constant history weight of the radiosity probes per update when RadiosityMaxHistorySamples is 0.</summary>
		[GIMethod(GIMode.Lumen)] public float RadiosityBlend = 0.9f;

		/// <summary>
		///     Sample-count accumulation of the radiosity probes once the scene has been quiet for
		///     RadianceCacheIdleAfterFrames: the history weight grows with the samples held up to this cap (0 = the
		///     constant RadiosityBlend instead).
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float RadiosityMaxHistorySamples = 64f;

		/// <summary>Cap of the radiosity probe history while the scene is changing or has just changed (short lag).</summary>
		[GIMethod(GIMode.Lumen)] public float RadiosityMovingHistorySamples = 16f;

		/// <summary>
		///     While the scene is quiet (see RadianceCacheIdleAfterFrames) only 1 / this of the radiosity probe rows
		///     is refreshed per frame, round robin. 1 = always refresh everything.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public int RadiosityIdleDivisor = 4;

		// ---- Lumen screen probe gather (ScreenProbeGatherPass) ----

		/// <summary>
		///     Resolution of the Lumen screen probe integration relative to the screen. 1 = per pixel, as in Lumen: at
		///     lower values detail smaller than two pixels (distant small objects) gets the blended lighting of its
		///     neighbours and turns into mush. Costs about 0.7 ms more than 0.5 on the Intel iGPU at 1280x720.
		/// </summary>
		[Header("Screen Probes")]
		[GIMethod(GIMode.Lumen)] public float ScreenProbeResolveScale = 1f;

		/// <summary>Pixels between screen probes.</summary>
		[GIMethod(GIMode.Lumen)] public int ProbeSpacing = 16;

		/// <summary>
		///     Adaptive probe placement (Lumen): extra probes where the uniform grid cannot be interpolated (thin
		///     geometry, silhouettes), placed hierarchically at ProbeSpacing / 2 and / 4 pixel steps.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public bool ProbeAdaptivePlacement = true;

		/// <summary>Capacity of adaptive probes as a fraction of the uniform probe count.</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeAdaptiveFraction = 0.5f;

		/// <summary>A candidate pixel gets an adaptive probe when its interpolation weight from existing probes is below this.</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeAdaptiveMinCoverage = 0.05f;

		/// <summary>
		///     The finest adaptive placement level only visits screen tiles the coarser level flagged (a candidate
		///     below 0.5 coverage, or a finer candidate on another surface). Pays off in views dominated by large flat
		///     surfaces; in the test scene the flagging costs as much as it saves, so it is off by default.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public bool ProbeAdaptiveFlaggedRefine = false;

		[GIMethod(GIMode.Lumen)] public bool ProbeSpatialFilter = true;

		/// <summary>
		///     Passes of the spatial probe filter: each widens its kernel (less noise, no added lag, ~0.2 ms on the Intel
		///     iGPU) but also softens the GI (measured: two passes flatten the emitters' light pools by a few percent).
		/// </summary>
		[GIMethod(GIMode.Lumen)] public int ProbeSpatialFilterPasses = 1;

		/// <summary>Per-frame jitter of probe anchors and ray directions (needed for convergence; off = diagnostics).</summary>
		[GIMethod(GIMode.Lumen)] public bool ProbeJitter = true;

		/// <summary>
		///     How far a probe's anchor pixel moves inside its cell from frame to frame, as a fraction of the cell
		///     (1 = anywhere in the cell, 0 = always the cell centre). Lumen jitters the whole cell; here the
		///     default is off because the history a probe carries over then comes from the same surface point,
		///     which measured 2-3x less temporal variation in a static view. Set to 1 to average probe placement
		///     over time instead (thin geometry gets covered more often, lighting breathes more).
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeAnchorJitter = 0f;

		/// <summary>Jitter ray directions inside their octahedral texel every frame.</summary>
		[GIMethod(GIMode.Lumen)] public bool ProbeDirectionJitter = true;

		/// <summary>
		///     Structured importance sampling (Lumen's): directions ranked by last frame's radiance times cosine; the
		///     dimmest 2x2 blocks of directions get one ray per block, and each ray saved supersamples one of the
		///     brightest directions with four rays. Every direction is traced every frame; the budget stays 64 per probe.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public bool ProbeImportanceSampling = true;

		/// <summary>A 2x2 block of directions gets a single ray when its importance is below this fraction of the mean block (at most 12 blocks).</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeImportanceMergeFraction = 0.5f;

		/// <summary>Radius of the spatial probe filter, in probes (1 = 3x3 neighbourhood).</summary>
		[GIMethod(GIMode.Lumen)] public int ProbeFilterRadius = 1; // the filter kernel is fixed at 3x3 probes; kept for CLI compatibility

		/// <summary>
		///     A neighbour probe's sample is reused only when its hit point lies within this many degrees of the
		///     centre probe's own ray direction (Lumen's angle-error rejection).
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeFilterMaxAngle = 10f;

		/// <summary>
		///     Temporal probe filter (Lumen's TemporalFilterProbes): weight of the reprojected previous radiance when a
		///     direction is retraced. Reduces the frame-to-frame colour noise of probes that see small bright surfaces.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeHistoryWeight = 0.5f;

		/// <summary>A probe's history is used only when the previous probe lay within this distance (world units).</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeHistoryDistance = 0.3f;

		/// <summary>
		///     Sample-count accumulation of probe radiance: a texel's history weight is n / (n + 1) up to this many
		///     samples, so static lighting converges to a noise-free value; 0 falls back to the constant
		///     <see cref="ProbeHistoryWeight" />. Probes whose lighting changed restart their counts (see below).
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeMaxHistorySamples = 32f;

		/// <summary>No longer read: the probe change test is per direction (<see cref="ProbeChangeSigma" />, <see cref="ProbeChangeFloor" />).</summary>
		[HideInInspector] public float ProbeChangeThreshold = 0.5f;

		/// <summary>
		///     Anti-lag of the probe directions (ProbeCompose.shader): besides its long history every direction keeps the
		///     mean of its last <see cref="ProbeFastHistorySamples" /> samples; when the long history is further from that
		///     mean than this many of the mean's standard deviations (from the direction's own spread) plus
		///     <see cref="ProbeChangeFloor" /> of its value, a light appeared or left and the long history restarts from it.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeChangeSigma = 4f;

		/// <summary>The relative part of the anti-lag threshold.</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeChangeFloor = 0.15f;

		/// <summary>Samples the fast history of a probe direction averages (the anti-lag reference).</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeFastHistorySamples = 4f;

		/// <summary>
		///     A probe restarts every direction when the brightness-weighted mean of its fast histories moved by more than
		///     this fraction of its long one: the light it mostly sees appeared, left or went out.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeJumpThreshold = 0.4f;

		/// <summary>History cap (samples) of a probe direction that sees, or saw in the last <see cref="ProbeMovingFrames" />, an object that moved.</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeMovingHistorySamples = 2f;

		/// <summary>Frames after its last hit on a moving object that a probe direction keeps the short history.</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeMovingFrames = 4f;

		/// <summary>
		///     Steady-state history weight of the per-pixel temporal filter (0 = off): a history accumulates up to
		///     1 / (1 - this) frames (0.9 = 10), each new frame weighted 1 / n, so a new or rejected history converges fast.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float ProbeTemporalBlend = 0.9f;

		/// <summary>Frames the per-pixel history accumulates at most on moving surfaces (their lighting changes as they move).</summary>
		[GIMethod(GIMode.Lumen)] public float ProbeTemporalMovingFrames = 3f;

		/// <summary>Length of the depth-buffer march at the start of every probe ray (world units, 0 = off).</summary>
		[GIMethod(GIMode.Lumen)] public float ScreenTraceDistance = 1.5f;

		[GIMethod(GIMode.Lumen)] public int ScreenTraceSteps = 16;

		/// <summary>Depth-buffer surfaces are assumed this thick when deciding whether a ray passed behind them.</summary>
		[GIMethod(GIMode.Lumen)] public float ScreenTraceThickness = 0.3f;

		/// <summary>Maximum plane distance (world units) for a probe to be used by a pixel or a neighbour.</summary>
		[GIMethod(GIMode.Lumen)] public float ProbePlaneTolerance = 0.3f;

		// ---- Lumen world-space radiance cache (RadianceCachePass) ----

		[Header("Radiance Cache")]
		[GIMethod(GIMode.Lumen)] public bool RadianceCacheEnabled = true;

		/// <summary>Probes per axis over the GI grid; probe spacing is GridSize / (n - 1).</summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheProbesPerAxis = 17;

		/// <summary>Stored octahedral map resolution per probe: 8 or 16 (Lumen stores 16).</summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheProbeResolution = 16;

		/// <summary>
		///     Rays per axis traced per probe update, a multiple of 16 and of the stored resolution (Lumen traces
		///     32x32). The rays are averaged into the stored texels, so 16 -> 8 gives 4 rays per texel per update.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheTraceResolution = 32;

		/// <summary>
		///     Probes inside the static scene bounds re-traced per frame (round robin; all of them when the budget
		///     allows). Probes outside the scene get an eighth of this on top. Cost scales with this times the
		///     trace resolution squared; for a given ray budget and accumulation time the noise is the same
		///     however the budget is split, so fewer probes with more rays each is the cheaper option.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheProbesPerFrame = 160;

		/// <summary>
		///     Time constant of a probe's accumulation, in frames: an update blends with weight exp(-dt / this), dt
		///     being the frames since the probe's previous update. Rarely updated probes therefore converge as fast
		///     as frequently updated ones. 0 = no accumulation.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float RadianceCacheHistoryFrames = 20f;

		/// <summary>
		///     Sample-count accumulation of the radiance cache probes: the history weight grows with the number of
		///     updates up to this cap (0 = the time-constant blend of RadianceCacheHistoryFrames instead).
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float RadianceCacheMaxHistorySamples = 32f;

		/// <summary>
		///     Cap of the probe history until the grid has been swept RadianceCacheIdleAfterSweeps times since the last
		///     change: the cache then tracks the surface cache's convergence instead of averaging the transient in.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float RadianceCacheMovingHistorySamples = 4f;

		/// <summary>
		///     Relative change of a probe's summed radiance between its fresh estimate and its history that counts as a
		///     lighting change and restarts the probe's accumulation.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float RadianceCacheChangeThreshold = 0.25f;

		/// <summary>
		///     Probes around what changed this frame (objects that moved, materials that changed) updated before the round
		///     robin, nearest first, on top of <see cref="RadianceCacheProbesPerFrame" />: the far field follows a moving or
		///     switched light within a frame or two instead of a full sweep of the grid.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheChangeProbesPerFrame = 64;

		/// <summary>How far around a change its probes are taken first, in probe spacings.</summary>
		[GIMethod(GIMode.Lumen)] public float RadianceCacheChangeRadius = 2f;

		/// <summary>After this many frames without object, material or sun changes the probe budget drops to a trickle.</summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheIdleAfterFrames = 60;

		/// <summary>Budget divisor while idle (converged probes only need an occasional refresh).</summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheIdleDivisor = 8;

		/// <summary>
		///     Idle also requires this many full passes over the probe grid at the full budget since the last change:
		///     a quiet scene is not a converged one until the multi-bounce chain (cache -> cards -> cache) has settled.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public int RadianceCacheIdleAfterSweeps = 40;

		/// <summary>
		///     Screen probe and radiosity rays trace the distance field this far (world units) and read the
		///     radiance cache beyond it. 0 traces the whole field.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public float RadianceCacheNearDistance = 2f;

		/// <summary>Let the surface cache radiosity rays read the radiance cache too (off = radiosity traces fully).</summary>
		[GIMethod(GIMode.Lumen)] public bool RadianceCacheForRadiosity = true;

		/// <summary>
		///     Radiance cache lookups whose target is at least 4 probe spacings away skip the per-probe target
		///     visibility pass (parallax below a texel): a third fewer atlas fetches for far-field rays.
		/// </summary>
		[GIMethod(GIMode.Lumen)] public bool RadianceCacheFarShortcut = true;

		// ---- Global distance field (GlobalDistanceFieldPass) ----

		[Header("Distance Field")]
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
		[GIMethod(GIMode.VoxelConeTracing)] public GITraceMode TraceMode = GITraceMode.SdfCones;

		/// <summary>Length of the SDF detail trace at the start of every cone (world units).</summary>
		[GIMethod(GIMode.VoxelConeTracing)] public float SdfDetailDistance = 1.5f;

		// ---- Runtime data written by VoxelGIPass / GIResolvePass ----

		/// <summary>GL handle of the 3D radiance texture, 0 when unavailable.</summary>
		[NonSerialized] public int VoxelTexture;

		/// <summary>
		///     The textures of the voxel radiance volume of this frame (<see cref="VoxelTexture" /> and the directional
		///     data its mips have, see <see cref="VoxelMipMode" />), for <see cref="Lighting.SetVoxelRadianceUniforms" />.
		/// </summary>
		[NonSerialized] public VoxelVolumeTextures VoxelVolume;

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
		///     Positions the GI grid for this frame: around <see cref="Center" />, or, with <see cref="FollowCamera" />,
		///     around the point <see cref="FollowCameraLookAhead" /> along the camera's view, snapped to voxels (the volume
		///     moves in whole voxels: every move re-voxelizes its static geometry). Called by the volume passes.
		/// </summary>
		public void UpdateGridPlacement(Vector3 cameraPosition, Vector3 cameraForward)
		{
			Vector3 center = Center;
			if (FollowCamera)
			{
				Vector3 target = cameraPosition + cameraForward * FollowCameraLookAhead;
				float voxel = VoxelSize;
				center = new Vector3(
					(float)Math.Floor(target.X / voxel) * voxel,
					(float)Math.Floor(target.Y / voxel) * voxel,
					(float)Math.Floor(target.Z / voxel) * voxel);
			}

			GridMin = center - new Vector3(GridSize * 0.5f);
		}

		#endregion
	}

	/// <summary>What the voxel radiance volume's mips keep of the direction its surfaces face.</summary>
	public enum VoxelMipMode
	{
		/// <summary>Box-filtered averages: every direction sees the same (cheapest, one fetch per cone step).</summary>
		Isotropic,

		/// <summary>
		///     Six views per mip level, each compositing its children front to back along an axis (two or three fetches
		///     per cone step): a wall's near side hides its far side. Not linear: the coarse levels change in steps while
		///     objects move (see <see cref="GlobalIlluminationSettings.AnisotropicMipLevels" />).
		/// </summary>
		Anisotropic,

		/// <summary>
		///     Spherical harmonics L1 per texel: every voxel's radiance and coverage weighted by (1 - n . d) for a cone
		///     travelling along d (its front side counts twice, its back side not at all), stored as the isotropic volume
		///     plus a gradient per axis at half resolution (four fetches per cone step). Linear, so the mips are plain
		///     averages and move smoothly with the objects; facing surfaces are told apart within a cell, not their order.
		/// </summary>
		SphericalHarmonics
	}

	/// <summary>The textures of a voxel radiance volume (published in <see cref="GlobalIlluminationSettings.VoxelVolume" />).</summary>
	public struct VoxelVolumeTextures
	{
		public VoxelMipMode Mode;

		/// <summary>Radiance premultiplied by occupancy (rgb) and occupancy (a), with isotropic mips.</summary>
		public int Radiance;

		/// <summary><see cref="VoxelMipMode.Anisotropic" />: six views side by side along x, level 0 = the volume's mip 1.</summary>
		public int Aniso;

		public int AnisoLevels;

		/// <summary>
		///     <see cref="VoxelMipMode.SphericalHarmonics" />: d.x, d.y, d.z coefficients of radiance (rgb) and coverage (a),
		///     level 0 = the volume's mip 1.
		/// </summary>
		public int SHX;

		public int SHY;
		public int SHZ;
	}

	public enum GIMode
	{
		/// <summary>Voxel cone tracing: radiance volume + cones (steps 1-3 of the roadmap).</summary>
		VoxelConeTracing,

		/// <summary>Lumen-style: surface cache cards lit with radiosity, screen probes traced through the global SDF.</summary>
		Lumen
	}

	/// <summary>
	///     A GI setting (<see cref="GlobalIlluminationSettings" />, <see cref="SceneGISettings" />) that only one
	///     <see cref="GIMode" /> reads: the editor shows it only while that method is selected. Settings without it are
	///     read by both methods.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class GIMethodAttribute : Attribute
	{
		public readonly GIMode Mode;

		public GIMethodAttribute(GIMode mode)
		{
			Mode = mode;
		}

		/// <summary>The method that reads <paramref name="field" />, null when both do.</summary>
		public static GIMode? Of(FieldInfo field)
		{
			return field.GetCustomAttribute<GIMethodAttribute>(false)?.Mode;
		}
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
