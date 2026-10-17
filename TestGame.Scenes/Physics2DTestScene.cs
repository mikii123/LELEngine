using System;
using System.Collections.Generic;
using LELEngine;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Box2D test: a static ground and walls, a stack of boxes, a bouncing ball, a hinge pendulum and chain,
	///     capsules, a spring, a kinematic platform, a trigger zone counting what enters it, raycasts and a scripted
	///     check of the result.
	///     Built with the same mesh assets as the other test scenes, in the XY plane (the camera looks down -Z).
	/// </summary>
	public static class Physics2DTestScene
	{
		#region PublicMethods

		public static void Load(Scene scene, bool animate)
		{
			EnvironmentSettings environment = scene.Settings.Environment;
			environment.AmbientColor = new Color4(0.55f, 0.6f, 0.75f, 1f);
			environment.AmbientStrength = 0.12f;
			environment.SpecularStrength = 0.2f;
			environment.SpecularShine = 32f;
			environment.BackgroundColor = new Color4(0.16f, 0.3f, 0.6f, 1f);
			scene.Settings.GI.Enabled = false;
			scene.Settings.Shadows.Distance = 30f;

			GameObject cameraObject = scene.CreateGameObject("Main Camera");
			cameraObject.transform.position = new Vector3(0f, 4f, -16f);
			cameraObject.transform.rotation = QuaternionHelper.LookRotation(Vector3.UnitZ, Vector3.UnitY);
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 50f;

			GameObject light = scene.CreateGameObject("Directional Light");
			light.transform.rotation = QuaternionHelper.LookRotation(new Vector3(0.3f, -0.7f, 0.6f).Normalized(), Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Strength = 1.2f;

			// Static world: ground, walls and a slope made of edges.
			StaticBox(scene, "Ground", new Vector2(0f, -0.25f), new Vector2(20f, 0.5f), "LitGray.material");
			StaticBox(scene, "LeftWall", new Vector2(-10.25f, 3f), new Vector2(0.5f, 7f), "LitGray.material");
			StaticBox(scene, "RightWall", new Vector2(10.25f, 3f), new Vector2(0.5f, 7f), "LitGray.material");
			GameObject slope = scene.CreateGameObject("Slope");
			EdgeCollider2D edge = slope.AddComponent<EdgeCollider2D>();
			edge.points = new List<Vector2> { new Vector2(-9.5f, 5f), new Vector2(-6f, 3.5f), new Vector2(-3.5f, 3f) };

			// A stack of dynamic boxes.
			for (int i = 0; i < 5; i++)
			{
				DynamicBox(scene, "Stack" + i, new Vector2(3f, 0.5f + i * 1.02f), new Vector2(1f, 1f), "LitBlue.material");
			}

			// A bouncy ball rolling down the slope.
			GameObject ball = DynamicCircle(scene, "Ball", new Vector2(-8.5f, 7f), 0.5f, "LitYellow.material");
			ball.GetComponent<CircleCollider2D>().sharedMaterial = new PhysicsMaterial2D("Bouncy") { bounciness = 0.6f, friction = 0.3f };
			ball.GetComponent<Rigidbody2D>().velocity = new Vector2(2f, 0f);

			// A pendulum: a box hanging from a hinge on a static anchor.
			GameObject pendulum = DynamicBox(scene, "Pendulum", new Vector2(-2f, 5f), new Vector2(0.4f, 0.4f), "LitRed.material");
			HingeJoint2D hinge = pendulum.AddComponent<HingeJoint2D>();
			hinge.autoConfigureConnectedAnchor = false;
			hinge.anchor = Vector2.Zero;
			hinge.connectedAnchor = new Vector2(0f, 8f);
			pendulum.GetComponent<Rigidbody2D>().velocity = new Vector2(4f, 0f);

			// A hinge chain of five links hanging from a static anchor, swinging from a sideways start.
			GameObject chainAnchor = scene.CreateGameObject("ChainAnchor");
			chainAnchor.transform.position = new Vector3(1f, 8f, 0f);
			Rigidbody2D anchorBody = chainAnchor.AddComponent<Rigidbody2D>();
			anchorBody.bodyType = RigidbodyType2D.Static;
			Rigidbody2D previousLink = anchorBody;
			for (int i = 0; i < 5; i++)
			{
				GameObject link = DynamicBox(scene, "Link" + i, new Vector2(1.3f + 0.6f * i, 8f), new Vector2(0.6f, 0.25f), "LitRed.material");
				HingeJoint2D chainHinge = link.AddComponent<HingeJoint2D>();
				chainHinge.connectedBody = previousLink;
				// Anchors are in unscaled local space: the left end of the mesh.
				chainHinge.anchor = new Vector2(-1f, 0f);
				previousLink = link.GetComponent<Rigidbody2D>();
			}

			// Capsules falling onto the stack's neighbourhood.
			for (int i = 0; i < 2; i++)
			{
				GameObject capsule = SceneModels.Instantiate(scene, SceneModels.Cube, "Capsule" + i);
				capsule.transform.position = new Vector3(5.5f + i * 1.2f, 6f + i * 2f, 0f);
				capsule.transform.localScale = new Vector3(0.25f, 0.5f, 0.25f);
				MeshRenderer capsuleRenderer = capsule.GetComponent<MeshRenderer>();
				capsuleRenderer.SetMaterials(SceneModels.Material("LitGreen.material"));
				capsule.AddComponent<Rigidbody2D>();
				capsule.AddComponent<CapsuleCollider2D>().FitToRenderer();
			}

			// A box on a spring.
			GameObject springBox = DynamicBox(scene, "SpringBox", new Vector2(7f, 4f), new Vector2(0.8f, 0.8f), "LitGreen.material");
			SpringJoint2D spring = springBox.AddComponent<SpringJoint2D>();
			spring.autoConfigureConnectedAnchor = false;
			spring.connectedAnchor = new Vector2(7f, 7f);
			spring.autoConfigureDistance = false;
			spring.distance = 2f;
			spring.frequency = 1.5f;
			spring.dampingRatio = 0.2f;

			// A kinematic platform moving back and forth.
			GameObject platform = DynamicBox(scene, "Platform", new Vector2(-6f, 1f), new Vector2(3f, 0.3f), "LitWhite.material");
			platform.GetComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
			platform.AddComponent<PlatformMover>();
			DynamicBox(scene, "Rider", new Vector2(-6f, 1.8f), new Vector2(0.6f, 0.6f), "LitWhite.material");

			// A trigger zone that counts what enters it.
			GameObject zone = scene.CreateGameObject("Zone");
			zone.transform.position = new Vector3(3f, 0.75f, 0f);
			BoxCollider2D zoneCollider = zone.AddComponent<BoxCollider2D>();
			zoneCollider.size = new Vector2(4f, 1.5f);
			zoneCollider.isTrigger = true;
			zone.AddComponent<TriggerCounter>();

			GameObject harness = scene.CreateGameObject("PhysicsHarness");
			harness.AddComponent<Physics2DHarness>().Animate = animate;
		}

		#endregion

		#region PrivateMethods

		private static GameObject StaticBox(Scene scene, string name, Vector2 center, Vector2 size, string material)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, name);
			go.transform.position = new Vector3(center.X, center.Y, 0f);
			// cube.obj spans -1..1: half the size as the scale, and the collider fits the mesh.
			go.transform.localScale = new Vector3(size.X * 0.5f, size.Y * 0.5f, 0.5f);
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material(material));
			renderer.IsStatic = true;
			go.AddComponent<BoxCollider2D>().FitToRenderer();
			return go;
		}

		private static GameObject DynamicBox(Scene scene, string name, Vector2 center, Vector2 size, string material)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, name);
			go.transform.position = new Vector3(center.X, center.Y, 0f);
			// cube.obj spans -1..1: half the size as the scale, and the collider fits the mesh.
			go.transform.localScale = new Vector3(size.X * 0.5f, size.Y * 0.5f, 0.5f);
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material(material));
			go.AddComponent<Rigidbody2D>();
			go.AddComponent<BoxCollider2D>().FitToRenderer();
			return go;
		}

		private static GameObject DynamicCircle(Scene scene, string name, Vector2 center, float radius, string material)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Sphere, name);
			go.transform.position = new Vector3(center.X, center.Y, 0f);
			// sphere.obj has radius 1.
			go.transform.localScale = new Vector3(radius, radius, radius);
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material(material));
			go.AddComponent<Rigidbody2D>();
			go.AddComponent<CircleCollider2D>().FitToRenderer();
			return go;
		}

		#endregion
	}

	/// <summary>Moves a kinematic platform left and right with MovePosition.</summary>
	public sealed class PlatformMover : Behaviour
	{
		public float Amplitude = 2.5f;
		public float Speed = 1f;
		private Rigidbody2D body;
		private Vector2 origin;

		public override void Start()
		{
			body = GetComponent<Rigidbody2D>();
			origin = body.position;
		}

		public override void FixedUpdate()
		{
			if (body != null)
			{
				body.MovePosition(origin + new Vector2(MathF.Sin(Time.fixedTime * Speed) * Amplitude, 0f));
			}
		}
	}

	/// <summary>Counts trigger enters / exits and logs them.</summary>
	public sealed class TriggerCounter : Behaviour
	{
		public int Enters;
		public int Exits;
		public int Stays;

		public override void OnTriggerEnter2D(Collider2D other)
		{
			Enters++;
			Debug.Log($"[Physics2D] Trigger enter: {other.gameObject.Name} (total {Enters})");
		}

		public override void OnTriggerStay2D(Collider2D other)
		{
			Stays++;
		}

		public override void OnTriggerExit2D(Collider2D other)
		{
			Exits++;
			Debug.Log($"[Physics2D] Trigger exit: {other.gameObject.Name} (total {Exits})");
		}
	}

	/// <summary>Logs collision callbacks and queries while the scene runs; reports a summary after a few seconds.</summary>
	public sealed class Physics2DHarness : Behaviour
	{
		public bool Animate = true;
		public float ReportAfter = 3f;
		private int collisionEnters;
		private int collisionStays;
		private bool reported;
		private readonly List<RaycastHit2D> hits = new List<RaycastHit2D>();

		public override void Start()
		{
			foreach (Rigidbody2D body in gameObject.scene.FindObjectsOfType<Rigidbody2D>())
			{
				body.gameObject.AddComponent<CollisionLogger>().Harness = this;
			}

			Debug.Log($"[Physics2D] World: {gameObject.scene.Physics2D.bodyCount} bodies, {gameObject.scene.Physics2D.colliderCount} colliders, {gameObject.scene.Physics2D.jointCount} joints");
		}

		internal void CountCollision(bool enter)
		{
			if (enter) collisionEnters++;
			else collisionStays++;
		}

		public override void Update()
		{
			if (reported || Time.time < ReportAfter)
			{
				return;
			}

			reported = true;
			Scene scene = gameObject.scene;
			var summary = new System.Text.StringBuilder();
			summary.AppendLine($"[Physics2D] Report at {Time.time:0.00} s: {collisionEnters} collision enters, {collisionStays} stays");
			foreach (Rigidbody2D body in scene.FindObjectsOfType<Rigidbody2D>())
			{
				summary.AppendLine($"  {body.gameObject.Name}: position {body.position.X:0.00}, {body.position.Y:0.00}  rotation {body.rotation:0.0}  velocity {body.velocity.Length:0.00}  sleeping {body.IsSleeping()}");
			}

			TriggerCounter zone = scene.FindObjectOfType<TriggerCounter>();
			if (zone != null)
			{
				summary.AppendLine($"  Zone: {zone.Enters} enters, {zone.Exits} exits, {zone.Stays} stay steps");
			}

			RaycastHit2D hit = Physics2D.Raycast(new Vector2(3f, 10f), new Vector2(0f, -1f));
			summary.AppendLine($"  Raycast down over the stack: {hit}");
			int count = Physics2D.Raycast(new Vector2(-12f, 0.25f), Vector2.UnitX, ContactFilter2D.NoFilter(), hits, 30f);
			summary.Append($"  Raycast along the ground: {count} hits:");
			foreach (RaycastHit2D h in hits)
			{
				summary.Append(" " + h.collider.gameObject.Name);
			}

			summary.AppendLine();
			Collider2D[] overlaps = Physics2D.OverlapCircleAll(new Vector2(3f, 1f), 2f);
			summary.Append($"  OverlapCircle at the stack: {overlaps.Length} colliders:");
			foreach (Collider2D c in overlaps)
			{
				summary.Append(" " + c.gameObject.Name);
			}

			summary.AppendLine();
			RaycastHit2D cast = Physics2D.CircleCast(new Vector2(3f, 12f), 0.5f, new Vector2(0f, -1f));
			summary.AppendLine($"  CircleCast down over the stack: {cast}");
			Debug.Log(summary.ToString().TrimEnd());
		}
	}

	/// <summary>Counts OnCollision callbacks on a body.</summary>
	public sealed class CollisionLogger : Behaviour
	{
		internal Physics2DHarness Harness;
		private bool loggedEnter;

		public override void OnCollisionEnter2D(Collision2D collision)
		{
			Harness?.CountCollision(true);
			if (!loggedEnter)
			{
				loggedEnter = true;
				ContactPoint2D contact = collision.contactCount > 0 ? collision.GetContact(0) : default;
				Debug.Log($"[Physics2D] {gameObject.Name} hit {collision.gameObject.Name}: {collision.contactCount} contacts, normal {contact.normal.X:0.00},{contact.normal.Y:0.00}, relative velocity {collision.relativeVelocity.Length:0.00}");
			}
		}

		public override void OnCollisionStay2D(Collision2D collision)
		{
			Harness?.CountCollision(false);
		}
	}
}
