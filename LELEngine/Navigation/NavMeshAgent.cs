using System;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	internal enum AgentState
	{
		Invalid,
		Walking
	}

	internal enum TargetState
	{
		None,
		Failed,
		Valid,
		Requesting,
		WaitingForQueue,
		WaitingForPath,
		Velocity
	}

	/// <summary>Quality (sample count) of the local avoidance of an agent.</summary>
	public enum ObstacleAvoidanceType
	{
		NoObstacleAvoidance = 0,
		LowQualityObstacleAvoidance = 1,
		MedQualityObstacleAvoidance = 2,
		GoodQualityObstacleAvoidance = 3,
		HighQualityObstacleAvoidance = 4
	}

	/// <summary>
	///     A character that walks the navmesh (Unity's NavMeshAgent): give it a destination and it plans a path,
	///     follows it with steering, avoids other agents and obstacles and slides along walls. The agent keeps its
	///     own simulated position (<see cref="nextPosition" />) and moves its transform to it every frame. Subclass
	///     and override <see cref="ModifyDesiredVelocity" /> or <see cref="OnDestinationReached" /> for custom steering.
	/// </summary>
	public class NavMeshAgent : Behaviour
	{
		#region PublicFields

		[Tooltip("Surfaces baked for this agent type are walkable.")]
		public int agentTypeId;

		[Tooltip("Height of the transform's pivot above the navmesh.")]
		public float baseOffset;

		[Header("Steering")]
		[Tooltip("Maximum speed, units per second.")]
		public float speed = 3.5f;

		[Tooltip("Maximum turning speed, degrees per second.")]
		public float angularSpeed = 120f;

		[Tooltip("Maximum acceleration, units per second squared.")]
		public float acceleration = 8f;

		[Tooltip("The agent stops this close to the destination.")]
		public float stoppingDistance;

		[Tooltip("Slow down when approaching the destination.")]
		public bool autoBraking = true;

		[Header("Obstacle Avoidance")]
		public float radius = 0.5f;

		public float height = 2f;
		public ObstacleAvoidanceType obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;

		[Tooltip("Agents with a lower value give way to agents with a higher one (0 = most important).")]
		[Range(0, 99)]
		public int avoidancePriority = 50;

		[Header("Path Finding")]
		[Tooltip("Re-plan when the path becomes invalid (the navmesh changed).")]
		public bool autoRepath = true;

		[Tooltip("Bit mask of the areas the agent may walk on.")]
		public int areaMask = NavMeshArea.AllAreas;

		/// <summary>Move the transform to the simulated position every frame.</summary>
		[NonSerialized] public bool updatePosition = true;

		/// <summary>Turn the transform toward the movement direction every frame.</summary>
		[NonSerialized] public bool updateRotation = true;

		/// <summary>Halt the agent on its path; false resumes.</summary>
		[NonSerialized] public bool isStopped;

		/// <summary>Current destination (the position the path ends at); setting it requests a path.</summary>
		public Vector3 destination
		{
			get => TargetState == TargetState.None ? Position : TargetPos;
			set => SetDestination(value);
		}

		/// <summary>The agent stands on the navmesh and is being simulated.</summary>
		public bool isOnNavMesh => State == AgentState.Walking;

		public bool hasPath => TargetState == TargetState.Valid || TargetState == TargetState.WaitingForPath || TargetState == TargetState.WaitingForQueue;

		/// <summary>A path request is being processed.</summary>
		public bool pathPending => TargetState == TargetState.Requesting || TargetState == TargetState.WaitingForQueue || TargetState == TargetState.WaitingForPath;

		/// <summary>The current path does not reach the destination.</summary>
		public bool isPathStale => TargetState == TargetState.Failed;

		public NavMeshPathStatus pathStatus => TargetState == TargetState.Valid ? PartialPath ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathInvalid;

		/// <summary>Distance left along the path corners; infinity while the path is pending.</summary>
		public float remainingDistance
		{
			get
			{
				if (pathPending)
				{
					return float.PositiveInfinity;
				}

				if (TargetState != TargetState.Valid || CornerCount == 0)
				{
					return 0f;
				}

				float distance = 0f;
				Vector3 previous = Position;
				for (int i = 0; i < CornerCount; i++)
				{
					distance += NavMath.Dist2D(previous, CornerVerts[i]);
					previous = CornerVerts[i];
				}

				// Corners stop at the lookahead; the corridor's end is further.
				if ((CornerFlags[CornerCount - 1] & NavMeshQuery.StraightPathEnd) == 0)
				{
					distance += NavMath.Dist2D(previous, Corridor.Target);
				}

				return distance;
			}
		}

		/// <summary>Velocity the steering wants, before avoidance.</summary>
		public Vector3 desiredVelocity => DesiredVelocityInternal;

		/// <summary>Current velocity; setting it drives the agent directly (no path).</summary>
		public Vector3 velocity
		{
			get => VelocityInternal;
			set
			{
				TargetState = TargetState.Velocity;
				TargetPos = value;
				TargetRef = PolyRef.Null;
			}
		}

		/// <summary>The simulated position on the navmesh (set: warps the simulation, not the transform).</summary>
		public Vector3 nextPosition
		{
			get => Position;
			set => Warp(value);
		}

		/// <summary>The corner the agent currently steers toward.</summary>
		public Vector3 steeringTarget => CornerCount > 0 ? CornerVerts[0] : Position;

		public NavMeshQueryFilter Filter { get; } = new NavMeshQueryFilter();

		#endregion

		#region InternalFields

		internal AgentState State = AgentState.Invalid;
		internal TargetState TargetState = TargetState.None;
		internal readonly PathCorridor Corridor = new PathCorridor();
		internal readonly LocalBoundary Boundary = new LocalBoundary();
		internal Vector3 Position;
		internal Vector3 VelocityInternal;
		internal Vector3 DesiredVelocityInternal;
		internal Vector3 NewVelocity;
		internal Vector3 Displacement;
		internal readonly Vector3[] CornerVerts = new Vector3[NavMeshCrowd.MaxCorners];
		internal readonly byte[] CornerFlags = new byte[NavMeshCrowd.MaxCorners];
		internal readonly PolyRef[] CornerPolys = new PolyRef[NavMeshCrowd.MaxCorners];
		internal int CornerCount;
		internal readonly int[] Neighbors = new int[NavMeshCrowd.MaxNeighbors];
		internal readonly float[] NeighborDistances = new float[NavMeshCrowd.MaxNeighbors];
		internal int NeighborCount;
		internal PolyRef TargetRef;
		internal Vector3 TargetPos;
		internal bool TargetReplan;
		internal float TargetReplanTime;
		internal int TargetPathQueueRef;
		internal float TopologyOptTime;
		internal bool PartialPath;
		internal int CrowdIndex = -1;
		internal bool Registered;

		internal float CollisionQueryRange => radius * 12f;
		internal float PathOptimizationRange => radius * 30f;

		#endregion

		#region PrivateFields

		private bool reachedNotified;
		private readonly NavMeshPath scratchPath = new NavMeshPath();

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			Register();
		}

		public override void OnDisable()
		{
			Unregister();
		}

		public override void LateUpdate()
		{
			SyncTransform();
		}

		protected internal override void ReleaseResources()
		{
			Unregister();
		}

		#endregion

		#region PublicMethods

		/// <summary>Requests a path to the position; the agent starts moving as soon as a first segment is known.</summary>
		public bool SetDestination(Vector3 target)
		{
			Filter.AreaMask = areaMask;
			Filter.AgentTypeId = agentTypeId;
			if (!Registered)
			{
				Register();
			}

			NavMeshQuery query = NavMesh.Crowd.Query;
			if (!query.FindNearestPoly(target, new Vector3(2f, 4f, 2f), Filter, out PolyRef reference, out Vector3 nearest))
			{
				TargetState = TargetState.Failed;
				return false;
			}

			reachedNotified = false;
			RequestMoveTarget(reference, nearest, false);
			return true;
		}

		/// <summary>Drops the current path; the agent stops.</summary>
		public void ResetPath()
		{
			TargetState = TargetState.None;
			TargetRef = PolyRef.Null;
			CornerCount = 0;
			DesiredVelocityInternal = Vector3.Zero;
			Corridor.Reset(Corridor.FirstPoly, Position);
		}

		/// <summary>Teleports the agent onto the navmesh at the position; false when none is there.</summary>
		public bool Warp(Vector3 newPosition)
		{
			Filter.AreaMask = areaMask;
			Filter.AgentTypeId = agentTypeId;
			NavMeshQuery query = NavMesh.Crowd.Query;
			if (!query.FindNearestPoly(newPosition, new Vector3(2f, 4f, 2f), Filter, out PolyRef reference, out Vector3 nearest))
			{
				Position = newPosition;
				State = AgentState.Invalid;
				return false;
			}

			Position = nearest;
			VelocityInternal = Vector3.Zero;
			Corridor.Reset(reference, nearest);
			Boundary.Reset();
			State = AgentState.Walking;
			if (TargetState != TargetState.None && TargetState != TargetState.Velocity)
			{
				RequestMoveTarget(TargetRef, TargetPos, true);
			}

			SyncTransform();
			return true;
		}

		/// <summary>Moves the agent by an offset along the navmesh (sliding on walls), independent of its path.</summary>
		public void Move(Vector3 offset)
		{
			if (State != AgentState.Walking)
			{
				return;
			}

			Corridor.MovePosition(Position + offset, NavMesh.Crowd.Query, Filter);
			Position = Corridor.Pos;
		}

		/// <summary>Computes a path for this agent's settings without following it.</summary>
		public bool CalculatePath(Vector3 target, NavMeshPath path)
		{
			Filter.AreaMask = areaMask;
			Filter.AgentTypeId = agentTypeId;
			return NavMesh.CalculatePath(NavMesh.Crowd.Query, Filter, Position, target, path);
		}

		/// <summary>Follows a path computed with <see cref="CalculatePath" /> (its end becomes the destination).</summary>
		public bool SetPath(NavMeshPath path)
		{
			if (path == null || path.Status == NavMeshPathStatus.PathInvalid || path.Corners.Length == 0)
			{
				return false;
			}

			return SetDestination(path.Corners[path.Corners.Length - 1]);
		}

		/// <summary>Traces a line on the navmesh from the agent; true when a wall is hit first.</summary>
		public bool Raycast(Vector3 target, out NavMeshHit hit)
		{
			Filter.AreaMask = areaMask;
			Filter.AgentTypeId = agentTypeId;
			hit = default;
			PolyRef start = Corridor.FirstPoly;
			if (start.IsNull)
			{
				hit.Position = Position;
				hit.Hit = true;
				return true;
			}

			NavStatus status = NavMesh.Crowd.Query.Raycast(start, Position, target, Filter, out float t, out Vector3 normal, null);
			if (status.Failed() || t >= float.MaxValue)
			{
				hit.Position = target;
				return false;
			}

			hit.Position = Vector3.Lerp(Position, target, t);
			hit.Normal = normal;
			hit.Distance = (hit.Position - Position).Length;
			hit.Hit = true;
			return true;
		}

		/// <summary>Corners of the current path (a copy).</summary>
		public NavMeshPath path
		{
			get
			{
				scratchPath.ClearCorners();
				if (TargetState == TargetState.Valid)
				{
					NavMesh.CalculatePath(NavMesh.Crowd.Query, Filter, Position, TargetPos, scratchPath);
				}

				return scratchPath;
			}
		}

		#endregion

		#region ProtectedMethods

		/// <summary>Hook for custom steering: the velocity the crowd computed for this frame, before avoidance.</summary>
		protected internal virtual Vector3 ModifyDesiredVelocity(Vector3 desiredVelocity)
		{
			return desiredVelocity;
		}

		/// <summary>Called once when the agent gets within the stopping distance of its destination.</summary>
		protected virtual void OnDestinationReached()
		{ }

		#endregion

		#region InternalMethods

		internal void RequestMoveTarget(PolyRef reference, in Vector3 position, bool replan)
		{
			TargetRef = reference;
			TargetPos = position;
			TargetPathQueueRef = PathQueue.Invalid;
			TargetReplan = replan;
			TargetState = reference.IsNull ? TargetState.Failed : TargetState.Requesting;
		}

		internal bool AtDestination(float distanceToGoal)
		{
			bool endOfPath = CornerCount > 0 && (CornerFlags[CornerCount - 1] & NavMeshQuery.StraightPathEnd) != 0;
			bool reached = endOfPath && distanceToGoal <= Math.Max(stoppingDistance, 0.01f) && !PartialPath;
			if (!reached && endOfPath && PartialPath)
			{
				// Unreachable target: stop at the closest point.
				reached = distanceToGoal <= Math.Max(stoppingDistance, radius * 0.5f);
			}

			if (reached && !reachedNotified)
			{
				reachedNotified = true;
				try
				{
					OnDestinationReached();
				}
				catch (Exception e)
				{
					Debug.LogException(e, this);
				}
			}

			return reached;
		}

		internal void SyncTransform()
		{
			if (transform == null)
			{
				return;
			}

			if (updatePosition && State == AgentState.Walking)
			{
				transform.position = Position + new Vector3(0f, baseOffset, 0f);
			}

			if (updateRotation && VelocityInternal.LengthSquared > 0.01f)
			{
				Vector3 dir = VelocityInternal;
				dir.Y = 0f;
				dir.Normalize();
				Quaternion wanted = QuaternionHelper.LookRotation(dir, Vector3.UnitY);
				float maxDegrees = angularSpeed * Time.deltaTime;
				transform.rotation = RotateTowards(transform.rotation, wanted, maxDegrees);
			}
		}

		#endregion

		#region PrivateMethods

		private void Register()
		{
			if (Registered)
			{
				return;
			}

			Registered = true;
			Filter.AreaMask = areaMask;
			Filter.AgentTypeId = agentTypeId;
			Position = transform.position - new Vector3(0f, baseOffset, 0f);
			NavMesh.Crowd.Add(this);
			Warp(Position);
		}

		private void Unregister()
		{
			if (!Registered)
			{
				return;
			}

			Registered = false;
			NavMesh.Crowd.Remove(this);
			State = AgentState.Invalid;
		}

		private static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegrees)
		{
			float dot = Math.Clamp(Math.Abs(NavMath.QuaternionDot(from, to)), -1f, 1f);
			float angle = (float)(Math.Acos(dot) * 2.0 * 180.0 / Math.PI);
			if (angle <= maxDegrees || angle < 1e-4f)
			{
				return to;
			}

			return Quaternion.Slerp(from, to, maxDegrees / angle);
		}

		#endregion
	}
}
