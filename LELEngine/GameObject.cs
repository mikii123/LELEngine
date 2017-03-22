using System;
using System.Collections.Generic;
using LELEngine;

class GameObject
{
    public Scene Scene;
    public List<Behaviour> Components = new List<Behaviour>();
    public Transform transform;
    public string name;
    public GameObject(string Name, Scene scene)
    {
        name = Name;
        Scene = scene;
        transform = AddTransform();
        foreach (var ob in Components)
        {
            ob.gameObject = this;
        }
        Program.MainWindow.InitBehaviour(Components);
    }

    public T GetComponent<T>()
        where T : Behaviour, new()
    {
        T dummy = Activator.CreateInstance<T>();

        foreach (var ob in Components)
        {
            if (ob.GetType() == dummy.GetType())
            {
                return ob as T;
            }
        }
        return null;
    }

    public T AddComponent<T>()
        where T : Behaviour, new()
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

        Components.Add(component);
        component.gameObject = this;
        return component;
    }

    public Behaviour LinkComponent(Behaviour component)
    {
        Components.Add(component);
        component.gameObject = this;
        Program.MainWindow.InitBehaviour(component);
        return component;
    }
}
