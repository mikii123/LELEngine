using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LELEngine.Navigation;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's actions (<see cref="EditorActions" />): what the main menu bar's items do, run by the menu and, as they
	///     are, by automation. An action asks nothing; the menu's click may ask first (unsaved changes, Force Reserialize).
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PrivateFields

		private EditorActions actions;

		// The object a GameObject action creates under (the hierarchy's context menu); null: the scene's root.
		private Transform createParent;

		#endregion

		#region PublicMethods

		/// <summary>Every built-in action (made at first use: they read the editor's windows and settings).</summary>
		public EditorActions Actions => actions ??= BuildActions();

		/// <summary>
		///     Runs the action at <paramref name="path" />: a built-in one (<see cref="Actions" />) or an editor script's
		///     [MenuItem] method, called at once. False when there is none.
		/// </summary>
		/// <exception cref="InvalidOperationException">It is disabled now, or cannot run without the user's word (the reason).</exception>
		public bool RunAction(string path)
		{
			if (Project == null)
			{
				throw new InvalidOperationException("No project is open.");
			}

			EditorAction action = Actions.Find(path);
			if (action != null)
			{
				if (!action.IsEnabled)
				{
					throw new InvalidOperationException(action.Path + " cannot run now (it is disabled: play mode, prefab mode, nothing selected, ...).");
				}

				action.Run();
				return true;
			}

			MethodInfo method = Extensions.FindMenuMethod(path);
			if (method == null)
			{
				return false;
			}

			try
			{
				method.Invoke(null, null);
			}
			catch (TargetInvocationException e) when (e.InnerException != null)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
			}

			return true;
		}

		/// <summary>Every action: the built-in ones, then the editor scripts' [MenuItem]s.</summary>
		public List<(string path, bool enabled, bool ticked, string shortcut)> ListActions()
		{
			var list = new List<(string, bool, bool, string)>();
			if (Project == null)
			{
				return list;
			}

			foreach (EditorAction action in Actions.All)
			{
				list.Add((action.Path, action.IsEnabled, action.IsChecked, action.Shortcut));
			}

			foreach (string path in Extensions.MenuPaths())
			{
				list.Add((path, true, false, null));
			}

			return list;
		}

		/// <summary>File > New Scene as an action: the unsaved changes as <paramref name="unsaved" /> says.</summary>
		public void NewSceneAction(UnsavedChanges unsaved)
		{
			SettleUnsaved(unsaved, false);
			NewSceneNow();
		}

		/// <summary>Opens the scene at <paramref name="assetPath" /> as an action: the unsaved changes as <paramref name="unsaved" /> says.</summary>
		public void OpenSceneAction(string assetPath, UnsavedChanges unsaved)
		{
			if (!File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				throw new InvalidOperationException("No scene at " + assetPath);
			}

			SettleUnsaved(unsaved, false);
			OpenSceneNow(assetPath);
		}

		/// <summary>Saves the open scene (prefab mode: the prefab), or the scene to <paramref name="assetPath" /> when given.</summary>
		public void SaveSceneAction(string assetPath = null)
		{
			if (IsPlaying)
			{
				throw new InvalidOperationException("Play mode: stop it to save the scene.");
			}

			if (Stage != null)
			{
				if (assetPath != null)
				{
					throw new InvalidOperationException("Prefab mode saves the prefab: leave it to save the scene to a file.");
				}

				SavePrefabStage();
				return;
			}

			if (assetPath != null)
			{
				SaveSceneTo(assetPath);
				return;
			}

			if (string.IsNullOrEmpty(Scene?.Path))
			{
				throw new InvalidOperationException("The scene has no file yet: give it one (scene save <path>).");
			}

			SaveScene();
		}

		/// <summary>
		///     Before the editor closes (automation's quit): the unsaved changes as <paramref name="unsaved" /> says; after it the
		///     close asks nothing.
		/// </summary>
		public void PrepareClose(UnsavedChanges unsaved)
		{
			if (unsaved != UnsavedChanges.Discard)
			{
				SettleUnsaved(unsaved, true);
			}

			closeConfirmed = true;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     What an action that replaces the scene would lose (prefab stages, the scene, with <paramref name="tools" /> the
		///     tools' own documents) is saved, dropped, or (<see cref="UnsavedChanges.Refuse" />) stops it before anything changes.
		///     Prefab stages are left either way.
		/// </summary>
		private void SettleUnsaved(UnsavedChanges unsaved, bool tools)
		{
			bool sceneDirty = !IsPlaying && (stageFrames.Count > 0 ? stageFrames[0].Dirty : SceneDirty);
			var lost = new List<string>();
			if (Stage != null && SceneDirty && !Stage.AutoSave)
			{
				lost.Add("the prefab " + Stage.AssetPath);
			}

			for (int i = stageFrames.Count - 1; i >= 1; i--)
			{
				if (stageFrames[i].Dirty && !stageFrames[i - 1].Stage.AutoSave)
				{
					lost.Add("the prefab " + stageFrames[i - 1].Stage.AssetPath);
				}
			}

			if (sceneDirty)
			{
				lost.Add("the scene " + (EditScenePath ?? "(no file)"));
			}

			List<string> toolFiles = tools ? UnsavedToolFiles() : new List<string>();
			lost.AddRange(toolFiles);
			if (lost.Count > 0 && unsaved == UnsavedChanges.Refuse)
			{
				throw new InvalidOperationException("Unsaved changes in " + string.Join(", ", lost) + ": give --save or --discard.");
			}

			string scenePath = stageFrames.Count > 0 ? stageFrames[0].Path : Scene?.Path;
			if (sceneDirty && unsaved == UnsavedChanges.Save && string.IsNullOrEmpty(scenePath))
			{
				throw new InvalidOperationException("The scene has no file yet: save it first (scene save <path>), or give --discard.");
			}

			while (Stage != null)
			{
				if (SceneDirty && (Stage.AutoSave || unsaved == UnsavedChanges.Save))
				{
					SavePrefabStage();
				}

				ClosePrefabStageNow();
			}

			if (toolFiles.Count > 0)
			{
				if (unsaved == UnsavedChanges.Save)
				{
					if (!SaveToolFiles())
					{
						throw new InvalidOperationException("A tool's document could not be saved (a new UI document asks for its file in its window).");
					}
				}
				else
				{
					DiscardToolFiles();
				}
			}

			if (sceneDirty && unsaved == UnsavedChanges.Save)
			{
				SaveScene();
			}
		}

		private EditorActions BuildActions()
		{
			var all = new EditorActions();

			// File
			all.Add(new EditorAction("File/New Scene", () => NewSceneAction(UnsavedChanges.Refuse)) { Click = NewScene, Shortcut = "Ctrl+N" });
			all.Add(new EditorAction("File/Save", () => SaveSceneAction())
			{
				Click = SaveScene, Label = () => Stage != null ? "Save Prefab" : "Save Scene", Shortcut = "Ctrl+S", Enabled = () => !IsPlaying
			});
			all.Add(new EditorAction("File/Save Scene As...", () => throw new InvalidOperationException("Save Scene As asks for a file: scene save <path>."))
			{
				Click = SaveSceneAs, Shortcut = "Ctrl+Shift+S", Enabled = () => !IsPlaying && Stage == null
			});
			all.Add(new EditorAction("File/Open Script Project", () => Scripts.OpenInIde()) { Enabled = () => Scripts != null, SeparatorBefore = true });
			all.Add(new EditorAction("File/Rebuild Scripts", () => Scripts.RequestBuild()) { Enabled = () => Scripts != null && !IsPlaying });
			all.Add(new EditorAction("File/Build Settings...", () => ShowBuildWindow()) { Shortcut = "Ctrl+Shift+B", SeparatorBefore = true });
			all.Add(new EditorAction("File/Close Project", () =>
			{
				SettleUnsaved(UnsavedChanges.Refuse, true);
				CloseProject();
			})
			{
				Click = () => ConfirmToolChanges(() => LeaveAllStages(() => ConfirmDiscard(CloseProject))), SeparatorBefore = true
			});
			all.Add(new EditorAction("File/Exit", () =>
			{
				PrepareClose(UnsavedChanges.Refuse);
				Window.Close();
			})
			{
				Click = () => Window.Close()
			});

			// Edit (Undo and Redo keep their paths whatever the text says)
			all.Add(new EditorAction("Edit/Undo", UndoLast) { Label = () => Undo.CanUndo ? "Undo " + Undo.UndoName : "Undo", Shortcut = "Ctrl+Z", Enabled = () => Undo.CanUndo });
			all.Add(new EditorAction("Edit/Redo", RedoLast) { Label = () => Undo.CanRedo ? "Redo " + Undo.RedoName : "Redo", Shortcut = "Ctrl+Y", Enabled = () => Undo.CanRedo });
			all.Add(new EditorAction("Edit/Duplicate", DuplicateSelected) { Shortcut = "Ctrl+D", Enabled = () => Selected != null, SeparatorBefore = true });
			all.Add(new EditorAction("Edit/Delete", DeleteSelected) { Shortcut = "Del", Enabled = () => Selected != null });
			all.Add(new EditorAction("Edit/Reload Scripts On Play", () =>
			{
				reloadScriptsOnPlay = !reloadScriptsOnPlay;
				SaveWindowStates();
			})
			{
				Checked = () => reloadScriptsOnPlay, SeparatorBefore = true
			});
			all.Add(new EditorAction("Edit/Project Settings...", () => ShowProjectSettings()) { SeparatorBefore = true });
			all.Add(new EditorAction("Edit/Preferences...", () => ShowPreferences()));

			// Assets
			// Imports what changed on disk now (it also happens by itself when the editor comes to the front).
			all.Add(new EditorAction("Assets/Refresh", ImportChangesNow) { Enabled = () => reserializer == null });
			all.Add(new EditorAction("Assets/Force Reserialize All Assets", ForceReserializeAllAssets)
			{
				Click = AskForceReserializeAllAssets, Enabled = () => reserializer == null && !IsPlaying && Stage == null, SeparatorBefore = true
			});
			// Impostors: of the selected prefab or model, the selected impostor asset, or all of the project's.
			AddImpostorActions(all);

			// GameObject (the hierarchy's context menu creates the same under the clicked object)
			AddCreateActions(all);

			// Window: ticked windows are open; a closed one comes back where it was docked (layout.ini keeps its place).
			all.Add(new EditorAction("Window/Scene", () =>
			{
				if (SceneView.Open) SceneView.Open = false;
				else SceneView.Focus();
			})
			{
				Checked = () => SceneView.Open
			});
			all.Add(new EditorAction("Window/Game", () =>
			{
				if (GameView.Open) GameView.Open = false;
				else GameView.Focus();
			})
			{
				Checked = () => GameView.Open
			});
			all.Add(new EditorAction("Window/Hierarchy", () => hierarchy.Open = !hierarchy.Open) { Checked = () => hierarchy.Open });
			all.Add(new EditorAction("Window/Inspector", () => inspector.Open = !inspector.Open) { Checked = () => inspector.Open });
			all.Add(new EditorAction("Window/Project", () => projectPanel.Open = !projectPanel.Open) { Checked = () => projectPanel.Open });
			all.Add(new EditorAction("Window/Console", () => consolePanel.Open = !consolePanel.Open) { Checked = () => consolePanel.Open });
			all.Add(new EditorAction("Window/Lighting", () => lightingPanel.Open = !lightingPanel.Open) { Checked = () => lightingPanel.Open });
			all.Add(new EditorAction("Window/Project Settings", () =>
			{
				if (projectSettingsWindow.Open) projectSettingsWindow.Open = false;
				else ShowProjectSettings();
			})
			{
				Checked = () => projectSettingsWindow.Open, SeparatorBefore = true
			});
			all.Add(new EditorAction("Window/VFX Graph", () =>
			{
				if (vfxGraphWindow.Open) vfxGraphWindow.Close();
				else OpenVfxGraph(null);
			})
			{
				Checked = () => vfxGraphWindow.Open
			});
			all.Add(new EditorAction("Window/Animator", () =>
			{
				if (animatorWindow.Open) animatorWindow.Close();
				else OpenAnimator((string)null);
			})
			{
				Checked = () => animatorWindow.Open
			});
			all.Add(new EditorAction("Window/Animation", () =>
			{
				if (animationWindow.Open) animationWindow.Close();
				else OpenAnimationWindow(null);
			})
			{
				Checked = () => animationWindow.Open
			});
			all.Add(new EditorAction("Window/Timeline", () =>
			{
				if (timelineWindow.Open) timelineWindow.Close();
				else OpenTimeline((string)null);
			})
			{
				Checked = () => timelineWindow.Open
			});
			all.Add(new EditorAction("Window/Input Manager", () =>
			{
				if (inputManagerWindow.Open) inputManagerWindow.Open = false;
				else OpenInputManager(null);
			})
			{
				Checked = () => inputManagerWindow.Open
			});
			all.Add(new EditorAction("Window/Test Runner", () =>
			{
				if (testRunnerWindow.Open) testRunnerWindow.Open = false;
				else testRunnerWindow.Show();
			})
			{
				Checked = () => testRunnerWindow.Open
			});
			all.Add(new EditorAction("Window/UI Builder", () =>
			{
				if (uiBuilder != null && uiBuilder.IsOpen) uiBuilder.Close();
				else OpenUIBuilder(null);
			})
			{
				Checked = () => uiBuilder != null && uiBuilder.IsOpen
			});
			all.Add(new EditorAction("Window/Audio Studio", () =>
			{
				if (audioStudio != null && audioStudio.IsOpen) audioStudio.Close();
				else OpenAudioStudio(null, null);
			})
			{
				Checked = () => audioStudio != null && audioStudio.IsOpen
			});
			all.Add(new EditorAction("Window/Reset Layout", () =>
			{
				// Every panel open, docked as by default.
				SceneView.Open = GameView.Open = hierarchy.Open = inspector.Open = projectPanel.Open = consolePanel.Open = lightingPanel.Open = true;
				SceneView.Maximized = false;
				resetLayoutRequested = true;
			})
			{
				SeparatorBefore = true
			});
			return all;
		}

		/// <summary>GameObject > ...: objects made under <see cref="createParent" /> (the scene's root from the main menu).</summary>
		private void AddCreateActions(EditorActions all)
		{
			all.Add(new EditorAction("GameObject/Create Empty", () => CreateObject("GameObject", null, createParent)));
			all.Add(new EditorAction("GameObject/Cube", () => CreateModelObject("Cube", DefaultScene.CubeModel, null, createParent)) { SeparatorBefore = true });
			all.Add(new EditorAction("GameObject/Sphere", () => CreateModelObject("Sphere", DefaultScene.SphereModel, null, createParent)));
			all.Add(new EditorAction("GameObject/Quad", () => CreateModelObject("Quad", DefaultScene.QuadModel, null, createParent)));
			all.Add(new EditorAction("GameObject/Navigation/NavMesh Surface", () => CreateObject("NavMesh Surface", go => go.AddComponent<NavMeshSurface>(), createParent))
			{
				SeparatorBefore = true
			});
			all.Add(new EditorAction("GameObject/Navigation/NavMesh Agent", () => CreateModelObject("NavMesh Agent", DefaultScene.CubeModel, go =>
			{
				go.transform.localScale = new Vector3(0.35f, 0.9f, 0.35f);
				NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
				agent.radius = 0.35f;
				agent.height = 1.8f;
				agent.baseOffset = 0.9f;
			}, createParent)));
			all.Add(new EditorAction("GameObject/Navigation/NavMesh Obstacle", () => CreateModelObject("NavMesh Obstacle", DefaultScene.CubeModel, go =>
			{
				NavMeshObstacle obstacle = go.AddComponent<NavMeshObstacle>();
				obstacle.size = new Vector3(2f, 2f, 2f);
			}, createParent)));
			all.Add(new EditorAction("GameObject/Physics 2D/Dynamic Box (Rigidbody2D + BoxCollider2D)", () => CreateModelObject("Box 2D", DefaultScene.CubeModel, go =>
			{
				// Cube.obj spans -1..1, so the scale is the half size; the colliders fit the mesh like Unity's Reset.
				go.transform.localScale = Planar(0.5f, 0.5f, 0.25f);
				go.AddComponent<Rigidbody2D>();
				go.AddComponent<BoxCollider2D>().FitToRenderer();
			}, createParent)));
			all.Add(new EditorAction("GameObject/Physics 2D/Dynamic Circle (Rigidbody2D + CircleCollider2D)", () => CreateModelObject("Circle 2D", DefaultScene.SphereModel, go =>
			{
				go.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
				go.AddComponent<Rigidbody2D>();
				go.AddComponent<CircleCollider2D>().FitToRenderer();
			}, createParent)));
			all.Add(new EditorAction("GameObject/Physics 2D/Static Ground (BoxCollider2D)", () => CreateModelObject("Ground 2D", DefaultScene.CubeModel, go =>
			{
				go.transform.localScale = Planar(5f, 0.25f, 0.25f);
				if (go.GetComponent<MeshRenderer>() is MeshRenderer renderer)
				{
					renderer.IsStatic = true;
				}

				go.AddComponent<BoxCollider2D>().FitToRenderer();
			}, createParent)));
			all.Add(new EditorAction("GameObject/Physics 2D/Dynamic Capsule (Rigidbody2D + CapsuleCollider2D)", () => CreateModelObject("Capsule 2D", DefaultScene.CubeModel, go =>
			{
				go.transform.localScale = Planar(0.25f, 0.5f, 0.25f);
				go.AddComponent<Rigidbody2D>();
				go.AddComponent<CapsuleCollider2D>().FitToRenderer();
			}, createParent)));
			all.Add(new EditorAction("GameObject/Physics 2D/Edge Ground (EdgeCollider2D)", () => CreateObject("Edge 2D", go => go.AddComponent<EdgeCollider2D>(), createParent)));
			all.Add(new EditorAction("GameObject/Physics 2D/Hinge Chain (3 links)", () => CreateObject("Chain Anchor", go =>
			{
				Rigidbody2D anchor = go.AddComponent<Rigidbody2D>();
				anchor.bodyType = RigidbodyType2D.Static;
				Rigidbody2D previous = anchor;
				for (int i = 0; i < 3; i++)
				{
					GameObject link = DefaultScene.Instantiate(Scene, DefaultScene.CubeModel, "Link " + (i + 1), go.transform);
					// Links of 0.25 x 0.6 m hanging end to end; the anchor is in unscaled local space (the mesh top).
					link.transform.localPosition = Planar(0f, -0.3f - 0.6f * i, 0f);
					link.transform.localScale = Planar(0.125f, 0.3f, 0.125f);
					Rigidbody2D body = link.AddComponent<Rigidbody2D>();
					link.AddComponent<BoxCollider2D>().FitToRenderer();
					HingeJoint2D hinge = link.AddComponent<HingeJoint2D>();
					hinge.connectedBody = previous;
					hinge.anchor = new Vector2(0f, 1f);
					previous = body;
				}
			}, createParent))
			{
				SeparatorBefore = true
			});
			all.Add(new EditorAction("GameObject/Visual Effect", () => CreateObject("Visual Effect", go =>
			{
				var effect = go.AddComponent<LELEngine.VFX.VisualEffect>();
				var graphs = AssetDatabase.FindAssets(typeof(LELEngine.VFX.VfxGraph));
				if (graphs.Count > 0)
				{
					effect.visualEffectAsset = LELEngine.VFX.VfxGraph.Load(graphs[0]);
				}
			}, createParent))
			{
				SeparatorBefore = true
			});
			// With the default line material, as the inspector's Add Component gives it.
			all.Add(new EditorAction("GameObject/Trail", () => CreateObject("Trail", go => go.AddComponent<TrailRenderer>().Reset(), createParent)));
			all.Add(new EditorAction("GameObject/Audio/Audio Listener", () => CreateObject("Audio Listener", go => go.AddComponent<LELEngine.Audio.AudioListener>(), createParent)));
			all.Add(new EditorAction("GameObject/Audio/Audio Event Emitter", () => CreateObject("Audio Emitter", go => go.AddComponent<LELEngine.Audio.AudioEventEmitter>(), createParent)));
			all.Add(new EditorAction("GameObject/Audio/Audio Bank Loader", () => CreateObject("Audio Bank Loader", go => go.AddComponent<LELEngine.Audio.AudioBankLoader>(), createParent)));
			all.Add(new EditorAction("GameObject/Audio/Audio Snapshot Trigger", () => CreateObject("Audio Snapshot Trigger", go => go.AddComponent<LELEngine.Audio.AudioSnapshotTrigger>(), createParent)));
			all.Add(new EditorAction("GameObject/Camera", () => CreateObject("Camera", go => go.AddComponent<Camera>(), createParent)) { SeparatorBefore = true });
			all.Add(new EditorAction("GameObject/Directional Light", () => CreateObject("Directional Light", go =>
			{
				go.transform.rotation = QuaternionHelper.LookRotation(new Vector3(-0.45f, -0.75f, 0.35f).Normalized(), Vector3.UnitY);
				go.AddComponent<DirectionalLight>();
			}, createParent)));
			all.Add(new EditorAction("GameObject/Point Light", () => CreateObject("Point Light", go => go.AddComponent<Light>(), createParent)));
			all.Add(new EditorAction("GameObject/Spot Light", () => CreateObject("Spot Light", go =>
			{
				// Shining down at what is in front of it.
				go.transform.rotation = QuaternionHelper.LookRotation(new Vector3(0f, -1f, 0.35f).Normalized(), Vector3.UnitZ);
				go.AddComponent<Light>().Type = LightType.Spot;
			}, createParent)));
		}

		#endregion
	}
}
