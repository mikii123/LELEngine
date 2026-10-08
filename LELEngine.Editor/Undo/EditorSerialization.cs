using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>JSON snapshots of single components and settings objects (undo records, copy / paste of values).</summary>
	internal static class EditorSerialization
	{
		#region PublicMethods

		/// <summary>The serialized state of a component: {"enabled": ..., "fields": {...}}.</summary>
		public static string CaptureComponent(Behaviour component)
		{
			var context = new SerializationContext { Scene = component.gameObject?.scene, Location = component.GetType().Name };
			return Write(writer =>
			{
				writer.WriteStartObject();
				writer.WriteBoolean("enabled", component.enabled);
				writer.WritePropertyName("fields");
				writer.WriteStartObject();
				ValueSerializer.WriteFields(writer, component, context);
				writer.WriteEndObject();
				writer.WriteEndObject();
			});
		}

		public static void ApplyComponent(Behaviour component, string json)
		{
			Scene scene = component.gameObject?.scene;
			using (JsonDocument document = JsonDocument.Parse(json))
			{
				JsonElement value;
				if (document.RootElement.TryGetProperty("enabled", out value))
				{
					component.enabled = value.GetBoolean();
				}

				if (document.RootElement.TryGetProperty("fields", out value))
				{
					ValueSerializer.ReadFields(value, component, ContextFor(scene));
				}
			}

			if (component is ISerializationCallbackReceiver receiver)
			{
				receiver.OnAfterDeserialize();
			}

			scene?.Validate(component);
		}

		/// <summary>Fields of any serializable object (scene settings).</summary>
		public static string CaptureObject(object target, Scene scene)
		{
			var context = new SerializationContext { Scene = scene, Location = target.GetType().Name };
			return Write(writer =>
			{
				writer.WriteStartObject();
				ValueSerializer.WriteFields(writer, target, context);
				writer.WriteEndObject();
			});
		}

		public static void ApplyObject(object target, string json, Scene scene)
		{
			using (JsonDocument document = JsonDocument.Parse(json))
			{
				ValueSerializer.ReadFields(document.RootElement, target, ContextFor(scene));
			}
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

		#region PrivateMethods

		private static string Write(Action<Utf8JsonWriter> write)
		{
			using (var stream = new MemoryStream())
			{
				using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
				{
					write(writer);
				}

				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		#endregion
	}
}
