using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Frustum culling of one view on the CPU: a camera of the frame (<see cref="RenderContext.Culling" />), a shadow
	///     map's tile. Everything the passes draw one by one asks it before it draws, so what the view cannot see costs no
	///     draw call, no uniforms and no upload: mesh renderers outside the GPU-driven tables (transparent materials,
	///     shader values of their own, shaders without an instanced variant), the parts of static batches, trails and
	///     custom renderers. The GPU-driven instances are culled by the GPU with the same test (Engine/InstanceCull.shader),
	///     particle systems and volumetric objects by their own bounds.
	///     A mesh renderer is tested once per view whatever the number of its sub-meshes and of the passes that ask (the
	///     answer is stamped on it), and its world bounds are worked out once per rendered frame whatever the number of
	///     views (the cameras of a stack, the shadow maps' tiles).
	///     Only what a view draws is culled by it: a renderer the camera does not see still casts its shadow into the
	///     picture (the shadow maps cull with their own views) and stays in the global illumination.
	/// </summary>
	public sealed class ViewCulling
	{
		#region PublicFields

		/// <summary>The view's frustum (world space).</summary>
		public Frustum Frustum { get; private set; }

		/// <summary>Off: everything passes (<see cref="RenderSettings.FrustumCulling" />).</summary>
		public bool Enabled { get; private set; }

		/// <summary>What the view left out since <see cref="Begin" /> (a mesh renderer counts once).</summary>
		public int Culled { get; private set; }

		#endregion

		#region PrivateFields

		// One value per view (Begin): a renderer stamped with it has its answer for this view. Never 0, a renderer's own
		// value before its first test.
		private static int lastStamp;

		// One value per rendered frame (NewFrame): a renderer stamped with it has its world bounds worked out.
		private static int epoch = 1;

		private int stamp = NextStamp();

		#endregion

		#region PublicMethods

		/// <summary>Starts a view: its frustum from its view-projection matrix; every renderer is asked anew.</summary>
		public void Begin(Matrix4 viewProjection, bool enabled = true)
		{
			Frustum = new Frustum(viewProjection);
			Enabled = enabled;
			Culled = 0;
			stamp = NextStamp();
		}

		/// <summary>The view sees the renderer's mesh (its bounds reach into the frustum).</summary>
		public bool IsVisible(MeshRenderer renderer)
		{
			if (renderer.cullStamp == stamp)
			{
				return renderer.cullVisible;
			}

			if (Enabled && renderer.boundsEpoch != epoch)
			{
				SetBounds(renderer, renderer.transform.LocalToWorld);
			}

			return Test(renderer);
		}

		/// <summary><see cref="IsVisible(MeshRenderer)" /> for a caller that has the renderer's world matrix at hand.</summary>
		public bool IsVisible(MeshRenderer renderer, in Matrix4 localToWorld)
		{
			if (renderer.cullStamp == stamp)
			{
				return renderer.cullVisible;
			}

			if (Enabled && renderer.boundsEpoch != epoch)
			{
				SetBounds(renderer, localToWorld);
			}

			return Test(renderer);
		}

		/// <summary>The view sees a box of an object's space under the object's world matrix (a custom renderer's bounds, a static batch's part).</summary>
		public bool IsVisible(in Matrix4 localToWorld, Vector3 localMin, Vector3 localMax)
		{
			if (!Enabled || Frustum.Intersects(localToWorld, localMin, localMax))
			{
				return true;
			}

			Culled++;
			return false;
		}

		/// <summary>The view sees a world-space box (a trail's bounds).</summary>
		public bool IsVisible(Vector3 worldMin, Vector3 worldMax)
		{
			if (!Enabled || Frustum.Intersects(worldMin, worldMax))
			{
				return true;
			}

			Culled++;
			return false;
		}

		#endregion

		#region InternalMethods

		/// <summary>A frame starts rendering: objects may have moved since the last one, the world bounds are worked out again.</summary>
		internal static void NewFrame()
		{
			unchecked
			{
				epoch++;
			}

			if (epoch == 0)
			{
				epoch = 1;
			}
		}

		#endregion

		#region PrivateMethods

		private static int NextStamp()
		{
			unchecked
			{
				lastStamp++;
			}

			if (lastStamp == 0)
			{
				lastStamp = 1;
			}

			return lastStamp;
		}

		private static void SetBounds(MeshRenderer renderer, in Matrix4 localToWorld)
		{
			Mesh mesh = renderer.Mesh;
			Frustum.WorldBox(localToWorld, mesh != null ? mesh.BoundsMin : Vector3.Zero, mesh != null ? mesh.BoundsMax : Vector3.Zero, out renderer.worldCenter, out renderer.worldExtent);
			renderer.boundsEpoch = epoch;
		}

		private bool Test(MeshRenderer renderer)
		{
			bool visible = !Enabled || Frustum.IntersectsBox(renderer.worldCenter, renderer.worldExtent);
			if (!visible)
			{
				Culled++;
			}

			renderer.cullStamp = stamp;
			renderer.cullVisible = visible;
			return visible;
		}

		#endregion
	}
}
