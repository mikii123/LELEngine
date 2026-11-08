using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Rendering;
using LELEngine.Serialization;
using LELEngine.Volumetrics;

namespace LELEngine.Editor
{
	/// <summary>
	///     The clouds of a game build's volumetric objects, shipped as files (Data/Volumes/&lt;key&gt;.lelvol, see
	///     <see cref="VolumeFile" />): the player reads and unpacks them on worker threads while a scene loads and uploads them,
	///     instead of making them on the GPU (New Frontier's storm took ~110-180 ms of the GPU and was missing for its first
	///     frames). Each comes from the editor's cache (Library/VolumeCache: what it made, lit as it drew it), else is made now
	///     when this thread has the GL context (the density only: the lit voxels need the scene's sun, the player lights the
	///     cloud), else is left to the player. Objects of the build's scenes (their prefab instances too) and of the prefabs
	///     in Resources folders.
	/// </summary>
	internal static class VolumeBaker
	{
		#region PublicFields

		public sealed class Result
		{
			public int Cached;
			public int Made;
			public int Missing;
			public long Bytes;

			public override string ToString()
			{
				int total = Cached + Made + Missing;
				return total == 0
					? "Volumes: none."
					: $"Volumes: {total} clouds ({Cached} from the editor's cache, {Made} made now{(Missing > 0 ? $", {Missing} left to the player" : "")}), {Bytes / 1024.0 / 1024.0:0.0} MB.";
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Writes the clouds of the build's scenes (asset paths) into <paramref name="folder" />.</summary>
		public static Result Collect(ProjectInfo project, IEnumerable<string> scenes, string folder, Action<string> log)
		{
			var result = new Result();
			var volumes = new Dictionary<string, VolumetricObjectBase>(StringComparer.Ordinal);
			// Detached copies of the components: they must not start reading their files as they are read.
			VolumeCache.SuppressPrefetch = true;
			try
			{
				foreach (string scene in scenes)
				{
					try
					{
						foreach (DocumentEntry entry in SceneSerializer.ReadDocument(File.ReadAllBytes(AssetDatabase.ToAbsolute(scene))).Entries)
						{
							if (entry.Instance != null)
							{
								AddInstance(entry.Instance, volumes);
								continue;
							}

							foreach (DocumentComponent component in entry.Object.Components)
							{
								Add(component.Data, volumes);
							}
						}
					}
					catch (Exception e)
					{
						log("Warning: the volumes of " + scene + " could not be listed (" + e.Message + ").");
					}
				}

				foreach (string path in AssetDatabase.GetAllAssetPaths(Prefab.Extension))
				{
					if (!Resources.IsInResources(path))
					{
						continue;
					}

					ResolvedPrefab prefab = PrefabCache.Get(path);
					if (prefab?.Root == null)
					{
						continue;
					}

					foreach (PrefabObject obj in prefab.Objects)
					{
						foreach (PrefabComponent component in obj.Components)
						{
							Add(component.Data, volumes);
						}
					}
				}
			}
			finally
			{
				VolumeCache.SuppressPrefetch = false;
			}

			foreach (KeyValuePair<string, VolumetricObjectBase> volume in volumes)
			{
				string name = VolumeFile.FileName(volume.Key);
				string cached = Path.Combine(project.VolumeCachePath, name);
				string target = Path.Combine(folder, name);
				try
				{
					if (VolumeFile.ReadHeader(cached, out string density, out _, out _) && density == volume.Key)
					{
						result.Cached++;
					}
					else if (GraphicsThread.HasContext)
					{
						VolumeFile file = VolumetricData.MakeDensity(volume.Value);
						file.Write(cached);
						result.Made++;
					}
					else
					{
						result.Missing++;
						continue;
					}

					Directory.CreateDirectory(folder);
					File.Copy(cached, target, true);
					result.Bytes += new FileInfo(target).Length;
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					log("Warning: the cloud " + name + " could not be written (" + e.Message + "); the player makes it.");
					result.Missing++;
				}
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		// A component's generic data: a volumetric object's settings become a detached copy, keyed by its density.
		private static void Add(SerializedNode data, Dictionary<string, VolumetricObjectBase> volumes)
		{
			Type type = data?.TypeName != null ? TypeRegistry.Resolve(data.TypeName) : null;
			if (type == null || !typeof(VolumetricObjectBase).IsAssignableFrom(type))
			{
				return;
			}

			if (DocumentValues.FromNode(data, typeof(VolumetricObjectBase), new SerializationContext()) is VolumetricObjectBase volume)
			{
				volumes[volume.DensityKey()] = volume;
			}
		}

		// The volumetric objects of a prefab instance (its resolved prefab with the instance's changes).
		private static void AddInstance(PrefabInstanceRecord record, Dictionary<string, VolumetricObjectBase> volumes)
		{
			string path = PrefabCache.PathOf(record.Prefab);
			ResolvedPrefab prefab = path != null ? PrefabCache.Get(path) : null;
			if (prefab?.Root == null)
			{
				return;
			}

			var removedComponents = new HashSet<ulong>(record.RemovedComponents);
			Dictionary<ulong, List<PropertyModification>> modifications = PrefabResolver.GroupByTarget(record.Modifications);
			foreach (PrefabObject obj in prefab.Objects)
			{
				foreach (PrefabComponent component in obj.Components)
				{
					if (removedComponents.Contains(component.Id))
					{
						continue;
					}

					SerializedNode data = component.Data;
					if (modifications.TryGetValue(component.Id, out List<PropertyModification> changes))
					{
						data = GenericValues.CloneNode(data);
						foreach (PropertyModification change in changes)
						{
							GenericValues.Apply(data, change);
						}
					}

					Add(data, volumes);
				}
			}

			foreach (DocumentComponent added in record.AddedComponents)
			{
				Add(added.Data, volumes);
			}
		}

		#endregion
	}
}
