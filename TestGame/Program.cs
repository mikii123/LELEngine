using System.Reflection;
using LELCS;
using LELCS.Model;
using LELEngine;
using OpenTK;
using OpenTK.Graphics;

namespace TestGame
{
	internal class Program
	{
		#region PrivateMethods

		private static void Main(string[] args)
		{
			Game.CreateWindow(800, 600, "LELEngine");

			// Load Default Scene
			Game.Mono.LoadDefaultScene();

			// Setup scene for debug
			GameObject cam = Game.Mono.ActiveScene.CreateGameObject("MainCamera");
			GameObject CameraRig = Game.Mono.ActiveScene.CreateGameObject("CameraRig");
			GameObject lightRig = Game.Mono.ActiveScene.CreateGameObject("LightRig");
			GameObject light = Game.Mono.ActiveScene.CreateGameObject("LDirectional");

			// Setup light
			light.transform.SetParent(lightRig.transform);
			light.transform.localRotation = Quaternion.Identity;
			light.transform.localPosition = new Vector3(0, 0, -200);
			light.transform.scale = Vector3.One * 10;
			light.AddComponent<DirectionalLight>();

			lightRig.transform.position = Vector3.Zero;
			lightRig.transform.rotation = QuaternionHelper.Euler(10, 40, 0);

			// Setup sun sphere
			MeshRenderer m = light.AddComponent<MeshRenderer>();
			m.SetMaterial("Sun.material");
			m.SetMesh("sphere.obj");

			// Setup camera
			CameraRig.AddComponent<CameraController>();
			Camera c = cam.AddComponent<Camera>();
			cam.transform.SetParent(CameraRig.transform);
			CameraRig.transform.position = Vector3.Zero;
			CameraRig.transform.rotation = QuaternionHelper.Euler(0, 180, 0);
			cam.transform.localPosition = new Vector3(0, 4, -15f);
			cam.transform.localRotation = Quaternion.Identity;
			c.FoV = 70;
			c.FarClip = 1000f;
			c.NearClip = 0.1f;

			// Setup lighting
			Lighting.Ambient.Color = Color4.LightGreen;
			Lighting.Ambient.Strength = 0.3f;
			Lighting.Directional.Color = Color4.AntiqueWhite;
			Lighting.Directional.Strength = 1f;
			Lighting.Specular.Strength = 1f;
			Lighting.Specular.Shine = 32f;

			foreach (GameObject go in Game.Mono.ActiveScene.SceneGameObjects)
			{
				if (go.Name == "Floor")
				{
					MeshRenderer mr = go.AddComponent<MeshRenderer>();
					mr.SetMaterial("Cube.material");
					mr.SetMesh("quad.obj");
					mr.transform.rotation = QuaternionHelper.Euler(0, 0, 90);
					mr.transform.scale = Vector3.One * 500;
					mr.transform.position = new Vector3(0, -10, 10f);
				}
			}

			// Create ShipGraphics
			GameObject shipGraphics = Game.Mono.ActiveScene.CreateGameObject("ShipGraphics");
			shipGraphics.transform.rotation = Quaternion.Identity;
			shipGraphics.transform.scale = Vector3.One * 5f;

			//Create ShipController
			GameObject shipController = Game.Mono.ActiveScene.CreateGameObject("ShipController");
			shipController.transform.rotation = Quaternion.Identity;

			shipGraphics.transform.SetParent(shipController.transform);
			shipGraphics.transform.localPosition = Vector3.Zero;
			shipGraphics.transform.localRotation = QuaternionHelper.Euler(-90, 180, 0);

			MeshRenderer shipGraphicsMR = shipGraphics.AddComponent<MeshRenderer>();
			shipGraphicsMR.SetMaterial("Axe.material");
			shipGraphicsMR.SetMesh("axe.obj");

			PlayerShipControl shipControllerPSC = shipController.AddComponent<PlayerShipControl>();

			Game.Mono.InitializeECSScope(Assembly.GetExecutingAssembly());
			ECSManager manager = Game.Mono.ECSManager;
			ECSEntity ecsEntity = manager.CreateEntity();
			manager.SetComponent(ecsEntity, new FrameRateCounterComponent());

			Game.Mono.Run();
			// Main function is frozen until game window closes
		}

		#endregion
	}
}
