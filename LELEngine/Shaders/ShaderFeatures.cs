using LELEngine.Rendering;

namespace LELEngine.Shaders
{
	/// <summary>Where vertex shaders get gl_BaseInstance from (GPU-driven draws), see <see cref="ShaderFeatures" />.</summary>
	public enum DrawParametersSupport
	{
		/// <summary>Not at all: the instance index comes from a per-instance vertex attribute.</summary>
		None,

		/// <summary>ARB_shader_draw_parameters (gl_BaseInstanceARB).</summary>
		Extension,

		/// <summary>GLSL 4.60 core (gl_BaseInstance): the vertex stage's #version becomes 460.</summary>
		Core
	}

	/// <summary>
	///     The native features of a GL context that the engine preamble adapts every shader stage to (defines and
	///     extension directives after the #version line, see <see cref="ShaderProgram" />): the same file compiles to
	///     different code on different GPUs. <see cref="Current" /> is the context's; offline validation of a game build
	///     checks each kind.
	/// </summary>
	public readonly struct ShaderFeatures
	{
		#region Constructors

		public ShaderFeatures(DrawParametersSupport drawParameters, bool computeSubgroups, bool previousModels, bool rayQueries = false, bool positionFetch = false)
		{
			DrawParameters = drawParameters;
			ComputeSubgroups = computeSubgroups;
			PreviousModels = previousModels;
			RayQueries = rayQueries;
			PositionFetch = rayQueries && positionFetch;
		}

		#endregion

		#region PublicFields

		public DrawParametersSupport DrawParameters { get; }

		/// <summary>KHR_shader_subgroup basic and arithmetic operations in compute stages (LEL_SUBGROUPS).</summary>
		public bool ComputeSubgroups { get; }

		/// <summary>A storage binding left for last frame's model matrices (else LEL_NO_PREVIOUS_MODELS).</summary>
		public bool PreviousModels { get; }

		/// <summary>
		///     Ray queries (GL_EXT_ray_query, <see cref="GraphicsCapabilities.RayQueries" />): a stage compiled with the define
		///     <see cref="ShaderProgram.RayQueryDefine" /> (a variant the GI's tracing passes ask for while they trace the
		///     scene's triangles) gets the extension; without ray queries the define is left out and the stage compiles its
		///     other path.
		/// </summary>
		public bool RayQueries { get; }

		/// <summary>
		///     A ray query reads its hit triangle's corners from the structure (GL_EXT_ray_tracing_position_fetch,
		///     <see cref="GraphicsCapabilities.RayTracingPositionFetch" />): the ray query variants get the extension and
		///     LEL_RAY_POSITION_FETCH.
		/// </summary>
		public bool PositionFetch { get; }

		/// <summary>The features of the current context (<see cref="GraphicsCapabilities" />).</summary>
		public static ShaderFeatures Current => new ShaderFeatures(
			!GraphicsCapabilities.ShaderDrawParameters ? DrawParametersSupport.None : GraphicsCapabilities.ShaderDrawParametersExtension ? DrawParametersSupport.Extension : DrawParametersSupport.Core,
			GraphicsCapabilities.ComputeSubgroups,
			GpuScene.SupportsPreviousModels,
			GraphicsCapabilities.RayQueries,
			GraphicsCapabilities.RayTracingPositionFetch);

		#endregion

		#region PublicMethods

		/// <summary>"draw parameters ARB, subgroups, previous models, ray queries".</summary>
		public override string ToString()
		{
			string drawParameters = DrawParameters == DrawParametersSupport.Extension ? "draw parameters ARB" : DrawParameters == DrawParametersSupport.Core ? "draw parameters GLSL 4.60" : "no draw parameters";
			return drawParameters + (ComputeSubgroups ? ", subgroups" : "") + (PreviousModels ? ", previous models" : "") + (RayQueries ? ", ray queries" : "") + (PositionFetch ? ", position fetch" : "");
		}

		#endregion
	}
}
