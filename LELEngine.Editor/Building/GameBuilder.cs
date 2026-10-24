using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using LELEngine.Rendering;
using LELEngine.Rendering.DistanceField;
using LELEngine.Serialization;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	internal sealed class BuildSettings
	{
		public string OutputFolder;
		public bool Release = true;

		/// <summary>With the .NET runtime (<see cref="PlayerFiles.SelfContainedFolder" />), else framework-dependent.</summary>
		public bool SelfContained;

		public string StartScene;
		public List<string> Scenes = new List<string>();
	}

	/// <summary>
	///     Produces a runnable game folder:
	///     <code>
	///     Output/
	///       &lt;Name&gt;.exe + LELEngine.Player.dll, engine and OpenTK assemblies, native GLFW (runtimes/), and the
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
			List<ShaderVariant> variants = dependencies.CollectShaderVariants();
			if (!ValidateShaderVariants(variants, log))
			{
				log("Error: shader variants do not compile; nothing was written.");
				return false;
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
					if (ConvertDocument(file, target, relative, log))
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
			if (textures.Count > 0)
			{
				log(textures.ToString());
			}

			// The GUID index of the shipped assets: the player reads one file instead of every meta file.
			AssetDatabase.WriteIndex(Path.Combine(data, AssetDatabase.IndexFileName), path => dependencies.Included.Contains(path));

			// The shader variants for the player to compile while it loads (Data/ShaderVariants.txt).
			ShaderVariantList.Write(Path.Combine(data, ShaderVariantList.FileName), variants);

			// 4b. The GI's mesh distance fields at the scales the scenes draw the meshes ("model.ext.sdf" next to each
			//     model): baked here when this thread has the GL context (batch mode), by the editor before the build
			//     otherwise; cached in Library.
			DistanceFieldBaker.Result fields = DistanceFieldBaker.Collect(project, buildScenes, log);
			foreach (KeyValuePair<string, List<KeyValuePair<string, BakedDistanceField>>> model in fields.Models)
			{
				DistanceFieldFile.Write(Path.Combine(data, model.Key) + DistanceFieldFile.Extension, null, model.Value);
			}

			log(fields.ToString());

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

			log($"Shader variants: {variants.Count} used by the materials, {result.Stages} stage sources checked with glslang in {result.Milliseconds} ms"
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

		/// <summary>A document converted to binary into the build (copied as is when it cannot be); false when it was copied.</summary>
		private static bool ConvertDocument(string source, string target, string relative, Action<string> log)
		{
			try
			{
				Documents.ConvertFile(source, target, DocumentFormat.Binary);
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
