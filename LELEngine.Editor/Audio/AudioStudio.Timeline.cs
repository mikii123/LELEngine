using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The Event Editor, drawn the way FMOD Studio draws it: the sheet tabs (the timeline and the parameter sheets),
	///     the ruler (time or bars), the logic track (transition regions with chevrons, transition markers, destination
	///     flags, tempo and sustain markers, loop regions), the tracks (a header with SOLO, MUTE, a volume knob and a
	///     meter; a lane with the instruments, where overlapping ones share the lane in thinner rows) and the overview
	///     strip below. A transition with a transition timeline opens inline: the timeline splits at the transition
	///     into the part before it, the transition timeline itself (the source's sounds extended into it and fading
	///     out, the destination's sounds starting early and fading in, the transition's own instruments) and the
	///     destination's part after it. Everything is edited with the mouse: move, trim, the region ends and the fade
	///     handles.
	/// </summary>
	internal sealed partial class AudioStudio
	{
		#region PrivateFields

		private const float HeaderWidth = 214f;
		private const float RulerHeight = 26f;
		private const float LogicRowHeight = 21f;
		private const float LogicHeight = LogicRowHeight * 3f + 9f;
		private const float TrackHeight = 90f;
		private const float MasterHeight = 56f;
		private const float MinimapHeight = 30f;
		private const float EdgeGrab = 6f;
		private const float HandleSize = 9f;
		private const float PieceHeaderHeight = 15f;

		private enum RowKind
		{
			Logic,
			Track,
			Master
		}

		private struct Row
		{
			public RowKind Kind;
			public AudioTrack Track;
			public int TrackIndex;
			public float Y;
			public float Height;
		}

		/// <summary>The parts of the timeline while a transition is open inline (one part otherwise).</summary>
		private enum Segment
		{
			/// <summary>Event time, before the transition.</summary>
			Event,

			/// <summary>Transition time (0 = the transition point).</summary>
			Transition,

			/// <summary>Event time from the destination on.</summary>
			Destination
		}

		private enum PieceKind
		{
			/// <summary>An instrument on the sheet shown.</summary>
			Normal,

			/// <summary>A timelocked instrument playing at the transition, extended into the transition timeline.</summary>
			Source,

			/// <summary>An instrument at or after the destination, as the transition plays it.</summary>
			Destination,

			/// <summary>An instrument on the transition timeline itself.</summary>
			TransitionClip
		}

		/// <summary>One drawn instrument region (an instrument can show as several pieces while a transition is open).</summary>
		private sealed class Piece
		{
			public PieceKind Kind;
			public AudioInstrument Instrument;
			public AudioTrack Track;
			public int TrackIndex;

			/// <summary>Display units: seconds of the (split) timeline, or parameter units.</summary>
			public double From;

			public double To;

			/// <summary>Seconds into the instrument's region at <see cref="From" /> (for the waveform).</summary>
			public double ContentOffset;

			/// <summary>The visible start is not the instrument's start (cut by the transition).</summary>
			public bool CutStart;

			public bool CutEnd;

			/// <summary>A source piece that ends where the source region ends (its end edge sets the source length).</summary>
			public bool EndsAtSourceLength;

			/// <summary>A destination piece that starts with the destination region (its start edge sets the destination start).</summary>
			public bool StartsAtDestination;

			public int Row;
			public int Rows = 1;
			public NVector2 Min;
			public NVector2 Max;
		}

		private sealed class PieceOrder : IComparer<Piece>
		{
			public static readonly PieceOrder Instance = new PieceOrder();

			public int Compare(Piece a, Piece b)
			{
				int order = a.From.CompareTo(b.From);
				return order != 0 ? order : b.To.CompareTo(a.To);
			}
		}

		private enum DragKind
		{
			None,
			MoveInstrument,
			ResizeInstrumentEnd,
			ResizeInstrumentStart,
			MoveMarker,
			ResizeMarker,
			SourceEnd,
			DestinationStart,
			TimelineEnd,
			SourceFade,
			DestinationFade
		}

		private enum HitKind
		{
			None,
			Instrument,
			InstrumentStart,
			InstrumentEnd,
			Marker,
			MarkerEnd,
			TransitionBadge,
			TransitionHeader,
			Lane,
			Logic,
			SourceEnd,
			DestinationStart,
			TimelineEnd,
			SourceFade,
			DestinationFade
		}

		private struct Hit
		{
			public HitKind Kind;
			public string Id;
			public AudioTrack Track;
			public double Display;
			public Piece Piece;
		}

		private struct LogicItem
		{
			public HitKind Kind;
			public string Id;
			public NVector2 Min;
			public NVector2 Max;
		}

		/// <summary>The inline transition timeline's mapping (rebuilt every frame).</summary>
		private struct TransitionView
		{
			public bool Active;
			public TransitionMarker Marker;
			public TransitionTimeline Timeline;

			/// <summary>Event time the transition leaves from (the marker, or a region's end).</summary>
			public double Source;

			/// <summary>Event time of the destination marker; -1 when the transition goes to the event's end.</summary>
			public double Destination;
		}

		private struct GridLine
		{
			public float X;

			/// <summary>1 a beat (or a minor time step), 2 a bar (or a major time step).</summary>
			public int Level;

			/// <summary>The bar number at bar lines (1-based; counted from the transition inside a transition timeline).</summary>
			public int Bar;

			public Segment Segment;
		}

		private string currentSheet;
		private float zoom = 90f;
		private double scroll;
		private bool snap = true;
		private QuantizationInterval snapInterval = QuantizationInterval.Beat;
		private float bodyScroll;
		private readonly HashSet<string> openSheets = new HashSet<string>(StringComparer.Ordinal);
		private double previewStartPosition;

		/// <summary>The transition marker whose transition timeline is open inline (null: none).</summary>
		private string expandedTransitionId;

		private TransitionView view;

		private DragKind dragKind;
		private Segment dragSegment;
		private string dragId;
		private double dragStart0;
		private double dragLength0;
		private float dragMouseX0;
		private bool dragChanged;
		private byte[] dragSnapshot;

		private readonly List<Row> rows = new List<Row>();
		private readonly List<Piece> pieces = new List<Piece>();
		private readonly List<Piece> piecePool = new List<Piece>();
		private readonly List<double> rowEnds = new List<double>();
		private readonly List<LogicItem> logicItems = new List<LogicItem>();
		private readonly List<GridLine> gridLines = new List<GridLine>();
		private float canvasLeft;
		private float canvasRight;
		private double sheetMin;
		private double sheetMax;
		private Hit contextHit;
		private Hit hoverHit;
		private bool openEditorContext;
		private bool previewPaused;

		/// <summary>Set when the current sheet was changed by code (a test command, a menu), so the tab bar follows.</summary>
		private bool tabSwitchRequested;

		private string draggedTrackId;
		private AudioInstrument clipboardInstrument;

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- sheets and mapping

		private bool OnTimeline => string.IsNullOrEmpty(currentSheet);

		/// <summary>The current sheet's x axis is time (the timeline), not a parameter.</summary>
		private bool IsTimeSheet => OnTimeline;

		/// <summary>The transition marker open inline, or null.</summary>
		private TransitionMarker ExpandedTransition => expandedTransitionId != null ? SelectedEvent?.FindMarker(expandedTransitionId) as TransitionMarker : null;

		/// <summary>Pixels per unit of the current sheet (seconds, or parameter units).</summary>
		private double Scale => IsTimeSheet ? zoom : (canvasRight - canvasLeft) / Math.Max(1e-6, sheetMax - sheetMin);

		private double Origin => IsTimeSheet ? scroll : sheetMin;

		private float ToX(double value)
		{
			return canvasLeft + (float)((value - Origin) * Scale);
		}

		private double FromX(float x)
		{
			return Origin + (x - canvasLeft) / Scale;
		}

		/// <summary>Display position of a value of a segment (the identity unless a transition is open).</summary>
		private double Display(Segment segment, double value)
		{
			if (!view.Active)
			{
				return value;
			}

			switch (segment)
			{
				case Segment.Transition:
					return view.Source + value;
				case Segment.Destination:
					return view.Source + view.Timeline.DestinationStart + (value - view.Destination);
				default:
					return value;
			}
		}

		/// <summary>The segment under a display position and the position in its units.</summary>
		private Segment SegmentAt(double display, out double value)
		{
			if (!view.Active || display < view.Source)
			{
				value = display;
				return Segment.Event;
			}

			if (display < view.Source + view.Timeline.Length || view.Destination < 0.0)
			{
				value = display - view.Source;
				return Segment.Transition;
			}

			value = view.Destination + (display - view.Source - view.Timeline.DestinationStart);
			return Segment.Destination;
		}

		/// <summary>Display offset of a segment (display = value + offset).</summary>
		private double SegmentOffset(Segment segment)
		{
			return segment == Segment.Event || !view.Active ? 0.0 : segment == Segment.Transition ? view.Source : view.Source + view.Timeline.DestinationStart - view.Destination;
		}

		/// <summary>Snaps a value of a segment: to the event's beat grid (the tempo at the transition inside a transition timeline), else to a time step.</summary>
		private double SnapValue(Segment segment, double value, AudioEvent current)
		{
			if (!snap)
			{
				return value;
			}

			if (!IsTimeSheet)
			{
				double step = (sheetMax - sheetMin) / 100.0;
				return sheetMin + Math.Round((value - sheetMin) / step) * step;
			}

			List<TempoMarker> tempos = current.TempoMarkers;
			if (tempos.Count > 0 && snapInterval != QuantizationInterval.None)
			{
				var grid = new BeatGrid(tempos);
				if (segment == Segment.Transition)
				{
					double interval = grid.IntervalSeconds(snapInterval, view.Source);
					if (interval > 1e-6)
					{
						return Math.Max(0.0, Math.Round(value / interval) * interval);
					}
				}
				else
				{
					return Math.Max(0.0, grid.Snap(snapInterval, value));
				}
			}

			double seconds = NiceStep(zoom, 24f);
			return Math.Max(0.0, Math.Round(value / seconds) * seconds);
		}

		/// <summary>A step in seconds at least <paramref name="minPixels" /> wide at the zoom.</summary>
		private static double NiceStep(float pixelsPerSecond, float minPixels)
		{
			double[] steps = { 0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
			foreach (double step in steps)
			{
				if (step * pixelsPerSecond >= minPixels)
				{
					return step;
				}
			}

			return 600;
		}

		/// <summary>Where the transition leaves from: the marker, or a region's end.</summary>
		private static double SourcePosition(TransitionMarker transition)
		{
			return transition is TransitionRegion region ? region.End : transition.Position;
		}

		/// <summary>Opens a transition's timeline inline in the timeline (creating the timeline when the marker has none).</summary>
		private void OpenTransitionTimeline(TransitionMarker transition)
		{
			if (transition == null)
			{
				return;
			}

			if (transition.Timeline == null)
			{
				AddTransitionTimeline(transition);
			}

			expandedTransitionId = transition.Id;
			selection = Selection.Of(SelectionKind.Marker, transition.Id);
			if (!OnTimeline)
			{
				currentSheet = null;
				tabSwitchRequested = true;
			}

			// Bring the transition into view.
			double at = SourcePosition(transition);
			double visible = (canvasRight - canvasLeft) / Math.Max(1f, zoom);
			if (canvasRight > canvasLeft && (at < scroll || at + transition.Timeline.Length > scroll + visible))
			{
				scroll = Math.Max(0.0, at - visible * 0.3);
			}
		}

		private void CloseTransitionTimeline()
		{
			expandedTransitionId = null;
		}

		/// <summary>Gives a transition a timeline of one bar (or second) with a plain crossfade over the whole of it.</summary>
		private void AddTransitionTimeline(TransitionMarker transition)
		{
			AudioEvent current = SelectedEvent;
			double length = current != null ? Math.Max(transition.Crossfade, DefaultLength(current)) : 1.0;
			var timeline = new TransitionTimeline
			{
				Length = length,
				SourceLength = length,
				DestinationStart = 0.0,
				SourceFadeOut = new AudioFade { Length = length },
				DestinationFadeIn = new AudioFade { Length = length }
			};
			Edit("Add transition timeline", () => transition.Timeline = timeline);
		}

		private void RemoveTransitionTimeline(TransitionMarker transition)
		{
			if (transition == null)
			{
				return;
			}

			Edit("Remove transition timeline", () =>
			{
				transition.Timeline = null;
				// Its instruments go with it.
				string sheet = TransitionTimeline.SheetId(transition.Id);
				foreach (AudioTrack track in SelectedEvent.Tracks)
				{
					track.Instruments.RemoveAll(i => i.Sheet == sheet);
				}
			});
			if (expandedTransitionId == transition.Id)
			{
				expandedTransitionId = null;
			}
		}

		private void BuildTransitionView(AudioEvent current)
		{
			view = default;
			if (!OnTimeline || expandedTransitionId == null)
			{
				return;
			}

			if (!(current.FindMarker(expandedTransitionId) is TransitionMarker marker) || marker.Timeline == null)
			{
				expandedTransitionId = null;
				return;
			}

			view.Active = true;
			view.Marker = marker;
			view.Timeline = marker.Timeline;
			view.Source = SourcePosition(marker);
			AudioMarker destination = marker.DestinationId == TransitionMarker.EndDestination ? null : current.FindMarker(marker.DestinationId);
			view.Destination = destination != null ? destination.Position : -1.0;
		}

		// ---------------------------------------------------------------- panel

		private void DrawEventEditor()
		{
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(4f, 4f));
			bool open = ImGui.Begin("Event Editor", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			ImGui.PopStyleVar();
			if (open)
			{
				AudioEvent current = SelectedEvent;
				UpdateLive(current);
				if (current == null)
				{
					ImGui.TextDisabled(Project == null ? "No audio project open." : "Select an event in the Events panel, or create one.");
				}
				else
				{
					if (expandedTransitionId != null && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape, false) && !ImGui.GetIO().WantTextInput)
					{
						CloseTransitionTimeline();
					}

					DrawTransport(current);
					DrawSheetTabs(current);
					DrawBody(current);
				}
			}

			ImGui.End();
		}

		private void DrawSheetTabs(AudioEvent current)
		{
			NVector2 tabsOrigin = ImGui.GetCursorScreenPos();
			float tabsWidth = ImGui.GetContentRegionAvail().X;
			if (!ImGui.BeginTabBar("##sheets", ImGuiTabBarFlags.AutoSelectNewTabs))
			{
				return;
			}

			if (EditorGui.BeginTabItem("Timeline", OnTimeline && tabSwitchRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
			{
				if (!OnTimeline && !tabSwitchRequested)
				{
					currentSheet = null;
				}

				ImGui.EndTabItem();
			}

			// A sheet exists for every parameter with instruments on it, plus the ones opened explicitly.
			var sheets = new List<AudioParameter>();
			foreach (AudioParameter parameter in Project.ParametersOf(current))
			{
				bool used = openSheets.Contains(parameter.Id) || parameter.Id == currentSheet;
				if (!used)
				{
					foreach (AudioTrack track in current.Tracks)
					{
						foreach (AudioInstrument instrument in track.Instruments)
						{
							if (instrument.Sheet == parameter.Id)
							{
								used = true;
							}
						}
					}
				}

				if (used)
				{
					sheets.Add(parameter);
				}
			}

			foreach (AudioParameter parameter in sheets)
			{
				ImGuiTabItemFlags flags = currentSheet == parameter.Id && tabSwitchRequested ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
				if (EditorGui.BeginTabItem(parameter.Name + "##sheet_" + parameter.Id, flags))
				{
					if (currentSheet != parameter.Id && !tabSwitchRequested)
					{
						currentSheet = parameter.Id;
					}

					ImGui.EndTabItem();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Parameter sheet: instruments trigger while '" + parameter.Name + "' is inside their range.\nDrag on the ruler to set the value. Right click: close the sheet.");
				}

				if (EditorGui.BeginPopupContextItem("##sheet_menu_" + parameter.Id))
				{
					if (EditorMenu.Item("Close Sheet"))
					{
						openSheets.Remove(parameter.Id);
						if (currentSheet == parameter.Id)
						{
							currentSheet = null;
							tabSwitchRequested = true;
						}
					}

					ImGui.EndPopup();
				}
			}

			if (ImGui.TabItemButton("+##addsheet", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
			{
				ImGui.OpenPopup("##add_sheet_popup");
			}

			if (ImGui.BeginPopup("##add_sheet_popup"))
			{
				ImGui.TextDisabled("Add a parameter sheet");
				ImGui.Separator();
				bool any = false;
				foreach (AudioParameter parameter in Project.ParametersOf(current))
				{
					if (sheets.Contains(parameter))
					{
						continue;
					}

					any = true;
					if (EditorMenu.Item(parameter.Name))
					{
						openSheets.Add(parameter.Id);
						currentSheet = parameter.Id;
						tabSwitchRequested = true;
					}
				}

				if (!any)
				{
					ImGui.TextDisabled("Every parameter has a sheet. Add parameters in the Overview.");
				}

				ImGui.EndPopup();
			}

			ImGui.EndTabBar();
			tabSwitchRequested = false;

			// Snapping and zoom at the right end of the tabs' row.
			NVector2 after = ImGui.GetCursorScreenPos();
			float right = tabsOrigin.X + tabsWidth;
			DrawSnapControls(current, Math.Max(tabsOrigin.X + 260f, right - SnapControlsWidth), tabsOrigin.Y - 1f);
			ImGui.SetCursorScreenPos(after);
		}

		private void DrawBody(AudioEvent current)
		{
			NVector2 avail = ImGui.GetContentRegionAvail();
			if (avail.X < 120f || avail.Y < 90f)
			{
				// The sheet tabs left the cursor set below them: an item ends that before the window does (ImGui asserts
				// on a window ending with the cursor set past its content, e.g. on the studio's first frame, before the
				// dock layout gives the window its size).
				ImGui.Dummy(NVector2.Zero);
				return;
			}

			NVector2 origin = ImGui.GetCursorScreenPos();
			canvasLeft = origin.X + HeaderWidth;
			canvasRight = origin.X + avail.X - 2f;
			if (!IsTimeSheet)
			{
				AudioParameter sheet = Project.FindParameter(current, currentSheet);
				if (sheet == null)
				{
					currentSheet = null;
					tabSwitchRequested = true;
				}
				else
				{
					sheetMin = Math.Min(sheet.Min, sheet.Max);
					sheetMax = Math.Max(sheet.Min, sheet.Max);
					if (sheetMax - sheetMin < 1e-6)
					{
						sheetMax = sheetMin + 1.0;
					}
				}
			}

			BuildTransitionView(current);

			// Rows below the ruler.
			rows.Clear();
			float y = 0f;
			if (IsTimeSheet)
			{
				rows.Add(new Row { Kind = RowKind.Logic, Y = y, Height = LogicHeight, TrackIndex = -1 });
				y += LogicHeight;
			}

			for (int i = 0; i < current.Tracks.Count; i++)
			{
				rows.Add(new Row { Kind = RowKind.Track, Track = current.Tracks[i], TrackIndex = i, Y = y, Height = TrackHeight });
				y += TrackHeight;
			}

			rows.Add(new Row { Kind = RowKind.Master, Track = current.Master, TrackIndex = -1, Y = y, Height = MasterHeight });
			y += MasterHeight;
			float totalHeight = y + 34f;
			float minimap = IsTimeSheet ? MinimapHeight : 0f;
			float lanesHeight = Math.Max(20f, avail.Y - RulerHeight - minimap);

			CollectGridLines(current);
			DrawRuler(current, origin, avail.X);
			ImGui.SetCursorScreenPos(new NVector2(origin.X, origin.Y + RulerHeight));
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, NVector2.Zero);
			bool bodyOpen = ImGui.BeginChild("##lanes", new NVector2(avail.X, lanesHeight), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
			ImGui.PopStyleVar();
			if (bodyOpen)
			{
				bodyScroll = Math.Clamp(bodyScroll, 0f, Math.Max(0f, totalHeight - lanesHeight));
				NVector2 lanesOrigin = ImGui.GetCursorScreenPos() - new NVector2(0f, bodyScroll);
				ImDrawListPtr draw = ImGui.GetWindowDrawList();
				LayoutPieces(current, lanesOrigin);

				// Headers first: their widgets take the mouse before the canvas does.
				DrawHeaders(current, lanesOrigin, draw);

				// The canvas: one invisible button over every lane.
				ImGui.SetCursorScreenPos(new NVector2(canvasLeft, lanesOrigin.Y));
				EditorGui.InvisibleButton("##lanes_canvas", new NVector2(Math.Max(1f, canvasRight - canvasLeft), Math.Max(1f, totalHeight)), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
				bool canvasHovered = ImGui.IsItemHovered();
				var clipMin = new NVector2(canvasLeft, origin.Y + RulerHeight);
				var clipMax = new NVector2(canvasRight, origin.Y + RulerHeight + lanesHeight);
				draw.PushClipRect(clipMin, clipMax, true);
				DrawLaneBackgrounds(lanesOrigin, draw);
				DrawGrid(draw, lanesOrigin);
				foreach (Piece piece in pieces)
				{
					DrawPiece(piece, draw);
				}

				logicItems.Clear();
				if (IsTimeSheet)
				{
					DrawMarkerLines(current, draw, lanesOrigin);
					DrawLogic(current, draw, lanesOrigin.Y + rows[0].Y);
				}

				DrawEventEnd(current, draw, lanesOrigin);
				DrawPlayheads(current, draw, clipMin.Y, clipMax.Y);
				draw.PopClipRect();

				HandleDrop(current, lanesOrigin);
				if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
				{
					HandleWheel(lanesHeight, totalHeight);
				}

				HandleMouse(current, lanesOrigin, canvasHovered);
				DrawContextMenu(current);
			}

			ImGui.EndChild();
			if (IsTimeSheet)
			{
				DrawMinimap(current, new NVector2(origin.X, origin.Y + RulerHeight + lanesHeight), avail.X);
			}
		}

		// ---------------------------------------------------------------- pieces

		private Piece AddPiece(PieceKind kind, AudioInstrument instrument, AudioTrack track, int trackIndex, double from, double to)
		{
			Piece piece;
			if (pieces.Count < piecePool.Count)
			{
				piece = piecePool[pieces.Count];
			}
			else
			{
				piece = new Piece();
				piecePool.Add(piece);
			}

			piece.Kind = kind;
			piece.Instrument = instrument;
			piece.Track = track;
			piece.TrackIndex = trackIndex;
			piece.From = from;
			piece.To = Math.Max(to, from);
			piece.ContentOffset = 0.0;
			piece.CutStart = false;
			piece.CutEnd = false;
			piece.EndsAtSourceLength = false;
			piece.StartsAtDestination = false;
			piece.Row = 0;
			piece.Rows = 1;
			pieces.Add(piece);
			return piece;
		}

		/// <summary>The pieces of every track lane, stacked where they overlap, with their screen rectangles.</summary>
		private void LayoutPieces(AudioEvent current, NVector2 lanesOrigin)
		{
			pieces.Clear();
			foreach (Row row in rows)
			{
				if (row.Kind != RowKind.Track)
				{
					continue;
				}

				int first = pieces.Count;
				CollectPieces(row.Track, row.TrackIndex);
				int count = pieces.Count - first;
				StackPieces(first, count);
				float top = lanesOrigin.Y + row.Y + 3f;
				float height = row.Height - 6f;
				for (int i = first; i < first + count; i++)
				{
					Piece piece = pieces[i];
					float rowHeight = height / piece.Rows;
					float y = top + piece.Row * rowHeight;
					float x0 = ToX(piece.From);
					float x1 = Math.Max(ToX(piece.To), x0 + 3f);
					piece.Min = new NVector2(x0, y + (piece.Row > 0 ? 1f : 0f));
					piece.Max = new NVector2(x1, y + rowHeight - (piece.Row < piece.Rows - 1 ? 1f : 0f));
				}
			}
		}

		private void CollectPieces(AudioTrack track, int trackIndex)
		{
			if (!IsTimeSheet)
			{
				foreach (AudioInstrument instrument in track.Instruments)
				{
					if (instrument.Sheet == currentSheet)
					{
						AddPiece(PieceKind.Normal, instrument, track, trackIndex, instrument.Start, instrument.End);
					}
				}

				return;
			}

			if (!view.Active)
			{
				foreach (AudioInstrument instrument in track.Instruments)
				{
					if (instrument.OnTimeline)
					{
						AddPiece(PieceKind.Normal, instrument, track, trackIndex, instrument.Start, instrument.End);
					}
				}

				return;
			}

			double p = view.Source;
			double d = view.Destination;
			TransitionTimeline timeline = view.Timeline;
			string sheet = TransitionTimeline.SheetId(view.Marker.Id);
			foreach (AudioInstrument instrument in track.Instruments)
			{
				if (instrument.Sheet == sheet)
				{
					AddPiece(PieceKind.TransitionClip, instrument, track, trackIndex, p + instrument.Start, p + instrument.End);
					continue;
				}

				if (!instrument.OnTimeline)
				{
					continue;
				}

				// Before the transition.
				if (instrument.Start < p)
				{
					Piece before = AddPiece(PieceKind.Normal, instrument, track, trackIndex, instrument.Start, Math.Min(instrument.End, p));
					before.CutEnd = instrument.End > p + 1e-9;
				}

				// Playing at the transition: timelocked sounds continue as the source until the source region ends.
				if (!instrument.Async && instrument.Start <= p && p < instrument.End)
				{
					double length = Math.Min(timeline.SourceLength, instrument.End - p);
					if (length > 1e-6)
					{
						Piece source = AddPiece(PieceKind.Source, instrument, track, trackIndex, p, p + length);
						source.ContentOffset = p - instrument.Start;
						source.CutStart = true;
						source.EndsAtSourceLength = length >= timeline.SourceLength - 1e-9;
						source.CutEnd = !source.EndsAtSourceLength;
					}
				}

				// At and after the destination, as the transition plays it.
				if (d >= 0.0 && instrument.End > d)
				{
					double from = Math.Max(instrument.Start, d);
					Piece destination = AddPiece(PieceKind.Destination, instrument, track, trackIndex, Display(Segment.Destination, from), Display(Segment.Destination, instrument.End));
					destination.ContentOffset = from - instrument.Start;
					destination.CutStart = instrument.Start < d - 1e-9;
					destination.StartsAtDestination = instrument.Start <= d + 1e-9;
				}
			}
		}

		/// <summary>Overlapping pieces of a lane share it: each cluster of overlaps gets as many rows as it needs.</summary>
		private void StackPieces(int first, int count)
		{
			if (count <= 0)
			{
				return;
			}

			pieces.Sort(first, count, PieceOrder.Instance);
			rowEnds.Clear();
			int clusterStart = first;
			double clusterEnd = double.NegativeInfinity;
			for (int i = first; i < first + count; i++)
			{
				Piece piece = pieces[i];
				if (i > clusterStart && piece.From >= clusterEnd - 1e-9)
				{
					for (int j = clusterStart; j < i; j++)
					{
						pieces[j].Rows = rowEnds.Count;
					}

					rowEnds.Clear();
					clusterStart = i;
				}

				int row = -1;
				for (int r = 0; r < rowEnds.Count; r++)
				{
					if (rowEnds[r] <= piece.From + 1e-9)
					{
						row = r;
						break;
					}
				}

				if (row < 0)
				{
					row = rowEnds.Count;
					rowEnds.Add(piece.To);
				}
				else
				{
					rowEnds[row] = piece.To;
				}

				piece.Row = row;
				clusterEnd = Math.Max(clusterEnd, piece.To);
			}

			for (int j = clusterStart; j < first + count; j++)
			{
				pieces[j].Rows = Math.Max(1, rowEnds.Count);
			}
		}

		// ---------------------------------------------------------------- headers

		private void DrawHeaders(AudioEvent current, NVector2 lanesOrigin, ImDrawListPtr draw)
		{
			foreach (Row row in rows)
			{
				var min = new NVector2(lanesOrigin.X, lanesOrigin.Y + row.Y);
				var max = new NVector2(canvasLeft - 1f, min.Y + row.Height);
				ImGui.PushID(row.Kind + (row.Track?.Id ?? ""));
				switch (row.Kind)
				{
					case RowKind.Logic:
						DrawLogicHeader(current, min, max, draw);
						break;
					case RowKind.Track:
						DrawTrackHeader(current, row.Track, min, max, draw);
						break;
					case RowKind.Master:
						DrawMasterHeader(current, min, max, draw);
						break;
				}

				draw.AddLine(new NVector2(min.X, max.Y - 0.5f), new NVector2(max.X, max.Y - 0.5f), StudioUi.LaneLine);
				ImGui.PopID();
			}

			// Below the rows: add a track.
			float addY = lanesOrigin.Y + (rows.Count > 0 ? rows[rows.Count - 1].Y + rows[rows.Count - 1].Height : 0f) + 6f;
			ImGui.SetCursorScreenPos(new NVector2(lanesOrigin.X + 8f, addY));
			if (EditorGui.SmallButton("+ Track"))
			{
				Edit("Add track", () => current.Tracks.Add(AudioProject.CreateTrack("Audio " + (current.Tracks.Count + 1))));
			}
		}

		private void DrawLogicHeader(AudioEvent current, NVector2 min, NVector2 max, ImDrawListPtr draw)
		{
			draw.AddRectFilled(min, max, StudioUi.Shade(StudioUi.TrackHeader, 0.8f));
			draw.AddText(min + new NVector2(10f, 5f), StudioUi.Text, "Logic Tracks");
			List<TempoMarker> tempos = current.TempoMarkers;
			string tempo = tempos.Count > 0 ? $"{tempos[0].Bpm:0.#} bpm  {tempos[0].BeatsPerBar}/{tempos[0].BeatUnit}" + (tempos.Count > 1 ? "  (" + tempos.Count + " tempos)" : "") : "no tempo";
			draw.AddText(min + new NVector2(10f, 24f), StudioUi.TextDim, tempo);
			if (view.Active)
			{
				ImGui.SetCursorScreenPos(min + new NVector2(10f, 46f));
				if (EditorGui.SmallButton("Close transition"))
				{
					CloseTransitionTimeline();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Back to the plain timeline (Esc). The transition timeline stays; its badge on the marker opens it again.");
				}
			}
			else
			{
				draw.AddText(min + new NVector2(10f, 43f), StudioUi.TextDim, "right click the lane: add");
			}
		}

		private void DrawTrackHeader(AudioEvent current, AudioTrack track, NVector2 min, NVector2 max, ImDrawListPtr draw)
		{
			bool selected = selection.Is(SelectionKind.Track, track.Id);
			draw.AddRectFilled(min, max, selected ? StudioUi.TrackHeaderSelected : StudioUi.TrackHeader);
			draw.AddRectFilled(min, new NVector2(min.X + 4f, max.Y), Color(track.Color));
			float controlsX = max.X - 118f;

			// Name.
			ImGui.SetCursorScreenPos(min + new NVector2(10f, 5f));
			if (EditorGui.Selectable(track.Name + "##trackname", selected, ImGuiSelectableFlags.AllowDoubleClick, new NVector2(Math.Max(20f, controlsX - min.X - 16f), 0f)))
			{
				selection = Selection.Of(SelectionKind.Track, track.Id);
				if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				{
					RequestRename(Selection.Of(SelectionKind.Track, track.Id), track.Name);
				}
			}

			if (ImGui.BeginDragDropSource())
			{
				draggedTrackId = track.Id;
				unsafe
				{
					ImGui.SetDragDropPayload(TrackPayload, null, 0);
				}

				ImGui.TextUnformatted(track.Name);
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(TrackPayload).IsNull && draggedTrackId != null && draggedTrackId != track.Id)
				{
					AudioTrack moved = current.FindTrack(draggedTrackId);
					draggedTrackId = null;
					if (moved != null && moved != current.Master)
					{
						Edit("Reorder tracks", () =>
						{
							current.Tracks.Remove(moved);
							current.Tracks.Insert(current.Tracks.IndexOf(track), moved);
						});
					}
				}

				ImGui.EndDragDropTarget();
			}

			if (EditorGui.BeginPopupContextItem("##track_menu"))
			{
				DrawTrackMenu(current, track);
				ImGui.EndPopup();
			}

			// What the track holds, under the name.
			int count = 0;
			foreach (AudioInstrument instrument in track.Instruments)
			{
				if ((IsTimeSheet && (instrument.OnTimeline || instrument.OnTransition)) || (!IsTimeSheet && instrument.Sheet == currentSheet))
				{
					count++;
				}
			}

			string info = count + (count == 1 ? " instrument" : " instruments");
			if (track.Effects.Count > 0) info += ", " + track.Effects.Count + " fx";
			if (track.Automation.Count > 0) info += ", automated";
			draw.PushClipRect(min, new NVector2(controlsX - 4f, max.Y), true);
			draw.AddText(min + new NVector2(12f, 28f), StudioUi.TextDim, info);
			if (Math.Abs(track.Pan) > 0.005f || Math.Abs(track.PitchSemitones) > 0.005f)
			{
				draw.AddText(min + new NVector2(12f, 46f), StudioUi.TextDim, "pan " + StudioUi.FormatPan(track.Pan) + (Math.Abs(track.PitchSemitones) > 0.005f ? ", " + StudioUi.FormatSemitones(track.PitchSemitones) : ""));
			}

			draw.PopClipRect();

			// SOLO, MUTE.
			ImGui.SetCursorScreenPos(new NVector2(controlsX, min.Y + 10f));
			if (StudioUi.TextToggle("##solo", "SOLO", track.Solo, StudioUi.Yellow, new NVector2(42f, 18f), "Solo: hear only the soloed tracks (auditioning in the editor; game builds play every track)"))
			{
				bool solo = track.Solo;
				Edit("Toggle solo", () => track.Solo = !solo);
			}

			ImGui.SetCursorScreenPos(new NVector2(controlsX, min.Y + 32f));
			if (StudioUi.TextToggle("##mute", "MUTE", track.Mute, StudioUi.LoopBlue, new NVector2(42f, 18f), "Mute the track"))
			{
				bool mute = track.Mute;
				Edit("Toggle track mute", () => track.Mute = !mute);
			}

			// Volume knob and meter.
			ImGui.SetCursorScreenPos(new NVector2(controlsX + 44f, min.Y + 6f));
			float volume = track.VolumeDb;
			bool changed = StudioUi.Knob("##vol", ref volume, AudioMath.SilenceDb, AudioMath.MaxDb, 0f, null, StudioUi.FormatDb(track.VolumeDb), StudioUi.KnobScale.Volume, false, 32f, 58f, false, "Track volume");
			Track(changed);
			if (changed) track.VolumeDb = volume;
			StudioUi.Meter(draw, new NVector2(max.X - 11f, min.Y + 6f), new NVector2(max.X - 4f, max.Y - 6f), TrackLevel(track.Id));
		}

		private void DrawTrackMenu(AudioEvent current, AudioTrack track)
		{
			if (EditorMenu.Item("Rename...")) RequestRename(Selection.Of(SelectionKind.Track, track.Id), track.Name);
			if (EditorMenu.Item("Add Track Below"))
			{
				Edit("Add track", () => current.Tracks.Insert(current.Tracks.IndexOf(track) + 1, AudioProject.CreateTrack("Audio " + (current.Tracks.Count + 1))));
			}

			if (EditorMenu.Item("Duplicate Track"))
			{
				Edit("Duplicate track", () =>
				{
					AudioEvent clone = EventCompiler.CloneEvent(current);
					AudioTrack copy = clone.FindTrack(track.Id);
					if (copy != null)
					{
						copy.Id = AudioProject.NewId();
						copy.Name += " copy";
						foreach (AudioInstrument instrument in copy.Instruments) instrument.Id = AudioProject.NewId();
						foreach (AudioEffect effect in copy.Effects) effect.Id = AudioProject.NewId();
						current.Tracks.Insert(current.Tracks.IndexOf(track) + 1, copy);
					}
				});
			}

			ImGui.Separator();
			if (EditorMenu.Item("Delete Track")) Edit("Delete track", () => current.Tracks.Remove(track));
		}

		private void DrawMasterHeader(AudioEvent current, NVector2 min, NVector2 max, ImDrawListPtr draw)
		{
			bool selected = selection.Kind == SelectionKind.Master;
			draw.AddRectFilled(min, max, selected ? StudioUi.TrackHeaderSelected : StudioUi.Shade(StudioUi.TrackHeader, 0.88f));
			draw.AddRectFilled(min, new NVector2(min.X + 4f, max.Y), StudioUi.Rgb(0.6f, 0.6f, 0.62f));
			float controlsX = max.X - 118f;
			ImGui.SetCursorScreenPos(min + new NVector2(10f, 5f));
			if (EditorGui.Selectable("Master##master", selected, ImGuiSelectableFlags.None, new NVector2(Math.Max(20f, controlsX - min.X - 16f), 0f)))
			{
				selection = Selection.Of(SelectionKind.Master, current.Master.Id);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("The event's master track: its volume, pitch, effects, automation, routing and bank.");
			}

			draw.AddText(min + new NVector2(12f, 28f), StudioUi.TextDim, Project.FindBus(current.BusId) != null ? "to " + Project.BusPath(Project.FindBus(current.BusId)) : "to Master");
			ImGui.SetCursorScreenPos(new NVector2(controlsX, min.Y + 10f));
			if (StudioUi.TextToggle("##mute", "MUTE", current.Master.Mute, StudioUi.LoopBlue, new NVector2(42f, 18f), "Mute the whole event"))
			{
				bool mute = current.Master.Mute;
				Edit("Toggle master mute", () => current.Master.Mute = !mute);
			}

			ImGui.SetCursorScreenPos(new NVector2(controlsX + 50f, min.Y + 3f));
			float volume = current.Master.VolumeDb;
			bool changed = StudioUi.Knob("##mvol", ref volume, AudioMath.SilenceDb, AudioMath.MaxDb, 0f, null, StudioUi.FormatDb(current.Master.VolumeDb), StudioUi.KnobScale.Volume, false, 26f, 50f, false, "Event volume");
			Track(changed);
			if (changed) current.Master.VolumeDb = volume;
			StudioUi.Meter(draw, new NVector2(max.X - 11f, min.Y + 5f), new NVector2(max.X - 4f, max.Y - 5f), masterLevel);
		}

		// ---------------------------------------------------------------- ruler and grid

		private void CollectGridLines(AudioEvent current)
		{
			gridLines.Clear();
			if (!IsTimeSheet)
			{
				return;
			}

			double from = FromX(canvasLeft);
			double to = FromX(canvasRight);
			if (!view.Active)
			{
				AddGridSegment(current, Segment.Event, from, to);
				return;
			}

			AddGridSegment(current, Segment.Event, from, Math.Min(to, view.Source));
			AddGridSegment(current, Segment.Transition, Math.Max(from, view.Source), Math.Min(to, view.Source + view.Timeline.Length));
			if (view.Destination >= 0.0)
			{
				AddGridSegment(current, Segment.Destination, Math.Max(from, view.Source + view.Timeline.Length), to);
			}
		}

		private void AddGridSegment(AudioEvent current, Segment segment, double displayFrom, double displayTo)
		{
			if (displayTo <= displayFrom)
			{
				return;
			}

			double offset = SegmentOffset(segment);
			double from = Math.Max(0.0, displayFrom - offset);
			double to = displayTo - offset;
			List<TempoMarker> tempos = current.TempoMarkers;
			if (tempos.Count > 0)
			{
				var grid = new BeatGrid(tempos);
				TempoMarker first = segment == Segment.Transition ? grid.At(view.Source) : tempos[0];
				double beatPixels = zoom * first.SecondsPerBeat;
				int perBar = Math.Max(1, first.BeatsPerBar);
				bool beats = beatPixels >= 5f;
				if (!beats && beatPixels * perBar < 4f)
				{
					// Bars too dense: plain time steps.
					AddTimeSteps(segment, from, to, offset);
					return;
				}

				int guard = 0;
				if (segment == Segment.Transition)
				{
					double step = beats ? first.SecondsPerBeat : first.SecondsPerBeat * perBar;
					long index = (long)Math.Ceiling(from / step - 1e-9);
					for (; index * step <= to + 1e-9 && guard++ < 4000; index++)
					{
						bool bar = !beats || index % perBar == 0;
						gridLines.Add(new GridLine { X = ToX(index * step + offset), Level = bar ? 2 : 1, Bar = bar ? (int)(beats ? index / perBar : index) + 1 : 0, Segment = segment });
					}

					return;
				}

				QuantizationInterval interval = beats ? QuantizationInterval.Beat : QuantizationInterval.Bar;
				double t = grid.NextGridPoint(interval, from, 0.0);
				while (t <= to + 1e-9 && guard++ < 4000)
				{
					grid.BarBeat(t + 1e-6, out int bar, out int beat, out _);
					bool barLine = beat == 1;
					gridLines.Add(new GridLine { X = ToX(t + offset), Level = barLine ? 2 : 1, Bar = barLine ? bar : 0, Segment = segment });
					double next = grid.NextGridPoint(interval, t + 1e-6, 0.0);
					if (next <= t)
					{
						break;
					}

					t = next;
				}

				return;
			}

			AddTimeSteps(segment, from, to, offset);
		}

		private void AddTimeSteps(Segment segment, double from, double to, double offset)
		{
			double step = NiceStep(zoom, 64f);
			double first = Math.Floor(from / step) * step;
			int guard = 0;
			for (double v = first; v <= to + 1e-9 && guard++ < 2000; v += step)
			{
				if (v >= from - 1e-9)
				{
					gridLines.Add(new GridLine { X = ToX(v + offset), Level = 2, Segment = segment });
				}

				if (step * zoom >= 32f)
				{
					for (int k = 1; k < 4; k++)
					{
						double minor = v + step * k / 4.0;
						if (minor >= from && minor <= to)
						{
							gridLines.Add(new GridLine { X = ToX(minor + offset), Level = 1, Segment = segment });
						}
					}
				}
			}
		}

		private static string FormatRulerTime(double seconds, double step)
		{
			int minutes = (int)Math.Floor(seconds / 60.0);
			double rest = seconds - minutes * 60.0;
			if (step >= 1.0)
			{
				return minutes + ":" + Math.Round(rest).ToString("00");
			}

			return minutes + ":" + rest.ToString(step >= 0.1 ? "00.0" : step >= 0.01 ? "00.00" : "00.000");
		}

		private void DrawRuler(AudioEvent current, NVector2 origin, float width)
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			var min = origin;
			var max = new NVector2(origin.X + width, origin.Y + RulerHeight);
			draw.AddRectFilled(min, max, StudioUi.Ruler);
			draw.AddRectFilled(min, new NVector2(canvasLeft - 1f, max.Y), StudioUi.Shade(StudioUi.TrackHeader, 0.8f));
			AudioParameter parameter = IsTimeSheet ? null : Project.FindParameter(current, currentSheet);
			string title = IsTimeSheet ? (showBeats && current.TempoMarkers.Count > 0 ? "Bars" : "Time") : parameter?.Name ?? "";
			draw.AddText(min + new NVector2(10f, 5f), StudioUi.TextDim, title);
			draw.AddLine(new NVector2(min.X, max.Y - 0.5f), new NVector2(max.X, max.Y - 0.5f), StudioUi.LaneLine);
			draw.PushClipRect(new NVector2(canvasLeft, min.Y), max, true);
			if (IsTimeSheet)
			{
				if (view.Active)
				{
					float t0 = ToX(view.Source);
					float t1 = ToX(view.Source + view.Timeline.Length);
					draw.AddRectFilled(new NVector2(t0, min.Y), new NVector2(t1, max.Y), StudioUi.Rgba(0.35f, 0.5f, 0.85f, 0.35f));
				}

				if (showBeats && current.TempoMarkers.Count > 0)
				{
					DrawBarTicks(current, draw, min.Y, max.Y);
				}
				else
				{
					DrawTimeTicks(draw, min.Y, max.Y);
				}
			}
			else if (parameter != null)
			{
				double range = sheetMax - sheetMin;
				for (int i = 0; i <= 20; i++)
				{
					double v = sheetMin + range * i / 20.0;
					float x = ToX(v);
					bool major = i % 2 == 0;
					draw.AddLine(new NVector2(x, max.Y - (major ? 11f : 5f)), new NVector2(x, max.Y), StudioUi.RulerTick);
					if (major)
					{
						draw.AddText(new NVector2(x + 3f, min.Y + 3f), StudioUi.RulerText, ParameterValueText(parameter, (float)v));
					}
				}
			}

			DrawRulerCursor(current, draw, min.Y, max.Y);
			draw.PopClipRect();

			// Clicks on the ruler move the playback position (timeline) or the parameter (parameter sheets).
			ImGui.SetCursorScreenPos(new NVector2(canvasLeft, min.Y));
			EditorGui.InvisibleButton("##ruler", new NVector2(Math.Max(1f, canvasRight - canvasLeft), RulerHeight));
			if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				double display = FromX(ImGui.GetMousePos().X);
				if (IsTimeSheet)
				{
					Segment segment = SegmentAt(display, out double value);
					if (segment != Segment.Transition)
					{
						value = SnapValue(segment, Math.Max(0.0, value), current);
						previewStartPosition = value;
						Preview?.SetTimelinePosition((float)value);
					}
				}
				else if (parameter != null && !IsLiveShown)
				{
					SetPreviewParameter(parameter.Id, parameter.Clamp((float)Math.Clamp(display, sheetMin, sheetMax)));
				}
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(IsTimeSheet ? "Click or drag: the playback position. Ctrl + wheel: zoom, Shift + wheel: scroll." : "Click or drag: the parameter's value for the preview.");
			}
		}

		private void DrawTimeTicks(ImDrawListPtr draw, float top, float bottom)
		{
			double step = NiceStep(zoom, 64f);
			DrawTimeTickSegment(draw, Segment.Event, top, bottom, step);
			if (view.Active)
			{
				DrawTimeTickSegment(draw, Segment.Transition, top, bottom, step);
				if (view.Destination >= 0.0)
				{
					DrawTimeTickSegment(draw, Segment.Destination, top, bottom, step);
				}
			}
		}

		private void DrawTimeTickSegment(ImDrawListPtr draw, Segment segment, float top, float bottom, double step)
		{
			double displayFrom = FromX(canvasLeft);
			double displayTo = FromX(canvasRight);
			if (view.Active)
			{
				switch (segment)
				{
					case Segment.Event:
						displayTo = Math.Min(displayTo, view.Source);
						break;
					case Segment.Transition:
						displayFrom = Math.Max(displayFrom, view.Source);
						displayTo = Math.Min(displayTo, view.Source + view.Timeline.Length);
						break;
					default:
						displayFrom = Math.Max(displayFrom, view.Source + view.Timeline.Length);
						break;
				}
			}

			if (displayTo <= displayFrom)
			{
				return;
			}

			double offset = SegmentOffset(segment);
			double from = Math.Max(0.0, displayFrom - offset);
			double to = displayTo - offset;
			double first = Math.Floor(from / step) * step;
			int guard = 0;
			for (double v = first; v <= to + 1e-9 && guard++ < 2000; v += step)
			{
				if (v >= from - 1e-9)
				{
					float x = ToX(v + offset);
					draw.AddLine(new NVector2(x, bottom - 11f), new NVector2(x, bottom), StudioUi.RulerTick);
					string label = segment == Segment.Transition ? "+" + FormatRulerTime(v, step) : FormatRulerTime(v, step);
					draw.AddText(new NVector2(x + 3f, top + 3f), segment == Segment.Transition ? StudioUi.Rgb(0.7f, 0.82f, 1f) : StudioUi.RulerText, label);
				}

				for (int k = 1; k < 4; k++)
				{
					double minor = v + step * k / 4.0;
					if (minor >= from && minor <= to)
					{
						float x = ToX(minor + offset);
						draw.AddLine(new NVector2(x, bottom - 5f), new NVector2(x, bottom), StudioUi.RulerTick);
					}
				}
			}
		}

		private void DrawBarTicks(AudioEvent current, ImDrawListPtr draw, float top, float bottom)
		{
			// Label every bar, or every 2nd, 4th... when bars get narrow.
			TempoMarker tempo = current.TempoMarkers[0];
			double barPixels = zoom * tempo.SecondsPerBeat * Math.Max(1, tempo.BeatsPerBar);
			int stride = 1;
			while (barPixels * stride < 34.0 && stride < 1024)
			{
				stride *= 2;
			}

			foreach (GridLine line in gridLines)
			{
				if (line.Level == 2)
				{
					draw.AddLine(new NVector2(line.X, bottom - 12f), new NVector2(line.X, bottom), StudioUi.RulerTick);
					if (line.Bar > 0 && (line.Bar - 1) % stride == 0)
					{
						string label = line.Segment == Segment.Transition ? "+" + (line.Bar - 1) : line.Bar.ToString();
						draw.AddText(new NVector2(line.X + 3f, top + 3f), line.Segment == Segment.Transition ? StudioUi.Rgb(0.7f, 0.82f, 1f) : StudioUi.RulerText, label);
					}
				}
				else
				{
					draw.AddLine(new NVector2(line.X, bottom - 5f), new NVector2(line.X, bottom), StudioUi.RulerTick);
				}
			}
		}

		/// <summary>The playhead's head on the ruler (or the parameter's cursor on a parameter sheet).</summary>
		private void DrawRulerCursor(AudioEvent current, ImDrawListPtr draw, float top, float bottom)
		{
			if (IsTimeSheet)
			{
				EventInstance shown = Shown;
				double display;
				if (shown != null)
				{
					if (!InstanceDisplay(shown, out display))
					{
						return;
					}
				}
				else
				{
					display = previewStartPosition <= view.Source || !view.Active ? previewStartPosition : -1.0;
					if (display < 0.0)
					{
						return;
					}
				}

				float x = ToX(display);
				uint color = IsLiveShown ? StudioUi.LivePlayhead : shown != null ? StudioUi.Playhead : StudioUi.Rgba(1f, 1f, 1f, 0.55f);
				draw.AddTriangleFilled(new NVector2(x - 6f, bottom - 10f), new NVector2(x + 6f, bottom - 10f), new NVector2(x, bottom), color);
				return;
			}

			AudioParameter parameter = Project.FindParameter(current, currentSheet);
			if (parameter == null)
			{
				return;
			}

			float value = ShownParameterValue(parameter);
			float cx = ToX(value);
			uint cursor = IsLiveShown ? StudioUi.LivePlayhead : StudioUi.Yellow;
			draw.AddTriangleFilled(new NVector2(cx - 6f, bottom - 10f), new NVector2(cx + 6f, bottom - 10f), new NVector2(cx, bottom), cursor);
		}

		private void DrawLaneBackgrounds(NVector2 lanesOrigin, ImDrawListPtr draw)
		{
			foreach (Row row in rows)
			{
				var min = new NVector2(canvasLeft, lanesOrigin.Y + row.Y);
				var max = new NVector2(canvasRight, min.Y + row.Height);
				uint background;
				switch (row.Kind)
				{
					case RowKind.Logic:
						background = StudioUi.LogicLane;
						break;
					case RowKind.Master:
						background = selection.Kind == SelectionKind.Master ? StudioUi.LaneSelected : StudioUi.Shade(StudioUi.LaneAlternate, 0.9f);
						break;
					default:
						background = selection.Is(SelectionKind.Track, row.Track.Id) ? StudioUi.LaneSelected : row.TrackIndex % 2 == 0 ? StudioUi.Lane : StudioUi.LaneAlternate;
						break;
				}

				draw.AddRectFilled(min, max, background);
				draw.AddLine(new NVector2(min.X, max.Y - 0.5f), new NVector2(max.X, max.Y - 0.5f), StudioUi.LaneLine);
			}

			if (view.Active && rows.Count > 0)
			{
				// The transition timeline is tinted from the logic track down.
				float top = lanesOrigin.Y + rows[0].Y;
				float bottom = lanesOrigin.Y + rows[rows.Count - 1].Y + rows[rows.Count - 1].Height;
				draw.AddRectFilled(new NVector2(ToX(view.Source), top), new NVector2(ToX(view.Source + view.Timeline.Length), bottom), StudioUi.TransitionTint);
			}
		}

		private void DrawGrid(ImDrawListPtr draw, NVector2 lanesOrigin)
		{
			if (rows.Count == 0)
			{
				return;
			}

			float top = lanesOrigin.Y + rows[0].Y;
			float bottom = lanesOrigin.Y + rows[rows.Count - 1].Y + rows[rows.Count - 1].Height;
			if (!IsTimeSheet)
			{
				double range = sheetMax - sheetMin;
				for (int i = 0; i <= 20; i++)
				{
					float x = ToX(sheetMin + range * i / 20.0);
					draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), i % 2 == 0 ? StudioUi.GridBar : StudioUi.GridBeat);
				}

				return;
			}

			foreach (GridLine line in gridLines)
			{
				draw.AddLine(new NVector2(line.X, top), new NVector2(line.X, bottom), line.Level == 2 ? StudioUi.GridBar : StudioUi.GridBeat);
			}

			// Zero line.
			float zero = ToX(0.0);
			draw.AddLine(new NVector2(zero, top), new NVector2(zero, bottom), StudioUi.Rgba(1f, 1f, 1f, 0.22f));
		}

		// ---------------------------------------------------------------- instruments

		private static uint InstrumentColor(AudioInstrument instrument)
		{
			switch (instrument)
			{
				case MultiInstrument _: return StudioUi.Rgb(0.5f, 0.5f, 0.9f);
				case ScattererInstrument _: return StudioUi.Rgb(0.26f, 0.68f, 0.62f);
				case EventInstrument _: return StudioUi.Rgb(0.88f, 0.58f, 0.26f);
				case SnapshotInstrument _: return StudioUi.Rgb(0.8f, 0.38f, 0.5f);
				default: return StudioUi.Rgb(0.37f, 0.6f, 0.9f);
			}
		}

		private void DrawPiece(Piece piece, ImDrawListPtr draw)
		{
			NVector2 min = piece.Min;
			NVector2 max = piece.Max;
			if (max.X < canvasLeft || min.X > canvasRight)
			{
				return;
			}

			AudioInstrument instrument = piece.Instrument;
			bool selected = selection.Is(SelectionKind.Instrument, instrument.Id);
			uint body = InstrumentColor(instrument);
			if (piece.Kind == PieceKind.Source || piece.Kind == PieceKind.Destination)
			{
				body = StudioUi.Shade(body, 0.86f);
			}

			if (selected)
			{
				body = StudioUi.Shade(body, 1.14f);
			}

			float height = max.Y - min.Y;
			float header = height >= 30f ? PieceHeaderHeight : height;
			draw.AddRectFilled(min, max, body, 3f);
			draw.AddRectFilled(min, new NVector2(max.X, min.Y + header), StudioUi.Shade(body, 0.7f), 3f, height > header ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll);
			draw.PushClipRect(min, max, true);
			if (height > header + 6f)
			{
				AudioClipReference clip = null;
				switch (instrument)
				{
					case SingleInstrument single: clip = single.Clip; break;
					case MultiInstrument multi when multi.Playlist.Count > 0: clip = multi.Playlist[0].Clip; break;
					case ScattererInstrument scatterer when scatterer.Playlist.Count > 0: clip = scatterer.Playlist[0].Clip; break;
				}

				if (clip != null && !clip.IsEmpty)
				{
					DrawWaveform(draw, clip, piece, new NVector2(min.X + 1f, min.Y + header + 2f), new NVector2(max.X - 1f, max.Y - 2f), StudioUi.Shade(body, 0.3f));
				}
			}

			// Transition fades over the pieces they shape.
			if (view.Active)
			{
				TransitionTimeline timeline = view.Timeline;
				if (piece.Kind == PieceKind.Source)
				{
					StudioUi.Fade(draw, ToX(view.Source + timeline.SourceLength - timeline.SourceFadeOut.Length), ToX(view.Source + timeline.SourceLength), min.Y + header, max.Y, timeline.SourceFadeOut.Shape, false, StudioUi.Rgba(1f, 1f, 1f, 0.9f), StudioUi.Rgba(0f, 0f, 0f, 0.32f));
				}
				else if (piece.Kind == PieceKind.Destination)
				{
					StudioUi.Fade(draw, ToX(view.Source + timeline.DestinationStart), ToX(view.Source + timeline.DestinationStart + timeline.DestinationFadeIn.Length), min.Y + header, max.Y, timeline.DestinationFadeIn.Shape, true, StudioUi.Rgba(1f, 1f, 1f, 0.9f), StudioUi.Rgba(0f, 0f, 0f, 0.32f));
				}
			}

			string label = instrument.DisplayName;
			string flags = (instrument.Async ? " A" : "") + (instrument.Loop ? " L" : "") + (instrument.Conditions.Count > 0 ? " ?" : "") + (instrument.Probability < 100f ? " %" : "") + (instrument.Quantization != QuantizationInterval.None ? " Q" : "");
			draw.AddText(new NVector2(min.X + 5f, min.Y + (header - ImGui.GetTextLineHeight()) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.95f), label);
			if (flags.Length > 0)
			{
				float flagsWidth = ImGui.CalcTextSize(flags).X;
				draw.AddText(new NVector2(Math.Max(min.X + 5f, max.X - flagsWidth - 4f), min.Y + (header - ImGui.GetTextLineHeight()) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.7f), flags);
			}

			draw.PopClipRect();

			// Cut edges: the instrument goes on (or came from) the other side of the transition.
			if (piece.CutStart)
			{
				DrawCutEdge(draw, min.X + 1f, min.Y + 2f, max.Y - 2f);
			}

			if (piece.CutEnd)
			{
				DrawCutEdge(draw, max.X - 1f, min.Y + 2f, max.Y - 2f);
			}

			draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 3f, ImDrawFlags.None, selected ? 2f : 1f);

			// Fade handles on the transition's pieces.
			if (view.Active)
			{
				TransitionTimeline timeline = view.Timeline;
				if (piece.Kind == PieceKind.Source && piece.EndsAtSourceLength)
				{
					float x = ToX(view.Source + timeline.SourceLength - timeline.SourceFadeOut.Length);
					StudioUi.Handle(draw, new NVector2(Math.Clamp(x, min.X + 4f, max.X - 4f), min.Y + 4f), HandleSize, StudioUi.Selected, IsHot(HitKind.SourceFade, piece));
				}
				else if (piece.Kind == PieceKind.Destination && piece.StartsAtDestination)
				{
					float x = ToX(view.Source + timeline.DestinationStart + timeline.DestinationFadeIn.Length);
					StudioUi.Handle(draw, new NVector2(Math.Clamp(x, min.X + 4f, max.X - 4f), min.Y + 4f), HandleSize, StudioUi.Selected, IsHot(HitKind.DestinationFade, piece));
				}
			}
		}

		private bool IsHot(HitKind kind, Piece piece)
		{
			return hoverHit.Kind == kind && (piece == null || hoverHit.Piece == piece);
		}

		private static void DrawCutEdge(ImDrawListPtr draw, float x, float top, float bottom)
		{
			for (float y = top; y < bottom; y += 6f)
			{
				draw.AddLine(new NVector2(x, y), new NVector2(x, Math.Min(bottom, y + 3f)), StudioUi.Rgba(1f, 1f, 1f, 0.75f), 1.5f);
			}
		}

		/// <summary>Peaks per second of the waveform cache (one bucket count per clip, so the clip's peak cache stays valid).</summary>
		private const int WaveformBucketsPerSecond = 200;

		/// <summary>The clip's waveform over a piece: at the sound's own duration on the timeline (repeated when looping), stretched on a parameter sheet.</summary>
		private void DrawWaveform(ImDrawListPtr draw, AudioClipReference reference, Piece piece, NVector2 min, NVector2 max, uint color)
		{
			AudioClip clip = AudioClip.Load(reference);
			if (clip == null || clip.Error != null || max.X - min.X < 4f || max.Y - min.Y < 4f)
			{
				return;
			}

			float midY = (min.Y + max.Y) * 0.5f;
			if (!clip.IsDecoded || clip.Duration <= 0.0)
			{
				// Not decoded yet (decodes on first use, or when the bank is built): a flat line.
				draw.AddLine(new NVector2(min.X, midY), new NVector2(max.X, midY), color);
				return;
			}

			int buckets = Math.Clamp((int)(clip.Duration * WaveformBucketsPerSecond), 64, 16384);
			float[] peaks = clip.GetPeaks(buckets);
			if (peaks == null)
			{
				return;
			}

			AudioInstrument instrument = piece.Instrument;
			float half = (max.Y - min.Y) * 0.5f;
			bool timeBased = IsTimeSheet;
			double clipLength = clip.Duration;
			double pieceLength = Math.Max(1e-6, piece.To - piece.From);
			double offset = timeBased ? piece.ContentOffset + instrument.StartOffset : 0.0;
			float x0 = ToX(piece.From);
			int left = (int)Math.Max(min.X, canvasLeft);
			int right = (int)Math.Min(max.X, canvasRight);
			for (int x = left; x < right; x++)
			{
				// Seconds into the clip at this column and the next.
				double a = (x - x0) / (double)Scale;
				double b = (x + 1 - x0) / (double)Scale;
				double ua;
				double ub;
				if (timeBased)
				{
					ua = a + offset;
					ub = b + offset;
					if (ua >= clipLength)
					{
						if (!instrument.Loop)
						{
							break;
						}

						double wrapped = ua % clipLength;
						ub = wrapped + (ub - ua);
						ua = wrapped;
					}
				}
				else
				{
					ua = a / pieceLength * clipLength;
					ub = b / pieceLength * clipLength;
				}

				int b0 = Math.Clamp((int)(ua / clipLength * buckets), 0, buckets - 1);
				int b1 = Math.Clamp((int)(ub / clipLength * buckets), b0, buckets - 1);
				float low = 0f;
				float high = 0f;
				for (int k = b0; k <= b1; k++)
				{
					low = Math.Min(low, peaks[k * 2]);
					high = Math.Max(high, peaks[k * 2 + 1]);
				}

				draw.AddLine(new NVector2(x + 0.5f, midY - high * half), new NVector2(x + 0.5f, midY - low * half + 1f), color);
			}
		}

		// ---------------------------------------------------------------- logic track

		/// <summary>Where a marker shows (display units); false when the open transition hides it.</summary>
		private bool MarkerSpan(AudioMarker marker, out double start, out double end, out bool cut)
		{
			cut = false;
			start = marker.Position;
			end = marker.End;
			if (!view.Active)
			{
				return true;
			}

			if (marker.Position < view.Source || (marker.Length <= 0.0 && marker.Position <= view.Source + 1e-9))
			{
				if (end > view.Source + 1e-9)
				{
					end = view.Source;
					cut = true;
				}

				return true;
			}

			if (view.Destination >= 0.0 && marker.Position >= view.Destination - 1e-9)
			{
				start = Display(Segment.Destination, marker.Position);
				end = Display(Segment.Destination, marker.End);
				return start >= view.Source + view.Timeline.Length - 1e-9;
			}

			return false;
		}

		private float LogicRowTop(float laneY, int row)
		{
			return laneY + 4f + row * (LogicRowHeight + 1f);
		}

		private void DrawLogic(AudioEvent current, ImDrawListPtr draw, float laneY)
		{
			float rowHeight = LogicRowHeight;

			// The open transition timeline first: the markers' flags draw over its header.
			if (view.Active)
			{
				DrawTransitionLogic(current, draw, laneY);
			}

			foreach (AudioMarker marker in current.Markers)
			{
				if (!MarkerSpan(marker, out double start, out double end, out bool cut))
				{
					continue;
				}

				bool selected = selection.Is(SelectionKind.Marker, marker.Id);
				float x0 = ToX(start);
				float x1 = ToX(end);
				switch (marker)
				{
					case TransitionRegion region:
					{
						float top = LogicRowTop(laneY, 0);
						var min = new NVector2(x0, top);
						var max = new NVector2(Math.Max(x1, x0 + 4f), top + rowHeight);
						StudioUi.Chevrons(draw, min, max, StudioUi.Green, StudioUi.GreenStripe);
						draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
						float badgeRight = max.X - 3f;
						badgeRight = DrawRegionBadges(draw, region, badgeRight, top, rowHeight);
						draw.PushClipRect(min, new NVector2(Math.Max(min.X, badgeRight - 2f), max.Y), true);
						DrawShadowedText(draw, new NVector2(min.X + 6f, top + 2f), StudioUi.GreenText, "To " + DestinationName(current, region.DestinationId) + (region.Conditions.Count > 0 ? "  ?" : ""));
						draw.PopClipRect();
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = min, Max = max });
						if (!cut)
						{
							logicItems.Add(new LogicItem { Kind = HitKind.MarkerEnd, Id = marker.Id, Min = new NVector2(max.X - EdgeGrab, top), Max = new NVector2(max.X + 2f, max.Y) });
						}

						break;
					}
					case TransitionMarker transition:
					{
						float top = LogicRowTop(laneY, 0);
						string text = TransitionFlagText(current, transition);
						float textWidth = ImGui.CalcTextSize(text).X;
						var min = new NVector2(x0, top);
						var max = new NVector2(x0 + textWidth + 22f, top + rowHeight);
						draw.AddRectFilled(min, max, StudioUi.Green, 2f);
						// The flag's arrow.
						draw.AddTriangleFilled(new NVector2(max.X - 11f, top + 5f), new NVector2(max.X - 11f, top + rowHeight - 5f), new NVector2(max.X - 4f, top + rowHeight * 0.5f), StudioUi.GreenText);
						draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
						draw.AddText(new NVector2(x0 + 5f, top + 2f), StudioUi.GreenText, text);
						if (transition.Timeline != null)
						{
							DrawTimelineBadge(draw, transition, max.X + 3f, top, rowHeight);
						}

						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(x0 - 3f, top), Max = max });
						break;
					}
					case DestinationMarker destination:
					{
						float top = LogicRowTop(laneY, 1);
						float right = StudioUi.FlagLabel(draw, x0, top, rowHeight, destination.Name ?? "", StudioUi.Flag, StudioUi.FlagText, selected);
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(x0 - 3f, top), Max = new NVector2(right, top + rowHeight) });
						break;
					}
					case TempoMarker tempo:
					{
						float top = LogicRowTop(laneY, 1);
						float right = StudioUi.FlagLabel(draw, x0, top, rowHeight, $"{tempo.Bpm:0.#}  {tempo.BeatsPerBar}/{tempo.BeatUnit}", StudioUi.Tempo, StudioUi.FlagText, selected);
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(x0 - 3f, top), Max = new NVector2(right, top + rowHeight) });
						break;
					}
					case SustainPoint sustain:
					{
						float top = LogicRowTop(laneY, 1);
						float right = StudioUi.FlagLabel(draw, x0, top, rowHeight, "S  " + (sustain.Name ?? ""), StudioUi.Sustain, StudioUi.FlagText, selected);
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(x0 - 3f, top), Max = new NVector2(right, top + rowHeight) });
						break;
					}
					case LoopRegion loop:
					{
						float top = LogicRowTop(laneY, 2);
						var min = new NVector2(x0, top + 3f);
						var max = new NVector2(Math.Max(x1, x0 + 4f), top + rowHeight - 3f);
						draw.AddRectFilled(min, max, StudioUi.LoopBlue, 2f);
						draw.AddRectFilled(new NVector2(min.X, max.Y - 3f), max, StudioUi.LoopBlueStripe, 2f);
						draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
						// Loop arrows at both ends.
						draw.AddTriangleFilled(new NVector2(min.X + 3f, min.Y + 2f), new NVector2(min.X + 3f, max.Y - 2f), new NVector2(min.X + 9f, (min.Y + max.Y) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.85f));
						draw.AddTriangleFilled(new NVector2(max.X - 3f, min.Y + 2f), new NVector2(max.X - 3f, max.Y - 2f), new NVector2(max.X - 9f, (min.Y + max.Y) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.85f));
						draw.PushClipRect(min + new NVector2(12f, 0f), max - new NVector2(12f, 0f), true);
						string text = (loop.Name ?? "Loop") + (loop.Conditions.Count > 0 ? "  ?" : "") + (loop.MaxLoops > 0 ? "  x" + loop.MaxLoops : "");
						draw.AddText(new NVector2(min.X + 13f, min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.95f), text);
						draw.PopClipRect();
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(min.X, top), Max = new NVector2(max.X, top + rowHeight) });
						if (!cut)
						{
							logicItems.Add(new LogicItem { Kind = HitKind.MarkerEnd, Id = marker.Id, Min = new NVector2(max.X - EdgeGrab, top), Max = new NVector2(max.X + 2f, top + rowHeight) });
						}

						break;
					}
				}
			}
		}

		private static string TransitionFlagText(AudioEvent current, TransitionMarker transition)
		{
			return "To " + DestinationName(current, transition.DestinationId) + (transition.Conditions.Count > 0 ? "  ?" : "");
		}

		/// <summary>The quantization and transition timeline badges at a transition region's right end; returns where they start.</summary>
		private float DrawRegionBadges(ImDrawListPtr draw, TransitionRegion region, float right, float top, float height)
		{
			if (region.Timeline != null)
			{
				float badgeX = right - 18f;
				DrawTimelineBadge(draw, region, badgeX, top, height);
				right = badgeX - 3f;
			}

			string quantization = QuantizationShort(region.Quantization);
			if (quantization.Length > 0)
			{
				NVector2 size = ImGui.CalcTextSize(quantization);
				var min = new NVector2(right - size.X - 8f, top + 3f);
				var max = new NVector2(right, top + height - 3f);
				if (min.X > ToX(region.Position) + 30f)
				{
					draw.AddRectFilled(min, max, StudioUi.Rgba(0f, 0f, 0f, 0.55f), 2f);
					draw.AddText(new NVector2(min.X + 4f, top + (height - size.Y) * 0.5f), StudioUi.Rgb(0.85f, 1f, 0.6f), quantization);
					right = min.X - 2f;
				}
			}

			return right;
		}

		/// <summary>The transition timeline badge (click: open or close it inline); returns its right edge.</summary>
		private float DrawTimelineBadge(ImDrawListPtr draw, TransitionMarker transition, float x, float top, float height)
		{
			bool open = expandedTransitionId == transition.Id;
			var min = new NVector2(x, top + 2f);
			var max = new NVector2(x + 18f, top + height - 2f);
			bool hot = hoverHit.Kind == HitKind.TransitionBadge && hoverHit.Id == transition.Id;
			draw.AddRectFilled(min, max, open ? StudioUi.Yellow : hot ? StudioUi.Rgb(0.4f, 0.4f, 0.42f) : StudioUi.Rgb(0.18f, 0.18f, 0.19f), 2f);
			draw.AddRect(min, max, StudioUi.Outline, 2f);
			// A tiny timeline: two clips crossing over.
			uint ink = open ? StudioUi.Rgb(0.08f, 0.08f, 0.09f) : StudioUi.Rgb(0.75f, 0.85f, 1f);
			draw.AddRectFilled(new NVector2(min.X + 3f, min.Y + 4f), new NVector2(min.X + 11f, min.Y + 7f), ink);
			draw.AddRectFilled(new NVector2(min.X + 8f, max.Y - 7f), new NVector2(max.X - 3f, max.Y - 4f), ink);
			logicItems.Add(new LogicItem { Kind = HitKind.TransitionBadge, Id = transition.Id, Min = min, Max = max });
			return max.X;
		}

		/// <summary>The open transition timeline in the logic track: its header with the end handle, the source region and the destination region with their fade handles.</summary>
		private void DrawTransitionLogic(AudioEvent current, ImDrawListPtr draw, float laneY)
		{
			TransitionTimeline timeline = view.Timeline;
			float rowHeight = LogicRowHeight;
			float p = ToX(view.Source);
			float end = ToX(view.Source + timeline.Length);

			// Row 0: the transition timeline itself.
			float top = LogicRowTop(laneY, 0);
			var headerMin = new NVector2(p, top);
			var headerMax = new NVector2(Math.Max(end, p + 4f), top + rowHeight);
			draw.AddRectFilled(headerMin, headerMax, StudioUi.Rgb(0.24f, 0.33f, 0.52f), 2f);
			draw.AddRect(headerMin, headerMax, StudioUi.Outline, 2f);
			draw.PushClipRect(headerMin, headerMax, true);
			string title = "Transition timeline  " + FormatTime(timeline.Length);

			// A transition marker's flag (and badge) sits at the same place, over the header: the title goes after it.
			float titleX = headerMin.X + 6f;
			if (!(view.Marker is TransitionRegion))
			{
				titleX += ImGui.CalcTextSize(TransitionFlagText(current, view.Marker)).X + 22f + 21f + 2f;
			}

			draw.AddText(new NVector2(titleX, top + 2f), StudioUi.Rgb(0.85f, 0.9f, 1f), title);
			draw.PopClipRect();
			logicItems.Add(new LogicItem { Kind = HitKind.TransitionHeader, Id = view.Marker.Id, Min = headerMin, Max = headerMax });
			DrawEdgeHandle(draw, end, top, top + LogicRowHeight * 3f + 2f, IsHot(HitKind.TimelineEnd, null));
			logicItems.Add(new LogicItem { Kind = HitKind.TimelineEnd, Id = view.Marker.Id, Min = new NVector2(end - EdgeGrab, top), Max = new NVector2(end + EdgeGrab, top + rowHeight * 3f + 2f) });

			// Row 1: the source region, fading out.
			float sourceTop = LogicRowTop(laneY, 1);
			float sourceEnd = ToX(view.Source + timeline.SourceLength);
			var sourceMin = new NVector2(p, sourceTop + 2f);
			var sourceMax = new NVector2(Math.Max(sourceEnd, p + 3f), sourceTop + rowHeight - 2f);
			draw.AddRectFilled(sourceMin, sourceMax, StudioUi.Rgba(0.55f, 0.78f, 0.24f, 0.75f), 2f);
			StudioUi.Fade(draw, ToX(view.Source + timeline.SourceLength - timeline.SourceFadeOut.Length), sourceEnd, sourceMin.Y, sourceMax.Y, timeline.SourceFadeOut.Shape, false, StudioUi.Rgb(1f, 1f, 1f), StudioUi.Rgba(0f, 0f, 0f, 0.3f));
			draw.AddRect(sourceMin, sourceMax, StudioUi.Outline, 2f);
			draw.PushClipRect(sourceMin, sourceMax, true);
			draw.AddText(new NVector2(sourceMin.X + 5f, sourceMin.Y + 1f), StudioUi.GreenText, "Source");
			draw.PopClipRect();
			float sourceFadeX = ToX(view.Source + timeline.SourceLength - timeline.SourceFadeOut.Length);
			StudioUi.Handle(draw, new NVector2(sourceFadeX, sourceMin.Y + 1f), HandleSize, StudioUi.Selected, IsHot(HitKind.SourceFade, null));
			DrawEdgeHandle(draw, sourceEnd, sourceMin.Y, sourceMax.Y, IsHot(HitKind.SourceEnd, null));
			logicItems.Add(new LogicItem { Kind = HitKind.SourceFade, Id = view.Marker.Id, Min = new NVector2(sourceFadeX - HandleSize, sourceMin.Y - HandleSize * 0.5f), Max = new NVector2(sourceFadeX + HandleSize, sourceMin.Y + HandleSize) });
			logicItems.Add(new LogicItem { Kind = HitKind.SourceEnd, Id = view.Marker.Id, Min = new NVector2(sourceEnd - EdgeGrab, sourceMin.Y), Max = new NVector2(sourceEnd + EdgeGrab, sourceMax.Y) });

			// Row 2: the destination region, fading in.
			float destinationTop = LogicRowTop(laneY, 2);
			float destinationStart = ToX(view.Source + timeline.DestinationStart);
			var destinationMin = new NVector2(destinationStart, destinationTop + 2f);
			var destinationMax = new NVector2(Math.Max(end, destinationStart + 3f), destinationTop + rowHeight - 2f);
			draw.AddRectFilled(destinationMin, destinationMax, StudioUi.WithAlpha(StudioUi.Destination, 0.75f), 2f);
			float destinationFadeX = ToX(view.Source + timeline.DestinationStart + timeline.DestinationFadeIn.Length);
			StudioUi.Fade(draw, destinationStart, destinationFadeX, destinationMin.Y, destinationMax.Y, timeline.DestinationFadeIn.Shape, true, StudioUi.Rgb(1f, 1f, 1f), StudioUi.Rgba(0f, 0f, 0f, 0.3f));
			draw.AddRect(destinationMin, destinationMax, StudioUi.Outline, 2f);
			string destinationLabel = "To " + DestinationName(current, view.Marker.DestinationId);
			float labelWidth = ImGui.CalcTextSize(destinationLabel).X;
			draw.PushClipRect(destinationMin, destinationMax, true);
			draw.AddText(new NVector2(Math.Max(destinationMin.X + 5f, destinationMax.X - labelWidth - 6f), destinationMin.Y + 1f), StudioUi.FlagText, destinationLabel);
			draw.PopClipRect();
			StudioUi.Handle(draw, new NVector2(destinationFadeX, destinationMin.Y + 1f), HandleSize, StudioUi.Selected, IsHot(HitKind.DestinationFade, null));
			DrawEdgeHandle(draw, destinationStart, destinationMin.Y, destinationMax.Y, IsHot(HitKind.DestinationStart, null));
			logicItems.Add(new LogicItem { Kind = HitKind.DestinationFade, Id = view.Marker.Id, Min = new NVector2(destinationFadeX - HandleSize, destinationMin.Y - HandleSize * 0.5f), Max = new NVector2(destinationFadeX + HandleSize, destinationMin.Y + HandleSize) });
			logicItems.Add(new LogicItem { Kind = HitKind.DestinationStart, Id = view.Marker.Id, Min = new NVector2(destinationStart - EdgeGrab, destinationMin.Y), Max = new NVector2(destinationStart + EdgeGrab, destinationMax.Y) });
		}

		private static void DrawEdgeHandle(ImDrawListPtr draw, float x, float top, float bottom, bool hot)
		{
			draw.AddRectFilled(new NVector2(x - 2f, top), new NVector2(x + 2f, bottom), hot ? StudioUi.Yellow : StudioUi.Rgba(1f, 1f, 1f, 0.85f), 1f);
		}

		private static void DrawShadowedText(ImDrawListPtr draw, NVector2 position, uint color, string text)
		{
			draw.AddText(position + new NVector2(1f, 1f), StudioUi.Rgba(1f, 1f, 1f, 0.35f), text);
			draw.AddText(position, color, text);
		}

		/// <summary>Thin lines through the lanes at the markers and region edges (and at the open transition's ends).</summary>
		private void DrawMarkerLines(AudioEvent current, ImDrawListPtr draw, NVector2 lanesOrigin)
		{
			if (rows.Count < 2)
			{
				return;
			}

			float top = lanesOrigin.Y + rows[1].Y;
			float bottom = lanesOrigin.Y + rows[rows.Count - 1].Y + rows[rows.Count - 1].Height;
			foreach (AudioMarker marker in current.Markers)
			{
				if (!MarkerSpan(marker, out double start, out double end, out bool cut))
				{
					continue;
				}

				uint color;
				switch (marker)
				{
					case TransitionMarker _: color = StudioUi.WithAlpha(StudioUi.Green, 0.5f); break;
					case LoopRegion _: color = StudioUi.WithAlpha(StudioUi.LoopBlue, 0.45f); break;
					case TempoMarker _: color = StudioUi.WithAlpha(StudioUi.Tempo, 0.3f); break;
					case SustainPoint _: color = StudioUi.WithAlpha(StudioUi.Sustain, 0.4f); break;
					default: color = StudioUi.Rgba(1f, 1f, 1f, 0.3f); break;
				}

				float x0 = ToX(start);
				draw.AddLine(new NVector2(x0, top), new NVector2(x0, bottom), color);
				if (marker.Length > 0.0 && !cut)
				{
					float x1 = ToX(end);
					draw.AddLine(new NVector2(x1, top), new NVector2(x1, bottom), color);
				}
			}

			if (view.Active)
			{
				uint edge = StudioUi.Rgba(0.55f, 0.72f, 1f, 0.7f);
				float p = ToX(view.Source);
				float end = ToX(view.Source + view.Timeline.Length);
				draw.AddLine(new NVector2(p, top), new NVector2(p, bottom), edge, 1.5f);
				draw.AddLine(new NVector2(end, top), new NVector2(end, bottom), edge, 1.5f);
			}
		}

		/// <summary>The event's end (timeline) in the master lane.</summary>
		private void DrawEventEnd(AudioEvent current, ImDrawListPtr draw, NVector2 lanesOrigin)
		{
			if (!IsTimeSheet || rows.Count == 0)
			{
				return;
			}

			Row master = rows[rows.Count - 1];
			float y = lanesOrigin.Y + master.Y;
			double length = current.TimelineLength;
			double display = length;
			if (view.Active)
			{
				if (view.Destination < 0.0)
				{
					display = view.Source + view.Timeline.Length;
				}
				else if (length >= view.Destination)
				{
					display = Display(Segment.Destination, length);
				}
				else if (length > view.Source)
				{
					return;
				}
			}

			float x = ToX(display);
			draw.AddLine(new NVector2(x, y), new NVector2(x, y + master.Height), StudioUi.Rgba(1f, 1f, 1f, 0.35f));
			draw.AddText(new NVector2(x + 4f, y + 6f), StudioUi.TextDim, "end " + FormatTime(length));
		}

		private static string DestinationName(AudioEvent current, string destinationId)
		{
			if (destinationId == TransitionMarker.EndDestination)
			{
				return "End";
			}

			AudioMarker destination = current.FindMarker(destinationId);
			return destination != null ? destination.Name ?? "marker" : "(none)";
		}

		private static string QuantizationShort(QuantizationInterval interval)
		{
			switch (interval)
			{
				case QuantizationInterval.QuarterBeat: return "1/4";
				case QuantizationInterval.HalfBeat: return "1/2";
				case QuantizationInterval.Beat: return "1 beat";
				case QuantizationInterval.TwoBeats: return "2 beats";
				case QuantizationInterval.Bar: return "1 bar";
				case QuantizationInterval.TwoBars: return "2 bars";
				case QuantizationInterval.FourBars: return "4 bars";
				case QuantizationInterval.EightBars: return "8 bars";
				default: return "";
			}
		}

		// ---------------------------------------------------------------- playheads and the overview

		/// <summary>Where an instance's playhead shows (display units); false when the open transition hides its position.</summary>
		private bool InstanceDisplay(EventInstance instance, out double display)
		{
			display = 0.0;
			if (instance == null)
			{
				return false;
			}

			if (!view.Active)
			{
				display = instance.TimelinePosition;
				return true;
			}

			int active = instance.Playback.ActiveTransitionIndex;
			if (active >= 0)
			{
				if (active != instance.Description.FindTransition(view.Marker.Id))
				{
					return false;
				}

				display = view.Source + instance.Playback.TransitionSeconds;
				return true;
			}

			double t = instance.TimelinePosition;
			if (t <= view.Source + 1e-3)
			{
				display = t;
				return true;
			}

			if (view.Destination >= 0.0 && t >= view.Destination + view.Timeline.Length - view.Timeline.DestinationStart - 1e-3)
			{
				display = Display(Segment.Destination, t);
				return true;
			}

			return false;
		}

		private void DrawPlayheads(AudioEvent current, ImDrawListPtr draw, float top, float bottom)
		{
			if (!IsTimeSheet)
			{
				AudioParameter parameter = Project.FindParameter(current, currentSheet);
				if (parameter != null)
				{
					float x = ToX(ShownParameterValue(parameter));
					draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), IsLiveShown ? StudioUi.LivePlayhead : StudioUi.Yellow, 2f);
				}

				return;
			}

			// Other game instances of the event: thin cyan lines.
			EventInstance shown = Shown;
			foreach (EventInstance instance in liveInstances)
			{
				if (instance != shown && InstanceDisplay(instance, out double other))
				{
					float x = ToX(other);
					draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), StudioUi.WithAlpha(StudioUi.LivePlayhead, 0.45f), 1f);
				}
			}

			if (shown != null)
			{
				if (InstanceDisplay(shown, out double display))
				{
					float x = ToX(display);
					if (followPlayhead && shown.IsPlaying && (x > canvasRight - 20f || x < canvasLeft))
					{
						scroll = Math.Max(0.0, display - 0.1 * (canvasRight - canvasLeft) / zoom);
					}

					draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), IsLiveShown ? StudioUi.LivePlayhead : StudioUi.Playhead, 1.5f);
				}

				return;
			}

			if (!view.Active || previewStartPosition <= view.Source)
			{
				float x = ToX(previewStartPosition);
				draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), StudioUi.Rgba(1f, 1f, 1f, 0.4f), 1f);
			}
		}

		/// <summary>The whole event in a strip below the lanes, the visible part framed; click or drag to scroll.</summary>
		private void DrawMinimap(AudioEvent current, NVector2 min, float width)
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			var max = new NVector2(min.X + width, min.Y + MinimapHeight);
			draw.AddRectFilled(min, max, StudioUi.Rgb(0.1f, 0.1f, 0.105f));
			draw.AddRectFilled(min, new NVector2(canvasLeft - 1f, max.Y), StudioUi.Shade(StudioUi.TrackHeader, 0.75f));
			draw.AddText(min + new NVector2(10f, (MinimapHeight - ImGui.GetTextLineHeight()) * 0.5f), StudioUi.TextDim, "Overview");
			float left = canvasLeft;
			float right = canvasRight;
			double visible = (right - left) / Math.Max(1f, zoom);
			double total = Math.Max(1.0, scroll + visible);
			foreach (Piece piece in pieces)
			{
				total = Math.Max(total, piece.To);
			}

			if (view.Active)
			{
				total = Math.Max(total, view.Source + view.Timeline.Length);
			}

			total *= 1.04;
			double perSecond = (right - left) / total;
			int trackCount = Math.Max(1, current.Tracks.Count);
			float bandHeight = (MinimapHeight - 8f) / trackCount;
			foreach (Piece piece in pieces)
			{
				float x0 = left + (float)(piece.From * perSecond);
				float x1 = Math.Max(x0 + 1f, left + (float)(piece.To * perSecond));
				float y0 = min.Y + 4f + piece.TrackIndex * bandHeight;
				draw.AddRectFilled(new NVector2(x0, y0), new NVector2(x1, y0 + Math.Max(1f, bandHeight - 1f)), StudioUi.WithAlpha(InstrumentColor(piece.Instrument), 0.85f));
			}

			foreach (AudioMarker marker in current.Markers)
			{
				if ((marker is TransitionMarker || marker is LoopRegion) && MarkerSpan(marker, out double start, out double end, out _))
				{
					float x0 = left + (float)(start * perSecond);
					float x1 = Math.Max(x0 + 2f, left + (float)(end * perSecond));
					draw.AddRectFilled(new NVector2(x0, min.Y + 1f), new NVector2(x1, min.Y + 3f), marker is LoopRegion ? StudioUi.LoopBlue : StudioUi.Green);
				}
			}

			float vx0 = left + (float)(scroll * perSecond);
			float vx1 = left + (float)((scroll + visible) * perSecond);
			draw.AddRectFilled(new NVector2(vx0, min.Y + 1f), new NVector2(vx1, max.Y - 1f), StudioUi.Rgba(1f, 1f, 1f, 0.07f));
			draw.AddRect(new NVector2(vx0, min.Y + 1f), new NVector2(vx1, max.Y - 1f), StudioUi.WithAlpha(StudioUi.Yellow, 0.8f));
			EventInstance shown = Shown;
			if (shown != null && InstanceDisplay(shown, out double display))
			{
				float px = left + (float)(display * perSecond);
				draw.AddLine(new NVector2(px, min.Y), new NVector2(px, max.Y), IsLiveShown ? StudioUi.LivePlayhead : StudioUi.Playhead);
			}

			ImGui.SetCursorScreenPos(new NVector2(left, min.Y));
			EditorGui.InvisibleButton("##minimap", new NVector2(Math.Max(1f, right - left), MinimapHeight));
			if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				double at = (ImGui.GetMousePos().X - left) / perSecond;
				scroll = Math.Max(0.0, at - visible * 0.5);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Overview of the whole event: click or drag to scroll.");
			}
		}

		// ---------------------------------------------------------------- input

		private void ZoomAround(float x, float factor)
		{
			if (!IsTimeSheet)
			{
				return;
			}

			double at = FromX(x);
			zoom = (float)Math.Clamp(zoom * factor, 4.0, 4000.0);
			scroll = Math.Max(0.0, at - (x - canvasLeft) / zoom);
		}

		private void HandleWheel(float viewHeight, float totalHeight)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			if (Math.Abs(io.MouseWheel) < 0.01f)
			{
				return;
			}

			if (io.KeyCtrl)
			{
				ZoomAround(ImGui.GetMousePos().X, io.MouseWheel > 0f ? 1.2f : 1f / 1.2f);
			}
			else if (io.KeyShift)
			{
				if (IsTimeSheet)
				{
					scroll = Math.Max(0.0, scroll - io.MouseWheel * 60.0 / zoom);
				}
			}
			else
			{
				bodyScroll = Math.Clamp(bodyScroll - io.MouseWheel * 40f, 0f, Math.Max(0f, totalHeight - viewHeight));
			}
		}

		private static bool Inside(NVector2 point, NVector2 min, NVector2 max)
		{
			return point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;
		}

		private Hit HitTest(NVector2 lanesOrigin, NVector2 mouse)
		{
			var hit = new Hit { Kind = HitKind.None };
			if (mouse.X < canvasLeft || mouse.X > canvasRight)
			{
				return hit;
			}

			hit.Display = FromX(mouse.X);

			// Logic items: handles and badges (drawn last) first.
			for (int i = logicItems.Count - 1; i >= 0; i--)
			{
				LogicItem item = logicItems[i];
				if (item.Kind != HitKind.Marker && item.Kind != HitKind.TransitionHeader && Inside(mouse, item.Min, item.Max))
				{
					hit.Kind = item.Kind;
					hit.Id = item.Id;
					return hit;
				}
			}

			for (int i = logicItems.Count - 1; i >= 0; i--)
			{
				LogicItem item = logicItems[i];
				if ((item.Kind == HitKind.Marker || item.Kind == HitKind.TransitionHeader) && Inside(mouse, item.Min, item.Max))
				{
					hit.Kind = item.Kind;
					hit.Id = item.Id;
					return hit;
				}
			}

			foreach (Row row in rows)
			{
				float y = lanesOrigin.Y + row.Y;
				if (mouse.Y < y || mouse.Y >= y + row.Height)
				{
					continue;
				}

				if (row.Kind == RowKind.Logic)
				{
					hit.Kind = HitKind.Logic;
					return hit;
				}

				if (row.Kind != RowKind.Track)
				{
					return hit;
				}

				hit.Kind = HitKind.Lane;
				hit.Track = row.Track;
				for (int i = pieces.Count - 1; i >= 0; i--)
				{
					Piece piece = pieces[i];
					if (piece.Track != row.Track || !Inside(mouse, piece.Min, piece.Max))
					{
						continue;
					}

					hit.Piece = piece;
					hit.Id = piece.Instrument.Id;
					hit.Kind = HitKind.Instrument;
					float width = piece.Max.X - piece.Min.X;
					bool edges = width > EdgeGrab * 3f;
					if (view.Active && piece.Kind == PieceKind.Source && piece.EndsAtSourceLength)
					{
						float handle = ToX(view.Source + view.Timeline.SourceLength - view.Timeline.SourceFadeOut.Length);
						if (Math.Abs(mouse.X - Math.Clamp(handle, piece.Min.X + 4f, piece.Max.X - 4f)) <= HandleSize && mouse.Y <= piece.Min.Y + HandleSize + 2f)
						{
							hit.Kind = HitKind.SourceFade;
							return hit;
						}
					}

					if (view.Active && piece.Kind == PieceKind.Destination && piece.StartsAtDestination)
					{
						float handle = ToX(view.Source + view.Timeline.DestinationStart + view.Timeline.DestinationFadeIn.Length);
						if (Math.Abs(mouse.X - Math.Clamp(handle, piece.Min.X + 4f, piece.Max.X - 4f)) <= HandleSize && mouse.Y <= piece.Min.Y + HandleSize + 2f)
						{
							hit.Kind = HitKind.DestinationFade;
							return hit;
						}
					}

					if (edges && mouse.X <= piece.Min.X + EdgeGrab)
					{
						if (piece.Kind == PieceKind.Destination && piece.StartsAtDestination)
						{
							hit.Kind = HitKind.DestinationStart;
						}
						else if (!piece.CutStart && piece.Kind != PieceKind.Source)
						{
							hit.Kind = HitKind.InstrumentStart;
						}
					}
					else if (edges && mouse.X >= piece.Max.X - EdgeGrab)
					{
						if (piece.Kind == PieceKind.Source)
						{
							if (piece.EndsAtSourceLength)
							{
								hit.Kind = HitKind.SourceEnd;
							}
						}
						else if (!piece.CutEnd)
						{
							hit.Kind = HitKind.InstrumentEnd;
						}
					}

					return hit;
				}

				return hit;
			}

			return hit;
		}

		private static bool IsTransitionHandle(HitKind kind)
		{
			return kind == HitKind.SourceEnd || kind == HitKind.DestinationStart || kind == HitKind.TimelineEnd || kind == HitKind.SourceFade || kind == HitKind.DestinationFade;
		}

		private void HandleMouse(AudioEvent current, NVector2 lanesOrigin, bool canvasHovered)
		{
			NVector2 mouse = ImGui.GetMousePos();
			Hit hit = canvasHovered && dragKind == DragKind.None ? HitTest(lanesOrigin, mouse) : new Hit();
			hoverHit = dragKind == DragKind.None ? hit : hoverHit;
			bool resizing = hit.Kind == HitKind.InstrumentStart || hit.Kind == HitKind.InstrumentEnd || hit.Kind == HitKind.MarkerEnd || IsTransitionHandle(hit.Kind)
				|| (dragKind != DragKind.None && dragKind != DragKind.MoveInstrument && dragKind != DragKind.MoveMarker);
			if (resizing)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
			}
			else if (hit.Kind == HitKind.TransitionBadge)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			}

			if (canvasHovered && dragKind == DragKind.None)
			{
				ShowHitTooltip(current, hit);
			}

			// Press.
			if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				dragMouseX0 = mouse.X;
				dragChanged = false;
				switch (hit.Kind)
				{
					case HitKind.Instrument:
					case HitKind.InstrumentStart:
					case HitKind.InstrumentEnd:
					{
						AudioInstrument instrument = current.FindInstrument(hit.Id, out _);
						selection = Selection.Of(SelectionKind.Instrument, hit.Id);
						if (instrument != null && hit.Piece != null && hit.Piece.Kind != PieceKind.Source)
						{
							dragKind = hit.Kind == HitKind.Instrument ? DragKind.MoveInstrument : hit.Kind == HitKind.InstrumentEnd ? DragKind.ResizeInstrumentEnd : DragKind.ResizeInstrumentStart;
							dragSegment = hit.Piece.Kind == PieceKind.TransitionClip ? Segment.Transition : Segment.Event;
							dragId = hit.Id;
							dragStart0 = instrument.Start;
							dragLength0 = instrument.Length;
							dragSnapshot = Project.ToBytes();
						}

						break;
					}
					case HitKind.Marker:
					case HitKind.MarkerEnd:
					{
						AudioMarker marker = current.FindMarker(hit.Id);
						selection = Selection.Of(SelectionKind.Marker, hit.Id);
						if (marker != null)
						{
							if (marker is TransitionMarker transition && EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
							{
								if (expandedTransitionId == transition.Id)
								{
									CloseTransitionTimeline();
								}
								else
								{
									OpenTransitionTimeline(transition);
								}

								break;
							}

							dragKind = hit.Kind == HitKind.MarkerEnd ? DragKind.ResizeMarker : DragKind.MoveMarker;
							dragSegment = Segment.Event;
							dragId = hit.Id;
							dragStart0 = marker.Position;
							dragLength0 = marker.Length;
							dragSnapshot = Project.ToBytes();
						}

						break;
					}
					case HitKind.TransitionBadge:
					{
						var transition = current.FindMarker(hit.Id) as TransitionMarker;
						selection = Selection.Of(SelectionKind.Marker, hit.Id);
						if (expandedTransitionId == hit.Id)
						{
							CloseTransitionTimeline();
						}
						else
						{
							OpenTransitionTimeline(transition);
						}

						break;
					}
					case HitKind.TransitionHeader:
						selection = Selection.Of(SelectionKind.Marker, hit.Id);
						if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							CloseTransitionTimeline();
						}

						break;
					case HitKind.SourceEnd:
					case HitKind.DestinationStart:
					case HitKind.TimelineEnd:
					case HitKind.SourceFade:
					case HitKind.DestinationFade:
					{
						if (view.Active)
						{
							TransitionTimeline timeline = view.Timeline;
							if (hit.Piece != null)
							{
								selection = Selection.Of(SelectionKind.Instrument, hit.Piece.Instrument.Id);
							}
							else
							{
								selection = Selection.Of(SelectionKind.Marker, view.Marker.Id);
							}

							switch (hit.Kind)
							{
								case HitKind.SourceEnd:
									dragKind = DragKind.SourceEnd;
									dragStart0 = timeline.SourceLength;
									break;
								case HitKind.DestinationStart:
									dragKind = DragKind.DestinationStart;
									dragStart0 = timeline.DestinationStart;
									break;
								case HitKind.TimelineEnd:
									dragKind = DragKind.TimelineEnd;
									dragStart0 = timeline.Length;
									break;
								case HitKind.SourceFade:
									// The handle sits where the fade starts.
									dragKind = DragKind.SourceFade;
									dragStart0 = timeline.SourceLength - timeline.SourceFadeOut.Length;
									break;
								default:
									// The handle sits where the fade ends.
									dragKind = DragKind.DestinationFade;
									dragStart0 = timeline.DestinationStart + timeline.DestinationFadeIn.Length;
									break;
							}

							dragSegment = Segment.Transition;
							dragId = view.Marker.Id;
							dragSnapshot = Project.ToBytes();
						}

						break;
					}
					case HitKind.Lane:
						selection = Selection.Of(SelectionKind.Track, hit.Track.Id);
						break;
					case HitKind.Logic:
						selection = Selection.Of(SelectionKind.Event, current.Id);
						break;
				}
			}

			// Drag.
			if (dragKind != DragKind.None && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				double delta = (mouse.X - dragMouseX0) / Scale;
				if (Math.Abs(mouse.X - dragMouseX0) >= 2f || dragChanged)
				{
					ApplyDrag(current, lanesOrigin, mouse, delta);
				}
			}

			// Release.
			if (dragKind != DragKind.None && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				if (dragChanged && dragSnapshot != null)
				{
					undoStack.Add(dragSnapshot);
					Trim(undoStack);
					redoStack.Clear();
				}

				dragSnapshot = null;
				dragKind = DragKind.None;
				dragId = null;
			}

			// Context menu.
			if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				contextHit = hit;
				if (hit.Kind == HitKind.Instrument || hit.Kind == HitKind.InstrumentStart || hit.Kind == HitKind.InstrumentEnd)
				{
					selection = Selection.Of(SelectionKind.Instrument, hit.Id);
				}
				else if (hit.Kind == HitKind.Marker || hit.Kind == HitKind.MarkerEnd || hit.Kind == HitKind.TransitionBadge || hit.Kind == HitKind.TransitionHeader)
				{
					selection = Selection.Of(SelectionKind.Marker, hit.Id);
				}

				openEditorContext = true;
			}
		}

		private void ShowHitTooltip(AudioEvent current, Hit hit)
		{
			switch (hit.Kind)
			{
				case HitKind.Instrument:
				case HitKind.InstrumentStart:
				case HitKind.InstrumentEnd:
				{
					AudioInstrument instrument = hit.Piece?.Instrument;
					if (instrument == null)
					{
						break;
					}

					string range = IsTimeSheet ? FormatTime(instrument.Start) + " - " + FormatTime(instrument.End) : instrument.Start.ToString("0.##") + " .. " + instrument.End.ToString("0.##");
					string role = hit.Piece.Kind == PieceKind.Source ? "\nSource: keeps playing into the transition timeline (drag its end, or the fade handle)"
						: hit.Piece.Kind == PieceKind.Destination ? (hit.Piece.StartsAtDestination ? "\nDestination: starts early in the transition timeline (drag its start, or the fade handle)" : "\nAfter the destination")
						: hit.Piece.Kind == PieceKind.TransitionClip ? "\nOn the transition timeline" : "";
					ImGui.SetTooltip(instrument.DisplayName + "\n" + instrument.Kind + (instrument.Async ? ", async" : ", timelocked") + (instrument.Loop ? ", looping" : "") + "\n" + range + role + "\nDrag: move, edges: trim, right click: menu");
					break;
				}
				case HitKind.TransitionBadge:
					ImGui.SetTooltip(expandedTransitionId == hit.Id ? "Close the transition timeline" : "Open the transition timeline in the timeline");
					break;
				case HitKind.TransitionHeader:
					ImGui.SetTooltip("The transition timeline: what plays between the transition and its destination.\nDouble click or Esc: close it.");
					break;
				case HitKind.SourceFade:
					ImGui.SetTooltip("Source fade out: drag to set how long the source's sounds fade before the source region ends.");
					break;
				case HitKind.DestinationFade:
					ImGui.SetTooltip("Destination fade in: drag to set how long the destination's sounds take to come up.");
					break;
				case HitKind.SourceEnd:
					ImGui.SetTooltip("End of the source: how long the sounds playing at the transition keep playing.");
					break;
				case HitKind.DestinationStart:
					ImGui.SetTooltip("Start of the destination: when the destination's sounds begin.");
					break;
				case HitKind.TimelineEnd:
					ImGui.SetTooltip("End of the transition timeline: playback continues on the destination from here.");
					break;
				case HitKind.Marker:
				case HitKind.MarkerEnd:
				{
					AudioMarker marker = current.FindMarker(hit.Id);
					if (marker is TransitionMarker transition)
					{
						ImGui.SetTooltip(marker.Kind + " to " + DestinationName(current, transition.DestinationId) + "\nDouble click: " + (transition.Timeline == null ? "add a transition timeline" : expandedTransitionId == marker.Id ? "close the transition timeline" : "open the transition timeline") + "\nDrag: move, right click: menu");
					}
					else if (marker != null)
					{
						ImGui.SetTooltip(marker.Kind + ": " + (marker.Name ?? "") + " at " + FormatTime(marker.Position) + (marker.Length > 0.0 ? " - " + FormatTime(marker.End) : "") + "\nDrag: move" + (marker.Length > 0.0 ? ", right edge: resize" : "") + ", right click: menu");
					}

					break;
				}
			}
		}

		private void ApplyDrag(AudioEvent current, NVector2 lanesOrigin, NVector2 mouse, double delta)
		{
			double lower = IsTimeSheet ? 0.0 : sheetMin;
			double upper = IsTimeSheet ? double.MaxValue : sheetMax;
			switch (dragKind)
			{
				case DragKind.MoveInstrument:
				{
					AudioInstrument instrument = current.FindInstrument(dragId, out AudioTrack owner);
					if (instrument == null)
					{
						dragKind = DragKind.None;
						return;
					}

					double start = SnapValue(dragSegment, dragStart0 + delta, current);
					start = Math.Clamp(start, lower, Math.Max(lower, upper - instrument.Length));
					if (Math.Abs(start - instrument.Start) > 1e-9)
					{
						instrument.Start = start;
						dragChanged = true;
					}

					// Another lane under the mouse: the instrument changes track.
					foreach (Row row in rows)
					{
						float y = lanesOrigin.Y + row.Y;
						if (row.Kind == RowKind.Track && mouse.Y >= y && mouse.Y < y + row.Height && row.Track != owner)
						{
							owner.Instruments.Remove(instrument);
							row.Track.Instruments.Add(instrument);
							dragChanged = true;
							break;
						}
					}

					if (dragChanged) Changed();
					break;
				}
				case DragKind.ResizeInstrumentEnd:
				{
					AudioInstrument instrument = current.FindInstrument(dragId, out _);
					if (instrument == null) return;
					double end = SnapValue(dragSegment, dragStart0 + dragLength0 + delta, current);
					double length = Math.Max(IsTimeSheet ? 0.01 : (sheetMax - sheetMin) * 0.005, Math.Min(end, upper) - instrument.Start);
					if (Math.Abs(length - instrument.Length) > 1e-9)
					{
						instrument.Length = length;
						dragChanged = true;
						Changed();
					}

					break;
				}
				case DragKind.ResizeInstrumentStart:
				{
					AudioInstrument instrument = current.FindInstrument(dragId, out _);
					if (instrument == null) return;
					double end = dragStart0 + dragLength0;
					double start = Math.Clamp(SnapValue(dragSegment, dragStart0 + delta, current), lower, end - (IsTimeSheet ? 0.01 : (sheetMax - sheetMin) * 0.005));
					if (Math.Abs(start - instrument.Start) > 1e-9)
					{
						instrument.Start = start;
						instrument.Length = end - start;
						dragChanged = true;
						Changed();
					}

					break;
				}
				case DragKind.MoveMarker:
				{
					AudioMarker marker = current.FindMarker(dragId);
					if (marker == null) return;
					double position = Math.Max(0.0, SnapValue(Segment.Event, dragStart0 + delta, current));
					if (Math.Abs(position - marker.Position) > 1e-9)
					{
						marker.Position = position;
						dragChanged = true;
						Changed();
					}

					break;
				}
				case DragKind.ResizeMarker:
				{
					AudioMarker marker = current.FindMarker(dragId);
					double end = SnapValue(Segment.Event, dragStart0 + dragLength0 + delta, current);
					double length = Math.Max(0.05, end - dragStart0);
					switch (marker)
					{
						case LoopRegion loop when Math.Abs(loop.RegionLength - length) > 1e-9:
							loop.RegionLength = length;
							dragChanged = true;
							Changed();
							break;
						case TransitionRegion region when Math.Abs(region.RegionLength - length) > 1e-9:
							region.RegionLength = length;
							dragChanged = true;
							Changed();
							break;
					}

					break;
				}
				case DragKind.SourceEnd:
				case DragKind.DestinationStart:
				case DragKind.TimelineEnd:
				case DragKind.SourceFade:
				case DragKind.DestinationFade:
				{
					TransitionTimeline timeline = (current.FindMarker(dragId) as TransitionMarker)?.Timeline;
					if (timeline == null)
					{
						dragKind = DragKind.None;
						return;
					}

					double value = Math.Max(0.0, SnapValue(Segment.Transition, dragStart0 + delta, current));
					bool changed = false;
					switch (dragKind)
					{
						case DragKind.SourceEnd:
							value = Math.Clamp(value, 0.0, timeline.Length);
							changed = Math.Abs(value - timeline.SourceLength) > 1e-9;
							timeline.SourceLength = value;
							break;
						case DragKind.DestinationStart:
							value = Math.Clamp(value, 0.0, timeline.Length);
							changed = Math.Abs(value - timeline.DestinationStart) > 1e-9;
							timeline.DestinationStart = value;
							break;
						case DragKind.TimelineEnd:
							value = Math.Max(0.05, value);
							changed = Math.Abs(value - timeline.Length) > 1e-9;
							timeline.Length = value;
							break;
						case DragKind.SourceFade:
						{
							// The handle sits at SourceLength - fade: dragging it right shortens the fade.
							double fade = Math.Clamp(timeline.SourceLength - value, 0.0, timeline.SourceLength);
							changed = Math.Abs(fade - timeline.SourceFadeOut.Length) > 1e-9;
							timeline.SourceFadeOut.Length = fade;
							break;
						}
						case DragKind.DestinationFade:
						{
							double fade = Math.Clamp(value - timeline.DestinationStart, 0.0, timeline.Length - timeline.DestinationStart);
							changed = Math.Abs(fade - timeline.DestinationFadeIn.Length) > 1e-9;
							timeline.DestinationFadeIn.Length = fade;
							break;
						}
					}

					timeline.Clamp();
					if (changed)
					{
						dragChanged = true;
						Changed();
					}

					break;
				}
			}
		}

		/// <summary>Where a new instrument goes for a display position: the transition timeline inside an open transition, else the timeline (or the sheet).</summary>
		private void PlacementAt(AudioEvent current, double display, out string sheet, out double start)
		{
			if (!IsTimeSheet)
			{
				sheet = currentSheet;
				start = Math.Clamp(SnapValue(Segment.Event, display, current), sheetMin, sheetMax);
				return;
			}

			Segment segment = SegmentAt(display, out double value);
			if (segment == Segment.Transition)
			{
				sheet = TransitionTimeline.SheetId(view.Marker.Id);
				start = Math.Max(0.0, SnapValue(Segment.Transition, value, current));
				return;
			}

			sheet = null;
			start = Math.Max(0.0, SnapValue(segment, value, current));
		}

		/// <summary>Sounds dragged from the Assets panel onto a lane become single instruments there.</summary>
		private void HandleDrop(AudioEvent current, NVector2 lanesOrigin)
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			if (!ImGui.AcceptDragDropPayload(ClipPayload).IsNull && draggedClipPath != null)
			{
				string path = draggedClipPath;
				draggedClipPath = null;
				Hit hit = HitTest(lanesOrigin, ImGui.GetMousePos());
				AudioTrack track = hit.Track;
				if (track == null && current.Tracks.Count > 0)
				{
					track = current.Tracks[current.Tracks.Count - 1];
				}

				if (track == null)
				{
					track = AudioProject.CreateTrack("Audio 1");
					AudioTrack added = track;
					Edit("Add track", () => current.Tracks.Add(added));
				}

				if (hit.Kind == HitKind.Instrument || hit.Kind == HitKind.InstrumentStart || hit.Kind == HitKind.InstrumentEnd)
				{
					// On a playlist instrument: a new entry.
					AudioInstrument target = current.FindInstrument(hit.Id, out _);
					List<AudioPlaylistEntry> playlist = (target as MultiInstrument)?.Playlist ?? (target as ScattererInstrument)?.Playlist;
					if (playlist != null)
					{
						Edit("Add playlist entry", () => playlist.Add(new AudioPlaylistEntry { Clip = AudioClipReference.FromPath(path) }));
						ImGui.EndDragDropTarget();
						return;
					}
				}

				AudioClip clip = AudioClip.Load(path);
				PlacementAt(current, hit.Display, out string sheet, out double start);
				double length = IsTimeSheet ? Math.Max(0.1, clip?.Duration ?? 1.0) : (sheetMax - sheetMin) * 0.25;
				AddInstrument(track, new SingleInstrument { Clip = AudioClipReference.FromPath(path) }, start, length, sheet);
			}

			ImGui.EndDragDropTarget();
		}

		private void DrawContextMenu(AudioEvent current)
		{
			if (openEditorContext)
			{
				ImGui.OpenPopup("##editor_context");
				openEditorContext = false;
			}

			if (!ImGui.BeginPopup("##editor_context"))
			{
				return;
			}

			Hit hit = contextHit;
			switch (hit.Kind)
			{
				case HitKind.Lane:
				{
					AudioTrack track = hit.Track;
					PlacementAt(current, hit.Display, out string sheet, out double at);
					bool onTransition = sheet != null && TransitionTimeline.TransitionOfSheet(sheet) != null;
					double length = IsTimeSheet ? DefaultLength(current) : (sheetMax - sheetMin) * 0.25;
					ImGui.TextDisabled(track.Name + (onTransition ? " on the transition timeline at +" : " at ") + (IsTimeSheet ? FormatTime(at) : at.ToString("0.##")));
					ImGui.Separator();
					if (EditorMenu.Item("Add Single Instrument")) AddInstrument(track, new SingleInstrument(), at, length, sheet);
					if (EditorMenu.Item("Add Multi Instrument")) AddInstrument(track, new MultiInstrument(), at, length, sheet);
					if (EditorMenu.Item("Add Scatterer Instrument")) AddInstrument(track, new ScattererInstrument(), at, Math.Max(length, IsTimeSheet ? 4.0 : length), sheet);
					if (EditorMenu.Item("Add Event Instrument")) AddInstrument(track, new EventInstrument(), at, length, sheet);
					if (EditorMenu.Item("Add Snapshot Instrument")) AddInstrument(track, new SnapshotInstrument { SnapshotId = Project.Snapshots.Count > 0 ? Project.Snapshots[0].Id : null }, at, length, sheet);
					ImGui.Separator();
					if (EditorMenu.Item("Paste Instrument", "", false, clipboardInstrument != null))
					{
						AudioInstrument copy = clipboardInstrument.Clone();
						copy.Id = AudioProject.NewId();
						copy.Sheet = sheet;
						copy.Start = at;
						Edit("Paste instrument", () => track.Instruments.Add(copy));
						selection = Selection.Of(SelectionKind.Instrument, copy.Id);
					}

					ImGui.Separator();
					DrawTrackMenu(current, track);
					break;
				}
				case HitKind.Instrument:
				case HitKind.InstrumentStart:
				case HitKind.InstrumentEnd:
				{
					AudioInstrument instrument = current.FindInstrument(hit.Id, out AudioTrack owner);
					if (instrument == null) break;
					ImGui.TextDisabled(instrument.DisplayName);
					ImGui.Separator();
					bool isAsync = instrument.Async;
					if (EditorMenu.Item("Async (plays to its end)", "", isAsync)) Edit("Toggle async", () => instrument.Async = !isAsync);
					bool loop = instrument.Loop;
					if (EditorMenu.Item("Loop", "", loop)) Edit("Toggle loop", () => instrument.Loop = !loop);
					ImGui.Separator();
					if (EditorMenu.Item("Copy")) clipboardInstrument = instrument.Clone();
					if (EditorMenu.Item("Duplicate", "Ctrl+D")) DuplicateSelection();
					if (EditorMenu.Begin("Move To Track"))
					{
						foreach (AudioTrack track in current.Tracks)
						{
							if (track != owner && EditorMenu.Item(track.Name))
							{
								AudioTrack target = track;
								Edit("Move instrument", () =>
								{
									owner.Instruments.Remove(instrument);
									target.Instruments.Add(instrument);
								});
							}
						}

						EditorMenu.End();
					}

					if (EditorMenu.Item("Fit Length To Sound", "", false, IsTimeSheet && instrument is SingleInstrument))
					{
						AudioClip clip = AudioClip.Load(((SingleInstrument)instrument).Clip);
						if (clip != null && clip.Duration > 0) Edit("Fit instrument", () => instrument.Length = clip.Duration);
					}

					ImGui.Separator();
					if (EditorMenu.Item("Delete", "Del")) DeleteSelection();
					break;
				}
				case HitKind.Logic:
				{
					PlacementAt(current, hit.Display, out string sheet, out double at);
					if (sheet != null)
					{
						DrawTransitionMenu(current, view.Marker);
						break;
					}

					ImGui.TextDisabled("Logic at " + FormatTime(at));
					ImGui.Separator();
					if (EditorMenu.Item("Add Destination Marker")) AddMarker(new DestinationMarker { Position = at, Name = "Marker " + (current.Markers.Count + 1) });
					if (EditorMenu.Item("Add Transition Marker")) AddMarker(new TransitionMarker { Position = at, DestinationId = FirstDestination(current), Name = "Transition" });
					if (EditorMenu.Item("Add Transition Region")) AddMarker(new TransitionRegion { Position = at, RegionLength = DefaultLength(current), DestinationId = FirstDestination(current), Name = "Transition Region" });
					if (EditorMenu.Item("Add Loop Region")) AddMarker(new LoopRegion { Position = at, RegionLength = DefaultLength(current), Name = "Loop" });
					if (EditorMenu.Item("Add Tempo Marker")) AddMarker(new TempoMarker { Position = at, Name = "Tempo" });
					if (EditorMenu.Item("Add Sustain Point")) AddMarker(new SustainPoint { Position = at, Name = "Sustain" });
					break;
				}
				case HitKind.TransitionHeader:
				case HitKind.SourceEnd:
				case HitKind.DestinationStart:
				case HitKind.TimelineEnd:
				case HitKind.SourceFade:
				case HitKind.DestinationFade:
					if (view.Active)
					{
						DrawTransitionMenu(current, view.Marker);
					}

					break;
				case HitKind.Marker:
				case HitKind.MarkerEnd:
				case HitKind.TransitionBadge:
				{
					AudioMarker marker = current.FindMarker(hit.Id);
					if (marker == null) break;
					ImGui.TextDisabled(marker.Kind + ": " + (marker.Name ?? ""));
					ImGui.Separator();
					if (marker is TransitionMarker transition)
					{
						if (transition.Timeline == null)
						{
							if (EditorMenu.Item("Add Transition Timeline")) OpenTransitionTimeline(transition);
						}
						else
						{
							if (EditorMenu.Item(expandedTransitionId == transition.Id ? "Close Transition Timeline" : "Open Transition Timeline"))
							{
								if (expandedTransitionId == transition.Id) CloseTransitionTimeline();
								else OpenTransitionTimeline(transition);
							}

							if (EditorMenu.Item("Remove Transition Timeline")) RemoveTransitionTimeline(transition);
						}

						ImGui.Separator();
					}

					if (EditorMenu.Item("Duplicate", "Ctrl+D")) DuplicateSelection();
					if (EditorMenu.Item("Delete", "Del")) DeleteSelection();
					break;
				}
				default:
					ImGui.TextDisabled("Nothing here");
					break;
			}

			ImGui.EndPopup();
		}

		private void DrawTransitionMenu(AudioEvent current, TransitionMarker transition)
		{
			if (transition?.Timeline == null)
			{
				return;
			}

			ImGui.TextDisabled("Transition timeline to " + DestinationName(current, transition.DestinationId));
			ImGui.Separator();
			if (EditorMenu.Item("Plain crossfade over the whole timeline"))
			{
				Edit("Transition crossfade", () =>
				{
					TransitionTimeline timeline = transition.Timeline;
					timeline.SourceLength = timeline.Length;
					timeline.DestinationStart = 0.0;
					timeline.SourceFadeOut.Length = timeline.Length;
					timeline.DestinationFadeIn.Length = timeline.Length;
				});
			}

			if (EditorMenu.Item("Cut (no overlap, no fades)"))
			{
				Edit("Transition cut", () =>
				{
					TransitionTimeline timeline = transition.Timeline;
					timeline.SourceLength = 0.0;
					timeline.DestinationStart = 0.0;
					timeline.SourceFadeOut.Length = 0.0;
					timeline.DestinationFadeIn.Length = 0.0;
				});
			}

			ImGui.Separator();
			if (EditorMenu.Item("Close Transition Timeline", "Esc")) CloseTransitionTimeline();
			if (EditorMenu.Item("Remove Transition Timeline")) RemoveTransitionTimeline(transition);
		}

		private static string FirstDestination(AudioEvent current)
		{
			foreach (AudioMarker marker in current.Markers)
			{
				if (marker is DestinationMarker)
				{
					return marker.Id;
				}
			}

			return TransitionMarker.EndDestination;
		}

		/// <summary>A sensible default length: one bar with a tempo, else one second.</summary>
		private static double DefaultLength(AudioEvent current)
		{
			List<TempoMarker> tempos = current.TempoMarkers;
			return tempos.Count > 0 ? tempos[0].SecondsPerBeat * Math.Max(1, tempos[0].BeatsPerBar) : 1.0;
		}

		#endregion
	}
}
