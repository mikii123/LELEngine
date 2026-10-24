using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>A rectangle of the scene target in pixels: from (X0, Y0) inclusive to (X1, Y1) exclusive.</summary>
	public readonly struct PixelRect : IEquatable<PixelRect>
	{
		#region PublicFields

		public readonly int X0;
		public readonly int Y0;
		public readonly int X1;
		public readonly int Y1;

		public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
		public long Area => IsEmpty ? 0 : (long)(X1 - X0) * (Y1 - Y0);

		#endregion

		#region Constructors

		public PixelRect(int x0, int y0, int x1, int y1)
		{
			X0 = x0;
			Y0 = y0;
			X1 = x1;
			Y1 = y1;
		}

		#endregion

		#region PublicMethods

		public bool Intersects(PixelRect other)
		{
			return !IsEmpty && !other.IsEmpty && X0 < other.X1 && other.X0 < X1 && Y0 < other.Y1 && other.Y0 < Y1;
		}

		/// <summary>The bounding rectangle of both (an empty one adds nothing).</summary>
		public PixelRect Union(PixelRect other)
		{
			if (IsEmpty) return other;
			if (other.IsEmpty) return this;
			return new PixelRect(Math.Min(X0, other.X0), Math.Min(Y0, other.Y0), Math.Max(X1, other.X1), Math.Max(Y1, other.Y1));
		}

		public bool Equals(PixelRect other)
		{
			return X0 == other.X0 && Y0 == other.Y0 && X1 == other.X1 && Y1 == other.Y1;
		}

		public override bool Equals(object obj)
		{
			return obj is PixelRect other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(X0, Y0, X1, Y1);
		}

		public override string ToString()
		{
			return "(" + X0 + ", " + Y0 + ")-(" + X1 + ", " + Y1 + ")";
		}

		#endregion
	}

	/// <summary>
	///     Unity's GrabPass for the transparent pass: before an object whose shader declares <c>GrabPass</c>
	///     (<see cref="ShaderRenderState.GrabPass" />) draws, the scene drawn so far (the opaques, the sky and the
	///     transparent objects before it) is copied into a texture the shader reads through Engine/GrabPass.glsl: rgb the
	///     HDR color, a the linear eye depth of the opaque surface, so the shader can tell what lies in front of the object
	///     and leave it alone. A copy covers only the rectangles the objects read (Engine/SceneGrab.shader under a
	///     scissor); the transparent pass decides where copies go and which objects share one. The texture is made on the
	///     first copy, follows the scene target's size and is freed with the scene.
	/// </summary>
	public sealed class SceneGrab
	{
		#region PublicFields

		/// <summary>Texture unit of the grab in the drawing shader: above every unit the engine's lighting uses when the context has it.</summary>
		public const int PreferredTextureUnit = 21;

		/// <summary>
		///     The unit the grab binds to: <see cref="PreferredTextureUnit" />, or on a context with fewer units the GI
		///     resolve's normals unit, which no transparent material samples (they light from the GI volume).
		/// </summary>
		public static int TextureUnit => PreferredTextureUnit < GLCapabilities.MaxTextureUnits ? PreferredTextureUnit : Lighting.ResolveNormalTextureUnit;

		/// <summary>Copies made last frame and the pixels they copied (stats).</summary>
		public int CopyCount { get; private set; }

		public long CopiedPixels { get; private set; }

		#endregion

		#region PrivateFields

		// The largest length of a normal map's tilt across the surface (tangent * x + bitangent * y with x^2 + y^2 <= 1).
		private const float MaxBend = 1.4143f;

		private Framebuffer target;
		private ShaderProgram copyProgram;

		#endregion

		#region PublicMethods

		/// <summary>Starts a frame's stats.</summary>
		public void BeginFrame()
		{
			CopyCount = 0;
			CopiedPixels = 0;
		}

		/// <summary>
		///     Copies the scene target into the grab inside <paramref name="count" /> rectangles of
		///     <paramref name="rects" /> from <paramref name="start" />, then binds the scene target again. Leaves blending,
		///     depth testing and face culling off and another program bound.
		/// </summary>
		public void Copy(RenderContext context, List<PixelRect> rects, int start, int count)
		{
			EnsureTarget(context.SceneTarget.Width, context.SceneTarget.Height);
			copyProgram ??= new ShaderProgram("Engine/SceneGrab.shader");
			target.Bind();
			GL.Disable(EnableCap.Blend);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.Enable(EnableCap.ScissorTest);
			copyProgram.Use();
			copyProgram.SetTexture("SceneColor", TextureTarget.Texture2D, context.SceneColor.Handle, 0);
			copyProgram.SetTexture("SceneDepth", TextureTarget.Texture2D, context.SceneDepth.Handle, 1);
			copyProgram.SetVector4("depthParams", DepthParams(context.Projection));
			for (int i = start; i < start + count; i++)
			{
				PixelRect rect = rects[i];
				GL.Scissor(rect.X0, rect.Y0, rect.X1 - rect.X0, rect.Y1 - rect.Y0);
				context.Fullscreen.Draw();
				CopiedPixels += rect.Area;
			}

			GL.Disable(EnableCap.ScissorTest);
			context.SceneTarget.Bind();
			CopyCount++;
		}

		/// <summary>
		///     Points a drawing program's grab uniforms (Engine/GrabPass.glsl) at the grab, readable inside
		///     <paramref name="readable" />, with the context's camera.
		/// </summary>
		public void SetUniforms(ShaderProgram program, RenderContext context, PixelRect readable)
		{
			if (target == null)
			{
				return;
			}

			int width = target.Width;
			int height = target.Height;
			float inverseWidth = 1f / width;
			float inverseHeight = 1f / height;
			program.SetTexture("lelGrabTexture", TextureTarget.Texture2D, target.ColorAttachments[0].Handle, TextureUnit);
			program.SetVector4("lelGrabRect", new Vector4((readable.X0 + 0.5f) * inverseWidth, (readable.Y0 + 0.5f) * inverseHeight, (readable.X1 - 0.5f) * inverseWidth, (readable.Y1 - 0.5f) * inverseHeight));
			program.SetVector4("lelGrabScreen", new Vector4(width, height, inverseWidth, inverseHeight));
			program.SetVector4("lelGrabDepth", DepthParams(context.Projection));
			// The view matrix's columns are the camera's axes in the world (row vectors: view x = dot(world, column 0)).
			program.SetVector3("lelGrabAxisX", context.View.Column0.Xyz * (0.5f * context.Projection.M11));
			program.SetVector3("lelGrabAxisY", context.View.Column1.Xyz * (0.5f * context.Projection.M22));
		}

		/// <summary>
		///     The pixels a box covers on a <paramref name="screen" />-sized target, through <paramref name="toClip" />
		///     (row vectors: local to clip space), and the nearest clip w of its corners (its distance in front of the
		///     camera). Empty when the box is wholly behind the camera or off the screen; false when it crosses the camera
		///     plane (no bounded rectangle: the caller takes the whole screen).
		/// </summary>
		public static bool ScreenRect(Matrix4 toClip, Vector3 min, Vector3 max, PixelRect screen, out PixelRect rect, out float nearestW)
		{
			const float Epsilon = 1e-5f;
			float minX = float.MaxValue;
			float minY = float.MaxValue;
			float maxX = float.MinValue;
			float maxY = float.MinValue;
			nearestW = float.MaxValue;
			int behind = 0;
			for (int i = 0; i < 8; i++)
			{
				var corner = new Vector4((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z, 1f);
				Vector4 clip = corner * toClip;
				if (clip.W <= Epsilon)
				{
					behind++;
					continue;
				}

				float x = clip.X / clip.W;
				float y = clip.Y / clip.W;
				minX = Math.Min(minX, x);
				maxX = Math.Max(maxX, x);
				minY = Math.Min(minY, y);
				maxY = Math.Max(maxY, y);
				nearestW = Math.Min(nearestW, clip.W);
			}

			if (behind == 8)
			{
				rect = default;
				return true;
			}

			if (behind > 0)
			{
				rect = screen;
				nearestW = 0f;
				return false;
			}

			int width = screen.X1;
			int height = screen.Y1;
			rect = Clamp(new PixelRect(
				(int)Math.Floor((minX * 0.5f + 0.5f) * width), (int)Math.Floor((minY * 0.5f + 0.5f) * height),
				(int)Math.Ceiling((maxX * 0.5f + 0.5f) * width), (int)Math.Ceiling((maxY * 0.5f + 0.5f) * height)), screen);
			return true;
		}

		/// <summary>
		///     <paramref name="drawn" /> widened by the largest shift LelSceneRefraction makes with
		///     <paramref name="strength" /> for a surface whose nearest point is <paramref name="nearestW" /> in front of the
		///     camera (plus a texel for the bilinear lookup), on the screen.
		/// </summary>
		public static PixelRect Widen(PixelRect drawn, float strength, float nearestW, Matrix4 projection, PixelRect screen)
		{
			if (drawn.IsEmpty)
			{
				return drawn;
			}

			if (nearestW <= 0f)
			{
				return screen;
			}

			// uv shift = dot(bend, axis) * strength / w, |axis| = |projection| / 2, |bend| <= MaxBend.
			double reach = MaxBend * 0.5 * Math.Abs(strength) / nearestW;
			double marginX = Math.Ceiling(reach * Math.Abs(projection.M11) * screen.X1) + 1.0;
			double marginY = Math.Ceiling(reach * Math.Abs(projection.M22) * screen.Y1) + 1.0;
			if (marginX >= screen.X1 || marginY >= screen.Y1)
			{
				return screen;
			}

			return Clamp(new PixelRect(drawn.X0 - (int)marginX, drawn.Y0 - (int)marginY, drawn.X1 + (int)marginX, drawn.Y1 + (int)marginY), screen);
		}

		/// <summary>Frees the texture (made again by the next copy).</summary>
		public void Release()
		{
			target?.Delete();
			target = null;
		}

		public void Delete()
		{
			Release();
			copyProgram?.Delete();
			copyProgram = null;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Linear eye depth from an NDC depth z: (z * x + y) / (z * z + w), the inverse of the projection's depth rows
		///     (perspective or orthographic; row vectors, so GLSL's projection[3][2] is M43).
		/// </summary>
		private static Vector4 DepthParams(Matrix4 projection)
		{
			return new Vector4(projection.M44, -projection.M43, projection.M34, -projection.M33);
		}

		private static PixelRect Clamp(PixelRect rect, PixelRect screen)
		{
			return new PixelRect(Math.Max(rect.X0, screen.X0), Math.Max(rect.Y0, screen.Y0), Math.Min(rect.X1, screen.X1), Math.Min(rect.Y1, screen.Y1));
		}

		private void EnsureTarget(int width, int height)
		{
			if (target != null && target.Width == width && target.Height == height)
			{
				return;
			}

			target?.Delete();
			target = new Framebuffer(width, height);
			target.AddColorAttachment(RenderTextureFormat.RGBA16F);
			target.Validate();
		}

		#endregion
	}
}
