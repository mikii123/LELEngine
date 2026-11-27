using System;
using System.Collections.Generic;
using System.Diagnostics;
using LELEngine.Mathematics;
using LELEngine.Navigation.Builder;

namespace LELEngine.Navigation
{
	/// <summary>What a scene's navigation bake reads as the ground and the obstacles.</summary>
	public enum NavMeshGeometry
	{
		/// <summary>The scene's renderers: walkable where their triangles face up (Unity's Render Meshes).</summary>
		RenderMeshes,

		/// <summary>
		///     The 2D physics: the simulation plane is walkable within the baked bounds and the 2D colliders that do not move
		///     (no rigidbody, or a static one) stand on it as obstacles. Nothing has to be made to walk on.
		/// </summary>
		PhysicsColliders
	}

	/// <summary>
	///     A scene's navigation, saved with the scene (Lighting's counterpart, edited in Window > Navigation): what its bake
	///     reads, where, for which agent types, and the navmeshes baked (an asset next to the scene). No object holds them:
	///     they are part of the world while the scene is loaded, in edit mode too.
	/// </summary>
	[Serializable]
	public sealed class SceneNavigationSettings
	{
		#region PublicFields

		[Tooltip("Render Meshes: walkable where the renderers' triangles face up. Physics Colliders: the 2D simulation plane is walkable, the 2D colliders that do not move are obstacles on it.")]
		public NavMeshGeometry Geometry = NavMeshGeometry.RenderMeshes;

		[Tooltip("Only objects on these layers are baked (their renderers or their colliders).")]
		public LayerMask IncludeLayers = LayerMask.Everything;

		[Tooltip("The agent types baked (ids of Project Settings > Navigation); empty: every agent type of the project.")]
		public List<int> AgentTypes = new List<int>();

		[Tooltip("Bake around what the scene holds (its geometry, colliders and bake obstacles), widened by Padding. Off: the box of Center and Size.")]
		public bool AutoBounds = true;

		[Tooltip("How far the baked area reaches past what the scene holds (world units; Auto Bounds).")]
		public float Padding = 10f;

		[Tooltip("Middle of the baked box (world; Physics Colliders: in the simulation plane, X and Z the plane's two axes).")]
		public Vector3 Center;

		[Tooltip("Size of the baked box (Physics Colliders: X and Z across the plane; the height is not read).")]
		public Vector3 Size = new Vector3(200f, 20f, 200f);

		[Tooltip("Area of the walkable ground (Physics Colliders: the plane; Render Meshes: renderers without a NavMesh Modifier).")]
		public int DefaultArea = NavMeshArea.Walkable;

		[Tooltip("The baked navmeshes, one per agent type (written by Bake into the scene's folder).")]
		public SceneNavMeshAsset NavMesh;

		#endregion
	}

	/// <summary>A scene's baked navmeshes, one per agent type (an asset next to the scene: "Scene/NavMesh.asset").</summary>
	public sealed class SceneNavMeshAsset : ScriptableObject
	{
		#region PublicFields

		[HideInInspector] public List<SceneNavMesh> NavMeshes = new List<SceneNavMesh>();

		#endregion

		#region PublicMethods

		/// <summary>The navmesh of an agent type, or null.</summary>
		public SceneNavMesh Find(int agentTypeId)
		{
			foreach (SceneNavMesh navMesh in NavMeshes)
			{
				if (navMesh != null && navMesh.AgentTypeId == agentTypeId)
				{
					return navMesh;
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>The navmesh of one agent type: its data in the surface's space and that space's turn in the world.</summary>
	[Serializable]
	public sealed class SceneNavMesh
	{
		#region PublicFields

		public int AgentTypeId;

		/// <summary>The surface's space in the world (no translation): identity for Render Meshes, the simulation plane's frame for Physics Colliders.</summary>
		public Quaternion Rotation = Quaternion.Identity;

		[HideInInspector] public NavMeshData Data;

		#endregion
	}

	/// <summary>What one agent type's bake made.</summary>
	public struct SceneNavMeshBakeResult
	{
		public int AgentTypeId;
		public string AgentName;
		public NavMeshData Data;
		public int Obstacles;
		public double Milliseconds;

		/// <summary>Why nothing was made (no geometry, an unknown agent type), else null.</summary>
		public string Problem;
	}

	/// <summary>
	///     A scene's navigation: bakes its navmeshes from what <see cref="SceneNavigationSettings" /> says, and keeps the
	///     baked ones part of the navigation world while the scene is loaded (<see cref="Sync" />; agents and queries find
	///     them by agent type as any surface's).
	///     <para>
	///         Render Meshes collect the renderers as a <see cref="NavMeshSurface" /> does. Physics Colliders make the
	///         ground themselves: the simulation plane within the bounds; every enabled, non-trigger 2D collider on an
	///         included layer whose object has no rigidbody or a static one is an obstacle on it (dynamic and kinematic
	///         bodies move: they are left out, as are NavMesh Agents and NavMesh Obstacles). A closed chain of edges that
	///         runs counter-clockwise (an outline facing out) is filled; open chains and clockwise loops (walls that keep
	///         things in) are walls. A <see cref="NavMeshModifier" /> leaves an object out (Ignore From Build) or overrides
	///         its colliders' area: other than Not Walkable they mark an area instead of cutting; a trigger counts only with
	///         an override (Not Walkable: a zone cut out, such as a landing zone agents must not wander into). Every obstacle is
	///         widened to cover each voxel it touches, then the walkable area keeps the agent's radius off it.
	///         <see cref="NavMeshBakeObstacle" />s are obstacles the bake alone sees, <see cref="NavMeshModifierVolume" />s
	///         areas, in both modes.
	///     </para>
	/// </summary>
	public static class SceneNavigation
	{
		#region PrivateFields

		private static readonly Dictionary<Scene, List<(SceneNavMesh entry, NavMeshData data, NavSurface surface)>> registered =
			new Dictionary<Scene, List<(SceneNavMesh, NavMeshData, NavSurface)>>();

		#endregion

		#region PublicMethods

		/// <summary>The agent types a scene's settings bake (every agent type of the project when its list is empty).</summary>
		public static List<NavMeshAgentType> AgentTypesOf(SceneNavigationSettings settings)
		{
			var result = new List<NavMeshAgentType>();
			NavigationSettings project = ProjectSettings.Current.Navigation ?? new NavigationSettings();
			foreach (NavMeshAgentType type in project.AgentTypes)
			{
				if (type != null && (settings.AgentTypes == null || settings.AgentTypes.Count == 0 || settings.AgentTypes.Contains(type.Id)))
				{
					result.Add(type);
				}
			}

			return result;
		}

		/// <summary>
		///     Bakes the navmesh of one agent type from the scene as it is now (blocking; tiles build in parallel). The data
		///     is in the surface's space, <paramref name="rotation" /> that space's turn in the world.
		/// </summary>
		public static SceneNavMeshBakeResult Build(Scene scene, SceneNavigationSettings settings, NavMeshAgentType agentType, out Quaternion rotation)
		{
			long start = Stopwatch.GetTimestamp();
			var result = new SceneNavMeshBakeResult { AgentTypeId = agentType.Id, AgentName = agentType.Name };
			bool plane = settings.Geometry == NavMeshGeometry.PhysicsColliders;
			rotation = plane ? PlaneRotation() : Quaternion.Identity;
			Matrix4 worldToSurface = Matrix4.CreateFromQuaternion(rotation).Inverted();
			NavMeshBuildSettings build = agentType.Build ?? new NavMeshBuildSettings();

			var sources = new List<NavMeshBuildSource>();
			var volumes = new List<NavMeshAreaVolume>();
			Vector3 min = new Vector3(float.MaxValue);
			Vector3 max = new Vector3(float.MinValue);
			if (plane)
			{
				CollectColliders(scene, settings, agentType.Id, build, worldToSurface, volumes, ref min, ref max);
			}
			else
			{
				CollectRenderers(scene, settings, agentType.Id, worldToSurface, sources, ref min, ref max);
			}

			CollectBakeObstacles(scene, settings, agentType.Id, build, worldToSurface, plane, volumes, ref min, ref max);
			CollectModifierVolumes(scene, worldToSurface, volumes);
			foreach (NavMeshAreaVolume volume in volumes)
			{
				if (volume.Obstacle)
				{
					result.Obstacles++;
				}
			}

			if (!settings.AutoBounds)
			{
				Vector3 half = settings.Size * 0.5f;
				min = settings.Center - half;
				max = settings.Center + half;
			}
			else if (min.X <= max.X)
			{
				Vector3 pad = new Vector3(Math.Max(0f, settings.Padding), 0f, Math.Max(0f, settings.Padding));
				min -= pad;
				max += pad;
			}

			if (plane)
			{
				min.Y = 0f;
				max.Y = 0f;
			}

			if (!(min.X < max.X && min.Z < max.Z) || (!plane && sources.Count == 0))
			{
				result.Problem = plane ? "no colliders or bake obstacles to bake around (set the bounds by hand: Auto Bounds off)" : "no geometry to bake";
				result.Data = new NavMeshData { Settings = build.Clone() };
				result.Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
				return result;
			}

			if (plane)
			{
				// The ground: the plane over the whole box (in the surface's space it is y = 0).
				sources.Add(new NavMeshBuildSource
				{
					Vertices = new[]
					{
						new Vector3(min.X, 0f, min.Z), new Vector3(min.X, 0f, max.Z), new Vector3(max.X, 0f, max.Z),
						new Vector3(min.X, 0f, min.Z), new Vector3(max.X, 0f, max.Z), new Vector3(max.X, 0f, min.Z)
					},
					Transform = Matrix4.Identity,
					Area = settings.DefaultArea
				});
			}

			string log = null;
			result.Data = NavMeshBuilder.Build(build, sources, volumes, min, max, message => log = message);
			if (result.Data.IsEmpty)
			{
				result.Problem = log ?? "nothing walkable";
			}

			result.Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			return result;
		}

		/// <summary>
		///     Keeps the scene's baked navmeshes registered with the navigation world as its settings say (after a bake, an
		///     undo, the asset read again). Cheap when nothing changed; the scene's start and every navigation step call it.
		/// </summary>
		public static void Sync(Scene scene)
		{
			if (scene == null || !scene.LifecycleStarted)
			{
				return;
			}

			SceneNavMeshAsset asset = scene.Settings?.Navigation?.NavMesh;
			registered.TryGetValue(scene, out List<(SceneNavMesh entry, NavMeshData data, NavSurface surface)> current);
			if (Matches(asset, current))
			{
				return;
			}

			Unregister(scene);
			if (asset == null)
			{
				return;
			}

			var surfaces = new List<(SceneNavMesh, NavMeshData, NavSurface)>();
			foreach (SceneNavMesh entry in asset.NavMeshes)
			{
				if (entry?.Data == null || entry.Data.IsEmpty)
				{
					surfaces.Add((entry, entry?.Data, null));
					continue;
				}

				NavSurface surface = NavMesh.World.AddSurface(null, entry.Data, entry.AgentTypeId, Matrix4.CreateFromQuaternion(entry.Rotation));
				NavMesh.Carver.MarkSurface(surface);
				surfaces.Add((entry, entry.Data, surface));
			}

			registered[scene] = surfaces;
		}

		/// <summary>Takes the scene's navmeshes out of the navigation world (the scene unloads).</summary>
		public static void Unregister(Scene scene)
		{
			if (scene == null || !registered.TryGetValue(scene, out List<(SceneNavMesh entry, NavMeshData data, NavSurface surface)> surfaces))
			{
				return;
			}

			foreach ((SceneNavMesh _, NavMeshData _, NavSurface surface) in surfaces)
			{
				if (surface != null)
				{
					NavMesh.World.RemoveSurface(surface);
				}
			}

			registered.Remove(scene);
		}

		#endregion

		#region InternalMethods

		/// <summary>The simulation plane's frame: x along its first axis, z along its second, y off it (identity for XZ).</summary>
		internal static Quaternion PlaneRotation()
		{
			PlaneBasis basis = PlaneBasis.Current;
			Vector3 up = Vector3.Cross(basis.V, basis.U);
			// Rows of the surface-to-world matrix (row vectors): local x, y, z in the world.
			var matrix = new Matrix3(basis.U, up, basis.V);
			Quaternion rotation = Quaternion.FromMatrix(matrix).Normalized();
			// Whichever way round the conversion reads the rows, local x must land on the plane's first axis.
			return (rotation * Vector3.UnitX - basis.U).LengthSquared < 1e-4f && (rotation * Vector3.UnitZ - basis.V).LengthSquared < 1e-4f
				? rotation
				: Quaternion.Conjugate(rotation);
		}

		#endregion

		#region PrivateMethods

		private static bool Matches(SceneNavMeshAsset asset, List<(SceneNavMesh entry, NavMeshData data, NavSurface surface)> current)
		{
			if (current == null)
			{
				return asset == null;
			}

			if (asset == null || asset.NavMeshes.Count != current.Count)
			{
				return false;
			}

			for (int i = 0; i < current.Count; i++)
			{
				SceneNavMesh entry = asset.NavMeshes[i];
				if (!ReferenceEquals(entry, current[i].entry) || !ReferenceEquals(entry?.Data, current[i].data))
				{
					return false;
				}
			}

			return true;
		}

		private static bool Included(GameObject go, SceneNavigationSettings settings)
		{
			return go != null && go.activeInHierarchy && (go.hideFlags & HideFlags.DontSave) == 0 && (settings.IncludeLayers.value & (1 << go.layer)) != 0;
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

		/// <summary>The nearest NavMesh Modifier that applies to an object for an agent type (Unity's rule), or null.</summary>
		private static NavMeshModifier ModifierOf(GameObject go, int agentTypeId)
		{
			for (Transform t = go.transform; t != null; t = t.parent)
			{
				NavMeshModifier modifier = t.gameObject.GetComponent<NavMeshModifier>();
				if (modifier == null || !modifier.enabled || (t != go.transform && !modifier.applyToChildren) || !modifier.AffectsAgentType(agentTypeId))
				{
					continue;
				}

				return modifier;
			}

			return null;
		}

		private static void CollectRenderers(Scene scene, SceneNavigationSettings settings, int agentTypeId, in Matrix4 worldToSurface, List<NavMeshBuildSource> sources, ref Vector3 min, ref Vector3 max)
		{
			foreach (GameObject go in scene.GameObjects)
			{
				if (!Included(go, settings))
				{
					continue;
				}

				MeshRenderer renderer = go.GetComponent<MeshRenderer>();
				if (renderer == null || !renderer.enabled || renderer.Mesh == null || IsAgentOrObstacle(go))
				{
					continue;
				}

				int area = settings.DefaultArea;
				NavMeshModifier modifier = ModifierOf(go, agentTypeId);
				if (modifier != null && modifier.ignoreFromBuild)
				{
					continue;
				}

				if (modifier != null && modifier.overrideArea)
				{
					area = modifier.area;
				}

				Matrix4 toSurface = renderer.transform.LocalToWorld * worldToSurface;
				Vector3[] vertices = renderer.Mesh.GetTrianglePositions();
				sources.Add(new NavMeshBuildSource { Vertices = vertices, Transform = toSurface, Area = area, Owner = go });
				foreach (Vector3 v in vertices)
				{
					Vector3 p = Vector3.TransformPosition(v, toSurface);
					min = Vector3.ComponentMin(min, p);
					max = Vector3.ComponentMax(max, p);
				}
			}
		}

		private static void CollectColliders(Scene scene, SceneNavigationSettings settings, int agentTypeId, NavMeshBuildSettings build, in Matrix4 worldToSurface, List<NavMeshAreaVolume> volumes, ref Vector3 min, ref Vector3 max)
		{
			float cell = build.CellSize;
			float margin = NavMeshShapes.CoverMargin(cell);
			float tolerance = cell * 0.25f;
			float depth = Math.Max(10f, build.AgentHeight * 4f);
			var shapes = new List<PhysicsShape2D>();
			var vertices = new List<Vector2>();
			Vector2 lo = new Vector2(min.X, min.Z);
			Vector2 hi = new Vector2(max.X, max.Z);
			foreach (GameObject go in scene.GameObjects)
			{
				if (!Included(go, settings) || IsAgentOrObstacle(go))
				{
					continue;
				}

				NavMeshModifier modifier = null;
				bool modifierLooked = false;
				foreach (Behaviour component in go.Components)
				{
					if (component is not Collider2D collider || !collider.enabled)
					{
						continue;
					}

					if (!modifierLooked)
					{
						modifier = ModifierOf(go, agentTypeId);
						modifierLooked = true;
					}

					if (modifier != null && modifier.ignoreFromBuild)
					{
						break;
					}

					// A trigger counts only when a modifier gives it an area: Not Walkable cuts it out (a zone agents keep out of),
					// any other marks it.
					bool overridden = modifier != null && modifier.overrideArea;
					bool areaOnly = overridden && modifier.area != NavMeshArea.NotWalkable;
					if ((collider.isTrigger && !overridden) || Moves(go))
					{
						continue;
					}

					shapes.Clear();
					vertices.Clear();
					collider.GetWorldShapes(shapes, vertices);
					int area = areaOnly ? modifier.area : NavMeshArea.NotWalkable;
					AddShapes(shapes, vertices, areaOnly ? 0f : margin, tolerance, depth, area, !areaOnly, volumes, ref lo, ref hi);
				}
			}

			min.X = lo.X;
			min.Z = lo.Y;
			max.X = hi.X;
			max.Z = hi.Y;
		}

		/// <summary>An object whose rigidbody moves it (dynamic or kinematic, simulated) is no obstacle for a bake.</summary>
		private static bool Moves(GameObject go)
		{
			Rigidbody2D body = go.GetComponentInParent<Rigidbody2D>();
			return body != null && body.enabled && body.simulated && body.bodyType != RigidbodyType2D.Static;
		}

		/// <summary>A collider's shapes (plane coordinates: the surface's x and z) as convex volumes.</summary>
		private static void AddShapes(List<PhysicsShape2D> shapes, List<Vector2> vertices, float margin, float tolerance, float depth, int area, bool obstacle, List<NavMeshAreaVolume> volumes, ref Vector2 lo, ref Vector2 hi)
		{
			var hullPoints = new List<Vector2>(8);
			for (int s = 0; s < shapes.Count; s++)
			{
				PhysicsShape2D shape = shapes[s];
				if (shape.shapeType == PhysicsShapeType2D.Edge || (shape.shapeType == PhysicsShapeType2D.Capsule && IsChained(shapes, vertices, s)))
				{
					// A run of joined segments (an edge collider's chain, or its thick segments).
					int end = s;
					while (end + 1 < shapes.Count && shapes[end + 1].shapeType == shape.shapeType && Joined(vertices, shapes[end], shapes[end + 1]))
					{
						end++;
					}

					AddRun(shapes, vertices, s, end, margin, tolerance, depth, area, obstacle, volumes, ref lo, ref hi);
					s = end;
					continue;
				}

				hullPoints.Clear();
				for (int v = 0; v < shape.vertexCount; v++)
				{
					hullPoints.Add(vertices[shape.vertexStartIndex + v]);
				}

				AddConvex(NavMeshShapes.ConvexHull(hullPoints), shape.radius + margin, tolerance, depth, area, obstacle, volumes, ref lo, ref hi);
			}
		}

		private static bool IsChained(List<PhysicsShape2D> shapes, List<Vector2> vertices, int index)
		{
			return (index + 1 < shapes.Count && shapes[index + 1].shapeType == PhysicsShapeType2D.Capsule && Joined(vertices, shapes[index], shapes[index + 1]))
			       || (index > 0 && shapes[index - 1].shapeType == PhysicsShapeType2D.Capsule && Joined(vertices, shapes[index - 1], shapes[index]));
		}

		private static bool Joined(List<Vector2> vertices, PhysicsShape2D a, PhysicsShape2D b)
		{
			return a.vertexCount == 2 && b.vertexCount == 2 && (vertices[a.vertexStartIndex + 1] - vertices[b.vertexStartIndex]).LengthSquared < 1e-6f;
		}

		/// <summary>
		///     Segments joined end to end: each a capsule as wide as the edge (at least a voxel's cover); a closed run that
		///     goes counter-clockwise is an outline facing out, and what it encloses is filled.
		/// </summary>
		private static void AddRun(List<PhysicsShape2D> shapes, List<Vector2> vertices, int first, int last, float margin, float tolerance, float depth, int area, bool obstacle, List<NavMeshAreaVolume> volumes, ref Vector2 lo, ref Vector2 hi)
		{
			var outline = new List<Vector2>(last - first + 2);
			for (int s = first; s <= last; s++)
			{
				PhysicsShape2D shape = shapes[s];
				Vector2 a = vertices[shape.vertexStartIndex];
				Vector2 b = vertices[shape.vertexStartIndex + 1];
				if (s == first)
				{
					outline.Add(a);
				}

				outline.Add(b);
				float width = shape.radius + (obstacle ? Math.Max(margin, 1e-3f) : margin);
				AddConvex(new List<Vector2> { a, b }, width, tolerance, depth, area, obstacle, volumes, ref lo, ref hi);
			}

			bool closed = outline.Count >= 4 && (outline[outline.Count - 1] - outline[0]).LengthSquared < 1e-6f;
			if (!closed)
			{
				return;
			}

			outline.RemoveAt(outline.Count - 1);
			if (NavMeshShapes.SignedArea2(outline) <= 0f)
			{
				return;
			}

			foreach (Vector2[] triangle in NavMeshShapes.Triangulate(outline))
			{
				AddConvex(new List<Vector2>(triangle), margin, tolerance, depth, area, obstacle, volumes, ref lo, ref hi);
			}
		}

		private static void AddConvex(List<Vector2> hull, float radius, float tolerance, float depth, int area, bool obstacle, List<NavMeshAreaVolume> volumes, ref Vector2 lo, ref Vector2 hi)
		{
			if (hull.Count >= 3)
			{
				// Counter-clockwise for the rounding (a triangle of a fill may come either way).
				if (NavMeshShapes.SignedArea2(hull) < 0f)
				{
					hull.Reverse();
				}
			}

			Vector2[] polygon = NavMeshShapes.Rounded(hull, radius, tolerance);
			if (polygon.Length < 3)
			{
				return;
			}

			NavMeshShapes.Extend(polygon, ref lo, ref hi);
			volumes.Add(new NavMeshAreaVolume { Polygon = polygon, MinY = -depth, MaxY = depth, Area = area, Obstacle = obstacle });
		}

		private static void CollectBakeObstacles(Scene scene, SceneNavigationSettings settings, int agentTypeId, NavMeshBuildSettings build, in Matrix4 worldToSurface, bool plane, List<NavMeshAreaVolume> volumes, ref Vector3 min, ref Vector3 max)
		{
			float cell = build.CellSize;
			float depth = Math.Max(10f, build.AgentHeight * 4f);
			foreach (NavMeshBakeObstacle obstacle in scene.FindObjectsOfType<NavMeshBakeObstacle>())
			{
				// Layers do not apply: a bake obstacle is there for the bake alone.
				if (!obstacle.enabled || !obstacle.gameObject.activeInHierarchy || !obstacle.Affects(agentTypeId))
				{
					continue;
				}

				if (obstacle.GetFootprint(worldToSurface, NavMeshShapes.CoverMargin(cell), cell * 0.25f, plane, -depth, depth, out NavMeshAreaVolume volume))
				{
					volumes.Add(volume);
					foreach (Vector2 p in volume.Polygon)
					{
						min = Vector3.ComponentMin(min, new Vector3(p.X, plane ? 0f : volume.MinY, p.Y));
						max = Vector3.ComponentMax(max, new Vector3(p.X, plane ? 0f : volume.MaxY, p.Y));
					}
				}
			}
		}

		private static void CollectModifierVolumes(Scene scene, in Matrix4 worldToSurface, List<NavMeshAreaVolume> volumes)
		{
			foreach (NavMeshModifierVolume volume in scene.FindObjectsOfType<NavMeshModifierVolume>())
			{
				if (volume.enabled && volume.gameObject.activeInHierarchy)
				{
					volumes.Add(volume.ToLocalVolume(worldToSurface));
				}
			}
		}

		#endregion
	}
}
