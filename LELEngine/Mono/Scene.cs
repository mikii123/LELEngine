using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    class Scene
    {
        public List<GameObject> SceneGameObjects = new List<GameObject>();

        public Scene()
        {

        }

        public Scene(string path)
        {

        }

        public Scene(params string[] gameObjects)
        {
            foreach(var ob in gameObjects)
            {
                CreateGameObject(ob);
            }
        }

        public Scene(ICollection<string> components, params string[] gameObjects)
        {
            foreach (var ob in gameObjects)
            {
                CreateGameObject(ob, components);
            }
        }

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
            foreach (var ob in components)
            {
                result.AddComponent(ob);
            }
            return result;
        }
    }
}
