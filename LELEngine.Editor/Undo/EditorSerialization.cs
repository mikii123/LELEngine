using System;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>Binary snapshots of single components and settings objects (undo records, copy / paste of values).</summary>
	internal static class EditorSerialization
	{
		#region PublicMethods

		/// <summary>The serialized state of a component: enabled flag and fields.</summary>
		public static byte[] CaptureComponent(Behaviour component)
		{
			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = component.gameObject?.scene, Location = component.GetType().Name }, 1024);
			writer.Body.WriteInt32(writer.TypeId(component.GetType()));
			writer.Body.WriteBool(component.enabled);
			int lengthPosition = writer.Body.ReserveInt32();
			writer.WriteFields(component);
			writer.Body.PatchInt32(lengthPosition, writer.Body.Length - lengthPosition - 4);
			return writer.ToArray();
		}

		public static void ApplyComponent(Behaviour component, byte[] data)
		{
			Scene scene = component.gameObject?.scene;
			var reader = new BinaryDocumentReader(data, ContextFor(scene));
			reader.Context.Location = component.GetType().Name;
			int typeId = reader.Body.ReadInt32();
			component.enabled = reader.Body.ReadBool();
			reader.ReadFieldBlock(component, typeId);

			if (component is ISerializationCallbackReceiver receiver)
			{
				receiver.OnAfterDeserialize();
			}

			scene?.Validate(component);
		}

		/// <summary>Fields of any serializable object (scene settings).</summary>
		public static byte[] CaptureObject(object target, Scene scene)
		{
			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = scene, Location = target.GetType().Name }, 1024);
			writer.WriteObjectValue(target, 0);
			return writer.ToArray();
		}

		public static void ApplyObject(object target, byte[] data, Scene scene)
		{
			var reader = new BinaryDocumentReader(data, ContextFor(scene));
			reader.Context.Location = target.GetType().Name;
			reader.ReadObjectValue(target.GetType(), target, 0);
		}

		public static bool SameState(byte[] a, byte[] b)
		{
			return a == b || (a != null && b != null && a.AsSpan().SequenceEqual(b));
		}

		/// <summary>Reference resolution context holding every object and component of the scene by id.</summary>
		public static SerializationContext ContextFor(Scene scene)
		{
			var context = new SerializationContext { Scene = scene };
			if (scene == null)
			{
				return context;
			}

			foreach (GameObject go in scene.GameObjects)
			{
				context.Objects[go.Id] = go;
				foreach (Behaviour component in go.Components)
				{
					context.Objects[component.Id] = component;
				}
			}

			return context;
		}

		#endregion
	}
}
