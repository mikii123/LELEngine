using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Immediate-mode debug geometry (lines and translucent triangles in world space) collected during a frame
	///     and drawn by <see cref="Passes.DebugDrawPass" /> into the scene, depth tested. The editor draws its
	///     gizmos with it (scene view); games see it when <see cref="VisibleInGame" /> is set. Cleared by the host
	///     after the frame.
	/// </summary>
	public static class DebugDraw
	{
		#region PublicFields

		/// <summary>Game renderers draw the primitives too (debug overlays in builds / the game view).</summary>
		public static bool VisibleInGame;

		public static bool HasPrimitives => lines.Count > 0 || triangles.Count > 0;

		#endregion

		#region InternalFields

		internal static readonly List<float> lines = new List<float>();
		internal static readonly List<float> triangles = new List<float>();

		#endregion

		#region PublicMethods

		public static void Clear()
		{
			lines.Clear();
			triangles.Clear();
		}

		/// <summary>How much is queued now: <see cref="Truncate" /> drops what was added after (one-off renders, window captures).</summary>
		public static (int lines, int triangles) Mark => (lines.Count, triangles.Count);

		/// <summary>Drops the primitives added since <paramref name="mark" /> was taken.</summary>
		public static void Truncate((int lines, int triangles) mark)
		{
			if (lines.Count > mark.lines)
			{
				lines.RemoveRange(mark.lines, lines.Count - mark.lines);
			}

			if (triangles.Count > mark.triangles)
			{
				triangles.RemoveRange(mark.triangles, triangles.Count - mark.triangles);
			}
		}

		public static void Line(in Vector3 a, in Vector3 b, in Color4 color)
		{
			Push(lines, a, color);
			Push(lines, b, color);
		}

		public static void Triangle(in Vector3 a, in Vector3 b, in Vector3 c, in Color4 color)
		{
			Push(triangles, a, color);
			Push(triangles, b, color);
			Push(triangles, c, color);
		}

		/// <summary>Convex polygon as a fan of triangles.</summary>
		public static void Polygon(ReadOnlySpan<Vector3> verts, in Color4 color)
		{
			for (int i = 2; i < verts.Length; i++)
			{
				Triangle(verts[0], verts[i - 1], verts[i], color);
			}
		}

		public static void Circle(in Vector3 center, float radius, in Color4 color, int segments = 24)
		{
			Vector3 previous = center + new Vector3(radius, 0f, 0f);
			for (int i = 1; i <= segments; i++)
			{
				float angle = i * MathHelper.TwoPi / segments;
				Vector3 point = center + new Vector3((float)Math.Cos(angle) * radius, 0f, (float)Math.Sin(angle) * radius);
				Line(previous, point, color);
				previous = point;
			}
		}

		public static void Cylinder(in Vector3 bottomCenter, float radius, float height, in Color4 color, int segments = 16)
		{
			Vector3 top = bottomCenter + new Vector3(0f, height, 0f);
			Circle(bottomCenter, radius, color, segments);
			Circle(top, radius, color, segments);
			for (int i = 0; i < 4; i++)
			{
				float angle = i * MathHelper.PiOver2;
				Vector3 offset = new Vector3((float)Math.Cos(angle) * radius, 0f, (float)Math.Sin(angle) * radius);
				Line(bottomCenter + offset, top + offset, color);
			}
		}

		/// <summary>Box with the given center and half extents, transformed by a rotation.</summary>
		public static void WireBox(in Vector3 center, in Vector3 halfExtents, in Quaternion rotation, in Color4 color)
		{
			Span<Vector3> corners = stackalloc Vector3[8];
			for (int i = 0; i < 8; i++)
			{
				Vector3 local = new Vector3((i & 1) != 0 ? halfExtents.X : -halfExtents.X, (i & 2) != 0 ? halfExtents.Y : -halfExtents.Y, (i & 4) != 0 ? halfExtents.Z : -halfExtents.Z);
				corners[i] = center + rotation * local;
			}

			ReadOnlySpan<int> edges = stackalloc int[] { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
			for (int e = 0; e < edges.Length; e += 2)
			{
				Line(corners[edges[e]], corners[edges[e + 1]], color);
			}
		}

		public static void Arrow(in Vector3 from, in Vector3 to, in Color4 color, float headSize = 0.15f)
		{
			Line(from, to, color);
			Vector3 dir = to - from;
			float length = dir.Length;
			if (length < 1e-4f)
			{
				return;
			}

			dir /= length;
			Vector3 side = Vector3.Cross(dir, Vector3.UnitY);
			if (side.LengthSquared < 1e-6f)
			{
				side = Vector3.UnitX;
			}

			side.Normalize();
			Line(to, to - dir * headSize + side * headSize * 0.5f, color);
			Line(to, to - dir * headSize - side * headSize * 0.5f, color);
		}

		#endregion

		#region PrivateMethods

		private static void Push(List<float> list, in Vector3 p, in Color4 c)
		{
			list.Add(p.X);
			list.Add(p.Y);
			list.Add(p.Z);
			list.Add(c.R);
			list.Add(c.G);
			list.Add(c.B);
			list.Add(c.A);
		}

		#endregion
	}
}

namespace LELEngine.Rendering.Passes
{
	/// <summary>Draws the <see cref="DebugDraw" /> primitives into the HDR scene target with depth testing.</summary>
	public sealed class DebugDrawPass : RenderPass
	{
		#region PublicFields

		public override string Name => "DebugDraw";

		#endregion

		#region PrivateFields

		private const string VertexSource = @"
#version 330 core
uniform mat4 viewProjection;
layout(location = 0) in vec3 vPosition;
layout(location = 1) in vec4 vColor;
out vec4 fColor;
void main()
{
	fColor = vColor;
	gl_Position = viewProjection * vec4(vPosition, 1.0);
}
";

		private const string FragmentSource = @"
#version 330 core
in vec4 fColor;
out vec4 FragColor;
void main()
{
	FragColor = fColor;
}
";

		private ShaderProgram program;
		private int vao;
		private int vbo;
		private int capacity;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			var vertex = new Shader(VertexSource, ShaderType.VertexShader, "Builtin/DebugDraw");
			var fragment = new Shader(FragmentSource, ShaderType.FragmentShader, "Builtin/DebugDraw");
			program = new ShaderProgram("Builtin/DebugDraw", vertex, fragment);
			vertex.Delete();
			fragment.Delete();

			vao = GL.GenVertexArray();
			vbo = GL.GenBuffer();
			GL.BindVertexArray(vao);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
			GL.EnableVertexAttribArray(0);
			GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
			GL.EnableVertexAttribArray(1);
			GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
			GL.BindVertexArray(0);
		}

		public override void Execute(RenderContext context)
		{
			if (!(context.Settings.DebugPrimitives || DebugDraw.VisibleInGame) || !DebugDraw.HasPrimitives)
			{
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(true, false, DepthFunction.Lequal);
			GLState.SetCull(false);
			GLState.SetBlend(true);
			program.Use();
			program.SetMatrix4("viewProjection", context.ViewProjection);
			GL.BindVertexArray(vao);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

			if (DebugDraw.triangles.Count > 0)
			{
				GL.Enable(EnableCap.PolygonOffsetFill);
				GL.PolygonOffset(-1f, -1f);
				Draw(DebugDraw.triangles, PrimitiveType.Triangles);
				GL.Disable(EnableCap.PolygonOffsetFill);
			}

			if (DebugDraw.lines.Count > 0)
			{
				Draw(DebugDraw.lines, PrimitiveType.Lines);
			}

			GL.BindVertexArray(0);
			GLState.SetBlend(false);
			GLState.SetCull(true);
			GLState.SetDepth(true, true);
			GLState.Reset();
		}

		public override void Dispose()
		{
			program?.Delete();
			program = null;
			if (vbo != 0)
			{
				GL.DeleteBuffer(vbo);
				vbo = 0;
			}

			if (vao != 0)
			{
				GL.DeleteVertexArray(vao);
				vao = 0;
			}
		}

		#endregion

		#region PrivateMethods

		private void Draw(List<float> data, PrimitiveType type)
		{
			int bytes = data.Count * sizeof(float);
			float[] array = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data).ToArray();
			if (bytes > capacity)
			{
				capacity = Math.Max(bytes, capacity * 2);
				GL.BufferData(BufferTarget.ArrayBuffer, capacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
			}
			else
			{
				// Orphan the store so the upload never waits for the previous frame's draw.
				GL.BufferData(BufferTarget.ArrayBuffer, capacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
			}

			GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, array);
			GL.DrawArrays(type, 0, data.Count / 7);
		}

		#endregion
	}
}
