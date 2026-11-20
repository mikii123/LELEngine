using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The Game view's debug overlay (the toolbar's Debug Overlay, kept in the editor's preferences): a panel in the top
	///     right corner of the game's image with the game's statistics and graphs of the last five seconds
	///     (<see cref="GameViewStats" />). Drawn with the window's draw list over the image: no UI elements, nothing of the
	///     game's input or image changes, captures of the window show it.
	///     <list type="bullet">
	///         <item>frames a second, the frame time (average, shortest, longest of the last second);</item>
	///         <item>CPU: the game's update (scripts, physics, loads) and the Game view's render; GPU: the render's passes
	///         (timer queries: a few frames behind), the three heaviest named;</item>
	///         <item>graphs: the frame time against 60 and 30 frames a second; the update, the render and the GPU; what the
	///         game's update allocated each frame, with the garbage collections (yellow, orange, red: generation 0, 1, 2);</item>
	///         <item>the view (its size, the scene's when super sampled), draw calls and triangles
	///         (<see cref="DrawStatistics" />), the renderers in the cameras' lists, the GPU-driven instances, the lights;</item>
	///         <item>the loaded scenes' objects, updating components, physics bodies and particles; the managed heap, the
	///         process's memory, allocations a frame and the collections so far.</item>
	///     </list>
	/// </summary>
	internal sealed class GameViewOverlay
	{
		#region PrivateFields

		private static readonly NVector4 Background = new NVector4(0.05f, 0.055f, 0.065f, 0.82f);
		private static readonly NVector4 Border = new NVector4(1f, 1f, 1f, 0.1f);
		private static readonly NVector4 GraphBackground = new NVector4(0f, 0f, 0f, 0.35f);
		private static readonly NVector4 Guide = new NVector4(1f, 1f, 1f, 0.16f);
		private static readonly NVector4 GuideText = new NVector4(1f, 1f, 1f, 0.38f);
		private static readonly NVector4 Label = new NVector4(0.6f, 0.63f, 0.68f, 1f);
		private static readonly NVector4 Value = new NVector4(0.94f, 0.95f, 0.97f, 1f);
		private static readonly NVector4 FrameColor = new NVector4(0.94f, 0.95f, 0.97f, 1f);
		private static readonly NVector4 UpdateColor = new NVector4(1f, 0.63f, 0.26f, 1f);
		private static readonly NVector4 RenderColor = new NVector4(0.45f, 0.86f, 0.45f, 1f);
		private static readonly NVector4 GpuColor = new NVector4(0.38f, 0.72f, 1f, 1f);
		private static readonly NVector4 AllocationColor = new NVector4(0.72f, 0.55f, 1f, 1f);
		private static readonly NVector4 Spike = new NVector4(1f, 0.32f, 0.3f, 1f);
		private static readonly NVector4[] CollectionColors =
		{
			new NVector4(1f, 0.86f, 0.3f, 0.75f),
			new NVector4(1f, 0.58f, 0.22f, 0.85f),
			new NVector4(1f, 0.3f, 0.28f, 0.95f)
		};

		// Scales of the time graphs (ms) and of the allocation graph (KB): the smallest that holds the samples, spikes above
		// the last one clipped (and marked) so one long frame (entering play mode) does not flatten the rest.
		private static readonly float[] TimeScales = { 4f, 8f, 16.7f, 33.3f, 66.7f, 100f };
		private static readonly float[] AllocationScales = { 16f, 64f, 256f, 1024f, 4096f, 16384f };

		// The numbers that change slowly or cost a little to gather are read four times a second.
		private const double RefreshSeconds = 0.25;

		private readonly GameViewStats stats;
		private readonly NVector2[] points = new NVector2[GameViewStats.Capacity];
		private readonly List<KeyValuePair<string, double>> heaviestPasses = new List<KeyValuePair<string, double>>();
		private long nextRefresh;
		private int objects;
		private int behaviours;
		private int bodies;
		private int particles;
		private int particleCapacity;
		private long heapBytes;
		private long processBytes;

		// The layout of the panel being drawn.
		private ImDrawListPtr draw;
		private float left;
		private float width;
		private float y;
		private float lineHeight;
		private float labelWidth;

		#endregion

		#region Constructors

		public GameViewOverlay(GameViewStats stats)
		{
			this.stats = stats;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Draws the panel over the image at <paramref name="imageMin" /> of <paramref name="imageSize" /> (clipped to it), with
		///     the frame statistics of <paramref name="renderer" /> (the renderer that drew the image; null: none yet).
		/// </summary>
		public void Draw(NVector2 imageMin, NVector2 imageSize, Renderer renderer)
		{
			RefreshSlowNumbers(renderer);
			float font = ImGui.GetFontSize();
			float margin = MathF.Round(font * 0.6f);
			float padding = MathF.Round(font * 0.55f);
			width = MathF.Round(font * 25f);
			lineHeight = ImGui.GetTextLineHeight() + 1f;
			labelWidth = MathF.Round(font * 5.6f);
			NVector2 imageMax = imageMin + imageSize;
			float panelLeft = MathF.Max(imageMin.X + margin, imageMax.X - margin - width - padding * 2f);
			float top = imageMin.Y + margin;
			left = panelLeft + padding;
			y = top + padding;

			draw = ImGui.GetWindowDrawList();
			draw.PushClipRect(imageMin, imageMax, true);
			// The content first on the upper channel, then the background behind it, as tall as the content came out.
			draw.ChannelsSplit(2);
			draw.ChannelsSetCurrent(1);
			DrawContent(renderer, font);
			draw.ChannelsSetCurrent(0);
			var panelMax = new NVector2(panelLeft + width + padding * 2f, y + padding - 1f);
			draw.AddRectFilled(new NVector2(panelLeft, top), panelMax, Color(Background), font * 0.3f);
			draw.AddRect(new NVector2(panelLeft, top), panelMax, Color(Border), font * 0.3f);
			draw.ChannelsMerge();
			draw.PopClipRect();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent(Renderer renderer, float font)
		{
			GameViewStats.Summary second = stats.Summarize(1f);
			RenderFrameStatistics frame = renderer?.LastFrame ?? default;

			// Frames a second and the frame time.
			float x = left;
			Text(ref x, Value, second.Frames > 0 ? Number(second.FramesPerSecond, 1) + " FPS" : "- FPS");
			x += font * 0.8f;
			Text(ref x, Label, second.Frames > 0
				? $"{Number(second.FrameAverage, 2)} ms  ({Number(second.FrameMin, 1)} - {Number(second.FrameMax, 1)})"
				: "no frames yet");
			NextLine();

			// Where the time went: the game's update, the view's render on the CPU, and on the GPU.
			x = left;
			Text(ref x, Label, "CPU");
			x = left + labelWidth;
			Text(ref x, UpdateColor, "game ");
			Text(ref x, Value, Number(second.Update, 2));
			x += font * 0.8f;
			Text(ref x, RenderColor, "render ");
			Text(ref x, Value, second.Rendered > 0 ? Number(second.Render, 2) : "-");
			Text(ref x, Label, " ms");
			NextLine();

			x = left;
			Text(ref x, Label, "GPU");
			x = left + labelWidth;
			Text(ref x, GpuColor, "render ");
			Text(ref x, Value, second.Rendered > 0 ? Number(second.Gpu, 2) : "-");
			Text(ref x, Label, second.Rendered > 0 ? " ms" : " ms  (the view is not rendering)");
			NextLine();
			if (second.Rendered > 0 && heaviestPasses.Count > 0)
			{
				x = left + labelWidth;
				foreach (KeyValuePair<string, double> pass in heaviestPasses)
				{
					string item = pass.Key + " " + Number((float)pass.Value, 2);
					if (x + ImGui.CalcTextSize(item).X > left + width)
					{
						break;
					}

					Text(ref x, Label, item);
					x += font * 0.8f;
				}

				NextLine();
			}

			y += font * 0.2f;
			TimeGraph("frame time", FrameColor, stats.FrameMs, null, null, font);
			TimeGraph(null, UpdateColor, stats.UpdateMs, stats.RenderMs, stats.GpuMs, font);

			// What the view drew.
			x = left;
			Text(ref x, Label, "View");
			x = left + labelWidth;
			if (frame.Frame == 0)
			{
				Text(ref x, Label, "not rendered yet");
			}
			else
			{
				Text(ref x, Value, $"{frame.Width}x{frame.Height}");
				if (frame.RenderWidth != frame.Width || frame.RenderHeight != frame.Height)
				{
					Text(ref x, Label, $"  scene {frame.RenderWidth}x{frame.RenderHeight}");
				}

				if (frame.Cameras > 1)
				{
					Text(ref x, Label, $"  {frame.Cameras} cameras");
				}
			}

			NextLine();
			x = left;
			Text(ref x, Label, "Draws");
			x = left + labelWidth;
			Text(ref x, Value, Count(frame.DrawCalls));
			Text(ref x, Label, frame.IndirectDrawCalls > 0 ? $" ({Count(frame.IndirectDrawCalls)} indirect)" : "");
			x += font * 0.8f;
			Text(ref x, Value, Count(frame.Triangles));
			Text(ref x, Label, frame.IndirectDrawCalls > 0 ? " tris + indirect" : " tris");
			NextLine();

			x = left;
			Text(ref x, Label, "Renderers");
			x = left + labelWidth;
			Text(ref x, Value, Count(frame.OpaqueRenderers));
			Text(ref x, Label, " opaque  ");
			Text(ref x, Value, Count(frame.TransparentRenderers));
			Text(ref x, Label, " transparent");
			NextLine();

			x = left;
			Text(ref x, Label, "Lights");
			x = left + labelWidth;
			Text(ref x, Value, Count(frame.Lights));
			if (frame.GpuInstances > 0)
			{
				x += font * 0.8f;
				Text(ref x, Value, Count(frame.GpuInstances));
				Text(ref x, Label, " GPU-driven instances");
			}

			NextLine();

			// The game's world.
			y += font * 0.2f;
			x = left;
			Text(ref x, Label, "Scene");
			x = left + labelWidth;
			Text(ref x, Value, Count(objects));
			Text(ref x, Label, " objects  ");
			Text(ref x, Value, Count(behaviours));
			Text(ref x, Label, " updating");
			NextLine();

			x = left;
			Text(ref x, Label, "Physics");
			x = left + labelWidth;
			Text(ref x, Value, Count(bodies));
			Text(ref x, Label, " bodies  ");
			Text(ref x, Label, "particles ");
			Text(ref x, Value, Count(particles));
			Text(ref x, Label, " / " + Count(particleCapacity));
			NextLine();

			// Memory and the garbage collector.
			y += font * 0.2f;
			x = left;
			Text(ref x, Label, "Memory");
			x = left + labelWidth;
			Text(ref x, Label, "heap ");
			Text(ref x, Value, Bytes(heapBytes));
			Text(ref x, Label, "  process ");
			Text(ref x, Value, Bytes(processBytes));
			NextLine();

			x = left;
			Text(ref x, Label, "Allocated");
			x = left + labelWidth;
			Text(ref x, AllocationColor, Kilobytes(second.AllocatedKB));
			Text(ref x, Label, " a frame  GC ");
			Text(ref x, Value, $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}");
			NextLine();

			y += font * 0.2f;
			AllocationGraph(font);
		}

		/// <summary>
		///     A graph of up to three time series (ms) over the samples kept: lines from the oldest at the left to the newest
		///     at the right edge, the 60 and 30 frames a second times as guides when the scale reaches them.
		/// </summary>
		private void TimeGraph(string title, NVector4 color, float[] first, float[] second, float[] third, float font)
		{
			float peak = Peak(first);
			if (second != null)
			{
				peak = MathF.Max(peak, Peak(second));
			}

			if (third != null)
			{
				peak = MathF.Max(peak, Peak(third));
			}

			float scale = PickScale(peak, TimeScales);

			// The title (the series' colours) and the scale (the graph's top) above the graph.
			float x = left;
			if (second == null)
			{
				Text(ref x, color, title);
			}
			else
			{
				Text(ref x, UpdateColor, "game  ");
				Text(ref x, RenderColor, "render  ");
				Text(ref x, GpuColor, "GPU");
			}

			Text(ref x, GuideText, "   up to " + Number(scale, scale < 10f ? 0 : 1) + " ms");
			NextLine();

			float height = MathF.Round(font * 3.4f);
			var min = new NVector2(left, y);
			var max = new NVector2(left + width, y + height);
			draw.AddRectFilled(min, max, Color(GraphBackground));
			// The labels at the left: the newest samples are at the right edge.
			foreach (float guide in new[] { 16.7f, 33.3f })
			{
				if (guide < scale)
				{
					float gy = max.Y - guide / scale * height;
					draw.AddLine(new NVector2(min.X, gy), new NVector2(max.X, gy), Color(Guide));
					draw.AddText(new NVector2(min.X + 3f, gy - lineHeight + 2f), Color(GuideText), Number(guide, 1));
				}
			}

			// The GPU and the render under the first series (the frame, or the game's own update).
			if (third != null)
			{
				Series(min, max, third, scale, GpuColor);
			}

			if (second != null)
			{
				Series(min, max, second, scale, RenderColor);
			}

			Series(min, max, first, scale, color);
			y = max.Y + font * 0.35f;
		}

		/// <summary>What the game's update allocated each frame (bars), and the frames with a garbage collection (lines).</summary>
		private void AllocationGraph(float font)
		{
			float scale = PickScale(Peak(stats.AllocatedKB), AllocationScales);
			float tx = left;
			Text(ref tx, AllocationColor, "allocated a frame  ");
			Text(ref tx, CollectionColors[0], "GC");
			Text(ref tx, GuideText, "   up to " + Kilobytes(scale));
			NextLine();

			float height = MathF.Round(font * 2.6f);
			var min = new NVector2(left, y);
			var max = new NVector2(left + width, y + height);
			draw.AddRectFilled(min, max, Color(GraphBackground));
			int count = stats.Count;
			float step = width / (GameViewStats.Capacity - 1);
			float half = MathF.Max(0.5f, step * 0.5f);
			uint bar = Color(AllocationColor * new NVector4(1f, 1f, 1f, 0.7f));
			for (int i = 0; i < count; i++)
			{
				float value = stats.AllocatedKB[stats.Slot(i)];
				if (value > 0f)
				{
					float x = SampleX(min.X, step, i, count);
					float barTop = max.Y - MathF.Min(1f, value / scale) * height;
					draw.AddRectFilled(new NVector2(x - half, MathF.Min(barTop, max.Y - 1f)), new NVector2(x + half, max.Y), value > scale ? Color(Spike) : bar);
				}
			}

			// The collections over the bars.
			for (int i = 0; i < count; i++)
			{
				int collected = stats.Collections[stats.Slot(i)];
				if (collected > 0)
				{
					float x = SampleX(min.X, step, i, count);
					draw.AddLine(new NVector2(x, min.Y), new NVector2(x, max.Y), Color(CollectionColors[collected - 1]), 1.5f);
				}
			}

			y = max.Y;
		}

		/// <summary>One series as a line; the samples above the scale sit on the top edge and get a red mark there.</summary>
		private void Series(NVector2 min, NVector2 max, float[] values, float scale, NVector4 color)
		{
			int count = stats.Count;
			if (count < 2)
			{
				return;
			}

			float height = max.Y - min.Y;
			float step = (max.X - min.X) / (GameViewStats.Capacity - 1);
			for (int i = 0; i < count; i++)
			{
				float value = values[stats.Slot(i)];
				float x = SampleX(min.X, step, i, count);
				points[i] = new NVector2(x, max.Y - MathF.Min(1f, value / scale) * (height - 1f));
				if (value > scale)
				{
					draw.AddRectFilled(new NVector2(x - 1f, min.Y), new NVector2(x + 1f, min.Y + 3f), Color(Spike));
				}
			}

			draw.AddPolyline(ref points[0], count, Color(color), ImDrawFlags.None, 1.3f);
		}

		// The newest sample at the right edge, older ones to the left of it.
		private static float SampleX(float left, float step, int index, int count)
		{
			return left + (GameViewStats.Capacity - count + index) * step;
		}

		private float Peak(float[] values)
		{
			float peak = 0f;
			for (int i = 0; i < stats.Count; i++)
			{
				peak = MathF.Max(peak, values[stats.Slot(i)]);
			}

			return peak;
		}

		private static float PickScale(float peak, float[] scales)
		{
			foreach (float scale in scales)
			{
				if (peak <= scale)
				{
					return scale;
				}
			}

			return scales[scales.Length - 1];
		}

		/// <summary>The scene counts, the memory and the heaviest passes, four times a second.</summary>
		private void RefreshSlowNumbers(Renderer renderer)
		{
			long now = Stopwatch.GetTimestamp();
			if (now < nextRefresh)
			{
				return;
			}

			nextRefresh = now + (long)(RefreshSeconds * Stopwatch.Frequency);
			objects = 0;
			behaviours = 0;
			bodies = 0;
			foreach (Scene scene in SceneManager.LoadedScenes)
			{
				objects += scene.GameObjects.Count;
				behaviours += scene.ActiveBehaviours.Count;
				bodies += scene.Physics2DIfCreated?.bodyCount ?? 0;
			}

			particles = 0;
			particleCapacity = 0;
			foreach (LELEngine.VFX.VfxInstance effect in LELEngine.VFX.VfxRuntime.Instances)
			{
				particles += effect.AliveCount;
				particleCapacity += effect.Capacity;
			}

			heapBytes = GC.GetTotalMemory(false);
			processBytes = Environment.WorkingSet;
			heaviestPasses.Clear();
			if (renderer != null)
			{
				heaviestPasses.AddRange(renderer.Profiler.Results);
				heaviestPasses.Sort((a, b) => b.Value.CompareTo(a.Value));
				if (heaviestPasses.Count > 3)
				{
					heaviestPasses.RemoveRange(3, heaviestPasses.Count - 3);
				}

				heaviestPasses.RemoveAll(pass => pass.Value < 0.01);
			}
		}

		private void Text(ref float x, NVector4 color, string text)
		{
			Text(ref x, y, color, text);
		}

		private void Text(ref float x, float top, NVector4 color, string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			draw.AddText(new NVector2(MathF.Round(x), MathF.Round(top)), Color(color), text);
			x += ImGui.CalcTextSize(text).X;
		}

		private void NextLine()
		{
			y += lineHeight;
		}

		private static uint Color(NVector4 color)
		{
			return ImGui.ColorConvertFloat4ToU32(color);
		}

		private static string Number(float value, int decimals)
		{
			return value.ToString(decimals switch { 0 => "0", 1 => "0.0", _ => "0.00" }, CultureInfo.InvariantCulture);
		}

		private static string Count(long value)
		{
			if (value < 100_000)
			{
				return value.ToString("N0", CultureInfo.InvariantCulture);
			}

			return value < 10_000_000
				? (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " K"
				: (value / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " M";
		}

		private static string Bytes(long bytes)
		{
			double megabytes = bytes / (1024.0 * 1024.0);
			return megabytes >= 1024.0
				? (megabytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
				: megabytes.ToString(megabytes >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
		}

		private static string Kilobytes(float kilobytes)
		{
			return kilobytes >= 1024f
				? (kilobytes / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
				: kilobytes.ToString(kilobytes >= 100f ? "0" : "0.0", CultureInfo.InvariantCulture) + " KB";
		}

		#endregion
	}
}
