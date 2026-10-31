using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A component that draws a mesh it builds itself (procedural effects whose shape the shader makes from the
	///     component's values): one entry of the transparent pass, sorted with the other transparent objects at the centre
	///     of its bounds. Asked once per camera and frame while it is active and enabled on one of the camera's layers. Its
	///     draw has no shadows and no GI. The inspector lists its material.
	/// </summary>
	public interface ICustomRenderer
	{
		/// <summary>The material drawn (nothing is drawn without one, or while its shader does not build).</summary>
		Material Material { get; }

		/// <summary>
		///     This draw: the mesh, the shader values on top of the material's (null: none), and the mesh's bounds in the
		///     transform's space as the shader shapes it (sorting, grab rectangles). False draws nothing this time.
		/// </summary>
		bool PrepareDraw(out Mesh mesh, out MaterialPropertyBlock block, out Vector3 boundsMin, out Vector3 boundsMax);
	}
}
