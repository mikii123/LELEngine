using System;
using System.Collections.Generic;
using System.Reflection;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The inspector's <see cref="AnimationCurve" /> field: a preview of the curve on the field's line; a click opens
	///     the curve editor in a popup.
	///     <list type="bullet">
	///         <item>presets (constant, linear, ease in / out / in-out, bell, step), mapped onto the curve's current ranges;</item>
	///         <item>keys: double click adds one, drag moves it (between its neighbours), Delete removes the selected one;</item>
	///         <item>tangents: the selected key's handles; right click on a key: Clamped Auto, Free, Linear, Constant, Broken, Delete;</item>
	///         <item>view: mouse wheel zooms (Shift: time only, Ctrl: values only), middle or Alt + left drag pans, F frames;</item>
	///         <item>the selected key's numbers and mode, and the wrap modes before and after the keys.</item>
	///     </list>
	///     Edits go through the inspector's undo (one step per drag or click).
	/// </summary>
	internal static class CurveEditor
	{
		#region PrivateFields

		private const float CanvasHeight = 250f;
		private const float HandleLength = 46f;
		private const float KeyRadius = 5f;

		private static readonly NVector4 CurveColor = new NVector4(0.45f, 0.8f, 1f, 1f);
		private static readonly NVector4 WrapColor = new NVector4(0.45f, 0.8f, 1f, 0.35f);
		private static readonly NVector4 GridColor = new NVector4(1f, 1f, 1f, 0.07f);
		private static readonly NVector4 AxisColor = new NVector4(1f, 1f, 1f, 0.2f);
		private static readonly NVector4 HandleColor = new NVector4(0.85f, 0.85f, 0.85f, 0.9f);

		private static readonly (string Name, Func<AnimationCurve> Create)[] Presets =
		{
			("Constant", () => AnimationCurve.Constant(0f, 1f, 1f)),
			("Linear", () => AnimationCurve.Linear(0f, 0f, 1f, 1f)),
			("Linear (falling)", () => AnimationCurve.Linear(0f, 1f, 1f, 0f)),
			("Ease in", () => AnimationCurve.EaseIn(0f, 0f, 1f, 1f)),
			("Ease out", () => AnimationCurve.EaseOut(0f, 0f, 1f, 1f)),
			("Ease in-out", () => AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)),
			("Ease in-out (falling)", () => AnimationCurve.EaseInOut(0f, 1f, 1f, 0f)),
			("Bell", Bell),
			("Step", Step)
		};

		private enum Drag
		{
			None,
			Key,
			InHandle,
			OutHandle,
			Pan
		}

		// One curve editor is open at a time (a popup): its view and selection.
		private static AnimationCurve editing;
		private static float timeMin;
		private static float timeMax = 1f;
		private static float valueMin;
		private static float valueMax = 1f;
		private static int selected = -1;
		private static int menuKey = -1;
		private static float menuTime;
		private static float menuValue;
		private static Drag drag;
		private static bool dragChanged;

		#endregion

		#region PublicMethods

		/// <summary>The field line: label, preview, popup. True when the curve changed (or was created).</summary>
		public static bool DrawField(string label, ref object value, FieldInfo info, InspectorContext context)
		{
			RowDecoration row = FieldDrawer.BeginLine(label, info, context);
			var curve = value as AnimationCurve;
			bool changed = false;

			var size = new NVector2(Math.Max(40f, ImGui.CalcItemWidth()), ImGui.GetFrameHeight());
			NVector2 min = ImGui.GetCursorScreenPos();
			bool clicked = EditorGui.InvisibleButton("##curvefield", size);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			bool hovered = ImGui.IsItemHovered();
			draw.AddRectFilled(min, min + size, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
			if (curve != null && curve.length > 0)
			{
				DrawPreview(draw, curve, min + new NVector2(3f, 3f), min + size - new NVector2(3f, 3f), ImGui.GetColorU32(CurveColor), 1.5f);
			}
			else
			{
				draw.AddText(min + new NVector2(6f, ImGui.GetStyle().FramePadding.Y), ImGui.GetColorU32(ImGuiCol.TextDisabled), "(no keys: click to edit)");
			}

			if (hovered)
			{
				ImGui.SetTooltip("Click to edit the curve");
			}

			if (clicked)
			{
				ImGui.OpenPopup("##curveeditor");
				if (curve == null)
				{
					context.BeginEdit();
					curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
					value = curve;
					changed = true;
					context.EndEdit();
				}

				Open(curve);
			}

			if (curve != null && ImGui.BeginPopup("##curveeditor"))
			{
				if (!ReferenceEquals(editing, curve))
				{
					Open(curve);
				}

				changed |= DrawEditor(curve, context);
				ImGui.EndPopup();
			}

			FieldDrawer.EndLine(row);
			return changed;
		}

		/// <summary>The curve over its key range, fitted into a rectangle (field previews and preset buttons).</summary>
		public static void DrawPreview(ImDrawListPtr draw, AnimationCurve curve, NVector2 min, NVector2 max, uint color, float thickness)
		{
			curve.GetRange(out float t0, out float t1);
			if (t1 - t0 < 1e-6f)
			{
				t1 = t0 + 1f;
			}

			int samples = Math.Max(8, (int)(max.X - min.X) / 2);
			float low = float.MaxValue;
			float high = float.MinValue;
			var values = new float[samples + 1];
			for (int i = 0; i <= samples; i++)
			{
				values[i] = curve.Evaluate(t0 + (t1 - t0) * i / samples);
				low = Math.Min(low, values[i]);
				high = Math.Max(high, values[i]);
			}

			if (high - low < 1e-6f)
			{
				low -= 0.5f;
				high += 0.5f;
			}

			NVector2 previous = default;
			for (int i = 0; i <= samples; i++)
			{
				var point = new NVector2(min.X + (max.X - min.X) * i / samples, max.Y - (max.Y - min.Y) * (values[i] - low) / (high - low));
				if (i > 0)
				{
					draw.AddLine(previous, point, color, thickness);
				}

				previous = point;
			}
		}

		#endregion

		#region PrivateMethods

		private static void Open(AnimationCurve curve)
		{
			editing = curve;
			selected = -1;
			drag = Drag.None;
			Frame(curve);
		}

		private static bool DrawEditor(AnimationCurve curve, InspectorContext context)
		{
			bool changed = DrawPresets(curve, context);

			// Canvas.
			float width = Math.Max(420f, ImGui.GetContentRegionAvail().X);
			var size = new NVector2(width, CanvasHeight);
			NVector2 min = ImGui.GetCursorScreenPos();
			NVector2 max = min + size;
			EditorGui.InvisibleButton("##canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
			bool hovered = ImGui.IsItemHovered();
			if (TestAnchors.Enabled)
			{
				TestAnchors.Record("curvecanvas", min, max);
			}
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, max, ImGui.GetColorU32(new NVector4(0.09f, 0.09f, 0.1f, 1f)));
			draw.PushClipRect(min, max, true);
			DrawGrid(draw, min, max);
			DrawCurve(draw, curve, min, max);
			DrawKeys(draw, curve, min, max);
			draw.PopClipRect();
			draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));

			changed |= HandleCanvasInput(curve, context, min, max, hovered);
			changed |= DrawKeyMenu(curve, context);
			changed |= DrawKeyFields(curve, context);
			changed |= DrawWrapModes(curve, context);
			return changed;
		}

		// ---------------------------------------------------------------- presets

		private static bool DrawPresets(AnimationCurve curve, InspectorContext context)
		{
			bool changed = false;
			var size = new NVector2(40f, 24f);
			for (int i = 0; i < Presets.Length; i++)
			{
				if (i > 0)
				{
					ImGui.SameLine();
				}

				ImGui.PushID(i);
				NVector2 min = ImGui.GetCursorScreenPos();
				bool clicked = EditorGui.Button("##preset", size);
				ImDrawListPtr draw = ImGui.GetWindowDrawList();
				DrawPreview(draw, Presets[i].Create(), min + new NVector2(5f, 4f), min + size - new NVector2(5f, 4f), ImGui.GetColorU32(ImGuiCol.Text), 1.2f);
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(Presets[i].Name);
				}

				if (clicked)
				{
					context.BeginEdit();
					ApplyPreset(curve, Presets[i].Create());
					context.EndEdit();
					selected = -1;
					Frame(curve);
					changed = true;
				}

				ImGui.PopID();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Frame", new NVector2(0f, size.Y)))
			{
				Frame(curve);
			}

			return changed;
		}

		/// <summary>Replaces the keys with a preset's (made over 0..1), stretched over the curve's current time and value range.</summary>
		private static void ApplyPreset(AnimationCurve curve, AnimationCurve preset)
		{
			float t0 = 0f;
			float t1 = 1f;
			float v0 = 0f;
			float v1 = 1f;
			if (curve.length >= 2)
			{
				curve.GetRange(out t0, out t1);
				v0 = float.MaxValue;
				v1 = float.MinValue;
				foreach (Keyframe key in curve.keys)
				{
					v0 = Math.Min(v0, key.value);
					v1 = Math.Max(v1, key.value);
				}

				if (t1 - t0 < 1e-6f)
				{
					t1 = t0 + 1f;
				}

				if (v1 - v0 < 1e-6f)
				{
					v1 = v0 + 1f;
				}
			}

			float timeScale = t1 - t0;
			float valueScale = v1 - v0;
			var keys = preset.keys;
			for (int i = 0; i < keys.Length; i++)
			{
				keys[i].time = t0 + keys[i].time * timeScale;
				keys[i].value = v0 + keys[i].value * valueScale;
				keys[i].inTangent *= valueScale / timeScale;
				keys[i].outTangent *= valueScale / timeScale;
			}

			curve.keys = keys;
			curve.RecalculateTangents();
		}

		private static AnimationCurve Bell()
		{
			var curve = new AnimationCurve(
				new Keyframe(0f, 0f) { tangentMode = TangentMode.ClampedAuto },
				new Keyframe(0.5f, 1f) { tangentMode = TangentMode.ClampedAuto },
				new Keyframe(1f, 0f) { tangentMode = TangentMode.ClampedAuto });
			curve.RecalculateTangents();
			return curve;
		}

		private static AnimationCurve Step()
		{
			return new AnimationCurve(
				new Keyframe(0f, 0f) { tangentMode = TangentMode.Constant },
				new Keyframe(0.5f, 1f) { tangentMode = TangentMode.Constant },
				new Keyframe(1f, 1f) { tangentMode = TangentMode.Constant });
		}

		// ---------------------------------------------------------------- drawing

		private static NVector2 ToScreen(NVector2 min, NVector2 max, float time, float value)
		{
			return new NVector2(
				min.X + (time - timeMin) / (timeMax - timeMin) * (max.X - min.X),
				max.Y - (value - valueMin) / (valueMax - valueMin) * (max.Y - min.Y));
		}

		private static void ToCurve(NVector2 min, NVector2 max, NVector2 point, out float time, out float value)
		{
			time = timeMin + (point.X - min.X) / (max.X - min.X) * (timeMax - timeMin);
			value = valueMin + (max.Y - point.Y) / (max.Y - min.Y) * (valueMax - valueMin);
		}

		/// <summary>Grid lines at round steps (1, 2 or 5 times a power of ten) with their numbers.</summary>
		private static void DrawGrid(ImDrawListPtr draw, NVector2 min, NVector2 max)
		{
			uint grid = ImGui.GetColorU32(GridColor);
			uint axis = ImGui.GetColorU32(AxisColor);
			uint text = ImGui.GetColorU32(ImGuiCol.TextDisabled);

			float timeStep = NiceStep((timeMax - timeMin) / Math.Max(1f, (max.X - min.X) / 70f));
			for (float t = MathF.Ceiling(timeMin / timeStep) * timeStep; t <= timeMax; t += timeStep)
			{
				float x = ToScreen(min, max, t, 0f).X;
				bool zero = Math.Abs(t) < timeStep * 0.01f;
				draw.AddLine(new NVector2(x, min.Y), new NVector2(x, max.Y), zero ? axis : grid);
				draw.AddText(new NVector2(x + 3f, max.Y - ImGui.GetTextLineHeight() - 1f), text, Format(t, timeStep));
			}

			float valueStep = NiceStep((valueMax - valueMin) / Math.Max(1f, (max.Y - min.Y) / 40f));
			for (float v = MathF.Ceiling(valueMin / valueStep) * valueStep; v <= valueMax; v += valueStep)
			{
				float y = ToScreen(min, max, 0f, v).Y;
				bool zero = Math.Abs(v) < valueStep * 0.01f;
				draw.AddLine(new NVector2(min.X, y), new NVector2(max.X, y), zero ? axis : grid);
				draw.AddText(new NVector2(min.X + 3f, y - ImGui.GetTextLineHeight()), text, Format(v, valueStep));
			}
		}

		private static float NiceStep(float raw)
		{
			if (raw <= 0f || float.IsNaN(raw) || float.IsInfinity(raw))
			{
				return 1f;
			}

			float power = MathF.Pow(10f, MathF.Floor(MathF.Log10(raw)));
			float fraction = raw / power;
			return (fraction <= 1f ? 1f : fraction <= 2f ? 2f : fraction <= 5f ? 5f : 10f) * power;
		}

		private static string Format(float value, float step)
		{
			int decimals = Math.Clamp((int)MathF.Ceiling(-MathF.Log10(step)), 0, 6);
			// No "-0.0" for a line that is zero up to rounding.
			return (Math.Abs(value) < step * 1e-3f ? 0f : value).ToString("F" + decimals);
		}

		/// <summary>The curve across the view; outside the keys (the wrap modes) dimmer.</summary>
		private static void DrawCurve(ImDrawListPtr draw, AnimationCurve curve, NVector2 min, NVector2 max)
		{
			if (curve.length == 0)
			{
				return;
			}

			curve.GetRange(out float t0, out float t1);
			uint inside = ImGui.GetColorU32(CurveColor);
			uint outside = ImGui.GetColorU32(WrapColor);
			int samples = Math.Max(16, (int)(max.X - min.X) / 2);
			NVector2 previous = default;
			for (int i = 0; i <= samples; i++)
			{
				float t = timeMin + (timeMax - timeMin) * i / samples;
				NVector2 point = ToScreen(min, max, t, curve.Evaluate(t));
				if (i > 0)
				{
					bool within = t >= t0 && t - (timeMax - timeMin) / samples <= t1;
					draw.AddLine(previous, point, within ? inside : outside, within ? 2f : 1.5f);
				}

				previous = point;
			}
		}

		private static void DrawKeys(ImDrawListPtr draw, AnimationCurve curve, NVector2 min, NVector2 max)
		{
			for (int i = 0; i < curve.length; i++)
			{
				Keyframe key = curve[i];
				NVector2 p = ToScreen(min, max, key.time, key.value);
				if (i == selected && key.tangentMode != TangentMode.Constant)
				{
					uint handle = ImGui.GetColorU32(HandleColor);
					NVector2 inPoint = HandlePoint(min, max, key, true);
					NVector2 outPoint = HandlePoint(min, max, key, false);
					if (i > 0)
					{
						draw.AddLine(p, inPoint, handle, 1f);
						draw.AddCircleFilled(inPoint, 3.5f, handle);
					}

					if (i < curve.length - 1)
					{
						draw.AddLine(p, outPoint, handle, 1f);
						draw.AddCircleFilled(outPoint, 3.5f, handle);
					}
				}

				uint color = ImGui.GetColorU32(i == selected ? EditorStyle.Selection : new NVector4(0.95f, 0.95f, 0.95f, 1f));
				draw.AddQuadFilled(p + new NVector2(0f, -KeyRadius), p + new NVector2(KeyRadius, 0f), p + new NVector2(0f, KeyRadius), p + new NVector2(-KeyRadius, 0f), color);
				if (TestAnchors.Enabled)
				{
					TestAnchors.Record("curvekey:" + i, p - new NVector2(KeyRadius), p + new NVector2(KeyRadius));
				}
			}
		}

		/// <summary>The end of a tangent handle in screen space: <see cref="HandleLength" /> pixels along the slope.</summary>
		private static NVector2 HandlePoint(NVector2 min, NVector2 max, Keyframe key, bool incoming)
		{
			float slope = incoming ? key.inTangent : key.outTangent;
			float pixelsPerTime = (max.X - min.X) / (timeMax - timeMin);
			float pixelsPerValue = (max.Y - min.Y) / (valueMax - valueMin);
			var direction = new NVector2(pixelsPerTime, -slope * pixelsPerValue);
			if (direction.LengthSquared() < 1e-12f)
			{
				direction = new NVector2(1f, 0f);
			}

			direction = NVector2.Normalize(direction) * HandleLength;
			return ToScreen(min, max, key.time, key.value) + (incoming ? -direction : direction);
		}

		// ---------------------------------------------------------------- input

		private static bool HandleCanvasInput(AnimationCurve curve, InspectorContext context, NVector2 min, NVector2 max, bool hovered)
		{
			bool changed = false;
			NVector2 mouse = ImGui.GetMousePos();
			ToCurve(min, max, mouse, out float mouseTime, out float mouseValue);
			ImGuiIOPtr io = ImGui.GetIO();

			// What is under the mouse: a tangent handle of the selected key, then a key.
			Drag hitHandle = Drag.None;
			if (selected >= 0 && selected < curve.length && curve[selected].tangentMode != TangentMode.Constant)
			{
				if (selected > 0 && NVector2.DistanceSquared(mouse, HandlePoint(min, max, curve[selected], true)) < 49f)
				{
					hitHandle = Drag.InHandle;
				}
				else if (selected < curve.length - 1 && NVector2.DistanceSquared(mouse, HandlePoint(min, max, curve[selected], false)) < 49f)
				{
					hitHandle = Drag.OutHandle;
				}
			}

			int hitKey = -1;
			for (int i = 0; i < curve.length; i++)
			{
				if (NVector2.DistanceSquared(mouse, ToScreen(min, max, curve[i].time, curve[i].value)) < 64f)
				{
					hitKey = i;
					break;
				}
			}

			if (hovered)
			{
				// View: wheel zooms around the mouse.
				if (Math.Abs(io.MouseWheel) > 0f)
				{
					float factor = MathF.Pow(0.87f, io.MouseWheel);
					if (!io.KeyCtrl)
					{
						timeMin = mouseTime + (timeMin - mouseTime) * factor;
						timeMax = mouseTime + (timeMax - mouseTime) * factor;
					}

					if (!io.KeyShift)
					{
						valueMin = mouseValue + (valueMin - mouseValue) * factor;
						valueMax = mouseValue + (valueMax - mouseValue) * factor;
					}
				}

				if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || (io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
				{
					drag = Drag.Pan;
				}
				else if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && hitKey < 0 && hitHandle == Drag.None)
				{
					context.BeginEdit();
					int added = curve.AddKey(mouseTime, mouseValue);
					context.EndEdit();
					if (added >= 0)
					{
						selected = added;
						changed = true;
					}
				}
				else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
				{
					if (hitHandle != Drag.None)
					{
						drag = hitHandle;
					}
					else if (hitKey >= 0)
					{
						selected = hitKey;
						drag = Drag.Key;
					}
					else
					{
						selected = -1;
					}

					dragChanged = false;
				}
				else if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGui.GetMouseDragDelta(ImGuiMouseButton.Right).LengthSquared() < 16f)
				{
					menuKey = hitKey;
					menuTime = mouseTime;
					menuValue = mouseValue;
					if (hitKey >= 0)
					{
						selected = hitKey;
					}

					ImGui.OpenPopup("##curvekeymenu");
				}

				if (ImGui.IsKeyPressed(ImGuiKey.F))
				{
					Frame(curve);
				}
			}

			if (selected >= 0 && selected < curve.length && (ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.Backspace)) && ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive())
			{
				context.BeginEdit();
				curve.RemoveKey(selected);
				curve.RecalculateTangents();
				context.EndEdit();
				selected = -1;
				changed = true;
			}

			// Drags.
			if (drag != Drag.None)
			{
				bool held = drag == Drag.Pan ? ImGui.IsMouseDown(ImGuiMouseButton.Middle) || ImGui.IsMouseDown(ImGuiMouseButton.Left) : ImGui.IsMouseDown(ImGuiMouseButton.Left);
				if (!held)
				{
					if (dragChanged)
					{
						context.EndEdit();
					}

					drag = Drag.None;
					dragChanged = false;
				}
				else if (drag == Drag.Pan)
				{
					NVector2 delta = io.MouseDelta;
					float timePerPixel = (timeMax - timeMin) / (max.X - min.X);
					float valuePerPixel = (valueMax - valueMin) / (max.Y - min.Y);
					timeMin -= delta.X * timePerPixel;
					timeMax -= delta.X * timePerPixel;
					valueMin += delta.Y * valuePerPixel;
					valueMax += delta.Y * valuePerPixel;
				}
				else if (selected >= 0 && selected < curve.length && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
				{
					if (!dragChanged)
					{
						context.BeginEdit();
						dragChanged = true;
					}

					Keyframe key = curve[selected];
					if (drag == Drag.Key)
					{
						// Between the neighbours: the key keeps its place (and its selection).
						float low = selected > 0 ? curve[selected - 1].time + 1e-4f : float.MinValue;
						float high = selected < curve.length - 1 ? curve[selected + 1].time - 1e-4f : float.MaxValue;
						key.time = Math.Clamp(mouseTime, low, high);
						key.value = mouseValue;
						curve.MoveKey(selected, key);
					}
					else
					{
						bool incoming = drag == Drag.InHandle;
						float dt = mouseTime - key.time;
						float dv = mouseValue - key.value;
						float minDt = (timeMax - timeMin) * 1e-3f;
						dt = incoming ? Math.Min(dt, -minDt) : Math.Max(dt, minDt);
						float slope = dv / dt;
						if (key.tangentMode == TangentMode.Broken)
						{
							if (incoming)
							{
								key.inTangent = slope;
							}
							else
							{
								key.outTangent = slope;
							}
						}
						else
						{
							key.inTangent = slope;
							key.outTangent = slope;
							key.tangentMode = TangentMode.Free;
						}

						curve.MoveKey(selected, key);
					}

					curve.RecalculateTangents();
					changed = true;
				}
			}

			return changed;
		}

		private static bool DrawKeyMenu(AnimationCurve curve, InspectorContext context)
		{
			bool changed = false;
			if (!ImGui.BeginPopup("##curvekeymenu"))
			{
				return false;
			}

			if (menuKey >= 0 && menuKey < curve.length)
			{
				TangentMode current = curve[menuKey].tangentMode;
				foreach ((TangentMode mode, string label) in new[] { (TangentMode.ClampedAuto, "Clamped Auto"), (TangentMode.Free, "Free Smooth"), (TangentMode.Linear, "Linear"), (TangentMode.Constant, "Constant"), (TangentMode.Broken, "Broken") })
				{
					if (EditorMenu.Item(label, "", current == mode))
					{
						context.BeginEdit();
						SetMode(curve, menuKey, mode);
						context.EndEdit();
						changed = true;
					}
				}

				ImGui.Separator();
				if (EditorMenu.Item("Delete Key"))
				{
					context.BeginEdit();
					curve.RemoveKey(menuKey);
					curve.RecalculateTangents();
					context.EndEdit();
					selected = -1;
					changed = true;
				}
			}
			else if (EditorMenu.Item("Add Key"))
			{
				context.BeginEdit();
				int added = curve.AddKey(menuTime, menuValue);
				context.EndEdit();
				if (added >= 0)
				{
					selected = added;
					changed = true;
				}
			}

			ImGui.EndPopup();
			return changed;
		}

		private static void SetMode(AnimationCurve curve, int index, TangentMode mode)
		{
			Keyframe key = curve[index];
			key.tangentMode = mode;
			if (mode == TangentMode.Free)
			{
				// One slope on both sides.
				float slope = (key.inTangent + key.outTangent) * 0.5f;
				key.inTangent = slope;
				key.outTangent = slope;
			}

			curve.MoveKey(index, key);
			curve.RecalculateTangents();
		}

		// ---------------------------------------------------------------- fields

		private static bool DrawKeyFields(AnimationCurve curve, InspectorContext context)
		{
			if (selected < 0 || selected >= curve.length)
			{
				ImGui.TextDisabled("Double click: add a key. Right click a key: tangents. Wheel: zoom, middle drag: pan, F: frame.");
				return false;
			}

			bool changed = false;
			Keyframe key = curve[selected];
			float fieldWidth = Math.Max(60f, (ImGui.GetContentRegionAvail().X - 300f) / 4f);
			changed |= KeyFloat("Time", ref key.time, fieldWidth, context);
			ImGui.SameLine();
			changed |= KeyFloat("Value", ref key.value, fieldWidth, context);
			ImGui.SameLine();
			bool tangentsEditable = key.tangentMode == TangentMode.Free || key.tangentMode == TangentMode.Broken;
			ImGui.BeginDisabled(!tangentsEditable);
			bool inChanged = KeyFloat("In", ref key.inTangent, fieldWidth, context);
			ImGui.SameLine();
			bool outChanged = KeyFloat("Out", ref key.outTangent, fieldWidth, context);
			ImGui.EndDisabled();
			if (key.tangentMode == TangentMode.Free)
			{
				// Free keeps one slope: an edit of either side moves both.
				if (inChanged)
				{
					key.outTangent = key.inTangent;
				}
				else if (outChanged)
				{
					key.inTangent = key.outTangent;
				}
			}

			changed |= inChanged || outChanged;
			ImGui.SameLine();
			ImGui.SetNextItemWidth(110f);
			TangentMode picked = key.tangentMode;
			if (EditorGui.BeginCombo("##mode", ModeName(key.tangentMode)))
			{
				foreach (TangentMode mode in (TangentMode[])Enum.GetValues(typeof(TangentMode)))
				{
					if (EditorGui.Selectable(ModeName(mode), mode == key.tangentMode))
					{
						picked = mode;
					}
				}

				EditorGui.EndCombo();
			}

			if (picked != key.tangentMode && !changed)
			{
				context.BeginEdit();
				SetMode(curve, selected, picked);
				context.EndEdit();
				return true;
			}

			if (changed)
			{
				// Times stay between the neighbours (the key keeps its index).
				float low = selected > 0 ? curve[selected - 1].time + 1e-4f : float.MinValue;
				float high = selected < curve.length - 1 ? curve[selected + 1].time - 1e-4f : float.MaxValue;
				key.time = Math.Clamp(key.time, low, high);
				curve.MoveKey(selected, key);
				curve.RecalculateTangents();
			}

			return changed;
		}

		private static bool KeyFloat(string label, ref float value, float width, InspectorContext context)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(width);
			bool changed = EditorGui.DragFloat("##" + label, ref value, 0.01f, 0f, 0f, "%.3f");
			context.Track(changed);
			return changed;
		}

		private static string ModeName(TangentMode mode) => mode switch
		{
			TangentMode.ClampedAuto => "Clamped Auto",
			TangentMode.Free => "Free Smooth",
			_ => mode.ToString()
		};

		private static bool DrawWrapModes(AnimationCurve curve, InspectorContext context)
		{
			bool changed = false;
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Before");
			ImGui.SameLine();
			WrapMode pre = curve.preWrapMode;
			if (WrapCombo("##pre", ref pre, context))
			{
				curve.preWrapMode = pre;
				changed = true;
			}

			ImGui.SameLine();
			ImGui.TextUnformatted("After");
			ImGui.SameLine();
			WrapMode post = curve.postWrapMode;
			if (WrapCombo("##post", ref post, context))
			{
				curve.postWrapMode = post;
				changed = true;
			}

			return changed;
		}

		private static bool WrapCombo(string id, ref WrapMode mode, InspectorContext context)
		{
			bool changed = false;
			ImGui.SetNextItemWidth(110f);
			string shown = mode == WrapMode.Loop ? "Loop" : mode == WrapMode.PingPong ? "Ping Pong" : "Clamp";
			if (EditorGui.BeginCombo(id, shown))
			{
				foreach ((WrapMode option, string label) in new[] { (WrapMode.ClampForever, "Clamp"), (WrapMode.Loop, "Loop"), (WrapMode.PingPong, "Ping Pong") })
				{
					if (EditorGui.Selectable(label, shown == label) && shown != label)
					{
						context.BeginEdit();
						mode = option;
						context.EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			return changed;
		}

		// ---------------------------------------------------------------- view

		/// <summary>The view around the keys and what the curve does between them (overshoot included), with a margin.</summary>
		private static void Frame(AnimationCurve curve)
		{
			if (curve == null || curve.length == 0)
			{
				timeMin = -0.1f;
				timeMax = 1.1f;
				valueMin = -0.1f;
				valueMax = 1.1f;
				return;
			}

			curve.GetRange(out float t0, out float t1);
			if (t1 - t0 < 1e-6f)
			{
				t0 -= 0.5f;
				t1 += 0.5f;
			}

			float low = float.MaxValue;
			float high = float.MinValue;
			for (int i = 0; i <= 128; i++)
			{
				float value = curve.Evaluate(t0 + (t1 - t0) * i / 128f);
				low = Math.Min(low, value);
				high = Math.Max(high, value);
			}

			if (high - low < 1e-6f)
			{
				low -= 0.5f;
				high += 0.5f;
			}

			float timeMargin = (t1 - t0) * 0.08f;
			float valueMargin = (high - low) * 0.15f;
			timeMin = t0 - timeMargin;
			timeMax = t1 + timeMargin;
			valueMin = low - valueMargin;
			valueMax = high + valueMargin;
		}

		#endregion
	}
}
