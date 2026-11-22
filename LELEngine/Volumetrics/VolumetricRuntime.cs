using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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
		private const int LightsCUnit = 8;
		private const int ResultUnit = 0;
		private const int ResultDepthUnit = 1;
		private const int SceneDepthUnit = 2;
		private const int DynamicResultUnit = 3;
		private const int HistoryUnit = 2;
		private const int HistoryMetaUnit = 3;

		// A history texel matches when its light lay within this share of the distance expected (the light-weighted mean
		// distance moves with the march's jitter by a few percent; an object in front, or other cloud, is far further off).
		private const float HistoryDistanceTolerance = 0.2f;

		// A view's targets, per march scale (index = scene pixels per march pixel along each axis, 1..MaxMarchScale): objects
		// of different resolutions in one view keep their own instead of resizing shared ones back and forth every frame.
		private sealed class ViewTargets
		{
			public readonly Framebuffer[] March = new Framebuffer[MaxMarchScale + 1];
			public readonly Framebuffer[] Depth = new Framebuffer[MaxMarchScale + 1];
			public readonly long[] DepthStamp = { -1, -1, -1, -1, -1, -1, -1, -1, -1 };
		}

		private const int MaxMarchScale = 8;

		/// <summary>
		///     Scene pixels per march pixel: the object's resolution (1, 2, 4) against the window's, so a super sampled scene
		///     (<see cref="RenderContext.SuperSampling" />) marches about as many pixels as the window has (soft light needs
		///     no more): the whole number nearest to resolution x scale on a log scale (1.33 -> 1, 1.5 -> 2, 2.67 -> 3).
		/// </summary>
		private static int MarchScale(int resolution, float superSampling)
		{
			float wanted = resolution * Math.Max(1f, superSampling);
			int below = Math.Max(1, (int)MathF.Floor(wanted));
			int scale = wanted / below <= (below + 1) / wanted ? below : below + 1;
			return Math.Clamp(scale, 1, MaxMarchScale);
		}

		private sealed class History
		{
			public readonly Framebuffer[] Targets = new Framebuffer[2];
			public int Current;
			public bool Valid;
			public Matrix4 PreviousViewProjection;
			public Matrix4 PreviousLocalToWorld;
			public Vector3 PreviousCameraPosition;
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
		private static readonly Vector4[] directionalDirs = new Vector4[DirectionalLight.MaxLights];
		private static readonly Vector4[] directionalColors = new Vector4[DirectionalLight.MaxLights];
		private static readonly int[] directionalSlots = new int[DirectionalLight.MaxLights];
		private static readonly List<(Rendering.Renderer, Camera, VolumetricObjectBase)> staleHistories = new List<(Rendering.Renderer, Camera, VolumetricObjectBase)>();

		private static ComputeShader generate;
		private static ComputeShader generatePoints;
		private static ComputeShader light;
		private static ComputeShader lightTransport;
		private static ComputeShader lightTransportBaked;
		private static ComputeShader occupancy;
		private static ComputeShader giShader;
		// The march's programs by variant: bit 0 the high quality preset, bit 1 live lights lit, bit 2 baked lights, bit 3
		// dynamic lights.
		private static readonly ShaderProgram[] marchPrograms = new ShaderProgram[16];

		// The dynamic lights' uniform buffer (VolumetricMarch.shader's VolDynamicLights block): positions, colours, spots and
		// cones, MaxDynamicLights each, a block after the other.
		private const int DynamicLightBinding = 8;
		private static readonly float[] dynamicData = new float[VolumetricObjectBase.MaxDynamicLights * 4 * 4];
		private static readonly List<VolumeLightSlot> dynamicSlots = new List<VolumeLightSlot>();
		private static int dynamicBuffer;
		private static ShaderProgram depthDownsample;
		private static ShaderProgram temporal;
		private static ShaderProgram composite;

		// Bit 0 / 1: the programs of the standard / high quality preset were made (StartPrograms).
		private static int programsStarted;
		private static int detailTexture;
		private static long frameStamp;
		private static int drawnThisFrame;

		#endregion

		#region InternalProperties

		internal static ComputeShader Generate => generate ??= new ComputeShader("Engine/Volumetrics/VolumetricGenerate.shader");
		internal static ComputeShader GeneratePoints => generatePoints ??= new ComputeShader("Engine/Volumetrics/VolumetricGenerate.shader", "VOL_POINTS");
		internal static ComputeShader Light => light ??= new ComputeShader("Engine/Volumetrics/VolumetricLight.shader");
		internal static ComputeShader LightTransport => lightTransport ??= new ComputeShader("Engine/Volumetrics/VolumetricLightTransport.shader");
		internal static ComputeShader LightTransportBaked => lightTransportBaked ??= new ComputeShader("Engine/Volumetrics/VolumetricLightTransport.shader", "VOL_BAKED");
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

			StartPrograms(volume.HighQuality);
		}

		/// <summary>
		///     Makes every program the clouds use, so their builds start now (a first cloud enabled: while a scene starts)
		///     and run in the driver while the cloud's voxels are read, which takes frames; they were made one by one where a
		///     frame first needed each, the frames the first cloud appeared in (seconds with nothing compiled yet, on the
		///     780M 3.4 s and 0.9 s in two frames of the game). Needs the GL context (else the first use makes them, as before).
		/// </summary>
		internal static void StartPrograms(bool highQuality)
		{
			int preset = highQuality ? 1 : 0;
			if ((programsStarted & (1 << preset)) != 0 || !GraphicsThread.HasContext)
			{
				return;
			}

			long started = FrameProfiler.Timestamp;
			bool first = programsStarted == 0;
			programsStarted |= 1 << preset;
			if (first)
			{
				_ = Generate;
				_ = GeneratePoints;
				_ = Light;
				_ = LightTransport;
				_ = LightTransportBaked;
				_ = OccupancyShader;
				_ = GIShader;
				VolumetricMedia.StartPrograms();
				// With the detail noise every cloud shares (its own program runs once, here).
				EnsurePrograms();
			}

			// The preset's eight variants (which lights shine on the cloud right now picks one).
			MarchProgram(highQuality, false, false, false);
			FrameProfiler.EventSince(started, "volumetrics", highQuality ? "programs (high quality march)" : "programs", "every program of the clouds made, their builds started (a first cloud enabled)");
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
			VolumeCache.Update();
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
				// Worked out again only when what it depends on changed: where the light is and how far it reaches, and the
				// clouds within its range (which ones, where they are, what they hold). Steady lights in a cloud cost
				// nothing (144 samples of the voxels per light and cloud every frame before: 1.6 ms of the CPU for thirty
				// lamps in a storm).
				Vector3 origin = light.transform.position;
				var state = new HashCode();
				state.Add(origin);
				state.Add(light.Range);
				state.Add(light.VolumetricShadows);
				if (light.VolumetricShadows)
				{
					foreach (VolumetricObjectBase volume in objects)
					{
						if (volume.isActiveAndEnabled && volume.Ready && Reaches(volume, origin, light.Range))
						{
							state.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(volume));
							state.Add(volume.CloudVersion);
							state.Add(volume.transform.LocalToWorld);
							state.Add(volume.LightGlow);
							state.Add(volume.Data.Sigma);
						}
					}
				}

				int key = state.ToHashCode();
				if (light.CloudTransmittanceKnown && light.CloudTransmittanceKey == key)
				{
					continue;
				}

				light.CloudTransmittanceKey = key;
				light.CloudTransmittanceKnown = true;
				float[] transmittance = light.CloudTransmittance;
				for (int k = 0; k < 6; k++)
				{
					transmittance[k] = 1f;
				}

				if (!light.VolumetricShadows || objects.Count == 0)
				{
					continue;
				}

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
				if (!volume.isActiveAndEnabled || !volume.Ready || (context.CullingMask & (1 << (volume.gameObject.layer & 31))) == 0 || context.Hides(volume.gameObject))
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
			int scale = MarchScale(volume.Resolution switch { VolumetricResolution.Half => 2, VolumetricResolution.Quarter => 4, _ => 1 }, context.SuperSampling);
			int level = scale;
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

			// The dynamic lights that reach it this frame (worked out in the march, nothing made beforehand).
			volume.GatherDynamicLights(dynamicSlots, worldToLocal, context.CameraPosition);
			bool dynamic = dynamicSlots.Count > 0;

			// The march, inside the object's rectangle at its resolution: light and transmittance, and the light's mean
			// distance along the ray (the temporal filter reprojects with it); with dynamic lights also their light, kept
			// out of the temporal filter (a third target, made the first time a cloud has them).
			Ensure(ref view.March[level], lowWidth, lowHeight, RenderTextureFormat.RGBA16F, true, RenderTextureFormat.R32F);
			Framebuffer target = view.March[level];
			if (dynamic && target.ColorAttachments.Count < 3)
			{
				target.AddColorAttachment(RenderTextureFormat.RGBA16F, true);
				target.Validate();
			}

			var lowRect = new PixelRect(rect.X0 / scale, rect.Y0 / scale, Math.Min(lowWidth, (rect.X1 + scale - 1) / scale + 1), Math.Min(lowHeight, (rect.Y1 + scale - 1) / scale + 1));
			History history = volume.TemporalFilter ? HistoryOf(context, volume, lowWidth, lowHeight) : null;
			target.Bind();
			GL.Enable(EnableCap.ScissorTest);
			// A temporal filter reads the march around each pixel: march a texel more on every side.
			int margin = history != null ? 2 : 0;
			GL.Scissor(Math.Max(0, lowRect.X0 - margin), Math.Max(0, lowRect.Y0 - margin), lowRect.X1 - lowRect.X0 + margin * 2, lowRect.Y1 - lowRect.Y0 + margin * 2);
			GL.ClearColor(0f, 0f, 0f, 0f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			ShaderProgram program = MarchProgram(hq, data.LightsA != 0 && AnyLightLit(volume, data), data.LightsC != 0 && data.BakedLit, dynamic);
			program.Use();
			SetViewUniforms(program, context, volume, data, worldToLocal, invViewProjection, lowWidth, lowHeight);
			SetMarchUniforms(program, context, volume, data, localToWorld, scale, lowHeight, history, depth, lowDepth, depthParams);
			if (dynamic)
			{
				SetDynamicLights(program, volume, dynamicSlots);
			}

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
				// Cleared whole (both targets): what lies outside this frame's rectangle is no history next frame.
				GL.Disable(EnableCap.ScissorTest);
				GL.ClearColor(0f, 0f, 0f, 0f);
				GL.Clear(ClearBufferMask.ColorBufferBit);
				GL.Enable(EnableCap.ScissorTest);
				GL.Scissor(lowRect.X0, lowRect.Y0, lowRect.X1 - lowRect.X0, lowRect.Y1 - lowRect.Y0);
				temporal.Use();
				SetViewUniforms(temporal, context, volume, data, worldToLocal, invViewProjection, lowWidth, lowHeight);
				temporal.SetTexture("volCurrent", TextureTarget.Texture2D, target.ColorAttachments[0].Handle, ResultUnit);
				temporal.SetTexture("volCurrentDistance", TextureTarget.Texture2D, target.ColorAttachments[1].Handle, ResultDepthUnit);
				temporal.SetTexture("volHistory", TextureTarget.Texture2D, history.Targets[history.Current].ColorAttachments[0].Handle, HistoryUnit);
				temporal.SetTexture("volHistoryMeta", TextureTarget.Texture2D, history.Targets[history.Current].ColorAttachments[1].Handle, HistoryMetaUnit);
				temporal.SetMatrix4("volPreviousViewProjection", history.PreviousViewProjection);
				temporal.SetMatrix4("volPreviousLocalToWorld", history.PreviousLocalToWorld);
				temporal.SetVector3("volPreviousCameraPosition", history.PreviousCameraPosition);
				temporal.SetFloat("volMaxFrames", 1f / (1f - Math.Clamp(volume.HistoryWeight, 0f, 0.98f)));
				temporal.SetFloat("volDistanceTolerance", MathF.Log2(1f + HistoryDistanceTolerance));
				temporal.SetInt("volHistoryValid", history.Valid ? 1 : 0);
				context.Fullscreen.Draw();
				history.Settled = history.Valid && history.PreviousViewProjection == context.ViewProjection && history.PreviousLocalToWorld == localToWorld ? history.Settled + 1 : 0;
				history.Current = 1 - history.Current;
				history.Valid = true;
				history.PreviousViewProjection = context.ViewProjection;
				history.PreviousLocalToWorld = localToWorld;
				history.PreviousCameraPosition = context.CameraPosition;
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
			// This frame's dynamic light, not through the temporal filter: a moving light leaves no trail.
			composite.SetTexture("volDynamic", TextureTarget.Texture2D, dynamic ? target.ColorAttachments[2].Handle : 0, DynamicResultUnit);
			composite.SetInt("volDynamicActive", dynamic ? 1 : 0);
			composite.SetVector4("volDepthParams", depthParams);
			composite.SetInt("volUpsample", scale > 1 ? 1 : 0);
			context.Fullscreen.Draw();

			GL.Disable(EnableCap.ScissorTest);
			GLState.SetBlend(false);
			GLState.SetDepth(true, false, DepthFunction.Lequal);
			GLState.SetCull(true, TriangleFace.Back);
			UnbindCloudTextures();
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
				for (int i = 0; i <= MaxMarchScale; i++)
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
			depthDownsample ??= new ShaderProgram("Engine/Volumetrics/VolumetricDepth.shader");
			temporal ??= new ShaderProgram("Engine/Volumetrics/VolumetricTemporal.shader");
			composite ??= new ShaderProgram("Engine/Volumetrics/VolumetricComposite.shader");
			if (detailTexture == 0)
			{
				detailTexture = MakeDetail();
			}
		}

		/// <summary>
		///     The march's program for a preset and the lights lit right now (VolumetricMarch.shader's variants: the lights'
		///     fetches are compiled in only where a light shines, the dynamic lights' loop only while one reaches the cloud). A
		///     preset's eight variants are made together, so a light going on later (a first flash, a first bullet) finds its
		///     program started, not a compile in the frame it lights up.
		/// </summary>
		private static ShaderProgram MarchProgram(bool hq, bool lights, bool baked, bool dynamic)
		{
			int preset = hq ? 1 : 0;
			int index = preset | (lights ? 2 : 0) | (baked ? 4 : 0) | (dynamic ? 8 : 0);
			if (marchPrograms[index] == null)
			{
				var defines = new List<string>(4);
				for (int variant = 0; variant < 8; variant++)
				{
					int i = preset | (variant << 1);
					if (marchPrograms[i] != null)
					{
						continue;
					}

					defines.Clear();
					if (hq) defines.Add("VOL_HQ");
					if ((variant & 1) != 0) defines.Add("VOL_LIGHTS");
					if ((variant & 2) != 0) defines.Add("VOL_BAKED_LIGHTS");
					if ((variant & 4) != 0) defines.Add("VOL_DYNAMIC_LIGHTS");
					marchPrograms[i] = new ShaderProgram("Engine/Volumetrics/VolumetricMarch.shader", defines.ToArray());
				}
			}

			return marchPrograms[index];
		}

		/// <summary>
		///     The cloud's dynamic lights for the march (VOL_DYNAMIC_LIGHTS): their places, reaches, colours and cones in a
		///     uniform buffer on <see cref="DynamicLightBinding" />, written for each cloud drawn.
		/// </summary>
		private static void SetDynamicLights(ShaderProgram program, VolumetricObjectBase volume, List<VolumeLightSlot> slots)
		{
			int count = Math.Min(slots.Count, VolumetricObjectBase.MaxDynamicLights);
			const int stride = VolumetricObjectBase.MaxDynamicLights * 4;
			for (int i = 0; i < count; i++)
			{
				VolumeLightSlot slot = slots[i];
				// The slot's colour is divided by the transport's factor (the march here has none).
				Vector3 colour = slot.Color * slot.Spot.W;
				int at = i * 4;
				dynamicData[at + 0] = slot.PositionSize.X;
				dynamicData[at + 1] = slot.PositionSize.Y;
				dynamicData[at + 2] = slot.PositionSize.Z;
				dynamicData[at + 3] = slot.PositionSize.W;
				dynamicData[stride + at + 0] = colour.X;
				dynamicData[stride + at + 1] = colour.Y;
				dynamicData[stride + at + 2] = colour.Z;
				dynamicData[stride + at + 3] = slot.Params.Y;
				dynamicData[stride * 2 + at + 0] = slot.Spot.X;
				dynamicData[stride * 2 + at + 1] = slot.Spot.Y;
				dynamicData[stride * 2 + at + 2] = slot.Spot.Z;
				dynamicData[stride * 2 + at + 3] = slot.Params.Z;
				dynamicData[stride * 3 + at + 0] = slot.Params.W;
				dynamicData[stride * 3 + at + 1] = 0f;
				dynamicData[stride * 3 + at + 2] = 0f;
				dynamicData[stride * 3 + at + 3] = 0f;
			}

			if (dynamicBuffer == 0)
			{
				dynamicBuffer = GL.GenBuffer();
				GL.BindBuffer(BufferTarget.UniformBuffer, dynamicBuffer);
				GL.BufferData(BufferTarget.UniformBuffer, dynamicData.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
			}

			// A new store for each cloud (orphaned): the last cloud's draw may still read the old one.
			GL.BindBuffer(BufferTarget.UniformBuffer, dynamicBuffer);
			GL.BufferData(BufferTarget.UniformBuffer, dynamicData.Length * sizeof(float), dynamicData, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.UniformBuffer, 0);
			GL.BindBufferBase(BufferRangeTarget.UniformBuffer, DynamicLightBinding, dynamicBuffer);
			program.SetInt("volDynamicCount", count);
			program.SetFloat("volWorldScale", volume.WorldScale);
			program.SetFloat("volDynamicGlow", volume.LightGlow);
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

				// The result, and where its light was and how many frames it holds (VolumetricTemporal.shader).
				Ensure(ref history.Targets[i], width, height, RenderTextureFormat.RGBA16F, true, RenderTextureFormat.RG16F);
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
			program.SetTexture("volOccupancy", TextureTarget.Texture3D, data.Occupancy, OccupancyUnit);
			program.SetTexture("volDetail", TextureTarget.Texture3D, detailTexture, DetailUnit);
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

			Matrix4 worldToLocal = localToWorld.Inverted();
			SetCloudLightUniforms(program, volume, data, worldToLocal);
			program.SetFloat("volPhaseG", Math.Clamp(volume.PhaseG, -0.95f, 0.95f));

			// The detail: its tile in local units, drifting with the wind.
			float detailTile = MathF.Max(volume.DetailSize, 0.01f) * noiseSize;
			float time = (float)Time.timeD;
			program.SetFloat("volDetailStrength", volume.DetailStrength);
			program.SetFloat("volDetailShade", volume.DetailShade);
			program.SetFloat("volEdgeBand", MathF.Max(volume.EdgeBand, 0.05f));
			program.SetFloat("volDetailFrequency", 1f / detailTile);
			program.SetFloat("volDetailVoxel", detailTile / DetailResolution);
			program.SetVector3("volDetailOffset", -volume.DetailWind * time);

			program.SetMatrix4("volLocalToWorld", localToWorld);
			program.SetFloat("volExposure", volume.Exposure);
			program.SetFloat("volOpacity", Math.Clamp(volume.Opacity, 0f, 1f));
		}

		/// <summary>
		///     How a cloud is lit right now, for a program that reads its lit voxels (the march; the GI's media volume,
		///     VolumetricMedia.shader): the lit voxels, the sun (direction, colour, the scene's shadows of it), the sky and the
		///     flat ambient light, the albedo and emission, the GI the cloud receives, and its lights (the live ones' channels
		///     with their colour x intensity right now, the baked ones' texture). Texture units 0 and 3..8 of the program.
		///     <paramref name="forGI" />: the light as the GI takes it, each light's times its Indirect Strength, nothing of
		///     one with Affects GI off (the baked lights' texture holds their sum: they count as they are seen).
		/// </summary>
		internal static void SetCloudLightUniforms(ShaderProgram program, VolumetricObjectBase volume, VolumetricData data, Matrix4 worldToLocal, bool forGI = false)
		{
			program.SetTexture("volLit", TextureTarget.Texture3D, data.Lit, LitUnit);
			bool lights = data.LightsA != 0 && AnyLightLit(volume, data);
			program.SetTexture("volLightsA", TextureTarget.Texture3D, lights ? data.LightsA : 0, LightsAUnit);
			program.SetTexture("volLightsB", TextureTarget.Texture3D, lights ? data.LightsB : 0, LightsBUnit);
			bool baked = data.LightsC != 0 && data.BakedLit;
			program.SetTexture("volLightsC", TextureTarget.Texture3D, baked ? data.LightsC : 0, LightsCUnit);
			program.SetInt("volBakedActive", baked ? 1 : 0);
			program.SetFloat("volSigma", data.Sigma);
			program.SetFloat("volTauMax", VolumetricData.TauMax);

			// The sun, the sky and the flat ambient light (the scene's, or the volume's own). The scene's shadow maps are
			// those of the directional lights that cast them: a cloud receives its sun's, when its sun is one of them.
			DirectionalLight sun = volume.EffectiveSun;
			int sunIndex = sun != null ? Rendering.DirectionalLightBuffer.IndexOf(sun) : -1;
			bool shadows = volume.ReceiveShadows && sunIndex >= 0 && Lighting.ShadowCascades.LightCount[sunIndex] > 0;
			program.SetInt("volReceiveShadows", shadows ? 1 : 0);
			program.SetInt("volSunShadowLight", Math.Max(sunIndex, 0));
			program.SetFloat("volShadowStrength", Math.Clamp(volume.ShadowStrength, 0f, 1f));
			Lighting.SetShadowUniforms(program, shadows);
			Vector3 towards = sun != null ? -sun.transform.forward : Vector3.UnitY;
			Vector3 sunLocal = Vector3.TransformVector(towards, worldToLocal);
			sunLocal = sunLocal.LengthSquared > 1e-12f ? sunLocal.Normalized() : Vector3.UnitY;
			program.SetVector3("volSunDirection", sunLocal);
			float sunIndirect = !forGI ? 1f : sun != null && sun.AffectsGI ? MathF.Max(sun.IndirectStrength, 0f) : 0f;
			program.SetVector3("volSunColor", volume.SunRadiance * sunIndirect);
			Lighting.SetSkyUniforms(program);
			program.SetFloat("volSkyStrength", volume.SkyStrength);
			Vector3 ambient = volume.OwnAmbient
				? new Vector3(volume.AmbientColor.R, volume.AmbientColor.G, volume.AmbientColor.B)
				: new Vector3(Lighting.Ambient.Color.R, Lighting.Ambient.Color.G, Lighting.Ambient.Color.B) * Lighting.Ambient.Strength;
			program.SetVector3("volAmbientColor", ambient * volume.AmbientStrength);
			program.SetVector3("volAlbedo", new Vector3(volume.Albedo.R, volume.Albedo.G, volume.Albedo.B));
			program.SetVector3("volEmission", new Vector3(volume.EmissionColor.R, volume.EmissionColor.G, volume.EmissionColor.B) * volume.EmissionStrength);
			program.SetFloat("volMultipleScattering", volume.MultipleScattering);

			// The GI it receives, once its grid has been worked out whole.
			bool gi = volume.ReceiveGI && data.GIValid && SceneGIAvailable;
			program.SetInt("volGIActive", gi ? 1 : 0);
			program.SetTexture("volGIA", TextureTarget.Texture3D, gi ? data.GIA : 0, GIAUnit);
			program.SetTexture("volGIB", TextureTarget.Texture3D, gi ? data.GIB : 0, GIBUnit);
			program.SetFloat("volGIStrength", volume.GIStrength);

			// The live lights (its own and the scene's, as their transport was worked out): colour x intensity right now.
			// The scene's other directional lights among them are scattered as the sun is (the cloud's albedo and phase,
			// its response to the sun), from where they are now: their slots hold only the cloud on the way.
			program.SetInt("volLightsActive", lights ? 1 : 0);
			int directional = 0;
			if (lights)
			{
				for (int i = 0; i < lightColors.Length; i++)
				{
					VolumeLightSlot slot = data.LightSlots[i];
					float indirect = forGI ? slot.IndirectStrength : 1f;
					if (i >= data.LightCount)
					{
						lightColors[i] = Vector4.Zero;
					}
					else if (!slot.IsDirectional)
					{
						lightColors[i] = new Vector4(slot.Color * indirect, 0f);
					}
					else
					{
						lightColors[i] = Vector4.Zero;
						Vector3 color = slot.Color * (volume.SunStrength * indirect);
						if (directional < directionalDirs.Length && slot.Directional != null && color.X + color.Y + color.Z > 1e-6f)
						{
							Vector3 local = Vector3.TransformVector(slot.Directional.Towards, worldToLocal);
							directionalDirs[directional] = new Vector4(local.LengthSquared > 1e-12f ? local.Normalized() : Vector3.UnitY, 0f);
							directionalColors[directional] = new Vector4(color, 0f);
							directionalSlots[directional] = i;
							directional++;
						}
					}
				}

				program.SetVector4Array("volLightColors", lightColors);
				if (directional > 0)
				{
					program.SetVector4Array("volDirectionalDirs", directionalDirs);
					program.SetVector4Array("volDirectionalColors", directionalColors);
					GL.Uniform4(program.GetUniformLocation("volDirectionalSlots"), directionalSlots[0], directionalSlots[1], directionalSlots[2], directionalSlots[3]);
				}
			}

			program.SetInt("volDirectionalCount", directional);
		}

		/// <summary>
		///     The detail that eats into a cloud's edges, for a program that takes it at its mean (the GI's media volume):
		///     the shared detail texture and the cloud's detail settings.
		/// </summary>
		internal static void SetDetailUniforms(ShaderProgram program, VolumetricObjectBase volume)
		{
			if (detailTexture == 0)
			{
				detailTexture = MakeDetail();
				// MakeDetail used its own program.
				program.Use();
			}

			program.SetTexture("volDetail", TextureTarget.Texture3D, detailTexture, DetailUnit);
			program.SetFloat("volDetailStrength", volume.DetailStrength);
			program.SetFloat("volDetailShade", volume.DetailShade);
			program.SetFloat("volEdgeBand", MathF.Max(volume.EdgeBand, 0.05f));
		}

		// The units a cloud's textures were bound to (SetCloudLightUniforms, the march): nothing of them stays bound.
		internal static void UnbindCloudTextures()
		{
			for (int unit = 0; unit <= LightsCUnit; unit++)
			{
				GL.ActiveTexture(TextureUnit.Texture0 + unit);
				GL.BindTexture(TextureTarget.Texture3D, 0);
				GL.BindTexture(TextureTarget.Texture2D, 0);
			}

			GL.ActiveTexture(TextureUnit.Texture0);
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
