using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.UIElements;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     UI test: a small 3D backdrop, a HUD loaded from UI/TestHud.uxml and a control showcase built in code at
	///     runtime (every control, dynamic composition). Run TestGame with ui=1 (exitafter=seconds closes the game).
	/// </summary>
	public static class UITestScene
	{
		#region PublicMethods

		public static void Load(Scene scene, bool animate, float exitAfterSeconds, float dumpTreeAfterSeconds = 0f, float scriptedInputAt = 0f)
		{
			Box(scene, "Floor", "LitGray.material", new Vector3(0f, -0.5f, 0f), new Vector3(12f, 0.5f, 12f));
			Box(scene, "Red", "LitRed.material", new Vector3(-3f, 1f, 2f), new Vector3(1f, 1f, 1f));
			Box(scene, "Blue", "LitBlue.material", new Vector3(2f, 0.75f, -1f), new Vector3(0.75f, 0.75f, 0.75f));
			Box(scene, "Green", "LitGreen.material", new Vector3(4f, 1.5f, 4f), new Vector3(1f, 1.5f, 1f));

			GameObject cameraObject = scene.CreateGameObject("MainCamera");
			cameraObject.transform.position = new Vector3(0f, 6f, -12f);
			cameraObject.transform.rotation = QuaternionHelper.LookRotation((new Vector3(0f, 0.5f, 0f) - cameraObject.transform.position).Normalized(), Vector3.UnitY);
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 55f;

			GameObject light = scene.CreateGameObject("Sun");
			light.transform.rotation = QuaternionHelper.LookRotation(new Vector3(-0.4f, -0.8f, 0.4f).Normalized(), Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Strength = 1.2f;

			Lighting.Environment.AmbientColor = new Color4(0.5f, 0.55f, 0.7f, 1f);
			Lighting.Environment.AmbientStrength = 0.3f;
			Lighting.Environment.BackgroundColor = new Color4(0.16f, 0.2f, 0.28f, 1f);
			Lighting.Shadows.Enabled = true;
			Lighting.GI.Enabled = false;

			// HUD from a UXML asset.
			GameObject hud = scene.CreateGameObject("HUD");
			UIDocument hudDocument = hud.AddComponent<UIDocument>();
			hudDocument.visualTreeAsset = UIAssets.LoadVisualTree("UI/TestHud.uxml");
			hudDocument.sortingOrder = 0;
			HudController hudController = hud.AddComponent<HudController>();
			hudController.Animate = animate;

			// Control showcase composed at runtime.
			GameObject showcase = scene.CreateGameObject("Showcase");
			UIDocument showcaseDocument = showcase.AddComponent<UIDocument>();
			showcaseDocument.sortingOrder = 1;
			UIShowcase controls = showcase.AddComponent<UIShowcase>();
			controls.Animate = animate;
			controls.ExitAfterSeconds = exitAfterSeconds;
			controls.DumpTreeAfterSeconds = dumpTreeAfterSeconds;
			controls.ScriptedInputAt = scriptedInputAt;
		}

		#endregion

		#region PrivateMethods

		private static GameObject Box(Scene scene, string name, string material, Vector3 center, Vector3 halfSize)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, name);
			go.transform.position = center;
			go.transform.localScale = halfSize;
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material(material));
			renderer.ContributesToGI = false;
			return go;
		}

		#endregion
	}

	/// <summary>Data the HUD binds to: a notifying source (bindings update only when a property changes).</summary>
	public sealed class HudModel : INotifyBindablePropertyChanged
	{
		public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

		private string m_Subtitle = "Bound subtitle: data source -> label";
		private float m_Health = 72f;

		public string subtitle
		{
			get => m_Subtitle;
			set
			{
				m_Subtitle = value;
				propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs("subtitle"));
			}
		}

		public float health
		{
			get => m_Health;
			set
			{
				m_Health = value;
				propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs("health"));
			}
		}
	}

	/// <summary>Drives the UXML HUD: fps label, animated bars, button clicks logged, data binding of the model.</summary>
	public sealed class HudController : Behaviour
	{
		public bool Animate = true;
		public HudModel Model { get; } = new HudModel();

		private UIDocument document;
		private Label fpsLabel;
		private ProgressBar health;
		private ProgressBar stamina;
		private float frames;
		private float timer;

		public override void Start()
		{
			document = GetComponent<UIDocument>();
			Bind();
			document.treeRebuilt += _ => Bind();
		}

		private void Bind()
		{
			VisualElement root = document.rootVisualElement;
			root.dataSource = Model;
			fpsLabel = root.Q<Label>("fps");
			health = root.Q<ProgressBar>("health");
			stamina = root.Q<ProgressBar>("stamina");
			foreach (Button button in root.Query<Button>().ToList())
			{
				string name = button.name;
				button.clicked += () => Console.WriteLine("[UI] Clicked " + name);
			}

			Button heal = root.Q<Button>("heal");
			if (heal != null)
			{
				// The bar is bound to the model: changing the model updates the UI.
				heal.clicked += () => Model.health = Math.Min(100f, Model.health + 10f);
			}
		}

		public override void Update()
		{
			frames++;
			timer += Time.deltaTime;
			if (timer >= 0.5f && fpsLabel != null)
			{
				fpsLabel.text = (frames / timer).ToString("0") + " fps";
				frames = 0f;
				timer = 0f;
			}

			if (Animate && stamina != null)
			{
				stamina.value = 50f + 45f * (float)Math.Sin(Time.time * 1.5);
			}
		}
	}

	/// <summary>Plain data object (no change notifications): bindings poll it once per frame.</summary>
	public sealed class ShowcaseSettings
	{
		public float volume = 65f;
		public string playerName = "Player One";
		public bool glow = true;
	}

	/// <summary>Builds a panel with every control through the runtime API and exercises dynamic changes.</summary>
	public sealed class UIShowcase : Behaviour
	{
		public bool Animate = true;
		public float ExitAfterSeconds;
		public ShowcaseSettings Settings { get; } = new ShowcaseSettings();

		private UIDocument document;
		private Label counterLabel;
		private VisualElement spinner;
		private ProgressBar sliderMirror;
		private Slider volumeSlider;
		private int clicks;
		private float elapsed;

		public override void Start()
		{
			document = GetComponent<UIDocument>();
			Build(document.rootVisualElement);
		}

		/// <summary>Prints both documents' trees to the console after this many seconds (0: never).</summary>
		public float DumpTreeAfterSeconds;

		/// <summary>Plays a scripted input sequence (clicks, typing, wheel, hover) starting at this time (0: off).</summary>
		public float ScriptedInputAt;

		private bool dumped;
		private int scriptStep;

		public override void Update()
		{
			elapsed += Time.deltaTime;
			if (Animate && spinner != null)
			{
				spinner.style.rotate = new Rotate(Angle.Degrees(elapsed * 90f));
			}

			if (ScriptedInputAt > 0f && elapsed >= ScriptedInputAt)
			{
				RunScript();
			}

			if (DumpTreeAfterSeconds > 0f && elapsed >= DumpTreeAfterSeconds && !dumped)
			{
				dumped = true;
				UIDebug.verbose = true;
				foreach (UIDocument document in gameObject.scene.FindObjectsOfType<UIDocument>())
				{
					Console.WriteLine("[UI] Tree of " + document.gameObject.Name + ":\n" + UIDebug.DumpTree(document.panel.visualTree));
				}
			}

			if (ExitAfterSeconds > 0f && elapsed >= ExitAfterSeconds)
			{
				Game.Mono?.Close();
			}
		}

		// Scripted input: one action per frame, through the same window events the game host feeds.
		private void RunScript()
		{
			VisualElement root = document.rootVisualElement;
			HudController hud = gameObject.scene.FindObjectOfType<HudController>();
			VisualElement hudRoot = hud?.GetComponent<UIDocument>().rootVisualElement;
			switch (scriptStep++)
			{
				case 0: MoveTo(root.Q<Button>("click-me")); break;
				case 2: Press(); break;
				case 4: Release(); break;
				case 6: MoveTo(root.Q<TextField>("name-field")?.Q<VisualElement>("unity-text-input")); break;
				case 8: Press(); break;
				case 10: Release(); break;
				case 12: Input.Input_TextInput(new OpenTK.Windowing.Common.TextInputEventArgs('!')); break;
				case 13: Input.Input_TextInput(new OpenTK.Windowing.Common.TextInputEventArgs('?')); break;
				case 15: MoveTo(root.Q<ScrollView>()?.contentViewport); break;
				case 17: Input.Input_MouseWheel(new OpenTK.Windowing.Common.MouseWheelEventArgs(0f, -3f)); break;
				case 20: MoveTo(root.Q<DropdownField>()?.Q<VisualElement>(className: "unity-base-popup-field__input")); break;
				case 22: Press(); break;
				case 24: Release(); break;
				case 30: MoveTo(hudRoot?.Q<Button>("attack")); break;
				// Bindings: model -> UI (notifying and polled sources) and UI -> model (two-way slider).
				case 32:
					if (hud != null) hud.Model.subtitle = "Subtitle changed through the model";
					if (hud != null) hud.Model.health = 30f;
					Settings.volume = 20f;
					break;
				case 34: volumeSlider.value = 80f; break;
				case 36:
					Console.WriteLine("[UI] Script done: clicks label = \"" + counterLabel.text + "\", name = \"" + root.Q<TextField>("name-field")?.value + "\", scroll = " + root.Q<ScrollView>()?.scrollOffset + ", dropdown open = " + (root.panel.visualTree.Q<VisualElement>("unity-dropdown-menu") != null) + ", hovered = " + ((hud?.GetComponent<UIDocument>().panel as RuntimePanel)?.topElementUnderPointer));
					Console.WriteLine("[UI] Bindings: subtitle label = \"" + hudRoot?.Q<Label>("subtitle")?.text + "\", health bar = " + hudRoot?.Q<ProgressBar>("health")?.value + ", settings.volume = " + Settings.volume + ", mirror = " + sliderMirror.value + ", bound name = \"" + root.Q<Label>("bound-name")?.text + "\"");
					break;
			}
		}

		private static void MoveTo(VisualElement element)
		{
			if (element == null)
			{
				Console.WriteLine("[UI] Script: element not found");
				return;
			}

			Rect bound = element.worldBound;
			float scale = (element.panel as RuntimePanel)?.scale ?? 1f;
			Vector2 center = bound.center * scale;
			Input.Input_MouseMove(new OpenTK.Windowing.Common.MouseMoveEventArgs(center.X, center.Y, 0f, 0f));
		}

		private static void Press()
		{
			Input.Input_MouseDown(new OpenTK.Windowing.Common.MouseButtonEventArgs(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left, OpenTK.Windowing.GraphicsLibraryFramework.InputAction.Press, 0));
		}

		private static void Release()
		{
			Input.Input_MouseUp(new OpenTK.Windowing.Common.MouseButtonEventArgs(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left, OpenTK.Windowing.GraphicsLibraryFramework.InputAction.Release, 0));
		}

		private void Build(VisualElement root)
		{
			var window = new VisualElement { name = "showcase" };
			window.style.position = Position.Absolute;
			window.style.right = 24f;
			window.style.top = 90f;
			window.style.width = 360f;
			window.style.maxHeight = Length.Percent(70f);
			window.style.backgroundColor = new Color4(0.12f, 0.13f, 0.16f, 0.92f);
			window.style.borderTopLeftRadius = window.style.borderTopRightRadius = window.style.borderBottomLeftRadius = window.style.borderBottomRightRadius = 8f;
			window.style.borderTopWidth = window.style.borderRightWidth = window.style.borderBottomWidth = window.style.borderLeftWidth = 1f;
			window.style.borderTopColor = window.style.borderRightColor = window.style.borderBottomColor = window.style.borderLeftColor = new Color4(1f, 1f, 1f, 0.1f);
			root.Add(window);

			var header = new VisualElement();
			header.style.flexDirection = FlexDirection.Row;
			header.style.alignItems = Align.Center;
			header.style.flexShrink = 0f;
			header.style.paddingLeft = header.style.paddingRight = 12f;
			header.style.paddingTop = header.style.paddingBottom = 8f;
			header.style.borderBottomWidth = 1f;
			header.style.borderBottomColor = new Color4(1f, 1f, 1f, 0.1f);
			var title = new Label("Runtime controls");
			title.style.unityFontStyleAndWeight = FontStyle.Bold;
			title.style.fontSize = 16f;
			title.style.flexGrow = 1f;
			spinner = new VisualElement();
			spinner.style.width = 16f;
			spinner.style.height = 16f;
			spinner.style.backgroundColor = new Color4(0.31f, 0.76f, 0.97f, 1f);
			spinner.style.borderTopLeftRadius = 3f;
			spinner.style.borderBottomRightRadius = 3f;
			header.Add(title);
			header.Add(spinner);
			window.Add(header);

			var scroll = new ScrollView();
			scroll.style.flexGrow = 1f;
			scroll.style.paddingLeft = scroll.style.paddingRight = 8f;
			scroll.style.paddingTop = 6f;
			window.Add(scroll);

			// Buttons and counter.
			var row = new VisualElement();
			row.style.flexDirection = FlexDirection.Row;
			row.style.alignItems = Align.Center;
			var button = new Button(() =>
			{
				clicks++;
				counterLabel.text = "Clicks: " + clicks;
			}) { text = "Click me", name = "click-me" };
			counterLabel = new Label("Clicks: 0");
			counterLabel.style.marginLeft = 8f;
			var disabled = new Button { text = "Disabled" };
			disabled.SetEnabled(false);
			row.Add(button);
			row.Add(disabled);
			row.Add(counterLabel);
			scroll.Add(row);

			// Toggles and radio buttons.
			var toggle = new Toggle("Enable glow") { value = true };
			toggle.RegisterValueChangedCallback(evt => Console.WriteLine("[UI] Toggle " + evt.newValue));
			scroll.Add(toggle);
			var radios = new RadioButtonGroup("Quality", new List<string> { "Low", "Medium", "High" }) { value = 1 };
			radios.RegisterValueChangedCallback(evt => Console.WriteLine("[UI] Quality " + evt.newValue));
			scroll.Add(radios);

			// Data binding: the slider edits Settings.volume (two-way), the bar and a label follow it (to target).
			scroll.dataSource = Settings;
			var slider = new Slider("Volume", 0f, 100f) { showInputField = true, fill = true };
			slider.SetBinding("value", DataBinding.Create("volume"));
			sliderMirror = new ProgressBar { title = "Bound mirror", lowValue = 0f, highValue = 100f };
			sliderMirror.SetBinding("value", DataBinding.Create("volume", BindingMode.ToTarget));
			var boundName = new Label { name = "bound-name" };
			boundName.SetBinding("text", DataBinding.Create("playerName", BindingMode.ToTarget));
			volumeSlider = slider;
			scroll.Add(slider);
			scroll.Add(sliderMirror);
			scroll.Add(boundName);
			scroll.Add(new SliderInt("Lives", 0, 10) { value = 3, pageSize = 1f });
			scroll.Add(new MinMaxSlider("Range", 20f, 70f, 0f, 100f));

			// Text and numbers.
			var text = new TextField("Name") { placeholderText = "Type a name", name = "name-field" };
			text.SetBinding("value", DataBinding.Create("playerName"));
			text.RegisterValueChangedCallback(evt => Console.WriteLine("[UI] Name " + evt.newValue));
			scroll.Add(text);
			scroll.Add(new TextField("Password") { isPasswordField = true, value = "secret" });
			scroll.Add(new IntegerField("Seed") { value = 1234 });
			scroll.Add(new FloatField("Speed") { value = 3.5f });
			scroll.Add(new Vector2Field("Offset") { value = new Vector2(1.5f, -2f) });

			// Choices.
			scroll.Add(new DropdownField("Weapon", new List<string> { "Sword", "Bow", "Staff" }, 1));
			scroll.Add(new EnumField("Mode", FlexDirection.Row));

			// Foldout with a help box and a multiline field.
			var foldout = new Foldout { text = "Advanced" };
			foldout.Add(new HelpBox("Multiline text wraps and the foldout collapses its content.", HelpBoxMessageType.Info));
			foldout.Add(new TextField("Notes") { multiline = true, value = "First line\nSecond line" });
			scroll.Add(foldout);

			// A virtualized list of a thousand items.
			var items = new List<string>();
			for (int i = 0; i < 1000; i++)
			{
				items.Add("Item " + i);
			}

			var list = new ListView(items, 22f, () => new Label(), (element, index) => ((Label)element).text = items[index]);
			list.style.height = 120f;
			list.selectionType = SelectionType.Multiple;
			list.selectionChanged += selection => Console.WriteLine("[UI] Selected " + string.Join(", ", selection));
			list.SetSelection(2);
			scroll.Add(list);

			// Dynamic composition: add and remove rows.
			var dynamicRow = new VisualElement();
			dynamicRow.style.flexDirection = FlexDirection.Row;
			dynamicRow.style.flexWrap = Wrap.Wrap;
			var add = new Button { text = "Add chip" };
			var clear = new Button { text = "Clear" };
			int chipCount = 0;
			add.clicked += () =>
			{
				var chip = new Label("Chip " + ++chipCount);
				chip.style.backgroundColor = new Color4(0.25f, 0.5f, 0.85f, 1f);
				chip.style.borderTopLeftRadius = chip.style.borderTopRightRadius = chip.style.borderBottomLeftRadius = chip.style.borderBottomRightRadius = 10f;
				chip.style.paddingLeft = chip.style.paddingRight = 8f;
				chip.style.marginRight = chip.style.marginBottom = 4f;
				chip.RegisterCallback<ClickEvent>(evt => chip.RemoveFromHierarchy());
				chip.pickingMode = PickingMode.Position;
				dynamicRow.Add(chip);
			};
			clear.clicked += () => dynamicRow.Clear();
			var dynamicButtons = new VisualElement();
			dynamicButtons.style.flexDirection = FlexDirection.Row;
			dynamicButtons.Add(add);
			dynamicButtons.Add(clear);
			scroll.Add(dynamicButtons);
			scroll.Add(dynamicRow);
			for (int i = 0; i < 3; i++)
			{
				add.clickable.SimulateSingleClick(null, 0);
			}
		}
	}
}
