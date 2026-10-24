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

		public ShaderFeatures(DrawParametersSupport drawParameters, bool computeSubgroups, bool previousModels)
		{
			DrawParameters = drawParameters;
			ComputeSubgroups = computeSubgroups;
			PreviousModels = previousModels;
		}

		#endregion

		#region PublicFields

		public DrawParametersSupport DrawParameters { get; }

		/// <summary>KHR_shader_subgroup basic and arithmetic operations in compute stages (LEL_SUBGROUPS).</summary>
		public bool ComputeSubgroups { get; }

		/// <summary>A storage binding left for last frame's model matrices (else LEL_NO_PREVIOUS_MODELS).</summary>
		public bool PreviousModels { get; }

		/// <summary>The features of the current context (<see cref="GLCapabilities" />).</summary>
		public static ShaderFeatures Current => new ShaderFeatures(
			!GLCapabilities.ShaderDrawParameters ? DrawParametersSupport.None : GLCapabilities.ShaderDrawParametersExtension ? DrawParametersSupport.Extension : DrawParametersSupport.Core,
			GLCapabilities.ComputeSubgroups,
			GpuScene.SupportsPreviousModels);

		#endregion

		#region PublicMethods

		/// <summary>"draw parameters ARB, subgroups, previous models".</summary>
		public override string ToString()
		{
			string drawParameters = DrawParameters == DrawParametersSupport.Extension ? "draw parameters ARB" : DrawParameters == DrawParametersSupport.Core ? "draw parameters GLSL 4.60" : "no draw parameters";
			return drawParameters + (ComputeSubgroups ? ", subgroups" : "") + (PreviousModels ? ", previous models" : "");
		}

		#endregion
	}
}
