using System;
using System.Linq;
using LELEngine.Rendering;

namespace LELEngine.Shaders
{
	public sealed partial class ShaderProgram
	{
		#region PublicFields

		/// <summary>This program is compiled with <see cref="RayQueryDefine" /> on a device with ray queries: its stages trace rays.</summary>
		public bool TracesRays => (Array.IndexOf(defines, RayQueryDefine) >= 0 || (generatedDefines != null && Array.IndexOf(generatedDefines, RayQueryDefine) >= 0)) && GraphicsCapabilities.RayQueries;

		/// <summary>
		///     The program reads the directional lights' shadow maps (Engine/Shadows.glsl's switch is in it) and not the traced
		///     shadows (Engine/RayTracedShadows.glsl's): what it draws needs every caster in the maps
		///     (<see cref="Rendering.DirectionalShadowMapReaders" />). Asked once per program.
		/// </summary>
		internal bool ReadsShadowMapsOnly
		{
			get
			{
				int known = shadowReads;
				if (known == 0)
				{
					known = !IsLinked ? 1 : HasUniform("shadowsEnabled") && !HasUniform("rayShadowDirectional") ? 2 : 1;
					shadowReads = known;
				}

				return known == 2;
			}
		}

		#endregion

		#region PrivateFields

		// The variant of this program compiled with RayQueryDefine (GetRayQueryVariant), made on first use.
		private ShaderProgram rayQueryVariant;

		// The defines a generated program was compiled with (FromSources, StartFromSources; a file's are its defines).
		private string[] generatedDefines;

		// ReadsShadowMapsOnly: 0 not asked yet, 1 no, 2 yes.
		private int shadowReads;

		#endregion

		#region PublicMethods

		/// <summary>
		///     This program compiled with <see cref="RayQueryDefine" /> as well (its keywords, its instanced define kept): the
		///     variant the passes draw a shader with whose render state says <c>RayQueries On</c> while the lighting traces per
		///     fragment (<see cref="Lighting.RayQueryProgram" />). Made on first use and kept with this program; null for a
		///     program that is not from a file, on a device without ray queries, or when the variant does not link.
		/// </summary>
		public ShaderProgram GetRayQueryVariant()
		{
			if (SourcePath == null || !GraphicsCapabilities.RayQueries)
			{
				return null;
			}

			if (Array.IndexOf(defines, RayQueryDefine) >= 0)
			{
				return this;
			}

			ShaderProgram made = rayQueryVariant;
			if (made == null)
			{
				lock (variants)
				{
					made = rayQueryVariant;
					if (made == null)
					{
						made = new ShaderProgram(SourcePath, defines.Append(RayQueryDefine).ToArray(), false) { assetPath = assetPath };
						rayQueryVariant = made;
					}
				}

				// Outside the lock, as the other variants: on the compile thread this builds it whole.
				made.StartBuild();
				if (!made.IsLinked)
				{
					Console.WriteLine("[Shader] The ray query variant of " + Name + " does not link; it draws without it:\n" + made.BuildLog);
				}
			}

			return made.IsLinked ? made : null;
		}

		#endregion
	}
}
