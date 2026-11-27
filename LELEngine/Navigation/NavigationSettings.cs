using System;
using System.Collections.Generic;

namespace LELEngine.Navigation
{
	/// <summary>
	///     Project Settings > Navigation: the agent types a scene's navigation is baked for (Unity's Agents tab). Each type
	///     has an id that agents (<see cref="NavMeshAgent.agentTypeId" />), surfaces and queries name it by, and the build
	///     settings of its navmesh: the agent's radius, height, climb and slope, the voxels it is baked at.
	/// </summary>
	[Serializable]
	public sealed class NavigationSettings
	{
		#region PublicFields

		public List<NavMeshAgentType> AgentTypes = new List<NavMeshAgentType> { new NavMeshAgentType() };

		/// <summary>The project's own areas (3..31: 0 Walkable, 1 Not Walkable and 2 Jump are the engine's): names and path costs.</summary>
		public List<NavMeshAreaDefinition> Areas = new List<NavMeshAreaDefinition>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     Makes the areas the live ones: <see cref="NavMesh.AreaNames" /> and the costs paths are found with
		///     (<see cref="NavMesh.SetAreaCost" />); areas not defined go back to "User n" at cost 1.
		/// </summary>
		public void Apply()
		{
			for (int area = NavMeshArea.FirstUserArea; area < NavMeshArea.Count; area++)
			{
				NavMesh.AreaNames[area] = "User " + area;
				NavMesh.SetAreaCost(area, 1f);
			}

			if (Areas == null)
			{
				return;
			}

			foreach (NavMeshAreaDefinition definition in Areas)
			{
				if (definition != null && definition.Index >= NavMeshArea.FirstUserArea && definition.Index < NavMeshArea.Count)
				{
					NavMesh.AreaNames[definition.Index] = string.IsNullOrEmpty(definition.Name) ? "User " + definition.Index : definition.Name;
					NavMesh.SetAreaCost(definition.Index, definition.Cost);
				}
			}
		}

		/// <summary>An area index no area of the project has (the first free one from 3), or -1 when all are taken.</summary>
		public int NextAreaIndex()
		{
			for (int area = NavMeshArea.FirstUserArea; area < NavMeshArea.Count; area++)
			{
				if (Areas == null || !Areas.Exists(definition => definition != null && definition.Index == area))
				{
					return area;
				}
			}

			return -1;
		}

		/// <summary>The agent type of an id, or null.</summary>
		public NavMeshAgentType Find(int id)
		{
			if (AgentTypes == null)
			{
				return null;
			}

			foreach (NavMeshAgentType type in AgentTypes)
			{
				if (type != null && type.Id == id)
				{
					return type;
				}
			}

			return null;
		}

		/// <summary>The name of an agent type id ("Agent 3" for an id the project does not have).</summary>
		public string NameOf(int id)
		{
			NavMeshAgentType type = Find(id);
			return type != null && !string.IsNullOrEmpty(type.Name) ? type.Name : "Agent " + id;
		}

		/// <summary>An id no agent type has (the next after the highest).</summary>
		public int NextId()
		{
			int next = 0;
			if (AgentTypes != null)
			{
				foreach (NavMeshAgentType type in AgentTypes)
				{
					if (type != null)
					{
						next = Math.Max(next, type.Id + 1);
					}
				}
			}

			return next;
		}

		#endregion
	}

	/// <summary>An area of the project: its index (what modifiers and polygons carry), its name and its path cost.</summary>
	[Serializable]
	public sealed class NavMeshAreaDefinition
	{
		#region PublicFields

		[Tooltip("The area's number (3..31) in modifiers, volumes and the baked polygons.")]
		public int Index = NavMeshArea.FirstUserArea;

		public string Name = "Area";

		[Tooltip("What a unit of path through it costs (Walkable costs 1): paths go round it unless the way round is this many times longer.")]
		public float Cost = 1f;

		#endregion
	}

	/// <summary>An agent type of the project: its id, its name and how its navmesh is baked.</summary>
	[Serializable]
	public sealed class NavMeshAgentType
	{
		#region PublicFields

		[Tooltip("The id agents (NavMesh Agent's Agent Type Id), surfaces and queries name this type by.")]
		public int Id;

		public string Name = "Default";

		[Tooltip("The agent's size and the voxels its navmesh is baked at.")]
		public NavMeshBuildSettings Build = new NavMeshBuildSettings();

		#endregion
	}
}
