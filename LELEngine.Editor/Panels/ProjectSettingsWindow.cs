using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using LELEngine.Serialization;
using LELEngine.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Project Settings (Edit > Project Settings...), one tab each: the project-wide lighting quality (shadow map,
	///     GI method and its tunables), the names of the user layers, 2D physics (simulation plane, gravity, solver, queries, layer collision matrix),
	///     graphics (the shaders every build ships although nothing references them: a list that takes shader files
	///     dropped on it),
	///     the splash screen of builds (duration, background, logo, with a preview) and the build (scenes, start scene,
	///     output folder, configuration; builds in the background: the tab that runs a build, not one of settings).
	///     Lighting, graphics, physics and splash screen edits are
	///     undoable and saved to ProjectSettings/ProjectSettings.asset when they end; the lighting of a scene
	///     (environment, shadow reach, GI volume) stays in the Lighting panel.
	/// </summary>
	internal sealed class ProjectSettingsWindow : IEditorPanel
	{
		#region PublicFields

		public enum Tab
		{
			Lighting,
			Layers,
			Physics2D,
			SplashScreen,
			Audio,
			Input,
			Build,
			Graphics
		}

		public bool Open;

		string IEditorPanel.PanelName => "Project Settings";
		string IEditorPanel.ImGuiName => "Project Settings";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(820, 700);
		void IEditorPanel.Focus() => EditorApplication.Instance.ShowProjectSettings();

		/// <summary>The tab shown last (saved with the editor's windows: ImGui does not keep a tab bar's selection).</summary>
		public Tab CurrentTab { get; private set; } = Tab.Lighting;

		#endregion

		#region PrivateFields

		private static readonly string[] PlaneNames = { "XY (side view)", "XZ (top-down)" };

		private const string ShaderExtension = ".shader";

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private Tab? requestedTab;
		private bool focus;

		// ---- build
		private Task<bool> buildTask;
		private readonly List<string> lines = new List<string>();
		private string lastOutput;

		#endregion

		#region Constructors

		public ProjectSettingsWindow(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		/// <summary>Reopens the window as it was saved (the editor's windows): open on <paramref name="tab" />, without taking the focus.</summary>
		public void Restore(Tab tab)
		{
			Open = true;
			requestedTab = tab;
		}

		/// <summary>Opens the window in front, on <paramref name="tab" /> (null: the tab it was on).</summary>
		public void Show(Tab? tab)
		{
			Open = true;
			focus = true;
			requestedTab = tab;
		}

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(620, 600), ImGuiCond.FirstUseEver);
			if (focus)
			{
				ImGui.SetNextWindowFocus();
				focus = false;
			}

			if (ImGui.Begin("Project Settings", ref Open))
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					context.Release();
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			// Empty while the project loads until its assets are read (settings name assets: the splash logo, the input
			// configuration, the audio project).
			if (!editor.AssetsReady)
			{
				return;
			}

			ProjectSettings settings = ProjectSettings.Current;
			context.Scene = editor.Scene;
			context.UndoTarget = settings;
			context.LabelWidth = Math.Max(170f, ImGui.GetContentRegionAvail().X * 0.45f);

			if (!ImGui.BeginTabBar("##tabs"))
			{
				return;
			}

			bool changed = false;
			if (BeginTab(Tab.Lighting, "Lighting"))
			{
				changed |= DrawLighting(settings);
				EndTab();
			}

			if (BeginTab(Tab.Graphics, "Graphics"))
			{
				changed |= DrawGraphics(settings.Graphics ??= new GraphicsSettings());
				// How LOD groups pick their levels (it was under Lighting, which it has nothing to do with).
				ImGui.Spacing();
				ImGui.SeparatorText("Level of Detail");
				ImGui.PushID("lod");
				changed |= FieldDrawer.DrawFields(settings.LOD ??= new LODSettings(), context);
				ImGui.PopID();
				EndTab();
			}

			if (BeginTab(Tab.Layers, "Layers"))
			{
				changed |= DrawLayers(settings.Layers);
				EndTab();
			}

			if (BeginTab(Tab.Physics2D, "Physics 2D"))
			{
				changed |= DrawPhysics(settings.Physics2D);
				EndTab();
			}

			if (BeginTab(Tab.SplashScreen, "Splash Screen"))
			{
				changed |= DrawSplashScreen(settings.SplashScreen ??= new SplashScreenSettings());
				EndTab();
			}

			if (BeginTab(Tab.Audio, "Audio"))
			{
				changed |= Audio.AudioInspectors.DrawSettings(settings.Audio ??= new LELEngine.Audio.AudioSettings(), context);
				EndTab();
			}

			if (BeginTab(Tab.Input, "Input"))
			{
				changed |= DrawInput(settings.Input ??= new LELEngine.InputSystem.InputProjectSettings());
				EndTab();
			}

			if (BeginTab(Tab.Build, "Build"))
			{
				DrawBuild(editor.Project);
				EndTab();
			}

			ImGui.EndTabBar();
			requestedTab = null;
			if (changed)
			{
				// Saved when the edit ends (EditTracker); the views show the change right away.
				editor.NotifySceneChanged();
			}
		}

		private bool BeginTab(Tab tab, string label)
		{
			ImGuiTabItemFlags flags = requestedTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
			if (!EditorGui.BeginTabItem(label, flags))
			{
				return false;
			}

			CurrentTab = tab;

			ImGui.BeginChild("##content", NVector2.Zero);
			return true;
		}

		private static void EndTab()
		{
			ImGui.EndChild();
			ImGui.EndTabItem();
		}

		// ---------------------------------------------------------------- input

		/// <summary>The input configuration the game's players use (its actions, maps and players are edited in the Input Manager).</summary>
		private bool DrawInput(LELEngine.InputSystem.InputProjectSettings input)
		{
			FieldDrawer.Label("Configuration", null, context);
			object value = string.IsNullOrEmpty(input.Configuration) ? null : ScriptableObjectAssets.Load(input.Configuration);
			bool changed = AssetField.Draw(typeof(LELEngine.InputSystem.InputConfiguration), ref value);
			context.Track(changed);
			if (changed)
			{
				input.Configuration = (value as LELEngine.InputSystem.InputConfiguration)?.AssetPath ?? "";
			}

			ImGui.Spacing();
			if (EditorGui.Button(string.IsNullOrEmpty(input.Configuration) ? "Open Input Manager (create one)" : "Open in Input Manager"))
			{
				editor.OpenInputManager(string.IsNullOrEmpty(input.Configuration) ? null : input.Configuration);
			}

			ImGui.Spacing();
			Hint("Scripts read actions of players: InputManager.GetPlayer(0).GetButtonDown(\"Fire\").");
			Hint("Without a configuration the game reads keys directly (Input.GetKey).");
			return changed;
		}

		// ---------------------------------------------------------------- lighting

		/// <summary>
		///     The GI method combo (project-wide; the context's undo target must be the project settings). Also drawn at
		///     the top of the Lighting panel's GI section. Locked in play mode: the method the game started with runs
		///     until it stops (<see cref="Lighting.GIMethod" />).
		/// </summary>
		internal static bool DrawGIMethod(GlobalIlluminationSettings gi, InspectorContext context)
		{
			FieldDrawer.Label("GI Method", null, context);
			GIMode selected = gi.Mode;
			bool playing = context.Editor != null && context.Editor.IsPlaying;
			ImGui.BeginDisabled(playing);
			if (EditorGui.BeginCombo("##gimethod", SerializationUtility.NicifyName(selected.ToString())))
			{
				foreach (GIMode mode in Enum.GetValues<GIMode>())
				{
					if (EditorGui.Selectable(SerializationUtility.NicifyName(mode.ToString()), mode == selected))
					{
						selected = mode;
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.EndDisabled();
			if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				EditorGui.SetTooltip("The GI method cannot change in play mode.");
			}

			bool changed = selected != gi.Mode;
			context.Track(changed);
			if (changed)
			{
				gi.Mode = selected;
				gi.InvalidateStatic();
			}

			return changed;
		}

		/// <summary>Whether a GI setting is read by <paramref name="mode" /> (settings without [GIMethod] are read by both).</summary>
		internal static bool IsUsedBy(SerializedField field, GIMode mode)
		{
			GIMode? only = GIMethodAttribute.Of(field.Field);
			return only == null || only == mode;
		}

		// The lighting settings are laid out by what they belong to, and a setting shows only while it changes something:
		// a feature's settings go with it when it is off, a method's with the method, the distance field's while nothing
		// traces it (Hardware Ray Tracing), a choice's details with the choice that reads them. What reads each setting:
		// GlobalIlluminationSettings, RayTracingSettings, ShadowSettings (keep this in step when that changes).
		private bool DrawLighting(ProjectSettings settings)
		{
			GlobalIlluminationSettings gi = settings.GI;
			bool changed = DrawGIMethod(gi, context);
			ImGui.Spacing();
			Hint("How shadows and global illumination are computed in every scene.");
			Hint("A scene's environment, shadow reach and GI volume are in the Lighting panel.");
			ImGui.Spacing();

			changed |= DrawShadows(settings);

			// The selected method's section (one id for both, so it keeps its open state when the method changes).
			ImGui.PushID("gi");
			bool giChanged = false;
			if (EditorGui.CollapsingHeader(SerializationUtility.NicifyName(gi.Mode.ToString()) + "###method", ImGuiTreeNodeFlags.DefaultOpen))
			{
				giChanged = gi.Mode == GIMode.VoxelConeTracing ? DrawVoxelConeTracing(settings) : DrawLumen(settings);
			}

			ImGui.PopID();
			if (giChanged)
			{
				gi.InvalidateStatic();
			}

			return changed | giChanged;
		}

		/// <summary>The project's graphics API is Vulkan, the only one that traces rays (on GPUs with ray queries).</summary>
		private static bool ProjectCanTraceRays(ProjectSettings settings)
		{
			return settings.Graphics != null && settings.Graphics.GraphicsApi == Graphics.GraphicsApi.Vulkan;
		}

		private bool DrawShadows(ProjectSettings settings)
		{
			if (!EditorGui.CollapsingHeader("Shadows", ImGuiTreeNodeFlags.DefaultOpen))
			{
				return false;
			}

			ShadowSettings shadows = settings.Shadows;
			RayTracingSettings rays = settings.RayTracing;
			ImGui.PushID("shadows");
			bool changed = Field(shadows, nameof(ShadowSettings.Enabled));
			if (shadows.Enabled)
			{
				if (Group("Shadow Maps"))
				{
					changed |= Fields(shadows, nameof(ShadowSettings.MapSize), nameof(ShadowSettings.DepthBiasTexels), nameof(ShadowSettings.NormalBiasTexels),
						nameof(ShadowSettings.Cascades));
					// Right below the cascade count: where each cascade begins (the split fields are hidden).
					changed |= CascadeSplitsEditor.Draw(shadows, context);
					changed |= Field(shadows, nameof(ShadowSettings.ShadowedLights));
					if (shadows.ShadowedLights > 1)
					{
						changed |= Fields(shadows, nameof(ShadowSettings.AdditionalLightMapSize), nameof(ShadowSettings.AdditionalLightCascades));
					}

					ImGui.TreePop();
				}

				if (ProjectCanTraceRays(settings) && Group("Ray Traced"))
				{
					if (!GraphicsCapabilities.RayQueries)
					{
						Hint("This GPU has no ray queries: here the shadows come from the maps only.");
					}

					changed |= Field(rays, nameof(RayTracingSettings.LocalLightShadows));
					if (rays.LocalLightShadows)
					{
						changed |= Field(rays, nameof(RayTracingSettings.ShadowedLocalLights));
					}

					changed |= Field(rays, nameof(RayTracingSettings.DirectionalLightShadows));
					if (!rays.DirectionalLightShadows)
					{
						changed |= Field(rays, nameof(RayTracingSettings.DirectionalLightShadowsOnDiscreteGpus));
					}

					bool directional = rays.DirectionalLightShadows || rays.DirectionalLightShadowsOnDiscreteGpus;
					if (directional)
					{
						changed |= Field(rays, nameof(RayTracingSettings.SkipUnneededShadowMaps));
					}

					if (rays.LocalLightShadows || directional)
					{
						changed |= Fields(rays, nameof(RayTracingSettings.TransparentShadows), nameof(RayTracingSettings.SoftShadows));
						if (rays.SoftShadows)
						{
							changed |= Field(rays, nameof(RayTracingSettings.SoftShadowRays));
						}
					}

					ImGui.TreePop();
				}
			}

			ImGui.PopID();
			return changed;
		}

		private bool DrawVoxelConeTracing(ProjectSettings settings)
		{
			GlobalIlluminationSettings gi = settings.GI;
			RayTracingSettings rays = settings.RayTracing;
			bool changed = false;
			if (Group("Volume"))
			{
				changed |= SizeCombo(gi, nameof(GlobalIlluminationSettings.Resolution), ref gi.Resolution, 32, 64, 128, 256);
				changed |= Fields(gi, nameof(GlobalIlluminationSettings.LightingUpdateInterval), nameof(GlobalIlluminationSettings.DynamicUpdateInterval));
				changed |= SizeCombo(gi, nameof(GlobalIlluminationSettings.GridShadowMapSize), ref gi.GridShadowMapSize, 256, 512, 1024, 2048, 4096);
				changed |= Field(gi, nameof(GlobalIlluminationSettings.SoftVoxelShadows));
				ImGui.TreePop();
			}

			if (Group("Voxelization"))
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.VoxelCoverage));
				if (gi.VoxelCoverage)
				{
					changed |= Field(gi, nameof(GlobalIlluminationSettings.ConservativeVoxelization));
					if (gi.ConservativeVoxelization && Graphics.GraphicsDevice.Current != null && !Graphics.Gfx.Capabilities.ConservativeRasterization)
					{
						Hint("This graphics device has no conservative rasterization: it takes eight samples here.");
					}

					changed |= Fields(gi, nameof(GlobalIlluminationSettings.CoverageOccupancyScale), nameof(GlobalIlluminationSettings.CoverageSplat));
				}

				ImGui.TreePop();
			}

			if (Group("Mips"))
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.VoxelMips));
				if (gi.VoxelMips == VoxelMipMode.Anisotropic)
				{
					changed |= Field(gi, nameof(GlobalIlluminationSettings.AnisotropicMipLevels));
				}

				ImGui.TreePop();
			}

			if (Group("Bounces"))
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.BounceIterations));
				if (gi.BounceIterations > 0)
				{
					changed |= Field(gi, nameof(GlobalIlluminationSettings.BounceCells));
					if (!gi.BounceCells)
					{
						changed |= Field(gi, nameof(GlobalIlluminationSettings.BounceConeLayout));
					}
				}

				ImGui.TreePop();
			}

			if (Group("Cones"))
			{
				changed |= Fields(gi, nameof(GlobalIlluminationSettings.ConeMaxDistance), nameof(GlobalIlluminationSettings.ConeJitter));
				// Two choices, said as what they are: what the cones see (the voxels only, or the exact surfaces near them or
				// all along them), then where the exact surfaces come from (ray tracing or the distance field). The fields
				// behind them keep their names (TraceMode: VoxelCones / SdfDetail / SdfCones; HardwareRayTracing).
				changed |= ConeVisibility(gi);
				if (gi.TraceMode == GITraceMode.SdfDetail)
				{
					changed |= Field(gi, nameof(GlobalIlluminationSettings.SdfDetailDistance));
				}

				if (gi.TraceMode != GITraceMode.VoxelCones)
				{
					bool traced = false;
					if (ProjectCanTraceRays(settings))
					{
						changed |= ExactSurfacesSource(gi);
						traced = gi.HardwareRayTracing;
					}
					else
					{
						Hint("Geometry Source: Global SDF (OpenGL has no ray tracing).");
					}

					if (traced)
					{
						if (gi.TraceMode == GITraceMode.SdfCones)
						{
							changed |= Field(rays, nameof(RayTracingSettings.ConeRays));
						}

						changed |= Field(rays, nameof(RayTracingSettings.MaterialConeRays));
						Hint("GPUs without ray queries fall back to the Global SDF (its settings show with it selected).");
					}
					else if (Group("Global SDF"))
					{
						changed |= DistanceField(gi, gi.TraceMode == GITraceMode.SdfDetail);
						ImGui.TreePop();
					}
				}

				ImGui.TreePop();
			}

			if (Group("Screen-Space Resolve"))
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.ScreenSpaceResolve));
				if (gi.ScreenSpaceResolve)
				{
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.ResolveScale), nameof(GlobalIlluminationSettings.ResolveFilterPasses));
					if (gi.ConeJitter)
					{
						changed |= Field(gi, nameof(GlobalIlluminationSettings.ConeJitterCellSize));
					}
				}

				ImGui.TreePop();
			}

			changed |= VolumetricObjects(gi, gi.ScreenSpaceResolve);
			return changed;
		}

		private bool DrawLumen(ProjectSettings settings)
		{
			GlobalIlluminationSettings gi = settings.GI;
			RayTracingSettings rays = settings.RayTracing;
			bool changed = false;
			if (ProjectCanTraceRays(settings) && Group("Ray Tracing"))
			{
				changed |= HardwareRayTracing(gi,
					"Lumen's rays (screen probes, radiance cache probes, the surface cache's radiosity, the local lights' GI shadows) trace the scene's " +
					"triangles with the GPU's ray tracing (Vulkan on a GPU with ray queries); no distance field is built then. Off, or on a GPU without " +
					"ray queries: they trace the global distance field. What a ray hits is lit from the surface cache either way. Taken when the GI first runs.");
				if (gi.HardwareRayTracing)
				{
					changed |= Field(rays, nameof(RayTracingSettings.Reflections));
					if (rays.Reflections)
					{
						changed |= Field(rays, nameof(RayTracingSettings.ReflectionMaxRoughness));
					}

					if (settings.Shadows.Enabled)
					{
						changed |= Field(rays, nameof(RayTracingSettings.SurfaceCacheShadows));
					}

					Hint("GPUs without ray queries trace a distance field instead (its settings show with this off).");
				}

				ImGui.TreePop();
			}

			bool traced = ProjectCanTraceRays(settings) && gi.HardwareRayTracing;
			if (!traced && Group("Distance Field"))
			{
				changed |= DistanceField(gi, true);
				changed |= Field(gi, nameof(GlobalIlluminationSettings.DynamicUpdateInterval));
				ImGui.TreePop();
			}

			if (Group("Surface Cache"))
			{
				changed |= Fields(gi, nameof(GlobalIlluminationSettings.SurfaceCacheAtlasSize), nameof(GlobalIlluminationSettings.SurfaceCacheTexelsPerMeter),
					nameof(GlobalIlluminationSettings.SurfaceCacheMaxCardSize), nameof(GlobalIlluminationSettings.GridShadowMapSize),
					nameof(GlobalIlluminationSettings.RadiosityMaxHistorySamples));
				changed |= gi.RadiosityMaxHistorySamples > 0f
					? Field(gi, nameof(GlobalIlluminationSettings.RadiosityMovingHistorySamples))
					: Field(gi, nameof(GlobalIlluminationSettings.RadiosityBlend));
				changed |= Field(gi, nameof(GlobalIlluminationSettings.RadiosityIdleDivisor));
				ImGui.TreePop();
			}

			if (Group("Screen Probes"))
			{
				changed |= Fields(gi, nameof(GlobalIlluminationSettings.ScreenProbeResolveScale), nameof(GlobalIlluminationSettings.ProbeSpacing),
					nameof(GlobalIlluminationSettings.ProbePlaneTolerance), nameof(GlobalIlluminationSettings.ProbeAdaptivePlacement));
				if (gi.ProbeAdaptivePlacement)
				{
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.ProbeAdaptiveFraction), nameof(GlobalIlluminationSettings.ProbeAdaptiveMinCoverage),
						nameof(GlobalIlluminationSettings.ProbeAdaptiveFlaggedRefine));
				}

				changed |= Field(gi, nameof(GlobalIlluminationSettings.ProbeJitter));
				if (gi.ProbeJitter)
				{
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.ProbeAnchorJitter), nameof(GlobalIlluminationSettings.ProbeDirectionJitter));
				}

				changed |= Field(gi, nameof(GlobalIlluminationSettings.ProbeImportanceSampling));
				if (gi.ProbeImportanceSampling)
				{
					changed |= Field(gi, nameof(GlobalIlluminationSettings.ProbeImportanceMergeFraction));
				}

				changed |= Field(gi, nameof(GlobalIlluminationSettings.ProbeSpatialFilter));
				if (gi.ProbeSpatialFilter)
				{
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.ProbeSpatialFilterPasses), nameof(GlobalIlluminationSettings.ProbeFilterMaxAngle));
				}

				changed |= Field(gi, nameof(GlobalIlluminationSettings.ScreenTraceDistance));
				if (gi.ScreenTraceDistance > 0f)
				{
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.ScreenTraceSteps), nameof(GlobalIlluminationSettings.ScreenTraceThickness));
				}

				ImGui.TreePop();
			}

			if (Group("Probe History"))
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.ProbeMaxHistorySamples));
				changed |= gi.ProbeMaxHistorySamples > 0f
					? Fields(gi, nameof(GlobalIlluminationSettings.ProbeFastHistorySamples), nameof(GlobalIlluminationSettings.ProbeChangeSigma),
						nameof(GlobalIlluminationSettings.ProbeChangeFloor), nameof(GlobalIlluminationSettings.ProbeJumpThreshold))
					: Field(gi, nameof(GlobalIlluminationSettings.ProbeHistoryWeight));
				changed |= Fields(gi, nameof(GlobalIlluminationSettings.ProbeHistoryDistance), nameof(GlobalIlluminationSettings.ProbeMovingHistorySamples),
					nameof(GlobalIlluminationSettings.ProbeMovingFrames), nameof(GlobalIlluminationSettings.ProbeTemporalBlend));
				if (gi.ProbeTemporalBlend > 0f)
				{
					changed |= Field(gi, nameof(GlobalIlluminationSettings.ProbeTemporalMovingFrames));
				}

				ImGui.TreePop();
			}

			if (Group("Radiance Cache"))
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.RadianceCacheEnabled));
				if (gi.RadianceCacheEnabled)
				{
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.RadianceCacheProbesPerAxis), nameof(GlobalIlluminationSettings.RadianceCacheProbeResolution),
						nameof(GlobalIlluminationSettings.RadianceCacheTraceResolution), nameof(GlobalIlluminationSettings.RadianceCacheProbesPerFrame),
						nameof(GlobalIlluminationSettings.RadianceCacheMaxHistorySamples));
					changed |= gi.RadianceCacheMaxHistorySamples > 0f
						? Field(gi, nameof(GlobalIlluminationSettings.RadianceCacheMovingHistorySamples))
						: Field(gi, nameof(GlobalIlluminationSettings.RadianceCacheHistoryFrames));
					changed |= Fields(gi, nameof(GlobalIlluminationSettings.RadianceCacheChangeThreshold), nameof(GlobalIlluminationSettings.RadianceCacheChangeProbesPerFrame),
						nameof(GlobalIlluminationSettings.RadianceCacheChangeRadius), nameof(GlobalIlluminationSettings.RadianceCacheIdleAfterFrames),
						nameof(GlobalIlluminationSettings.RadianceCacheIdleDivisor), nameof(GlobalIlluminationSettings.RadianceCacheIdleAfterSweeps),
						nameof(GlobalIlluminationSettings.RadianceCacheNearDistance), nameof(GlobalIlluminationSettings.RadianceCacheForRadiosity),
						nameof(GlobalIlluminationSettings.RadianceCacheFarShortcut));
				}

				ImGui.TreePop();
			}

			changed |= VolumetricObjects(gi, false);
			return changed;
		}

		/// <summary>
		///     The global distance field's settings (while it is built: an SDF trace without the scene's triangles);
		///     <paramref name="sphereTraced" />: something sphere traces it (Lumen, SDF Detail), which Max Steps bounds.
		/// </summary>
		private bool DistanceField(GlobalIlluminationSettings gi, bool sphereTraced)
		{
			bool changed = SizeCombo(gi, nameof(GlobalIlluminationSettings.SdfResolution), ref gi.SdfResolution, 64, 128, 256);
			changed |= Field(gi, nameof(GlobalIlluminationSettings.SdfBandVoxels));
			if (sphereTraced)
			{
				changed |= Field(gi, nameof(GlobalIlluminationSettings.SdfMaxSteps));
			}

			return changed;
		}

		/// <summary>
		///     The volumetric objects in the GI. <paramref name="accurate" />: the method has the Accurate way (voxel cone
		///     tracing's screen-space resolve); elsewhere Fast and Accurate are the same, so it is on or off.
		/// </summary>
		private bool VolumetricObjects(GlobalIlluminationSettings gi, bool accurate)
		{
			if (!Group("Volumetric Objects"))
			{
				return false;
			}

			bool changed;
			if (accurate)
			{
				changed = Field(gi, nameof(GlobalIlluminationSettings.VolumesInGI));
			}
			else
			{
				bool on = gi.VolumesInGI != VolumeGIMode.Off;
				FieldDrawer.Label("Volumes In GI", null, context);
				changed = EditorGui.Checkbox("##volumesingi", ref on);
				Tooltip("The volumetric objects with Contribute GI light the scene through the GI and hide the sky and the light behind them\n" +
					"(a coarse volume of them is made every frame). Off: the GI does not see clouds at all.");
				context.Track(changed);
				if (changed)
				{
					gi.VolumesInGI = on ? VolumeGIMode.Fast : VolumeGIMode.Off;
				}
			}

			if (gi.VolumesInGI != VolumeGIMode.Off)
			{
				changed |= Fields(gi, nameof(GlobalIlluminationSettings.VolumeMediaSides), nameof(GlobalIlluminationSettings.VolumeMediaResolution),
					nameof(GlobalIlluminationSettings.VolumeMediaReach));
			}

			ImGui.TreePop();
			return changed;
		}

		private static readonly (GITraceMode Mode, string Label)[] coneVisibilities =
		{
			(GITraceMode.VoxelCones, "Voxel"),
			(GITraceMode.SdfDetail, "Hybrid"),
			(GITraceMode.SdfCones, "Exact")
		};

		/// <summary>VCT's trace mode under the name of what it gives (the field stays <see cref="GlobalIlluminationSettings.TraceMode" />).</summary>
		private bool ConeVisibility(GlobalIlluminationSettings gi)
		{
			FieldDrawer.Label("Cone Occlusion", null, context);
			GITraceMode selected = gi.TraceMode;
			string shown = Array.Find(coneVisibilities, entry => entry.Mode == selected).Label ?? selected.ToString();
			if (EditorGui.BeginCombo("##conevisibility", shown))
			{
				foreach ((GITraceMode mode, string label) in coneVisibilities)
				{
					if (EditorGui.Selectable(label, mode == selected))
					{
						selected = mode;
					}
				}

				EditorGui.EndCombo();
			}

			Tooltip("What the cones see of the surfaces they run into.\n" +
				"Voxel: the voxel mips' opacity only (cheapest; light leaks through walls about a voxel thick, stepped corners).\n" +
				"Hybrid: exact geometry over the first stretch of every cone (Detail Distance), voxel mips after it.\n" +
				"Exact: the whole cone's visibility from the exact geometry (no leaks through walls, smooth corners).\n" +
				"Geometry Source sets where the exact geometry comes from (hardware ray tracing or the global SDF).");
			bool changed = selected != gi.TraceMode;
			context.Track(changed);
			if (changed)
			{
				gi.TraceMode = selected;
			}

			return changed;
		}

		/// <summary>Where VCT's exact surfaces come from: the triangles by ray tracing, or the distance field (<see cref="GlobalIlluminationSettings.HardwareRayTracing" />).</summary>
		private bool ExactSurfacesSource(GlobalIlluminationSettings gi)
		{
			FieldDrawer.Label("Geometry Source", null, context);
			int selected = gi.HardwareRayTracing ? 0 : 1;
			string[] labels = { "Hardware Ray Tracing", "Global SDF" };
			if (EditorGui.BeginCombo("##exactsurfaces", labels[selected]))
			{
				for (int i = 0; i < labels.Length; i++)
				{
					if (EditorGui.Selectable(labels[i], i == selected))
					{
						selected = i;
					}
				}

				EditorGui.EndCombo();
			}

			Tooltip("Hardware Ray Tracing: the cones trace the scene's triangles with ray queries (Vulkan on a GPU that has them);\n" +
				"no distance field is built. Global SDF: they read the scene's global signed distance field, built and kept up to date.\n" +
				"A GPU without ray queries uses the distance field either way. Taken when the GI first runs: not while the game runs.");
			bool changed = (selected == 0) != gi.HardwareRayTracing;
			context.Track(changed);
			if (changed)
			{
				gi.HardwareRayTracing = selected == 0;
			}

			if (gi.HardwareRayTracing && !GraphicsCapabilities.RayQueries)
			{
				Hint("This GPU has no ray queries: it falls back to the Global SDF.");
			}

			return changed;
		}

		/// <summary>The GI's Hardware Ray Tracing, with the description of what it changes for the method drawn.</summary>
		private bool HardwareRayTracing(GlobalIlluminationSettings gi, string tooltip)
		{
			bool on = gi.HardwareRayTracing;
			FieldDrawer.Label("Hardware Ray Tracing", null, context);
			bool edited = EditorGui.Checkbox("##hardwareraytracing", ref on);
			Tooltip(tooltip);
			context.Track(edited);
			if (edited)
			{
				gi.HardwareRayTracing = on;
			}

			if (on && !GraphicsCapabilities.RayQueries)
			{
				Hint("This GPU has no ray queries: here the rays trace the distance field.");
			}

			return edited;
		}

		/// <summary>
		///     A size of a power of two as a drop-down of <paramref name="options" /> (a value set elsewhere shows as it is),
		///     with the field's label and tooltip.
		/// </summary>
		private bool SizeCombo(object target, string fieldName, ref int value, params int[] options)
		{
			System.Reflection.FieldInfo info = target.GetType().GetField(fieldName);
			FieldDrawer.Label(SerializationUtility.NicifyName(fieldName), info, context);
			int selected = value;
			if (EditorGui.BeginCombo("##" + fieldName, selected.ToString()))
			{
				foreach (int option in options)
				{
					if (EditorGui.Selectable(option.ToString(), option == selected))
					{
						selected = option;
					}
				}

				EditorGui.EndCombo();
			}

			bool changed = selected != value;
			context.Track(changed);
			value = selected;
			return changed;
		}

		/// <summary>A foldout of a section's settings, open at first; the caller pops it (ImGui.TreePop) when it returns true.</summary>
		private static bool Group(string label)
		{
			return EditorGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
		}

		/// <summary>One field of <paramref name="target" /> through the inspector's drawer (label, tooltip, range, undo).</summary>
		private bool Field(object target, string name)
		{
			return FieldDrawer.DrawFields(target, context, filter: field => field.Name == name);
		}

		/// <summary>The named fields of <paramref name="target" />, in the order given.</summary>
		private bool Fields(object target, params string[] names)
		{
			bool changed = false;
			foreach (string name in names)
			{
				changed |= Field(target, name);
			}

			return changed;
		}

		// ---------------------------------------------------------------- layers

		/// <summary>The layer names: built in for 0..7, the project's for 8..31 (objects keep their layer when it is renamed).</summary>
		private bool DrawLayers(LayerSettings layers)
		{
			Hint("Every object is on one layer (Inspector header). Cameras draw the layers of their culling mask,");
			Hint("2D physics collides layers by the matrix in Physics 2D.");
			ImGui.Spacing();

			bool changed = false;
			ImGui.PushID("layers");
			for (int layer = 0; layer < LayerMask.LayerCount; layer++)
			{
				ImGui.PushID(layer);
				bool builtIn = layer < LayerMask.FirstUserLayer;
				FieldDrawer.Label((builtIn ? "Built-in Layer " : "User Layer ") + layer, null, context);
				if (builtIn)
				{
					string fixedName = LayerMask.LayerToName(layer);
					ImGui.BeginDisabled();
					EditorGui.InputText("##name", ref fixedName, 64, ImGuiInputTextFlags.ReadOnly);
					ImGui.EndDisabled();
				}
				else
				{
					string name = layers.GetName(layer);
					bool edited = EditorGui.InputText("##name", ref name, 64);
					context.Track(edited);
					if (edited)
					{
						layers.SetName(layer, name);
						changed = true;
					}
				}

				ImGui.PopID();
			}

			ImGui.PopID();
			return changed;
		}

		// ---------------------------------------------------------------- physics

		private bool DrawPhysics(Physics2DSettings physics)
		{
			bool changed = DrawSimulationPlane(physics);
			ImGui.PushID("physics2d");
			changed |= FieldDrawer.DrawFields(physics, context);
			changed |= DrawLayerCollisionMatrix(physics);
			ImGui.PopID();
			return changed;
		}

		/// <summary>The plane is read when a scene's physics world is created, so it does not change during play.</summary>
		private bool DrawSimulationPlane(Physics2DSettings physics)
		{
			int current = physics.SimulationPlane == SimulationPlane2D.XZ ? 1 : 0;
			int selected = current;
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Simulation Plane");
			ImGui.SameLine(context.LabelWidth);
			ImGui.SetNextItemWidth(-1);
			ImGui.BeginDisabled(editor.IsPlaying);
			bool changed = ImGui.Combo("##plane", ref selected, PlaneNames, PlaneNames.Length) && selected != current;
			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				EditorGui.SetTooltip(editor.IsPlaying
					? "Applies when play starts (the running world keeps its plane)."
					: "XY: 2D y is world Y, bodies turn about Z.\nXZ: 2D y is world Z, bodies turn about -Y (the XY plane turned 90 degrees about X).");
			}

			context.Track(changed);
			if (changed)
			{
				physics.SimulationPlane = selected == 1 ? SimulationPlane2D.XZ : SimulationPlane2D.XY;
			}

			return changed;
		}

		/// <summary>Which layers collide with which (the used layers: named ones and those of the scene's objects).</summary>
		private bool DrawLayerCollisionMatrix(Physics2DSettings physics)
		{
			if (!EditorGui.TreeNodeEx("Layer Collision Matrix", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen))
			{
				return false;
			}

			var layers = new List<int>();
			for (int i = 0; i < 32; i++)
			{
				if (LayerMask.LayerToName(i).Length > 0)
				{
					layers.Add(i);
				}
			}

			Scene scene = editor.Scene;
			if (scene != null)
			{
				foreach (GameObject go in scene.GameObjects)
				{
					if (!layers.Contains(go.layer))
					{
						layers.Add(go.layer);
					}
				}
			}

			layers.Sort();
			bool changed = false;
			if (ImGui.BeginTable("##matrix", layers.Count + 1, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInner))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				foreach (int column in layers)
				{
					ImGui.TableNextColumn();
					ImGui.TextDisabled(LayerMask.DisplayName(column));
				}

				foreach (int row in layers)
				{
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.TextUnformatted(LayerMask.DisplayName(row));
					foreach (int column in layers)
					{
						ImGui.TableNextColumn();
						if (column < row)
						{
							continue;
						}

						bool collide = !physics.GetIgnoreLayerCollision(row, column);
						ImGui.PushID(row * 32 + column);
						if (EditorGui.Checkbox("##c", ref collide))
						{
							editor.Edits.Begin(context.UndoTarget);
							physics.IgnoreLayerCollision(row, column, !collide);
							editor.Edits.RequestCommit();
							changed = true;
						}

						ImGui.PopID();
					}
				}

				ImGui.EndTable();
			}

			ImGui.TreePop();
			return changed;
		}

		// ---------------------------------------------------------------- splash screen

		/// <summary>On / off, the minimum duration, the background color, the logo (a texture asset; its GUID is stored) and its size, with a preview.</summary>
		private bool DrawSplashScreen(SplashScreenSettings splash)
		{
			Hint("The first image of a build: up a moment after its window opens, while the start scene");
			Hint("loads in the background. The start scene replaces it once loaded and the duration is over.");
			ImGui.Spacing();

			ImGui.PushID("splash");
			bool changed = false;

			bool enabled = splash.Enabled;
			FieldDrawer.Label("Show Splash Screen", null, context);
			bool edited = EditorGui.Checkbox("##enabled", ref enabled);
			Tooltip("Off: the window stays black while the start scene loads, and shows it as soon as it is ready.");
			context.Track(edited);
			if (edited)
			{
				splash.Enabled = enabled;
				changed = true;
			}

			ImGui.BeginDisabled(!splash.Enabled);

			float duration = splash.Duration;
			FieldDrawer.Label("Duration", null, context);
			edited = EditorGui.SliderFloat("##duration", ref duration, 0f, 10f, "%.1f s");
			Tooltip("Seconds the splash screen stays at least; longer when the start scene takes longer to load.\n0: only while it loads. Ctrl+click to type a value.");
			context.Track(edited);
			if (edited)
			{
				splash.Duration = Math.Max(0f, duration);
				changed = true;
			}

			Color4 color = splash.BackgroundColor;
			var rgb = new NVector3(color.R, color.G, color.B);
			FieldDrawer.Label("Background", null, context);
			edited = EditorGui.ColorEdit3("##background", ref rgb);
			context.Track(edited);
			if (edited)
			{
				splash.BackgroundColor = new Color4(rgb.X, rgb.Y, rgb.Z, 1f);
				changed = true;
			}

			string logoPath = string.IsNullOrEmpty(splash.Logo) ? null : AssetDatabase.GetPath(splash.Logo);
			object logo = logoPath != null ? LoadLogo(logoPath) : null;
			FieldDrawer.Label("Logo", null, context);
			edited = AssetField.Draw(typeof(Texture2D), ref logo, path => MaterialEditor.ShapeMatches(path, typeof(Texture2D)));
			context.Track(edited);
			if (edited)
			{
				splash.Logo = logo is IAsset asset && asset.AssetPath != null ? AssetDatabase.GetGuid(asset.AssetPath) : null;
				logoPath = splash.Logo != null ? AssetDatabase.GetPath(splash.Logo) : null;
				logo = logoPath != null ? LoadLogo(logoPath) : null;
				changed = true;
			}

			if (logo == null && !string.IsNullOrEmpty(splash.Logo))
			{
				ImGui.TextColored(EditorStyle.Warning, "The logo's asset is missing: builds show the background only.");
			}

			float size = splash.LogoSize;
			FieldDrawer.Label("Logo Size", null, context);
			edited = EditorGui.SliderFloat("##logosize", ref size, 0.05f, 1f, "%.2f");
			Tooltip("The logo fits into this fraction of the window's width and height, keeping its aspect ratio.");
			context.Track(edited);
			if (edited)
			{
				splash.LogoSize = Math.Clamp(size, 0.01f, 1f);
				changed = true;
			}

			ImGui.EndDisabled();

			DrawSplashPreview(splash, logo != null ? logoPath : null);
			ImGui.PopID();
			return changed;
		}

		/// <summary>
		///     The splash screen at the aspect ratio of a build's window, as the player draws it: the logo's file values
		///     over the background (the UI copy of the image, gamma storage, the way the build bakes it).
		/// </summary>
		private static void DrawSplashPreview(SplashScreenSettings splash, string logoPath)
		{
			var window = new BootConfig();
			float width = Math.Min(ImGui.GetContentRegionAvail().X, 480f);
			if (width < 16f)
			{
				return;
			}

			float height = width * window.Height / window.Width;
			ImGui.Spacing();
			ImGui.SeparatorText("Preview");
			NVector2 start = ImGui.GetCursorScreenPos();
			NVector2 extent = new NVector2(width, height);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			Color4 color = splash.Enabled ? splash.BackgroundColor : Color4.Black;
			draw.AddRectFilled(start, start + extent, ImGui.GetColorU32(new NVector4(Math.Clamp(color.R, 0f, 1f), Math.Clamp(color.G, 0f, 1f), Math.Clamp(color.B, 0f, 1f), 1f)));
			Texture2D logo = splash.Enabled && logoPath != null ? TextureAssets.LoadForUI(logoPath, logoPath) : null;
			if (logo != null && logo.Gpu != null && logo.width > 0 && logo.height > 0)
			{
				float scale = Math.Min(width * splash.LogoSize / logo.width, height * splash.LogoSize / logo.height);
				var size = new NVector2(logo.width * scale, logo.height * scale);
				NVector2 min = start + (extent - size) * 0.5f;
				draw.AddImage(EditorGui.Texture(logo.Gpu), min, min + size);
			}

			draw.AddRect(start, start + extent, ImGui.GetColorU32(ImGuiCol.Border));
			ImGui.Dummy(extent);
			Hint(splash.Enabled
				? $"{window.Width} x {window.Height}, the window of builds."
				: "No splash screen: the window stays black until the start scene is ready.");
		}

		private static Texture2D LoadLogo(string assetPath)
		{
			try
			{
				return AssetDatabase.Load(typeof(Texture2D), assetPath) as Texture2D;
			}
			catch (Exception)
			{
				return null;
			}
		}

		// A dim line of explanation, wrapped at the window's edge (never cut off).
		private static void Hint(string text)
		{
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled(text);
			ImGui.PopTextWrapPos();
		}

		// The tooltip of a control drawn by hand after FieldDrawer.Label: on its label, as every field's (not on the control).
		private static void Tooltip(string text)
		{
			FieldDrawer.LabelTooltip(text);
		}

		// ---------------------------------------------------------------- build

		/// <summary>Scenes in the build, start scene, output folder, configuration, Self-contained and Run In Background (saved in Project.json as they change), Build / Open Folder / Run and the log.</summary>
		private void DrawBuild(ProjectInfo project)
		{
			ImGui.Text("Scenes in the build");
			bool changed = false;
			List<string> scenes = AssetDatabase.GetAllAssetPaths(SceneSerializer.Extension);
			if (ImGui.BeginChild("##scenes", new NVector2(0, 150), ImGuiChildFlags.Borders))
			{
				foreach (string scene in scenes)
				{
					bool included = project.BuildScenes.Contains(scene) || scene == project.StartScene;
					if (EditorGui.Checkbox(scene, ref included))
					{
						if (included) project.BuildScenes.Add(scene);
						else project.BuildScenes.Remove(scene);
						changed = true;
					}
				}
			}

			ImGui.EndChild();

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Start scene");
			ImGui.SameLine(120);
			ImGui.SetNextItemWidth(-1);
			if (EditorGui.BeginCombo("##start", project.StartScene ?? "(none)"))
			{
				foreach (string scene in scenes)
				{
					if (EditorGui.Selectable(scene, scene == project.StartScene))
					{
						project.StartScene = scene;
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Output folder");
			ImGui.SameLine(120);
			ImGui.SetNextItemWidth(-1);
			string outputFolder = OutputFolderOf(project);
			if (EditorGui.InputText("##output", ref outputFolder, 512))
			{
				project.BuildOutputFolder = outputFolder;
			}

			changed |= ImGui.IsItemDeactivatedAfterEdit();

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Configuration");
			ImGui.SameLine(120);
			if (EditorGui.RadioButton("Release", project.BuildRelease))
			{
				project.BuildRelease = true;
				changed = true;
			}

			ImGui.SameLine();
			if (EditorGui.RadioButton("Debug", !project.BuildRelease))
			{
				project.BuildRelease = false;
				changed = true;
			}

			bool selfContained = project.BuildSelfContained;
			if (EditorGui.Checkbox("Self-contained", ref selfContained))
			{
				project.BuildSelfContained = selfContained;
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				EditorGui.SetTooltip("The game ships the .NET runtime and runs on machines without .NET installed (about 70 MB more).\n" +
					"Off: framework-dependent, smaller; the target machine needs the .NET 10 runtime.");
			}

			string runtime = PlayerFiles.SelfContainedRuntime;
			ImGui.SameLine();
			Hint(!selfContained ? "Framework-dependent: the target machine needs the .NET 10 runtime."
				: runtime != null ? "Includes the .NET runtime for " + runtime + "."
				: "This editor has no self-contained player (published without a runtime identifier).");

			bool readyToRun = project.BuildReadyToRun;
			if (EditorGui.Checkbox("Compile Ahead Of Time (ReadyToRun)", ref readyToRun))
			{
				project.BuildReadyToRun = readyToRun;
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				EditorGui.SetTooltip("The game's code is compiled when it is built, not method by method as it first runs (no stutter the first time\n" +
					"something happens; larger assemblies). Needs a player published that way and the .NET SDK's crossgen compiler:\n" +
					"a build that has neither fails. Off: the scripts compile as they run.");
			}

			bool runInBackground = project.RunInBackground;
			if (EditorGui.Checkbox("Run In Background", ref runInBackground))
			{
				project.RunInBackground = runInBackground;
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				EditorGui.SetTooltip("The game keeps updating and rendering while its window is not focused.");
			}

			if (changed)
			{
				project.Save();
			}

			ImGui.Spacing();
			bool building = buildTask != null && !buildTask.IsCompleted;
			// The build waits for the scripts the editor compiles: their errors (the editor scripts' too) fail it.
			bool compiling = editor.Scripts != null && editor.Scripts.IsCompiling;
			ImGui.BeginDisabled(building || compiling);
			if (EditorGui.Button(building ? "Building..." : compiling ? "Compiling..." : "Build", new NVector2(140, 0)))
			{
				// The build reads the scenes' files: the user decides about changes they do not have yet.
				editor.AskToSaveScene(() => StartBuild(project));
			}

			ImGui.EndDisabled();
			if (lastOutput != null && !building)
			{
				ImGui.SameLine();
				if (EditorGui.Button("Open Folder", new NVector2(140, 0)))
				{
					Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lastOutput + "\"") { UseShellExecute = true });
				}

				ImGui.SameLine();
				if (EditorGui.Button("Run", new NVector2(100, 0)))
				{
					string exe = Path.Combine(lastOutput, ProjectGenerator.ProjectName(project) + ".exe");
					Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = lastOutput, UseShellExecute = false });
				}
			}

			ImGui.Separator();
			if (ImGui.BeginChild("##log", new NVector2(0, 0), ImGuiChildFlags.Borders))
			{
				lock (lines)
				{
					foreach (string line in lines)
					{
						if (line.StartsWith("Error", StringComparison.Ordinal) || line.Contains(": error "))
						{
							ImGui.TextColored(EditorStyle.Error, line);
						}
						else
						{
							ImGui.TextUnformatted(line);
						}
					}
				}
			}

			ImGui.EndChild();
		}

		// ---------------------------------------------------------------- graphics

		/// <summary>
		///     The shaders every build ships although nothing of it references them (scripts load them by path): a list
		///     that takes shader files dropped on it from the Project panel, a row each (a click shows the file, X takes
		///     it off), and a combo of the project's other shaders. Undoable, saved with the project settings.
		/// </summary>
		/// <summary>
		///     Super sampling anti-aliasing: the factor (NVIDIA DSR's, with what it makes of a 1080p window), the filter and
		///     the Gaussian's smoothness. Undoable, applied on the next frame of the Game view and the player.
		/// </summary>
		private bool DrawSuperSampling(SuperSamplingSettings content)
		{
			bool changed = false;
			ImGui.SeparatorText("Anti-aliasing");
			FieldDrawer.Label("Super Sampling", null, context);
			ImGui.SetNextItemWidth(-1f);
			if (EditorGui.BeginCombo("##supersampling", FactorLabel(content.Factor)))
			{
				foreach (float factor in SuperSamplingSettings.Factors)
				{
					bool selected = Math.Abs(factor - content.Factor) < 0.005f;
					if (EditorGui.Selectable(FactorLabel(factor), selected) && !selected)
					{
						// The "before" state is taken first: taken after the change, the edit showed no difference and was
						// neither saved nor undoable (the value lived in memory only until the editor closed).
						context.BeginEdit();
						content.Factor = factor;
						context.EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			Tooltip("The scene renders at this many times the window's pixels and is filtered down to it after tonemapping\n" +
				"(the UI draws at the window's resolution). The factors are NVIDIA DSR's: 1.78x and 2.25x are also DLDSR's.\n" +
				"DSR does the same in the driver (the game at a larger 'display' resolution); both at once multiply the cost.\n" +
				"The screen-space GI and the clouds keep the window's density: their cost does not grow with it.\n" +
				"Builds start with this; games change it at run time (QualitySettings.superSampling) and the player's\n" +
				"command line overrides it: ssaa=2.25 ssaafilter=lanczos ssaasmooth=0.33.");

			if (content.Factor > 1.001f)
			{
				FieldDrawer.Label("Filter", null, context);
				ImGui.SetNextItemWidth(-1f);
				if (EditorGui.BeginCombo("##supersamplingfilter", content.Filter.ToString()))
				{
					foreach (SuperSamplingFilter filter in Enum.GetValues<SuperSamplingFilter>())
					{
						bool selected = filter == content.Filter;
						if (EditorGui.Selectable(filter.ToString(), selected) && !selected)
						{
							context.BeginEdit();
							content.Filter = filter;
							context.EndEdit();
							changed = true;
						}
					}

					EditorGui.EndCombo();
				}

				Tooltip("Gaussian: soft, no ringing (DSR's filter; Smoothness widens it).\n" +
					"Area: each window pixel the exact average of what it covers: the sharpest without ringing.\n" +
					"Lanczos: the crispest (a sharpened look, like DLDSR's), a little ringing at hard edges.");

				if (content.Filter == SuperSamplingFilter.Gaussian)
				{
					float smoothness = content.Smoothness;
					FieldDrawer.Label("Smoothness", null, context);
					ImGui.SetNextItemWidth(-1f);
					bool edited = EditorGui.SliderFloat("##supersamplingsmoothness", ref smoothness, 0f, 1f, "%.2f");
					Tooltip("DSR's Smoothness: 0 keeps the most detail, 1 is soft (0.33 by default, as DSR's 33 %).");
					context.Track(edited);
					if (edited)
					{
						content.Smoothness = Math.Clamp(smoothness, 0f, 1f);
						changed = true;
					}
				}
			}

			return changed;
		}

		/// <summary>
		///     The graphics API of the project's builds and of the editor running it. The device is made once per process,
		///     so a change shows the API the editor runs on and offers to start it again (nothing unsaved may be left).
		/// </summary>
		private bool DrawGraphicsApi(GraphicsSettings content)
		{
			bool changed = false;
			ImGui.SeparatorText("Rendering");
			FieldDrawer.Label("Graphics API", null, context);
			ImGui.SetNextItemWidth(-1f);
			if (EditorGui.BeginCombo("##graphicsapi", content.GraphicsApi.ToString()))
			{
				foreach (Graphics.GraphicsApi api in Enum.GetValues<Graphics.GraphicsApi>())
				{
					bool selected = api == content.GraphicsApi;
					if (EditorGui.Selectable(api.ToString(), selected) && !selected)
					{
						context.BeginEdit();
						content.GraphicsApi = api;
						context.EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			Tooltip("The graphics API built games draw with, and the editor runs this project on (so the editor shows what the game shows).\n" +
				"Vulkan: the engine's native path. OpenGL: the same rendering through OpenGL 4.3 .. 4.6.\n" +
				"A machine without a Vulkan driver or GPU runs the game on OpenGL instead (Player.log says so).\n" +
				"The player's command line overrides it: graphics=opengl or graphics=vulkan; the editor's too.");

			Graphics.GraphicsDevice device = Graphics.GraphicsDevice.Current;
			if (device != null && device.Api != content.GraphicsApi)
			{
				if (Graphics.GraphicsDevice.FellBackFrom == content.GraphicsApi)
				{
					Hint($"{content.GraphicsApi} is not available on this machine: the editor runs on {device.Api}.");
				}
				else
				{
					Hint($"The editor runs on {device.Api}: {content.GraphicsApi} takes effect when it starts again.");
					if (EditorGui.Button("Restart Editor"))
					{
						try
						{
							editor.RestartEditor();
						}
						catch (InvalidOperationException e)
						{
							Debug.LogWarning("[Editor] Restart Editor: " + e.Message);
						}
					}
				}
			}

			return changed;
		}

		/// <summary>"1.78x (2560 x 1440 at 1080p)", "Off".</summary>
		private static string FactorLabel(float factor)
		{
			if (!(factor > 1.001f))
			{
				return "Off";
			}

			(int width, int height) = SuperSamplingSettings.RenderSize(1920, 1080, factor);
			string dl = Math.Abs(factor - 1.78f) < 0.005f || Math.Abs(factor - 2.25f) < 0.005f ? ", DSR / DLDSR" : ", DSR";
			return $"{SuperSamplingSettings.Label(factor)}  ({width} x {height} at 1080p{dl})";
		}

		/// <summary>Labels of Frames In Flight's choices (0: Auto).</summary>
		private static readonly string[] FramesInFlightLabels = { "Auto (2 on a GPU of its own, 1 integrated)", "1 (least latency)", "2 (smoother when the CPU varies)" };

		private bool DrawFramesInFlight(GraphicsSettings content)
		{
			bool changed = false;
			int current = Math.Clamp(content.FramesInFlight, 0, 2);
			FieldDrawer.Label("Frames In Flight", null, context);
			ImGui.SetNextItemWidth(-1f);
			if (EditorGui.BeginCombo("##framesinflight", FramesInFlightLabels[current]))
			{
				for (int i = 0; i < FramesInFlightLabels.Length; i++)
				{
					bool selected = i == current;
					if (EditorGui.Selectable(FramesInFlightLabels[i], selected) && !selected)
					{
						context.BeginEdit();
						content.FramesInFlight = i;
						context.EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			Tooltip("Frames a built game's CPU may prepare ahead of the GPU on Vulkan (OpenGL's driver decides for itself).\n" +
				"1: the next frame is recorded while the GPU draws the last one: the least latency between input and screen.\n" +
				"2: the GPU queues two frames, so a CPU frame that takes longer than the others does not leave it waiting:\n" +
				"smoother on a GPU of its own, one frame more of latency. Auto: 2 on a GPU of its own, 1 on an integrated one\n" +
				"(it shares memory and power with the CPU). The editor keeps its own (Auto). The player's command line\n" +
				"overrides it: framesinflight=1 or 2.");
			return changed;
		}

		private bool DrawGraphics(GraphicsSettings content)
		{
			bool changed = DrawGraphicsApi(content);
			ImGui.Spacing();
			ImGui.SeparatorText("Display");
			bool vsync = content.VSync;
			FieldDrawer.Label("VSync", null, context);
			bool edited = EditorGui.Checkbox("##vsync", ref vsync);
			Tooltip("Built games wait for the display's refresh to show a frame: no tearing, frames shown evenly.\n" +
				"Off: they render as fast as they can, and a slower display shows them at uneven moments (stutter).\n" +
				"The editor always waits. The player's command line overrides it: vsync=0 or vsync=1.");
			context.Track(edited);
			if (edited)
			{
				content.VSync = vsync;
				changed = true;
			}

			changed |= DrawFramesInFlight(content);
			ImGui.Spacing();
			changed |= DrawSuperSampling(content.SuperSampling ??= new SuperSamplingSettings());

			ImGui.Spacing();
			ImGui.SeparatorText("Batching");
			bool staticBatching = content.StaticBatching;
			FieldDrawer.Label("Static Batching", null, context);
			bool batchingEdited = EditorGui.Checkbox("##staticbatching", ref staticBatching);
			Tooltip("The meshes of objects marked Static that are drawn one by one (transparent materials, opaque ones whose shader\n" +
				"has no GPU-driven variant) are merged into batches: objects that draw one after another go in one draw call.\n" +
				"The picture stays the same (a transparent batch is still sorted object by object). Built games get the batches\n" +
				"with their scenes, the editor makes them when play mode starts (kept in the project's Library); the scenes in\n" +
				"the project stay as they are. The console (a build's log) says what was merged.");
			context.Track(batchingEdited);
			if (batchingEdited)
			{
				content.StaticBatching = staticBatching;
				changed = true;
			}

			ImGui.Spacing();
			ImGui.SeparatorText("Always Included Shaders");
			ImGui.PushTextWrapPos(0f);
			Hint("A build ships the shaders its materials use. A shader that only a script loads by its path (new ShaderProgram(\"Shaders/Fog.shader\")) is used by nothing the build can see, and the built game would not find it: put it on this list.");
			ImGui.PopTextWrapPos();
			ImGui.Spacing();

			List<IncludedAsset> shaders = content.AlwaysIncludedShaders ??= new List<IncludedAsset>();
			float height = Math.Clamp(shaders.Count + 1, 4, 14) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2f;
			int remove = -1;
			if (ImGui.BeginChild("##includedshaders", new NVector2(0, height), ImGuiChildFlags.Borders))
			{
				if (shaders.Count == 0)
				{
					Hint("Drag shader files here from the Project panel.");
				}

				float buttonWidth = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2f;
				for (int i = 0; i < shaders.Count; i++)
				{
					IncludedAsset entry = shaders[i];
					string path = entry?.Resolve();
					ImGui.PushID(i);
					var size = new NVector2(Math.Max(40f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X), 0f);
					if (path != null)
					{
						if (EditorGui.Selectable(path, false, size))
						{
							editor.Ping(path);
						}

						if (ImGui.IsItemHovered())
						{
							EditorGui.SetTooltip("Click: shows the file in the Project panel.");
						}
					}
					else
					{
						// Deleted, or its meta file lost: the build warns and goes on without it.
						ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Warning);
						EditorGui.Selectable((entry?.Path ?? "?") + " (missing)", false, size);
						ImGui.PopStyleColor();
					}

					ImGui.SameLine();
					if (EditorGui.SmallButton("X"))
					{
						remove = i;
					}

					if (ImGui.IsItemHovered())
					{
						EditorGui.SetTooltip("Take it off the list.");
					}

					ImGui.PopID();
				}
			}

			ImGui.EndChild();

			// The whole list takes the shader files dropped on it.
			if (DragAndDrop.AcceptFiles(IsShaderFile, out List<string> dropped))
			{
				context.BeginEdit();
				foreach (string path in dropped)
				{
					changed |= content.AddShader(path);
				}

				context.EndEdit();
			}

			if (TestAnchors.Enabled)
			{
				TestAnchors.Record("includedshaders", ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
			}

			ImGui.SetNextItemWidth(-1);
			if (EditorGui.BeginCombo("##addshader", "Add Shader..."))
			{
				bool any = false;
				foreach (string path in AssetDatabase.GetAllAssetPaths(ShaderExtension))
				{
					if (content.IncludesShader(path))
					{
						continue;
					}

					any = true;
					if (EditorGui.Selectable(path))
					{
						context.BeginEdit();
						changed |= content.AddShader(path);
						context.EndEdit();
					}
				}

				if (!any)
				{
					Hint("Every shader of the project is on the list.");
				}

				EditorGui.EndCombo();
			}

			if (remove >= 0)
			{
				context.BeginEdit();
				shaders.RemoveAt(remove);
				context.EndEdit();
				changed = true;
			}

			ImGui.Spacing();
			ImGui.PushTextWrapPos(0f);
			Hint("Drag shader files onto the list from the Project panel, or pick them above. Their #include files go with them; the build checks that they compile, and the built game compiles them while it loads.");
			ImGui.PopTextWrapPos();
			return changed;
		}

		private static bool IsShaderFile(string assetPath)
		{
			return assetPath.EndsWith(ShaderExtension, StringComparison.OrdinalIgnoreCase) && !AssetDatabase.IsEngineResource(assetPath);
		}

		private static string OutputFolderOf(ProjectInfo project)
		{
			return string.IsNullOrEmpty(project.BuildOutputFolder) ? "Build/" + ProjectGenerator.ProjectName(project) : project.BuildOutputFolder;
		}

		private void StartBuild(ProjectInfo project)
		{
			if (editor.Scripts != null && editor.Scripts.HasErrors)
			{
				lock (lines)
				{
					lines.Clear();
				}

				Append("Error: the scripts have compile errors (see the Console); nothing was built.");
				return;
			}

			// The build copies the settings file; an edit still in progress is committed (and saved) first.
			editor.Edits.Commit();
			editor.SaveProjectSettings();

			string output = Path.GetFullPath(Path.Combine(project.RootPath, OutputFolderOf(project)));
			var settings = new BuildSettings { OutputFolder = output, Release = project.BuildRelease, SelfContained = project.BuildSelfContained, ReadyToRun = project.BuildReadyToRun, StaticBatching = ProjectSettings.Current.Graphics?.StaticBatching ?? true, StartScene = project.StartScene, Scenes = new List<string>(project.BuildScenes) };
			lock (lines)
			{
				lines.Clear();
			}

			// The GI distance fields need the GL context, which is this thread's: baked now into the cache the build
			// thread reads (usually fast: the fields of unchanged meshes are cached).
			try
			{
				var scenes = new List<string>(settings.Scenes);
				if (!scenes.Contains(settings.StartScene))
				{
					scenes.Insert(0, settings.StartScene);
				}

				DistanceFieldBaker.Collect(project, scenes, Append);
			}
			catch (Exception e)
			{
				Append("Warning: the distance fields could not be baked (" + e.Message + "); the player builds them.");
			}

			buildTask = Task.Run(() =>
			{
				bool ok;
				try
				{
					ok = GameBuilder.Build(project, settings, Append);
				}
				catch (Exception e)
				{
					Append("Error: " + e.Message);
					ok = false;
				}

				if (ok)
				{
					lastOutput = output;
				}

				return ok;
			});
		}

		private void Append(string line)
		{
			lock (lines)
			{
				lines.Add(line);
			}

			Console.WriteLine("[Build] " + line);
		}

		#endregion
	}
}
