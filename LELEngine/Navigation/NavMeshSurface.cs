using System;
using System.Collections.Generic;
using LELEngine.Navigation.Builder;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>Which objects a surface bakes.</summary>
	public enum CollectObjects
	{
		/// <summary>Every renderer of the scene.</summary>
		All,

		/// <summary>Renderers inside the volume (<see cref="NavMeshSurface.center" /> / <see cref="NavMeshSurface.size" />, local space).</summary>
		Volume,

		/// <summary>Renderers on this object and its children.</summary>
		Children
	}

	/// <summary>Components that add geometry to a surface's bake (custom shapes, procedural floors).</summary>
	public interface INavMeshBuildSource
	{
		/// <summary>Appends sources in the surface's space (use <paramref name="surface" />.WorldToSurface).</summary>
		void CollectNavMeshSources(NavMeshSurface surface, List<NavMeshBuildSource> sources);
	}

	/// <summary>
	///     A baked navmesh placed in the scene (Unity's NavMeshSurface). The navmesh is baked and stored in the
	///     object's local space, so the object can be moved and rotated; touching surfaces connect to each other
	///     automatically (edges that coincide within a cell size). The data lives in this component and is saved
	///     with the scene. Subclass to change what is collected (<see cref="CollectSources" />).
	/// </summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.Navigation)]
	public class NavMeshSurface : Behaviour
	{
		#region PublicFields

		[Tooltip("Agents with this type id walk on this surface.")]
		public int agentTypeId;

		public CollectObjects collectObjects = CollectObjects.All;

		[Tooltip("Volume (local space) baked when Collect Objects is Volume.")]
		public Vector3 center;

		public Vector3 size = new Vector3(50f, 20f, 50f);

		[Tooltip("Area of the geometry without a NavMeshModifier.")]
		public int defaultArea = NavMeshArea.Walkable;

		[Tooltip("Ignore NavMeshModifier components while collecting.")]
		public bool ignoreModifiers;

		public NavMeshBuildSettings buildSettings = new NavMeshBuildSettings();

		[SerializeField] [HideInInspector] private NavMeshData navMeshData;

		/// <summary>Baked data (null before the first bake).</summary>
		public NavMeshData NavMeshData => navMeshData;

		/// <summary>Registered with the navigation world (enabled, with data).</summary>
		public bool IsRegistered => instance != null;

		/// <summary>Raised after a bake or a data change.</summary>
		public event Action<NavMeshSurface> DataChanged;

		/// <summary>
		///     The surface's space: the object's position and rotation without its scale (as Unity's AddNavMeshData),
		///     so the agent settings stay in world units whatever the object's scale.
		/// </summary>
		public Matrix4 SurfaceToWorld => Matrix4.CreateFromQuaternion(transform.rotation) * Matrix4.CreateTranslation(transform.position);

		public Matrix4 WorldToSurface => SurfaceToWorld.Inverted();

		#endregion

		#region PrivateFields

		private NavSurface instance;
		private Matrix4 registeredMatrix;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			NavMesh.RegisterSurface(this);
			AddData();
		}

		public override void OnDisable()
		{
			RemoveData();
			NavMesh.UnregisterSurface(this);
		}

		protected internal override void ReleaseResources()
		{
			RemoveData();
			NavMesh.UnregisterSurface(this);
		}

		/// <summary>Fields changed (inspector, undo): the registered navmesh follows the data and agent type.</summary>
		public override void OnValidate()
		{
			if (instance != null && (instance.Data != navMeshData || instance.AgentTypeId != agentTypeId))
			{
				RemoveData();
				AddData();
				DataChanged?.Invoke(this);
			}
			else if (instance == null && isActiveAndEnabled)
			{
				AddData();
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Bakes the navmesh from the scene's geometry now (blocking; tiles build in parallel).</summary>
		public void BuildNavMesh()
		{
			var sources = new List<NavMeshBuildSource>();
			var volumes = new List<NavMeshAreaVolume>();
			CollectSources(sources, volumes);
			if (!ComputeBounds(sources, out Vector3 min, out Vector3 max))
			{
				Debug.LogWarning("[NavMesh] Nothing to bake on " + gameObject?.Name, this);
				navMeshData = new NavMeshData { Settings = buildSettings.Clone() };
			}
			else
			{
				navMeshData = NavMeshBuilder.Build(buildSettings, sources, volumes, min, max, message => Debug.Log("[NavMesh] " + message, this));
			}

			RemoveData();
			AddData();
			DataChanged?.Invoke(this);
		}

		/// <summary>Drops the baked data.</summary>
		public void RemoveNavMeshData()
		{
			RemoveData();
			navMeshData = null;
			DataChanged?.Invoke(this);
		}

		/// <summary>Registers the baked data with the navigation world (done on enable).</summary>
		public void AddData()
		{
			if (instance != null || navMeshData == null || navMeshData.IsEmpty || gameObject == null)
			{
				return;
			}

			registeredMatrix = SurfaceToWorld;
			instance = NavMesh.World.AddSurface(this, navMeshData, agentTypeId, registeredMatrix);
			NavMesh.Carver.MarkSurface(instance);
		}

		public void RemoveData()
		{
			if (instance == null)
			{
				return;
			}

			NavMesh.World.RemoveSurface(instance);
			instance = null;
		}

		/// <summary>Geometry of the bake: renderers chosen by <see cref="collectObjects" /> plus <see cref="INavMeshBuildSource" /> components.</summary>
		public virtual void CollectSources(List<NavMeshBuildSource> sources, List<NavMeshAreaVolume> volumes)
		{
			Scene scene = gameObject?.scene;
			if (scene == null)
			{
				return;
			}

			Matrix4 worldToLocal = WorldToSurface;
			IEnumerable<GameObject> objects = collectObjects == CollectObjects.Children ? Descendants(gameObject) : scene.GameObjects;
			foreach (GameObject go in objects)
			{
				if (!go.activeInHierarchy || (go.hideFlags & HideFlags.DontSave) != 0)
				{
					continue;
				}

				MeshRenderer renderer = go.GetComponent<MeshRenderer>();
				if (renderer != null && renderer.enabled && renderer.Mesh != null && !IsAgentOrObstacle(go) && GetArea(go, out int area))
				{
					Matrix4 toLocal = renderer.transform.LocalToWorld * worldToLocal;
					if (collectObjects == CollectObjects.Volume && !InVolume(renderer, toLocal))
					{
						continue;
					}

					sources.Add(new NavMeshBuildSource { Vertices = MeshPositions(renderer.Mesh), Indices = null, Transform = toLocal, Area = area, Owner = go });
				}

				foreach (Behaviour component in go.Components)
				{
					if (component is INavMeshBuildSource custom && component.enabled)
					{
						custom.CollectNavMeshSources(this, sources);
					}

					if (!ignoreModifiers && component is NavMeshModifierVolume volume && component.enabled)
					{
						volumes.Add(volume.ToLocalVolume(worldToLocal));
					}
				}
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>The object moved: the registered navmesh follows.</summary>
		internal void SyncTransform()
		{
			if (instance == null || gameObject == null)
			{
				return;
			}

			Matrix4 matrix = SurfaceToWorld;
			if (matrix != registeredMatrix)
			{
				registeredMatrix = matrix;
				NavMesh.World.UpdateTransform(instance, matrix);
			}
		}

		internal NavSurface Instance => instance;

		#endregion

		#region PrivateMethods

		private bool ComputeBounds(List<NavMeshBuildSource> sources, out Vector3 min, out Vector3 max)
		{
			if (collectObjects == CollectObjects.Volume)
			{
				min = center - size * 0.5f;
				max = center + size * 0.5f;
				return sources.Count > 0;
			}

			min = new Vector3(float.MaxValue);
			max = new Vector3(float.MinValue);
			bool any = false;
			foreach (NavMeshBuildSource source in sources)
			{
				foreach (Vector3 v in source.Vertices)
				{
					Vector3 p = Vector3.TransformPosition(v, source.Transform);
					min = Vector3.ComponentMin(min, p);
					max = Vector3.ComponentMax(max, p);
					any = true;
				}
			}

			if (any)
			{
				float pad = buildSettings.CellSize * 2f;
				min -= new Vector3(pad);
				max += new Vector3(pad);
			}

			return any;
		}

		private bool InVolume(MeshRenderer renderer, in Matrix4 toLocal)
		{
			Vector3 bmin = renderer.Mesh.BoundsMin;
			Vector3 bmax = renderer.Mesh.BoundsMax;
			Vector3 lmin = new Vector3(float.MaxValue);
			Vector3 lmax = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3((i & 1) != 0 ? bmax.X : bmin.X, (i & 2) != 0 ? bmax.Y : bmin.Y, (i & 4) != 0 ? bmax.Z : bmin.Z);
				Vector3 local = Vector3.TransformPosition(corner, toLocal);
				lmin = Vector3.ComponentMin(lmin, local);
				lmax = Vector3.ComponentMax(lmax, local);
			}

			return NavMath.OverlapBounds(lmin, lmax, center - size * 0.5f, center + size * 0.5f);
		}

		private bool GetArea(GameObject go, out int area)
		{
			area = defaultArea;
			if (ignoreModifiers)
			{
				return true;
			}

			for (Transform t = go.transform; t != null; t = t.parent)
			{
				NavMeshModifier modifier = t.gameObject.GetComponent<NavMeshModifier>();
				if (modifier == null || !modifier.enabled || (t != go.transform && !modifier.applyToChildren))
				{
					continue;
				}

				if (!modifier.AffectsAgentType(agentTypeId))
				{
					continue;
				}

				if (modifier.ignoreFromBuild)
				{
					return false;
				}

				if (modifier.overrideArea)
				{
					area = modifier.area;
				}

				return true;
			}

			return true;
		}

		private static bool IsAgentOrObstacle(GameObject go)
		{
			foreach (Behaviour component in go.Components)
			{
				if (component is NavMeshAgent || component is NavMeshObstacle)
				{
					return true;
				}
			}

			return false;
		}

		private static IEnumerable<GameObject> Descendants(GameObject root)
		{
			yield return root;
			foreach (Transform child in root.transform.Children)
			{
				foreach (GameObject descendant in Descendants(child.gameObject))
				{
					yield return descendant;
				}
			}
		}

		private static readonly Dictionary<Mesh, Vector3[]> positionCache = new Dictionary<Mesh, Vector3[]>();

		/// <summary>Drops cached mesh data (the assets were released).</summary>
		public static void ClearCaches()
		{
			lock (positionCache)
			{
				positionCache.Clear();
			}
		}

		/// <summary>Triangle vertices of a mesh (three per triangle).</summary>
		private static Vector3[] MeshPositions(Mesh mesh)
		{
			lock (positionCache)
			{
				if (positionCache.TryGetValue(mesh, out Vector3[] cached))
				{
					return cached;
				}

				Vector3[] positions = mesh.GetTrianglePositions();
				positionCache[mesh] = positions;
				return positions;
			}
		}

		#endregion
	}

	/// <summary>Changes how an object (and optionally its children) is baked: another area, or left out.</summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.Navigation)]
	public sealed class NavMeshModifier : Behaviour
	{
		#region PublicFields

		[Tooltip("Leave this object out of the bake.")]
		public bool ignoreFromBuild;

		[Tooltip("Bake this object with the area below instead of the surface's default.")]
		public bool overrideArea;

		public int area = NavMeshArea.Walkable;

		[Tooltip("The modifier also applies to the children of this object.")]
		public bool applyToChildren = true;

		[Tooltip("Agent type the modifier affects; -1 for all.")]
		public int affectedAgentTypeId = -1;

		#endregion

		#region PublicMethods

		public bool AffectsAgentType(int agentTypeId)
		{
			return affectedAgentTypeId < 0 || affectedAgentTypeId == agentTypeId;
		}

		#endregion
	}

	/// <summary>A box volume whose walkable cells get an area (water, mud, danger zones), yaw-aligned with the object.</summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.Navigation)]
	public sealed class NavMeshModifierVolume : Behaviour
	{
		#region PublicFields

		public Vector3 center;
		public Vector3 size = Vector3.One;
		public int area = NavMeshArea.Walkable;

		#endregion

		#region InternalMethods

		internal NavMeshAreaVolume ToLocalVolume(in Matrix4 worldToLocal)
		{
			Matrix4 m = transform.LocalToWorld * worldToLocal;
			Vector3 half = size * 0.5f;
			var polygon = new Vector2[4];
			float minY = float.MaxValue;
			float maxY = float.MinValue;
			Vector3[] corners =
			{
				center + new Vector3(-half.X, 0f, -half.Z), center + new Vector3(half.X, 0f, -half.Z),
				center + new Vector3(half.X, 0f, half.Z), center + new Vector3(-half.X, 0f, half.Z)
			};
			for (int i = 0; i < 4; i++)
			{
				Vector3 p = Vector3.TransformPosition(corners[i], m);
				polygon[i] = new Vector2(p.X, p.Z);
			}

			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = center + new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
				float y = Vector3.TransformPosition(corner, m).Y;
				minY = Math.Min(minY, y);
				maxY = Math.Max(maxY, y);
			}

			return new NavMeshAreaVolume { Polygon = polygon, MinY = minY, MaxY = maxY, Area = area };
		}

		#endregion
	}
}
