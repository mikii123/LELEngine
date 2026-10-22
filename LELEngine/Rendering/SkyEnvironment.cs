using System;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     What the GI passes see of the environment's skybox material: its cubemap (a panorama is converted to
	///     one), tint, exposure and rotation, refreshed once per frame (<see cref="Lighting.BeginFrame" />) and
	///     bound by <see cref="Lighting.SetSkyUniforms" />. Without a skybox the GI keeps its constant sky color.
	/// </summary>
	internal static class SkyEnvironment
	{
		#region PublicFields

		public static bool Enabled { get; private set; }
		public static int CubemapHandle { get; private set; }
		public static Vector4 TintExposure { get; private set; } = Vector4.One;
		public static Vector2 Rotation { get; private set; } = new Vector2(1f, 0f);

		/// <summary>Cos, sin of the skybox material's Tilt (about X, before the yaw).</summary>
		public static Vector2 Tilt { get; private set; } = new Vector2(1f, 0f);
		public static float Lod { get; private set; }

		/// <summary>A 1x1 black cubemap bound when no skybox is set (the sampler stays complete).</summary>
		public static int BlackCubemapHandle => (blackCubemap ??= new Cubemap(1, "black sky", false)).Handle;

		#endregion

		#region PrivateFields

		private static Cubemap blackCubemap;
		private static Texture2D panoramaSource;
		private static Cubemap panoramaCubemap;
		private static int panoramaWidth;
		private static int panoramaHeight;

		#endregion

		#region PublicMethods

		/// <summary>Reads the skybox material's properties for this frame.</summary>
		public static void Update(Material skybox)
		{
			Enabled = false;
			if (skybox == null)
			{
				return;
			}

			Cubemap cubemap = null;
			Texture2D panorama = null;
			foreach (MaterialProperty property in skybox.Properties)
			{
				if (!(property is TextureProperty texture) || texture.value == null)
				{
					continue;
				}

				if (texture.value is Cubemap cube && cubemap == null)
				{
					cubemap = cube;
				}
				else if (texture.value is Texture2D image && panorama == null)
				{
					panorama = image;
				}
			}

			if (cubemap == null && panorama != null)
			{
				cubemap = PanoramaCubemap(panorama);
			}

			if (cubemap == null || cubemap.Handle == 0)
			{
				return;
			}

			Color4 tint = skybox.GetColor("Tint", Color4.White);
			float exposure = skybox.GetFloat("Exposure", 1f);
			float rotation = skybox.GetFloat("Rotation", 0f) * (float)(Math.PI / 180.0);
			CubemapHandle = cubemap.Handle;
			TintExposure = new Vector4(tint.R * exposure, tint.G * exposure, tint.B * exposure, 1f);
			Rotation = new Vector2(MathF.Cos(rotation), MathF.Sin(rotation));
			float tilt = skybox.GetFloat("Tilt", 0f) * (float)(Math.PI / 180.0);
			Tilt = new Vector2(MathF.Cos(tilt), MathF.Sin(tilt));
			// Diffuse rays read a blurred sky (about 8 texels across a face) when the cubemap has mips.
			Lod = cubemap.mipmapEnabled ? Math.Max(0f, MathF.Log2(Math.Max(1, cubemap.size)) - 3f) : 0f;
			Enabled = true;
		}

		/// <summary>Drops the cached panorama conversion (project closed).</summary>
		public static void Clear()
		{
			panoramaCubemap?.Release();
			panoramaCubemap = null;
			panoramaSource = null;
		}

		#endregion

		#region PrivateMethods

		/// <summary>The cubemap of an equirectangular skybox texture (built once per source image).</summary>
		private static Cubemap PanoramaCubemap(Texture2D panorama)
		{
			if (panoramaCubemap != null && ReferenceEquals(panoramaSource, panorama) && panoramaWidth == panorama.width && panoramaHeight == panorama.height)
			{
				return panoramaCubemap;
			}

			panoramaCubemap?.Release();
			panoramaCubemap = null;
			panoramaSource = panorama;
			panoramaWidth = panorama.width;
			panoramaHeight = panorama.height;
			try
			{
				ImageData image = panorama.isHdr ? new ImageData(panorama.width, panorama.height, panorama.GetRawFloatPixels()) : new ImageData(panorama.width, panorama.height, panorama.GetRawPixels());
				if (panorama.bottomUp)
				{
					image = image.FlipVertical();
				}

				int size = Math.Clamp(panorama.height / 2, 16, 512);
				ImageData[] faces = TextureAssets.ExtractFaces(image, CubemapLayout.Equirectangular, size);
				if (faces == null)
				{
					return null;
				}

				byte[][] rgba = faces[0].IsHdr ? null : new byte[6][];
				float[][] hdr = faces[0].IsHdr ? new float[6][] : null;
				for (int i = 0; i < 6; i++)
				{
					if (hdr != null) hdr[i] = faces[i].FloatPixels;
					else rgba[i] = faces[i].Pixels;
				}

				var cubemap = new Cubemap { name = (panorama.name ?? "panorama") + " (sky)" };
				cubemap.SetFaces(size, rgba, hdr, panorama.sRGB && hdr == null, true);
				cubemap.SetSamplerState(FilterMode.Trilinear, TextureWrap.Clamp, 1);
				panoramaCubemap = cubemap;
				return cubemap;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Sky] Cannot convert the panorama for the GI: " + e.Message);
				return null;
			}
		}

		#endregion
	}
}
