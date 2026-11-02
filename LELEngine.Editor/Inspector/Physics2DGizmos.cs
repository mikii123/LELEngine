using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Scene view gizmos of the selected object's 2D physics components, shown while a component is expanded in the
	///     inspector (as in Unity). Colliders draw the shape the physics builds and carry handles that edit the component:
	///     box and capsule sides (the opposite side stays; Alt: around the center), the circle's radius, polygon and edge
	///     points (a press on a line adds one, Ctrl + click removes one), the diamond at the center moves the offset (the
	///     transform gizmo comes first where they overlap; Alt: the diamond). Joints only show their anchors. Everything
	///     draws over the view's image: nothing hides it and the view renders nothing for it.
	/// </summary>
	internal sealed class Physics2DGizmos
	{
		#region PublicFields

		/// <summary>A handle is under the mouse or being dragged: a press here is not a pick.</summary>
		public bool IsHot => hasHot || dragging;

		#endregion

		#region PrivateFields

		/// <summary>Half the size of a handle on screen, and how far from a handle or a line a press still takes it (pixels).</summary>
		private const float HandleSize = 4f;

		private const float PickRadius = 8f;
		private const float LinePickRadius = 5f;
		private const float LineThickness = 1.5f;

		/// <summary>The smallest half extent or radius the physics builds (the colliders' BuildShapes).</summary>
		private const float MinExtent = 0.005f;

		/// <summary>Clip space w in front of the camera that lines are cut at.</summary>
		private const float NearW = 0.01f;

		private const int CircleSegments = 48;
		private const int ArcSegments = 12;

		private static readonly NVector4 ColliderColor = new NVector4(0.45f, 1f, 0.45f, 1f);
		private static readonly NVector4 TriggerColor = new NVector4(0.45f, 0.9f, 1f, 1f);
		private static readonly NVector4 JointColor = new NVector4(1f, 0.75f, 0.3f, 1f);
		private static readonly NVector4 HotColor = new NVector4(1f, 1f, 1f, 1f);
		private static readonly NVector4 RemoveColor = new NVector4(1f, 0.35f, 0.3f, 1f);
		private static readonly NVector4 BorderColor = new NVector4(0f, 0f, 0f, 0.85f);

		private readonly EditorApplication editor;
		private readonly List<Handle> handles = new List<Handle>();
		private readonly List<Vector2> path = new List<Vector2>();
		private Matrix4 viewProjection;
		private NVector2 viewMin;
		private NVector2 viewSize;
		private Handle hot;
		private bool hasHot;
		private Drag drag;
		private bool dragging;

		#endregion

		#region Constructors

		public Physics2DGizmos(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		/// <summary>The view the gizmos work in: its camera's matrix and its image's rectangle on screen.</summary>
		public void SetView(Matrix4 cameraViewProjection, NVector2 min, NVector2 size)
		{
			viewProjection = cameraViewProjection;
			viewMin = min;
			viewSize = size;
		}

		/// <summary>
		///     The handles' hover and drags. Runs before the transform gizmo: ImGuizmo starts nothing over a hovered item, so
		///     the handle under the mouse takes the press whatever of the gizmo is there.
		/// </summary>
		/// <param name="selected">The object, null when it has no gizmos in this view.</param>
		/// <param name="canHover">The mouse is free to take a handle (over the image, no camera or gizmo drag going on).</param>
		public void Update(GameObject selected, bool canHover)
		{
			hasHot = false;
			if (dragging && (selected == null || drag.Collider.dead || drag.Collider.gameObject != selected))
			{
				EndDrag();
			}

			if (selected == null)
			{
				return;
			}

			CollectHandles(selected);
			// A press that started elsewhere takes nothing on its way.
			if (!dragging && canHover && (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
			{
				FindHot(selected);
			}

			if (!dragging && !hasHot)
			{
				return;
			}

			// One button under the mouse for all the handles: ImGui's hover and active state (no pick, no camera, no gizmo).
			NVector2 buttonSize = new NVector2(PickRadius * 2f);
			NVector2 buttonMax = viewMin + viewSize - buttonSize;
			ImGui.SetCursorScreenPos(NVector2.Clamp(ImGui.GetMousePos() - buttonSize * 0.5f, viewMin, NVector2.Max(viewMin, buttonMax)));
			EditorGui.InvisibleButton("##physics_handle", buttonSize);
			if (ImGui.IsItemActivated() && hasHot)
			{
				BeginDrag();
			}

			if (dragging)
			{
				if (ImGui.IsItemActive())
				{
					ContinueDrag();
				}
				else
				{
					EndDrag();
				}
			}
			else if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
			{
				ImGui.SetTooltip(TooltipOf(hot.Kind));
			}

			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		/// <summary>The outlines and the handles of the expanded components, with this frame's edits. Keeps no reference to them.</summary>
		public void Draw(GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.PushClipRect(viewMin, viewMin + viewSize, true);
			foreach (Behaviour component in selected.Components)
			{
				if (!Shows(component))
				{
					continue;
				}

				switch (component)
				{
					case Collider2D collider:
						DrawCollider(draw, collider);
						break;
					case AnchoredJoint2D joint:
						DrawJoint(draw, joint);
						break;
				}
			}

			CollectHandles(selected);
			foreach (Handle handle in handles)
			{
				DrawHandle(draw, handle);
			}

			if (hasHot && !dragging && hot.Kind == HandleKind.NewPoint && hot.Collider != null)
			{
				draw.AddCircleFilled(hot.Screen, HandleSize, ImGui.GetColorU32(HotColor));
				draw.AddCircle(hot.Screen, HandleSize, ImGui.GetColorU32(BorderColor));
			}

			draw.PopClipRect();
			handles.Clear();
			hot = default;
		}

		#endregion

		#region PrivateMethods

		/// <summary>Disabled components have no gizmo, nor have folded ones.</summary>
		private bool Shows(Behaviour component)
		{
			return !component.dead && component.enabled && editor.IsExpandedInInspector(component);
		}

		private static NVector4 ColorOf(Collider2D collider)
		{
			return collider.isTrigger ? TriggerColor : ColliderColor;
		}

		private static List<Vector2> PointsOf(Collider2D collider)
		{
			return collider switch
			{
				PolygonCollider2D polygon => polygon.points,
				EdgeCollider2D edge => edge.points,
				_ => null
			};
		}

		/// <summary>The fewest points a shape still has after a removal.</summary>
		private static bool CanRemovePoint(Collider2D collider)
		{
			return PointsOf(collider)?.Count > (collider is PolygonCollider2D ? 3 : 2);
		}

		private static Vector2 BoxHalf(BoxCollider2D box)
		{
			return new Vector2(Math.Max(MinExtent, Math.Abs(box.size.X) * 0.5f), Math.Max(MinExtent, Math.Abs(box.size.Y) * 0.5f));
		}

		private static float CircleRadius(CircleCollider2D circle, in ShapeSpace space)
		{
			return Math.Max(MinExtent, Math.Abs(circle.radius) * space.LargestScale);
		}

		/// <summary>The capsule the physics builds, in world units: the radius and half the inner segment.</summary>
		private static void CapsuleShape(CapsuleCollider2D capsule, in ShapeSpace space, out float radius, out float halfLength)
		{
			bool vertical = capsule.direction == CapsuleDirection2D.Vertical;
			float width = Math.Abs(capsule.size.X) * space.Scale.X;
			float height = Math.Abs(capsule.size.Y) * space.Scale.Y;
			radius = Math.Max(MinExtent, (vertical ? width : height) * 0.5f);
			halfLength = Math.Max(MinExtent, (vertical ? height : width) * 0.5f - radius);
		}

		/// <summary>The local rectangle of a box or a capsule; false for the other shapes.</summary>
		private static bool BoundsOf(Collider2D collider, in ShapeSpace space, out Vector2 min, out Vector2 max)
		{
			Vector2 half;
			switch (collider)
			{
				case BoxCollider2D box:
					half = BoxHalf(box);
					break;
				case CapsuleCollider2D capsule:
				{
					CapsuleShape(capsule, space, out float radius, out float halfLength);
					bool vertical = capsule.direction == CapsuleDirection2D.Vertical;
					half = new Vector2((vertical ? radius : radius + halfLength) / space.Scale.X, (vertical ? radius + halfLength : radius) / space.Scale.Y);
					break;
				}
				default:
					min = max = Vector2.Zero;
					return false;
			}

			min = collider.offset - half;
			max = collider.offset + half;
			return true;
		}

		private void CollectHandles(GameObject selected)
		{
			handles.Clear();
			foreach (Behaviour component in selected.Components)
			{
				if (component is Collider2D collider && Shows(component))
				{
					AddHandles(collider);
				}
			}
		}

		private void AddHandles(Collider2D collider)
		{
			var space = new ShapeSpace(collider);
			switch (collider)
			{
				case CircleCollider2D circle:
				{
					float radius = CircleRadius(circle, space);
					Vector2 center = circle.offset;
					AddHandle(space, collider, HandleKind.Radius, center + new Vector2(radius / space.Scale.X, 0f), new Vector2(1f, 0f));
					AddHandle(space, collider, HandleKind.Radius, center - new Vector2(radius / space.Scale.X, 0f), new Vector2(-1f, 0f));
					AddHandle(space, collider, HandleKind.Radius, center + new Vector2(0f, radius / space.Scale.Y), new Vector2(0f, 1f));
					AddHandle(space, collider, HandleKind.Radius, center - new Vector2(0f, radius / space.Scale.Y), new Vector2(0f, -1f));
					AddHandle(space, collider, HandleKind.Center, center);
					break;
				}
				case PolygonCollider2D _:
				case EdgeCollider2D _:
					AddPointHandles(space, collider);
					break;
				default:
				{
					if (!BoundsOf(collider, space, out Vector2 min, out Vector2 max))
					{
						break;
					}

					Vector2 center = (min + max) * 0.5f;
					for (int y = -1; y <= 1; y++)
					{
						for (int x = -1; x <= 1; x++)
						{
							// A capsule has no corners.
							if ((x == 0 && y == 0) || (collider is CapsuleCollider2D && x != 0 && y != 0))
							{
								continue;
							}

							var local = new Vector2(x < 0 ? min.X : x > 0 ? max.X : center.X, y < 0 ? min.Y : y > 0 ? max.Y : center.Y);
							AddHandle(space, collider, HandleKind.Sides, local, new Vector2(x, y));
						}
					}

					AddHandle(space, collider, HandleKind.Center, center);
					break;
				}
			}
		}

		private void AddPointHandles(in ShapeSpace space, Collider2D collider)
		{
			List<Vector2> points = PointsOf(collider);
			if (points == null || points.Count == 0)
			{
				return;
			}

			Vector2 offset = collider.offset;
			Vector2 min = points[0] + offset;
			Vector2 max = min;
			for (int i = 0; i < points.Count; i++)
			{
				Vector2 local = points[i] + offset;
				min = Vector2.ComponentMin(min, local);
				max = Vector2.ComponentMax(max, local);
				AddHandle(space, collider, HandleKind.Point, local, default, i);
				// The lines take a press anywhere: scripted drags aim at their middles.
				if (TestAnchors.Enabled && i + 1 < points.Count && Project(space.ToWorld((local + points[i + 1] + offset) * 0.5f), out NVector2 middle))
				{
					TestAnchors.Record("gizmo:" + collider.GetType().Name + "/Line" + i, middle - new NVector2(HandleSize), middle + new NVector2(HandleSize));
				}
			}

			AddHandle(space, collider, HandleKind.Center, (min + max) * 0.5f);
		}

		private void AddHandle(in ShapeSpace space, Collider2D collider, HandleKind kind, Vector2 local, Vector2 side = default, int index = 0)
		{
			var handle = new Handle { Collider = collider, Kind = kind, Local = local, Side = side, Index = index };
			handle.Visible = Project(space.ToWorld(local), out handle.Screen);
			handles.Add(handle);
			if (TestAnchors.Enabled && handle.Visible)
			{
				TestAnchors.Record(AnchorOf(handle), handle.Screen - new NVector2(HandleSize), handle.Screen + new NVector2(HandleSize));
			}
		}

		private static string AnchorOf(in Handle handle)
		{
			string part = handle.Kind switch
			{
				HandleKind.Center => "Center",
				HandleKind.Point => "Point" + handle.Index,
				_ => (handle.Side.Y > 0f ? "Top" : handle.Side.Y < 0f ? "Bottom" : "") + (handle.Side.X > 0f ? "Right" : handle.Side.X < 0f ? "Left" : "")
			};
			return "gizmo:" + handle.Collider.GetType().Name + "/" + part;
		}

		private static string TooltipOf(HandleKind kind)
		{
			return kind switch
			{
				HandleKind.Sides => "Drag: move this side, the opposite one stays. Alt: around the center.",
				HandleKind.Radius => "Drag: the radius.",
				HandleKind.Center => "Drag: move the shape (its offset).",
				HandleKind.Point => "Drag: move the point. Ctrl + click: remove it.",
				_ => "Press: add a point here."
			};
		}

		/// <summary>The handle under the mouse: the nearest, then a center, then a new point on a line.</summary>
		private void FindHot(GameObject selected)
		{
			NVector2 mouse = ImGui.GetMousePos();
			ImGuiIOPtr io = ImGui.GetIO();
			bool overGizmo = ImGuizmo.IsOver();
			float pickRange = PickRadius * PickRadius;
			float nearest = pickRange;
			float nearestCenter = pickRange;
			int found = -1;
			int foundCenter = -1;
			for (int i = 0; i < handles.Count; i++)
			{
				if (!handles[i].Visible)
				{
					continue;
				}

				float distance = NVector2.DistanceSquared(handles[i].Screen, mouse);
				if (handles[i].Kind != HandleKind.Center)
				{
					if (distance < nearest)
					{
						nearest = distance;
						found = i;
					}
				}
				else if (distance < nearestCenter && (!overGizmo || io.KeyAlt))
				{
					nearestCenter = distance;
					foundCenter = i;
				}
			}

			found = found >= 0 ? found : foundCenter;
			if (found >= 0)
			{
				hot = handles[found];
				hasHot = true;
			}
			else if (!overGizmo && !io.KeyCtrl)
			{
				FindNewPoint(selected, mouse);
			}
		}

		/// <summary>The point of a polygon's or an edge's line nearest to the mouse, measured on screen, computed on the shape's plane.</summary>
		private void FindNewPoint(GameObject selected, NVector2 mouse)
		{
			float nearest = LinePickRadius * LinePickRadius;
			foreach (Behaviour component in selected.Components)
			{
				if (!(component is Collider2D collider) || !Shows(component))
				{
					continue;
				}

				List<Vector2> points = PointsOf(collider);
				if (points == null || points.Count < 2)
				{
					continue;
				}

				var space = new ShapeSpace(collider);
				if (!MouseOnPlane(space, out Vector2 onPlane))
				{
					continue;
				}

				Vector2 offset = collider.offset;
				int lines = collider is PolygonCollider2D ? points.Count : points.Count - 1;
				for (int i = 0; i < lines; i++)
				{
					Vector2 start = points[i] + offset;
					Vector2 line = points[(i + 1) % points.Count] + offset - start;
					Vector2 scaled = line * space.Scale;
					float squared = Vector2.Dot(scaled, scaled);
					if (squared < 1e-12f)
					{
						continue;
					}

					float along = Math.Clamp(Vector2.Dot((onPlane - start) * space.Scale, scaled) / squared, 0f, 1f);
					Vector2 local = start + line * along;
					if (Project(space.ToWorld(local), out NVector2 screen) && NVector2.DistanceSquared(screen, mouse) < nearest)
					{
						nearest = NVector2.DistanceSquared(screen, mouse);
						hot = new Handle { Collider = collider, Kind = HandleKind.NewPoint, Local = local, Index = i + 1, Screen = screen, Visible = true };
						hasHot = true;
					}
				}
			}
		}

		private void BeginDrag()
		{
			Collider2D collider = hot.Collider;
			var space = new ShapeSpace(collider);
			editor.Edits.Begin(collider);
			if (hot.Kind == HandleKind.Point && ImGui.GetIO().KeyCtrl && CanRemovePoint(collider))
			{
				PointsOf(collider).RemoveAt(hot.Index);
				collider.MarkDirty();
				editor.Edits.RequestCommit();
				editor.MarkDirty();
				return;
			}

			if (!MouseOnPlane(space, out Vector2 mouse))
			{
				editor.Edits.RequestCommit();
				return;
			}

			drag = new Drag
			{
				Collider = collider,
				Kind = hot.Kind,
				Side = hot.Side,
				Index = hot.Index,
				Grab = hot.Local - mouse,
				Start = hot.Local,
				StartOffset = collider.offset
			};
			BoundsOf(collider, space, out drag.StartMin, out drag.StartMax);
			if (hot.Kind == HandleKind.NewPoint)
			{
				PointsOf(collider).Insert(hot.Index, hot.Local - collider.offset);
				collider.MarkDirty();
				editor.MarkEdited();
				drag.Kind = HandleKind.Point;
				drag.Changed = true;
			}

			dragging = true;
		}

		private void ContinueDrag()
		{
			Collider2D collider = drag.Collider;
			var space = new ShapeSpace(collider);
			if (!MouseOnPlane(space, out Vector2 mouse))
			{
				return;
			}

			// An undo during the drag ended the edit: this starts the next one.
			editor.Edits.Begin(collider);
			Vector2 target = mouse + drag.Grab;
			bool changed = false;
			switch (drag.Kind)
			{
				case HandleKind.Sides:
					changed = MoveSides(collider, space, target);
					break;
				case HandleKind.Radius:
				{
					var circle = (CircleCollider2D)collider;
					float radius = Math.Max(MinExtent, ((target - circle.offset) * space.Scale).Length / space.LargestScale);
					changed = radius != circle.radius;
					if (changed)
					{
						circle.radius = radius;
					}

					break;
				}
				case HandleKind.Center:
				{
					Vector2 offset = drag.StartOffset + target - drag.Start;
					changed = offset != collider.offset;
					collider.offset = offset;
					break;
				}
				case HandleKind.Point:
				{
					List<Vector2> points = PointsOf(collider);
					if (points != null && drag.Index < points.Count)
					{
						Vector2 point = target - collider.offset;
						changed = point != points[drag.Index];
						if (changed)
						{
							points[drag.Index] = point;
							collider.MarkDirty();
						}
					}

					break;
				}
			}

			if (changed)
			{
				drag.Changed = true;
				editor.MarkEdited();
			}
		}

		/// <summary>Moves what the handle moves from where it all was at the press: the axes of its sides, in both size and offset.</summary>
		private bool MoveSides(Collider2D collider, in ShapeSpace space, Vector2 target)
		{
			bool symmetric = ImGui.GetIO().KeyAlt;
			var smallest = new Vector2(MinExtent * 2f);
			Vector2 size;
			if (collider is CapsuleCollider2D capsule)
			{
				size = capsule.size;
				// The physics makes a capsule at least as long as it is wide: the handle stops there too.
				if (capsule.direction == CapsuleDirection2D.Vertical)
				{
					smallest.Y = Math.Max(smallest.Y, Math.Abs(size.X) * space.Scale.X / space.Scale.Y);
				}
				else
				{
					smallest.X = Math.Max(smallest.X, Math.Abs(size.Y) * space.Scale.Y / space.Scale.X);
				}
			}
			else
			{
				size = ((BoxCollider2D)collider).size;
			}

			Vector2 oldSize = size;
			Vector2 offset = collider.offset;
			MoveSide(drag.Side.X, target.X, drag.StartMin.X, drag.StartMax.X, smallest.X, symmetric, ref size.X, ref offset.X);
			MoveSide(drag.Side.Y, target.Y, drag.StartMin.Y, drag.StartMax.Y, smallest.Y, symmetric, ref size.Y, ref offset.Y);
			if (size == oldSize && offset == collider.offset)
			{
				return false;
			}

			collider.offset = offset;
			if (collider is CapsuleCollider2D edited)
			{
				edited.size = size;
			}
			else
			{
				((BoxCollider2D)collider).size = size;
			}

			return true;
		}

		private static void MoveSide(float side, float target, float min, float max, float smallest, bool symmetric, ref float size, ref float offset)
		{
			if (side == 0f)
			{
				return;
			}

			if (symmetric)
			{
				float center = (min + max) * 0.5f;
				size = Math.Max((target - center) * side * 2f, smallest);
				offset = center;
				return;
			}

			float opposite = side > 0f ? min : max;
			size = Math.Max((target - opposite) * side, smallest);
			offset = opposite + side * size * 0.5f;
		}

		private void EndDrag()
		{
			dragging = false;
			editor.Edits.RequestCommit();
			if (drag.Changed)
			{
				// What depends on the scene's version (the inspector's prefab override marks) catches up.
				editor.MarkDirty();
			}

			drag = default;
		}

		/// <summary>The local 2D point of the collider's plane under the mouse; false when the plane is edge-on or behind the camera.</summary>
		private bool MouseOnPlane(in ShapeSpace space, out Vector2 local)
		{
			NVector2 mouse = ImGui.GetMousePos();
			float x = (mouse.X - viewMin.X) / viewSize.X * 2f - 1f;
			float y = 1f - (mouse.Y - viewMin.Y) / viewSize.Y * 2f;
			Matrix4 inverse = viewProjection.Inverted();
			Vector4 near = new Vector4(x, y, -1f, 1f) * inverse;
			Vector4 far = new Vector4(x, y, 1f, 1f) * inverse;
			Vector3 origin = near.Xyz / near.W;
			return space.RayToLocal(origin, far.Xyz / far.W - origin, out local);
		}

		private bool Project(Vector3 world, out NVector2 screen)
		{
			Vector4 clip = new Vector4(world, 1f) * viewProjection;
			if (clip.W < NearW)
			{
				screen = default;
				return false;
			}

			screen = ToScreen(clip);
			return true;
		}

		private NVector2 ToScreen(Vector4 clip)
		{
			return new NVector2(viewMin.X + (clip.X / clip.W * 0.5f + 0.5f) * viewSize.X, viewMin.Y + (0.5f - clip.Y / clip.W * 0.5f) * viewSize.Y);
		}

		private void DrawLine(ImDrawListPtr draw, Vector3 a, Vector3 b, uint color)
		{
			Vector4 clipA = new Vector4(a, 1f) * viewProjection;
			Vector4 clipB = new Vector4(b, 1f) * viewProjection;
			if (clipA.W < NearW && clipB.W < NearW)
			{
				return;
			}

			// The part in front of the camera.
			if (clipA.W < NearW)
			{
				clipA = Vector4.Lerp(clipA, clipB, (NearW - clipA.W) / (clipB.W - clipA.W));
			}
			else if (clipB.W < NearW)
			{
				clipB = Vector4.Lerp(clipB, clipA, (NearW - clipB.W) / (clipA.W - clipB.W));
			}

			draw.AddLine(ToScreen(clipA), ToScreen(clipB), color, LineThickness);
		}

		/// <summary>A path of local 2D points.</summary>
		private void DrawPath(ImDrawListPtr draw, in ShapeSpace space, List<Vector2> points, bool closed, uint color)
		{
			int count = closed ? points.Count : points.Count - 1;
			for (int i = 0; i < count; i++)
			{
				DrawLine(draw, space.ToWorld(points[i]), space.ToWorld(points[(i + 1) % points.Count]), color);
			}
		}

		/// <summary>Adds an arc to <see cref="path" />: center and radius in world units along the local axes (a circle whatever the scale).</summary>
		private void AddArc(in ShapeSpace space, Vector2 center, float radius, float from, float to, int segments = ArcSegments)
		{
			for (int i = 0; i <= segments; i++)
			{
				float angle = from + (to - from) * i / segments;
				path.Add(new Vector2((center.X + MathF.Cos(angle) * radius) / space.Scale.X, (center.Y + MathF.Sin(angle) * radius) / space.Scale.Y));
			}
		}

		private void DrawCollider(ImDrawListPtr draw, Collider2D collider)
		{
			var space = new ShapeSpace(collider);
			NVector4 baseColor = ColorOf(collider);
			uint color = ImGui.GetColorU32(baseColor);
			// The edge radius: the contour outside the shape the handles edit.
			uint radiusColor = ImGui.GetColorU32(new NVector4(baseColor.X, baseColor.Y, baseColor.Z, baseColor.W * 0.45f));
			Vector2 center = collider.offset * space.Scale;
			path.Clear();
			switch (collider)
			{
				case BoxCollider2D box:
				{
					Vector2 half = BoxHalf(box);
					path.Add(collider.offset + new Vector2(-half.X, -half.Y));
					path.Add(collider.offset + new Vector2(half.X, -half.Y));
					path.Add(collider.offset + new Vector2(half.X, half.Y));
					path.Add(collider.offset + new Vector2(-half.X, half.Y));
					DrawPath(draw, space, path, true, color);
					float radius = box.edgeRadius * space.LargestScale;
					if (radius > 0f)
					{
						path.Clear();
						Vector2 corner = half * space.Scale;
						AddArc(space, center + new Vector2(corner.X, -corner.Y), radius, -MathF.PI * 0.5f, 0f);
						AddArc(space, center + corner, radius, 0f, MathF.PI * 0.5f);
						AddArc(space, center + new Vector2(-corner.X, corner.Y), radius, MathF.PI * 0.5f, MathF.PI);
						AddArc(space, center - corner, radius, MathF.PI, MathF.PI * 1.5f);
						DrawPath(draw, space, path, true, radiusColor);
					}

					break;
				}
				case CircleCollider2D circle:
					AddArc(space, center, CircleRadius(circle, space), 0f, MathF.PI * 2f, CircleSegments);
					DrawPath(draw, space, path, true, color);
					break;
				case CapsuleCollider2D capsule:
				{
					CapsuleShape(capsule, space, out float radius, out float halfLength);
					if (capsule.direction == CapsuleDirection2D.Vertical)
					{
						AddArc(space, center + new Vector2(0f, halfLength), radius, 0f, MathF.PI);
						AddArc(space, center - new Vector2(0f, halfLength), radius, MathF.PI, MathF.PI * 2f);
					}
					else
					{
						AddArc(space, center - new Vector2(halfLength, 0f), radius, MathF.PI * 0.5f, MathF.PI * 1.5f);
						AddArc(space, center + new Vector2(halfLength, 0f), radius, MathF.PI * 1.5f, MathF.PI * 2.5f);
					}

					DrawPath(draw, space, path, true, color);
					break;
				}
				case PolygonCollider2D polygon:
					if (polygon.points != null)
					{
						foreach (Vector2 point in polygon.points)
						{
							path.Add(point + collider.offset);
						}

						DrawPath(draw, space, path, path.Count >= 3, color);
					}

					break;
				case EdgeCollider2D edge:
					if (edge.points != null)
					{
						foreach (Vector2 point in edge.points)
						{
							path.Add(point + collider.offset);
						}

						DrawPath(draw, space, path, false, color);
						DrawEdgeRadius(draw, space, edge, radiusColor);
					}

					break;
			}
		}

		/// <summary>A thick edge: the capsule around each of its lines, as the physics makes them.</summary>
		private void DrawEdgeRadius(ImDrawListPtr draw, in ShapeSpace space, EdgeCollider2D edge, uint color)
		{
			float radius = edge.edgeRadius * space.LargestScale;
			if (radius <= 0f)
			{
				return;
			}

			for (int i = 0; i + 1 < edge.points.Count; i++)
			{
				Vector2 a = (edge.points[i] + edge.offset) * space.Scale;
				Vector2 b = (edge.points[i + 1] + edge.offset) * space.Scale;
				if ((b - a).LengthSquared < 1e-12f)
				{
					continue;
				}

				float angle = MathF.Atan2(b.Y - a.Y, b.X - a.X);
				path.Clear();
				AddArc(space, b, radius, angle - MathF.PI * 0.5f, angle + MathF.PI * 0.5f);
				AddArc(space, a, radius, angle + MathF.PI * 0.5f, angle + MathF.PI * 1.5f);
				DrawPath(draw, space, path, true, color);
			}
		}

		private void DrawJoint(ImDrawListPtr draw, AnchoredJoint2D joint)
		{
			PlaneBasis plane = joint.world?.Plane ?? PlaneBasis.Current;
			uint color = ImGui.GetColorU32(JointColor);
			Vector2 anchor = joint.anchor * plane.ScaleOf(joint.transform.lossyScale);
			Vector3 world = joint.transform.position + joint.transform.rotation * plane.FromPlane(anchor);
			DrawCross(draw, plane, world, 0.12f, color);

			Vector3 other;
			if (joint.connectedBody != null)
			{
				Vector2 connected = joint.connectedAnchor * plane.ScaleOf(joint.connectedBody.transform.lossyScale);
				other = joint.connectedBody.transform.position + joint.connectedBody.transform.rotation * plane.FromPlane(connected);
			}
			else if (joint.autoConfigureConnectedAnchor)
			{
				return;
			}
			else
			{
				other = plane.FromPlane(joint.connectedAnchor, world);
			}

			DrawCross(draw, plane, other, 0.12f, color);
			DrawLine(draw, world, other, color);
		}

		private void DrawCross(ImDrawListPtr draw, PlaneBasis plane, Vector3 center, float size, uint color)
		{
			DrawLine(draw, center - plane.U * size, center + plane.U * size, color);
			DrawLine(draw, center - plane.V * size, center + plane.V * size, color);
		}

		private void DrawHandle(ImDrawListPtr draw, in Handle handle)
		{
			if (!handle.Visible)
			{
				return;
			}

			NVector4 color = ColorOf(handle.Collider);
			if (dragging && drag.Collider == handle.Collider && drag.Kind == handle.Kind && drag.Index == handle.Index && drag.Side == handle.Side)
			{
				color = EditorStyle.Selection;
			}
			else if (hasHot && !dragging && hot.Collider == handle.Collider && hot.Kind == handle.Kind && hot.Index == handle.Index && hot.Side == handle.Side)
			{
				color = handle.Kind == HandleKind.Point && ImGui.GetIO().KeyCtrl && CanRemovePoint(handle.Collider) ? RemoveColor : HotColor;
			}

			uint fill = ImGui.GetColorU32(color);
			uint border = ImGui.GetColorU32(BorderColor);
			NVector2 center = handle.Screen;
			switch (handle.Kind)
			{
				case HandleKind.Point:
					draw.AddCircleFilled(center, HandleSize + 0.5f, fill);
					draw.AddCircle(center, HandleSize + 0.5f, border);
					break;
				case HandleKind.Center:
				{
					float size = HandleSize + 1.5f;
					NVector2 top = center - new NVector2(0f, size);
					NVector2 right = center + new NVector2(size, 0f);
					NVector2 bottom = center + new NVector2(0f, size);
					NVector2 left = center - new NVector2(size, 0f);
					draw.AddQuadFilled(top, right, bottom, left, fill);
					draw.AddQuad(top, right, bottom, left, border);
					break;
				}
				default:
				{
					NVector2 half = new NVector2(HandleSize);
					draw.AddRectFilled(center - half, center + half, fill);
					draw.AddRect(center - half, center + half, border);
					break;
				}
			}
		}

		#endregion

		#region NestedTypes

		private enum HandleKind
		{
			/// <summary>A side or a corner of a box or a capsule.</summary>
			Sides,
			Radius,
			Center,
			Point,

			/// <summary>The spot of a polygon's or an edge's line under the mouse: a press adds a point there.</summary>
			NewPoint
		}

		private struct Handle
		{
			public Collider2D Collider;
			public HandleKind Kind;

			/// <summary>Which way it points from the shape's center, per axis -1, 0 or 1 (sides and radius).</summary>
			public Vector2 Side;

			/// <summary>Index in the shape's points (of the new point for <see cref="HandleKind.NewPoint" />).</summary>
			public int Index;

			/// <summary>Where it is in the collider's local 2D space, the offset included.</summary>
			public Vector2 Local;

			public NVector2 Screen;
			public bool Visible;
		}

		private struct Drag
		{
			public Collider2D Collider;
			public HandleKind Kind;
			public Vector2 Side;
			public int Index;

			/// <summary>From the mouse to the handle at the press: a press beside the handle does not make it jump.</summary>
			public Vector2 Grab;

			public Vector2 Start;
			public Vector2 StartOffset;
			public Vector2 StartMin;
			public Vector2 StartMax;
			public bool Changed;
		}

		/// <summary>
		///     A collider's local 2D space: the simulation plane maps it into the object's local space, the transform into the
		///     world (the shapes of unrotated objects lie in the plane; the physics works in plane coordinates, as here).
		/// </summary>
		private readonly struct ShapeSpace
		{
			private readonly Vector3 origin;
			private readonly Vector3 axisU;
			private readonly Vector3 axisV;

			/// <summary>World units per local unit along 2D x and y.</summary>
			public readonly Vector2 Scale;

			public float LargestScale => Math.Max(Scale.X, Scale.Y);

			public ShapeSpace(Collider2D collider)
			{
				PlaneBasis plane = collider.Plane;
				Matrix4 localToWorld = collider.transform.LocalToWorld;
				origin = Vector3.TransformPosition(Vector3.Zero, localToWorld);
				axisU = Vector3.TransformVector(plane.U, localToWorld);
				axisV = Vector3.TransformVector(plane.V, localToWorld);
				Scale = new Vector2(Math.Max(axisU.Length, 1e-6f), Math.Max(axisV.Length, 1e-6f));
			}

			public Vector3 ToWorld(Vector2 local)
			{
				return origin + axisU * local.X + axisV * local.Y;
			}

			/// <summary>
			///     Where a world ray meets the plane of the shape, in local 2D; false when it runs along the plane (or the plane
			///     is flattened), points away, or is not a number (a mouse outside every window): such a value never reaches a field.
			/// </summary>
			public bool RayToLocal(Vector3 from, Vector3 direction, out Vector2 local)
			{
				local = Vector2.Zero;
				Vector3 normal = Vector3.Cross(axisU, axisV);
				float facing = Vector3.Dot(direction, normal);
				if (!(Math.Abs(facing) > 0.01f * direction.Length * normal.Length))
				{
					return false;
				}

				float distance = Vector3.Dot(origin - from, normal) / facing;
				if (!(distance > 0f))
				{
					return false;
				}

				// The axes are not perpendicular under a parent's non-uniform scale.
				Vector3 point = from + direction * distance - origin;
				float uu = Vector3.Dot(axisU, axisU);
				float uv = Vector3.Dot(axisU, axisV);
				float vv = Vector3.Dot(axisV, axisV);
				float pointU = Vector3.Dot(point, axisU);
				float pointV = Vector3.Dot(point, axisV);
				float determinant = uu * vv - uv * uv;
				local = new Vector2((vv * pointU - uv * pointV) / determinant, (uu * pointV - uv * pointU) / determinant);
				return true;
			}
		}

		#endregion
	}
}
