using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>When a visual effect simulates.</summary>
	public enum VfxCullingMode
	{
		/// <summary>Only while some view saw the effect's bounds in the last frames (frozen off-screen).</summary>
		SimulateWhenVisible,

		/// <summary>Every frame, seen or not.</summary>
		AlwaysSimulate
	}

	/// <summary>
	///     A visual effect graph asset (.vfx document: readable JSON in the editor, binary in builds), the way
	///     Unity's VFX Graph assets work: contexts (spawn, initialize, update, output) chained by flow edges form
	///     particle systems, blocks inside the contexts change the particles, operators and properties compute the
	///     block inputs. <see cref="VfxCompiler" /> turns it into compute and draw programs; <see cref="VisualEffect" />
	///     components play it. One graph may hold several systems (a parent and its GPU-event children, or unrelated ones).
	/// </summary>
	public sealed class VfxGraph : IAsset, ISerializationCallbackReceiver
	{
		#region PublicFields

		public const string Extension = ".vfx";

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;
		public string name;

		public List<VfxProperty> Properties = new List<VfxProperty>();
		public List<VfxNodeData> Nodes = new List<VfxNodeData>();
		public List<VfxEdge> Edges = new List<VfxEdge>();
		public List<VfxFlowEdge> Flow = new List<VfxFlowEdge>();

		/// <summary>Editor view (saved with the asset so the graph opens where it was left).</summary>
		public Vector2 ViewOffset;

		public float ViewZoom = 1f;
		public int FormatVersion = 1;

		/// <summary>
		///     When the effects playing the graph simulate (Unity's culling flags): only while some view sees their
		///     bounds (the default: off-screen effects cost nothing and resume where they stopped), or always.
		/// </summary>
		public VfxCullingMode CullingMode = VfxCullingMode.SimulateWhenVisible;

		/// <summary>
		///     Bumped by every change (<see cref="MarkChanged" />): the runtime recompiles the programs and the
		///     instances rebuild their buffers when the version they run is older.
		/// </summary>
		[NonSerialized] public int Version = 1;

		/// <summary>Raised after <see cref="MarkChanged" /> (editor windows refresh, instances recompile).</summary>
		public event Action Changed;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, VfxGraph> cache = new Dictionary<string, VfxGraph>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- files

		/// <summary>Loads (or returns the cached) graph at a root-relative path. Unreadable files give an empty graph.</summary>
		public static VfxGraph Load(string assetPath)
		{
			lock (cache)
			{
				VfxGraph cached;
				if (cache.TryGetValue(assetPath, out cached))
				{
					return cached;
				}
			}

			VfxGraph graph = null;
			try
			{
				byte[] data = File.ReadAllBytes(AssetDatabase.ToAbsolute(assetPath));
				if (Documents.IsDocument(data))
				{
					graph = Documents.ReadObject(data, typeof(VfxGraph), null, new SerializationContext { Location = assetPath }) as VfxGraph;
				}
				else
				{
					Debug.LogError($"[VFX] {assetPath} is not a graph document");
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[VFX] Cannot load {assetPath}: {e.Message}");
			}

			graph = graph ?? new VfxGraph();
			graph.AssetPath = assetPath.Replace('\\', '/');
			graph.name = Path.GetFileNameWithoutExtension(assetPath);
			lock (cache)
			{
				cache[assetPath] = graph;
			}

			return graph;
		}

		/// <summary>The cached graph of an asset path when it was loaded (null otherwise).</summary>
		public static VfxGraph FindLoaded(string assetPath)
		{
			lock (cache)
			{
				VfxGraph graph;
				return assetPath != null && cache.TryGetValue(assetPath, out graph) ? graph : null;
			}
		}

		/// <summary>Writes the graph to its asset file (or a new one) in <see cref="Documents.FileFormat" />.</summary>
		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The graph has no asset path");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			Documents.WriteObjectFile(AssetDatabase.ToAbsolute(AssetPath), this);
			lock (cache)
			{
				cache[AssetPath] = this;
			}
		}

		/// <summary>The graph as a document (undo snapshots, copies).</summary>
		public byte[] ToBytes(DocumentFormat format = DocumentFormat.Binary)
		{
			return Documents.WriteObject(this, format, new SerializationContext { Location = name ?? "VfxGraph" });
		}

		/// <summary>
		///     Replaces the content from a document written by <see cref="ToBytes" /> and marks the graph changed
		///     (<paramref name="recompile" /> false: only the layout differs, the players keep their programs).
		/// </summary>
		public void ApplyBytes(byte[] data, bool recompile = true)
		{
			Properties.Clear();
			Nodes.Clear();
			Edges.Clear();
			Flow.Clear();
			Documents.ReadObject(data, typeof(VfxGraph), this, new SerializationContext { Location = name ?? "VfxGraph" });
			if (recompile)
			{
				MarkChanged();
			}
			else
			{
				Changed?.Invoke();
			}
		}

		/// <summary>Independent copy (same asset path, own node objects).</summary>
		public VfxGraph Clone()
		{
			var copy = Documents.ReadObject(ToBytes(), typeof(VfxGraph), null) as VfxGraph ?? new VfxGraph();
			copy.AssetPath = AssetPath;
			copy.name = name;
			return copy;
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
			}
		}

		/// <summary>A file or folder moved: the loaded graphs keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (cache)
			{
				AssetDatabase.RekeyMoved(cache, fromPath, toPath, (graph, path) => graph.AssetPath = path);
			}
		}

		/// <summary>Drops a cached graph (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void Forget(string assetPath)
		{
			lock (cache)
			{
				cache.Remove(assetPath);
			}
		}

		// ---------------------------------------------------------------- editing

		/// <summary>Call after any edit: bumps <see cref="Version" /> and raises <see cref="Changed" />.</summary>
		public void MarkChanged()
		{
			Version++;
			Changed?.Invoke();
		}

		public static ulong NewId()
		{
			ulong id;
			do
			{
				id = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
			}
			while (id == 0);

			return id;
		}

		public VfxNodeData FindNode(ulong id)
		{
			foreach (VfxNodeData node in Nodes)
			{
				if (node.Id == id)
				{
					return node;
				}
			}

			return null;
		}

		/// <summary>Creates a node of a definition at a position, with the definition's default settings and inline values.</summary>
		public VfxNodeData AddNode(string type, Vector2 position)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(type);
			if (definition == null)
			{
				throw new ArgumentException("Unknown node type '" + type + "'");
			}

			var node = new VfxNodeData { Id = NewId(), Type = type, Position = position };
			foreach (VfxSettingDefinition setting in definition.Settings)
			{
				node.Settings.Add(new VfxSetting(setting.Name, setting.Default));
			}

			foreach (VfxSlotDefinition slot in definition.GetInputs(node))
			{
				node.Inputs.Add(new VfxSlotValue(slot.Name, slot.CreateDefault()));
			}

			Nodes.Add(node);
			return node;
		}

		/// <summary>Adds a block node into a context (at the end, or at <paramref name="index" />).</summary>
		public VfxNodeData AddBlock(VfxNodeData context, string type, int index = -1)
		{
			VfxNodeData block = AddNode(type, Vector2.Zero);
			block.Parent = context.Id;
			if (index < 0 || index >= context.Blocks.Count)
			{
				context.Blocks.Add(block.Id);
			}
			else
			{
				context.Blocks.Insert(index, block.Id);
			}

			return block;
		}

		/// <summary>Removes a node with its edges; a context takes its blocks along.</summary>
		public void RemoveNode(VfxNodeData node)
		{
			if (node == null)
			{
				return;
			}

			foreach (ulong blockId in new List<ulong>(node.Blocks))
			{
				RemoveNode(FindNode(blockId));
			}

			if (node.Parent != 0)
			{
				FindNode(node.Parent)?.Blocks.Remove(node.Id);
			}

			Edges.RemoveAll(edge => edge.From == node.Id || edge.To == node.Id);
			Flow.RemoveAll(edge => edge.From == node.Id || edge.To == node.Id);
			Nodes.Remove(node);
		}

		/// <summary>Connects an output slot to an input slot, replacing the input's previous connection.</summary>
		public VfxEdge Connect(VfxNodeData from, string fromSlot, VfxNodeData to, string toSlot)
		{
			Edges.RemoveAll(edge => edge.To == to.Id && edge.ToSlot == toSlot);
			var edge = new VfxEdge(from.Id, fromSlot, to.Id, toSlot);
			Edges.Add(edge);
			return edge;
		}

		public void Disconnect(VfxNodeData to, string toSlot)
		{
			Edges.RemoveAll(edge => edge.To == to.Id && edge.ToSlot == toSlot);
		}

		/// <summary>The edge feeding an input slot, or null.</summary>
		public VfxEdge FindInputEdge(ulong nodeId, string slot)
		{
			foreach (VfxEdge edge in Edges)
			{
				if (edge.To == nodeId && edge.ToSlot == slot)
				{
					return edge;
				}
			}

			return null;
		}

		/// <summary>Connects two context ports (the target port keeps at most one source for ports that take one).</summary>
		public VfxFlowEdge ConnectFlow(VfxNodeData from, int fromPort, VfxNodeData to, int toPort)
		{
			Flow.RemoveAll(edge => edge.From == from.Id && edge.FromPort == fromPort && edge.To == to.Id && edge.ToPort == toPort);
			var edge = new VfxFlowEdge(from.Id, fromPort, to.Id, toPort);
			Flow.Add(edge);
			return edge;
		}

		public void DisconnectFlow(VfxFlowEdge edge)
		{
			Flow.Remove(edge);
		}

		/// <summary>The blocks of a context in order (missing ids skipped).</summary>
		public List<VfxNodeData> GetBlocks(VfxNodeData context)
		{
			var result = new List<VfxNodeData>();
			foreach (ulong id in context.Blocks)
			{
				VfxNodeData block = FindNode(id);
				if (block != null)
				{
					result.Add(block);
				}
			}

			return result;
		}

		/// <summary>The property of a name, or null.</summary>
		public VfxProperty FindProperty(string propertyName)
		{
			foreach (VfxProperty property in Properties)
			{
				if (property.Name == propertyName)
				{
					return property;
				}
			}

			return null;
		}

		public VfxProperty AddProperty(string propertyName, VfxValue value, bool exposed = true)
		{
			string unique = propertyName;
			int suffix = 1;
			while (FindProperty(unique) != null)
			{
				unique = propertyName + " " + ++suffix;
			}

			var property = new VfxProperty(unique, value, exposed);
			Properties.Add(property);
			return property;
		}

		/// <summary>Renames a property and the Property nodes that read it.</summary>
		public void RenameProperty(VfxProperty property, string newName)
		{
			if (property == null || string.IsNullOrWhiteSpace(newName) || newName == property.Name)
			{
				return;
			}

			string old = property.Name;
			property.Name = newName;
			foreach (VfxNodeData node in Nodes)
			{
				if (node.Type == VfxNodeLibrary.PropertyNodeType && node.GetSetting("Property") == old)
				{
					node.SetSetting("Property", newName);
				}
			}
		}

		public void RemoveProperty(VfxProperty property)
		{
			Properties.Remove(property);
		}

		/// <summary>Every context node (any type of context).</summary>
		public List<VfxNodeData> GetContexts()
		{
			var result = new List<VfxNodeData>();
			foreach (VfxNodeData node in Nodes)
			{
				VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
				if (definition != null && definition.Kind == VfxNodeKind.Context)
				{
					result.Add(node);
				}
			}

			return result;
		}

		/// <summary>Flow edges arriving at a context port.</summary>
		public List<VfxFlowEdge> GetFlowInputs(ulong nodeId, int port)
		{
			var result = new List<VfxFlowEdge>();
			foreach (VfxFlowEdge edge in Flow)
			{
				if (edge.To == nodeId && edge.ToPort == port)
				{
					result.Add(edge);
				}
			}

			return result;
		}

		/// <summary>Flow edges leaving a context port.</summary>
		public List<VfxFlowEdge> GetFlowOutputs(ulong nodeId, int port)
		{
			var result = new List<VfxFlowEdge>();
			foreach (VfxFlowEdge edge in Flow)
			{
				if (edge.From == nodeId && edge.FromPort == port)
				{
					result.Add(edge);
				}
			}

			return result;
		}

		/// <summary>Removes edges whose nodes are gone and blocks without their context (after a load of an edited file).</summary>
		public void Repair()
		{
			var ids = new HashSet<ulong>();
			foreach (VfxNodeData node in Nodes)
			{
				ids.Add(node.Id);
			}

			Edges.RemoveAll(edge => !ids.Contains(edge.From) || !ids.Contains(edge.To));
			Flow.RemoveAll(edge => !ids.Contains(edge.From) || !ids.Contains(edge.To));
			foreach (VfxNodeData node in Nodes)
			{
				node.Blocks.RemoveAll(id => !ids.Contains(id));
				if (node.Parent != 0 && !ids.Contains(node.Parent))
				{
					node.Parent = 0;
				}

				// Inputs the definition declares that the file lacks (a node added inputs since the file was written).
				VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
				if (definition == null)
				{
					continue;
				}

				foreach (VfxSettingDefinition setting in definition.Settings)
				{
					if (node.GetSetting(setting.Name) == null)
					{
						node.Settings.Add(new VfxSetting(setting.Name, setting.Default));
					}
				}

				foreach (VfxSlotDefinition slot in definition.GetInputs(node))
				{
					if (node.FindInput(slot.Name) == null)
					{
						node.Inputs.Add(new VfxSlotValue(slot.Name, slot.CreateDefault()));
					}
				}
			}
		}

		public void OnBeforeSerialize()
		{
			Nodes.RemoveAll(node => node == null);
			Edges.RemoveAll(edge => edge == null);
			Flow.RemoveAll(edge => edge == null);
			Properties.RemoveAll(property => property == null || string.IsNullOrEmpty(property.Name));
		}

		public void OnAfterDeserialize()
		{
			Nodes.RemoveAll(node => node == null || string.IsNullOrEmpty(node.Type));
			Edges.RemoveAll(edge => edge == null);
			Flow.RemoveAll(edge => edge == null);
			Properties.RemoveAll(property => property == null || string.IsNullOrEmpty(property.Name));
			foreach (VfxProperty property in Properties)
			{
				property.Value = property.Value ?? new VfxValue(VfxValueType.Float);
			}

			if (ViewZoom <= 0.01f)
			{
				ViewZoom = 1f;
			}

			Repair();
		}

		public override string ToString()
		{
			return "VfxGraph(" + (AssetPath ?? name) + ")";
		}

		#endregion
	}
}
