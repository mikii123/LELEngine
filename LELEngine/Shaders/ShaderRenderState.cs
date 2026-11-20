using System;
using System.Globalization;
using System.Linq;
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
	///     ShadowCaster Custom                         // the shader draws its casters itself (Unity: a shader with a ShadowCaster pass of its own)
	///     GeometryPrepass Custom                      // Custom / Default: the shader draws its depth, normal and motion itself (Unity: its own DepthNormals pass)
	///     SurfaceCapture On                           // On / Off: the shader can write its surface instead of lighting it (what impostor bakes capture)
	///     GrabPass On                                 // On / Off / a float property: the scene drawn so far is copied before each draw (Unity's GrabPass)
	///     SceneDepth On                               // On / Off: the shader reads the opaque scene's depth (Unity's _CameraDepthTexture, soft particles)
	///     Batching Off                                // On / Off: the material's objects are never merged into a static batch (Unity's DisableBatching tag)
	///     </code>
	///     A shader that blends or grabs without naming a queue goes to the Transparent queue. Blending, depth writes and
	///     the depth test apply in the transparent pass (the opaque passes draw with their own depth state and no
	///     blending); the cull mode applies everywhere a material shader draws. Each variant of a shader
	///     (<see cref="ShaderProgram.GetVariant" />) has its own state: the lines may sit in #if blocks over its keywords.
	///     GrabPass (transparent queues only): the transparent pass copies what it has drawn so far (opaques, sky and the
	///     transparent objects before this one) into a texture the shader reads through Engine/GrabPass.glsl, right before
	///     the object draws. Naming a float property instead of On ("GrabPass RefractionStrength") bounds how far the
	///     shader shifts its lookups (LelSceneRefraction's strength, world units): only the object's screen rectangle
	///     widened by that shift is copied, and one copy serves the later objects nothing drawn since overlaps.
	///     SceneDepth (transparent queues only): the transparent pass copies the depth buffer once the opaques are drawn and
	///     binds the copy to the shader's draws (Engine/SceneDepth.glsl).
	///     ShadowCaster Custom and GeometryPrepass Custom (opaque queues): the engine draws depth with programs of its own
	///     that only place the mesh's vertices, so a shader that shapes its vertices, cuts its surface out by alpha or
	///     writes its own depth (a billboard that stands for a mesh: Engine/Impostor.shader) draws those passes itself,
	///     with the variant of the same file compiled for the pass (<see cref="ShaderProgram.GetPassVariant" />: the
	///     define LEL_SHADOW_CASTER or LEL_GEOMETRY_PREPASS next to the material's keywords). The variant gets the pass's
	///     view (viewMatrix, projectionMatrix), the object (Engine/Instancing.glsl, GPU-driven too) and the material's
	///     values. A shadow caster writes depth only; a geometry prepass writes what Engine/GBuffer.shader writes (world
	///     normal + roughness at location 0, world motion at location 1) and the depth its own shading then tests against.
	///     SurfaceCapture On: the variant compiled with LEL_SURFACE_CAPTURE writes the surface the shader would light
	///     (see <see cref="ShaderPass.SurfaceCapture" />) and reads no lighting.
	///     Batching Off: a game build's static batching (<see cref="StaticBatch" />) moves the meshes it merges into one
	///     space, so a shader that works from its object's own space (a billboard built around the object's origin, an
	///     effect measured from its pivot or along its axes) would draw wrong in a batch: its objects stay as they are.
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

		/// <summary>
		///     ShadowCaster Custom: the shadow passes draw the material's objects with the shader's own variant
		///     (<see cref="ShaderPass.ShadowCaster" />) instead of the engine's depth program.
		/// </summary>
		public bool CustomShadowCaster;

		/// <summary>
		///     GeometryPrepass Custom: the geometry prepass draws the material's objects with the shader's own variant
		///     (<see cref="ShaderPass.GeometryPrepass" />) instead of Engine/GBuffer.shader.
		/// </summary>
		public bool CustomGeometryPrepass;

		/// <summary>SurfaceCapture On: the shader has a variant that writes its surface (<see cref="ShaderPass.SurfaceCapture" />).</summary>
		public bool SurfaceCapture;

		/// <summary>
		///     The transparent pass copies the scene drawn so far right before each of the material's objects draws
		///     (Unity's GrabPass); the shader reads the copy through Engine/GrabPass.glsl.
		/// </summary>
		public bool GrabPass;

		/// <summary>
		///     The float property bounding how far the shader shifts its lookups into the grab (world units at the object's
		///     distance, LelSceneRefraction's strength): only the object's screen bounds widened by that shift are copied.
		///     Null (GrabPass On): the shader may read anywhere, the whole screen is copied.
		/// </summary>
		public string GrabOffsetProperty;

		/// <summary>
		///     The shader reads the opaque scene's depth (Unity's _CameraDepthTexture, e.g. soft particles) through
		///     Engine/SceneDepth.glsl: the transparent pass copies the depth buffer after the opaques and binds it.
		/// </summary>
		public bool SceneDepth;

		/// <summary>The shader named its queue (otherwise it follows the blend state).</summary>
		public bool QueueDeclared;

		/// <summary>
		///     Batching Off clears it: the material's objects are not merged into static batches (the shader works from its
		///     object's own space, which a merged mesh no longer has).
		/// </summary>
		public bool Batching = true;

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
				case "geometryprepass":
				case "surfacecapture":
				case "grabpass":
				case "scenedepth":
				case "batching":
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
					return ParseShadowCaster(rest, out error);
				case "geometryprepass":
					return ParseGeometryPrepass(rest, out error);
				case "surfacecapture":
					return ParseSwitch(rest, out SurfaceCapture, out error);
				case "grabpass":
					return ParseGrabPass(rest, out error);
				case "scenedepth":
					return ParseSwitch(rest, out SceneDepth, out error);
				case "batching":
					return ParseSwitch(rest, out Batching, out error);
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

		/// <summary>
		///     Blend, GrabPass or SceneDepth without a queue: the Transparent queue (call once the block is read). A grab and
		///     the depth copy exist only in the transparent pass, so such a shader that names an opaque queue draws right
		///     after the opaques.
		/// </summary>
		/// <summary>
		///     Draws of this state give the same picture in any order: they do not blend and write depth, or their blend
		///     only adds to the target (additive), only multiplies it, or is the screen blend, without writing depth. Alpha
		///     blending (the target scaled by one minus the source's alpha) does not: such draws go back to front. What a
		///     static batch may draw in one go (<see cref="StaticBatch" />).
		/// </summary>
		public bool OrderIndependent
		{
			get
			{
				if (!BlendEnabled)
				{
					return ZWrite;
				}

				if (ZWrite)
				{
					return false;
				}

				BlendingFactor source = SrcColor;
				BlendingFactor target = DstColor;
				bool sourceReadsTarget = source == BlendingFactor.DstColor || source == BlendingFactor.OneMinusDstColor || source == BlendingFactor.DstAlpha
					|| source == BlendingFactor.OneMinusDstAlpha || source == BlendingFactor.SrcAlphaSaturate;

				// target + source x something of the source's own.
				if (target == BlendingFactor.One && !sourceReadsTarget)
				{
					return true;
				}

				// target x source (also twice), and nothing else.
				if (source == BlendingFactor.DstColor && (target == BlendingFactor.Zero || target == BlendingFactor.SrcColor))
				{
					return true;
				}

				if (source == BlendingFactor.Zero && target == BlendingFactor.SrcColor)
				{
					return true;
				}

				// Screen: 1 - (1 - source) x (1 - target).
				return source == BlendingFactor.One && target == BlendingFactor.OneMinusSrcColor;
			}
		}

		/// <summary>Takes every value of <paramref name="other" /> (a shader read again keeps its state object).</summary>
		public void CopyFrom(ShaderRenderState other)
		{
			BlendEnabled = other.BlendEnabled;
			SrcColor = other.SrcColor;
			DstColor = other.DstColor;
			SrcAlpha = other.SrcAlpha;
			DstAlpha = other.DstAlpha;
			ZWrite = other.ZWrite;
			ZTest = other.ZTest;
			Cull = other.Cull;
			Queue = other.Queue;
			CastShadows = other.CastShadows;
			CustomShadowCaster = other.CustomShadowCaster;
			CustomGeometryPrepass = other.CustomGeometryPrepass;
			SurfaceCapture = other.SurfaceCapture;
			GrabPass = other.GrabPass;
			GrabOffsetProperty = other.GrabOffsetProperty;
			SceneDepth = other.SceneDepth;
			QueueDeclared = other.QueueDeclared;
			Batching = other.Batching;
		}

		public void Finish()
		{
			if ((BlendEnabled || GrabPass || SceneDepth) && !QueueDeclared)
			{
				Queue = MaterialQueue.Transparent;
			}

			if ((GrabPass || SceneDepth) && !MaterialQueue.IsTransparent(Queue))
			{
				Queue = MaterialQueue.GeometryLast + 1;
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
			if (CustomShadowCaster) text.Append(", own shadow caster");
			if (CustomGeometryPrepass) text.Append(", own geometry prepass");
			if (GrabPass) text.Append(", GrabPass").Append(GrabOffsetProperty != null ? " (" + GrabOffsetProperty + ")" : "");
			if (SceneDepth) text.Append(", SceneDepth");
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

		// "On" / "Off", or "Custom": on, drawn by the shader's own variant.
		private bool ParseShadowCaster(string text, out string error)
		{
			if (text.Trim().Equals("Custom", StringComparison.OrdinalIgnoreCase))
			{
				error = null;
				CastShadows = true;
				CustomShadowCaster = true;
				return true;
			}

			CustomShadowCaster = false;
			if (ParseSwitch(text, out CastShadows, out error))
			{
				return true;
			}

			error = "expected On, Off or Custom";
			return false;
		}

		// "Custom" (the shader's own variant) or "Default" (Engine/GBuffer.shader).
		private bool ParseGeometryPrepass(string text, out string error)
		{
			error = null;
			switch (text.Trim().ToLowerInvariant())
			{
				case "custom":
					CustomGeometryPrepass = true;
					return true;
				case "default":
					CustomGeometryPrepass = false;
					return true;
				default:
					error = "expected Custom or Default";
					return false;
			}
		}

		// "On" / "Off", or the float property bounding the shader's lookup shift (on, region copies).
		private bool ParseGrabPass(string text, out string error)
		{
			string value = text.Trim();
			if (ParseSwitch(value, out GrabPass, out error))
			{
				GrabOffsetProperty = null;
				return true;
			}

			if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_') || !value.All(c => char.IsLetterOrDigit(c) || c == '_'))
			{
				GrabPass = false;
				error = "expected On, Off or the name of the float property bounding the lookup offset";
				return false;
			}

			error = null;
			GrabPass = true;
			GrabOffsetProperty = value;
			return true;
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
