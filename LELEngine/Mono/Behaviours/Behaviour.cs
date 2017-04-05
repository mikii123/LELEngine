using OpenTK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    public class Behaviour
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
            return gameObject.scene.CreateGameObject(Name);
        }

        public GameObject Instanciate(string Name, ICollection<string> Components)
        {
            return gameObject.scene.CreateGameObject(Name, Components);
        }

        internal bool Batched = false;
        internal bool Batch(Matrix4 prev, Matrix4 curr)
        {
            if (prev == curr)
            {
                Batched = true;
                return true;
            }
            else
            {
                Batched = false;
                return false;
            }
        }

        // Executable methods
        public virtual void Awake() {}
        public virtual void Start() {}
        public virtual void Update() {}
        public virtual void FixedUpdate() {}
        /// <summary>
        /// Internal Method.
        /// Override at your own risk.
        /// Called in sync with Transform component.
        /// </summary>
        public virtual void LateUpdate() {}
        /// <summary>
        /// Internal Method.
        /// Override at your own risk.
        /// Called in sync with MeshRenderer component.
        /// </summary>
        public virtual void OnRender() {}
    }
}
