using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace LELEngine
{
	/// <summary>
	///     Records the assets <see cref="AssetDatabase.Load(Type, string, string)" /> returns on this thread while the
	///     scope is open: a scene load opens one, so the scene manager knows which assets the scene uses (prepared for
	///     the GPU before the scene activates, released when no loaded scene uses them any more) and how long each kind
	///     of asset took to load (each load without the assets it loaded in turn: a material without its textures).
	/// </summary>
	public sealed class AssetLoadScope : IDisposable
	{
		#region PublicFields

		/// <summary>The scope open on this thread (null when none is).</summary>
		public static AssetLoadScope Current => current;

		/// <summary>Every asset loaded or looked up in the scope, in load order (nested assets before the asset that loaded them finished).</summary>
		public IReadOnlyList<object> Assets => assets;

		#endregion

		#region PrivateFields

		[ThreadStatic] private static AssetLoadScope current;

		private sealed class Category
		{
			public int Count;
			public long Ticks;
		}

		private readonly AssetLoadScope parent;
		private readonly List<object> assets = new List<object>();
		private readonly HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
		private readonly Dictionary<string, Category> categories = new Dictionary<string, Category>();
		private readonly Stack<long> nested = new Stack<long>();

		#endregion

		#region Constructors

		private AssetLoadScope(AssetLoadScope parent)
		{
			this.parent = parent;
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens a scope on this thread; dispose it to close it.</summary>
		public static AssetLoadScope Begin()
		{
			current = new AssetLoadScope(current);
			return current;
		}

		public void Dispose()
		{
			if (current == this)
			{
				current = parent;
			}
		}

		/// <summary>"textures 12: 770 ms, materials 9: 15 ms, ..." (slowest kind first).</summary>
		public string Summary()
		{
			var ordered = new List<KeyValuePair<string, Category>>(categories);
			ordered.Sort((a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));
			var text = new StringBuilder();
			foreach (KeyValuePair<string, Category> entry in ordered)
			{
				if (text.Length > 0)
				{
					text.Append(", ");
				}

				text.Append(entry.Key).Append(' ').Append(entry.Value.Count).Append(": ")
					.Append(entry.Value.Ticks * 1000 / Stopwatch.Frequency).Append(" ms");
			}

			return text.ToString();
		}

		#endregion

		#region InternalMethods

		/// <summary>Start of one <see cref="AssetDatabase.Load(Type, string, string)" />; pass the result to <see cref="EndLoad" />.</summary>
		internal long BeginLoad()
		{
			nested.Push(0);
			return Stopwatch.GetTimestamp();
		}

		internal void EndLoad(long start, Type requested, object asset)
		{
			long elapsed = Stopwatch.GetTimestamp() - start;
			long inner = nested.Count > 0 ? nested.Pop() : 0;
			if (nested.Count > 0)
			{
				nested.Push(nested.Pop() + elapsed);
			}

			if (asset == null || !seen.Add(asset))
			{
				return;
			}

			// A material loaded by an earlier scene comes from the cache without loading its shader and textures
			// again: they are this scene's assets all the same.
			RecordDependencies(asset);
			assets.Add(asset);
			string kind = KindOf(asset, requested);
			if (!categories.TryGetValue(kind, out Category category))
			{
				category = new Category();
				categories[kind] = category;
			}

			category.Count++;
			category.Ticks += Math.Max(0, elapsed - inner);
		}

		#endregion

		#region PrivateMethods

		private void RecordDependencies(object asset)
		{
			switch (asset)
			{
				case Shaders.Material material:
					Record(material.UsingShader);
					foreach (Shaders.MaterialProperty property in material.Properties)
					{
						if (property is Shaders.TextureProperty texture)
						{
							Record(texture.value);
						}
					}

					break;
				case Sprite sprite:
					Record(sprite.texture);
					break;
			}
		}

		private void Record(object asset)
		{
			if (asset != null && seen.Add(asset))
			{
				RecordDependencies(asset);
				assets.Add(asset);
			}
		}

		private static string KindOf(object asset, Type requested)
		{
			switch (asset)
			{
				case Texture _:
				case Sprite _:
					return "textures";
				case Mesh _:
				case Model _:
					return "meshes";
				case Shaders.Material _:
					return "materials";
				case Shaders.ShaderProgram _:
					return "shaders";
				default:
					return (requested ?? asset.GetType()).Name;
			}
		}

		#endregion
	}
}
