using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Last frame's world matrix of every renderer a frame draws: the geometry prepass writes each surface point's
	///     world-space motion from it (the motion vectors the GI's temporal filters reproject with). One per
	///     <see cref="Renderer" />, so several views of the same scene (editor viewports) each see their own previous
	///     frame. A renderer seen for the first time, or again after a frame without it, has not moved.
	/// </summary>
	public sealed class ObjectMotion
	{
		#region PrivateFields

		private Dictionary<MeshRenderer, Matrix4> previous = new Dictionary<MeshRenderer, Matrix4>();
		private Dictionary<MeshRenderer, Matrix4> current = new Dictionary<MeshRenderer, Matrix4>();

		#endregion

		#region PublicMethods

		/// <summary>Starts a frame: the matrices recorded during the last one become the previous ones.</summary>
		public void BeginFrame()
		{
			Dictionary<MeshRenderer, Matrix4> swap = previous;
			previous = current;
			current = swap;
			current.Clear();
		}

		/// <summary>The renderer's world matrix in the previous frame (its current one when it was not drawn then); records the current one.</summary>
		public Matrix4 GetPrevious(MeshRenderer renderer, Matrix4 model)
		{
			current[renderer] = model;
			return previous.TryGetValue(renderer, out Matrix4 last) ? last : model;
		}

		/// <summary>Forgets every renderer (the scene was replaced: its renderers must not be kept alive).</summary>
		public void Clear()
		{
			previous.Clear();
			current.Clear();
		}

		#endregion
	}
}
