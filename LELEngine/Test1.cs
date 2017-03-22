using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LELEngine;

class Test1 : Behaviour
{
    //Use this for initialization
    //Do not use "base"
    public override void Start()
    {
        Console.WriteLine("Start of Test1 in " + gameObject.name);
    }
}
