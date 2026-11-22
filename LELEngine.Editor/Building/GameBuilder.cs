using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using LELEngine.Rendering;
using LELEngine.Rendering.DistanceField;
using LELEngine.Serialization;
using LELEngine.Shaders;
using LELEngine.Mathematics;

namespace LELEngine.Editor
{
	internal sealed class BuildSettings
	{
		public string OutputFolder;
		public bool Release = true;

		/// <summary>With the .NET runtime (<see cref="PlayerFiles.SelfContainedFolder" />), else framework-dependent.</summary>
		public bool SelfContained;

		/// <summary>
		///     The game ships compiled ahead of time (ReadyToRun): the player must be published that way and the scripts
		///     are compiled for it; a build that cannot do either fails.
		/// </summary>
		public bool ReadyToRun;

		/// <summary>
		///     The shipped scenes' static renderers that draw one by one are merged into batches (Project Settings > Graphics >
		///     Static Batching; <see cref="StaticBatcher" />).
		/// </summary>
		public bool StaticBatching = true;

		public string StartScene;
		public List<string> Scenes = new List<string>();
	}

	/// <summary>
	///     Produces a runnable game folder:
	///     <code>
	///     Output/
	///       &lt;Name&gt;.exe + LELEngine.Player.dll, engine and BRUTAL assemblies, native GLFW and GLAD (runtimes/), and the
	///                         .NET runtime when the build is self-contained (see <see cref="PlayerFiles" />)
	///       Shaders/Engine/   engine shader library
	///       Game.dll          the scripts, compiled in the chosen configuration
	///       Data/             the assets the build uses, with their .meta files: the scenes, everything in Resources
	///                         folders and what those use (<see cref="BuildDependencies" />; Library/BuildReport.txt
	///                         lists what went in and what was left out). Documents binary, models imported, images
	///                         baked to KTX, models' GI distance fields next to them (.sdf)
	///       ProjectSettings.asset  physics and lighting quality (binary)
	///       Splash.bmp        the splash screen's logo baked over its background color (when it has one)
	///       boot.json         start scene, window title, splash screen
	///     </code>
	///     Runs on a background thread; <paramref name="log" /> is called from it.
	/// </summary>
	internal static class GameBuilder
	{
		#region PublicMethods

		public static bool Build(ProjectInfo project, BuildSettings settings, Action<string> log)
		{
			string output = Path.GetFullPath(settings.OutputFolder);
			if (IsInside(output, project.AssetsPath))
			{
				log("Error: the build folder must not be inside Assets.");
				return false;
			}

			if (string.IsNullOrEmpty(settings.StartScene))
			{
				log("Error: choose a start scene.");
				return false;
			}

			if (!File.Exists(Path.Combine(project.AssetsPath, settings.StartScene)))
			{
				log("Error: the start scene does not exist: " + settings.StartScene);
				return false;
			}

			// The player (a development editor publishes its self-contained one first when it has none).
			string playerSource = PlayerFiles.Resolve(settings.SelfContained, log);
			if (playerSource == null)
			{
				return false;
			}

			// Compile Ahead Of Time: the player itself must be (it is when it was published for a platform), said before
			// anything is built.
			if (settings.ReadyToRun)
			{
				string playerError = ReadyToRunCompiler.CheckPlayer(playerSource);
				if (playerError != null)
				{
					log("Error: " + playerError);
					return false;
				}
			}

			// 1. Scripts.
			bool hasScripts = Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).GetEnumerator().MoveNext();
			string scriptsOutput = Path.Combine(project.LibraryPath, "PlayerScripts", settings.Release ? "Release" : "Debug");
			if (hasScripts)
			{
				log($"Compiling scripts ({(settings.Release ? "Release" : "Debug")})...");
				ProjectGenerator.Generate(project);
				BuildResult scripts = ScriptBuilder.Build(ProjectGenerator.ProjectFile(project), settings.Release ? "Release" : "Debug", scriptsOutput);
				foreach (BuildDiagnostic diagnostic in scripts.Diagnostics)
				{
					log(diagnostic.ToString());
				}

				if (!scripts.Success)
				{
					log("Error: the scripts do not compile; nothing was written.");
					return false;
				}
			}

			Directory.CreateDirectory(output);

			// 2. Player and engine.
			log(settings.SelfContained ? $"Copying the player (self-contained, {PlayerFiles.SelfContainedRuntime})..." : "Copying the player (framework-dependent)...");
			PlayerFiles.RemoveOtherPlayer(output, playerSource, log);
			// The player's app host: LELEngine.Player.exe on Windows, LELEngine.Player (no extension) when it was
			// published for Linux or macOS (the editor's PlayerRuntimeIdentifier).
			string host = File.Exists(Path.Combine(playerSource, PlayerFiles.AssemblyName + ".exe")) ? PlayerFiles.AssemblyName + ".exe" : PlayerFiles.AssemblyName;
			string executable = ProjectGenerator.ProjectName(project) + Path.GetExtension(host);
			foreach (string file in Directory.GetFiles(playerSource, "*", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(playerSource, file);
				if (relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && settings.Release)
				{
					continue;
				}

				// The executable takes the game's name; it still starts LELEngine.Player.dll.
				string target = string.Equals(relative, host, StringComparison.Ordinal) ? executable : relative;
				Copy(file, Path.Combine(output, target));
			}

			// 3. Scripts (and the packages they use).
			if (hasScripts)
			{
				foreach (string file in Directory.GetFiles(scriptsOutput))
				{
					string name = Path.GetFileName(file);
					bool symbols = name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
					if ((symbols && settings.Release) || File.Exists(Path.Combine(output, name)) && !name.StartsWith(ProjectGenerator.AssemblyName + ".", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					Copy(file, Path.Combine(output, name));
				}

				// 3b. Compile Ahead Of Time (the build setting): the scripts are compiled for the player's platform, as the
				//     player's own assemblies are (ReadyToRun); their methods would otherwise compile as they first run,
				//     most of them in the frame a scene starts. The setting is a promise: a build that cannot keep it fails.
				if (settings.ReadyToRun)
				{
					string scriptsAssembly = ProjectGenerator.AssemblyName + ".dll";
					string error = ReadyToRunCompiler.Compile(Path.Combine(scriptsOutput, scriptsAssembly), Path.Combine(output, scriptsAssembly), output, playerSource, log);
					if (error != null)
					{
						log("Error: " + error + " Nothing more was written (Compile Ahead Of Time is on in the build settings).");
						return false;
					}
				}
			}

			// 4. Assets: the scenes, everything in Resources folders and what those use, transitively (the meta files keep
			//    the GUIDs the scenes reference); the rest of the project is left out. The editor's readable JSON
			//    documents (scenes, materials, meta files, physics materials, ScriptableObjects) become binary: the
			//    player loads them without parsing text, and no type has to be loaded to convert them. Model files (.obj,
			//    .fbx, ...) are written as their imported data under their own names, images as baked KTX textures (BC7
			//    with their mip levels: nothing to decode) but for sprites and the images of UI documents (the UI draws
			//    images as they are).
			var buildScenes = new List<string>(settings.Scenes);
			if (!buildScenes.Contains(settings.StartScene))
			{
				buildScenes.Insert(0, settings.StartScene);
			}

			log("Collecting the assets the build uses...");
			BuildDependencies dependencies = BuildDependencies.Collect(project, buildScenes);
			foreach (string warning in dependencies.Warnings)
			{
				log("Warning: " + warning);
			}

			// The stored types (components, ScriptableObjects, objects of polymorphic fields) must exist in the player.
			foreach (string problem in dependencies.CheckTypes(hasScripts ? scriptsOutput : null))
			{
				log("Warning: " + problem);
			}

			string reportFile = Path.Combine(project.LibraryPath, "BuildReport.txt");
			try
			{
				Directory.CreateDirectory(project.LibraryPath);
				File.WriteAllText(reportFile, dependencies.Report(project));
			}
			catch (Exception e)
			{
				log("Warning: the build report could not be written (" + e.Message + ").");
			}

			log($"Assets: {dependencies.Included.Count} files ({dependencies.SceneCount} scenes, {dependencies.ResourceCount} in Resources folders, the rest used by them); report: {reportFile}");

			// The shader variants the materials draw with, checked by the reference compiler (glslang: no GPU, the same
			// verdict on every machine); one that does not compile stops the build.
			ShaderVariantSet variants = dependencies.CollectShaderVariants();
			if (!ValidateShaderVariants(variants.Variants, log))
			{
				log("Error: shader variants do not compile; nothing was written.");
				return false;
			}

			foreach (string scene in buildScenes)
			{
				log($"Shader variants of {scene}: {variants.Of(scene)?.Count ?? 0} of {variants.Variants.Count}" + (scene == settings.StartScene ? " (compiled while the player loads)" : ""));
			}

			log("Copying assets...");
			string data = Path.Combine(output, "Data");
			if (Directory.Exists(data))
			{
				// Files of an earlier build the scenes no longer use must not stay loadable.
				Directory.Delete(data, true);
			}

			int copied = 0;
			int converted = 0;
			int models = 0;
			var batching = new BatchingStats();
			var textures = new TextureBakeStats();
			var assets = new List<string>(dependencies.Included);
			assets.Sort(StringComparer.OrdinalIgnoreCase);
			HashSet<string> uiImages = CollectUiImageNames(assets);
			foreach (string assetPath in assets)
			{
				string file = AssetDatabase.ToAbsolute(assetPath);
				string relative = assetPath.Replace('/', Path.DirectorySeparatorChar);
				string target = Path.Combine(data, relative);
				string meta = file + AssetDatabase.MetaExtension;
				if (File.Exists(meta) && ConvertDocument(meta, target + AssetDatabase.MetaExtension, relative + AssetDatabase.MetaExtension, log))
				{
					converted++;
				}

				if (TextureAssets.IsImageFile(file) && IsBakedTexture(assetPath, uiImages))
				{
					try
					{
						byte[] ktx = TextureBaker.Bake(project, assetPath, out bool fromCache);
						Directory.CreateDirectory(Path.GetDirectoryName(target));
						File.WriteAllBytes(target, ktx);
						textures.Add(new FileInfo(file).Length, ktx.Length, fromCache, KtxFile.Read(ktx).IsCompressed);
					}
					catch (Exception e)
					{
						// The player reads the source images then: six-file cubemaps and numbered volumes need their siblings.
						log("Warning: " + relative + " could not be baked (" + e.Message + "); copied as is.");
						foreach (string source in TextureAssets.GetSourceFiles(assetPath))
						{
							Copy(AssetDatabase.ToAbsolute(source), Path.Combine(data, source.Replace('/', Path.DirectorySeparatorChar)));
						}
					}
				}
				else if (ModelAssets.IsModelFile(file))
				{
					// Models ship imported (the binary under the model's own name): the player needs no importer.
					try
					{
						ModelData model = ModelAssets.LoadData(AssetDatabase.ToAssetPath(file));
						Directory.CreateDirectory(Path.GetDirectoryName(target));
						using (FileStream stream = File.Create(target))
						{
							ModelAssets.Write(stream, model, null);
						}

						models++;
					}
					catch (Exception e)
					{
						log("Error: " + relative + " could not be imported (" + e.Message + ").");
						return false;
					}
				}
				else if (Documents.IsDocumentFile(file))
				{
					// 4a. Static batching: a scene's static renderers that draw one by one are merged into batches (their
					//     objects added to the built scene, their meshes shipped under Library/StaticBatches); the batches
					//     come from the project's Library when the scene and what it uses did not change.
					Func<byte[], byte[]> batch = null;
					if (settings.StaticBatching && assetPath.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase))
					{
						string scenePath = assetPath;
						batch = bytes => BatchScene(project, bytes, scenePath, data, batching, log);
					}

					if (ConvertDocument(file, target, relative, log, batch))
					{
						converted++;
					}
				}
				else
				{
					Copy(file, target);
				}

				copied++;
			}

			log($"Converted {converted} documents to binary, imported {models} models.");
			if (settings.StaticBatching)
			{
				log(batching.ToString());
			}

			if (textures.Count > 0)
			{
				log(textures.ToString());
			}

			// The GUID index of the shipped assets: the player reads one file instead of every meta file.
			AssetDatabase.WriteIndex(Path.Combine(data, AssetDatabase.IndexFileName), path => dependencies.Included.Contains(path));

			// The shader variants for the player (Data/ShaderVariants.txt): its start scene's compile while it loads, the
			// rest when the game asks (ShaderCompilation).
			ShaderVariantList.Write(Path.Combine(data, ShaderVariantList.FileName), variants);

			// 4b. The GI's mesh distance fields at the scales the scenes draw the meshes ("model.ext.sdf" next to each
			//     model): baked here when this thread has the GL context (batch mode), by the editor before the build
			//     otherwise; cached in Library.
			DistanceFieldBaker.Result fields = DistanceFieldBaker.Collect(project, buildScenes, log, dependencies.Included);
			foreach (KeyValuePair<string, List<KeyValuePair<string, BakedDistanceField>>> model in fields.Models)
			{
				DistanceFieldFile.Write(Path.Combine(data, model.Key) + DistanceFieldFile.Extension, null, model.Value);
			}

			log(fields.ToString());

			// 4b'. The volumetric objects' clouds (Data/Volumes/<key>.lelvol): from the editor's cache, made here when missing
			//      and this thread has the GL context; the player reads them on worker threads instead of making them.
			log(VolumeBaker.Collect(project, buildScenes, Path.Combine(data, LELEngine.Volumetrics.VolumeCache.BuildFolder), log).ToString());

			// 4c. Audio: the audio project compiled into bank files (Data/Audio/Banks/<Bank>.bank): the events with their
			//     sounds stored as their import settings say. The player loads the master bank at start; the .lelaudio
			//     asset and the source audio files do not ship.
			LELEngine.Audio.AudioProject audioProject = Audio.AudioBankBuilder.FindProject();
			if (audioProject != null)
			{
				log("Building audio banks from " + audioProject.AssetPath + "...");
				if (!Audio.AudioBankBuilder.BuildAll(audioProject, Path.Combine(data, "Audio", "Banks"), log))
				{
					log("Error: the audio banks could not be built.");
					return false;
				}
			}

			// 5. Project settings (physics, lighting quality), binary like the assets; the defaults when the project has none.
			string projectSettingsTarget = Path.Combine(output, ProjectSettings.FileName);
			if (File.Exists(project.ProjectSettingsFile))
			{
				Documents.ConvertFile(project.ProjectSettingsFile, projectSettingsTarget, DocumentFormat.Binary);
			}
			else
			{
				new ProjectSettings().Save(projectSettingsTarget, DocumentFormat.Binary);
			}

			// 6. Splash screen: in boot.json and Splash.bmp, so the player shows it before it reads any asset.
			SplashScreenConfig splash = BakeSplashScreen(project, output, log);

			// 7. Boot configuration.
			new BootConfig
			{
				DataFolder = "Data",
				GameAssembly = hasScripts ? ProjectGenerator.AssemblyName + ".dll" : null,
				StartScene = settings.StartScene,
				ProjectSettings = ProjectSettings.FileName,
				Title = project.Name,
				RunInBackground = project.RunInBackground,
				SplashScreen = splash
			}.Save(Path.Combine(output, BootConfig.FileName));

			log($"Done: {Path.Combine(output, executable)} ({copied} asset files).");
			return true;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     An image becomes a baked texture unless the UI may draw it: sprites, and the images UI documents name
		///     (url(...) / resource(...) in style sheets and UXML files) stay images (the UI takes rows top down, gamma storage).
		/// </summary>
		/// <summary>
		///     Checks the shader variants the build's materials use with glslang (<see cref="ShaderValidator" />): every
		///     stage, with the instanced programs and for each kind of GPU; false (with the compiler's messages) when one
		///     does not compile or the compiler cannot run.
		/// </summary>
		private static bool ValidateShaderVariants(List<ShaderVariant> variants, Action<string> log)
		{
			ShaderValidator.Result result;
			try
			{
				result = ShaderValidator.Validate(variants);
			}
			catch (Exception e)
			{
				log("Error: the shader compiler (glslang, shaderc) could not run: " + e.Message);
				return false;
			}

			foreach (string error in result.Errors)
			{
				log("Error: shader variant " + error);
			}

			log($"Shader variants: {variants.Count} (the materials' and the always included shaders), {result.Stages} stage sources checked with glslang in {result.Milliseconds} ms"
				+ (result.Warnings > 0 ? $", {result.Warnings} warnings" : "") + (result.Errors.Count > 0 ? $", {result.Errors.Count} failed" : ""));
			return result.Errors.Count == 0;
		}

		private static bool IsBakedTexture(string assetPath, HashSet<string> uiImages)
		{
			if (assetPath == null || uiImages.Contains(Path.GetFileName(assetPath)))
			{
				return false;
			}

			return TextureAssets.GetImporter(assetPath).textureType != TextureImporterType.Sprite;
		}

		/// <summary>File names of the images referenced by the shipped style sheets and UXML documents.</summary>
		private static HashSet<string> CollectUiImageNames(IEnumerable<string> assetPaths)
		{
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var reference = new Regex(@"(?:url|resource)\(\s*['""]?([^'""\)]+)['""]?\s*\)", RegexOptions.IgnoreCase);
			foreach (string assetPath in assetPaths)
			{
				if (!assetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase) && !assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				foreach (Match match in reference.Matches(File.ReadAllText(AssetDatabase.ToAbsolute(assetPath))))
				{
					names.Add(Path.GetFileName(match.Groups[1].Value.Trim()));
				}
			}

			return names;
		}

		/// <summary>
		///     The static batches of a scene the build ships (from the project's Library, or worked out and kept there): the
		///     scene with their objects added, their meshes copied into the build's Data.
		/// </summary>
		private static byte[] BatchScene(ProjectInfo project, byte[] scene, string sceneAssetPath, string data, BatchingStats stats, Action<string> log)
		{
			try
			{
				StaticBatchCache.Result result = StaticBatchCache.Get(project, scene, sceneAssetPath);
				stats.Scenes++;
				stats.Batches += result.Batches.Count;
				stats.FromCache += result.FromCache && result.Batches.Count > 0 ? 1 : 0;
				if (result.StaticRenderers > 0)
				{
					// A scene without static renderers has nothing to say.
					log($"Static batching, {sceneAssetPath}: {result.Summary}" + (result.FromCache ? " (from the Library)" : ""));
				}

				foreach (KeyValuePair<string, string> mesh in result.MeshFiles)
				{
					Copy(mesh.Value, Path.Combine(data, mesh.Key.Replace('/', Path.DirectorySeparatorChar)));
				}

				return StaticBatcher.Append(scene, result.Batches);
			}
			catch (Exception e)
			{
				// The scene ships as it is: its objects draw on their own.
				log($"Warning: {sceneAssetPath} could not be batched ({e.Message}).");
				return scene;
			}
		}

		/// <summary>Counts of the batched scenes, for the build log.</summary>
		private sealed class BatchingStats
		{
			public int Scenes;
			public int Batches;
			public int FromCache;

			public override string ToString()
			{
				return $"Static batching: {Batches} batch{(Batches == 1 ? "" : "es")} in {Scenes} scene{(Scenes == 1 ? "" : "s")}" + (FromCache > 0 ? $" ({FromCache} from the Library)" : "") + ".";
			}
		}

		/// <summary>
		///     A document converted to binary into the build (copied as is when it cannot be); false when it was copied.
		///     <paramref name="process" /> reworks the document first (a scene's static batching).
		/// </summary>
		private static bool ConvertDocument(string source, string target, string relative, Action<string> log, Func<byte[], byte[]> process = null)
		{
			try
			{
				// Scenes and prefabs go without the editor scripts' components (a player has no such class).
				byte[] bytes = EditorScriptComponents.Strip(File.ReadAllBytes(source), out int editorComponents);
				if (editorComponents > 0)
				{
					log($"{relative}: {editorComponents} editor script component{(editorComponents == 1 ? "" : "s")} left out");
				}

				if (process != null)
				{
					bytes = process(bytes);
				}

				string directory = Path.GetDirectoryName(target);
				if (!string.IsNullOrEmpty(directory))
				{
					Directory.CreateDirectory(directory);
				}

				File.WriteAllBytes(target, Documents.IsDocument(bytes) ? Documents.Convert(bytes, DocumentFormat.Binary) : bytes);
				return true;
			}
			catch (Exception e)
			{
				log("Warning: " + relative + " could not be converted to binary (" + e.Message + "); copied as is.");
				Copy(source, target);
				return false;
			}
		}

		/// <summary>Counts and sizes of the baked textures, for the build log.</summary>
		private sealed class TextureBakeStats
		{
			public int Count;
			private int compressed;
			private int cached;
			private long sourceBytes;
			private long bakedBytes;

			public void Add(long source, long baked, bool fromCache, bool isCompressed)
			{
				Count++;
				sourceBytes += source;
				bakedBytes += baked;
				cached += fromCache ? 1 : 0;
				compressed += isCompressed ? 1 : 0;
			}

			public override string ToString()
			{
				return $"Baked {Count} textures to KTX ({compressed} BC7, {Count - compressed} uncompressed; {cached} from the cache): {sourceBytes / 1024.0 / 1024.0:0.0} MB of images, {bakedBytes / 1024.0 / 1024.0:0.0} MB baked.";
			}
		}

		/// <summary>
		///     The project's splash screen for boot.json (null when it is off), its logo composited over the background
		///     color into Splash.bmp. Read from the saved settings file, the one the build ships.
		/// </summary>
		private static SplashScreenConfig BakeSplashScreen(ProjectInfo project, string output, Action<string> log)
		{
			// A logo removed since the last build into this folder must not stay behind.
			string imageFile = Path.Combine(output, SplashScreen.ImageFileName);
			if (File.Exists(imageFile))
			{
				File.Delete(imageFile);
			}

			var projectSettings = new ProjectSettings();
			if (File.Exists(project.ProjectSettingsFile))
			{
				projectSettings = Documents.ReadObject(File.ReadAllBytes(project.ProjectSettingsFile), typeof(ProjectSettings), projectSettings) as ProjectSettings ?? projectSettings;
			}

			SplashScreenSettings splash = projectSettings.SplashScreen ?? new SplashScreenSettings();
			if (!splash.Enabled)
			{
				log("Splash screen: off.");
				return null;
			}

			// 8-bit values: the player's clear color and the background baked around the logo must be the same
			// (a value halfway between two levels rounds differently there, and the logo's rectangle shows).
			Color4 color = splash.BackgroundColor;
			var background = new Vector3(ToByteLevel(color.R), ToByteLevel(color.G), ToByteLevel(color.B));
			var config = new SplashScreenConfig
			{
				Duration = Math.Max(0f, splash.Duration),
				BackgroundColor = new[] { background.X, background.Y, background.Z },
				ImageSize = Math.Clamp(splash.LogoSize, 0f, 1f)
			};

			string logo = string.IsNullOrEmpty(splash.Logo) ? null : AssetDatabase.GetPath(splash.Logo);
			if (logo == null)
			{
				log(string.IsNullOrEmpty(splash.Logo)
					? $"Splash screen: {config.Duration:0.##} s, no logo."
					: "Warning: the splash screen's logo is missing (GUID " + splash.Logo + "); the splash screen shows the background only.");
				return config;
			}

			try
			{
				// The file's values, as the editor's preview shows them (an HDR image tonemapped).
				ImageData image = TextureAssets.Decode(AssetDatabase.ToAbsolute(logo), true).ToLdr();
				SplashScreen.WriteImage(imageFile, image.Width, image.Height, image.Pixels, background);
				config.Image = SplashScreen.ImageFileName;
				log($"Splash screen: {config.Duration:0.##} s, logo {logo} ({image.Width}x{image.Height}).");
			}
			catch (Exception e)
			{
				log("Warning: the splash screen's logo " + logo + " could not be read (" + e.Message + "); the splash screen shows the background only.");
			}

			return config;
		}

		private static float ToByteLevel(float value)
		{
			return MathF.Round(Math.Clamp(value, 0f, 1f) * 255f) / 255f;
		}

		private static void Copy(string source, string target)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.Copy(source, target, true);
		}

		private static bool IsInside(string path, string folder)
		{
			string a = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string b = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
		}

		#endregion
	}
}
