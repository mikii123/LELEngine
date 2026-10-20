using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Screen rectangles of named UI items, recorded while an automated run asks for them (<see cref="Enabled" />):
	///     scripted mouse input of the test hooks aims at them. Names: "hierarchy:Object", "project:Folder/File.ext",
	///     "component:Type", "field:Type.field" (the field's first line).
	/// </summary>
	internal static class TestAnchors
	{
		#region PublicFields

		public static bool Enabled;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, (NVector2 Min, NVector2 Max)> rects = new Dictionary<string, (NVector2, NVector2)>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		public static void Record(string name, NVector2 min, NVector2 max)
		{
			if (Enabled)
			{
				rects[name] = (min, max);
			}
		}

		/// <summary>Records the last item's rectangle under the name.</summary>
		public static void RecordItem(string name)
		{
			if (Enabled)
			{
				rects[name] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
			}
		}

		/// <summary>
		///     The point a scripted mouse aims at: a field's value column (70% across its first line), the label of other
		///     items, or "name@0.93": that fraction across (the buttons at the end of a field's line); false when the
		///     item was not drawn.
		/// </summary>
		public static bool TryGetPoint(string name, out NVector2 point)
		{
			point = default;
			float fraction = -1f;
			int at = name.IndexOf('@');
			if (at > 0)
			{
				float.TryParse(name.Substring(at + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fraction);
				name = name.Substring(0, at);
			}

			if (!rects.TryGetValue(name, out (NVector2 Min, NVector2 Max) rect))
			{
				return false;
			}

			float lineY = rect.Min.Y + Math.Min(rect.Max.Y - rect.Min.Y, ImGui.GetFrameHeight()) * 0.5f;
			if (fraction >= 0f)
			{
				point = new NVector2(rect.Min.X + (rect.Max.X - rect.Min.X) * fraction, lineY);
			}
			else
			{
				point = name.StartsWith("field:", StringComparison.OrdinalIgnoreCase)
					? new NVector2(rect.Min.X + (rect.Max.X - rect.Min.X) * 0.7f, lineY)
					: new NVector2(rect.Min.X + Math.Min(40f, (rect.Max.X - rect.Min.X) * 0.5f), lineY);
			}

			return true;
		}

		#endregion
	}
}
