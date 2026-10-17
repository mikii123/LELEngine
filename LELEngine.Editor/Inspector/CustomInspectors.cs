using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Navigation;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Extra inspector UI drawn below the serialized fields of specific component types (buttons, status).
	///     Registered per type; the generic field drawer stays in charge of the fields.
	/// </summary>
	internal static class CustomInspectors
	{
		#region PrivateFields

		private static readonly Dictionary<Type, Func<Behaviour, InspectorContext, bool>> drawers = new Dictionary<Type, Func<Behaviour, InspectorContext, bool>>
		{
			{ typeof(NavMeshSurface), (component, context) => NavMeshSurfaceInspector.Draw((NavMeshSurface)component, context) },
			{ typeof(NavMeshAgent), (component, context) => NavMeshAgentInspector.Draw((NavMeshAgent)component, context) },
			{ typeof(LELEngine.VFX.VisualEffect), (component, context) => VFX.VisualEffectInspector.Draw((LELEngine.VFX.VisualEffect)component, context) }
		};

		#endregion

		#region PublicMethods

		/// <summary>Draws the custom part for the component's type (or a base type); true when it changed the component.</summary>
		public static bool Draw(Behaviour component, InspectorContext context)
		{
			for (Type type = component.GetType(); type != null && type != typeof(Behaviour); type = type.BaseType)
			{
				if (drawers.TryGetValue(type, out Func<Behaviour, InspectorContext, bool> drawer))
				{
					return drawer(component, context);
				}
			}

			return false;
		}

		#endregion
	}

	/// <summary>Bake / clear buttons and the data summary of a navmesh surface.</summary>
	internal static class NavMeshSurfaceInspector
	{
		public static bool Draw(NavMeshSurface surface, InspectorContext context)
		{
			bool changed = false;
			ImGui.Spacing();
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			if (ImGui.Button("Bake", new NVector2(width, 0)))
			{
				context.Editor.Edits.Begin(context.UndoTarget);
				var watch = System.Diagnostics.Stopwatch.StartNew();
				surface.BuildNavMesh();
				Debug.Log($"[NavMesh] Baked {surface.gameObject?.Name} in {watch.ElapsedMilliseconds} ms");
				context.Editor.Edits.RequestCommit();
				changed = true;
			}

			ImGui.SameLine();
			ImGui.BeginDisabled(surface.NavMeshData == null || surface.NavMeshData.IsEmpty);
			if (ImGui.Button("Clear", new NVector2(width, 0)))
			{
				context.Editor.Edits.Begin(context.UndoTarget);
				surface.RemoveNavMeshData();
				context.Editor.Edits.RequestCommit();
				changed = true;
			}

			ImGui.EndDisabled();

			NavMeshData data = surface.NavMeshData;
			if (data == null || data.IsEmpty)
			{
				ImGui.TextDisabled("Not baked.");
			}
			else
			{
				ImGui.TextDisabled($"{data.Tiles.Length} tiles, {data.PolygonCount} polygons, {data.DataSize / 1024f:0.0} KB" + (surface.IsRegistered ? "" : " (not active)"));
				ImGui.TextDisabled($"Voxel {data.Settings.CellSize:0.###} x {data.Settings.CellHeight:0.###}, tile {data.TileWorldSize:0.##} units");
			}

			return changed;
		}
	}

	/// <summary>Live state of an agent while playing.</summary>
	internal static class NavMeshAgentInspector
	{
		public static bool Draw(NavMeshAgent agent, InspectorContext context)
		{
			if (!context.Editor.IsPlaying)
			{
				return false;
			}

			ImGui.Spacing();
			ImGui.TextDisabled(agent.isOnNavMesh ? "On navmesh" : "Off navmesh");
			ImGui.TextDisabled($"Path: {agent.pathStatus}{(agent.pathPending ? " (pending)" : "")}, remaining {(float.IsInfinity(agent.remainingDistance) ? "?" : agent.remainingDistance.ToString("0.00"))}");
			ImGui.TextDisabled($"Velocity {agent.velocity.Length:0.00} / desired {agent.desiredVelocity.Length:0.00}");
			return false;
		}
	}
}
