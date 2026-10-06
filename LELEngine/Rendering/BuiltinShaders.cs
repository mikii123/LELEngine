using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     GLSL sources for passes owned by the engine (depth-only, fullscreen post-process).
	///     Game shaders live in the game's Shaders directory; these must always be available.
	/// </summary>
	public static class BuiltinShaders
	{
		#region PublicFields

		/// <summary>
		///     Writes depth only. Used by the shadow pass and the depth prepass.
		///     gl_Position is declared invariant so the depth prepass matches material shaders exactly.
		/// </summary>
		public const string DepthOnlyVertex = @"
#version 330 core
invariant gl_Position;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

in vec3 vPosition;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
}
";

		public const string DepthOnlyFragment = @"
#version 330 core

void main()
{
}
";

		/// <summary>
		///     Fullscreen triangle generated from gl_VertexID.
		/// </summary>
		public const string FullscreenVertex = @"
#version 330 core

out vec2 fUV;

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	fUV = pos;
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}
";

		/// <summary>
		///     HDR scene color to display: exposure, ACES tonemap, gamma.
		/// </summary>
		public const string TonemapFragment = @"
#version 330 core

in vec2 fUV;
out vec4 FragColor;

uniform sampler2D SceneColor;
uniform float Exposure;
uniform int TonemapEnabled;

vec3 ACESFilm(vec3 x)
{
	const float a = 2.51;
	const float b = 0.03;
	const float c = 2.43;
	const float d = 0.59;
	const float e = 0.14;
	return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
	vec3 hdr = texture(SceneColor, fUV).rgb * Exposure;
	vec3 color = TonemapEnabled != 0 ? ACESFilm(hdr) : clamp(hdr, 0.0, 1.0);
	color = pow(color, vec3(1.0 / 2.2));
	FragColor = vec4(color, 1.0);
}
";

		#endregion

		#region PublicMethods

		public static ShaderProgram CreateDepthOnly()
		{
			return Create("Builtin/DepthOnly", DepthOnlyVertex, DepthOnlyFragment);
		}

		public static ShaderProgram CreateTonemap()
		{
			return Create("Builtin/Tonemap", FullscreenVertex, TonemapFragment);
		}

		/// <summary>
		///     Builds a fullscreen post-process program from a fragment shader source.
		///     The fragment stage receives "in vec2 fUV".
		/// </summary>
		public static ShaderProgram CreateFullscreen(string name, string fragmentSource)
		{
			return Create(name, FullscreenVertex, fragmentSource);
		}

		#endregion

		#region PrivateMethods

		private static ShaderProgram Create(string name, string vertexSource, string fragmentSource)
		{
			Shader vertex = new Shader(vertexSource, ShaderType.VertexShader, name);
			Shader fragment = new Shader(fragmentSource, ShaderType.FragmentShader, name);
			ShaderProgram program = new ShaderProgram(name, vertex, fragment);
			vertex.Delete();
			fragment.Delete();
			return program;
		}

		#endregion
	}
}
