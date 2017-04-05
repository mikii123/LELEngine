using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LELEngine;
using OpenTK;

class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("LELEngine\nCopyright LELDev Studio\nInitializing...");

            Game.CreateWindow(2560, 1440, "LELEngine");
            //Load Default Scene
            Game.MainWindow.LoadDefaultScene();

            //Setup scene for debug

            GameObject cam = MonoBahaviour.ActiveScene.CreateGameObject("MainCamera");
            GameObject CameraRig = MonoBahaviour.ActiveScene.CreateGameObject("CameraRig");
            GameObject lightRig = MonoBahaviour.ActiveScene.CreateGameObject("LightRig");
            GameObject light = MonoBahaviour.ActiveScene.CreateGameObject("LDirectional");

            light.transform.SetParent(lightRig.transform);
            light.transform.localRotation = Quaternion.Identity;
            light.transform.localPosition = new Vector3(0, 0, -200);
            light.transform.scale = Vector3.One * 10;
            light.AddComponent<DirectionalLight>();

            MeshRenderer m = light.AddComponent<MeshRenderer>();
            m.materialPath = "Sun.material";
            m.meshPath = "sphere.obj";

            lightRig.transform.position = Vector3.Zero;
            lightRig.transform.rotation = QuaternionHelper.Euler(10, 40, 0);

            CameraRig.AddComponent<Rotator>();
            Camera c = cam.AddComponent<Camera>();
            cam.transform.SetParent(CameraRig.transform);
            CameraRig.transform.position = Vector3.Zero;
            CameraRig.transform.rotation = Quaternion.Identity;
            cam.transform.localPosition = new Vector3(0, 0, -5f);
            cam.transform.localRotation = Quaternion.Identity;
            c.FoV = 70;
            c.farClip = 1000f;
            c.nearClip = 0.1f;

            Lighting.Ambient.Color = OpenTK.Graphics.Color4.LightGreen;
            Lighting.Ambient.Strength = 0.3f;
            Lighting.Directional.Color = OpenTK.Graphics.Color4.AntiqueWhite;
            Lighting.Directional.Strength = 1f;
            Lighting.Specular.Strength = 1f;
            Lighting.Specular.Shine = 32f;

            foreach (var ob in MonoBahaviour.ActiveScene.SceneGameObjects)
            {
                if (ob.name == "tri1")
                {
                    MeshRenderer mr = ob.AddComponent<MeshRenderer>();
                    mr.materialPath = "Cube.material";
                    mr.meshPath = "quad.obj";
                    mr.transform.rotation = QuaternionHelper.Euler(0, 0, 90);
                    mr.transform.scale = Vector3.One * 500;
                    mr.transform.position = new Vector3(0, -10, 10f);
                }
                if (ob.name == "tri2")
                {
                    MeshRenderer mr = ob.AddComponent<MeshRenderer>();
                    mr.materialPath = "AXE.material";
                    mr.meshPath = "axe.obj";
                    mr.transform.position = new Vector3(0, 0, 0);
                    mr.transform.rotation = QuaternionHelper.Euler(0, -90, 0);
                    mr.transform.scale = Vector3.One;
                }
                if (ob.name == "FramerateCounter")
                {
                    FrameRateCounter mr = ob.AddComponent<FrameRateCounter>();
                }
            }

            Game.MainWindow.Run();
        }
    }
