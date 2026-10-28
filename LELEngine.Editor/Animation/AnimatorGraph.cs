using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Editor.Graphs;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Animation
{
	/// <summary>What the Animator window's canvas shows: a state machine of a layer, or a blend tree of a state.</summary>
	internal sealed class AnimatorPage
	{
		public AnimatorLayer Layer;
		public AnimatorStateMachine Machine;

		/// <summary>Set when the page shows a blend tree (of this state; nested trees through <see cref="TreePath" />).</summary>
		public AnimatorState TreeState;

		public BlendTree Tree;
		public readonly List<int> TreePath = new List<int>();

		/// <summary>Set when the page looks into a sub-machine playing another asset: the node in the open controller.</summary>
		public AnimatorStateMachine Embedding;

		/// <summary>The asset the page shows (null: the open controller's own content).</summary>
		public AnimatorController External;

		/// <summary>Another asset's content is shown as it is: nothing on the page can be edited here.</summary>
		public bool ReadOnly => External != null;

		public bool IsTree => Tree != null;

		public string Title => IsTree ? Tree.DisplayName : Machine?.name ?? "?";

		public bool SameAs(AnimatorPage other)
		{
			if (other == null || Layer != other.Layer || Machine != other.Machine || TreeState != other.TreeState || Tree != other.Tree || External != other.External)
			{
				return false;
			}

			return true;
		}
	}

	/// <summary>
	///     Builds the Animator window's graph from the controller every frame (states, sub-machine nodes, Entry / Any
	///     State / Exit, transitions grouped per direction, notes, groups; blend trees as a tree of clip nodes) and
	///     turns the view's requests (moves, connections, deletes, duplicates, renames, menus) into undoable edits of
	///     the controller.
	/// </summary>
	internal sealed class AnimatorGraph
	{
		#region PublicFields

		public readonly GraphView View = new GraphView();
		public AnimatorPage Page = new AnimatorPage();

		/// <summary>Transitions behind each edge id (the first transition's id), with the source and destination node ids.</summary>
		public readonly Dictionary<ulong, EdgeInfo> EdgeInfos = new Dictionary<ulong, EdgeInfo>();

		/// <summary>Node ids of blend tree pages: the tree root and the children (by path).</summary>
		public readonly Dictionary<ulong, TreeNodeInfo> TreeNodes = new Dictionary<ulong, TreeNodeInfo>();

		public sealed class EdgeInfo
		{
			public readonly List<AnimatorTransition> Transitions = new List<AnimatorTransition>();
			public ulong From;
			public ulong To;
			public object Source;
			public string SourceKind;
		}

		public sealed class TreeNodeInfo
		{
			public BlendTree Tree;
			public BlendTreeChild Child;
			public BlendTree ChildTree;
			public int Index;
			public List<int> Path;
		}

		public static readonly NVector4[] Palette =
		{
			new NVector4(0.32f, 0.34f, 0.4f, 1f), new NVector4(0.75f, 0.45f, 0.2f, 1f), new NVector4(0.3f, 0.55f, 0.3f, 1f), new NVector4(0.3f, 0.45f, 0.75f, 1f),
			new NVector4(0.6f, 0.35f, 0.65f, 1f), new NVector4(0.2f, 0.55f, 0.6f, 1f), new NVector4(0.7f, 0.25f, 0.3f, 1f), new NVector4(0.55f, 0.55f, 0.3f, 1f)
		};

		public static readonly string[] PaletteNames = { "Default", "Orange", "Green", "Blue", "Purple", "Teal", "Red", "Olive" };
		public static readonly NVector4 DefaultStateColor = new NVector4(0.8f, 0.45f, 0.1f, 1f);
		public static readonly NVector4 MachineColor = new NVector4(0.28f, 0.4f, 0.55f, 1f);
		public static readonly NVector4 ExternalColor = new NVector4(0.45f, 0.35f, 0.6f, 1f);
		public static readonly NVector4 EntryColor = new NVector4(0.25f, 0.55f, 0.3f, 1f);
		public static readonly NVector4 AnyStateColor = new NVector4(0.2f, 0.5f, 0.55f, 1f);
		public static readonly NVector4 ExitColor = new NVector4(0.6f, 0.25f, 0.25f, 1f);

		#endregion

		#region PrivateFields

		private const ulong EntryBit = 0x10000000000000E1;
		private const ulong AnyBit = 0x20000000000000A1;
		private const ulong ExitBit = 0x3000000000000031;
		private const ulong TreeBit = 0x4000000000000041;

		private readonly AnimatorWindow window;

		/// <summary>The copied states and notes as a document (no script-type instances kept across reloads; pastes into any controller).</summary>
		private static byte[] clipboard;

		[Serializable]
		internal sealed class AnimatorClipboard
		{
			public List<AnimatorState> states = new List<AnimatorState>();
			public List<LELEngine.Animation.GraphNote> notes = new List<LELEngine.Animation.GraphNote>();
		}

		#endregion

		#region Constructors

		public AnimatorGraph(AnimatorWindow window)
		{
			this.window = window;
			View.AllowNodeConnections = true;
			View.AllowPortConnections = false;
			View.CreateOnRightClick = false;
			View.NodesMoved = OnNodesMoved;
			View.NodeResized = OnNodeResized;
			View.ConnectRequested = OnConnect;
			View.DeleteRequested = OnDelete;
			View.DuplicateRequested = OnDuplicate;
			View.CopyRequested = OnCopy;
			View.PasteRequested = OnPaste;
			View.NodeDoubleClicked = OnDoubleClick;
			View.RenameRequested = OnRename;
			View.ContextMenu = OnContextMenu;
			View.CreateEntries = CreateEntries;
			View.DropTarget = OnDrop;
			View.ShowDefaultContextItems = true;
		}

		#endregion

		#region PublicMethods

		public AnimatorDocument Document => window.Document;
		public AnimatorController Controller => window.Document?.Controller;

		/// <summary>True on a read-only page (another asset's content): edits are refused.</summary>
		public bool ReadOnly => Page.ReadOnly;

		/// <summary>Makes a sub-machine node play a controller asset (null: its own content again); refuses loops. One undo step.</summary>
		public bool SetExternal(AnimatorStateMachine sub, AnimatorController asset)
		{
			if (sub == null)
			{
				return false;
			}

			if (asset != null && (asset == Controller || asset.Embeds(Controller)))
			{
				Debug.LogWarning("[Animator] '" + asset.name + "' embeds " + Controller.name + " (or is it): a sub-machine playing it would loop.");
				return false;
			}

			AnimatorController owner = Controller;
			Document.Edit(asset != null ? "Play asset in sub-machine" : "Detach asset", () =>
			{
				sub.controller = asset;
				sub.controllerLayer = 0;
				sub.parameterMappings.Clear();
				if (asset != null)
				{
					MapByName(sub, owner);
				}
			});
			return true;
		}

		/// <summary>Maps every unmapped parameter of the node's asset to a parameter of <paramref name="owner" /> with the same name and type (no undo step of its own).</summary>
		public static int MapByName(AnimatorStateMachine sub, AnimatorController owner)
		{
			AnimatorController asset = sub?.controller;
			if (asset == null || owner == null)
			{
				return 0;
			}

			int mapped = 0;
			foreach (AnimatorParameter child in asset.parameters)
			{
				if (sub.FindMapping(child.id) != null)
				{
					continue;
				}

				AnimatorParameter parent = owner.FindParameter(child.name);
				if (parent != null && parent.ValueType == child.ValueType && parent.IsTrigger == child.IsTrigger)
				{
					sub.SetMapping(child.id, parent.id);
					mapped++;
				}
			}

			return mapped;
		}

		/// <summary>A sub-machine node playing an asset at a position (an undo step) and selected.</summary>
		public AnimatorStateMachine CreateExternal(NVector2 at, AnimatorController asset)
		{
			if (asset == null || ReadOnly || Page.Machine == null)
			{
				return null;
			}

			if (asset == Controller || asset.Embeds(Controller))
			{
				Debug.LogWarning("[Animator] '" + asset.name + "' embeds " + Controller.name + " (or is it): it cannot play inside it.");
				return null;
			}

			AnimatorStateMachine created = null;
			AnimatorController owner = Controller;
			Document.Edit("Add sub-machine from asset", () =>
			{
				created = Page.Machine.AddSubMachine(Page.Machine.UniqueStateName(asset.name), new Vector2(at.X, at.Y));
				created.controller = asset;
				MapByName(created, owner);
			});
			View.SelectOnly(created.id);
			return created;
		}

		public static ulong EntryId(AnimatorStateMachine machine) => machine.id ^ EntryBit;
		public static ulong AnyStateId(AnimatorStateMachine machine) => machine.id ^ AnyBit;
		public static ulong ExitId(AnimatorStateMachine machine) => machine.id ^ ExitBit;

		public bool IsEntry(ulong id) => Page.Machine != null && id == EntryId(Page.Machine);
		public bool IsAnyState(ulong id) => Page.Machine != null && id == AnyStateId(Page.Machine);
		public bool IsExit(ulong id) => Page.Machine != null && id == ExitId(Page.Machine);

		/// <summary>Shows a layer's root machine.</summary>
		public void ShowLayer(AnimatorLayer layer)
		{
			if (layer == null)
			{
				Page = new AnimatorPage();
				return;
			}

			ShowMachine(layer, layer.stateMachine);
		}

		public void ShowMachine(AnimatorLayer layer, AnimatorStateMachine machine)
		{
			bool same = Page.Layer == layer && Page.Machine == machine && !Page.IsTree;
			Page = new AnimatorPage { Layer = layer, Machine = machine };
			if (!same)
			{
				View.ClearSelection();
				View.Offset = new NVector2(machine.viewOffset.X, machine.viewOffset.Y);
				View.Zoom = machine.viewZoom > 0.05f ? machine.viewZoom : 1f;
				View.PendingFrame = machine.viewOffset == Vector2.Zero && machine.viewZoom == 1f;
			}
		}

		public void ShowTree(AnimatorLayer layer, AnimatorStateMachine machine, AnimatorState state, BlendTree tree, List<int> path)
		{
			Page = new AnimatorPage { Layer = layer, Machine = machine, TreeState = state, Tree = tree, Embedding = Page.Embedding, External = Page.External };
			Page.TreePath.AddRange(path ?? new List<int>());
			View.ClearSelection();
			View.PendingFrame = true;
		}

		/// <summary>
		///     Shows the content of a sub-machine that plays another controller asset, read-only: <paramref name="machine" />
		///     is a machine of that asset (null: the layer's root machine the node plays).
		/// </summary>
		public void ShowExternal(AnimatorLayer layer, AnimatorStateMachine embedding, AnimatorStateMachine machine = null)
		{
			AnimatorStateMachine target = machine ?? embedding?.ExternalMachine;
			if (embedding == null || embedding.controller == null || target == null)
			{
				return;
			}

			bool same = Page.External == embedding.controller && Page.Machine == target && !Page.IsTree;
			Page = new AnimatorPage { Layer = layer, Machine = target, Embedding = embedding, External = embedding.controller };
			if (!same)
			{
				View.ClearSelection();
				View.Offset = new NVector2(target.viewOffset.X, target.viewOffset.Y);
				View.Zoom = target.viewZoom > 0.05f ? target.viewZoom : 1f;
				View.PendingFrame = target.viewOffset == Vector2.Zero && target.viewZoom == 1f;
			}
		}

		/// <summary>
		///     The page's objects looked up again by id: an undo, redo or revert replaces the controller's objects, and
		///     the page must follow (else edits would go into detached copies).
		/// </summary>
		public void ResolvePage()
		{
			AnimatorController controller = Controller;
			if (controller == null || Page.Layer == null)
			{
				return;
			}

			if (Page.External != null)
			{
				// An asset's content: the node must still play that asset, and the machine must still be in the asset.
				AnimatorLayer ownerLayer = controller.FindLayer(Page.Layer.id);
				AnimatorStateMachine embedding = controller.FindMachine(Page.Embedding.id);
				AnimatorStateMachine external = embedding?.controller == Page.External ? Page.External.FindMachine(Page.Machine.id) : null;
				if (ownerLayer == null || external == null)
				{
					Page = ownerLayer != null ? new AnimatorPage { Layer = ownerLayer, Machine = ownerLayer.stateMachine } : new AnimatorPage();
					return;
				}

				if (ownerLayer != Page.Layer || embedding != Page.Embedding || external != Page.Machine)
				{
					Page = new AnimatorPage { Layer = ownerLayer, Machine = external, Embedding = embedding, External = Page.External };
				}

				return;
			}

			if (controller.layers.Contains(Page.Layer) && (Page.Machine == null || controller.FindMachine(Page.Machine.id) == Page.Machine) && (Page.TreeState == null || controller.FindState(Page.TreeState.id) == Page.TreeState))
			{
				return;
			}

			AnimatorLayer layer = controller.FindLayer(Page.Layer.id);
			AnimatorStateMachine machine = Page.Machine != null ? controller.FindMachine(Page.Machine.id) : null;
			if (layer == null || machine == null)
			{
				Page = new AnimatorPage();
				return;
			}

			if (!Page.IsTree)
			{
				Page = new AnimatorPage { Layer = layer, Machine = machine };
				return;
			}

			AnimatorState state = controller.FindState(Page.TreeState.id);
			BlendTree tree = state?.motion as BlendTree;
			foreach (int index in Page.TreePath)
			{
				tree = tree != null && index < tree.children.Count ? tree.children[index].motion as BlendTree : null;
			}

			if (tree == null)
			{
				Page = new AnimatorPage { Layer = layer, Machine = machine };
				return;
			}

			var resolved = new AnimatorPage { Layer = layer, Machine = machine, TreeState = state, Tree = tree };
			resolved.TreePath.AddRange(Page.TreePath);
			Page = resolved;
		}

		/// <summary>Draws the page into the current ImGui window area.</summary>
		public void Draw()
		{
			AnimatorController controller = Controller;
			if (controller == null)
			{
				return;
			}

			ResolvePage();
			if (Page.Layer == null)
			{
				if (controller.layers.Count > 0)
				{
					ShowLayer(controller.layers[0]);
				}
				else
				{
					return;
				}
			}

			View.ShowDefaultContextItems = !ReadOnly;
			View.Begin();
			EdgeInfos.Clear();
			TreeNodes.Clear();
			if (Page.IsTree)
			{
				BuildTree();
			}
			else
			{
				BuildMachine();
			}

			View.End();
			if (!Page.IsTree && Page.Machine != null)
			{
				// The view state travels with the asset (not undoable, not dirtying).
				Page.Machine.viewOffset = new Vector2(View.Offset.X, View.Offset.Y);
				Page.Machine.viewZoom = View.Zoom;
			}
		}

		/// <summary>The state behind a node id on this page, or null.</summary>
		public AnimatorState StateOf(ulong id)
		{
			return Page.Machine?.FindStateById(id);
		}

		public AnimatorStateMachine SubMachineOf(ulong id)
		{
			if (Page.Machine == null)
			{
				return null;
			}

			foreach (AnimatorStateMachine sub in Page.Machine.subMachines)
			{
				if (sub.id == id)
				{
					return sub;
				}
			}

			return null;
		}

		public Graphs.GraphNote NoteOf(ulong id)
		{
			if (Page.Machine == null)
			{
				return null;
			}

			foreach (LELEngine.Animation.GraphNote note in Page.Machine.notes)
			{
				if (note.id == id)
				{
					return new Graphs.GraphNote { Id = note.id, Text = note.text, Position = new NVector2(note.position.X, note.position.Y), Size = new NVector2(note.size.X, note.size.Y), Tag = note };
				}
			}

			return null;
		}

		public LELEngine.Animation.GraphNote NoteDataOf(ulong id)
		{
			return Page.Machine?.notes.Find(n => n.id == id);
		}

		public GraphGroupData GroupOf(ulong id)
		{
			return Page.Machine?.groups.Find(g => g.id == id);
		}

		/// <summary>Selects a transition's edge (the edge grouping it) on the current page.</summary>
		public void SelectTransition(AnimatorTransition transition)
		{
			foreach (KeyValuePair<ulong, EdgeInfo> entry in EdgeInfos)
			{
				if (entry.Value.Transitions.Contains(transition))
				{
					View.SelectOnly(entry.Key);
					return;
				}
			}
		}

		/// <summary>Creates a state at a position (an undo step) and selects it.</summary>
		public AnimatorState CreateState(NVector2 at, AnimationClip clip = null, string name = null)
		{
			AnimatorState created = null;
			Document.Edit("Create state", () =>
			{
				created = Page.Machine.AddState(name ?? (clip != null ? clip.name : "New State"), new Vector2(at.X, at.Y));
				if (clip != null)
				{
					created.motion = new ClipMotion { clip = clip };
				}
			});
			View.SelectOnly(created.id);
			return created;
		}

		public AnimatorStateMachine CreateSubMachine(NVector2 at)
		{
			AnimatorStateMachine created = null;
			Document.Edit("Create sub-state machine", () => created = Page.Machine.AddSubMachine(Page.Machine.UniqueStateName("New State Machine"), new Vector2(at.X, at.Y)));
			View.SelectOnly(created.id);
			return created;
		}

		public void CreateNote(NVector2 at)
		{
			ulong id = AnimatorController.NewId();
			Document.EditLayout("Create note", () => Page.Machine.notes.Add(new LELEngine.Animation.GraphNote { id = id, position = new Vector2(at.X, at.Y), text = "Note" }));
			View.SelectOnly(id);
		}

		public void GroupSelection()
		{
			var members = new List<ulong>();
			foreach (ulong id in View.Selection)
			{
				if (StateOf(id) != null || SubMachineOf(id) != null || NoteDataOf(id) != null)
				{
					members.Add(id);
				}
			}

			if (members.Count == 0)
			{
				return;
			}

			ulong groupId = AnimatorController.NewId();
			Document.EditLayout("Group", () =>
			{
				foreach (GraphGroupData other in Page.Machine.groups)
				{
					other.members.RemoveAll(members.Contains);
				}

				Page.Machine.groups.Add(new GraphGroupData { id = groupId, title = "Group", members = members });
			});
			View.SelectOnly(groupId);
		}

		/// <summary>Makes a transition from a node to another (states, Any State, Entry, sub-machine nodes, Exit).</summary>
		public AnimatorTransition Connect(ulong fromId, ulong toId)
		{
			AnimatorStateMachine machine = Page.Machine;
			AnimatorController controller = Controller;
			if (machine == null || fromId == toId && !IsAnyState(fromId))
			{
				if (fromId == toId && StateOf(fromId) != null)
				{
					// A self transition is allowed (restart the state).
				}
				else
				{
					return null;
				}
			}

			var transition = new AnimatorTransition { id = AnimatorController.NewId() };
			controller.transitionDefaults.Apply(transition);
			if (IsExit(toId))
			{
				transition.toExit = true;
			}
			else if (StateOf(toId) != null)
			{
				transition.destinationState = toId;
			}
			else if (SubMachineOf(toId) != null)
			{
				transition.destinationMachine = toId;
			}
			else
			{
				return null;
			}

			List<AnimatorTransition> list;
			string what;
			if (IsAnyState(fromId))
			{
				list = machine.anyStateTransitions;
				transition.hasExitTime = false;
				what = "Any State";
			}
			else if (IsEntry(fromId))
			{
				list = machine.entryTransitions;
				transition.hasExitTime = false;
				transition.duration = 0f;
				what = "Entry";
			}
			else if (StateOf(fromId) is AnimatorState state)
			{
				list = state.transitions;
				what = state.name;
			}
			else if (SubMachineOf(fromId) is AnimatorStateMachine sub)
			{
				list = sub.transitions;
				transition.hasExitTime = false;
				what = sub.name;
			}
			else
			{
				return null;
			}

			Document.Edit("Make transition from " + what, () => list.Add(transition));
			SelectTransition(transition);
			return transition;
		}

		/// <summary>Removes the nodes, edges, notes and groups with these ids (one undo step).</summary>
		public void Delete(IReadOnlyList<ulong> ids)
		{
			AnimatorController controller = Controller;
			if (controller == null || ids.Count == 0)
			{
				return;
			}

			if (Page.IsTree)
			{
				DeleteTreeNodes(ids);
				return;
			}

			var states = new List<AnimatorState>();
			var machines = new List<AnimatorStateMachine>();
			var transitions = new List<AnimatorTransition>();
			var noteIds = new List<ulong>();
			var groupIds = new List<ulong>();
			foreach (ulong id in ids)
			{
				if (StateOf(id) is AnimatorState state) states.Add(state);
				else if (SubMachineOf(id) is AnimatorStateMachine machine) machines.Add(machine);
				else if (EdgeInfos.TryGetValue(id, out EdgeInfo info)) transitions.AddRange(info.Transitions);
				else if (NoteDataOf(id) != null) noteIds.Add(id);
				else if (GroupOf(id) != null) groupIds.Add(id);
			}

			if (states.Count + machines.Count + transitions.Count + noteIds.Count + groupIds.Count == 0)
			{
				return;
			}

			Document.Edit("Delete", () =>
			{
				foreach (AnimatorTransition transition in transitions) controller.RemoveTransition(transition);
				foreach (AnimatorState state in states) controller.RemoveState(state);
				foreach (AnimatorStateMachine machine in machines) controller.RemoveMachine(machine);
				Page.Machine.notes.RemoveAll(n => noteIds.Contains(n.id));
				Page.Machine.groups.RemoveAll(g => groupIds.Contains(g.id));
			});
			View.ClearSelection();
		}

		/// <summary>Copies states (with the transitions among them) and notes at an offset (one undo step).</summary>
		public void Duplicate(IReadOnlyList<ulong> ids, NVector2 offset)
		{
			var states = new List<AnimatorState>();
			var notes = new List<LELEngine.Animation.GraphNote>();
			foreach (ulong id in ids)
			{
				if (StateOf(id) is AnimatorState state) states.Add(state);
				else if (NoteDataOf(id) is LELEngine.Animation.GraphNote note) notes.Add(note);
			}

			if (states.Count == 0 && notes.Count == 0)
			{
				return;
			}

			var newIds = new List<ulong>();
			Document.Edit("Duplicate", () => newIds = PasteInto(Page.Machine, states, notes, offset, true));
			View.Selection.Clear();
			foreach (ulong id in newIds) View.Selection.Add(id);
			View.Primary = newIds.Count > 0 ? newIds[0] : 0;
			View.SelectionChanged?.Invoke();
		}

		public static bool HasClipboard => clipboard != null;

		public void Copy(IReadOnlyList<ulong> ids)
		{
			var data = new AnimatorClipboard();
			foreach (ulong id in ids)
			{
				if (StateOf(id) is AnimatorState state) data.states.Add(state);
				else if (NoteDataOf(id) is LELEngine.Animation.GraphNote note) data.notes.Add(note);
			}

			clipboard = data.states.Count + data.notes.Count > 0 ? Serialization.Documents.WriteObject(data, Serialization.DocumentFormat.Binary) : null;
		}

		public void Paste(NVector2 at)
		{
			if (Page.IsTree || Page.Machine == null || clipboard == null)
			{
				return;
			}

			AnimatorClipboard data;
			try
			{
				data = Serialization.Documents.ReadObject(clipboard, typeof(AnimatorClipboard), null, null) as AnimatorClipboard;
			}
			catch (Exception e)
			{
				Debug.LogError("[Animator] Cannot paste: " + e.Message);
				return;
			}

			if (data == null || data.states.Count + data.notes.Count == 0)
			{
				return;
			}

			var min = new NVector2(float.MaxValue);
			foreach (AnimatorState state in data.states) min = NVector2.Min(min, new NVector2(state.position.X, state.position.Y));
			foreach (LELEngine.Animation.GraphNote note in data.notes) min = NVector2.Min(min, new NVector2(note.position.X, note.position.Y));
			NVector2 offset = at - min;
			var newIds = new List<ulong>();
			Document.Edit("Paste", () => newIds = PasteInto(Page.Machine, data.states, data.notes, offset, false));
			View.Selection.Clear();
			foreach (ulong id in newIds) View.Selection.Add(id);
			View.Primary = newIds.Count > 0 ? newIds[0] : 0;
			View.SelectionChanged?.Invoke();
		}

		public void Rename(ulong id, string name)
		{
			if (StateOf(id) is AnimatorState state)
			{
				string unique = Page.Machine.UniqueStateName(name);
				Document.Edit("Rename state", () => state.name = unique);
			}
			else if (SubMachineOf(id) is AnimatorStateMachine machine)
			{
				Document.Edit("Rename state machine", () => machine.name = name);
			}
			else if (GroupOf(id) is GraphGroupData group)
			{
				Document.EditLayout("Rename group", () => group.title = name);
			}
		}

		public void SetDefaultState(AnimatorState state)
		{
			Document.Edit("Set default state", () => Page.Machine.defaultState = state.id);
		}

		/// <summary>Opens a sub-machine node (its asset's content read-only when it plays one) or a state's blend tree (double click, "Open").</summary>
		public void Open(ulong id)
		{
			if (SubMachineOf(id) is AnimatorStateMachine machine)
			{
				if (machine.IsExternal)
				{
					if (Page.External != null)
					{
						// An asset inside an asset: shown as its own document.
						window.Show(machine.controller);
					}
					else
					{
						window.NavigateToExternal(machine);
					}
				}
				else if (Page.External != null)
				{
					window.NavigateToExternal(Page.Embedding, machine);
				}
				else
				{
					window.NavigateTo(machine);
				}
			}
			else if (StateOf(id) is AnimatorState state && state.motion is BlendTree tree)
			{
				window.NavigateToTree(state, tree, new List<int>());
			}
			else if (Page.IsTree && TreeNodes.TryGetValue(id, out TreeNodeInfo info) && info.ChildTree != null)
			{
				var path = new List<int>(info.Path) { info.Index };
				window.NavigateToTree(Page.TreeState, info.ChildTree, path);
			}
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- building: state machines

		private void BuildMachine()
		{
			AnimatorStateMachine machine = Page.Machine;
			AnimatorController controller = Controller;
			Animator live = window.LiveAnimator;
			LayerPlayer livePlayer = LiveLayer(live);
			ulong currentId = livePlayer?.Current?.State.Id ?? 0;
			ulong nextId = livePlayer?.Next?.State.Id ?? 0;
			ulong liveTransition = livePlayer?.Transition?.Source.id ?? 0;

			// Groups first (drawn behind).
			foreach (GraphGroupData group in machine.groups)
			{
				var graphGroup = new GraphGroup { Id = group.id, Title = group.title, Color = Palette[Math.Clamp(group.colorIndex, 0, Palette.Length - 1)], Tag = group };
				graphGroup.Members.AddRange(group.members);
				View.AddGroup(graphGroup);
			}

			foreach (LELEngine.Animation.GraphNote note in machine.notes)
			{
				View.AddNote(new Graphs.GraphNote { Id = note.id, Text = note.text, Position = new NVector2(note.position.X, note.position.Y), Size = new NVector2(note.size.X, note.size.Y), Color = NoteColor(note.colorIndex), Tag = note });
			}

			// Entry, Any State, Exit.
			View.AddNode(new GraphNode { Id = EntryId(machine), Title = "Entry", Position = ToN(machine.entryPosition), Shape = GraphNodeShape.Capsule, HeaderColor = EntryColor, Deletable = false, Renamable = false, ConnectableTo = false, Tooltip = "The layer starts here: the entry transitions pick a state, else the default state." });
			View.AddNode(new GraphNode { Id = AnyStateId(machine), Title = "Any State", Position = ToN(machine.anyStatePosition), Shape = GraphNodeShape.Capsule, HeaderColor = AnyStateColor, Deletable = false, Renamable = false, ConnectableTo = false, Tooltip = "Transitions from here are checked every frame, whatever state plays." });
			View.AddNode(new GraphNode { Id = ExitId(machine), Title = "Exit", Position = ToN(machine.exitPosition), Shape = GraphNodeShape.Capsule, HeaderColor = ExitColor, Deletable = false, Renamable = false, ConnectableFrom = false, Tooltip = controller.ParentOf(machine) == null ? "Leaving the layer: it starts again at Entry." : "Leaves this sub-state machine: the parent continues with the transitions from this machine's node." });

			// States.
			foreach (AnimatorState state in machine.states)
			{
				bool isDefault = machine.defaultState == state.id;
				var node = new GraphNode { Id = state.id, Title = state.name, Position = ToN(state.position), Tag = state, Width = 160f };
				node.HeaderColor = isDefault ? DefaultStateColor : Palette[Math.Clamp(state.colorIndex, 0, Palette.Length - 1)];
				string motion = state.motion?.DisplayName ?? "(no motion)";
				node.AddLine(motion, state.motion == null);
				if (state.motion is BlendTree)
				{
					node.Subtitle = "Blend Tree";
				}

				if (Math.Abs(state.speed - 1f) > 1e-4f || state.speedParameter != 0)
				{
					node.Lines[0].Right = "x" + state.speed.ToString("0.##") + (state.speedParameter != 0 ? " * " + (controller.FindParameter(state.speedParameter)?.name ?? "?") : "");
				}

				if (!string.IsNullOrEmpty(state.tag))
				{
					node.AddLine("#" + state.tag, true);
				}

				if (state.behaviours.Count > 0)
				{
					node.AddLine(state.behaviours.Count + (state.behaviours.Count == 1 ? " behaviour" : " behaviours"), true);
				}

				if (isDefault)
				{
					node.Badge = "default";
				}

				string tooltip = state.motion != null ? "Motion: " + motion : "No motion: the state only waits.";
				if (!string.IsNullOrEmpty(state.note))
				{
					tooltip += "\n" + state.note;
				}

				node.Tooltip = tooltip;
				if (state.id == currentId && livePlayer != null)
				{
					node.Outline = EditorStyle.Accent;
					node.OutlineThickness = 3f;
					double time = livePlayer.Current.NormalizedTime;
					node.Progress = livePlayer.Current.Loop ? (float)(time - Math.Floor(time)) : (float)Math.Clamp(time, 0d, 1d);
					node.Badge = (isDefault ? "default · " : "") + time.ToString("0.00");
				}
				else if (state.id == nextId && livePlayer != null)
				{
					node.Outline = new NVector4(EditorStyle.Accent.X, EditorStyle.Accent.Y, EditorStyle.Accent.Z, 0.6f);
					double time = livePlayer.Next.NormalizedTime;
					node.Progress = livePlayer.Next.Loop ? (float)(time - Math.Floor(time)) : (float)Math.Clamp(time, 0d, 1d);
				}

				View.AddNode(node);
			}

			// Sub-machine nodes (their own content, or another controller asset playing there).
			foreach (AnimatorStateMachine sub in machine.subMachines)
			{
				var node = new GraphNode { Id = sub.id, Title = sub.name, Position = ToN(sub.position), Tag = sub, HeaderColor = sub.colorIndex > 0 ? Palette[Math.Clamp(sub.colorIndex, 0, Palette.Length - 1)] : MachineColor, Width = 160f, Shape = GraphNodeShape.Box };
				if (sub.IsExternal)
				{
					AnimatorController asset = sub.controller;
					AnimatorStateMachine content = sub.ExternalMachine;
					var states = new List<AnimatorState>();
					content?.CollectStates(states);
					node.Subtitle = "Asset: " + asset.name + (asset.layers.Count > 1 ? " / " + (content != null ? asset.layers[sub.controllerLayer].name : "?") : "");
					node.HeaderColor = sub.colorIndex > 0 ? node.HeaderColor : ExternalColor;
					node.Badge = "asset";
					bool loop = asset == controller || asset.Embeds(controller);
					if (loop)
					{
						node.AddLine("embeds this controller: cut", false).Color = EditorStyle.Error;
					}
					else if (content == null)
					{
						node.AddLine("layer " + sub.controllerLayer + " missing", false).Color = EditorStyle.Error;
					}
					else
					{
						node.AddLine(states.Count + (states.Count == 1 ? " state" : " states"), true);
					}

					int mapped = 0;
					foreach (ParameterMapping mapping in sub.parameterMappings)
					{
						if (mapping.parentParameter != 0) mapped++;
					}

					node.AddLine(mapped + " of " + asset.parameters.Count + " parameters fed", true);
					node.Tooltip = "Plays " + (asset.AssetPath ?? asset.name) + ".\nDouble click to look inside (read-only); the inspector maps the parameters." + (string.IsNullOrEmpty(sub.note) ? "" : "\n" + sub.note);
				}
				else
				{
					var states = new List<AnimatorState>();
					sub.CollectStates(states);
					node.Subtitle = "State Machine";
					node.AddLine(states.Count + (states.Count == 1 ? " state" : " states"), true);
					node.Tooltip = "Double click to open." + (string.IsNullOrEmpty(sub.note) ? "" : "\n" + sub.note);
				}

				if (livePlayer?.Current != null && IsInside(livePlayer.Current.State, sub))
				{
					node.Outline = EditorStyle.Accent;
					node.OutlineThickness = 3f;
					node.Badge = livePlayer.Current.State.Name;
				}

				View.AddNode(node);
			}

			// Edges: transitions grouped by (source node, destination node).
			var groups = new Dictionary<(ulong, ulong), EdgeInfo>();
			void Add(ulong from, ulong to, AnimatorTransition transition, object source, string kind)
			{
				if (!groups.TryGetValue((from, to), out EdgeInfo info))
				{
					info = new EdgeInfo { From = from, To = to, Source = source, SourceKind = kind };
					groups[(from, to)] = info;
				}

				info.Transitions.Add(transition);
			}

			ulong DestinationOf(AnimatorTransition t)
			{
				if (t.toExit) return ExitId(machine);
				if (t.destinationState != 0 && machine.FindStateById(t.destinationState) != null) return t.destinationState;
				if (t.destinationMachine != 0 && machine.subMachines.Exists(m => m.id == t.destinationMachine)) return t.destinationMachine;
				return 0;
			}

			foreach (AnimatorState state in machine.states)
			{
				foreach (AnimatorTransition transition in state.transitions)
				{
					ulong to = DestinationOf(transition);
					if (to != 0) Add(state.id, to, transition, state, "state");
				}
			}

			foreach (AnimatorStateMachine sub in machine.subMachines)
			{
				foreach (AnimatorTransition transition in sub.transitions)
				{
					ulong to = DestinationOf(transition);
					if (to != 0) Add(sub.id, to, transition, sub, "machine");
				}
			}

			foreach (AnimatorTransition transition in machine.anyStateTransitions)
			{
				ulong to = DestinationOf(transition);
				if (to != 0) Add(AnyStateId(machine), to, transition, machine, "any");
			}

			foreach (AnimatorTransition transition in machine.entryTransitions)
			{
				ulong to = DestinationOf(transition);
				if (to != 0) Add(EntryId(machine), to, transition, machine, "entry");
			}

			foreach (KeyValuePair<(ulong, ulong), EdgeInfo> entry in groups)
			{
				EdgeInfo info = entry.Value;
				ulong edgeId = info.Transitions[0].id;
				EdgeInfos[edgeId] = info;
				var edge = new GraphEdge { Id = edgeId, From = info.From, To = info.To, Style = GraphEdgeStyle.Arrow, Tag = info };
				bool allMuted = true;
				bool anySolo = false;
				foreach (AnimatorTransition t in info.Transitions)
				{
					allMuted &= t.mute;
					anySolo |= t.solo;
				}

				edge.Color = info.SourceKind == "any" ? new NVector4(0.5f, 0.8f, 0.85f, 0.9f) : info.SourceKind == "entry" ? new NVector4(0.5f, 0.85f, 0.5f, 0.9f) : new NVector4(0.85f, 0.85f, 0.9f, 0.9f);
				if (allMuted)
				{
					edge.Dashed = true;
					edge.Color = new NVector4(0.6f, 0.4f, 0.4f, 0.8f);
				}
				else if (anySolo)
				{
					edge.Color = new NVector4(0.95f, 0.8f, 0.3f, 0.95f);
				}

				if (info.Transitions.Count > 1)
				{
					edge.Label = info.Transitions.Count.ToString();
				}

				var tooltip = new System.Text.StringBuilder();
				foreach (AnimatorTransition t in info.Transitions)
				{
					if (tooltip.Length > 0) tooltip.Append('\n');
					tooltip.Append(string.IsNullOrEmpty(t.name) ? "" : t.name + ": ");
					tooltip.Append(t.DescribeConditions(controller));
					if (t.hasExitTime && t.conditions.Count > 0) tooltip.Append(" (exit time " + t.exitTime.ToString("0.##") + ")");
					if (t.mute) tooltip.Append(" [muted]");
					if (t.solo) tooltip.Append(" [solo]");
				}

				edge.Tooltip = tooltip.ToString();
				foreach (AnimatorTransition t in info.Transitions)
				{
					if (t.id == liveTransition && livePlayer != null)
					{
						edge.Highlight = true;
						edge.Progress = livePlayer.TransitionProgress;
					}
				}

				View.AddEdge(edge);
			}

			// The default state's entry arrow (implicit).
			if (machine.defaultState != 0 && machine.FindStateById(machine.defaultState) != null && !groups.ContainsKey((EntryId(machine), machine.defaultState)))
			{
				View.AddEdge(new GraphEdge { Id = EntryId(machine) ^ machine.defaultState, From = EntryId(machine), To = machine.defaultState, Style = GraphEdgeStyle.Arrow, Color = new NVector4(0.9f, 0.6f, 0.2f, 0.9f), Selectable = false, Deletable = false, Tooltip = "Default state (the entry transitions come first)" });
			}
		}

		private static bool IsInside(RuntimeState state, AnimatorStateMachine machine)
		{
			for (RuntimeMachine m = state.Machine; m != null; m = m.Parent)
			{
				if (m.Id == machine.id)
				{
					return true;
				}
			}

			return false;
		}

		private LayerPlayer LiveLayer(Animator live)
		{
			if (live?.Player == null || Page.Layer == null)
			{
				return null;
			}

			int index = Controller.layers.IndexOf(Page.Layer);
			return index >= 0 && index < live.Player.Layers.Length ? live.Player.Layers[index] : null;
		}

		// ---------------------------------------------------------------- building: blend trees

		private void BuildTree()
		{
			BlendTree tree = Page.Tree;
			AnimatorController controller = Controller;
			Animator live = window.LiveAnimator;
			float[] weights = null;
			LayerPlayer livePlayer = LiveLayer(live);
			if (livePlayer != null && Page.TreeState != null)
			{
				weights = LiveWeights(livePlayer, tree);
			}

			ulong rootId = tree.GetHashCode() == 0 ? TreeBit : (ulong)tree.GetHashCode() ^ TreeBit;
			var root = new GraphNode { Id = rootId, Title = tree.DisplayName, Position = new NVector2(0f, 0f), HeaderColor = new NVector4(0.5f, 0.4f, 0.65f, 1f), Width = 200f, Deletable = false, Movable = false, ConnectableFrom = false, ConnectableTo = false, Tag = tree };
			root.Subtitle = tree.blendType.ToString();
			string x = controller.FindParameter(tree.parameter)?.name ?? (tree.blendType == BlendTreeType.Direct ? "" : "(no parameter)");
			if (tree.blendType != BlendTreeType.Direct)
			{
				root.AddLine(tree.blendType == BlendTreeType.Simple1D ? "Parameter: " + x : "X: " + x + "   Y: " + (controller.FindParameter(tree.parameterY)?.name ?? "(none)"));
			}

			if (live != null && tree.blendType != BlendTreeType.Direct)
			{
				float px = live.GetParameterState(tree.parameter != 0 ? controller.FindParameter(tree.parameter)?.name ?? "" : "")?.AsFloat() ?? 0f;
				root.Lines[0].Right = tree.blendType == BlendTreeType.Simple1D ? px.ToString("0.00") : px.ToString("0.00") + ", " + (live.GetParameterState(controller.FindParameter(tree.parameterY)?.name ?? "")?.AsFloat() ?? 0f).ToString("0.00");
			}

			TreeNodes[rootId] = new TreeNodeInfo { Tree = tree, Index = -1, Path = new List<int>(Page.TreePath) };
			for (int i = 0; i < tree.children.Count; i++)
			{
				root.AddPort("child" + i, GraphPortDirection.Output, new NVector4(0.8f, 0.7f, 0.95f, 1f), ChildLabel(tree, i));
			}

			View.AddNode(root);
			float y = -(tree.children.Count - 1) * 45f;
			for (int i = 0; i < tree.children.Count; i++)
			{
				BlendTreeChild child = tree.children[i];
				ulong id = ChildId(tree, i);
				var node = new GraphNode { Id = id, Position = new NVector2(300f, y + i * 90f), Width = 190f, Movable = false, ConnectableFrom = false, ConnectableTo = false, Tag = child };
				if (child.motion is BlendTree nested)
				{
					node.Title = nested.DisplayName;
					node.Subtitle = "Blend Tree (" + nested.children.Count + ")";
					node.HeaderColor = new NVector4(0.5f, 0.4f, 0.65f, 1f);
					node.Tooltip = "Double click to open.";
					TreeNodes[id] = new TreeNodeInfo { Tree = tree, Child = child, ChildTree = nested, Index = i, Path = new List<int>(Page.TreePath) };
				}
				else
				{
					AnimationClip clip = (child.motion as ClipMotion)?.clip;
					node.Title = clip != null ? clip.name : "(no clip)";
					node.Subtitle = clip != null ? clip.length.ToString("0.##") + " s" + (clip.loop ? ", loop" : "") : "Drop a clip here";
					node.HeaderColor = clip != null ? new NVector4(0.3f, 0.5f, 0.35f, 1f) : new NVector4(0.4f, 0.4f, 0.45f, 1f);
					TreeNodes[id] = new TreeNodeInfo { Tree = tree, Child = child, Index = i, Path = new List<int>(Page.TreePath) };
				}

				node.AddPort("in", GraphPortDirection.Input, new NVector4(0.8f, 0.7f, 0.95f, 1f), ChildLabel(tree, i));
				if (Math.Abs(child.timeScale - 1f) > 1e-4f)
				{
					node.AddLine("speed x" + child.timeScale.ToString("0.##"), true);
				}

				if (weights != null && i < weights.Length)
				{
					node.Progress = weights[i];
					node.Badge = weights[i].ToString("0.00");
					if (weights[i] > 0.001f)
					{
						node.Outline = new NVector4(EditorStyle.Accent.X, EditorStyle.Accent.Y, EditorStyle.Accent.Z, 0.4f + 0.6f * weights[i]);
					}
				}

				View.AddNode(node);
				View.AddEdge(new GraphEdge { Id = id ^ 0x77, From = rootId, FromPort = "child" + i, To = id, ToPort = "in", Style = GraphEdgeStyle.Bezier, Color = new NVector4(0.7f, 0.6f, 0.85f, 0.9f), Selectable = false, Deletable = false });
			}
		}

		public static ulong ChildId(BlendTree tree, int index)
		{
			return ((ulong)tree.GetHashCode() << 8 | (ulong)(index & 0xFF)) ^ TreeBit ^ 0x5151;
		}

		private static string ChildLabel(BlendTree tree, int i)
		{
			BlendTreeChild child = tree.children[i];
			switch (tree.blendType)
			{
				case BlendTreeType.Simple1D: return child.threshold.ToString("0.##");
				case BlendTreeType.Direct: return "direct";
				default: return child.position.X.ToString("0.##") + ", " + child.position.Y.ToString("0.##");
			}
		}

		private float[] LiveWeights(LayerPlayer player, BlendTree tree)
		{
			RuntimeState state = player.Current?.State;
			if (state == null || state.Id != Page.TreeState.id)
			{
				return null;
			}

			RuntimeMotion motion = state.Motion;
			foreach (int index in Page.TreePath)
			{
				if (motion is RuntimeBlendTree runtimeTree && index < runtimeTree.Children.Length)
				{
					motion = runtimeTree.Children[index].Motion;
				}
				else
				{
					return null;
				}
			}

			if (!(motion is RuntimeBlendTree target) || target.Children.Length == 0)
			{
				return null;
			}

			var weights = new float[target.Children.Length];
			AnimatorPlayer owner = player.Owner;
			float x = target.ParameterX >= 0 ? owner.ParameterFloat(target.ParameterX) : 0f;
			float y = target.ParameterY >= 0 ? owner.ParameterFloat(target.ParameterY) : 0f;
			target.ComputeWeights(x, y, owner.Parameters, weights);
			return weights;
		}

		private void DeleteTreeNodes(IReadOnlyList<ulong> ids)
		{
			var remove = new List<BlendTreeChild>();
			foreach (ulong id in ids)
			{
				if (TreeNodes.TryGetValue(id, out TreeNodeInfo info) && info.Child != null)
				{
					remove.Add(info.Child);
				}
			}

			if (remove.Count == 0)
			{
				return;
			}

			BlendTree tree = Page.Tree;
			Document.Edit("Remove blend tree child", () =>
			{
				tree.children.RemoveAll(remove.Contains);
				if (tree.automateThresholds) tree.SpreadThresholds();
			});
			View.ClearSelection();
		}

		// ---------------------------------------------------------------- view callbacks

		private void OnNodesMoved(IReadOnlyList<GraphMove> moves)
		{
			if (Page.Machine == null || Page.IsTree || ReadOnly)
			{
				return;
			}

			AnimatorStateMachine machine = Page.Machine;
			Document.EditLayout(moves.Count == 1 ? "Move node" : "Move nodes", () =>
			{
				foreach (GraphMove move in moves)
				{
					var position = new Vector2(move.Position.X, move.Position.Y);
					if (StateOf(move.Id) is AnimatorState state) state.position = position;
					else if (SubMachineOf(move.Id) is AnimatorStateMachine sub) sub.position = position;
					else if (NoteDataOf(move.Id) is LELEngine.Animation.GraphNote note) note.position = position;
					else if (IsEntry(move.Id)) machine.entryPosition = position;
					else if (IsAnyState(move.Id)) machine.anyStatePosition = position;
					else if (IsExit(move.Id)) machine.exitPosition = position;
				}
			});
		}

		private void OnNodeResized(GraphMove move)
		{
			if (!ReadOnly && NoteDataOf(move.Id) is LELEngine.Animation.GraphNote note)
			{
				Document.EditLayout("Resize note", () => note.size = new Vector2(move.Size.X, move.Size.Y));
			}
		}

		private void OnConnect(GraphConnection connection)
		{
			if (Page.IsTree || ReadOnly)
			{
				return;
			}

			Connect(connection.From, connection.To);
		}

		private void OnDelete(IReadOnlyList<ulong> ids)
		{
			if (!ReadOnly) Delete(ids);
		}

		private void OnDuplicate(IReadOnlyList<ulong> ids)
		{
			if (!ReadOnly) Duplicate(ids, new NVector2(40f, 40f));
		}

		private void OnCopy(IReadOnlyList<ulong> ids)
		{
			Copy(ids);
		}

		private void OnPaste(NVector2 at)
		{
			if (!ReadOnly) Paste(at);
		}

		private void OnDoubleClick(ulong id)
		{
			if (SubMachineOf(id) != null || (StateOf(id)?.motion is BlendTree) || (Page.IsTree && TreeNodes.TryGetValue(id, out TreeNodeInfo info) && info.ChildTree != null))
			{
				Open(id);
			}
			else if (StateOf(id) != null)
			{
				View.BeginRename(id);
			}
		}

		private void OnRename(ulong id, string name)
		{
			if (!ReadOnly) Rename(id, name);
		}

		private void OnDrop(NVector2 at)
		{
			if (!DragAndDrop.IsDragging || ReadOnly)
			{
				return;
			}

			if (Page.IsTree)
			{
				if (DragAndDrop.AcceptValue(typeof(AnimationClip), out object droppedChild) && droppedChild is AnimationClip childClip)
				{
					BlendTree tree = Page.Tree;
					Document.Edit("Add blend tree child", () => tree.AddClip(childClip, tree.children.Count > 0 ? tree.children[tree.children.Count - 1].threshold + 1f : 0f));
				}

				return;
			}

			if (DragAndDrop.AcceptValue(typeof(AnimationClip), out object dropped) && dropped is AnimationClip clip)
			{
				CreateState(at, clip);
			}
			else if (DragAndDrop.AcceptValue(typeof(AnimatorController), out object droppedAsset) && droppedAsset is AnimatorController asset)
			{
				CreateExternal(at, asset);
			}
		}

		private void OnContextMenu(GraphContextMenu menu)
		{
			AnimatorController controller = Controller;
			if (ReadOnly)
			{
				ImGui.TextDisabled("Content of " + Page.External.name + " (read-only)");
				if (EditorMenu.Item("Open " + Page.External.name + " to edit")) window.Show(Page.External);
				if (menu.Kind == GraphContextKind.Node && SubMachineOf(menu.Id) != null && EditorMenu.Item("Open")) Open(menu.Id);
				if (EditorMenu.Item("Back to " + (Page.Embedding?.name ?? "the controller"))) window.NavigateTo(controller.MachineOfNode(Page.Embedding) ?? Page.Layer.stateMachine);
				return;
			}

			switch (menu.Kind)
			{
				case GraphContextKind.Canvas:
					if (Page.IsTree)
					{
						if (EditorMenu.Item("Add Clip Child")) AddTreeChild(null);
						if (EditorMenu.Item("Add Blend Tree Child")) AddTreeChild(new BlendTree { name = "Blend Tree" });
						if (EditorMenu.Item("Back to State Machine")) window.NavigateTo(Page.Machine);
						break;
					}

					if (EditorMenu.Item("Create State", "Space")) CreateState(menu.GraphPosition);
					if (EditorMenu.Item("Create State with Blend Tree")) CreateBlendTreeState(menu.GraphPosition);
					if (EditorMenu.Item("Create Sub-State Machine")) CreateSubMachine(menu.GraphPosition);
					if (EditorMenu.Item("Create Note")) CreateNote(menu.GraphPosition);
					if (EditorMenu.Item("Paste", "Ctrl+V", false, HasClipboard)) Paste(menu.GraphPosition);
					ImGui.Separator();
					if (EditorMenu.Item("Auto Layout")) AutoLayout();
					break;
				case GraphContextKind.Node:
				{
					if (Page.IsTree)
					{
						if (TreeNodes.TryGetValue(menu.Id, out TreeNodeInfo info) && info.Child != null)
						{
							if (info.ChildTree != null && EditorMenu.Item("Open")) Open(menu.Id);
							if (EditorMenu.Item("Remove Child")) DeleteTreeNodes(new List<ulong> { menu.Id });
						}
						else
						{
							if (EditorMenu.Item("Add Clip Child")) AddTreeChild(null);
							if (EditorMenu.Item("Add Blend Tree Child")) AddTreeChild(new BlendTree { name = "Blend Tree" });
						}

						break;
					}

					AnimatorState state = StateOf(menu.Id);
					if (state != null)
					{
						if (EditorMenu.Item("Set as Default State", null, Page.Machine.defaultState == state.id)) SetDefaultState(state);
						if (state.motion is BlendTree && EditorMenu.Item("Open Blend Tree")) Open(menu.Id);
						if (EditorMenu.Begin("Motion"))
						{
							if (EditorMenu.Item("Clip", null, state.motion is ClipMotion)) Document.Edit("Set motion", () => state.motion = state.motion is ClipMotion ? state.motion : new ClipMotion());
							if (EditorMenu.Item("Blend Tree", null, state.motion is BlendTree)) Document.Edit("Set motion", () => state.motion = state.motion is BlendTree ? state.motion : new BlendTree { name = state.name + " Tree" });
							if (EditorMenu.Item("None", null, state.motion == null)) Document.Edit("Set motion", () => state.motion = null);
							EditorMenu.End();
						}

						if (EditorMenu.Begin("Add Behaviour"))
						{
							List<Type> types = TypeSelector.ConcreteTypes(typeof(StateMachineBehaviour));
							if (types.Count == 0) ImGui.TextDisabled("No StateMachineBehaviour class in the scripts.");
							foreach (Type type in types)
							{
								if (EditorMenu.Item(TypeSelector.DisplayName(type)))
								{
									Document.Edit("Add behaviour", () => state.behaviours.Add((StateMachineBehaviour)Activator.CreateInstance(type)));
								}
							}

							EditorMenu.End();
						}

						ColorMenu(state.colorIndex, index => Document.EditLayout("Color state", () => state.colorIndex = index));
						if (EditorMenu.Item("Add to New Group")) GroupSelection();
					}

					AnimatorStateMachine sub = SubMachineOf(menu.Id);
					if (sub != null)
					{
						if (EditorMenu.Item(sub.IsExternal ? "Look Inside (read-only)" : "Open")) Open(menu.Id);
						if (sub.IsExternal && EditorMenu.Item("Open " + sub.controller.name + " to edit")) window.Show(sub.controller);
						if (sub.IsExternal && EditorMenu.Item("Detach Asset (own content again)")) SetExternal(sub, null);
						ColorMenu(sub.colorIndex, index => Document.EditLayout("Color state machine", () => sub.colorIndex = index));
					}

					break;
				}
				case GraphContextKind.Edge:
					if (EdgeInfos.TryGetValue(menu.Id, out EdgeInfo edgeInfo))
					{
						bool allMuted = edgeInfo.Transitions.TrueForAll(t => t.mute);
						if (EditorMenu.Item(allMuted ? "Unmute" : "Mute")) Document.Edit("Mute transitions", () => edgeInfo.Transitions.ForEach(t => t.mute = !allMuted));
						bool allSolo = edgeInfo.Transitions.TrueForAll(t => t.solo);
						if (EditorMenu.Item(allSolo ? "Unsolo" : "Solo")) Document.Edit("Solo transitions", () => edgeInfo.Transitions.ForEach(t => t.solo = !allSolo));
						if (EditorMenu.Item("Add Another Transition")) Connect(edgeInfo.From, edgeInfo.To);
					}

					break;
				case GraphContextKind.Group:
				{
					GraphGroupData group = GroupOf(menu.Id);
					if (group != null)
					{
						if (EditorMenu.Item("Rename")) View.BeginRename(menu.Id);
						ColorMenu(group.colorIndex, index => Document.EditLayout("Color group", () => group.colorIndex = index));
						if (EditorMenu.Item("Ungroup")) Document.EditLayout("Ungroup", () => Page.Machine.groups.Remove(group));
					}

					break;
				}
				case GraphContextKind.Note:
				{
					LELEngine.Animation.GraphNote note = NoteDataOf(menu.Id);
					if (note != null)
					{
						ColorMenu(note.colorIndex, index => Document.EditLayout("Color note", () => note.colorIndex = index));
					}

					break;
				}
			}
		}

		private void ColorMenu(int current, Action<int> set)
		{
			if (EditorMenu.Begin("Color"))
			{
				for (int i = 0; i < PaletteNames.Length; i++)
				{
					if (EditorMenu.Item(PaletteNames[i], null, current == i))
					{
						set(i);
					}
				}

				EditorMenu.End();
			}
		}

		private IEnumerable<GraphMenuEntry> CreateEntries(NVector2 at, GraphConnection? connection)
		{
			var entries = new List<GraphMenuEntry>();
			if (ReadOnly)
			{
				return entries;
			}

			if (Page.IsTree)
			{
				entries.Add(new GraphMenuEntry("Clip Child", p => AddTreeChild(null)));
				entries.Add(new GraphMenuEntry("Blend Tree Child", p => AddTreeChild(new BlendTree { name = "Blend Tree" })));
				return entries;
			}

			foreach (string path in AssetDatabase.FindAssets(typeof(AnimatorController)))
			{
				string assetPath = path;
				if (string.Equals(assetPath, Controller.AssetPath, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				entries.Add(new GraphMenuEntry("Sub-State Machine from Asset/" + System.IO.Path.GetFileNameWithoutExtension(path), p =>
				{
					AnimatorStateMachine created = CreateExternal(p, AnimatorController.Load(assetPath));
					if (created != null && connection != null && connection.Value.From != 0) Connect(connection.Value.From, created.id);
				}, path));
			}

			entries.Add(new GraphMenuEntry("State/Empty State", p => CreateStateAndConnect(p, null, connection), "A state without a motion (waits, transitions)."));
			entries.Add(new GraphMenuEntry("State/Blend Tree State", p => CreateBlendTreeState(p, connection), "A state blending several clips by parameters."));
			foreach (string path in AssetDatabase.FindAssets(typeof(AnimationClip)))
			{
				string clipPath = path;
				entries.Add(new GraphMenuEntry("Clip/" + System.IO.Path.GetFileNameWithoutExtension(path), p => CreateStateAndConnect(p, AnimationClip.Load(clipPath), connection), path));
			}

			entries.Add(new GraphMenuEntry("Sub-State Machine", p =>
			{
				AnimatorStateMachine sub = CreateSubMachine(p);
				if (connection != null) Connect(connection.Value.From, sub.id);
			}));
			entries.Add(new GraphMenuEntry("Note", p => CreateNote(p)));
			return entries;
		}

		private void CreateStateAndConnect(NVector2 at, AnimationClip clip, GraphConnection? connection)
		{
			AnimatorState state = CreateState(at, clip);
			if (connection != null && connection.Value.From != 0)
			{
				Connect(connection.Value.From, state.id);
			}
		}

		private void CreateBlendTreeState(NVector2 at, GraphConnection? connection = null)
		{
			AnimatorState created = null;
			Document.Edit("Create blend tree state", () =>
			{
				created = Page.Machine.AddState("Blend Tree", new Vector2(at.X, at.Y));
				var tree = new BlendTree { name = "Blend Tree" };
				if (Controller.parameters.Count > 0)
				{
					foreach (AnimatorParameter parameter in Controller.parameters)
					{
						if (parameter.ValueType == typeof(float))
						{
							tree.parameter = parameter.id;
							break;
						}
					}
				}

				created.motion = tree;
			});
			View.SelectOnly(created.id);
			if (connection != null && connection.Value.From != 0)
			{
				Connect(connection.Value.From, created.id);
			}
		}

		private void AddTreeChild(Motion motion)
		{
			BlendTree tree = Page.Tree;
			Document.Edit("Add blend tree child", () =>
			{
				BlendTreeChild child = tree.AddChild(motion ?? new ClipMotion());
				if (!tree.automateThresholds && tree.children.Count > 1)
				{
					child.threshold = tree.children[tree.children.Count - 2].threshold + 1f;
				}
			});
		}

		/// <summary>
		///     Lays the machine out left to right: the default state first, then the states by their distance over the
		///     transitions (breadth first), unreachable ones last; Entry and Any State left of the first column, Exit
		///     right of the last. One undo step.
		/// </summary>
		public void AutoLayout()
		{
			if (Page.IsTree || Page.Machine == null || ReadOnly)
			{
				return;
			}

			AnimatorStateMachine machine = Page.Machine;
			var depth = new Dictionary<ulong, int>();
			var queue = new Queue<ulong>();
			void Visit(ulong id, int d)
			{
				if (id != 0 && !depth.ContainsKey(id) && (machine.FindStateById(id) != null || machine.subMachines.Exists(m => m.id == id)))
				{
					depth[id] = d;
					queue.Enqueue(id);
				}
			}

			Visit(machine.defaultState, 0);
			foreach (AnimatorTransition entry in machine.entryTransitions) Visit(entry.destinationState != 0 ? entry.destinationState : entry.destinationMachine, 0);
			while (queue.Count > 0)
			{
				ulong id = queue.Dequeue();
				int d = depth[id];
				List<AnimatorTransition> outgoing = machine.FindStateById(id)?.transitions ?? machine.subMachines.Find(m => m.id == id)?.transitions ?? new List<AnimatorTransition>();
				foreach (AnimatorTransition t in outgoing) Visit(t.destinationState != 0 ? t.destinationState : t.destinationMachine, d + 1);
			}

			int last = 0;
			foreach (int d in depth.Values) last = Math.Max(last, d);
			// Any-state targets and unreachable nodes go in their own columns after the reachable ones.
			foreach (AnimatorTransition t in machine.anyStateTransitions) Visit(t.destinationState != 0 ? t.destinationState : t.destinationMachine, last + 1);
			foreach (AnimatorState state in machine.states) Visit(state.id, last + 2);
			foreach (AnimatorStateMachine sub in machine.subMachines) Visit(sub.id, last + 2);
			var columns = new Dictionary<int, List<ulong>>();
			int maxDepth = 0;
			foreach (KeyValuePair<ulong, int> entry in depth)
			{
				if (!columns.TryGetValue(entry.Value, out List<ulong> column))
				{
					column = new List<ulong>();
					columns[entry.Value] = column;
				}

				column.Add(entry.Key);
				maxDepth = Math.Max(maxDepth, entry.Value);
			}

			const float columnWidth = 280f;
			const float rowHeight = 120f;
			Document.EditLayout("Auto layout", () =>
			{
				for (int d = 0; d <= maxDepth; d++)
				{
					if (!columns.TryGetValue(d, out List<ulong> column))
					{
						continue;
					}

					float y = -(column.Count - 1) * rowHeight * 0.5f;
					foreach (ulong id in column)
					{
						var position = new Vector2(d * columnWidth, y);
						AnimatorState state = machine.FindStateById(id);
						if (state != null) state.position = position;
						else machine.subMachines.Find(m => m.id == id).position = position;
						y += rowHeight;
					}
				}

				machine.entryPosition = new Vector2(-columnWidth, -rowHeight * 0.5f);
				machine.anyStatePosition = new Vector2(-columnWidth, rowHeight * 0.5f);
				machine.exitPosition = new Vector2((maxDepth + 1) * columnWidth, 0f);
			});
			View.PendingFrame = true;
		}

		private static List<ulong> PasteInto(AnimatorStateMachine machine, List<AnimatorState> states, List<LELEngine.Animation.GraphNote> notes, NVector2 offset, bool keepTransitionsAmong)
		{
			var map = new Dictionary<ulong, ulong>();
			var copies = new List<AnimatorState>();
			var ids = new List<ulong>();
			foreach (AnimatorState state in states)
			{
				AnimatorState copy = state.Clone();
				copy.id = AnimatorController.NewId();
				map[state.id] = copy.id;
				copy.name = machine.UniqueStateName(state.name);
				copy.position = state.position + new Vector2(offset.X, offset.Y);
				copies.Add(copy);
				ids.Add(copy.id);
			}

			foreach (AnimatorState copy in copies)
			{
				// Transitions among the copied states follow the copies; others are dropped (they would point out of the selection).
				copy.transitions.RemoveAll(t =>
				{
					if (t.destinationState != 0)
					{
						if (map.TryGetValue(t.destinationState, out ulong mapped))
						{
							t.destinationState = mapped;
							t.id = AnimatorController.NewId();
							return false;
						}

						return !keepTransitionsAmong && machine.FindStateById(t.destinationState) == null;
					}

					t.id = AnimatorController.NewId();
					return false;
				});
				machine.states.Add(copy);
			}

			foreach (LELEngine.Animation.GraphNote note in notes)
			{
				var copy = new LELEngine.Animation.GraphNote { id = AnimatorController.NewId(), text = note.text, position = note.position + new Vector2(offset.X, offset.Y), size = note.size, colorIndex = note.colorIndex };
				machine.notes.Add(copy);
				ids.Add(copy.id);
			}

			return ids;
		}

		private static NVector2 ToN(Vector2 v)
		{
			return new NVector2(v.X, v.Y);
		}

		private static NVector4 NoteColor(int index)
		{
			switch (index)
			{
				case 1: return new NVector4(0.95f, 0.65f, 0.45f, 1f);
				case 2: return new NVector4(0.6f, 0.9f, 0.6f, 1f);
				case 3: return new NVector4(0.6f, 0.75f, 0.95f, 1f);
				case 4: return new NVector4(0.85f, 0.65f, 0.9f, 1f);
				default: return new NVector4(0.95f, 0.85f, 0.4f, 1f);
			}
		}

		#endregion
	}
}
