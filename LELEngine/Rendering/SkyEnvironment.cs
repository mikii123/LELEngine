using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
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

		/// <summary>Texels across a face of the GI's sky cubemap (mip 0).</summary>
		public const int FaceSize = 128;

		/// <summary>A 1x1 black cubemap bound when no skybox is set (the sampler stays complete).</summary>
		public static int BlackCubemapHandle => (blackCubemap ??= new Cubemap(1, "black sky", false)).Handle;

		#endregion

		#region PrivateFields

		private static Cubemap blackCubemap;
		private static ComputeShader resample;
		private static int lightingHandle;
		private static int lightingSource;
		private static int lightingSourceSize;
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
			CubemapHandle = LightingCubemap(cubemap);
			if (CubemapHandle == 0)
			{
				return;
			}

			TintExposure = new Vector4(tint.R * exposure, tint.G * exposure, tint.B * exposure, 1f);
			Rotation = new Vector2(MathF.Cos(rotation), MathF.Sin(rotation));
			float tilt = skybox.GetFloat("Tilt", 0f) * (float)(Math.PI / 180.0);
			Tilt = new Vector2(MathF.Cos(tilt), MathF.Sin(tilt));
			// Rays read about 8 texels across a face (a diffuse sky); cones pick their level by aperture (Sky.glsl).
			Lod = MathF.Log2(FaceSize) - 3f;
			Enabled = true;
		}

		/// <summary>Drops the cached panorama conversion and the GI's sky (project closed, GI off).</summary>
		public static void Clear()
		{
			Enabled = false;
			panoramaCubemap?.Release();
			panoramaCubemap = null;
			panoramaSource = null;
			if (lightingHandle != 0)
			{
				GL.DeleteTexture(lightingHandle);
				lightingHandle = 0;
			}

			lightingSource = 0;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     The GI's own sky: the skybox cubemap box-filtered into <see cref="FaceSize" /> texels per face (RGBA16F,
		///     linear) with a full mip chain, rebuilt when the source texture changes. The lighting must not depend on
		///     how the skybox is imported for display: a skybox without mips (or point filtered) read at full
		///     resolution put a sharp sky into every rough surface's reflection.
		/// </summary>
		private static int LightingCubemap(Cubemap source)
		{
			int sourceHandle = source.Handle;
			if (lightingHandle != 0 && lightingSource == sourceHandle && lightingSourceSize == source.size)
			{
				return lightingHandle;
			}

			resample ??= new ComputeShader("Engine/SkyLighting.shader");
			if (!resample.Program.IsLinked)
			{
				return 0;
			}

			if (lightingHandle == 0)
			{
				lightingHandle = GL.GenTexture();
				GL.BindTexture(TextureTarget.TextureCubeMap, lightingHandle);
				int levels = (int)MathF.Log2(FaceSize) + 1;
				GL.TexStorage2D(TextureTarget2d.TextureCubeMap, levels, SizedInternalFormat.Rgba16f, FaceSize, FaceSize);
				GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
				GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
				GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
				GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
				GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			}

			// Taps per output texel and axis: one per source texel up to 16 (a 4096 face: 16 x 16 points of 32 x 32).
			int taps = Math.Clamp(source.size / FaceSize, 1, 16);
			float sourceLod = source.mipmapEnabled ? Math.Max(0f, MathF.Log2(Math.Max(1f, (float)source.size / (FaceSize * taps)))) : 0f;
			resample.Use();
			resample.Program.SetTexture("Source", TextureTarget.TextureCubeMap, sourceHandle, 0);
			resample.Program.SetInt("faceSize", FaceSize);
			resample.Program.SetInt("taps", taps);
			resample.Program.SetFloat("sourceLod", sourceLod);
			GL.BindImageTexture(0, lightingHandle, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			resample.DispatchThreads(FaceSize, FaceSize, 6, 8, 8, 1);
			GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
			GL.BindImageTexture(0, 0, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindTexture(TextureTarget.TextureCubeMap, lightingHandle);
			GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);
			GL.BindTexture(TextureTarget.TextureCubeMap, 0);
			lightingSource = sourceHandle;
			lightingSourceSize = source.size;
			return lightingHandle;
		}

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
