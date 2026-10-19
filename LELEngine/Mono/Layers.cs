using System;

namespace LELEngine
{
	/// <summary>
	///     A layer selection as a bit mask (bit n = layer n), and the names of the layers: built in for 0..7 (Default,
	///     TransparentFX, Ignore Raycast, Water, UI), the project's own for 8..31 (<see cref="LayerSettings" />, Project
	///     Settings > Layers). A GameObject is on one layer (<see cref="GameObject.layer" />); masks select layers for
	///     collisions and queries (2D physics) and for rendering (<see cref="Camera.CullingMask" />).
	/// </summary>
	[Serializable]
	public struct LayerMask
	{
		#region PublicFields

		public const int LayerCount = 32;

		/// <summary>Layers from this one on are named by the project; the ones below are built in.</summary>
		public const int FirstUserLayer = 8;

		public int value;

		/// <summary>Every layer.</summary>
		public static LayerMask Everything => new LayerMask { value = -1 };

		public static LayerMask Nothing => new LayerMask { value = 0 };

		#endregion

		#region PrivateFields

		private static readonly string[] builtInNames = { "Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI", "", "" };

		#endregion

		#region PublicMethods

		public static implicit operator int(LayerMask mask)
		{
			return mask.value;
		}

		public static implicit operator LayerMask(int value)
		{
			return new LayerMask { value = value };
		}

		/// <summary>The mask with the named layers set (unknown names are ignored).</summary>
		public static int GetMask(params string[] layerNames)
		{
			int mask = 0;
			foreach (string name in layerNames)
			{
				int layer = NameToLayer(name);
				if (layer >= 0)
				{
					mask |= 1 << layer;
				}
			}

			return mask;
		}

		/// <summary>The layer's name ("" for an unnamed one).</summary>
		public static string LayerToName(int layer)
		{
			if (layer < 0 || layer >= LayerCount)
			{
				return "";
			}

			return layer < FirstUserLayer ? builtInNames[layer] : ProjectSettings.Current.Layers.GetName(layer);
		}

		/// <summary>The layer with this name, or -1.</summary>
		public static int NameToLayer(string layerName)
		{
			for (int i = 0; i < LayerCount; i++)
			{
				string name = LayerToName(i);
				if (name.Length > 0 && string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>Names a user layer (8..31) of the live project settings; built-in names do not change.</summary>
		public static void SetLayerName(int layer, string name)
		{
			if (layer >= FirstUserLayer && layer < LayerCount)
			{
				ProjectSettings.Current.Layers.SetName(layer, name);
			}
		}

		/// <summary>Display name of a layer ("Layer 9" for unnamed ones).</summary>
		public static string DisplayName(int layer)
		{
			string name = LayerToName(layer);
			return name.Length > 0 ? name : "Layer " + layer;
		}

		public bool Contains(int layer)
		{
			return (value & (1 << layer)) != 0;
		}

		public override string ToString()
		{
			return "LayerMask(" + value + ")";
		}

		#endregion
	}

	/// <summary>The names of the user layers 8..31 (Project Settings > Layers). Part of <see cref="ProjectSettings" />.</summary>
	[Serializable]
	public sealed class LayerSettings
	{
		#region PublicFields

		/// <summary>Index = layer; the entries of the built-in layers (below 8) are not used.</summary>
		public string[] Names = NewNames();

		#endregion

		#region PublicMethods

		public string GetName(int layer)
		{
			EnsureNames();
			return layer >= 0 && layer < Names.Length ? Names[layer] ?? "" : "";
		}

		public void SetName(int layer, string name)
		{
			EnsureNames();
			if (layer >= LayerMask.FirstUserLayer && layer < Names.Length)
			{
				Names[layer] = name ?? "";
			}
		}

		#endregion

		#region PrivateMethods

		private static string[] NewNames()
		{
			var names = new string[LayerMask.LayerCount];
			Array.Fill(names, "");
			return names;
		}

		// A document from another version may hold fewer (or no) names.
		private void EnsureNames()
		{
			if (Names != null && Names.Length == LayerMask.LayerCount)
			{
				return;
			}

			string[] names = NewNames();
			if (Names != null)
			{
				Array.Copy(Names, names, Math.Min(Names.Length, names.Length));
			}

			Names = names;
		}

		#endregion
	}
}
