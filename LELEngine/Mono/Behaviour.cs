using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    class Behaviour
    {
        public GameObject gameObject;
        public Transform transform
        {
            get
            {
                return gameObject.transform;
            }
        }
        public Shaders.ShaderProgram UsingShader = null;
        public T GetComponent<T>()
            where T : Behaviour, new()
        {
            return gameObject.GetComponent<T>();
        }

        public T AddComponent<T>()
        where T : Behaviour, new()
        {
            return gameObject.AddComponent<T>();
        }

        public Behaviour AddComponent(string component)
        {
            return gameObject.AddComponent(component);
        }

        public GameObject Instanciate(string Name)
        {
            return gameObject.Scene.CreateGameObject(Name);
        }

        public GameObject Instanciate(string Name, ICollection<string> Components)
        {
            return gameObject.Scene.CreateGameObject(Name, Components);
        }

        // Executable methods
        public virtual void Awake() {}
        public virtual void Start() {}
        public virtual void Update() {}
        public virtual void FixedUpdate() {}
        // Internal methods (do not interfere)
        public virtual void LateUpdate() {}
        public virtual void OnRender() {}
    }
}
