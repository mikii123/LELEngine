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

        Game.CreateWindow(800, 600, "LELEngine");
        
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

        CameraRig.AddComponent<CameraController>();
        Camera c = cam.AddComponent<Camera>();
        cam.transform.SetParent(CameraRig.transform);
        CameraRig.transform.position = Vector3.Zero;
        CameraRig.transform.rotation = QuaternionHelper.Euler(0, 180, 0);
        cam.transform.localPosition = new Vector3(0, 4, -15f);
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
            if (ob.name == "floor")
            {
                MeshRenderer mr = ob.AddComponent<MeshRenderer>();
                mr.materialPath = "Cube.material";
                mr.meshPath = "quad.obj";
                mr.transform.rotation = QuaternionHelper.Euler(0, 0, 90);
                mr.transform.scale = Vector3.One * 500;
                mr.transform.position = new Vector3(0, -10, 10f);
            }
            if (ob.name == "FramerateCounter")
            {
                FrameRateCounter mr = ob.AddComponent<FrameRateCounter>();
            }
        }

        // Create ShipGraphics
        GameObject shipGraphics = MonoBahaviour.ActiveScene.CreateGameObject("ShipGraphics");
        shipGraphics.transform.rotation = Quaternion.Identity;
        shipGraphics.transform.scale = Vector3.One * 5;

        //Create ShipController
        GameObject shipController = MonoBahaviour.ActiveScene.CreateGameObject("ShipController");
        shipController.transform.rotation = Quaternion.Identity;

        shipGraphics.transform.SetParent(shipController.transform);
        shipGraphics.transform.localPosition = Vector3.Zero;
        shipGraphics.transform.localRotation = QuaternionHelper.Euler(-90, 180, 0);

        MeshRenderer shipGraphicsMR = shipGraphics.AddComponent<MeshRenderer>();
        shipGraphicsMR.materialPath = "Axe.material";
        shipGraphicsMR.meshPath = "axe.obj";

        PlayerShipControl shipControllerPSC = shipController.AddComponent<PlayerShipControl>();

        Game.MainWindow.Run();
    }
}
