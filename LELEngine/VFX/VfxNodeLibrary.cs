using System;
using System.Collections.Generic;
using System.Globalization;

namespace LELEngine.VFX
{
	public enum VfxNodeKind
	{
		/// <summary>A stage of a particle system (spawn, initialize, update, output) or an event source.</summary>
		Context,

		/// <summary>A step inside a context that reads and writes particle attributes.</summary>
		Block,

		/// <summary>A pure function from its inputs to its outputs (math, sampling, constants, properties).</summary>
		Operator
	}

	public enum VfxContextKind
	{
		None,

		/// <summary>An event source: OnPlay, OnStop or a named event raised from scripts; connects to a spawn context.</summary>
		Event,

		/// <summary>Runs on the CPU: decides how many particles to create per frame.</summary>
		Spawn,

		/// <summary>Runs on the GPU once per spawned particle; defines a system and its capacity.</summary>
		Initialize,

		/// <summary>Runs on the GPU once per alive particle per frame.</summary>
		Update,

		/// <summary>Draws the particles (one draw per output context).</summary>
		Output
	}

	public enum VfxOutputKind
	{
		None,
		Quad,
		Mesh,
		Point,
		Line,
		Strip
	}

	/// <summary>Where a block may be placed.</summary>
	[Flags]
	public enum VfxContextMask
	{
		None = 0,
		Spawn = 1,
		Initialize = 2,
		Update = 4,
		Output = 8,
		Particle = Initialize | Update | Output,
		InitializeUpdate = Initialize | Update
	}

	public enum VfxSettingKind
	{
		Enum,
		Bool,
		Int,
		Float,
		Text,

		/// <summary>Multi-line GLSL (custom code blocks).</summary>
		Code,

		/// <summary>A standard attribute name.</summary>
		Attribute,

		/// <summary>A graph property name.</summary>
		Property
	}

	/// <summary>An input or output slot of a node definition.</summary>
	public sealed class VfxSlotDefinition
	{
		#region PublicFields

		public string Name;
		public string DisplayName;
		public VfxValueType Type;
		public VfxValue Default;
		public string Tooltip;
		public bool HasRange;
		public float Min;
		public float Max = 1f;

		/// <summary>Colors with intensity above 1 (emission).</summary>
		public bool Hdr;

		#endregion

		#region Constructors

		public VfxSlotDefinition(string name, VfxValueType type, VfxValue defaultValue = null, string tooltip = null, string displayName = null)
		{
			Name = name;
			DisplayName = displayName ?? name;
			Type = type;
			Default = defaultValue ?? VfxValue.Default(type);
			Tooltip = tooltip;
		}

		#endregion

		#region PublicMethods

		public VfxSlotDefinition Range(float min, float max)
		{
			HasRange = true;
			Min = min;
			Max = max;
			return this;
		}

		public VfxSlotDefinition WithHdr()
		{
			Hdr = true;
			return this;
		}

		/// <summary>A fresh copy of the default value (each node owns its inline values).</summary>
		public VfxValue CreateDefault()
		{
			VfxValue value = Default?.Clone() ?? VfxValue.Default(Type);
			value.Type = Type;
			return value;
		}

		#endregion
	}

	/// <summary>A node setting: a choice, switch, number or text that changes how the node behaves (and sometimes which slots it has).</summary>
	public sealed class VfxSettingDefinition
	{
		#region PublicFields

		public string Name;
		public string DisplayName;
		public VfxSettingKind Kind;
		public string[] Options;
		public string Default;
		public string Tooltip;

		/// <summary>The slots of the node depend on this setting (the editor rebuilds them when it changes).</summary>
		public bool AffectsSlots;

		#endregion

		#region Constructors

		public VfxSettingDefinition(string name, VfxSettingKind kind, string defaultValue, string[] options = null, string tooltip = null, bool affectsSlots = false, string displayName = null)
		{
			Name = name;
			DisplayName = displayName ?? name;
			Kind = kind;
			Default = defaultValue;
			Options = options;
			Tooltip = tooltip;
			AffectsSlots = affectsSlots;
		}

		#endregion

		#region PublicMethods

		public static VfxSettingDefinition Enum(string name, string defaultValue, params string[] options)
		{
			return new VfxSettingDefinition(name, VfxSettingKind.Enum, defaultValue, options, null, true);
		}

		public static VfxSettingDefinition Bool(string name, bool defaultValue, string tooltip = null, bool affectsSlots = false)
		{
			return new VfxSettingDefinition(name, VfxSettingKind.Bool, defaultValue ? "1" : "0", null, tooltip, affectsSlots);
		}

		public static VfxSettingDefinition Int(string name, int defaultValue, string tooltip = null)
		{
			return new VfxSettingDefinition(name, VfxSettingKind.Int, defaultValue.ToString(CultureInfo.InvariantCulture), null, tooltip);
		}

		public static VfxSettingDefinition Float(string name, float defaultValue, string tooltip = null)
		{
			return new VfxSettingDefinition(name, VfxSettingKind.Float, defaultValue.ToString(CultureInfo.InvariantCulture), null, tooltip);
		}

		public static VfxSettingDefinition Text(string name, string defaultValue, string tooltip = null, bool affectsSlots = false)
		{
			return new VfxSettingDefinition(name, VfxSettingKind.Text, defaultValue, null, tooltip, affectsSlots);
		}

		public VfxSettingDefinition WithTooltip(string tooltip)
		{
			Tooltip = tooltip;
			return this;
		}

		#endregion
	}

	/// <summary>Inputs of an operator or block already resolved to expressions (see <see cref="VfxOperatorBuild" />).</summary>
	public delegate VfxExpression[] VfxOperatorEmitter(VfxOperatorBuild build);

	/// <summary>Emits the GLSL statements of a block into its kernel (see <see cref="VfxBlockBuild" />).</summary>
	public delegate void VfxBlockEmitter(VfxBlockBuild build);

	/// <summary>
	///     What a node type is and does: its slots (which may depend on its settings), its settings, where it may be
	///     placed and how it compiles. Registered in <see cref="VfxNodeLibrary" />; node instances (<see cref="VfxNodeData" />)
	///     only reference it by <see cref="Type" />.
	/// </summary>
	public sealed class VfxNodeDefinition
	{
		#region PublicFields

		/// <summary>Stable key stored in graph files.</summary>
		public string Type;

		public string Name;

		/// <summary>Menu path ("Math/Arithmetic", "Position", "Force").</summary>
		public string Category;

		public string Description;
		public VfxNodeKind Kind;
		public VfxContextKind ContextKind;
		public VfxOutputKind OutputKind;

		/// <summary>Blocks: the contexts they may be placed in.</summary>
		public VfxContextMask AllowedContexts = VfxContextMask.Particle;

		public List<VfxSettingDefinition> Settings = new List<VfxSettingDefinition>();

		/// <summary>Input slots (depends on the settings for some nodes).</summary>
		public Func<VfxNodeData, List<VfxSlotDefinition>> InputsFactory;

		public Func<VfxNodeData, List<VfxSlotDefinition>> OutputsFactory;

		/// <summary>Title shown on the node (depends on the settings for some nodes).</summary>
		public Func<VfxNodeData, string> TitleFactory;

		public VfxOperatorEmitter Operator;
		public VfxBlockEmitter Block;

		/// <summary>Contexts: names of the flow ports.</summary>
		public string[] FlowInputs = Array.Empty<string>();

		public string[] FlowOutputs = Array.Empty<string>();

		/// <summary>Search keywords in addition to the name.</summary>
		public string Keywords = "";

		#endregion

		#region PublicMethods

		public List<VfxSlotDefinition> GetInputs(VfxNodeData node)
		{
			return InputsFactory != null ? InputsFactory(node) : new List<VfxSlotDefinition>();
		}

		public List<VfxSlotDefinition> GetOutputs(VfxNodeData node)
		{
			return OutputsFactory != null ? OutputsFactory(node) : new List<VfxSlotDefinition>();
		}

		public VfxSlotDefinition FindInput(VfxNodeData node, string slotName)
		{
			foreach (VfxSlotDefinition slot in GetInputs(node))
			{
				if (slot.Name == slotName)
				{
					return slot;
				}
			}

			return null;
		}

		public VfxSlotDefinition FindOutput(VfxNodeData node, string slotName)
		{
			foreach (VfxSlotDefinition slot in GetOutputs(node))
			{
				if (slot.Name == slotName)
				{
					return slot;
				}
			}

			return null;
		}

		public VfxSettingDefinition FindSetting(string settingName)
		{
			foreach (VfxSettingDefinition setting in Settings)
			{
				if (setting.Name == settingName)
				{
					return setting;
				}
			}

			return null;
		}

		public string GetTitle(VfxNodeData node)
		{
			if (node != null && !string.IsNullOrEmpty(node.Title))
			{
				return node.Title;
			}

			return TitleFactory != null && node != null ? TitleFactory(node) : Name;
		}

		public override string ToString()
		{
			return Type;
		}

		#endregion
	}

	/// <summary>
	///     The registry of node definitions. The operators, blocks and contexts register themselves on first use
	///     (<see cref="VfxOperatorNodes" />, <see cref="VfxBlockNodes" />, <see cref="VfxContextNodes" />).
	/// </summary>
	public static class VfxNodeLibrary
	{
		#region PublicFields

		public const string PropertyNodeType = "Operator.Property";
		public const string InlineNodeType = "Operator.Inline";

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, VfxNodeDefinition> definitions = new Dictionary<string, VfxNodeDefinition>(StringComparer.Ordinal);
		private static readonly List<VfxNodeDefinition> ordered = new List<VfxNodeDefinition>();
		private static bool initialized;
		private static readonly object sync = new object();

		#endregion

		#region PublicMethods

		public static IReadOnlyList<VfxNodeDefinition> All
		{
			get
			{
				EnsureInitialized();
				return ordered;
			}
		}

		public static VfxNodeDefinition Find(string type)
		{
			EnsureInitialized();
			VfxNodeDefinition definition;
			return type != null && definitions.TryGetValue(type, out definition) ? definition : null;
		}

		public static void Register(VfxNodeDefinition definition)
		{
			lock (sync)
			{
				if (definitions.ContainsKey(definition.Type))
				{
					ordered.RemoveAll(d => d.Type == definition.Type);
				}

				definitions[definition.Type] = definition;
				ordered.Add(definition);
			}
		}

		/// <summary>Definitions of a kind, sorted by category and name (the editor's create menu).</summary>
		public static List<VfxNodeDefinition> OfKind(VfxNodeKind kind)
		{
			EnsureInitialized();
			var result = new List<VfxNodeDefinition>();
			foreach (VfxNodeDefinition definition in ordered)
			{
				if (definition.Kind == kind)
				{
					result.Add(definition);
				}
			}

			result.Sort((a, b) =>
			{
				int byCategory = string.Compare(a.Category ?? "", b.Category ?? "", StringComparison.OrdinalIgnoreCase);
				return byCategory != 0 ? byCategory : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
			});
			return result;
		}

		/// <summary>Blocks allowed inside a context kind.</summary>
		public static List<VfxNodeDefinition> BlocksFor(VfxContextKind contextKind)
		{
			VfxContextMask mask = MaskOf(contextKind);
			var result = new List<VfxNodeDefinition>();
			foreach (VfxNodeDefinition definition in OfKind(VfxNodeKind.Block))
			{
				if ((definition.AllowedContexts & mask) != 0)
				{
					result.Add(definition);
				}
			}

			return result;
		}

		public static VfxContextMask MaskOf(VfxContextKind contextKind)
		{
			switch (contextKind)
			{
				case VfxContextKind.Spawn: return VfxContextMask.Spawn;
				case VfxContextKind.Initialize: return VfxContextMask.Initialize;
				case VfxContextKind.Update: return VfxContextMask.Update;
				case VfxContextKind.Output: return VfxContextMask.Output;
				default: return VfxContextMask.None;
			}
		}

		#endregion

		#region PrivateMethods

		private static void EnsureInitialized()
		{
			if (initialized)
			{
				return;
			}

			lock (sync)
			{
				if (initialized)
				{
					return;
				}

				initialized = true;
				VfxContextNodes.Register();
				VfxOperatorNodes.Register();
				VfxBlockNodes.Register();
			}
		}

		#endregion
	}
}
