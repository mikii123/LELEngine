using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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

		/// <summary>Uploads the material's capture inputs and gives the program its maps.</summary>
		public static void SetUniforms(ShaderProgram program, Material material)
		{
			Read(material, out Inputs inputs);
			program.SetVector4("voxAlbedo", inputs.Albedo);
			program.SetVector4("voxEmissive", inputs.Emissive);
			program.SetVector4("voxAlbedoST", inputs.AlbedoScaleOffset);
			program.SetVector4("voxEmissionST", inputs.EmissionScaleOffset);
			program.SetInt("voxUseAlbedoMap", inputs.AlbedoMap != null ? 1 : 0);
			if (inputs.AlbedoMap != null)
			{
				program.SetTexture("voxAlbedoMap", inputs.AlbedoMap);
			}

			program.SetInt("voxUseEmissionMap", inputs.EmissionMap != null ? 1 : 0);
			if (inputs.EmissionMap != null)
			{
				program.SetTexture("voxEmissionMap", inputs.EmissionMap);
			}

			program.SetInt("voxWorkflow", inputs.Workflow);
			program.SetVector4("voxSpecular", inputs.Specular);
			program.SetInt("voxUseMaskMap", inputs.MaskMap != null ? 1 : 0);
			if (inputs.MaskMap != null)
			{
				program.SetTexture("voxMaskMap", inputs.MaskMap);
			}

			program.SetInt("voxUseSpecularMap", inputs.SpecularMap != null ? 1 : 0);
			if (inputs.SpecularMap != null)
			{
				program.SetTexture("voxSpecularMap", inputs.SpecularMap);
			}

			program.SetInt("voxUseDetailAlbedoMap", inputs.DetailAlbedoMap != null ? 1 : 0);
			if (inputs.DetailAlbedoMap != null)
			{
				program.SetVector4("voxDetailST", inputs.DetailScaleOffset);
				program.SetTexture("voxDetailAlbedoMap", inputs.DetailAlbedoMap);
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
				hash = hash * 31 + Id(inputs.AlbedoMap);
				hash = hash * 31 + Id(inputs.EmissionMap);
				hash = hash * 31 + inputs.AlbedoScaleOffset.GetHashCode();
				hash = hash * 31 + inputs.EmissionScaleOffset.GetHashCode();
				hash = hash * 31 + inputs.Workflow;
				hash = hash * 31 + inputs.Specular.GetHashCode();
				hash = hash * 31 + Id(inputs.MaskMap);
				hash = hash * 31 + Id(inputs.SpecularMap);
				hash = hash * 31 + Id(inputs.DetailAlbedoMap);
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

			inputs.AlbedoMap = Map(material, "DiffuseMap");
			if (inputs.AlbedoMap == null)
			{
				inputs.AlbedoMap = Map(material, "AlbedoMap");
			}

			inputs.EmissionMap = Map(material, "EmissionMap");
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
				inputs.DetailAlbedoMap = Map(material, "DetailAlbedoMap");
				inputs.DetailScaleOffset = ScaleOffset(material, "DetailAlbedoMap_ST");
			}

			if (metallic)
			{
				inputs.Workflow = 1;
				inputs.Specular = new Vector4(material.GetFloat("Metallic"), 0f, 0f, 0f);
				inputs.MaskMap = maskMap ? Map(material, "MaskMap") : null;
			}
			else if (specular)
			{
				inputs.Workflow = 2;
				Color4 color = material.GetColor("SpecularColor", new Color4(0.04f, 0.04f, 0.04f, 1f));
				inputs.Specular = new Vector4(color.R, color.G, color.B, 1f);
				inputs.SpecularMap = specularMap ? Map(material, "SpecularMap") : null;
			}
		}

		// The 2D texture of a material's property on the GPU, or null when the material has no such texture.
		private static GpuTexture Map(Material material, string name)
		{
			return material.GetTexture(name) is Texture2D texture ? texture.Gpu : null;
		}

		// A number of the texture alone for the capture's key (0: none).
		private static int Id(GpuTexture texture)
		{
			return texture != null ? texture.Id : 0;
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
			public GpuTexture AlbedoMap;
			public GpuTexture EmissionMap;
			public int Workflow;
			public Vector4 Specular;
			public GpuTexture MaskMap;
			public GpuTexture SpecularMap;
			public GpuTexture DetailAlbedoMap;
			public Vector4 DetailScaleOffset;
		}

		#endregion
	}
}
