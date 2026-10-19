using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Project Settings (Edit > Project Settings...), one tab each: the project-wide lighting quality (shadow map,
	///     GI method and its tunables), the names of the user layers, 2D physics (simulation plane, gravity, solver, queries, layer collision matrix),
	///     the splash screen of builds (duration, background, logo, with a preview) and the build (scenes, start scene,
	///     output folder, configuration; builds in the background). Lighting, physics and splash screen edits are
	///     undoable and saved to ProjectSettings/ProjectSettings.asset when they end; the lighting of a scene
	///     (environment, shadow reach, GI volume) stays in the Lighting panel.
	/// </summary>
	internal sealed class ProjectSettingsWindow
	{
		#region PublicFields

		public enum Tab
		{
			Lighting,
			Layers,
			Physics2D,
			SplashScreen,
			Build
		}

		public bool Open;

		/// <summary>The tab shown last (saved with the editor's windows: ImGui does not keep a tab bar's selection).</summary>
		public Tab CurrentTab { get; private set; } = Tab.Lighting;

		#endregion

		#region PrivateFields

		private static readonly string[] PlaneNames = { "XY (side view)", "XZ (top-down)" };

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private Tab? requestedTab;
		private bool focus;

		// ---- build
		private string outputFolder;
		private bool release = true;
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
			if (!ImGui.BeginTabItem(label, flags))
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

		// ---------------------------------------------------------------- lighting

		/// <summary>
		///     The GI method combo (project-wide; the context's undo target must be the project settings). Also drawn at
		///     the top of the Lighting panel's GI section.
		/// </summary>
		internal static bool DrawGIMethod(GlobalIlluminationSettings gi, InspectorContext context)
		{
			FieldDrawer.Label("GI Method", null, context);
			GIMode selected = gi.Mode;
			if (ImGui.BeginCombo("##gimethod", SerializationUtility.NicifyName(selected.ToString())))
			{
				foreach (GIMode mode in Enum.GetValues<GIMode>())
				{
					if (ImGui.Selectable(SerializationUtility.NicifyName(mode.ToString()), mode == selected))
					{
						selected = mode;
					}
				}

				ImGui.EndCombo();
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

		private bool DrawLighting(ProjectSettings settings)
		{
			// The method comes first: the GI sections below show only the settings it reads.
			GlobalIlluminationSettings gi = settings.GI;
			bool changed = DrawGIMethod(gi, context);
			ImGui.Spacing();
			ImGui.TextDisabled("How shadows and global illumination are computed in every scene.");
			ImGui.TextDisabled("A scene's environment, shadow reach and GI volume are in the Lighting panel.");
			ImGui.Spacing();

			if (ImGui.CollapsingHeader("Shadows", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("shadows");
				changed |= FieldDrawer.DrawFields(settings.Shadows, context);
				// Below the cascade count (the last field): the splits, which the fields leave out.
				changed |= CascadeSplitsEditor.Draw(settings.Shadows, context);
				ImGui.PopID();
			}

			// Settings both methods read, then those of the selected method only (the header keeps its id, and so
			// its open state, when the method changes).
			GIMode mode = gi.Mode;
			ImGui.PushID("gi");
			if (ImGui.CollapsingHeader("Global Illumination", ImGuiTreeNodeFlags.DefaultOpen)
				&& FieldDrawer.DrawFields(gi, context, filter: field => field.Name != nameof(GlobalIlluminationSettings.Mode) && GIMethodAttribute.Of(field.Field) == null))
			{
				gi.InvalidateStatic();
				changed = true;
			}

			if (ImGui.CollapsingHeader(SerializationUtility.NicifyName(mode.ToString()) + "###method", ImGuiTreeNodeFlags.DefaultOpen)
				&& FieldDrawer.DrawFields(gi, context, filter: field => GIMethodAttribute.Of(field.Field) == mode))
			{
				gi.InvalidateStatic();
				changed = true;
			}

			ImGui.PopID();
			return changed;
		}

		// ---------------------------------------------------------------- layers

		/// <summary>The layer names: built in for 0..7, the project's for 8..31 (objects keep their layer when it is renamed).</summary>
		private bool DrawLayers(LayerSettings layers)
		{
			ImGui.TextDisabled("Every object is on one layer (Inspector header). Cameras draw the layers of their culling mask,");
			ImGui.TextDisabled("2D physics collides layers by the matrix in Physics 2D.");
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
					ImGui.InputText("##name", ref fixedName, 64, ImGuiInputTextFlags.ReadOnly);
					ImGui.EndDisabled();
				}
				else
				{
					string name = layers.GetName(layer);
					bool edited = ImGui.InputText("##name", ref name, 64);
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
				ImGui.SetTooltip(editor.IsPlaying
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
			if (!ImGui.TreeNodeEx("Layer Collision Matrix", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen))
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
						if (ImGui.Checkbox("##c", ref collide))
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
			ImGui.TextDisabled("The first image of a build: up a moment after its window opens, while the start scene");
			ImGui.TextDisabled("loads in the background. The start scene replaces it once loaded and the duration is over.");
			ImGui.Spacing();

			ImGui.PushID("splash");
			bool changed = false;

			bool enabled = splash.Enabled;
			FieldDrawer.Label("Show Splash Screen", null, context);
			bool edited = ImGui.Checkbox("##enabled", ref enabled);
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
			edited = ImGui.SliderFloat("##duration", ref duration, 0f, 10f, "%.1f s");
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
			edited = ImGui.ColorEdit3("##background", ref rgb);
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
			edited = ImGui.SliderFloat("##logosize", ref size, 0.05f, 1f, "%.2f");
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
			if (logo != null && logo.Handle != 0 && logo.width > 0 && logo.height > 0)
			{
				float scale = Math.Min(width * splash.LogoSize / logo.width, height * splash.LogoSize / logo.height);
				var size = new NVector2(logo.width * scale, logo.height * scale);
				NVector2 min = start + (extent - size) * 0.5f;
				draw.AddImage(EditorGui.Texture(logo.Handle), min, min + size);
			}

			draw.AddRect(start, start + extent, ImGui.GetColorU32(ImGuiCol.Border));
			ImGui.Dummy(extent);
			ImGui.TextDisabled(splash.Enabled
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

		private static void Tooltip(string text)
		{
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(text);
			}
		}

		// ---------------------------------------------------------------- build

		/// <summary>Scenes in the build, start scene and Run In Background (saved in Project.json), output folder, configuration, Build / Open Folder / Run and the log.</summary>
		private void DrawBuild(ProjectInfo project)
		{
			outputFolder ??= Path.Combine(project.RootPath, "Build", ProjectGenerator.ProjectName(project));

			ImGui.Text("Scenes in the build");
			bool changed = false;
			List<string> scenes = AssetDatabase.GetAllAssetPaths(SceneSerializer.Extension);
			if (ImGui.BeginChild("##scenes", new NVector2(0, 150), ImGuiChildFlags.Borders))
			{
				foreach (string scene in scenes)
				{
					bool included = project.BuildScenes.Contains(scene) || scene == project.StartScene;
					if (ImGui.Checkbox(scene, ref included))
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
			if (ImGui.BeginCombo("##start", project.StartScene ?? "(none)"))
			{
				foreach (string scene in scenes)
				{
					if (ImGui.Selectable(scene, scene == project.StartScene))
					{
						project.StartScene = scene;
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Output folder");
			ImGui.SameLine(120);
			ImGui.SetNextItemWidth(-1);
			ImGui.InputText("##output", ref outputFolder, 512);

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Configuration");
			ImGui.SameLine(120);
			if (ImGui.RadioButton("Release", release)) release = true;
			ImGui.SameLine();
			if (ImGui.RadioButton("Debug", !release)) release = false;
			ImGui.TextDisabled("Framework-dependent build: the target machine needs the .NET 10 runtime.");

			bool runInBackground = project.RunInBackground;
			if (ImGui.Checkbox("Run In Background", ref runInBackground))
			{
				project.RunInBackground = runInBackground;
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("The game keeps updating and rendering while its window is not focused.");
			}

			if (changed)
			{
				project.Save();
			}

			ImGui.Spacing();
			bool building = buildTask != null && !buildTask.IsCompleted;
			ImGui.BeginDisabled(building);
			if (ImGui.Button(building ? "Building..." : "Build", new NVector2(140, 0)))
			{
				StartBuild(project);
			}

			ImGui.EndDisabled();
			if (lastOutput != null && !building)
			{
				ImGui.SameLine();
				if (ImGui.Button("Open Folder", new NVector2(140, 0)))
				{
					Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lastOutput + "\"") { UseShellExecute = true });
				}

				ImGui.SameLine();
				if (ImGui.Button("Run", new NVector2(100, 0)))
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

		private void StartBuild(ProjectInfo project)
		{
			if (editor.SceneDirty)
			{
				editor.SaveScene();
			}

			// The build copies the settings file; an edit still in progress is committed (and saved) first.
			editor.Edits.Commit();
			editor.SaveProjectSettings();

			var settings = new BuildSettings { OutputFolder = outputFolder, Release = release, StartScene = project.StartScene, Scenes = new List<string>(project.BuildScenes) };
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

			string output = Path.GetFullPath(outputFolder);
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
