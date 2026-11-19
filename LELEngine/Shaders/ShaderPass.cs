using System;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Passes a material shader can draw with a variant of its own instead of leaving them to the engine's programs:
	///     the same .shader file compiled with the pass's define next to the material's keywords
	///     (<see cref="ShaderProgram.GetPassVariant" />). A shader declares which it has in its Properties block
	///     (<see cref="ShaderRenderState" />: ShadowCaster Custom, GeometryPrepass Custom, SurfaceCapture On).
	/// </summary>
	public enum ShaderPass
	{
		/// <summary>
		///     Shadow maps (define LEL_SHADOW_CASTER): depth as the light sees the object. The variant gets the light's
		///     viewMatrix and projectionMatrix (orthographic for a directional light), the object and the material's
		///     values; it has no colour output.
		/// </summary>
		ShadowCaster,

		/// <summary>
		///     The geometry prepass (define LEL_GEOMETRY_PREPASS): the depth the shader's own shading tests against, and
		///     the outputs of Engine/GBuffer.shader: location 0 the world normal (xyz) and roughness (w), location 1 the
		///     world-space motion since the previous frame (xyz, w 1 where it moved; the previous world matrix comes from
		///     Engine/Instancing.glsl with LEL_PREVIOUS_MODELS).
		/// </summary>
		GeometryPrepass,

		/// <summary>
		///     The surface instead of its lighting (define LEL_SURFACE_CAPTURE), for bakes that light it later (impostors):
		///     location 0 the diffuse colour (rgb; a 1), location 1 the world normal (xyz), location 2 the specular colour
		///     (rgb) and smoothness (a), location 3 the emitted radiance (rgb) and the occlusion of the indirect light (a).
		///     The lit shaders' values: a metallic material gives its diffuse and specular colours, the scene workflow a
		///     specular colour of zero (it takes its highlight from the scene's settings).
		/// </summary>
		SurfaceCapture
	}

	/// <summary>Names of the passes' defines.</summary>
	public static class ShaderPasses
	{
		#region PublicFields

		public const string ShadowCasterDefine = "LEL_SHADOW_CASTER";
		public const string GeometryPrepassDefine = "LEL_GEOMETRY_PREPASS";
		public const string SurfaceCaptureDefine = "LEL_SURFACE_CAPTURE";

		/// <summary>The number of passes (<see cref="ShaderPass" /> values index arrays of this length).</summary>
		public const int Count = 3;

		/// <summary>
		///     The passes a frame draws: their variants are built ahead with the material's program (scene loads, the
		///     shader lists of game builds). The surface capture belongs to bakes and compiles when one runs.
		/// </summary>
		public static readonly ShaderPass[] Drawn = { ShaderPass.ShadowCaster, ShaderPass.GeometryPrepass };

		#endregion

		#region PublicMethods

		/// <summary>The define a pass's variant is compiled with.</summary>
		public static string DefineOf(ShaderPass pass)
		{
			switch (pass)
			{
				case ShaderPass.ShadowCaster: return ShadowCasterDefine;
				case ShaderPass.GeometryPrepass: return GeometryPrepassDefine;
				case ShaderPass.SurfaceCapture: return SurfaceCaptureDefine;
				default: throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
			}
		}

		/// <summary>Whether a shader's render state declares its own variant for the pass.</summary>
		public static bool Declares(ShaderRenderState state, ShaderPass pass)
		{
			switch (pass)
			{
				case ShaderPass.ShadowCaster: return state.CustomShadowCaster;
				case ShaderPass.GeometryPrepass: return state.CustomGeometryPrepass;
				case ShaderPass.SurfaceCapture: return state.SurfaceCapture;
				default: return false;
			}
		}

		/// <summary>True for a define that names a pass (not a keyword of a material).</summary>
		public static bool IsPassDefine(string define)
		{
			return define == ShadowCasterDefine || define == GeometryPrepassDefine || define == SurfaceCaptureDefine;
		}

		#endregion
	}
}
