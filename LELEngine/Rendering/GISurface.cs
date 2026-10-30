using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     What the GI captures see of a material (Engine/GISurface.glsl), read from the conventional values every lit
	///     material shares: Color, Emissive, DiffuseMap (or AlbedoMap) with DiffuseMap_ST, EmissionMap with EmissionMap_ST,
	///     and for the physically based workflows of Engine/LitTextured.shader (its WORKFLOW_METALLIC /
	///     WORKFLOW_SPECULAR variants) Metallic or SpecularColor with the MaskMap / SpecularMap its variant samples, and
	///     the detail albedo (DETAIL_ALBEDO_MAP: DetailAlbedoMap with DetailAlbedoMap_ST).
	///     Shared by the voxelization (VoxelGIPass) and the surface cache capture (SurfaceCachePass).
	/// </summary>
	internal static class GISurface
	{
		#region PrivateFields

		private static readonly Vector4 IdentityScaleOffset = new Vector4(1f, 1f, 0f, 0f);

		#endregion

		#region PublicMethods

		/// <summary>Uploads the material's capture inputs (maps on texture units 0 to 4).</summary>
		public static void SetUniforms(ShaderProgram program, Material material)
		{
			Read(material, out Inputs inputs);
			program.SetVector4("voxAlbedo", inputs.Albedo);
			program.SetVector4("voxEmissive", inputs.Emissive);
			program.SetVector4("voxAlbedoST", inputs.AlbedoScaleOffset);
			program.SetVector4("voxEmissionST", inputs.EmissionScaleOffset);
			program.SetInt("voxUseAlbedoMap", inputs.AlbedoMap != 0 ? 1 : 0);
			if (inputs.AlbedoMap != 0)
			{
				program.SetTexture("voxAlbedoMap", TextureTarget.Texture2D, inputs.AlbedoMap, 0);
			}

			program.SetInt("voxUseEmissionMap", inputs.EmissionMap != 0 ? 1 : 0);
			if (inputs.EmissionMap != 0)
			{
				program.SetTexture("voxEmissionMap", TextureTarget.Texture2D, inputs.EmissionMap, 1);
			}

			program.SetInt("voxWorkflow", inputs.Workflow);
			program.SetVector4("voxSpecular", inputs.Specular);
			program.SetInt("voxUseMaskMap", inputs.MaskMap != 0 ? 1 : 0);
			if (inputs.MaskMap != 0)
			{
				program.SetTexture("voxMaskMap", TextureTarget.Texture2D, inputs.MaskMap, 2);
			}

			program.SetInt("voxUseSpecularMap", inputs.SpecularMap != 0 ? 1 : 0);
			if (inputs.SpecularMap != 0)
			{
				program.SetTexture("voxSpecularMap", TextureTarget.Texture2D, inputs.SpecularMap, 3);
			}

			program.SetInt("voxUseDetailAlbedoMap", inputs.DetailAlbedoMap != 0 ? 1 : 0);
			if (inputs.DetailAlbedoMap != 0)
			{
				program.SetVector4("voxDetailST", inputs.DetailScaleOffset);
				program.SetTexture("voxDetailAlbedoMap", TextureTarget.Texture2D, inputs.DetailAlbedoMap, 4);
			}
		}

		/// <summary>
		///     Hash of the inputs a surface cache capture bakes into the cards: everything but the emission's intensity,
		///     which the object table carries (a pulsing emitter never recaptures).
		/// </summary>
		public static long CaptureKey(Material material)
		{
			Read(material, out Inputs inputs);
			unchecked
			{
				long hash = 17;
				hash = hash * 31 + inputs.Albedo.X.GetHashCode();
				hash = hash * 31 + inputs.Albedo.Y.GetHashCode();
				hash = hash * 31 + inputs.Albedo.Z.GetHashCode();
				hash = hash * 31 + inputs.Emissive.X.GetHashCode();
				hash = hash * 31 + inputs.Emissive.Y.GetHashCode();
				hash = hash * 31 + inputs.Emissive.Z.GetHashCode();
				hash = hash * 31 + inputs.AlbedoMap;
				hash = hash * 31 + inputs.EmissionMap;
				hash = hash * 31 + inputs.AlbedoScaleOffset.GetHashCode();
				hash = hash * 31 + inputs.EmissionScaleOffset.GetHashCode();
				hash = hash * 31 + inputs.Workflow;
				hash = hash * 31 + inputs.Specular.GetHashCode();
				hash = hash * 31 + inputs.MaskMap;
				hash = hash * 31 + inputs.SpecularMap;
				hash = hash * 31 + inputs.DetailAlbedoMap;
				hash = hash * 31 + inputs.DetailScaleOffset.GetHashCode();
				return hash;
			}
		}

		#endregion

		#region PrivateMethods

		private static void Read(Material material, out Inputs inputs)
		{
			inputs = default;
			if (!material.TryGetVector4("Color", out inputs.Albedo))
			{
				inputs.Albedo = Vector4.One;
			}

			if (!material.TryGetVector4("Emissive", out inputs.Emissive))
			{
				inputs.Emissive = Vector4.Zero;
			}

			inputs.AlbedoMap = material.GetTextureHandle("DiffuseMap");
			if (inputs.AlbedoMap == 0)
			{
				inputs.AlbedoMap = material.GetTextureHandle("AlbedoMap");
			}

			inputs.EmissionMap = material.GetTextureHandle("EmissionMap");
			inputs.AlbedoScaleOffset = ScaleOffset(material, "DiffuseMap_ST");
			inputs.EmissionScaleOffset = ScaleOffset(material, "EmissionMap_ST");

			// The workflow and its maps as the material's variant draws them.
			bool metallic = false;
			bool specular = false;
			bool maskMap = false;
			bool specularMap = false;
			bool detailAlbedoMap = false;
			foreach (string keyword in material.Keywords)
			{
				switch (keyword)
				{
					case "WORKFLOW_METALLIC": metallic = true; break;
					case "WORKFLOW_SPECULAR": specular = true; break;
					case "MASK_MAP": maskMap = true; break;
					case "SPECULAR_MAP": specularMap = true; break;
					case "DETAIL_ALBEDO_MAP": detailAlbedoMap = true; break;
				}
			}

			if (detailAlbedoMap)
			{
				inputs.DetailAlbedoMap = material.GetTextureHandle("DetailAlbedoMap");
				inputs.DetailScaleOffset = ScaleOffset(material, "DetailAlbedoMap_ST");
			}

			if (metallic)
			{
				inputs.Workflow = 1;
				inputs.Specular = new Vector4(material.GetFloat("Metallic"), 0f, 0f, 0f);
				inputs.MaskMap = maskMap ? material.GetTextureHandle("MaskMap") : 0;
			}
			else if (specular)
			{
				inputs.Workflow = 2;
				Color4 color = material.GetColor("SpecularColor", new Color4(0.04f, 0.04f, 0.04f, 1f));
				inputs.Specular = new Vector4(color.R, color.G, color.B, 1f);
				inputs.SpecularMap = specularMap ? material.GetTextureHandle("SpecularMap") : 0;
			}
		}

		// A material without the value (older files, other shaders) draws its maps untiled.
		private static Vector4 ScaleOffset(Material material, string name)
		{
			return material.TryGetVector4(name, out Vector4 value) ? value : IdentityScaleOffset;
		}

		#endregion

		#region NestedTypes

		private struct Inputs
		{
			public Vector4 Albedo;
			public Vector4 Emissive;
			public Vector4 AlbedoScaleOffset;
			public Vector4 EmissionScaleOffset;
			public int AlbedoMap;
			public int EmissionMap;
			public int Workflow;
			public Vector4 Specular;
			public int MaskMap;
			public int SpecularMap;
			public int DetailAlbedoMap;
			public Vector4 DetailScaleOffset;
		}

		#endregion
	}
}
