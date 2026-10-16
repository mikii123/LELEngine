using System;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>The world plane the 2D physics simulates in.</summary>
	public enum SimulationPlane2D
	{
		/// <summary>The XY plane: 2D x is world X, 2D y is world Y, bodies turn about Z (side views).</summary>
		XY,

		/// <summary>
		///     The XZ plane: 2D x is world X, 2D y is world Z (top-down views). The XY plane turned 90° about X, so a
		///     positive 2D rotation turns about -Y (a negative Y rotation of the transform).
		/// </summary>
		XZ
	}

	/// <summary>
	///     2D physics of the project (see <see cref="LELEngine.Physics2D" />): the simulation plane, gravity, solver
	///     sub-steps and worker threads, query options, default surface and the layer collision matrix. Part of
	///     <see cref="ProjectSettings" />.
	/// </summary>
	[Serializable]
	public sealed class Physics2DSettings
	{
		#region PublicFields

		/// <summary>World plane of the simulation; applies when a scene's physics world is created (the editor's Project Settings draw it).</summary>
		[HideInInspector] public SimulationPlane2D SimulationPlane = SimulationPlane2D.XY;

		/// <summary>Gravity in the simulation plane (2D x and y).</summary>
		public Vector2 Gravity = new Vector2(0f, -9.81f);

		/// <summary>Solver sub-steps per physics step: more is stiffer and more accurate (stacks, chains), and costs more.</summary>
		[Range(1, 16)] public int SubStepCount = 4;

		/// <summary>
		///     Threads Box2D steps on: 0 = automatic (half the logical processors, at most 8), 1 = the stepping thread only.
		///     Applies when a scene's physics world is created.
		/// </summary>
		[Range(0, 16)] public int WorkerCount;

		/// <summary>Queries (raycasts, overlaps) report trigger colliders.</summary>
		public bool QueriesHitTriggers = true;

		/// <summary>Raycasts and casts starting inside a collider report it (at distance 0).</summary>
		public bool QueriesStartInColliders = true;

		/// <summary>Friction and bounciness of colliders without a material.</summary>
		[Range(0f, 1f)] public float DefaultFriction = 0.4f;

		[Range(0f, 1f)] public float DefaultBounciness;

		/// <summary>Per layer: the mask of layers it collides with (bit n = layer n); all by default.</summary>
		[HideInInspector] public int[] LayerCollisionMatrix = DefaultMatrix();

		#endregion

		#region PublicMethods

		public bool GetIgnoreLayerCollision(int layer1, int layer2)
		{
			EnsureMatrix();
			return (LayerCollisionMatrix[layer1 & 31] & (1 << (layer2 & 31))) == 0;
		}

		public void IgnoreLayerCollision(int layer1, int layer2, bool ignore)
		{
			EnsureMatrix();
			int a = layer1 & 31;
			int b = layer2 & 31;
			if (ignore)
			{
				LayerCollisionMatrix[a] &= ~(1 << b);
				LayerCollisionMatrix[b] &= ~(1 << a);
			}
			else
			{
				LayerCollisionMatrix[a] |= 1 << b;
				LayerCollisionMatrix[b] |= 1 << a;
			}
		}

		/// <summary>The mask of layers a layer collides with.</summary>
		public int GetLayerCollisionMask(int layer)
		{
			EnsureMatrix();
			return LayerCollisionMatrix[layer & 31];
		}

		#endregion

		#region PrivateMethods

		private static int[] DefaultMatrix()
		{
			var matrix = new int[32];
			for (int i = 0; i < 32; i++)
			{
				matrix[i] = -1;
			}

			return matrix;
		}

		private void EnsureMatrix()
		{
			if (LayerCollisionMatrix == null || LayerCollisionMatrix.Length != 32)
			{
				LayerCollisionMatrix = DefaultMatrix();
			}
		}

		#endregion
	}
}
