using System;
using System.Collections.Generic;

namespace LELEngine
{
    public sealed class GameObject
    {
        public Scene scene;
        public List<Behaviour> components = new List<Behaviour>();
        public Transform transform;
        public string name;

        public GameObject(string Name, Scene scene)
        {
            name = Name;
            this.scene = scene;
            transform = AddTransform();
            foreach (var ob in components)
            {
                ob.gameObject = this;
            }
            Game.MainWindow.InitBehaviour(components);
        }

        public T GetComponent<T>()
            where T : Behaviour
        {
            foreach (var ob in components)
            {
                if (ob.GetType() == typeof(T))
                {
                    return ob as T;
                }
            }
            return null;
        }

        public T AddComponent<T>()
            where T : Behaviour
        {
            T component = Activator.CreateInstance<T>();

            LinkComponent(component);
            return component;
        }

        public Behaviour AddComponent(string component)
        {
            Behaviour beh = Activator.CreateInstance(Type.GetType(component)) as Behaviour;
            LinkComponent(beh);
            return beh;
        }

        public Transform AddTransform()
        {
            Transform component = new Transform();

            components.Add(component);
            component.gameObject = this;
            return component;
        }

        public Behaviour LinkComponent(Behaviour component)
        {
            components.Add(component);
            component.gameObject = this;
            Game.MainWindow.InitBehaviour(component);
            return component;
        }
    }
}
