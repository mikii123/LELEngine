using System;
using System.Globalization;
using System.Linq;
using System.Text;
using LELEngine.Graphics;

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
	///     LowResolution On                            // On / Off: the material's objects draw at half the view's resolution (large soft blended surfaces)
	///     Batching Off                                // On / Off: the material's objects are never merged into a static batch (Unity's DisableBatching tag)
	///     RayQueries On                               // On / Off: the shader has a LEL_RAY_QUERY variant that traces the lighting's rays per fragment
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
	///     LowResolution (transparent queues only): the transparent pass draws the material's objects into the layer at half
	///     the view's resolution that particle outputs with LowResolution use (<see cref="Rendering.LowResolutionTransparency" />):
	///     a quarter of the pixels, for surfaces whose cost is the area they cover many times over (sheets of fog, large
	///     glows). They draw there in their sorted order and the layer is laid over the scene where the last of them sorts,
	///     with the outline of what stands in front kept sharp. The layer keeps colour and what the scene shows through, so
	///     only blends it can hold go there: the target scaled by one minus the source's alpha (alpha, premultiplied) or kept
	///     (additive), with a source factor that does not read the target, no depth writes and no GrabPass; the shader's
	///     alpha factors are replaced by the layer's. Other blends draw at full resolution (the load says why). A shader with
	///     SceneDepth reads the opaque depth under its pixel the same way there (Engine/SceneDepth.glsl).
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
	///     RayQueries On: the shader's variant compiled with LEL_RAY_QUERY (<see cref="ShaderProgram.GetRayQueryVariant" />,
	///     the preamble enables GL_EXT_ray_query with it) traces rays per fragment: the transparent pass draws with it while
	///     transparent surfaces trace their shadows, and the passes that draw per-fragment voxel cones while those trace the
	///     scene's triangles (<see cref="Lighting.RayQueryProgram" />). Only on a device with ray queries.
	/// </summary>
	public sealed class ShaderRenderState
	{
		#region PublicFields

		/// <summary>The state of shaders that declare none: opaque, depth written, back faces culled.</summary>
		public static readonly ShaderRenderState Default = new ShaderRenderState();

		public bool BlendEnabled;
		public BlendFactor SrcColor = BlendFactor.SrcAlpha;
		public BlendFactor DstColor = BlendFactor.OneMinusSrcAlpha;
		public BlendFactor SrcAlpha = BlendFactor.SrcAlpha;
		public BlendFactor DstAlpha = BlendFactor.OneMinusSrcAlpha;
		public bool ZWrite = true;

		/// <summary>Depth test the shader asks for; null leaves the pass's own (Less, or LEqual behind a depth prepass).</summary>
		public CompareFunction? ZTest;

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

		/// <summary>
		///     LowResolution On: the transparent pass draws the material's objects into its half-resolution layer
		///     (<see cref="Rendering.LowResolutionTransparency" />) when the blend allows (<see cref="DrawsLowResolution" />).
		/// </summary>
		public bool LowResolution;

		/// <summary>The shader named its queue (otherwise it follows the blend state).</summary>
		public bool QueueDeclared;

		/// <summary>
		///     Batching Off clears it: the material's objects are not merged into static batches (the shader works from its
		///     object's own space, which a merged mesh no longer has).
		/// </summary>
		public bool Batching = true;

		/// <summary>RayQueries On: the shader has a variant that traces rays per fragment (<see cref="ShaderProgram.GetRayQueryVariant" />).</summary>
		public bool RayQueries;

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
				case "lowresolution":
				case "batching":
				case "rayqueries":
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
				case "lowresolution":
					return ParseSwitch(rest, out LowResolution, out error);
				case "batching":
					return ParseSwitch(rest, out Batching, out error);
				case "rayqueries":
					return ParseSwitch(rest, out RayQueries, out error);
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

				BlendFactor source = SrcColor;
				BlendFactor target = DstColor;
				bool sourceReadsTarget = source == BlendFactor.DstColor || source == BlendFactor.OneMinusDstColor || source == BlendFactor.DstAlpha
					|| source == BlendFactor.OneMinusDstAlpha || source == BlendFactor.SrcAlphaSaturate;

				// target + source x something of the source's own.
				if (target == BlendFactor.One && !sourceReadsTarget)
				{
					return true;
				}

				// target x source (also twice), and nothing else.
				if (source == BlendFactor.DstColor && (target == BlendFactor.Zero || target == BlendFactor.SrcColor))
				{
					return true;
				}

				if (source == BlendFactor.Zero && target == BlendFactor.SrcColor)
				{
					return true;
				}

				// Screen: 1 - (1 - source) x (1 - target).
				return source == BlendFactor.One && target == BlendFactor.OneMinusSrcColor;
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
			LowResolution = other.LowResolution;
			QueueDeclared = other.QueueDeclared;
			Batching = other.Batching;
			RayQueries = other.RayQueries;
		}

		/// <summary>
		///     LowResolution On and the half-resolution layer can hold this state (<see cref="LowResolutionProblem" /> is null):
		///     the transparent pass draws the material's objects there.
		/// </summary>
		public bool DrawsLowResolution => LowResolution && LowResolutionProblem() == null;

		/// <summary>
		///     Why LowResolution On cannot be kept for this state (it then draws at full resolution), or null. The layer holds
		///     the light its draws send and what the scene shows through them, laid over the scene as scene x alpha + colour:
		///     a draw may scale what is under it by one minus its alpha (alpha, premultiplied) or keep it (additive), with a
		///     source factor that does not read the target, and write no depth; a grab reads the full-resolution scene.
		/// </summary>
		public string LowResolutionProblem()
		{
			if (!LowResolution)
			{
				return null;
			}

			if (!BlendEnabled)
			{
				return "it does not blend";
			}

			if (ZWrite)
			{
				return "it writes depth (ZWrite On)";
			}

			if (GrabPass)
			{
				return "it grabs the scene (GrabPass)";
			}

			if (DstColor != BlendFactor.One && DstColor != BlendFactor.OneMinusSrcAlpha)
			{
				return "its blend's target factor is " + DstColor + " (One or OneMinusSrcAlpha can be laid over the scene)";
			}

			bool sourceReadsTarget = SrcColor == BlendFactor.DstColor || SrcColor == BlendFactor.OneMinusDstColor || SrcColor == BlendFactor.DstAlpha
				|| SrcColor == BlendFactor.OneMinusDstAlpha || SrcColor == BlendFactor.SrcAlphaSaturate;
			return sourceReadsTarget ? "its blend's source factor " + SrcColor + " reads the target" : null;
		}

		public void Finish()
		{
			if ((BlendEnabled || GrabPass || SceneDepth || LowResolution) && !QueueDeclared)
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
			ref RenderState state = ref GraphicsDevice.Current.Commands.State;
			if (BlendEnabled)
			{
				state.SetBlend(SrcColor, DstColor, SrcAlpha, DstAlpha);
			}
			else
			{
				state.Blend = false;
			}

			state.SetDepth(true, ZWrite, ZTest ?? CompareFunction.LessEqual);
			ApplyCull();
		}

		/// <summary>
		///     <see cref="ApplyTransparent" /> for a draw into the half-resolution layer (<see cref="DrawsLowResolution" />):
		///     the shader's colour factors, and the layer's alpha (what the scene shows through) scaled as the target's colour
		///     is: by one minus the source's alpha, or kept for an additive blend. No depth writes.
		/// </summary>
		public void ApplyLowResolution()
		{
			ref RenderState state = ref GraphicsDevice.Current.Commands.State;
			state.SetBlend(SrcColor, DstColor, BlendFactor.Zero, DstColor == BlendFactor.One ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha);
			state.SetDepth(true, false, ZTest ?? CompareFunction.LessEqual);
			ApplyCull();
		}

		/// <summary>Sets the cull state (every pass that draws with the material's shader).</summary>
		public void ApplyCull()
		{
			GraphicsDevice.Current.Commands.State.Culling = Cull == CullMode.Off ? FaceCulling.None : Cull == CullMode.Front ? FaceCulling.Front : FaceCulling.Back;
		}

		// The words the render state's description always used for depth tests.
		private static string TestName(CompareFunction function)
		{
			switch (function)
			{
				case CompareFunction.LessEqual: return "Lequal";
				case CompareFunction.GreaterEqual: return "Gequal";
				case CompareFunction.NotEqual: return "Notequal";
				default: return function.ToString();
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
			if (ZTest.HasValue) text.Append(", ZTest ").Append(TestName(ZTest.Value));
			if (Cull != CullMode.Back) text.Append(", Cull ").Append(Cull);
			if (!CastShadows) text.Append(", no shadows");
			if (CustomShadowCaster) text.Append(", own shadow caster");
			if (CustomGeometryPrepass) text.Append(", own geometry prepass");
			if (GrabPass) text.Append(", GrabPass").Append(GrabOffsetProperty != null ? " (" + GrabOffsetProperty + ")" : "");
			if (SceneDepth) text.Append(", SceneDepth");
			if (LowResolution) text.Append(DrawsLowResolution ? ", half resolution" : ", LowResolution ignored: " + LowResolutionProblem());
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
			if (groups.Length > 2 || color.Length != 2 || !TryFactor(color[0], out BlendFactor src) || !TryFactor(color[1], out BlendFactor dst))
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

		private static bool TryFactor(string name, out BlendFactor factor)
		{
			switch (name.ToLowerInvariant())
			{
				case "one": factor = BlendFactor.One; return true;
				case "zero": factor = BlendFactor.Zero; return true;
				case "srccolor": factor = BlendFactor.SrcColor; return true;
				case "srcalpha": factor = BlendFactor.SrcAlpha; return true;
				case "dstcolor": factor = BlendFactor.DstColor; return true;
				case "dstalpha": factor = BlendFactor.DstAlpha; return true;
				case "oneminussrccolor": factor = BlendFactor.OneMinusSrcColor; return true;
				case "oneminussrcalpha": factor = BlendFactor.OneMinusSrcAlpha; return true;
				case "oneminusdstcolor": factor = BlendFactor.OneMinusDstColor; return true;
				case "oneminusdstalpha": factor = BlendFactor.OneMinusDstAlpha; return true;
				default:
					factor = BlendFactor.One;
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
				case "less": ZTest = CompareFunction.Less; return true;
				case "lequal": ZTest = CompareFunction.LessEqual; return true;
				case "equal": ZTest = CompareFunction.Equal; return true;
				case "gequal": ZTest = CompareFunction.GreaterEqual; return true;
				case "greater": ZTest = CompareFunction.Greater; return true;
				case "notequal": ZTest = CompareFunction.NotEqual; return true;
				case "always":
				case "off":
					ZTest = CompareFunction.Always;
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
