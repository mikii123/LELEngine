using System;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     When an editor view renders. A view renders while something it shows changes (camera, view size, the
	///     scene: <see cref="EditorApplication.SceneVersion" />) and for a settle period afterwards, because the GI
	///     keeps accumulating over frames; then it stops and the last image stays on screen. Play mode, "Always
	///     Refresh" and edit-mode scripts ([ExecuteAlways] game components) render every frame.
	///     Settle periods measured on the default scene (lossless dumps against the converged image): from a cold start
	///     no pixel is off by more than 1/255 after 120 frames (max 2/255), after a camera turn after 60 frames.
	/// </summary>
	internal sealed class ViewRefresh
	{
		#region PublicFields

		/// <summary>Frames rendered after a scene change (or the first frame of the view).</summary>
		public const int SceneSettleFrames = 240;

		/// <summary>Frames rendered after a camera move or a view resize.</summary>
		public const int CameraSettleFrames = 90;

		/// <summary>The view shows its last image without rendering.</summary>
		public bool IsIdle => framesLeft <= 0;

		#endregion

		#region PrivateFields

		private int framesLeft = SceneSettleFrames;
		// An id, not the camera: a scene camera held here would keep an unloaded scene (and its scripts) alive.
		private ulong lastCameraId;
		private Matrix4 lastViewProjection;
		private int lastWidth;
		private int lastHeight;
		private int lastSceneVersion = int.MinValue;

		#endregion

		#region PublicMethods

		/// <summary>Renders the next frame(s) whatever changed (tests, explicit requests).</summary>
		public void Request(int frames = 1)
		{
			framesLeft = Math.Max(framesLeft, frames);
		}

		/// <summary>
		///     Call once per frame with the camera's current matrices (<see cref="Camera.UpdateMatrices" /> done):
		///     true when the view must render this frame.
		/// </summary>
		public bool ShouldRender(Camera camera, int width, int height, int sceneVersion, bool continuous)
		{
			if (sceneVersion != lastSceneVersion)
			{
				lastSceneVersion = sceneVersion;
				framesLeft = Math.Max(framesLeft, SceneSettleFrames);
			}

			Matrix4 viewProjection = camera.ViewProjectionMatrix;
			if (camera.Id != lastCameraId || viewProjection != lastViewProjection || width != lastWidth || height != lastHeight)
			{
				lastCameraId = camera.Id;
				lastViewProjection = viewProjection;
				lastWidth = width;
				lastHeight = height;
				framesLeft = Math.Max(framesLeft, CameraSettleFrames);
			}

			if (continuous)
			{
				// Settles like after a camera move once the continuous rendering ends.
				framesLeft = Math.Max(framesLeft, CameraSettleFrames);
				return true;
			}

			if (framesLeft <= 0)
			{
				return false;
			}

			framesLeft--;
			return true;
		}

		#endregion
	}
}
