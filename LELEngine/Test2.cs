using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LELEngine;

class Test2 : Behaviour
{
    public override void Awake()
    {
        Console.WriteLine("Awake of Test2 in " + gameObject.name);
    }

    public override void Start()
    {
        Console.WriteLine("Start of Test2 in " + gameObject.name);
    }
}
