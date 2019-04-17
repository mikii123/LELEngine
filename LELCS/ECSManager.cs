using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using LELCS.Model;

namespace LELCS
{
	public sealed class ECSManager
	{
		#region PublicFields

		public List<ECSEntity> Entities { get; private set; }

		#endregion

		#region PrivateFields

		private const int BATCH_SIZE = 512;

		private Dictionary<Type, int> typeIndexLookup;
		private ECSComponent[,] ecsComponentData;

		private List<AbstractSystem> systems;
		private List<ECSEntity> freeEntities;

		#endregion

		#region Constructors

		/// <summary>
		///     Automatic initialization from reflection
		/// </summary>
		/// <param name="assembly">Assembly to lookup</param>
		public ECSManager(Assembly assembly)
		{
			Console.WriteLine("[ECS] Initializing");

			typeIndexLookup = new Dictionary<Type, int>();
			systems = new List<AbstractSystem>();
			Entities = new List<ECSEntity>();
			freeEntities = new List<ECSEntity>();

			Type[] types = assembly.GetTypes();

			int componentIndex = 0;

			foreach (Type type in types)
			{
				bool isSystem = type.IsSubclassOf(typeof(AbstractSystem)) && !type.ContainsGenericParameters;
				bool isComponent = type.IsSubclassOf(typeof(ECSComponent));

				if (isSystem)
				{
					CreateSystemBind(type);
					continue;
				}

				if (isComponent)
				{
					typeIndexLookup[type] = componentIndex;
					componentIndex++;
					continue;
				}
			}

			Console.WriteLine($"[ECS] Found systems: {systems.Count}, component types: {typeIndexLookup.Count}, from {types.Length}");

			ecsComponentData = new ECSComponent[0, typeIndexLookup.Count];
		}

		#endregion

		#region PublicMethods

		public ECSEntity CreateEntity()
		{
			while (true)
			{
				if (freeEntities.Count > 0)
				{
					ECSEntity ecsEntity = freeEntities[0];
					freeEntities.RemoveAt(0);

					ValidateEntity(ref ecsEntity);

					return ecsEntity;
				}

				IncreaseBuffer(BATCH_SIZE);
			}
		}

		public void DestroyEntity(ref ECSEntity ecsEntity)
		{
			InvalidateEntity(ref ecsEntity);
			Entities[ecsEntity.Index] = ecsEntity;
			freeEntities.Add(ecsEntity);
		}

		public bool HasComponent<TComponent>(ECSEntity ecsEntity)
			where TComponent : ECSComponent
		{
			int typeIndex = typeIndexLookup[typeof(TComponent)];
			return ecsComponentData[ecsEntity.Index, typeIndex] != null;
		}

		public bool HasComponent(ECSEntity ecsEntity, Type type)
		{
			int typeIndex = typeIndexLookup[type];
			return ecsComponentData[ecsEntity.Index, typeIndex] != null;
		}

		public void SetComponent<TComponent>(ECSEntity ecsEntity, TComponent component)
			where TComponent : ECSComponent
		{
			int typeIndex = typeIndexLookup[typeof(TComponent)];
			ecsComponentData[ecsEntity.Index, typeIndex] = component;
		}

		public void RemoveComponent<TComponent>(ECSEntity ecsEntity)
			where TComponent : ECSComponent
		{
			int typeIndex = typeIndexLookup[typeof(TComponent)];
			ecsComponentData[ecsEntity.Index, typeIndex] = null;
		}

		public TComponent GetComponent<TComponent>(ECSEntity ecsEntity)
			where TComponent : ECSComponent
		{
			int typeIndex = typeIndexLookup[typeof(TComponent)];
			return (TComponent)ecsComponentData[ecsEntity.Index, typeIndex];
		}

		public void Execute()
		{
			foreach (AbstractSystem lelSystem in systems)
			{
				Parallel.ForEach(
					Entities,
					(entity, state) =>
					{
						if (entity.IsValid)
						{
							lelSystem.ValidateAndExecute(entity, this);
						}
					});
			}
		}

		public void InjectSystems(List<AbstractSystem> ecsSystems)
		{
			systems = ecsSystems;
		}

		public void Shutdown()
		{
			foreach (ECSEntity entity in Entities)
			{
				ECSEntity ecsEntity = entity;
				DestroyEntity(ref ecsEntity);
			}

			typeIndexLookup = null;
			systems = null;
			Entities = null;
			freeEntities = null;
			ecsComponentData = null;
		}

		#endregion

		#region PrivateMethods

		private void ConstructBuffer(int count)
		{
			ecsComponentData = new ECSComponent[count, typeIndexLookup.Count];

			for (int i = 0; i < count; i++)
			{
				CreateInvalidEntity(i);
			}
		}

		private void IncreaseBuffer(int count)
		{
			Console.WriteLine($"[ECS] Increasing buffer to {ecsComponentData.GetLength(0) + count} entities");

			int rows = ecsComponentData.GetLength(0) + count;
			int cols = typeIndexLookup.Count;

			var newArray = new ECSComponent[rows, cols];
			int minRows = Math.Min(rows, ecsComponentData.GetLength(0));
			int minCols = Math.Min(cols, ecsComponentData.GetLength(1));
			for (int i = 0; i < rows; i++)
			{
				if (i < minRows)
				{
					for (int j = 0; j < minCols; j++)
					{
						newArray[i, j] = ecsComponentData[i, j];
					}
				}
				else
				{
					CreateInvalidEntity(i);
				}
			}

			ecsComponentData = newArray;
		}

		private ECSEntity CreateInvalidEntity(int index)
		{
			ECSEntity ecsEntity = new ECSEntity { Index = index, IsValid = false };
			Entities.Add(ecsEntity);
			freeEntities.Add(ecsEntity);
			return ecsEntity;
		}

		private void ValidateEntity(ref ECSEntity ecsEntity)
		{
			ecsEntity.IsValid = true;
			Entities[ecsEntity.Index] = ecsEntity;
		}

		private void InvalidateEntity(ref ECSEntity ecsEntity)
		{
			ecsEntity.IsValid = false;

			for (int i = 0; i < ecsComponentData.GetLength(1); i++)
			{
				ecsComponentData[ecsEntity.Index, i] = null;
			}
		}

		private void CreateSystemBind(Type type)
		{
			Console.WriteLine($"[ECS] Creating system instance of: {type.Name}");
			AbstractSystem systemInstance = Activator.CreateInstance(type) as AbstractSystem;
			systems.Add(systemInstance);
		}

		private void LogErrorNotInitialized()
		{
			Console.WriteLine("[ECS] Not initialized");
		}

		#endregion
	}
}
