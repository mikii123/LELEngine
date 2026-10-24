using System;
using System.Globalization;
using System.Text;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	/// <summary>Render queue values of materials (Unity's): what a material's queue is compared against.</summary>
	public static class MaterialQueue
	{
		#region PublicFields

		public const int Background = 1000;
		public const int Geometry = 2000;
		public const int AlphaTest = 2450;

		/// <summary>Last queue drawn by the opaque passes; everything above is transparent.</summary>
		public const int GeometryLast = 2500;

		public const int Transparent = 3000;
		public const int Overlay = 4000;

		#endregion

		#region PublicMethods

		/// <summary>Queues above <see cref="GeometryLast" /> draw in the transparent pass: after the opaques, blended, sorted back to front.</summary>
		public static bool IsTransparent(int queue)
		{
			return queue > GeometryLast;
		}

		/// <summary>The named queue ("Transparent", "Geometry+10", "2450"), or null when the text is not one.</summary>
		public static int? Parse(string text)
		{
			text = text.Trim();
			int number;
			if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
			{
				return number;
			}

			int sign = text.IndexOfAny(new[] { '+', '-' });
			string name = sign > 0 ? text.Substring(0, sign).Trim() : text;
			int offset = 0;
			if (sign > 0 && !int.TryParse(text.Substring(sign).Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
			{
				return null;
			}

			switch (name.ToLowerInvariant())
			{
				case "background": return Background + offset;
				case "geometry": return Geometry + offset;
				case "alphatest": return AlphaTest + offset;
				case "geometrylast": return GeometryLast + offset;
				case "transparent": return Transparent + offset;
				case "overlay": return Overlay + offset;
				default: return null;
			}
		}

		#endregion
	}

	public enum CullMode
	{
		Back,
		Front,
		Off
	}

	/// <summary>
	///     Fixed-function state a shader asks for, declared in its Properties block next to the material parameters
	///     (Unity's keywords):
	///     <code>
	///     Blend SrcAlpha OneMinusSrcAlpha            // or: Blend One OneMinusSrcAlpha, One One (color, alpha)   Blend Off
	///     ZWrite Off                                  // On / Off
	///     ZTest LEqual                                // Less, LEqual, Equal, GEqual, Greater, NotEqual, Always
	///     Cull Off                                    // Back, Front, Off
	///     Queue Transparent                           // Background, Geometry, AlphaTest, Transparent, Overlay, +/- offset, or a number
	///     ShadowCaster Off                            // On / Off: the material's objects cast no shadow (Unity: a shader without a ShadowCaster pass)
	///     </code>
	///     A shader that blends without naming a queue goes to the Transparent queue. Blending, depth writes and the
	///     depth test apply in the transparent pass (the opaque passes draw with their own depth state and no
	///     blending); the cull mode applies everywhere a material shader draws. Each variant of a shader
	///     (<see cref="ShaderProgram.GetVariant" />) has its own state: the lines may sit in #if blocks over its keywords.
	/// </summary>
	public sealed class ShaderRenderState
	{
		#region PublicFields

		/// <summary>The state of shaders that declare none: opaque, depth written, back faces culled.</summary>
		public static readonly ShaderRenderState Default = new ShaderRenderState();

		public bool BlendEnabled;
		public BlendingFactor SrcColor = BlendingFactor.SrcAlpha;
		public BlendingFactor DstColor = BlendingFactor.OneMinusSrcAlpha;
		public BlendingFactor SrcAlpha = BlendingFactor.SrcAlpha;
		public BlendingFactor DstAlpha = BlendingFactor.OneMinusSrcAlpha;
		public bool ZWrite = true;

		/// <summary>Depth test the shader asks for; null leaves the pass's own (Less, or LEqual behind a depth prepass).</summary>
		public DepthFunction? ZTest;

		public CullMode Cull = CullMode.Back;
		public int Queue = MaterialQueue.Geometry;

		/// <summary>The material's objects draw into shadow maps (opaque depth, or the transmittance of transparent ones).</summary>
		public bool CastShadows = true;

		/// <summary>The shader named its queue (otherwise it follows the blend state).</summary>
		public bool QueueDeclared;

		#endregion

		#region PublicMethods

		/// <summary>True when the line is a render state keyword line (and not a material property named like one).</summary>
		public static bool IsStateLine(string line)
		{
			string[] parts = Split(line);
			if (parts.Length < 2 || parts[1].StartsWith("(", StringComparison.Ordinal))
			{
				return false;
			}

			switch (parts[0].ToLowerInvariant())
			{
				case "blend":
				case "zwrite":
				case "ztest":
				case "cull":
				case "queue":
				case "shadowcaster":
					return true;
				default:
					return false;
			}
		}

		/// <summary>Applies one keyword line; false (with a message) when it does not parse.</summary>
		public bool TryApplyLine(string line, out string error)
		{
			error = null;
			string[] parts = Split(line);
			string keyword = parts[0].ToLowerInvariant();
			string rest = line.Trim().Substring(parts[0].Length).Trim();
			switch (keyword)
			{
				case "blend":
					return ParseBlend(rest, out error);
				case "zwrite":
					return ParseSwitch(rest, out ZWrite, out error);
				case "ztest":
					return ParseDepthTest(rest, out error);
				case "cull":
					return ParseCull(rest, out error);
				case "shadowcaster":
					return ParseSwitch(rest, out CastShadows, out error);
				case "queue":
				{
					int? queue = MaterialQueue.Parse(rest);
					if (!queue.HasValue)
					{
						error = "unknown queue '" + rest + "'";
						return false;
					}

					Queue = queue.Value;
					QueueDeclared = true;
					return true;
				}
				default:
					error = "unknown keyword '" + parts[0] + "'";
					return false;
			}
		}

		/// <summary>Blend without a queue: the Transparent queue (call once the block is read).</summary>
		public void Finish()
		{
			if (BlendEnabled && !QueueDeclared)
			{
				Queue = MaterialQueue.Transparent;
			}
		}

		/// <summary>Sets the blend, depth write / test and cull state of a transparent draw.</summary>
		public void ApplyTransparent()
		{
			if (BlendEnabled)
			{
				GL.Enable(EnableCap.Blend);
				GL.BlendFuncSeparate((BlendingFactorSrc)SrcColor, (BlendingFactorDest)DstColor, (BlendingFactorSrc)SrcAlpha, (BlendingFactorDest)DstAlpha);
			}
			else
			{
				GL.Disable(EnableCap.Blend);
			}

			GL.Enable(EnableCap.DepthTest);
			GL.DepthMask(ZWrite);
			GL.DepthFunc(ZTest ?? DepthFunction.Lequal);
			ApplyCull();
		}

		/// <summary>Sets the cull state (every pass that draws with the material's shader).</summary>
		public void ApplyCull()
		{
			if (Cull == CullMode.Off)
			{
				GL.Disable(EnableCap.CullFace);
			}
			else
			{
				GL.Enable(EnableCap.CullFace);
				GL.CullFace(Cull == CullMode.Front ? TriangleFace.Front : TriangleFace.Back);
			}
		}

		/// <summary>One line for inspectors: "Transparent 3000, Blend One OneMinusSrcAlpha, ZWrite Off, Cull Off".</summary>
		public string Describe()
		{
			var text = new StringBuilder();
			text.Append(MaterialQueue.IsTransparent(Queue) ? "Transparent " : "Opaque ").Append(Queue);
			if (BlendEnabled)
			{
				text.Append(", Blend ").Append(SrcColor).Append(' ').Append(DstColor);
				if (SrcAlpha != SrcColor || DstAlpha != DstColor)
				{
					text.Append(", ").Append(SrcAlpha).Append(' ').Append(DstAlpha);
				}
			}

			if (!ZWrite) text.Append(", ZWrite Off");
			if (ZTest.HasValue) text.Append(", ZTest ").Append(ZTest.Value);
			if (Cull != CullMode.Back) text.Append(", Cull ").Append(Cull);
			if (!CastShadows) text.Append(", no shadows");
			return text.ToString();
		}

		#endregion

		#region PrivateMethods

		private static string[] Split(string line)
		{
			return line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		}

		private bool ParseBlend(string text, out string error)
		{
			error = null;
			if (text.Equals("Off", StringComparison.OrdinalIgnoreCase))
			{
				BlendEnabled = false;
				return true;
			}

			// "Src Dst" or "Src Dst, SrcAlpha DstAlpha".
			string[] groups = text.Split(',');
			string[] color = Split(groups[0]);
			if (groups.Length > 2 || color.Length != 2 || !TryFactor(color[0], out BlendingFactor src) || !TryFactor(color[1], out BlendingFactor dst))
			{
				error = "expected 'Blend <SrcFactor> <DstFactor>[, <SrcFactor> <DstFactor>]' or 'Blend Off'";
				return false;
			}

			BlendEnabled = true;
			SrcColor = src;
			DstColor = dst;
			SrcAlpha = src;
			DstAlpha = dst;
			if (groups.Length == 2)
			{
				string[] alpha = Split(groups[1]);
				if (alpha.Length != 2 || !TryFactor(alpha[0], out SrcAlpha) || !TryFactor(alpha[1], out DstAlpha))
				{
					error = "expected two alpha blend factors after the comma";
					return false;
				}
			}

			return true;
		}

		private static bool TryFactor(string name, out BlendingFactor factor)
		{
			switch (name.ToLowerInvariant())
			{
				case "one": factor = BlendingFactor.One; return true;
				case "zero": factor = BlendingFactor.Zero; return true;
				case "srccolor": factor = BlendingFactor.SrcColor; return true;
				case "srcalpha": factor = BlendingFactor.SrcAlpha; return true;
				case "dstcolor": factor = BlendingFactor.DstColor; return true;
				case "dstalpha": factor = BlendingFactor.DstAlpha; return true;
				case "oneminussrccolor": factor = BlendingFactor.OneMinusSrcColor; return true;
				case "oneminussrcalpha": factor = BlendingFactor.OneMinusSrcAlpha; return true;
				case "oneminusdstcolor": factor = BlendingFactor.OneMinusDstColor; return true;
				case "oneminusdstalpha": factor = BlendingFactor.OneMinusDstAlpha; return true;
				default:
					factor = BlendingFactor.One;
					return false;
			}
		}

		private static bool ParseSwitch(string text, out bool value, out string error)
		{
			error = null;
			switch (text.Trim().ToLowerInvariant())
			{
				case "on":
				case "true":
				case "1":
					value = true;
					return true;
				case "off":
				case "false":
				case "0":
					value = false;
					return true;
				default:
					value = true;
					error = "expected On or Off";
					return false;
			}
		}

		private bool ParseDepthTest(string text, out string error)
		{
			error = null;
			switch (text.Trim().ToLowerInvariant())
			{
				case "less": ZTest = DepthFunction.Less; return true;
				case "lequal": ZTest = DepthFunction.Lequal; return true;
				case "equal": ZTest = DepthFunction.Equal; return true;
				case "gequal": ZTest = DepthFunction.Gequal; return true;
				case "greater": ZTest = DepthFunction.Greater; return true;
				case "notequal": ZTest = DepthFunction.Notequal; return true;
				case "always":
				case "off":
					ZTest = DepthFunction.Always;
					return true;
				default:
					error = "expected Less, LEqual, Equal, GEqual, Greater, NotEqual or Always";
					return false;
			}
		}

		private bool ParseCull(string text, out string error)
		{
			error = null;
			switch (text.Trim().ToLowerInvariant())
			{
				case "back": Cull = CullMode.Back; return true;
				case "front": Cull = CullMode.Front; return true;
				case "off": Cull = CullMode.Off; return true;
				default:
					error = "expected Back, Front or Off";
					return false;
			}
		}

		#endregion
	}
}
