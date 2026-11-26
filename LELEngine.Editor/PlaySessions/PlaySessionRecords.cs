using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LELEngine.Editor
{
	/// <summary>
	///     One frame of a recorded play session: a line of its frames.csv (<see cref="PlaySessionWriter.FrameColumns" />).
	///     Filled on the main thread from what the Game view's debug overlay shows and a few more counters, written by the
	///     session's thread. NaN in a float, -1 in a count: not known this frame (the view did not render), an empty field.
	/// </summary>
	internal struct SessionFrame
	{
		public long Row;
		public double Seconds;
		public long GameFrame;
		public bool Step;
		public float DeltaMs;
		public float TimeScale;
		public float FrameMs;
		public float UpdateMs;
		public float RenderMs;
		public float GpuMs;
		public float SceneViewMs;
		public float SwapMs;
		public float AllocatedKB;
		public float ProcessAllocatedKB;
		public byte Collected;
		public float GcPauseMs;
		public int JitMethods;
		public float JitMs;
		public long DrawCalls;
		public long IndirectDrawCalls;
		public long Triangles;
		public int Opaque;
		public int Transparent;
		public int Culled;
		public int GpuInstances;
		public int Lights;
		public int Objects;
		public int Behaviours;
		public int Bodies;
		public int Particles;
		public float HeapMB;
		public float ProcessMB;
		public int PoolThreads;
		public long PoolQueue;
		public float CameraX;
		public float CameraY;
		public float CameraZ;
		public float MouseX;
		public float MouseY;
	}

	/// <summary>
	///     Something that happened at a moment of a play session: a line of its events.jsonl, one JSON object with the
	///     session's time ("t", seconds), the frames.csv row of the frame it happened in ("row": the row that frame gets when
	///     it ends; row 0 is the frame that entered play mode, so the scene's load and start are in it; while paused, the
	///     row the next frame of the game gets), the game's frame ("frame", Time.frameCount, which stands still while
	///     paused) and the kind ("kind") first.
	/// </summary>
	internal abstract class SessionEvent
	{
		public double Seconds;
		public long Row;
		public long Frame;

		public abstract string Kind { get; }

		public void Write(Utf8JsonWriter json)
		{
			json.WriteStartObject();
			json.WriteNumber("t", Math.Round(Seconds, 4));
			json.WriteNumber("row", Row);
			json.WriteNumber("frame", Frame);
			json.WriteString("kind", Kind);
			WriteFields(json);
			json.WriteEndObject();
		}

		protected abstract void WriteFields(Utf8JsonWriter json);
	}

	/// <summary>A message of the console (the game's or the editor's) logged during the session.</summary>
	internal sealed class SessionLogEvent : SessionEvent
	{
		public long Id;
		public LogType Type;
		public ConsoleSource Source;
		public string Text;

		/// <summary>The whole call stack (warnings, errors, exceptions) or its first line (plain messages: where it was logged).</summary>
		public string Stack;

		public bool FirstLineOnly;

		public override string Kind => "log";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteNumber("id", Id);
			json.WriteString("type", Type.ToString());
			json.WriteString("source", Source.ToString());
			json.WriteString("text", Text);
			if (!string.IsNullOrEmpty(Stack))
			{
				json.WriteString(FirstLineOnly ? "at" : "stack", Stack);
			}
		}
	}

	/// <summary>A change of the session's state: paused, resumed, a step, the scripts reloaded, the editor in front or not.</summary>
	internal sealed class SessionNoteEvent : SessionEvent
	{
		public string What;
		public string Detail;

		public override string Kind => "state";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteString("state", What);
			if (Detail != null)
			{
				json.WriteString("detail", Detail);
			}
		}
	}

	/// <summary>What the player did in a frame: keys and mouse buttons pressed and released, the wheel, the input actions that changed.</summary>
	internal sealed class SessionInputEvent : SessionEvent
	{
		public int[] KeysDown;
		public int[] KeysUp;
		public int MouseDown;
		public int MouseUp;
		public float WheelX;
		public float WheelY;

		/// <summary>The input system's actions whose value changed ("Player/Action", the value now).</summary>
		public KeyValuePair<string, float>[] Actions;

		public override string Kind => "input";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			Keys(json, "keysDown", KeysDown);
			Keys(json, "keysUp", KeysUp);
			Buttons(json, "mouseDown", MouseDown);
			Buttons(json, "mouseUp", MouseUp);
			if (WheelX != 0f || WheelY != 0f)
			{
				json.WriteStartArray("wheel");
				json.WriteNumberValue(WheelX);
				json.WriteNumberValue(WheelY);
				json.WriteEndArray();
			}

			if (Actions != null && Actions.Length > 0)
			{
				json.WriteStartObject("actions");
				foreach (KeyValuePair<string, float> action in Actions)
				{
					json.WriteNumber(action.Key, MathF.Round(action.Value, 3));
				}

				json.WriteEndObject();
			}
		}

		private static void Keys(Utf8JsonWriter json, string name, int[] keys)
		{
			if (keys == null || keys.Length == 0)
			{
				return;
			}

			json.WriteStartArray(name);
			foreach (int key in keys)
			{
				json.WriteStringValue(((LELEngine.Windowing.Keys)key).ToString());
			}

			json.WriteEndArray();
		}

		private static void Buttons(Utf8JsonWriter json, string name, int bits)
		{
			if (bits == 0)
			{
				return;
			}

			json.WriteStartArray(name);
			for (int button = 0; button < 32; button++)
			{
				if ((bits & (1 << button)) != 0)
				{
					json.WriteStringValue(button switch { 0 => "Left", 1 => "Right", 2 => "Middle", _ => "Button" + (button + 1) });
				}
			}

			json.WriteEndArray();
		}
	}

	/// <summary>A scene loaded, unloaded or made the active one.</summary>
	internal sealed class SessionSceneEvent : SessionEvent
	{
		public string Action;
		public string Name;
		public string Path;
		public string Mode;

		public override string Kind => "scene";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteString("action", Action);
			json.WriteString("name", Name);
			if (!string.IsNullOrEmpty(Path))
			{
				json.WriteString("path", Path);
			}

			if (Mode != null)
			{
				json.WriteString("mode", Mode);
			}
		}
	}

	/// <summary>The Game view's size changed (its image, and the scene's when super sampled; the cameras drawn).</summary>
	internal sealed class SessionViewEvent : SessionEvent
	{
		public int Width;
		public int Height;
		public int RenderWidth;
		public int RenderHeight;
		public int Cameras;

		public override string Kind => "view";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteNumber("width", Width);
			json.WriteNumber("height", Height);
			json.WriteNumber("renderWidth", RenderWidth);
			json.WriteNumber("renderHeight", RenderHeight);
			json.WriteNumber("cameras", Cameras);
		}
	}

	/// <summary>The Game view's GPU passes (timer queries, a few frames old), once a second.</summary>
	internal sealed class SessionGpuEvent : SessionEvent
	{
		public KeyValuePair<string, double>[] Passes;

		public override string Kind => "gpu";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			double total = 0;
			foreach (KeyValuePair<string, double> pass in Passes)
			{
				total += pass.Value;
			}

			json.WriteNumber("totalMs", Math.Round(total, 3));
			json.WriteStartObject("passes");
			foreach (KeyValuePair<string, double> pass in Passes)
			{
				json.WriteNumber(pass.Key, Math.Round(pass.Value, 3));
			}

			json.WriteEndObject();
		}
	}

	/// <summary>
	///     A frame much longer than the ones before it (over the hitch profiler's threshold and its factor over their median):
	///     where its time went, as frames.csv has it, so hitches are found without reading every row.
	/// </summary>
	internal sealed class SessionHitchEvent : SessionEvent
	{
		public float FrameMs;
		public float MedianMs;
		public float UpdateMs;
		public float RenderMs;
		public float GpuMs;
		public float SwapMs;
		public float GcPauseMs;
		public int Collected;
		public float JitMs;
		public int JitMethods;

		public override string Kind => "hitch";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteNumber("frameMs", MathF.Round(FrameMs, 2));
			json.WriteNumber("medianMs", MathF.Round(MedianMs, 2));
			json.WriteNumber("updateMs", MathF.Round(UpdateMs, 2));
			json.WriteNumber("renderMs", MathF.Round(RenderMs, 2));
			json.WriteNumber("gpuMs", MathF.Round(GpuMs, 2));
			json.WriteNumber("swapMs", MathF.Round(SwapMs, 2));
			if (Collected > 0)
			{
				json.WriteNumber("gcGeneration", Collected - 1);
				json.WriteNumber("gcPauseMs", MathF.Round(GcPauseMs, 2));
			}

			if (JitMethods > 0)
			{
				json.WriteNumber("jitMethods", JitMethods);
				json.WriteNumber("jitMs", MathF.Round(JitMs, 2));
			}
		}
	}

	/// <summary>A text of its own: the hitch profiler's report of a frame, an exception nobody caught.</summary>
	internal sealed class SessionTextEvent : SessionEvent
	{
		public string What;
		public string Text;

		public override string Kind => What;

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteString("text", Text);
		}
	}

	/// <summary>A game controller connected or disconnected.</summary>
	internal sealed class SessionControllerEvent : SessionEvent
	{
		public bool Connected;
		public string Name;
		public string Type;

		public override string Kind => "controller";

		protected override void WriteFields(Utf8JsonWriter json)
		{
			json.WriteBoolean("connected", Connected);
			json.WriteString("name", Name);
			json.WriteString("type", Type);
		}
	}
}
