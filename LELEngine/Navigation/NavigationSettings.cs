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

		#endregion

		#region PublicMethods

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
