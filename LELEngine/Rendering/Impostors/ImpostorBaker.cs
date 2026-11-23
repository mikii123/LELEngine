using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Impostors
{
	/// <summary>
	///     Bakes an impostor: the surfaces of an object captured from a grid of directions (<see cref="ImpostorMath" />)
	///     into atlases Engine/Impostor.shader draws a billboard from, lit like the mesh. Runs on the thread with the GL
	///     context, in the editor and in a running game alike (an object built at runtime can be baked once and drawn as
	///     an impostor from then on: <see cref="global::Impostor.Bake(GameObject, ImpostorSettings)" />).
	///     <para>
	///         What is captured is the surface, not its lighting: each material's shader writes it with its own surface
	///         capture variant (<see cref="ShaderPass.SurfaceCapture" />: the engine's lit shaders have one, so every map
	///         and workflow of a material is in the bake as it is drawn); a shader without one is captured from the
	///         conventional values the GI reads too (Color, DiffuseMap, Emissive, EmissionMap) with the mesh's normals.
	///         Transparent sub-meshes are left out. Every view is drawn at several samples a texel and filtered down
	///         over the samples that hit the object; the coverage is stored as a distance field (it blends between views
	///         into a shape between theirs and stays sharp when minified), and the surface's values are spread into the
	///         empty texels around it, so filtered lookups at the outline read the surface.
	///     </para>
	/// </summary>
	public static class ImpostorBaker
	{
		#region PublicFields

		/// <summary>The shader impostors draw with.</summary>
		public const string ShaderPath = "Engine/Impostor.shader";

		/// <summary>The largest atlas side a bake makes (frames x frame size).</summary>
		public const int MaxAtlasSize = 8192;

		#endregion

		#region PrivateFields

		private const float Far = 1e20f;

		// What the capture's colour targets are cleared to before the surfaces' largest values are kept: below anything a
		// surface writes, within half floats.
		private const float Lowest = -60000f;

		private static ShaderProgram resolveProgram;
		private static ShaderProgram captureProgram;

		private struct Surface
		{
			public MeshRenderer Renderer;
			public int SubMesh;
			public Material Material;

			/// <summary>The material shader's capture variant; null: the engine's capture of the conventional values.</summary>
			public ShaderProgram Program;

			/// <summary>The mesh's space to the baked object's.</summary>
			public Matrix4 Model;
		}

		#endregion

		#region PublicMethods

		/// <summary>The material draws an impostor (its shader is <see cref="ShaderPath" />).</summary>
		public static bool IsImpostor(Material material)
		{
			return material != null && string.Equals(material.ShaderPath, ShaderPath, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		///     The renderers an impostor of <paramref name="root" /> stands for: the enabled mesh renderers on it and its
		///     active descendants, of every LOD group among them only the first level, and none that draws an impostor
		///     itself (an object baked again keeps its impostor out of its own bake).
		/// </summary>
		public static List<MeshRenderer> CollectRenderers(GameObject root)
		{
			var result = new List<MeshRenderer>();
			if (root == null)
			{
				return result;
			}

			var lower = new HashSet<MeshRenderer>();
			var first = new HashSet<MeshRenderer>();
			foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>())
			{
				LOD[] levels = group.GetLODs();
				for (int i = 0; i < levels.Length; i++)
				{
					foreach (MeshRenderer renderer in levels[i].renderers)
					{
						if (renderer != null)
						{
							(i == 0 ? first : lower).Add(renderer);
						}
					}
				}
			}

			foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
			{
				if (!renderer.isActiveAndEnabled || renderer.Mesh == null || (lower.Contains(renderer) && !first.Contains(renderer)))
				{
					continue;
				}

				bool draws = false;
				for (int i = 0; i < renderer.SubMeshCount && !draws; i++)
				{
					Material material = renderer.GetMaterial(i);
					draws = material != null && !material.IsTransparent && !IsImpostor(material);
				}

				if (draws)
				{
					result.Add(renderer);
				}
			}

			return result;
		}

		/// <summary>Bakes the impostor of an object: <see cref="CollectRenderers" /> of it, in its own space.</summary>
		public static ImpostorBake Bake(GameObject root, ImpostorSettings settings = null)
		{
			if (root == null)
			{
				throw new ArgumentNullException(nameof(root));
			}

			return Bake(CollectRenderers(root), root.transform.WorldToLocal, settings);
		}

		/// <summary>
		///     Bakes the impostor of the opaque sub-meshes of <paramref name="renderers" />. <paramref name="worldToObject" />
		///     takes the world to the space the impostor is baked in (the object's: an impostor drawn with that object's
		///     transform stands where the renderers do). Throws when there is nothing to bake, the atlas would be larger
		///     than <see cref="MaxAtlasSize" />, or the thread has no GL context.
		/// </summary>
		public static ImpostorBake Bake(IReadOnlyList<MeshRenderer> renderers, Matrix4 worldToObject, ImpostorSettings settings = null)
		{
			if (!GraphicsThread.HasContext)
			{
				throw new InvalidOperationException("An impostor is baked on the thread with the GL context.");
			}

			settings = (settings ?? new ImpostorSettings()).Validated();
			int frames = settings.Frames;
			int frameSize = settings.FrameSize;
			int atlasSize = frames * frameSize;
			int largest = Math.Min(MaxAtlasSize, Gfx.Capabilities.MaxTextureSize);
			if (atlasSize > largest)
			{
				throw new ArgumentException($"{frames} x {frames} views of {frameSize} texels make an atlas of {atlasSize} texels a side (at most {largest}): fewer or smaller views.");
			}

			Stopwatch watch = Stopwatch.StartNew();
			List<Surface> surfaces = CollectSurfaces(renderers, worldToObject, out int triangles);
			if (surfaces.Count == 0)
			{
				throw new InvalidOperationException("Nothing to bake: no enabled mesh renderer with an opaque material.");
			}

			Bounds(surfaces, out Vector3 boundsMin, out Vector3 boundsMax, out Vector3 centre, out float boundingRadius);
			if (!(boundingRadius > 1e-6f))
			{
				throw new InvalidOperationException("Nothing to bake: the meshes have no size.");
			}

			// The views are a little wider than the sphere: the border stays empty.
			float radius = boundingRadius * frameSize / Math.Max(frameSize - 2f * settings.Border, 1f);
			var bake = new ImpostorBake
			{
				Frames = frames,
				FrameSize = frameSize,
				Hemisphere = settings.Hemisphere,
				Center = centre,
				Radius = radius,
				BoundsMin = boundsMin,
				BoundsMax = boundsMax,
				SourceTriangles = triangles
			};

			EnsurePrograms();
			int samples = settings.Supersampling;
			int tile = frameSize * samples;
			Framebuffer capture = null;
			Framebuffer atlas = null;
			// A bake may run in the middle of a frame (the editor's Bake button): it ends the pass that was open, and its
			// caller begins that pass again to draw on.
			CommandList commands = Gfx.Commands;
			var fullscreen = new FullscreenQuad();
			try
			{
				// One view at a time, at several samples a texel: what the surface capture variants write, and the depth.
				capture = new Framebuffer(tile, tile);
				RenderTexture captureDiffuse = capture.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				RenderTexture captureNormal = capture.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				RenderTexture captureSpecular = capture.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				RenderTexture captureEmission = capture.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				RenderTexture captureDepth = capture.AddDepthAttachment(RenderTextureFormat.Depth32F);
				capture.Validate();

				// Every view filtered down into its frame (Engine/ImpostorResolve.shader).
				atlas = new Framebuffer(atlasSize, atlasSize);
				atlas.AddColorAttachment(RenderTextureFormat.RGBA8, false);
				atlas.AddColorAttachment(RenderTextureFormat.RGBA8, false);
				atlas.AddColorAttachment(RenderTextureFormat.RGBA8, false);
				atlas.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				atlas.Validate();

				// The coverage as a distance field, a view at a time on worker threads (from the capture's own samples).
				var coverage = new byte[atlasSize * atlasSize];
				float spread = Math.Max(2f, frameSize / 16f);
				var depth = new float[tile * tile];
				var pending = new List<Task>();
				int parallel = Math.Max(1, Environment.ProcessorCount);
				Matrix4 projection = ImpostorMath.FrameProjection(radius);
				commands.State.Blend = false;

				for (int row = 0; row < frames; row++)
				{
					for (int column = 0; column < frames; column++)
					{
						Vector3 direction = ImpostorMath.FrameDirection(column, row, frames, settings.Hemisphere);
						ImpostorMath.FrameAxes(direction, out Vector3 right, out Vector3 up);
						Matrix4 view = ImpostorMath.FrameView(centre, radius, direction);

						// The view is drawn twice: its depth, then the surfaces at that depth with the largest value of each
						// channel kept. Where faces lie on one another (doubled faces, panels laid on a hull: every other
						// model has some) the depths are equal and the first face drawn would win, and a GPU does not draw
						// the faces of a mesh in the same order every time: bakes of one object differed in a texel here and
						// there (measured on the 780M: about one texel in three bakes of a 2,000 triangle ship). Kept so,
						// the picture does not depend on the order, and a bake of the same object is the same file.
						// The colours are cleared below anything a surface writes (normals are negative too); the samples
						// nothing covers are told by their depth and never read.
						capture.Begin(LoadOperation.Clear, new ClearValue(Lowest, Lowest, Lowest, Lowest), LoadOperation.Clear, 1f);
						commands.State.SetDepth(true, true, CompareFunction.Less);
						commands.State.ColorMask = ColorWriteMask.None;
						DrawSurfaces(surfaces, view, projection);

						commands.State.ColorMask = ColorWriteMask.All;
						commands.State.SetDepth(true, false, CompareFunction.Equal);
						commands.State.Blend = true;
						commands.State.ColorOperation = BlendOperation.Max;
						commands.State.AlphaOperation = BlendOperation.Max;
						DrawSurfaces(surfaces, view, projection);
						commands.State.Blend = false;
						commands.State.ColorOperation = BlendOperation.Add;
						commands.State.AlphaOperation = BlendOperation.Add;

						captureDepth.Gpu.GetData(0, depth);
						var mask = new byte[tile * tile];
						for (int i = 0; i < mask.Length; i++)
						{
							mask[i] = depth[i] < 1f ? (byte)1 : (byte)0;
						}

						int originX = column * frameSize;
						int originY = row * frameSize;
						pending.Add(Task.Run(() => WriteCoverage(mask, tile, samples, spread, coverage, atlasSize, originX, originY)));
						if (pending.Count >= parallel)
						{
							Task.WaitAll(pending.ToArray());
							pending.Clear();
						}

						atlas.Begin();
						commands.SetViewport(originX, originY, frameSize, frameSize);
						commands.State.SetDepth(false, false);
						commands.State.Culling = FaceCulling.None;
						resolveProgram.Use();
						resolveProgram.SetTexture("CaptureDiffuse", captureDiffuse);
						resolveProgram.SetTexture("CaptureNormal", captureNormal);
						resolveProgram.SetTexture("CaptureSpecular", captureSpecular);
						resolveProgram.SetTexture("CaptureEmission", captureEmission);
						resolveProgram.SetTexture("CaptureDepth", captureDepth);
						resolveProgram.SetInt("samples", samples);
						resolveProgram.SetInt2("frameOrigin", originX, originY);
						resolveProgram.SetVector3("frameRight", right);
						resolveProgram.SetVector3("frameUp", up);
						resolveProgram.SetVector3("frameDirection", direction);
						fullscreen.Draw();
					}
				}

				Task.WaitAll(pending.ToArray());
				Assemble(bake, atlas, coverage);
			}
			finally
			{
				commands.EndRenderPass();
				capture?.Delete();
				atlas?.Delete();
				fullscreen.Delete();
				commands.State.Blend = false;
				commands.State.ColorOperation = BlendOperation.Add;
				commands.State.AlphaOperation = BlendOperation.Add;
				commands.State.SetDepth(true, true, CompareFunction.Less);
				commands.State.Culling = FaceCulling.Back;
				commands.State.ColorMask = ColorWriteMask.All;
			}

			bake.Milliseconds = watch.Elapsed.TotalMilliseconds;
			return bake;
		}

		#endregion

		#region PrivateMethods

		private static void EnsurePrograms()
		{
			resolveProgram ??= new ShaderProgram("Engine/ImpostorResolve.shader");
			captureProgram ??= new ShaderProgram("Engine/ImpostorCapture.shader");
			if (!resolveProgram.IsLinked || !captureProgram.IsLinked)
			{
				string log = (resolveProgram.BuildLog + captureProgram.BuildLog).Trim();
				throw new InvalidOperationException("The impostor bake's shaders do not build" + (log.Length > 0 ? ":\n" + log : "."));
			}
		}

		// The opaque sub-meshes that are no impostors themselves, each with the program that captures its surface.
		private static List<Surface> CollectSurfaces(IReadOnlyList<MeshRenderer> renderers, Matrix4 worldToObject, out int triangles)
		{
			var surfaces = new List<Surface>();
			triangles = 0;
			if (renderers == null)
			{
				return surfaces;
			}

			foreach (MeshRenderer renderer in renderers)
			{
				Mesh mesh = renderer != null ? renderer.Mesh : null;
				if (mesh == null || !GeometryPool.Prepare(mesh))
				{
					continue;
				}

				Matrix4 model = renderer.transform.LocalToWorld * worldToObject;
				for (int i = 0; i < mesh.SubMeshCount; i++)
				{
					Material material = renderer.GetMaterial(i);
					if (material == null || material.Program == null || material.IsTransparent || IsImpostor(material) || mesh.SubMeshes[i].IndexCount == 0)
					{
						continue;
					}

					ShaderProgram program = null;
					if (material.RenderState.SurfaceCapture)
					{
						ShaderProgram variant = material.Program.GetPassVariant(ShaderPass.SurfaceCapture);
						if (variant != null && variant.IsLinked)
						{
							program = variant;
						}
						else
						{
							Debug.LogWarning($"[Impostor] The surface capture variant of {material.ShaderPath} does not build; '{material.name}' is captured from its colour and emission alone.");
						}
					}

					surfaces.Add(new Surface { Renderer = renderer, SubMesh = i, Material = material, Program = program, Model = model });
					triangles += mesh.SubMeshes[i].IndexCount / 3;
				}
			}

			return surfaces;
		}

		// The bounds of the surfaces' meshes in the baked object's space, and the sphere around the bounds' centre that
		// holds every vertex.
		private static void Bounds(List<Surface> surfaces, out Vector3 min, out Vector3 max, out Vector3 centre, out float radius)
		{
			min = new Vector3(float.MaxValue);
			max = new Vector3(float.MinValue);
			var seen = new HashSet<MeshRenderer>();
			foreach (Surface surface in surfaces)
			{
				if (!seen.Add(surface.Renderer))
				{
					continue;
				}

				Matrix4 model = surface.Model;
				foreach (Vertex vertex in surface.Renderer.Mesh.Vertices)
				{
					Vector3 p = Vector3.TransformPosition(vertex.position, model);
					min = Vector3.ComponentMin(min, p);
					max = Vector3.ComponentMax(max, p);
				}
			}

			centre = (min + max) * 0.5f;
			float squared = 0f;
			seen.Clear();
			foreach (Surface surface in surfaces)
			{
				if (!seen.Add(surface.Renderer))
				{
					continue;
				}

				Matrix4 model = surface.Model;
				foreach (Vertex vertex in surface.Renderer.Mesh.Vertices)
				{
					squared = Math.Max(squared, (Vector3.TransformPosition(vertex.position, model) - centre).LengthSquared);
				}
			}

			radius = MathF.Sqrt(squared);
		}

		// One view of the surfaces into the capture targets of the open pass.
		private static void DrawSurfaces(List<Surface> surfaces, Matrix4 view, Matrix4 projection)
		{
			CommandList commands = Gfx.Commands;
			foreach (Surface surface in surfaces)
			{
				CullMode cull = surface.Material.RenderState.Cull;
				commands.State.Culling = cull == CullMode.Off ? FaceCulling.None : cull == CullMode.Front ? FaceCulling.Front : FaceCulling.Back;
				SetSurface(surface, view, projection);
				surface.Renderer.DrawSubMesh(surface.SubMesh);
			}
		}

		// The program a surface is captured with, in use and with the view's and the material's values.
		private static void SetSurface(Surface surface, Matrix4 view, Matrix4 projection)
		{
			Material material = surface.Material;
			ShaderProgram program = surface.Program;
			if (program != null)
			{
				program.Use();
				program.SetMatrix4("viewMatrix", view);
				program.SetMatrix4("projectionMatrix", projection);
				program.SetMatrix4("modelMatrix", surface.Model);
				material.SetUniforms(program, surface.Renderer.PropertyBlock);
			}
			else
			{
				captureProgram.Use();
				captureProgram.SetMatrix4("viewMatrix", view);
				captureProgram.SetMatrix4("projectionMatrix", projection);
				captureProgram.SetMatrix4("modelMatrix", surface.Model);
				GISurface.SetUniforms(captureProgram, material);
				float roughness = material.TryGetFloat("Roughness", out float declared) && declared > 0f ? declared : 0.6f;
				captureProgram.SetFloat("captureRoughness", roughness);
			}
		}

		// ---------------------------------------------------------------- the atlases from the filtered views

		// Reads the filtered views back and makes the atlases of them: which there are (a specular atlas when some
		// surface has a specular colour, an emission atlas when something emits), their channels, and the surface's
		// values spread into the empty texels of every view.
		private static void Assemble(ImpostorBake bake, Framebuffer atlas, byte[] coverage)
		{
			int size = bake.AtlasSize;
			int frames = bake.Frames;
			int frameSize = bake.FrameSize;

			// rgb colour, a covered share of the texel; rg normal, b depth, a occlusion; rgb specular colour, a smoothness.
			byte[] colour = ReadTexels(atlas, 0, size);
			byte[] shape = ReadTexels(atlas, 1, size);
			byte[] specular = ReadTexels(atlas, 2, size);

			// Texels wholly on a surface with a specular colour of its own, and wholly on one without (lit with the scene's
			// highlight): which lighting the impostor needs.
			long withSpecular = 0;
			long withoutSpecular = 0;
			double covered = 0.0;
			double rough = 0.0;
			for (int i = 0; i < colour.Length; i += 4)
			{
				int share = colour[i + 3];
				if (share == 0)
				{
					continue;
				}

				covered += share;
				rough += share * (255 - specular[i + 3]);
				if (share == 255)
				{
					if (specular[i] != 0 || specular[i + 1] != 0 || specular[i + 2] != 0)
					{
						withSpecular++;
					}
					else
					{
						withoutSpecular++;
					}
				}
			}

			// A few stray texels of the other kind (an edge between two materials) do not make an object mixed.
			long whole = withSpecular + withoutSpecular;
			bool hasSpecular = withSpecular > 0 && withSpecular * 200 >= whole;
			bake.SceneHighlights = hasSpecular && withoutSpecular * 200 >= whole;
			bake.Roughness = covered > 0.0 ? Math.Clamp((float)(rough / (covered * 255.0)), 0.03f, 1f) : 0.6f;

			// The emitted radiance, a view at a time (floats: the atlas at once would be 16 bytes a texel): first how
			// bright it gets, then its texels as a share of that.
			GpuTexture emitted = atlas.ColorAttachments[3].Gpu;
			var radiance = new float[frameSize * frameSize * 4];
			var halves = new ushort[radiance.Length];
			float brightest = 0f;
			for (int row = 0; row < frames; row++)
			{
				for (int column = 0; column < frames; column++)
				{
					ReadRadiance(emitted, column * frameSize, row * frameSize, frameSize, halves, radiance);
					for (int i = 0; i < radiance.Length; i += 4)
					{
						brightest = Math.Max(brightest, Math.Max(radiance[i], Math.Max(radiance[i + 1], radiance[i + 2])));
					}
				}
			}

			byte[] emission = null;
			if (brightest > 1e-4f && !float.IsInfinity(brightest) && !float.IsNaN(brightest))
			{
				emission = new byte[size * size * 4];
				for (int row = 0; row < frames; row++)
				{
					for (int column = 0; column < frames; column++)
					{
						ReadRadiance(emitted, column * frameSize, row * frameSize, frameSize, halves, radiance);
						for (int y = 0; y < frameSize; y++)
						{
							int target = ((row * frameSize + y) * size + column * frameSize) * 4;
							int source = y * frameSize * 4;
							for (int x = 0; x < frameSize; x++, target += 4, source += 4)
							{
								emission[target] = EncodeSrgb(radiance[source] / brightest);
								emission[target + 1] = EncodeSrgb(radiance[source + 1] / brightest);
								emission[target + 2] = EncodeSrgb(radiance[source + 2] / brightest);
								emission[target + 3] = 255;
							}
						}
					}
				}

				bake.EmissionScale = brightest;
			}

			// The channels as the shader reads them.
			var shapeAtlas = new byte[size * size * 4];
			var baseAtlas = new byte[size * size * 4];
			byte[] specularAtlas = hasSpecular ? new byte[size * size * 4] : null;
			for (int i = 0, texel = 0; i < colour.Length; i += 4, texel++)
			{
				shapeAtlas[i] = shape[i];
				shapeAtlas[i + 1] = shape[i + 1];
				shapeAtlas[i + 2] = shape[i + 2];
				shapeAtlas[i + 3] = coverage[texel];
				baseAtlas[i] = colour[i];
				baseAtlas[i + 1] = colour[i + 1];
				baseAtlas[i + 2] = colour[i + 2];
				baseAtlas[i + 3] = hasSpecular ? specular[i + 3] : shape[i + 3];
				if (hasSpecular)
				{
					specularAtlas[i] = specular[i];
					specularAtlas[i + 1] = specular[i + 1];
					specularAtlas[i + 2] = specular[i + 2];
					specularAtlas[i + 3] = shape[i + 3];
				}
			}

			// Every empty texel takes the values of the nearest covered one of its view (the coverage keeps its own).
			Parallel.For(0, frames * frames, frame =>
			{
				int originX = frame % frames * frameSize;
				int originY = frame / frames * frameSize;
				int[] nearest = NearestCovered(colour, size, originX, originY, frameSize);
				if (nearest == null)
				{
					return;
				}

				for (int y = 0; y < frameSize; y++)
				{
					for (int x = 0; x < frameSize; x++)
					{
						int from = nearest[y * frameSize + x];
						if (from < 0)
						{
							continue;
						}

						int target = ((originY + y) * size + originX + x) * 4;
						int source = ((originY + from / frameSize) * size + originX + from % frameSize) * 4;
						shapeAtlas[target] = shapeAtlas[source];
						shapeAtlas[target + 1] = shapeAtlas[source + 1];
						shapeAtlas[target + 2] = shapeAtlas[source + 2];
						Copy(baseAtlas, source, target);
						if (specularAtlas != null)
						{
							Copy(specularAtlas, source, target);
						}

						if (emission != null)
						{
							Copy(emission, source, target);
						}
					}
				}
			});

			bake.Shape = shapeAtlas;
			bake.Base = baseAtlas;
			bake.Specular = specularAtlas;
			bake.Emission = emission;
		}

		private static byte[] ReadTexels(Framebuffer atlas, int attachment, int size)
		{
			return TexelConversion.ReadBytes(atlas.ColorAttachments[attachment].Gpu, 0, 0, 0, size, size);
		}

		// One view of the emission atlas (half floats) as floats.
		private static void ReadRadiance(GpuTexture emitted, int x, int y, int frameSize, ushort[] halves, float[] radiance)
		{
			emitted.GetData<ushort>(0, x, y, 0, frameSize, frameSize, 1, halves);
			TexelConversion.FromHalf(halves, radiance);
		}

		private static void Copy(byte[] texels, int source, int target)
		{
			texels[target] = texels[source];
			texels[target + 1] = texels[source + 1];
			texels[target + 2] = texels[source + 2];
			texels[target + 3] = texels[source + 3];
		}

		private static byte EncodeSrgb(float linear)
		{
			linear = Math.Clamp(linear, 0f, 1f);
			float encoded = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
			return (byte)Math.Clamp(encoded * 255f + 0.5f, 0f, 255f);
		}

		// ---------------------------------------------------------------- distance transforms

		// A view's coverage as a distance field into its frame of the atlas: the signed distance to the object's outline
		// among the capture's samples (exact, both ways), averaged over each texel's samples, 0.5 at the outline and
		// reaching 0 and 1 a spread of texels outside and inside it.
		private static void WriteCoverage(byte[] mask, int size, int samples, float spread, byte[] target, int atlasSize, int originX, int originY)
		{
			float[] toInside = SquaredDistances(mask, size, 1);
			float[] toOutside = SquaredDistances(mask, size, 0);
			int frame = size / samples;
			float scale = 1f / (samples * samples * samples);
			for (int y = 0; y < frame; y++)
			{
				for (int x = 0; x < frame; x++)
				{
					float sum = 0f;
					for (int sy = 0; sy < samples; sy++)
					{
						int index = (y * samples + sy) * size + x * samples;
						for (int sx = 0; sx < samples; sx++, index++)
						{
							// Between sample centres: the outline runs half a sample from the nearest one of the other kind.
							float distance = MathF.Sqrt(toInside[index]) - MathF.Sqrt(toOutside[index]);
							sum += distance > 0f ? distance - 0.5f : distance + 0.5f;
						}
					}

					// In texels of the frame, positive outside.
					float texels = sum * scale;
					float alpha = Math.Clamp(0.5f - texels / (2f * spread), 0f, 1f);
					target[(originY + y) * atlasSize + originX + x] = (byte)(alpha * 255f + 0.5f);
				}
			}
		}

		// For every sample the squared distance (in samples) to the nearest one whose mask is `site` (Felzenszwalb and
		// Huttenlocher's transform: columns, then rows).
		private static float[] SquaredDistances(byte[] mask, int size, byte site)
		{
			var result = new float[size * size];
			var f = new float[size];
			var d = new float[size];
			var v = new int[size];
			var z = new float[size + 1];
			for (int x = 0; x < size; x++)
			{
				for (int y = 0; y < size; y++)
				{
					f[y] = mask[y * size + x] == site ? 0f : Far;
				}

				Transform(f, size, d, v, z, null);
				for (int y = 0; y < size; y++)
				{
					result[y * size + x] = d[y];
				}
			}

			for (int y = 0; y < size; y++)
			{
				Array.Copy(result, y * size, f, 0, size);
				Transform(f, size, d, v, z, null);
				Array.Copy(d, 0, result, y * size, size);
			}

			return result;
		}

		// The nearest covered texel of every empty texel of a frame (as y x frameSize + x inside the frame; -1 for the
		// covered ones), or null when the frame has no covered texel at all.
		private static int[] NearestCovered(byte[] colour, int atlasSize, int originX, int originY, int frameSize)
		{
			var f = new float[frameSize];
			var d = new float[frameSize];
			var v = new int[frameSize];
			var z = new float[frameSize + 1];
			var argument = new int[frameSize];
			var columnDistance = new float[frameSize * frameSize];
			var columnNearest = new int[frameSize * frameSize];
			bool any = false;
			for (int x = 0; x < frameSize; x++)
			{
				for (int y = 0; y < frameSize; y++)
				{
					bool covered = colour[((originY + y) * atlasSize + originX + x) * 4 + 3] != 0;
					any |= covered;
					f[y] = covered ? 0f : Far;
				}

				Transform(f, frameSize, d, v, z, argument);
				for (int y = 0; y < frameSize; y++)
				{
					columnDistance[y * frameSize + x] = d[y];
					columnNearest[y * frameSize + x] = argument[y];
				}
			}

			if (!any)
			{
				return null;
			}

			var nearest = new int[frameSize * frameSize];
			for (int y = 0; y < frameSize; y++)
			{
				Array.Copy(columnDistance, y * frameSize, f, 0, frameSize);
				Transform(f, frameSize, d, v, z, argument);
				for (int x = 0; x < frameSize; x++)
				{
					if (colour[((originY + y) * atlasSize + originX + x) * 4 + 3] != 0 || d[x] >= Far * 0.5f)
					{
						nearest[y * frameSize + x] = -1;
						continue;
					}

					int fromX = argument[x];
					nearest[y * frameSize + x] = columnNearest[y * frameSize + fromX] * frameSize + fromX;
				}
			}

			return nearest;
		}

		// The lower envelope of the parabolas f[p] + (q - p)^2: d[q] its height at q, argument[q] (when asked) the p it
		// comes from. v and z are working room (n and n + 1 long).
		private static void Transform(float[] f, int n, float[] d, int[] v, float[] z, int[] argument)
		{
			int k = 0;
			v[0] = 0;
			z[0] = float.NegativeInfinity;
			z[1] = float.PositiveInfinity;
			for (int q = 1; q < n; q++)
			{
				// Where the parabola of q takes over from the last one of the envelope; the ones it hides go. The first
				// stays (it starts at minus infinity and the crossing is finite).
				float s = Crossing(f, v[k], q);
				while (s <= z[k])
				{
					k--;
					s = Crossing(f, v[k], q);
				}

				k++;
				v[k] = q;
				z[k] = s;
				z[k + 1] = float.PositiveInfinity;
			}

			k = 0;
			for (int q = 0; q < n; q++)
			{
				while (z[k + 1] < q)
				{
					k++;
				}

				int p = v[k];
				d[q] = (float)(q - p) * (q - p) + f[p];
				if (argument != null)
				{
					argument[q] = p;
				}
			}
		}

		// Where the parabolas of p and q (p before q) are equally high.
		private static float Crossing(float[] f, int p, int q)
		{
			return (f[q] + (float)q * q - (f[p] + (float)p * p)) / (2f * q - 2f * p);
		}

		#endregion
	}
}
