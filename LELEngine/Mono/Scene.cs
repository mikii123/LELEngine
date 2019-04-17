using System.Collections.Generic;

namespace LELEngine
{
	public sealed class Scene
	{
		#region PublicFields

		public List<GameObject> SceneGameObjects = new List<GameObject>();

		#endregion

		#region Constructors

		public Scene()
		{ }

		public Scene(string path)
		{ }

		public Scene(params string[] gameObjects)
		{
			foreach (string ob in gameObjects)
			{
				CreateGameObject(ob);
			}
		}

		public Scene(ICollection<string> components, params string[] gameObjects)
		{
			foreach (string ob in gameObjects)
			{
				CreateGameObject(ob, components);
			}
		}

		#endregion

		#region PublicMethods

		public GameObject CreateGameObject(string Name)
		{
			GameObject result = new GameObject(Name, this);
			SceneGameObjects.Add(result);
			return result;
		}

		public GameObject CreateGameObject(string Name, ICollection<string> components)
		{
			GameObject result = new GameObject(Name, this);
			SceneGameObjects.Add(result);
			foreach (string ob in components)
			{
				result.AddComponent(ob);
			}

			return result;
		}

		#endregion
	}
}
