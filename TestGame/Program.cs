using System;
using System.Reflection;
using LELCS;
using LELCS.Model;
using LELEngine;
using TestGame.Scenes;

namespace TestGame
{
	internal class Program
	{
		#region PrivateMethods

		/// <summary>
		///     Usage: TestGame [gi=lumen|vct|0] [voxels=0|1] [sdfview=0|1] [cacheview=0|1] [probeview=0|1] [emitters=0|1] [animate=0|1]
		///            [stats=0|1] [autocam=0|1] [dump=N] [dumpdir=path] [jitter=0|1] [resolve=0|1] [static=0|1]
		///            [bounce=0|1] [trace=voxel|sdf] [voxres=64|128|256] [sdfres=64|128|256]
		///            Lumen quality: [spacing=N px] [adaptive=0|1] [radrays=N] [radblend=0..1] [pblend=0..1] [tblend=0..1]
		///            [importance=0|1] [pfilter=0|1] [pradius=N probes] [texels=N per meter] [ajitter=0..1] [djitter=0|1]
		///            [rcache=0|1] [rcradio=0|1] [rcnear=meters] [rchistory=frames] [rcprobes=N per frame] [rcres=8|16] [rctrace=16|32]
	///            [dumpafter=seconds]
		///     Loads the GI test room with the FPS controller.
		/// </summary>
		private static void Main(string[] args)
		{
			Game.CreateWindow(1280, 720, "LELEngine");

			string giValue = GetValue(args, "gi");
			GITestScene.Options options = new GITestScene.Options
			{
				GI = !(giValue == "0" || string.Equals(giValue, "off", StringComparison.OrdinalIgnoreCase)),
				Lumen = !string.Equals(giValue, "vct", StringComparison.OrdinalIgnoreCase),
				VoxelView = GetBool(args, "voxels", false),
				SdfView = GetBool(args, "sdfview", false),
				SurfaceCacheView = GetBool(args, "cacheview", false),
				ProbeView = GetBool(args, "probeview", false),
				Emitters = GetBool(args, "emitters", true),
				Animate = GetBool(args, "animate", true),
				Stats = GetBool(args, "stats", false),
				AutoCamera = GetBool(args, "autocam", false),
				MouseLook = GetBool(args, "mouselook", true),
				Profiler = GetBool(args, "profiler", true),
				Fullscreen = GetBool(args, "fullscreen", false),
				DumpFrames = GetInt(args, "dump", 0),
				DumpDirectory = GetValue(args, "dumpdir") ?? "framedump",
				ProbeJitter = GetBool(args, "jitter", true),
				Resolve = GetBool(args, "resolve", true),
				StaticCache = GetBool(args, "static", true),
				Bounce = GetBool(args, "bounce", true),
				SdfTrace = string.Equals(GetValue(args, "trace"), "sdf", StringComparison.OrdinalIgnoreCase),
				VoxelResolution = GetInt(args, "voxres", 128),
				SdfResolution = GetInt(args, "sdfres", 128),
				ProbeAnchorJitter = GetFloat(args, "ajitter", 0f),
				ProbeDirectionJitter = GetBool(args, "djitter", true),
				ProbeSpacing = GetInt(args, "spacing", 16),
				ProbeAdaptivePlacement = GetBool(args, "adaptive", true),
				RadiosityRays = GetInt(args, "radrays", 4),
				RadiosityBlend = GetFloat(args, "radblend", 0.9f),
				RadiosityMaxHistorySamples = GetFloat(args, "radmax", 64f),
				ProbeHistoryWeight = GetFloat(args, "pblend", 0.5f),
				ProbeMaxHistorySamples = GetFloat(args, "pmax", 32f),
				ProbeChangeThreshold = GetFloat(args, "pchange", 0.5f),
				ProbeTemporalBlend = GetFloat(args, "tblend", 0.9f),
				ProbeImportanceSampling = GetBool(args, "importance", true),
				ProbeSpatialFilter = GetBool(args, "pfilter", true),
				ProbeFilterRadius = GetInt(args, "pradius", 1),
				SurfaceCacheTexelsPerMeter = GetInt(args, "texels", 6),
				RadianceCache = GetBool(args, "rcache", true),
				SdfObjectIdLookup = GetBool(args, "objectids", true),
				RadianceCacheFarShortcut = GetBool(args, "rcfar", true),
				ProbeAdaptiveFlaggedRefine = GetBool(args, "pflag", false),
				RadiosityIdleDivisor = GetInt(args, "radidle", 4),
				RadianceCacheForRadiosity = GetBool(args, "rcradio", true),
				RadianceCacheNearDistance = GetFloat(args, "rcnear", 2f),
				RadianceCacheHistoryFrames = GetFloat(args, "rchistory", 20f),
				RadianceCacheMaxHistorySamples = GetFloat(args, "rcmax", 32f),
				RadianceCacheChangeThreshold = GetFloat(args, "rcchange", 0.25f),
				RadianceCacheProbesPerFrame = GetInt(args, "rcprobes", 160),
				RadianceCacheProbeResolution = GetInt(args, "rcres", 16),
				RadianceCacheTraceResolution = GetInt(args, "rctrace", 32),
				DumpAfterSeconds = GetFloat(args, "dumpafter", 5f),
				DumpAfterFrames = GetInt(args, "dumpframe", 0)
			};
			GITestScene.Load(Game.Mono.LoadEmptyScene(), options);

			Game.Mono.InitializeECSScope(Assembly.GetExecutingAssembly());
			ECSManager manager = Game.Mono.ECSManager;
			ECSEntity ecsEntity = manager.CreateEntity();
			manager.SetComponent(ecsEntity, new FrameRateCounterComponent());

			Game.Mono.Run();
			// Main function is frozen until game window closes
		}

		private static int GetInt(string[] args, string name, int defaultValue)
		{
			string value = GetValue(args, name);
			int parsed;
			return value != null && int.TryParse(value, out parsed) ? parsed : defaultValue;
		}

		private static float GetFloat(string[] args, string name, float defaultValue)
		{
			string value = GetValue(args, name);
			float parsed;
			return value != null && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed) ? parsed : defaultValue;
		}

		private static string GetValue(string[] args, string name)
		{
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq > 0 && string.Equals(arg.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
				{
					return arg.Substring(eq + 1).Trim();
				}
			}

			return null;
		}

		// Parses "name=0" / "name=1" style switches.
		private static bool GetBool(string[] args, string name, bool defaultValue)
		{
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq <= 0 || !string.Equals(arg.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string value = arg.Substring(eq + 1).Trim();
				return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
			}

			return defaultValue;
		}

		#endregion
	}
}
