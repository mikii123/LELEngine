using System.Linq;

namespace LELCS.Model
{
	public abstract class ECSSystem<TComponent1> : AbstractSystem
		where TComponent1 : ECSComponent
	{
		#region ProtectedMethods

		protected abstract void Execute
		(
			ECSEntity ecsEntity,
			ref TComponent1 component1);

		#endregion

		#region AllOtherMembers

		internal override void ValidateAndExecute(ECSEntity ecsEntity, ECSManager manager)
		{
			if (Subtractive != null && Subtractive.Any(type => manager.HasComponent(ecsEntity, type)))
			{
				return;
			}

			if (!manager.HasComponent<TComponent1>(ecsEntity))
			{
				return;
			}

			TComponent1 component1 = manager.GetComponent<TComponent1>(ecsEntity);
			Execute(ecsEntity, ref component1);
		}

		#endregion
	}

	public abstract class ECSSystem<TComponent1, TComponent2> : AbstractSystem
		where TComponent1 : ECSComponent
		where TComponent2 : ECSComponent
	{
		#region ProtectedMethods

		protected abstract void Execute
		(
			ECSEntity ecsEntity,
			ref TComponent1 component1,
			ref TComponent2 component2);

		#endregion

		#region AllOtherMembers

		internal override void ValidateAndExecute(ECSEntity ecsEntity, ECSManager manager)
		{
			if (Subtractive != null && Subtractive.Any(type => manager.HasComponent(ecsEntity, type)))
			{
				return;
			}

			if (!manager.HasComponent<TComponent1>(ecsEntity)
				|| !manager.HasComponent<TComponent2>(ecsEntity))
			{
				return;
			}

			TComponent1 component1 = manager.GetComponent<TComponent1>(ecsEntity);
			TComponent2 component2 = manager.GetComponent<TComponent2>(ecsEntity);
			Execute(
				ecsEntity,
				ref component1,
				ref component2);
		}

		#endregion
	}

	public abstract class ECSSystem<TComponent1, TComponent2, TComponent3> : AbstractSystem
		where TComponent1 : ECSComponent
		where TComponent2 : ECSComponent
		where TComponent3 : ECSComponent
	{
		#region ProtectedMethods

		protected abstract void Execute
		(
			ECSEntity ecsEntity,
			ref TComponent1 component1,
			ref TComponent2 component2,
			ref TComponent3 component3);

		#endregion

		#region AllOtherMembers

		internal override void ValidateAndExecute(ECSEntity ecsEntity, ECSManager manager)
		{
			if (Subtractive != null && Subtractive.Any(type => manager.HasComponent(ecsEntity, type)))
			{
				return;
			}

			if (!manager.HasComponent<TComponent1>(ecsEntity)
				|| !manager.HasComponent<TComponent2>(ecsEntity)
				|| !manager.HasComponent<TComponent3>(ecsEntity))
			{
				return;
			}

			TComponent1 component1 = manager.GetComponent<TComponent1>(ecsEntity);
			TComponent2 component2 = manager.GetComponent<TComponent2>(ecsEntity);
			TComponent3 component3 = manager.GetComponent<TComponent3>(ecsEntity);
			Execute(
				ecsEntity,
				ref component1,
				ref component2,
				ref component3);
		}

		#endregion
	}

	public abstract class ECSSystem<TComponent1, TComponent2, TComponent3, TComponent4> : AbstractSystem
		where TComponent1 : ECSComponent
		where TComponent2 : ECSComponent
		where TComponent3 : ECSComponent
		where TComponent4 : ECSComponent
	{
		#region ProtectedMethods

		protected abstract void Execute
		(
			ECSEntity ecsEntity,
			ref TComponent1 component1,
			ref TComponent2 component2,
			ref TComponent3 component3,
			ref TComponent4 component4);

		#endregion

		#region AllOtherMembers

		internal override void ValidateAndExecute(ECSEntity ecsEntity, ECSManager manager)
		{
			if (Subtractive != null && Subtractive.Any(type => manager.HasComponent(ecsEntity, type)))
			{
				return;
			}

			if (!manager.HasComponent<TComponent1>(ecsEntity)
				|| !manager.HasComponent<TComponent2>(ecsEntity)
				|| !manager.HasComponent<TComponent3>(ecsEntity)
				|| !manager.HasComponent<TComponent4>(ecsEntity))
			{
				return;
			}

			TComponent1 component1 = manager.GetComponent<TComponent1>(ecsEntity);
			TComponent2 component2 = manager.GetComponent<TComponent2>(ecsEntity);
			TComponent3 component3 = manager.GetComponent<TComponent3>(ecsEntity);
			TComponent4 component4 = manager.GetComponent<TComponent4>(ecsEntity);
			Execute(
				ecsEntity,
				ref component1,
				ref component2,
				ref component3,
				ref component4);
		}

		#endregion
	}

	public abstract class ECSSystem<TComponent1, TComponent2, TComponent3, TComponent4, TComponent5> : AbstractSystem
		where TComponent1 : ECSComponent
		where TComponent2 : ECSComponent
		where TComponent3 : ECSComponent
		where TComponent4 : ECSComponent
		where TComponent5 : ECSComponent
	{
		#region ProtectedMethods

		protected abstract void Execute
		(
			ECSEntity ecsEntity,
			ref TComponent1 component1,
			ref TComponent2 component2,
			ref TComponent3 component3,
			ref TComponent4 component4,
			ref TComponent5 component5);

		#endregion

		#region AllOtherMembers

		internal override void ValidateAndExecute(ECSEntity ecsEntity, ECSManager manager)
		{
			if (Subtractive != null && Subtractive.Any(type => manager.HasComponent(ecsEntity, type)))
			{
				return;
			}

			if (!manager.HasComponent<TComponent1>(ecsEntity)
				|| !manager.HasComponent<TComponent2>(ecsEntity)
				|| !manager.HasComponent<TComponent3>(ecsEntity)
				|| !manager.HasComponent<TComponent4>(ecsEntity)
				|| !manager.HasComponent<TComponent5>(ecsEntity))
			{
				return;
			}

			TComponent1 component1 = manager.GetComponent<TComponent1>(ecsEntity);
			TComponent2 component2 = manager.GetComponent<TComponent2>(ecsEntity);
			TComponent3 component3 = manager.GetComponent<TComponent3>(ecsEntity);
			TComponent4 component4 = manager.GetComponent<TComponent4>(ecsEntity);
			TComponent5 component5 = manager.GetComponent<TComponent5>(ecsEntity);
			Execute(
				ecsEntity,
				ref component1,
				ref component2,
				ref component3,
				ref component4,
				ref component5);
		}

		#endregion
	}
}
