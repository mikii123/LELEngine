using System.Collections.Generic;
using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

/// <summary>
///     How a camera takes part in rendering (as URP's camera stacking): a base camera clears the image and renders,
///     then the overlay cameras of its <see cref="Camera.Stack" /> render on top of it, one after another.
/// </summary>
public enum CameraRenderType
{
	Base,
	Overlay
}

/// <summary>
///     Perspective camera. It draws the objects on the layers of its <see cref="CullingMask" />. A base camera
///     clears the image and renders, then the overlay cameras of its <see cref="Stack" /> draw over it in order
///     (each with its own mask, clearing the depth first when <see cref="ClearDepth" /> is set). One camera of a
///     stack lights the world (<see cref="LightingCamera" />, the base camera by default): its view gets the
///     screen-space GI, the shadow cascades and the GI volume follow it, and its layers are the world that casts
///     shadows and contributes to the GI. The other cameras of the stack use the same shadow map and light their
///     objects from the GI volume (voxel cones, Lumen's radiance cache probes). Every camera of a stack is culled on
///     the GPU from the same instance tables, built once per frame.
///     <see cref="main" /> is the lighting camera of the main stack (also in edit mode); <see cref="current" /> is the
///     camera the renderer is drawing with right now (the editor's scene view camera is not part of any scene).
/// </summary>
[ExecuteAlways]
public sealed class Camera : Behaviour
{
	#region PublicFields

	/// <summary>
	///     The camera that sees the world: the lighting camera of the first enabled base camera of the active scene
	///     (<see cref="SceneManager.MainCamera" />), so a background camera rendering first does not take its place.
	/// </summary>
	public static Camera main => SceneManager.MainCamera?.GetStackLightingCamera();

	/// <summary>Camera of the frame being rendered (set by the renderer).</summary>
	public static Camera current { get; internal set; }

	/// <summary>Base: clears the image and renders, then its <see cref="Stack" />. Overlay: renders only in a base camera's stack.</summary>
	public CameraRenderType RenderType = CameraRenderType.Base;

	/// <summary>Vertical field of view in degrees.</summary>
	public float FoV = 60f;

	public float NearClip = 0.1f;

	public float FarClip = 1000f;

	/// <summary>The layers this camera draws (renderers, visual effects).</summary>
	public LayerMask CullingMask = LayerMask.Everything;

	/// <summary>Overlay: clears the depth before drawing, so its objects draw over everything the cameras before it drew.</summary>
	[HideInInspector] public bool ClearDepth = true;

	/// <summary>Base: the overlay cameras drawn over this one, in order (disabled ones and base cameras are skipped).</summary>
	[HideInInspector] public List<Camera> Stack = new List<Camera>();

	/// <summary>
	///     Base: the camera of the stack that lights the world (null: this camera). Its view gets the screen-space GI,
	///     the shadow cascades and the GI volume follow it, and its layers cast the shadows and make up the GI.
	/// </summary>
	[HideInInspector] public Camera LightingCamera;

	/// <summary>Width / height of the view; set by the renderer every frame.</summary>
	[System.NonSerialized] public float Aspect = 4f / 3f;

	public Matrix4 ViewMatrix { get; private set; } = Matrix4.Identity;
	public Matrix4 ProjectionMatrix { get; private set; } = Matrix4.Identity;
	public Matrix4 ViewProjectionMatrix { get; private set; } = Matrix4.Identity;

	#endregion

	#region PublicMethods

	/// <summary>
	///     Recomputes view and projection from the current transform. Called by the renderer once per frame.
	/// </summary>
	public void UpdateMatrices(float aspect)
	{
		Aspect = aspect;
		ProjectionMatrix = Matrix4.CreatePerspectiveFieldOfView(FoV * QuaternionHelper.Deg2Rad2, Aspect, NearClip, FarClip);
		ViewMatrix = Matrix4.LookAt(transform.position, transform.position + transform.forward, transform.up);
		ViewProjectionMatrix = ViewMatrix * ProjectionMatrix;
	}

	/// <summary>
	///     The cameras this camera renders, in order: itself, then (a base camera) the overlay cameras of its stack
	///     that render: enabled on an active object, overlay cameras, each once.
	/// </summary>
	public void GetRenderStack(List<Camera> cameras)
	{
		cameras.Clear();
		cameras.Add(this);
		if (RenderType != CameraRenderType.Base || Stack == null)
		{
			return;
		}

		foreach (Camera overlay in Stack)
		{
			if (RendersInStack(overlay) && !cameras.Contains(overlay))
			{
				cameras.Add(overlay);
			}
		}
	}

	/// <summary>The camera of this base camera's stack that lights the world: <see cref="LightingCamera" /> when it renders in the stack, otherwise this camera.</summary>
	public Camera GetStackLightingCamera()
	{
		Camera chosen = LightingCamera;
		if (chosen == null || chosen == this || RenderType != CameraRenderType.Base || Stack == null || !RendersInStack(chosen) || !Stack.Contains(chosen))
		{
			return this;
		}

		return chosen;
	}

	/// <summary>The camera draws as part of a stack: an overlay camera, enabled on an active object.</summary>
	public static bool RendersInStack(Camera camera)
	{
		return camera != null && !camera.dead && camera.isActiveAndEnabled && camera.RenderType == CameraRenderType.Overlay;
	}

	public void SetViewUniform(ShaderProgram shader)
	{
		shader.SetMatrix4("viewMatrix", ViewMatrix);
	}

	public void SetProjectionUniform(ShaderProgram shader)
	{
		shader.SetMatrix4("projectionMatrix", ProjectionMatrix);
	}

	public void SetUniforms(ShaderProgram shader)
	{
		shader.SetMatrix4("projectionMatrix", ProjectionMatrix);
		shader.SetMatrix4("viewMatrix", ViewMatrix);
	}

	#endregion
}
