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

		/// <summary>Texture unit reserved for the directional shadow map (material textures use 0..N).</summary>
		public const int ShadowMapTextureUnit = 15;

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
}
