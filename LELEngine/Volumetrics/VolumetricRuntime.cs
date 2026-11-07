using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>
	///     Engine side of the volumetric objects: the enabled ones, the shared programs and detail texture, the work that
	///     makes and lights their clouds (a share per frame, <see cref="VolumetricPass" />), and their drawing in the
	///     transparent pass (<see cref="Draw" />). A draw marches the object into a target of the view at the object's
	///     resolution (Engine/Volumetrics/VolumetricMarch.shader), at reduced resolution against a checkerboard of the
	///     opaque scene's nearest and farthest depths (VolumetricDepth.shader), for the high quality preset through the
	///     temporal filter (VolumetricTemporal.shader, a history per camera and object), then blends it over the scene inside
	///     the object's screen rectangle (VolumetricComposite.shader).
	/// </summary>
	public static class VolumetricRuntime
	{
		#region PublicFields

		/// <summary>Voxels of work (making and lighting clouds) done per frame at most, over all objects.</summary>
		public static long WorkPerFrame = 8L << 20;

		/// <summary>The enabled volumetric objects (edit mode too).</summary>
		public static IReadOnlyList<VolumetricObjectBase> Objects => objects;

		/// <summary>
		///     True while a cloud is being made, or a temporal filter has not yet blended enough frames since its view or
		///     object last moved: the editor keeps its views refreshing until the cloud is complete and smooth.
		/// </summary>
		public static bool NeedsRefresh
		{
			get
			{
				foreach (VolumetricObjectBase volume in objects)
				{
					if (volume.isActiveAndEnabled && (!volume.Ready || volume.Data.Pending))
					{
						return true;
					}
				}

				foreach (KeyValuePair<(Rendering.Renderer, Camera, VolumetricObjectBase), History> pair in histories)
				{
					if (pair.Value.Stamp >= pair.Key.Item1.FrameIndex - 1 && pair.Value.Settled < SettleFrames && pair.Key.Item3.TemporalFilter)
					{
						return true;
					}
				}

				return false;
			}
		}

		/// <summary>Objects drawn last frame (stats).</summary>
		public static int DrawnCount { get; private set; }

		#endregion

		#region PrivateFields

		private const int DetailResolution = 96;

		// Frames a temporal filter blends with a still view before the editor may stop refreshing it.
		private const int SettleFrames = 32;

		/// <summary>
		///     LEL_VOL_DEBUG=1: every 120th frame the draws log their rectangles and the centre texel of their march (a
		///     readback: it stalls the GPU, debugging only).
		/// </summary>
		private static readonly bool Debug = Environment.GetEnvironmentVariable("LEL_VOL_DEBUG") == "1";

		// Texture units of the march (the lighting's own units stay free: shadow map 15, transmittance 10, sky 16).
		private const int LitUnit = 0;
		private const int OccupancyUnit = 1;
		private const int DetailUnit = 2;
		private const int LightsAUnit = 3;
		private const int LightsBUnit = 4;
		private const int DepthUnit = 5;
		private const int GIAUnit = 6;
		private const int GIBUnit = 7;
		private const int ResultUnit = 0;
		private const int ResultDepthUnit = 1;
		private const int SceneDepthUnit = 2;
		private const int HistoryUnit = 2;

		// A view's targets, per resolution (index 0 full, 1 half, 2 quarter): objects of different resolutions in one view
		// keep their own instead of resizing shared ones back and forth every frame.
		private sealed class ViewTargets
		{
			public readonly Framebuffer[] March = new Framebuffer[3];
			public readonly Framebuffer[] Depth = new Framebuffer[3];
			public readonly long[] DepthStamp = { -1, -1, -1 };
		}

		private sealed class History
		{
			public readonly Framebuffer[] Targets = new Framebuffer[2];
			public int Current;
			public bool Valid;
			public Matrix4 PreviousViewProjection;
			public Matrix4 PreviousLocalToWorld;
			public uint Frame;

			// The renderer's frame it was last continued in (its own count: the editor's scene and game views render in one
			// frame, and a count shared by them restarted every history each frame).
			public long Stamp;

			// Frames in a row with the same view and object transform.
			public int Settled;
		}

		private static readonly List<VolumetricObjectBase> objects = new List<VolumetricObjectBase>();
		private static readonly Dictionary<(Rendering.Renderer, Camera), ViewTargets> views = new Dictionary<(Rendering.Renderer, Camera), ViewTargets>();
		private static readonly Dictionary<(Rendering.Renderer, Camera, VolumetricObjectBase), History> histories = new Dictionary<(Rendering.Renderer, Camera, VolumetricObjectBase), History>();
		private static readonly Vector4[] lightColors = new Vector4[VolumetricObjectBase.MaxLights];
		private static readonly List<(Rendering.Renderer, Camera, VolumetricObjectBase)> staleHistories = new List<(Rendering.Renderer, Camera, VolumetricObjectBase)>();

		private static ComputeShader generate;
		private static ComputeShader generatePoints;
		private static ComputeShader light;
		private static ComputeShader lightTransport;
		private static ComputeShader occupancy;
		private static ComputeShader giShader;
		private static ShaderProgram march;
		private static ShaderProgram marchHQ;
		private static ShaderProgram depthDownsample;
		private static ShaderProgram temporal;
		private static ShaderProgram composite;
		private static int detailTexture;
		private static long frameStamp;
		private static int drawnThisFrame;

		#endregion

		#region InternalProperties

		internal static ComputeShader Generate => generate ??= new ComputeShader("Engine/Volumetrics/VolumetricGenerate.shader");
		internal static ComputeShader GeneratePoints => generatePoints ??= new ComputeShader("Engine/Volumetrics/VolumetricGenerate.shader", "VOL_POINTS");
		internal static ComputeShader Light => light ??= new ComputeShader("Engine/Volumetrics/VolumetricLight.shader");
		internal static ComputeShader LightTransport => lightTransport ??= new ComputeShader("Engine/Volumetrics/VolumetricLightTransport.shader");
		internal static ComputeShader OccupancyShader => occupancy ??= new ComputeShader("Engine/Volumetrics/VolumetricOccupancy.shader");
		internal static ComputeShader GIShader => giShader ??= new ComputeShader("Engine/Volumetrics/VolumetricGI.shader");

		/// <summary>The scene has GI this frame that a volume can read (voxel cone tracing's volume or Lumen's radiance cache).</summary>
		internal static bool SceneGIAvailable => Lighting.GI.Enabled
			&& (Lighting.GIMethod == GIMode.VoxelConeTracing && Lighting.GI.VoxelTexture != 0 || Lighting.GIMethod == GIMode.Lumen && Lighting.RadianceCache.Valid);

		#endregion

		#region PublicMethods

		internal static void Register(VolumetricObjectBase volume)
		{
			if (!objects.Contains(volume))
			{
				objects.Add(volume);
			}
		}

		internal static void Unregister(VolumetricObjectBase volume)
		{
			objects.Remove(volume);
			foreach (KeyValuePair<(Rendering.Renderer, Camera, VolumetricObjectBase), History> pair in histories)
			{
				if (pair.Key.Item3 == volume)
				{
					staleHistories.Add(pair.Key);
				}
			}

			foreach ((Rendering.Renderer, Camera, VolumetricObjectBase) key in staleHistories)
			{
				Delete(histories[key]);
				histories.Remove(key);
			}

			staleHistories.Clear();
		}

		/// <summary>
		///     Once per frame and renderer (<see cref="VolumetricPass" />): makes and lights the clouds that need it, a share
		///     of the work per frame.
		/// </summary>
		public static void Update()
		{
			frameStamp++;
			DrawnCount = drawnThisFrame;
			drawnThisFrame = 0;
			long budget = WorkPerFrame;
			foreach (VolumetricObjectBase volume in objects)
			{
				if (!volume.isActiveAndEnabled || volume.gameObject?.scene == null)
				{
					continue;
				}

				bool settle = !volume.gameObject.scene.IsPlaying;
				budget -= volume.Data.Update(Math.Max(budget, 1), settle);
				if (budget <= 0)
				{
					break;
				}
			}

			// The clouds that receive the GI: a share of their GI grid (last frame's GI).
			if (SceneGIAvailable)
			{
				foreach (VolumetricObjectBase volume in objects)
				{
					if (volume.ReceiveGI && volume.isActiveAndEnabled && volume.Ready)
					{
						volume.Data.UpdateGI();
					}
				}
			}

			GL.UseProgram(0);
		}

		/// <summary>
		///     For each light with volumetric shadows: how much of it gets through the clouds towards +x, -x, +y, -y, +z, -z
		///     within its range (<see cref="global::Light.CloudTransmittance" />), through every ready object its range reaches,
		///     on the CPU from their voxels (Rendering.LightBuffer, every frame). The lit materials and the GI dim the light by
		///     it in the direction of what they light: a flash deep in a cloud lights what is around the cloud less.
		/// </summary>
		public static void UpdateLightTransmittance(IReadOnlyList<global::Light> lights)
		{
			foreach (global::Light light in lights)
			{
				float[] transmittance = light.CloudTransmittance;
				for (int k = 0; k < 6; k++)
				{
					transmittance[k] = 1f;
				}

				if (!light.VolumetricShadows || objects.Count == 0)
				{
					continue;
				}

				Vector3 origin = light.transform.position;
				foreach (VolumetricObjectBase volume in objects)
				{
					if (!volume.isActiveAndEnabled || !volume.Ready || !Reaches(volume, origin, light.Range))
					{
						continue;
					}

					for (int k = 0; k < 6; k++)
					{
						// The straight path, and the light scattered on through the cloud that gets further (the cloud's glow, as
						// its own light transport has it: e^-tau + glow e^-0.15 tau).
						float straight = volume.Transmittance(origin, origin + Axes[k] * light.Range, 24);
						transmittance[k] *= MathF.Min(1f, straight + volume.LightGlow * MathF.Pow(straight, 0.15f));
					}
				}
			}
		}

		/// <summary>The objects a camera draws: enabled, ready, on its layers, their bounds in its view.</summary>
		public static void Collect(RenderContext context, List<VolumetricObjectBase> visible)
		{
			visible.Clear();
			if (objects.Count == 0)
			{
				return;
			}

			var screen = new PixelRect(0, 0, context.Width, context.Height);
			foreach (VolumetricObjectBase volume in objects)
			{
				if (!volume.isActiveAndEnabled || !volume.Ready || (context.CullingMask & (1 << (volume.gameObject.layer & 31))) == 0)
				{
					continue;
				}

				VolumetricData data = volume.Data;
				Matrix4 toClip = volume.transform.LocalToWorld * context.ViewProjection;
				if (SceneGrab.ScreenRect(toClip, -data.Size * 0.5f, data.Size * 0.5f, screen, out PixelRect rect, out _) && rect.IsEmpty)
				{
					continue;
				}

				visible.Add(volume);
			}
		}

		/// <summary>
		///     Draws one object over the scene target (bound when this returns): its march, its temporal filter (high
		///     quality), its composite. <paramref name="depth" /> is the opaque scene's depth copy of this view.
		/// </summary>
		public static void Draw(RenderContext context, VolumetricObjectBase volume, VFX.VfxDepthCapture depth)
		{
			VolumetricData data = volume.Data;
			if (!data.Ready || depth == null || !depth.Valid || depth.Depth == null)
			{
				return;
			}

			EnsurePrograms();
			int width = context.Width;
			int height = context.Height;
			var screen = new PixelRect(0, 0, width, height);
			Matrix4 localToWorld = volume.transform.LocalToWorld;
			Matrix4 worldToLocal = localToWorld.Inverted();
			if (!SceneGrab.ScreenRect(localToWorld * context.ViewProjection, -data.Size * 0.5f, data.Size * 0.5f, screen, out PixelRect rect, out _))
			{
				rect = screen;
			}

			if (rect.IsEmpty)
			{
				return;
			}

			if (Debug && frameStamp % 120 == 1)
			{
				Console.WriteLine($"[Volumetrics] Draw {volume.gameObject?.Name}: rect {rect}, resolution {volume.Resolution}, camera {context.CameraPosition}, local camera {Vector3.TransformPosition(context.CameraPosition, worldToLocal)}");
			}

			bool hq = volume is VolumetricObjectHQ;
			int level = volume.Resolution switch { VolumetricResolution.Half => 1, VolumetricResolution.Quarter => 2, _ => 0 };
			int scale = 1 << level;
			int lowWidth = (width + scale - 1) / scale;
			int lowHeight = (height + scale - 1) / scale;
			ViewTargets view = Targets(context);
			Matrix4 invViewProjection = context.ViewProjection.Inverted();
			Vector4 depthParams = SceneGrab.DepthParams(context.Projection);

			GLState.SetDepth(false, false);
			GLState.SetCull(false);
			GLState.SetBlend(false);

			// The opaque scene's depth at the march's resolution (once per frame, view and resolution).
			Framebuffer lowDepth = null;
			if (scale > 1)
			{
				Ensure(ref view.Depth[level], lowWidth, lowHeight, RenderTextureFormat.R32F, false, null);
				lowDepth = view.Depth[level];
				if (view.DepthStamp[level] != frameStamp)
				{
					lowDepth.Bind();
					depthDownsample.Use();
					depthDownsample.SetTexture("volSceneDepth", TextureTarget.Texture2D, depth.Depth.Handle, 0);
					depthDownsample.SetVector4("volDepthParams", depthParams);
					depthDownsample.SetInt("volScale", scale);
					context.Fullscreen.Draw();
					view.DepthStamp[level] = frameStamp;
				}
			}

			// The march, inside the object's rectangle at its resolution: light and transmittance, and the light's mean
			// distance along the ray (the temporal filter reprojects with it).
			Ensure(ref view.March[level], lowWidth, lowHeight, RenderTextureFormat.RGBA16F, true, RenderTextureFormat.R32F);
			Framebuffer target = view.March[level];

			var lowRect = new PixelRect(rect.X0 / scale, rect.Y0 / scale, Math.Min(lowWidth, (rect.X1 + scale - 1) / scale + 1), Math.Min(lowHeight, (rect.Y1 + scale - 1) / scale + 1));
			History history = volume.TemporalFilter ? HistoryOf(context, volume, lowWidth, lowHeight) : null;
			target.Bind();
			GL.Enable(EnableCap.ScissorTest);
			// A temporal filter reads the march around each pixel: march a texel more on every side.
			int margin = history != null ? 2 : 0;
			GL.Scissor(Math.Max(0, lowRect.X0 - margin), Math.Max(0, lowRect.Y0 - margin), lowRect.X1 - lowRect.X0 + margin * 2, lowRect.Y1 - lowRect.Y0 + margin * 2);
			GL.ClearColor(0f, 0f, 0f, 0f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			ShaderProgram program = hq ? marchHQ : march;
			program.Use();
			SetViewUniforms(program, context, volume, data, worldToLocal, invViewProjection, lowWidth, lowHeight);
			SetMarchUniforms(program, context, volume, data, localToWorld, scale, lowHeight, history, depth, lowDepth, depthParams);
			context.Fullscreen.Draw();

			if (Debug && frameStamp % 120 == 1)
			{
				var probe = new float[4];
				GL.ReadPixels((lowRect.X0 + lowRect.X1) / 2, (lowRect.Y0 + lowRect.Y1) / 2, 1, 1, PixelFormat.Rgba, PixelType.Float, probe);
				Console.WriteLine($"[Volumetrics] March {volume.gameObject?.Name}: centre texel ({probe[0]:0.000}, {probe[1]:0.000}, {probe[2]:0.000}, {probe[3]:0.000}), grid {data.Resolution}, blocks {data.Blocks}, lit {data.Lit}, occupancy {data.Occupancy}");
			}

			// The temporal filter: this frame's march blended with the history, into the other history target.
			RenderTexture result = target.ColorAttachments[0];
			if (history != null)
			{
				Framebuffer next = history.Targets[1 - history.Current];
				next.Bind();
				GL.Scissor(lowRect.X0, lowRect.Y0, lowRect.X1 - lowRect.X0, lowRect.Y1 - lowRect.Y0);
				GL.ClearColor(0f, 0f, 0f, 0f);
				GL.Clear(ClearBufferMask.ColorBufferBit);
				temporal.Use();
				SetViewUniforms(temporal, context, volume, data, worldToLocal, invViewProjection, lowWidth, lowHeight);
				temporal.SetTexture("volCurrent", TextureTarget.Texture2D, target.ColorAttachments[0].Handle, ResultUnit);
				temporal.SetTexture("volCurrentDistance", TextureTarget.Texture2D, target.ColorAttachments[1].Handle, ResultDepthUnit);
				temporal.SetTexture("volHistory", TextureTarget.Texture2D, history.Targets[history.Current].ColorAttachments[0].Handle, HistoryUnit);
				temporal.SetMatrix4("volPreviousViewProjection", history.PreviousViewProjection);
				temporal.SetMatrix4("volPreviousLocalToWorld", history.PreviousLocalToWorld);
				temporal.SetFloat("volHistoryWeight", Math.Clamp(volume.HistoryWeight, 0f, 0.98f));
				temporal.SetInt("volHistoryValid", history.Valid ? 1 : 0);
				context.Fullscreen.Draw();
				history.Settled = history.Valid && history.PreviousViewProjection == context.ViewProjection && history.PreviousLocalToWorld == localToWorld ? history.Settled + 1 : 0;
				history.Current = 1 - history.Current;
				history.Valid = true;
				history.PreviousViewProjection = context.ViewProjection;
				history.PreviousLocalToWorld = localToWorld;
				history.Frame++;
				history.Stamp = context.Renderer.FrameIndex;
				result = next.ColorAttachments[0];
			}

			// Blended over the scene inside the object's rectangle.
			context.SceneTarget.Bind();
			GL.Scissor(rect.X0, rect.Y0, rect.X1 - rect.X0, rect.Y1 - rect.Y0);
			GL.Enable(EnableCap.Blend);
			GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.Zero, BlendingFactorDest.One);
			composite.Use();
			SetViewUniforms(composite, context, volume, data, worldToLocal, invViewProjection, width, height);
			composite.SetTexture("volResult", TextureTarget.Texture2D, result.Handle, ResultUnit);
			composite.SetTexture("volResultDepth", TextureTarget.Texture2D, lowDepth != null ? lowDepth.ColorAttachments[0].Handle : 0, ResultDepthUnit);
			composite.SetTexture("volSceneDepth", TextureTarget.Texture2D, depth.Depth.Handle, SceneDepthUnit);
			composite.SetVector4("volDepthParams", depthParams);
			composite.SetInt("volUpsample", scale > 1 ? 1 : 0);
			context.Fullscreen.Draw();

			GL.Disable(EnableCap.ScissorTest);
			GLState.SetBlend(false);
			GLState.SetDepth(true, false, DepthFunction.Lequal);
			GLState.SetCull(true, TriangleFace.Back);
			for (int unit = 0; unit <= GIBUnit; unit++)
			{
				GL.ActiveTexture(TextureUnit.Texture0 + unit);
				GL.BindTexture(TextureTarget.Texture3D, 0);
				GL.BindTexture(TextureTarget.Texture2D, 0);
			}

			GL.ActiveTexture(TextureUnit.Texture0);
			drawnThisFrame++;
		}

		/// <summary>Drops the targets and histories of a renderer's views (its scene was replaced or it is going away).</summary>
		public static void ReleaseRenderer(Rendering.Renderer renderer)
		{
			var stale = new List<(Rendering.Renderer, Camera)>();
			foreach (KeyValuePair<(Rendering.Renderer, Camera), ViewTargets> pair in views)
			{
				if (pair.Key.Item1 == renderer)
				{
					stale.Add(pair.Key);
				}
			}

			foreach ((Rendering.Renderer, Camera) key in stale)
			{
				ViewTargets view = views[key];
				for (int i = 0; i < 3; i++)
				{
					view.March[i]?.Delete();
					view.Depth[i]?.Delete();
				}

				views.Remove(key);
			}

			foreach (KeyValuePair<(Rendering.Renderer, Camera, VolumetricObjectBase), History> pair in histories)
			{
				if (pair.Key.Item1 == renderer)
				{
					staleHistories.Add(pair.Key);
				}
			}

			foreach ((Rendering.Renderer, Camera, VolumetricObjectBase) key in staleHistories)
			{
				Delete(histories[key]);
				histories.Remove(key);
			}

			staleHistories.Clear();
		}

		#endregion

		#region PrivateMethods

		private static void EnsurePrograms()
		{
			march ??= new ShaderProgram("Engine/Volumetrics/VolumetricMarch.shader");
			marchHQ ??= new ShaderProgram("Engine/Volumetrics/VolumetricMarch.shader", new[] { "VOL_HQ" });
			depthDownsample ??= new ShaderProgram("Engine/Volumetrics/VolumetricDepth.shader");
			temporal ??= new ShaderProgram("Engine/Volumetrics/VolumetricTemporal.shader");
			composite ??= new ShaderProgram("Engine/Volumetrics/VolumetricComposite.shader");
			if (detailTexture == 0)
			{
				detailTexture = MakeDetail();
			}
		}

		// The tiling detail noise every object shares: made once.
		private static int MakeDetail()
		{
			int levels = 1 + (int)MathF.Floor(MathF.Log2(DetailResolution));
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexStorage3D(TextureTarget3d.Texture3D, levels, SizedInternalFormat.R8, DetailResolution, DetailResolution, DetailResolution);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, levels - 1);
			GL.BindTexture(TextureTarget.Texture3D, 0);

			var shader = new ComputeShader("Engine/Volumetrics/VolumetricDetail.shader");
			shader.Use();
			shader.Program.SetInt("volResolution", DetailResolution);
			GL.BindImageTexture(0, texture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R8);
			GL.DispatchCompute(DetailResolution / 8, DetailResolution / 8, DetailResolution);
			GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R8);
			GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			shader.Delete();
			return texture;
		}

		private static ViewTargets Targets(RenderContext context)
		{
			(Rendering.Renderer, Camera) key = (context.Renderer, context.Camera);
			if (!views.TryGetValue(key, out ViewTargets view))
			{
				view = new ViewTargets();
				views[key] = view;
			}

			return view;
		}

		private static History HistoryOf(RenderContext context, VolumetricObjectBase volume, int width, int height)
		{
			(Rendering.Renderer, Camera, VolumetricObjectBase) key = (context.Renderer, context.Camera, volume);
			if (!histories.TryGetValue(key, out History history))
			{
				history = new History();
				histories[key] = history;
			}

			// A history not continued in this renderer's last frame (the object was off screen) starts over.
			if (history.Stamp < context.Renderer.FrameIndex - 1)
			{
				history.Valid = false;
			}

			for (int i = 0; i < 2; i++)
			{
				if (history.Targets[i] == null || history.Targets[i].Width != width || history.Targets[i].Height != height)
				{
					history.Valid = false;
				}

				Ensure(ref history.Targets[i], width, height, RenderTextureFormat.RGBA16F, true, null);
			}

			return history;
		}

		// A framebuffer of this size with one colour target (and a second one), made or resized.
		private static void Ensure(ref Framebuffer framebuffer, int width, int height, RenderTextureFormat format, bool linear, RenderTextureFormat? second)
		{
			if (framebuffer == null)
			{
				framebuffer = new Framebuffer(width, height);
				framebuffer.AddColorAttachment(format, linear);
				if (second.HasValue)
				{
					framebuffer.AddColorAttachment(second.Value, false);
				}

				framebuffer.Validate();
				return;
			}

			framebuffer.Resize(width, height);
		}

		private static void SetViewUniforms(ShaderProgram program, RenderContext context, VolumetricObjectBase volume, VolumetricData data, Matrix4 worldToLocal, Matrix4 invViewProjection, int width, int height)
		{
			program.SetMatrix4("volInvViewProjection", invViewProjection);
			program.SetVector3("volCameraPosition", context.CameraPosition);
			program.SetVector3("volCameraForward", context.CameraForward);
			program.SetMatrix4("volWorldToLocal", worldToLocal);
			program.SetVector3("volSize", data.Size);
			program.SetInt("volShape", (int)data.Shape);
			program.SetVector2("volTargetSize", new Vector2(width, height));
		}

		private static void SetMarchUniforms(ShaderProgram program, RenderContext context, VolumetricObjectBase volume, VolumetricData data, Matrix4 localToWorld, int scale, int lowHeight, History history, VFX.VfxDepthCapture depth, Framebuffer lowDepth, Vector4 depthParams)
		{
			// Every sampler on a unit of its own (unused ones too: two sampler types on one unit fail the draw).
			program.SetInt("ShadowMap", Lighting.ShadowMapTextureUnit);
			program.SetInt("ShadowTransmittance", Lighting.ShadowTransmittanceTextureUnit);
			program.SetInt("giSkybox", Lighting.SkyboxTextureUnit);
			program.SetTexture("volLit", TextureTarget.Texture3D, data.Lit, LitUnit);
			program.SetTexture("volOccupancy", TextureTarget.Texture3D, data.Occupancy, OccupancyUnit);
			program.SetTexture("volDetail", TextureTarget.Texture3D, detailTexture, DetailUnit);
			bool lights = data.LightsA != 0 && AnyLightLit(volume, data);
			program.SetTexture("volLightsA", TextureTarget.Texture3D, lights ? data.LightsA : 0, LightsAUnit);
			program.SetTexture("volLightsB", TextureTarget.Texture3D, lights ? data.LightsB : 0, LightsBUnit);
			if (lowDepth != null)
			{
				program.SetTexture("volDepth", TextureTarget.Texture2D, lowDepth.ColorAttachments[0].Handle, DepthUnit);
				program.SetInt("volDepthIsWindow", 0);
			}
			else
			{
				program.SetTexture("volDepth", TextureTarget.Texture2D, depth.Depth.Handle, DepthUnit);
				program.SetInt("volDepthIsWindow", 1);
			}

			program.SetVector4("volDepthParams", depthParams);

			float noiseSize = MathF.Max(volume.NoiseSize, 0.01f);
			float step = data.Voxel * Math.Clamp(volume.StepSize, 0.1f, 8f);
			program.SetVector3("volBlocks", new Vector3(data.Blocks.X, data.Blocks.Y, data.Blocks.Z));
			program.SetFloat("volVoxel", data.Voxel);
			program.SetFloat("volSigma", data.Sigma);
			program.SetFloat("volTauMax", VolumetricData.TauMax);
			program.SetFloat("volStep", step);
			program.SetInt("volMaxSteps", Math.Clamp(volume.MaxSteps, 4, 1024));
			// Radians per pixel of the march's target at the view's centre (cot of half the field of view in M22).
			program.SetFloat("volPixelAngle", 2f / (MathF.Abs(context.Projection.M22) * lowHeight));
			if (volume is VolumetricObjectHQ hq)
			{
				program.SetFloat("volMinStep", data.Voxel * Math.Clamp(hq.MinStep, 0.01f, 4f));
				program.SetFloat("volNearStepScale", Math.Clamp(hq.NearStepScale, 0.0001f, 1f));
			}

			// A new sampling pattern every frame under the temporal filter (golden ratio steps: every offset in turn).
			program.SetFloat("volJitterOffset", history != null ? history.Frame * 0.618034f % 1f : 0f);

			// The sun, the sky and the flat ambient light (the scene's, or the volume's own). The scene's shadow map is its
			// main light's: only a cloud lit by that light receives it.
			DirectionalLight sun = volume.EffectiveSun;
			bool shadows = volume.ReceiveShadows && sun != null && sun == DirectionalLight.This;
			program.SetInt("volReceiveShadows", shadows ? 1 : 0);
			program.SetFloat("volShadowStrength", Math.Clamp(volume.ShadowStrength, 0f, 1f));
			Lighting.SetShadowUniforms(program, shadows);
			Vector3 towards = sun != null ? -sun.transform.forward : Vector3.UnitY;
			Vector3 sunLocal = Vector3.TransformVector(towards, localToWorld.Inverted());
			sunLocal = sunLocal.LengthSquared > 1e-12f ? sunLocal.Normalized() : Vector3.UnitY;
			Vector3 sunColor = sun != null ? new Vector3(sun.Color.R, sun.Color.G, sun.Color.B) * (sun.Strength * volume.SunStrength) : Vector3.Zero;
			program.SetVector3("volSunDirection", sunLocal);
			program.SetVector3("volSunColor", sunColor);
			Lighting.SetSkyUniforms(program);
			program.SetFloat("volSkyStrength", volume.SkyStrength);
			Vector3 ambient = volume.OwnAmbient
				? new Vector3(volume.AmbientColor.R, volume.AmbientColor.G, volume.AmbientColor.B)
				: new Vector3(Lighting.Ambient.Color.R, Lighting.Ambient.Color.G, Lighting.Ambient.Color.B) * Lighting.Ambient.Strength;
			program.SetVector3("volAmbientColor", ambient * volume.AmbientStrength);
			program.SetVector3("volAlbedo", new Vector3(volume.Albedo.R, volume.Albedo.G, volume.Albedo.B));
			program.SetVector3("volEmission", new Vector3(volume.EmissionColor.R, volume.EmissionColor.G, volume.EmissionColor.B) * volume.EmissionStrength);
			program.SetFloat("volPhaseG", Math.Clamp(volume.PhaseG, -0.95f, 0.95f));
			program.SetFloat("volMultipleScattering", volume.MultipleScattering);

			// The GI it receives, once its grid has been worked out whole.
			bool gi = volume.ReceiveGI && data.GIValid && SceneGIAvailable;
			program.SetInt("volGIActive", gi ? 1 : 0);
			program.SetTexture("volGIA", TextureTarget.Texture3D, gi ? data.GIA : 0, GIAUnit);
			program.SetTexture("volGIB", TextureTarget.Texture3D, gi ? data.GIB : 0, GIBUnit);
			program.SetFloat("volGIStrength", volume.GIStrength);

			// The detail: its tile in local units, drifting with the wind.
			float detailTile = MathF.Max(volume.DetailSize, 0.01f) * noiseSize;
			float time = (float)Time.timeD;
			program.SetFloat("volDetailStrength", volume.DetailStrength);
			program.SetFloat("volDetailShade", volume.DetailShade);
			program.SetFloat("volEdgeBand", MathF.Max(volume.EdgeBand, 0.05f));
			program.SetFloat("volDetailFrequency", 1f / detailTile);
			program.SetFloat("volDetailVoxel", detailTile / DetailResolution);
			program.SetVector3("volDetailOffset", -volume.DetailWind * time);

			// The lights (its own and the scene's, as their transport was worked out): colour x intensity right now.
			program.SetInt("volLightsActive", lights ? 1 : 0);
			if (lights)
			{
				for (int i = 0; i < lightColors.Length; i++)
				{
					lightColors[i] = i < data.LightCount ? new Vector4(data.LightSlots[i].Color, 0f) : Vector4.Zero;
				}

				program.SetVector4Array("volLightColors", lightColors);
			}

			program.SetMatrix4("volLocalToWorld", localToWorld);
			program.SetFloat("volExposure", volume.Exposure);
			program.SetFloat("volOpacity", Math.Clamp(volume.Opacity, 0f, 1f));
		}

		private static readonly Vector3[] Axes = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };

		// Whether a sphere (world) reaches the object's box.
		private static bool Reaches(VolumetricObjectBase volume, Vector3 center, float radius)
		{
			Vector3 local = Vector3.TransformPosition(center, volume.transform.WorldToLocal);
			Vector3 half = volume.Data.Size * 0.5f;
			Vector3 outside = Vector3.ComponentMax(Vector3.ComponentMax(-half - local, local - half), Vector3.Zero);
			return outside.Length * volume.WorldScale < radius;
		}

		private static bool AnyLightLit(VolumetricObjectBase volume, VolumetricData data)
		{
			for (int i = 0; i < data.LightCount; i++)
			{
				Vector3 color = data.LightSlots[i].Color;
				if (color.X + color.Y + color.Z > 1e-6f)
				{
					return true;
				}
			}

			return false;
		}

		private static void Delete(History history)
		{
			for (int i = 0; i < 2; i++)
			{
				history.Targets[i]?.Delete();
				history.Targets[i] = null;
			}
		}

		#endregion
	}
}
