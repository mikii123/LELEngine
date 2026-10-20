using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>Scene view outlines of the 2D colliders and joint anchors of the selected object (green, as in Unity).</summary>
	internal static class Physics2DGizmos
	{
		#region PrivateFields

		private static readonly Color4 ColliderColor = new Color4(0.45f, 1f, 0.45f, 1f);
		private static readonly Color4 TriggerColor = new Color4(0.45f, 0.9f, 1f, 1f);
		private static readonly Color4 JointColor = new Color4(1f, 0.75f, 0.3f, 1f);

		#endregion

		#region PublicMethods

		public static void Draw(GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			DrawObject(selected);
			foreach (Transform child in selected.transform.Children)
			{
				DrawTree(child.gameObject);
			}
		}

		#endregion

		#region PrivateMethods

		private static void DrawTree(GameObject go)
		{
			DrawObject(go);
			foreach (Transform child in go.transform.Children)
			{
				DrawTree(child.gameObject);
			}
		}

		private static void DrawObject(GameObject go)
		{
			foreach (Behaviour component in go.Components)
			{
				if (component.dead || !component.enabled)
				{
					continue;
				}

				switch (component)
				{
					case Collider2D collider:
						DrawCollider(collider);
						break;
					case AnchoredJoint2D joint:
						DrawJoint(joint);
						break;
				}
			}
		}

		private static void DrawCollider(Collider2D collider)
		{
			Transform transform = collider.transform;
			PlaneBasis plane = collider.Plane;
			Color4 color = collider.isTrigger ? TriggerColor : ColliderColor;
			switch (collider)
			{
				case BoxCollider2D box:
				{
					Vector2 half = box.size * 0.5f;
					DrawPolygon(plane, transform, collider.offset, new[] { new Vector2(-half.X, -half.Y), new Vector2(half.X, -half.Y), new Vector2(half.X, half.Y), new Vector2(-half.X, half.Y) }, color);
					break;
				}
				case CircleCollider2D circle:
					DrawCircle(plane, transform, collider.offset, circle.radius, color);
					break;
				case CapsuleCollider2D capsule:
					DrawCapsule(plane, transform, collider.offset, capsule.size, capsule.direction, color);
					break;
				case PolygonCollider2D polygon:
					if (polygon.points != null && polygon.points.Count >= 2)
					{
						DrawPolygon(plane, transform, collider.offset, polygon.points.ToArray(), color);
					}

					break;
				case EdgeCollider2D edge:
					if (edge.points != null && edge.points.Count >= 2)
					{
						DrawPath(plane, transform, collider.offset, edge.points, color, false);
					}

					break;
			}
		}

		private static void DrawJoint(AnchoredJoint2D joint)
		{
			PlaneBasis plane = joint.world?.Plane ?? PlaneBasis.Current;
			Vector2 anchor = joint.anchor * plane.ScaleOf(joint.transform.lossyScale);
			Vector3 world = joint.transform.position + joint.transform.rotation * plane.FromPlane(anchor);
			DrawCross(plane, world, 0.12f, JointColor);

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

			DrawCross(plane, other, 0.12f, JointColor);
			DebugDraw.Line(world, other, JointColor);
		}

		private static void DrawPolygon(PlaneBasis plane, Transform transform, Vector2 offset, Vector2[] points, Color4 color)
		{
			DrawPath(plane, transform, offset, points, color, true);
		}

		/// <summary>A collider-local 2D path: the plane maps it into the object's local space, the transform into the world.</summary>
		private static void DrawPath(PlaneBasis plane, Transform transform, Vector2 offset, IReadOnlyList<Vector2> points, Color4 color, bool closed)
		{
			int count = closed ? points.Count : points.Count - 1;
			for (int i = 0; i < count; i++)
			{
				Vector3 a = transform.TransformPoint(plane.FromPlane(points[i] + offset));
				Vector3 b = transform.TransformPoint(plane.FromPlane(points[(i + 1) % points.Count] + offset));
				DebugDraw.Line(a, b, color);
			}
		}

		private static void DrawCircle(PlaneBasis plane, Transform transform, Vector2 offset, float radius, Color4 color)
		{
			const int segments = 32;
			var points = new Vector2[segments];
			for (int i = 0; i < segments; i++)
			{
				float a = i * MathF.PI * 2f / segments;
				points[i] = new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius);
			}

			DrawPolygon(plane, transform, offset, points, color);
		}

		private static void DrawCapsule(PlaneBasis plane, Transform transform, Vector2 offset, Vector2 size, CapsuleDirection2D direction, Color4 color)
		{
			bool vertical = direction == CapsuleDirection2D.Vertical;
			float radius = (vertical ? size.X : size.Y) * 0.5f;
			float half = Math.Max(0f, (vertical ? size.Y : size.X) * 0.5f - radius);
			const int arc = 12;
			var points = new List<Vector2>();
			for (int i = 0; i <= arc; i++)
			{
				float a = i * MathF.PI / arc;
				points.Add(vertical ? new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius + half) : new Vector2(MathF.Cos(a + MathF.PI * 0.5f) * radius + -half, MathF.Sin(a + MathF.PI * 0.5f) * radius));
			}

			for (int i = 0; i <= arc; i++)
			{
				float a = MathF.PI + i * MathF.PI / arc;
				points.Add(vertical ? new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius - half) : new Vector2(MathF.Cos(a + MathF.PI * 0.5f) * radius + half, MathF.Sin(a + MathF.PI * 0.5f) * radius));
			}

			DrawPolygon(plane, transform, offset, points.ToArray(), color);
		}

		private static void DrawCross(PlaneBasis plane, Vector3 center, float size, Color4 color)
		{
			DebugDraw.Line(center - plane.U * size, center + plane.U * size, color);
			DebugDraw.Line(center - plane.V * size, center + plane.V * size, color);
		}

		#endregion
	}
}
