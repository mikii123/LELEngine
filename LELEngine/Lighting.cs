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

		/// <summary>World -> light clip space. Written by the shadow pass every frame.</summary>
		public static Matrix4 LightSpaceMatrix = Matrix4.Identity;

		/// <summary>Depth texture of the directional light, or null when shadows are off.</summary>
		public static RenderTexture ShadowMap;

		#endregion

		#region PublicMethods

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
			Vector3 camPos = Camera.main != null ? Camera.main.transform.position : Vector3.Zero;
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
			bool enabled = receiveGI && GI.Enabled && GI.VoxelTexture != 0;
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
			program.SetInt("giTraceMode", sdf && GI.TraceMode == GITraceMode.SdfDetail ? 1 : 0);
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
	/// </summary>
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

		/// <summary>Resolution of the GI resolve relative to the screen (0.5 = half width and height).</summary>
		public float ResolveScale = 0.5f;

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
		///     Set when the static geometry cache must be rebuilt. Grid and resolution changes set it
		///     automatically; call <see cref="InvalidateStatic" /> after moving or re-materialing a static renderer.
		///     Lighting changes never require a rebuild: light is injected every frame.
		/// </summary>
		public bool StaticDirty = true;

		// ---- Global distance field (GlobalDistanceFieldPass) ----

		public bool DistanceFieldEnabled = true;

		/// <summary>Texels per axis of the global distance field (shares the GI grid extent).</summary>
		public int SdfResolution = 128;

		/// <summary>Distances are clamped to this many SDF voxels; also bounds how far dynamic objects reach.</summary>
		public int SdfBandVoxels = 8;

		/// <summary>Maximum sphere tracing iterations per ray.</summary>
		public int SdfMaxSteps = 48;

		/// <summary>How cones find geometry: voxel alpha mips, or sphere tracing the global SDF near the origin.</summary>
		public GITraceMode TraceMode = GITraceMode.SdfDetail;

		/// <summary>Length of the SDF detail trace at the start of every cone (world units).</summary>
		public float SdfDetailDistance = 1.5f;

		/// <summary>Radiance from outside the scene seen by cones that escape (linear RGB).</summary>
		public Vector3 SkyRadiance = Vector3.Zero;

		/// <summary>Set when the static part of the global distance field must be recomposed.</summary>
		public bool SdfStaticDirty = true;

		// ---- Runtime data written by VoxelGIPass / GIResolvePass ----

		/// <summary>GL handle of the 3D radiance texture, 0 when unavailable.</summary>
		public int VoxelTexture;

		/// <summary>World-space minimum corner of the volume for the current frame.</summary>
		public Vector3 GridMin;

		/// <summary>GL handle of the global distance field texture, 0 when unavailable.</summary>
		public int GlobalSdf;

		public bool ResolveActive;
		public int ResolvedDiffuse;
		public int ResolvedSpecular;
		public int ResolveWidth;
		public int ResolveHeight;
		public int ScreenWidth;
		public int ScreenHeight;

		#endregion

		#region PublicMethods

		public float VoxelSize => GridSize / Resolution;
		public float SdfVoxelSize => GridSize / SdfResolution;

		public void InvalidateStatic()
		{
			StaticDirty = true;
			SdfStaticDirty = true;
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

	public enum GITraceMode
	{
		/// <summary>Occlusion from the voxel alpha mip chain. Cheapest, leaks through thin geometry.</summary>
		VoxelCones,

		/// <summary>
		///     The first <see cref="GlobalIlluminationSettings.SdfDetailDistance" /> of each cone is sphere traced
		///     through the global distance field (radiance read from the voxel volume at the hit), then the
		///     voxel cone continues. Removes leaks through nearby thin geometry.
		/// </summary>
		SdfDetail
	}
}
