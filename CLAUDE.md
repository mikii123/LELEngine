# LELEngine notes

## Building and running on this machine

- Since 2026-10-20 the `dotnet` on PATH is the .NET 10 SDK (10.0.401, `C:\Program Files\dotnet`): plain `dotnet build`
  works and built executables find the runtime. `C:\Users\Admin\.dotnet` holds no SDK any more. The older notes
  (NETSDK1045, `DOTNET_ROOT=C:\Users\Admin\.dotnet`) apply only on a machine whose PATH still has an older SDK.
- Debug outputs land in `<project>/bin/Debug/net10.0/` (Release in `bin/Release/net10.0/`). Shader library files
  are copied to `bin/.../Shaders/Engine/...` at build time. Python is not installed; use PowerShell with
  `System.Drawing` for image work (BMP to PNG, cropping).
- GPUs: the C:\NewFrontier machine has only an AMD Radeon 780M (OpenGL 4.6; the volumetrics and lights measurements of
  2026-11 are from it); other notes measured on an NVIDIA RTX 3080 Ti, an RTX 5070 Ti or an Intel iGPU, as they say.
  On the NVIDIA card small test scenes run at 1000+ fps with vsync off, so
  frame-count based automation (`exitframe`, `dumpframe`) covers well under a second: prefer `exitseconds=S` and
  `dump=1 dumpafter=S` for anything time dependent.
- Never run `git commit` or `git push`; the user does that.
- `dotnet publish LELEngine.Editor -c Release -r <rid>` gives an editor ready to build games: the player is published
  by its own `dotnet publish` processes (target `PublishPlayer`) for `PlayerRuntimeIdentifier` (default the editor's
  RID) twice: framework-dependent into `Player/`, self-contained into `PlayerSelfContained/`
  (`PlayerSelfContained=false` leaves it out; no RID: `Player/` portable, no self-contained player), plus
  `LELEngine.Editor.Console.exe` on Windows. `dotnet build` copies only the framework-dependent build output
  (`CopyPlayer`, without `bin/.../<rid>/` and `publish/`); a development editor publishes `PlayerSelfContained/` from
  the sources (path in the `LELEngine.PlayerProject` assembly metadata, absent when publishing) the first time a build
  asks for it and again when `Player/` changed (`PlayerSelfContained.stamp`). Project setting `BuildSelfContained`
  (Project.json, Build tab "Self-contained", batch `-selfContained [true|false]`) picks the player (`PlayerFiles`);
  a build deletes the other player's files from the output first. `GameBuilder` renames the player's app host
  (`LELEngine.Player.exe`, or `LELEngine.Player` for a Linux / macOS player) to the game's name.
- The editor starts with the project chooser in its own window (`LauncherWindow`) unless the command line names a
  project (`project=`, `newproject=`, `-projectPath`): automated runs must pass one. File > Close Project hides the
  editor's window and shows the chooser (same GL context: engine caches stay valid). The editor window's place and
  maximized state are in `%APPDATA%\LELEngine\Editor\window.json`, not used by automated runs (`focus=0`,
  `exitframe=`, `screenshot=`).
- The editor compiles a project's scripts with the `dotnet` on PATH: for automated editor runs on a project with
  scripts, put `C:\Users\Admin\.dotnet` first on PATH too, otherwise every script build fails with NETSDK1045.
- The GL context is core forward-compatible: legacy GLSL (`gl_FragColor`, `texture2D`, `attribute` / `varying`)
  does not compile. Intel's compiler warns "barrier call inside control flow" for a `barrier()` in a loop
  (`RadianceCacheSH.shader` unrolls its reduction for that).

## Models and meshes

- Every mesh file is a model (`LELEngine/Model`): `ModelAssets.Load(path)` gives a `Model` (node hierarchy, meshes
  as sub-assets named after their node, material slots), `Model.Instantiate(scene, parent)` builds GameObjects with
  MeshRenderers. Code-built scenes use it too: `DefaultScene.Instantiate` / `EditorApplication.CreateModelObject` in
  the editor, `SceneModels` in `TestGame.Scenes` (full asset paths: `Meshes/cube.obj`, `Materials/X.material`).
- Backends: `ObjModelBackend` (built into the engine, .obj + .mtl, works without the editor, e.g. in TestGame) and
  `AssimpModelBackend` (editor, Silk.NET.Assimp: fbx, gltf/glb, dae, 3ds, blend, ply, stl), registered in the
  editor's `Program.Main` with `ModelMaterialExtractor` (writes `<name>.material` next to the model with the engine's
  `Engine/LitTextured.shader` / `Engine/LitTransparent.shader`, embedded textures as image files next to it too,
  never overwrites; only materials a mesh uses, as Assimp adds an unused default material to every glTF file).
  Since 2026-10-22 both are recorded by GUID in the model's meta (`ModelImporter.materialAssets` / `textureAssets`,
  Unity's external object remap, left out of the import stamp): a renamed or moved material or texture stays the
  model's (the next import finds it, no duplicate). What older imports put in `Materials/` and `Textures/` moves next
  to the model on the next import (extractor layout 3 in the stamp makes every model import once); emptied folders go.
  Unnamed embedded images (glTF) are named `<model> Texture <n>`, so models in one folder don't share them.
- The lit shaders (`Lit`, `LitTextured`, `LitTransparent`) are engine shaders in `LELEngine/ShaderLibrary/Engine`
  (the GI and shadow contracts live there); new projects get no copies. A material naming a project copy that no
  longer exists (`Shaders/Lit.shader`) loads `Engine/Lit.shader` (`AssetDatabase.EngineShaderFallback`). Imports cache in `Library/ModelCache`
  (`ModelAssets.CacheFolder`, set when a project opens); player builds write the imported binary (`LELMODEL`
  magic) under the model's own file name, so the player never needs Assimp.
- `LitTextured`: maps, workflows and what the GI reads of them are in "LitTextured: workflows, maps, GI surfaces"
  below. `Material.SetUniforms` binds the neutral default of every texture the shader declares and the material lacks,
  and `OpaquePass` does the same (`Material.SetDefaultTextures`, since 2026-10-28 also the declared defaults of the
  non-standard numbers) for GPU-driven buckets without a material: an unbound sampler read whatever an earlier draw
  left on its unit (2026-10-22).
- `Mesh` is indexed (`Vertices`, `Indices`, `SubMeshes`); `GetTrianglePositions()` serves CPU queries (picking,
  navmesh, SDF build). `MeshRenderer` has one material per sub-mesh (`Materials`, `GetMaterial(i)` repeats the last
  slot); a renderer can be in both the opaque and the transparent list (`HasOpaqueParts` / `HasTransparentParts`),
  every pass draws its own sub-meshes (`RendererPart` in `GpuScene`: one GPU instance per opaque sub-mesh).
- FBX units: `UnitScaleFactor` from the scene metadata is applied by the import (Assimp does not); verified with
  Unity's SkyCar.fbx (2.32 x 1.45 x 4.47 m). Node matrices from Silk.NET are row-major with the translation in the
  4th column: transpose before `Matrix4x4.Decompose`.
- Test hook: `addmodel=N;Models/Car.fbx|M;...` instantiates model files at frames N, M.

## Layers and camera stack

- `GameObject.layer` (0..31); `LayerMask` lives in `LELEngine/Mono/Layers.cs`: built-in names 0..7, user names 8..31
  in `ProjectSettings.Layers` (Project Settings > Layers; `LayerMask.LayerToName` reads the live settings). Fields of
  type `LayerMask` draw as a mask dropdown (`FieldDrawer`).
- `Camera`: `RenderType` Base / Overlay, `CullingMask`, `ClearDepth` (overlay), `Stack` and `LightingCamera` (base;
  drawn by `CameraInspector`). `SceneManager.MainCamera` = first enabled base camera (hosts render it with its stack);
  `Camera.main` = that stack's lighting camera (null `LightingCamera` = the base camera).
- `Renderer.RenderFrame` renders the stack: passes by `RenderPass.Scope` (Frame once with the lighting camera's
  context: VFX simulation, shadows, SDF, surface cache, radiance cache, voxel GI; then per camera EveryCamera:
  opaque, VFX opaque, transparent; LightingCamera: prepass, screen-space GI, callbacks, debug; BaseCamera: sky; then
  Final: post-process, UI). One pass over the renderers splits them per camera by mask; `GpuScene` is built once from
  all cameras' opaque renderers, each instance holds its layer bit (`refs.z`), `InstanceCull.shader` tests the
  `layerMask` uniform (uint: `SetUint`). `EnsureCameraCulled` re-culls only when view-projection or mask change.
- GI: only the lighting camera's view reads the screen-space GI (`Lighting.ScreenGIView`); other cameras get
  `giResolveMode` 0 (voxel cones) or 2 (Lumen radiance cache probes, `GetProbeIndirectLighting` in
  `VolumeLighting.glsl`, SH sampler on unit 17 `OpaqueProbeSHTextureUnit` so no 2D sampler shares its unit). Shadow
  casters and GI contributors are the lighting camera's layers. VFX: `VfxInstance.Layer` (set by `VisualEffect`),
  depth for collisions captured from the lighting camera only.

## Editor scripts (custom inspectors, editor windows, menu items)

- Public API in `LELEngine.Editor/Extensions`: `Editor` + `[CustomEditor]`, `EditorWindow`, `[MenuItem]`,
  `EditorGUILayout`, `Undo`, `Selection`; the internal host is `EditorExtensions` (`EditorApplication.Extensions`).
  The editor's own main window class is `MainWindow` (the name `EditorWindow` belongs to the public API).
- Scripts in any `Editor` folder under `Assets` build into `Game.Editor.dll` through a second generated project
  (`<Name>.Editor.csproj`, ProjectReference to `<Name>.csproj`, which excludes `Assets\**\Editor\**` and
  `Assets\**\Tests\**`; Tests folders are the third project, see "Unit tests of games"). Each project has its own obj
  folder (`Library/obj/<project>/`). Player builds compile only the game project.
- `Game.Editor.dll` loads into the same collectible `GameLoadContext` as `Game.dll` and is not registered in
  `TypeRegistry` (only temporarily while a window state is restored). Everything holding a type of it must be dropped
  in `EditorExtensions.OnDomainUnloading` (called from `EditorApplication.ClearTypeCaches`). `GameLoadContext` must
  not keep its own list of the assemblies it loaded: that kept the old context alive (leak warning after every
  reload); it resolves Game.dll for Game.Editor.dll through its `Assemblies`.
- Batch mode (since 2026-10-22) builds the editor project too when the project has editor scripts, loads both
  assemblies into one `ScriptDomain.GameLoadContext` (plain `Assembly.Load(byte[])` gives each its own anonymous
  context, so Game.Editor.dll could not resolve Game.dll) and lets `-executeMethod` find types of Game.Editor.dll
  (`BatchMode.editorScripts`; not registered in `TypeRegistry`). Since 2026-10-26 `-executeMethod` and `-listen` run
  inside a whole editor without UI (`BatchEditor`, see "Editor automation"): editor windows, menus and
  `EditorAutomation` work there; a plain `-build` keeps the light path.
- Whether the built scripts are current (editor start, batch mode; `ScriptDomain.IsAssemblyCurrent`): a successful
  build records the scripts it compiled next to its assemblies (`Game.dll.sources`, `Game.Editor.dll.sources`: a
  SHA-256 of every .cs under Assets with its size and modification time, taken when the build starts), and the build is
  current while the scripts give the same value and the generated projects are older than it (2026-10-25). Before, only
  the newest script time counted, so a deleted, renamed or moved script left the old types loaded ("Scripts are up to
  date"). A build without the record (older engines, an IDE build) is built again once. Running editors also rebuild
  on the file watcher's events; `dotnet build` itself drops deleted files (the SDK hashes its Compile items).
- Leak report: a context that survives its unload check (`ScriptDomain.VerifyUnload`, 40 GC passes in the
  background) is traced on the main thread (`ScriptDomain.Update` -> `LeakTracer`, `LELEngine.Editor/Scripting`):
  breadth-first from the static fields of the engine and the assemblies referencing it (closed generic base classes
  of their types included, so per-type pools like `EventBase<T>.pool` are seen), the editor instance and the
  runtime's hooks (AppContext events / data store, AppDomain, AssemblyLoadContext events, TimerQueue, thread pool
  queues, Console, the main thread), stopping at objects, types, members and delegates of the old assembly; a reverse
  walk gives every root its own shortest path. One warning entry (`ConsoleLog.Add`, all lines colored), e.g.
  `UIRuntime.frameDocuments[0] (UIDocument on 'Async').gameObject (GameObject 'Async').components[1] -> AsyncProbe`.
  Not visible to it: thread stacks (a script's running thread), GC handles, native callbacks, libraries' statics (not
  read: their static constructors could fail when run early); the warning then says so. ~60 ms on the probe project.
- Open windows are saved (type, ImGui id, serialized fields) before a reload, before play mode and as they change
  (`Library/EditorWindows.txt`), and restored on the next frame. Their capture runs before the scene unloads, since
  references to scene objects are only written for objects of the current scene.
- User callbacks run guarded: an exception is logged once per message, `ImGuiP.ErrorRecoveryTryToRecoverState`
  (with ImGui's own assert, log and tooltip switched off) closes the scopes left open, and an unbalanced
  Begin/Push is reported as an error.
- Test hook: `menuitem=N;Tools/Item|M;Window/Other` runs `[MenuItem]`s at frames N, M (with `touchscript=` for a
  reload, `playframe=`/`stopframe=` for play mode and `buildgame=` for a player build).

## Unit tests of games (Tests folders, Test Runner; 2026-10-29)

- `LELEngine.TestFramework` (new project, namespace `LELEngine.Testing`, referenced by the editor only, never by the
  player): NUnit-style attributes (`Attributes.cs`), `Assert` / `CollectionAssert` / `StringAssert` (`Assert.cs`, every
  failure through `Assert.Failed` -> `TestRun.OnAssertionFailing` for Break on Failure), `LogAssert`, `TestContext`
  (waits, output, `TemporaryDirectory`), `TestDiscovery` (`TestTree.cs`: `TestNode` tree Root > Namespace > Fixture >
  Method > Case, source lines from the portable PDB; async methods: the state machine's MoveNext), `TestRun`
  (`TestRun.cs`), `TestReport` (JUnit XML, summary). The user asked for it "like the editor assembly": scripts in any
  `Tests` folder under Assets (Tests wins over Editor around or inside it) -> `<Name>.Tests.csproj` -> `Game.Tests.dll`
  against Game.dll (ProjectReference), LELEngine.dll, LELEngine.TestFramework.dll, OpenTK; not the editor.
- `TestRun` runs on the main thread inside `Tick()` (once per editor frame from `EditorApplication.Update` via
  `EditorTests.Update`) under its own `SynchronizationContext` (`RunContext`): posted continuations run while the frame
  budget lasts (`FrameBudgetMilliseconds`, 12; batch 250), `TestContext.NextFrame` ones in the next tick; the whole run
  is one async method, sync tests run back to back until the budget is spent (`await new FrameAwaitable(this)`).
  Methods are invoked with `BindingFlags.DoNotWrapExceptions` inside `catch when (BreakOnException(e))` (the filter
  breaks with the throwing frame still on the stack). `active` is static (awaits on other threads find the run);
  `End()` clears it, closes the context and unsubscribes `Debug.MessageLogged`: nothing of the test assembly may stay
  referenced (results are strings only and survive reloads in `EditorTests.results`).
- Build (`ScriptDomain`): the game's stamp (`SourceStamp(project)`) leaves test scripts out, the tests' stamp
  (`SourceStamp(project, true)`, `Game.Tests.dll.sources`) covers everything. A build whose game assemblies are current
  is tests-only (`BuildResult.TestsOnly`: `ScriptBuilder.BuildTests` = `dotnet build <Name>.Tests.csproj
  --no-dependencies`) and reloads only the tests (`SwapTests`, the scene stays); otherwise the game build runs and the
  tests build after it (`BuildResult.Tests`). Test errors never fail the game build (logged as `[Tests]`, sticky, listed
  in the Test Runner; the tests stay unloaded). "Rebuild Scripts" forces a full build (`RequestBuild(true)`).
  Reloads wait for a test run to end like for play mode (`pendingReload`).
- `Game.Tests.dll` loads into its own collectible `ScriptDomain.TestLoadContext` that resolves Game.dll (and the game's
  packages, also lazily through `game.LoadFromAssemblyName`) from the `GameLoadContext`; `UnloadDomain` unloads it
  first (it holds the game context). Verified: tests-only and full reloads both report "Previous script assembly
  unloaded" (no leak).
- Player builds compile only `<Name>.csproj` (Tests excluded); `BuildDependencies.IsTestAsset` keeps `Tests/**/Resources`
  out. Light batch builds (`-build` alone) never compile the tests.
- Editor: `Testing/EditorTests.cs` (service: `Root`, `Results`, `Start`, `Stop` / `Abort`, `OutcomeOf`, `Find`; play
  mode or an unload aborts a run), `Testing/TestRunnerWindow.cs` (Window > Test Runner, `IEditorPanel` "Test Runner";
  rows are `item:<Name>` selectables with transparent text and drawn content; filters `filter:Passed|Failed|Skipped|Not
  Run`; Debug asks for a debugger in a modal and starts when `Debugger.IsAttached`, `BreakAtTestStart`). Batch
  `-runTests [-testFilter] [-testCategory] [-testResults x.xml]` in `BatchEditor.RunTests` (lines via
  `Console.WriteLine`: a `Debug` message during a run would count as the next test's log). Project panel: Create C#
  Test Script (into a Tests folder).
- Verify recipe (scratchpad `TestProbe` of session be6ed329): game `Inventory` + `Health` component, editor menu script,
  `Assets/Tests/InventoryTests.cs` + `SceneTests.cs` (pass, fail, exception, cases, source, LogAssert expected /
  unexpected / missing, ignore, explicit, async frames / delay / timeout / Task<int>, ThrowsAsync, scene with a
  component stepped by `scene.Update`, destroyed object null, temp folder) + `Scripts/Editor/Tests/` (Tests inside
  Editor) + `Tests/Resources/testdata.txt`. Batch `-runTests -testResults` gave 20 passed / 6 failed / 1 skipped
  (exactly the designed outcomes, exit 1); filters / category / explicit / no match; `-build` shipped Game.dll without
  test types and left testdata out; tests-only edit -> "[Tests] Tests reloaded"; a compile error in a test -> exit 1,
  game scripts fine. GUI (`project=P focus=0` + CLI `menu "Window/Test Runner"`, `click "button:Run All"`,
  `click item:FailsOnPurpose`, `screenshot "Test Runner" 1000x760 f.png`): running state, details with snippet, debug
  modal (a size forces the layout path, which does not show live popups: screenshot without a size), play during a run
  aborts it. Not verified: an IDE debugger actually attached (Debugger.Break paths).

## Editor automation (EditorAutomation, CLI, window screenshots; 2026-10-26)

- The user wants AI to drive the editor natively (no MCP, no mouse simulation): `LELEngine.Editor/Automation`.
  `EditorAutomation` (public, static) is the C# API: windows (`GetWindows`, `OpenWindow`, `FocusWindow`,
  `CloseWindow`), `GetScreenshot(window, w, h, layout, viewFrames)` (also `EditorWindow.GetScreenshot(w, h)`), UI
  elements (`GetElements`, `Click`, `DoubleClick`, `OpenContextMenu`, `Input`, `Scroll`, `Key`, `TypeText`), menus
  (`GetMenuItems`, `ExecuteMenuItem`), scene and objects (`OpenScene`, `SaveScene`, `FindObject` "A/B" / name / "#id",
  `Select`, `SelectAsset`, `DescribeHierarchy`, `DescribeObject`, `GetField`, `SetField`, `Reparent`, `Rename`,
  `AddComponent`), `Play` / `Stop` / `Pause` / `Step` / `Undo` / `Redo` / `RefreshAssets`, console, waits
  (`NextFrame`, `WaitUntil`, `WaitForIdle`). Everything runs on the main thread between frames (`AutomationContext`,
  pumped by `EditorAutomation.Pump` from `MainWindow.OnUpdateFrame`); async code continues there.
- CLI: every editor (GUI or batch with `-listen`) serves a named pipe per project (`LELEngine.Editor.` + 16 hex of the
  SHA-256 of the lowercased project path; owner and ACL = the user, the client checks the owner). `LELEngine.Editor.Console
  [-projectPath P] -command <words...>` (everything after `-command` is the command), `-script file`, `-json`; exit 0 /
  1 failed / 2 no editor. `-command help` lists the commands (`AutomationCommands`). Screenshots default to
  `<project>/Library/Screenshots/`. Batch: `-batchmode -projectPath P -listen` serves until `quit [--discard]`;
  `-executeMethod Type.Method` may return void / bool / int / Task / Task<T> (`string[] args` optional).
- Batch mode draws no UI at all (the user's call): `BatchEditor` makes `EditorApplication(hasUI: false)` in a hidden
  `MainWindow`, ticks it at 60 Hz without UI frames, waits for `IsBusy` (scripts, assets, settings, window restore,
  scene loads) to settle. UI elements and keys need the GUI editor (clear error in batch); screenshots and element
  lists work in batch through layout captures.
- Screenshots are exclusive (only the asked window, nothing over it). `WindowCapture.Capture`: FrameCapture when the
  window is on screen at the asked size (re-renders its own draw lists from the last frame: the window, its child
  windows, popups / menus / tooltips opened from it, the dock host's tab bar clipped to it); else LayoutCapture (a fresh
  ImGui context sharing the owner's font atlas (`EnsureAtlasReady` first), the owner's ini with the window docked
  alone in a DockSpaceOverViewport, 4 frames, state and scroll copied from the owner, rendered to an FBO). Scene /
  Game views render their own image at that size (`viewFrames`). `--layout` forces the layout path. Pixels RGBA
  top-first, `SavePng`; `Anchors` = the elements with their rectangles in the image.
- Elements, not the mouse: `EditorGui` wrappers (`EditorGui.Controls.cs`: Button, Checkbox, Selectable, TreeNodeEx,
  BeginTabItem, BeginCombo, InputText*, CollapsingHeader, IsItemClicked, IsItemDoubleClicked, BeginPopupContextItem,
  ...) replace the raw ImGui calls in every editor file and record elements `kind:Text` per window (`TestAnchors`:
  "#2" for repeats; a hidden-label widget after a field's label column takes the label: `checkbox:Cast Shadows`);
  menus go through `EditorMenu` (`menu:Path`). `UIAutomation` invokes one by ImGui item id in its window and context
  for up to 4 frames: ImGui's own activation (NavActivateId etc. around that widget call: buttons press, text fields
  start editing with the text selected), click checks report it, context menus open, tabs get SetSelected. It works
  when the element is covered or its window is a tab behind another (focused first). Code acting on a release (the
  hierarchy row) must also accept `EditorGui.IsItemInvoked()`. New editor code: use the `EditorGui` wrappers, not
  raw `ImGui.Button` etc. Canvases (scene view picking, VFX graph, curve editor, audio timeline, UI Builder canvas)
  are not elements: use the actions (`select`, `set`, menus) there.
- Keys and text by play state (the user's call, 2026-10-31): `key` / `type` go to the game in play mode (playing or
  paused; a paused game reads nothing, as with the keyboard) and to the editor in edit mode, or with `--window W` /
  `--editor`; the answer says which ("Pressed Q in the game"). `--hold S` keeps the key down S seconds (at least a frame;
  NF's undock needs 0.7 s). Game side (`Automation/GameKeyboard.cs`): ImGui key names mapped back through
  `ImGuiController.MapKey`, the key held in `GameInputHost` (`SetKeyHeld`: down for the game whatever has focus, so the
  input actions see it) plus `Input.Input_KeyDown / KeyUp / TextInput` events (GetKeyDown, the game's UI); needs no UI,
  so batch play mode takes keys too. Verified on NF's station (GUI and batch): Q / E switch tabs one per press, U held
  1.2 s undocks (0.3 s does not), `--editor` leaves the game alone, edit-mode Ctrl+D duplicates the selection.
- Names as the inspector shows them (the user's call, 2026-10-26: every component's fields, the game's own too, as
  named in the inspector): an element is `kind:path`, the path built from scopes (`TestAnchors.PushScope`: the
  inspector pushes each component's title, `FieldDrawer.Header` its field (nested objects, lists, dictionaries),
  list rows their index, dictionary entries their key, materials their name; open combos (`EditorGui.EndCombo`
  pops) their own name) plus the widget's text (a hidden-label widget takes its line's label column:
  `input:Mover/Speed`, `input:Mover/Settings/Max Speed`, `input:Mover/Weights/1`, `input:Mover/Loot/gold/value`,
  `item:Mover/Mode/Fast`). A text equal to its scope's last segment adds none (`header:Mover`). Lookup
  (`TestAnchors.FindElements`): whole name, path, or the path's end at a `/` ("Speed"), newest drawing first, controls
  before the hooks' anchors (`row:`, `field:`) when no kind is named, whole paths before ends; several left = an
  error listing them. Value widgets (`EditorGui.DragFloat/2/3/4`, `DragInt/2/3/4`, `SliderFloat/Int`, `InputInt`,
  `InputFloat`, `ColorEdit3/4`, `InputText*`, `Checkbox`) record their value (`elements` prints `= value`) and take
  one: `input <element> <value>` = `UIAutomation.Action.SetValue`, parsed by `ValueText` ("1, 2, 3", "#FF8000",
  true), written after the widget, which then returns true and reads as edited and deactivated after an edit
  (`UIAutomation.ValueApplied` sets ImGui's LastItemData / DeactivatedItemData), so `InspectorContext.Track` makes
  one undo step per input. Combos: click, then the option's element. Verified 2026-10-26 on a scratch `Mover`
  component (float, int, string, bool, Vector3, Color4, enum, nested class, List, Dictionary, Transform): 10 inputs,
  values in the inspector, 10 undos back to the start.
- Editor scripts: `EditorGUILayout.Button / Toggle / TextField / Foldout` (and `Field` / `PropertyField`) are
  elements; a raw `ImGui.Button` in a script window is not (by design: no hook into raw ImGui).
- Verify recipe (scratchpad `AutoProbe` of 2026-10-26: `ProbeWindow` + `AutoTest.Run`): GUI editor `project=P focus=0`,
  then the CLI: `windows`, `elements "Probe Window"`, `click "button:Press me"`, `input "input:Note" text`,
  `click hierarchy:Sphere` + `status`, `click "checkbox:Cast Shadows"` + `get`, `undo`, screenshots of covered
  windows; batch `-executeMethod AutoTest.Run` and `-listen` + `quit`. Start / stop test editors from the PowerShell
  tool (a Bash-run `Start-Process` hangs on inherited handles).

## Player startup and splash screen

- `LELEngine.Player/Program.cs`: boot.json (source-generated STJ, `BootConfigJson`), the window opens hidden without
  its renderer (`GameWindowOptions`), `Window.Show` puts the splash screen up as the first image (cloaked by DWM
  until presented: a swap on a hidden window is dropped and the window shows white), then `GameLoader` loads on a
  background thread: asset index, project settings and Game.dll while the window opens, then (the main thread hands
  over the GL context) renderer, start scene and two offscreen warm-up frames (`GameHost.WarmUp`). The main thread
  pumps events, takes the context back, waits out `Duration`, then `Run()` (lifecycle on the main thread).
- Only the context's holder may draw (the splash is redrawn after a resize between loader steps); GLFW size getters
  (`ClientSize`, `FramebufferSize`) are main-thread only; resize callbacks check `Context.IsCurrent` before GL work.
- Settings: `ProjectSettings.SplashScreen` (logo stored as GUID), editor tab Project Settings > Splash Screen
  (`projectsettings=N;SplashScreen`). `GameBuilder` bakes it into boot.json `SplashScreen` and `Splash.bmp` (logo
  composited over the background, background quantized to 8 bit so no rectangle shows); the player draws it with a
  clear and a blit, no shader.
- Player.log has `[Player] Splash screen shown / Loaded in the background (per step) / Start scene's first frame`
  times. On the Intel iGPU window + GL context take ~450 ms (driver init ~300 ms; a WGL warm-up thread does not help,
  it serializes), splash ~600 ms after the process start, scene ready ~1 s.

## Scene loading

- `LELEngine/Mono/SceneManager.cs` (+ `SceneLoadOperation.cs`), Unity-like. `LoadScene(path, mode)` loads and
  activates now. `LoadSceneAsync(path, mode, allowActivation)` reads on a loading thread (the scene document and its
  assets inside an `AssetLoadScope`, no GL calls), then `SceneManager.Update()` prepares the recorded assets on the
  main thread (shader compiles, texture and mesh uploads) within `BackgroundLoadingBudget` ms per frame (at least one
  asset), then activates. With `allowActivation: false` the scene stays loaded but invisible (`IsLoaded`; nothing of
  it runs or draws) until `AllowActivation = true` / `Activate()`; scenes activate in request order (a held one holds
  the later ones, which still prepare). Also `Cancel()`, `UnloadScene(scene | path)`, `GetSceneByPath`,
  `MoveGameObjectToScene`, `DontDestroyOnLoad`, events `SceneLoaded` / `SceneUnloaded` / `ActiveSceneChanged`.
  Single unloads every loaded scene but DontDestroyOnLoad, Additive adds.
- Hosts call `SceneManager.Update()` and `UpdateScenes(dt)` every frame (all loaded scenes phase by phase, one
  fixed-step accumulator; each scene keeps its own Physics2D world), draw `GetRenderLists`, and set `Lifecycle`
  (`StartLifecycle(true)` in the player, Edit / Play in the editor). `Camera.main` and `DirectionalLight.This` go
  through `SceneManager.MainCamera` / `MainLight` (active scene first). `UnloadAll` releases no asset.
- GL work off the GL thread: `GraphicsThread.HasContext`; a `ShaderProgram` created without a context compiles on
  first use (`EnsureBuilt`), textures and meshes upload on first use (`Texture.Handle`, `GeometryPool.Prepare`);
  `InternalStorage.Sync` guards the caches. Unloading frees the textures and meshes no loaded or loading scene uses
  (`ReleaseUnusedAssets`, off in the editor; `GeometryPool` reuses freed ranges).
- Every load logs `[SceneManager] <path>: read N ms (textures N: N ms, meshes ..., per asset kind), GPU N ms in N
  frames, activated in N ms` (also `SceneLoadOperation.Summary`).

## Textures of a load on worker threads (TextureImportBatch, 2026-10-26)

- `SceneSerializer.LoadFromBytes` (every scene and prefab load: the editor's `OpenSceneNow`, `SceneLoadOperation.Read`
  for `LoadScene` / `LoadSceneAsync` / the player's start scene, snapshots) and `ResourceRequest` open a
  `TextureImportBatch` (`Assets/TextureImportBatch.cs`, one per thread, nested ones join). Inside it
  `TextureAssets.TryDefer` makes each fresh 2D texture from its file's header (PNG etc. through StbImageSharp's
  `ImageInfo` plus the importer's `maxSize`; a build's KTX through `KtxFile.ReadHeader`), so materials get the final
  object, and `Task.Run`s the rest (`Import`: read, decode, flip, downscale; KTX: the file read). Disposing the batch
  completes them on the loading thread, so a load still returns complete textures. Cubemaps, volumes, HDR, reimports
  (`previous`) and headers the decoder rejects take the old synchronous path.
- `Texture2D.PendingImport`: `HasData` (upload) and `EnsureCpuPixels` (CPU pixels) complete it first (waiting for that
  one task; `PendingTextureImport.Complete` locks, so any thread may ask); `SetPixels` / `SetPixelsFloat` / `SetImage` /
  `SetBaked` drop it (a late result never overwrites newer texels). A failed import logs `Cannot import` and leaves the
  texture empty (it samples black; before, the material got its default texture). The load's summary shows the join
  as `textures on worker threads N: ms` (the "textures" category is then the header reads); the editor logs `[Editor]
  Opened <scene> in N ms (...)`.
- Measured (scratchpad `LoadProbe`, headless: `AssetDatabase.Initialize` + `SceneSerializer.Load`, no GL): New
  Frontier's Planets scene (112 textures, 17 of 4096^2, ~2.5 GB RGBA) read in 1.56 s against 9.2 s for the same
  textures one by one (StbImageSharp 297 MB/s per thread, x11 on 32 threads); pixels, size, sRGB, mipmaps identical.
  Synthetic checks: a KTX under an image name, pixels read inside the batch, SetPixels during the import, a truncated
  PNG. GL work stays on the main thread: shader compiles and uploads (+ GenerateMipmap) happen on first use.

## Shaders compiled together, immutable textures (2026-10-26)

- `GLCapabilities.ParallelShaderCompile` (KHR / ARB_parallel_shader_compile): `glMaxShaderCompilerThreads(0xFFFFFFFF)`,
  or 0 with `LEL_GL_DISABLE=parallelcompile` (NVIDIA's default is neither). `ShaderProgram` starts and finishes builds
  apart: `StartBuild` (the constructor on the context's thread, `Start`: binary cache or `Shader.Submit` per stage and
  `StartLink`, no queries) and `EnsureBuilt` (`FinishBuild`: `Shader.Check`, `FinishLink`, uniforms, `BuildLog`, the
  binary saved); every accessor of the program (Handle, Use, IsLinked, uniforms, BuildLog) goes through `EnsureBuilt`.
  `ShaderProgram.FinishStartedBuilds()` waits for all started ones (the editor's `OpenSceneNow`, which logs the wait);
  `SceneManager.StartShaderBuilds` starts a scene's / request's programs on their first `Prepare`;
  `ShaderVariantList.Compile` starts every variant, then waits for each. Programs from `FromSources` and the instanced
  variants (checked at once by `GetInstancedVariant`) still build in one go.
- Measured on an RTX 5070 Ti (driver 621.01; scratchpad `GLBench`, hidden window, sources with a random comment so the
  driver's disk cache misses): the 13 FORGE3D planet programs (1.2 MB of source with the includes) 13.8 s one by one,
  10.7 s one by one with driver threads, 9.3 s started together without them, 2.8 s started together with them.
  Through the engine (Planets scene read, `FinishStartedBuilds`): 2.1 s with an empty binary cache, 0 ms from it (13
  loaded); with `parallelcompile` disabled every program still links clean.
- `Texture2D.UploadData`: `glTexStorage2D` with the whole mip chain (1 level without mipmaps), then `glTexSubImage2D`
  (0x0 textures keep `glTexImage2D`). The Planets scene's 112 textures (1.6 GB): 274-287 ms against 779-868 ms, of which
  `GenerateMipmap` on mutable textures was 518-530 ms (it allocated the chain). Verified: immutable levels as expected,
  level 0 read back equal to the CPU pixels, no GL error.

## Background resource loading (Resources.LoadAsync)

- `Resources.LoadAsync<T>(path)` / `LoadAsync(path, type)` / `LoadAsync(path)` return a `ResourceRequest`
  (`LELEngine/Assets/ResourceRequest.cs`; `Asset`, `IsDone`, `Progress`, `Error`, `Summary`, `Completed`, awaitable,
  `ResourceRequest<T>` typed). Requests and `LoadSceneAsync` share one loading thread ("Scene loader", queue of
  `IBackgroundLoad`, read in request order; `ReadNext` keeps no reference to a load while it waits). The main thread
  prepares them within `BackgroundLoadingBudget` (scenes first, then requests, at least one asset each per frame) and
  completes them in `SceneManager.Update()` (the editor calls `UpdateResourceLoads()` in edit mode); each logs
  `[Resources] <path>: read ... GPU ...`. A resource path shared by two files (Menu.uss and Menu.uxml) needs the type.
- `SceneManager` installs `MainThreadContext` (a `SynchronizationContext`) on the main thread at its first update and
  pumps it every update: awaiting code continues on the main thread, an exception escaping an async void method is
  logged instead of ending the process. `UnloadAll` drops pending requests and posted continuations (they never run).
- `AssetLoadScope.Begin(background: true)` holds back the Awake / OnEnable of ScriptableObjects it loads
  (`ScriptableObject.heldBack`, `TakeDeferred`, `RunDeferred`, `ScriptableObjectAssets.EnableHeld`): requests run them
  when they complete, async scene loads once prepared (before the scene's components wake); a load on the main thread
  that meets a held asset runs them at once. Synchronous loads run them on the loading thread as before (the
  player's start scene loads on `GameLoader`'s thread).
- UI on the loading thread: `UIAssets` parses outside its cache locks (first result stored wins; a [ThreadStatic]
  `parsing` set stops @import / template cycles), `TextureAssets.Decode` flips rows itself (stb's flip switch is one
  global for all threads), `FontAsset` has a lock (`sync`; outlines and distance fields computed outside it, the atlas
  through `GetPageTexture` on the GL thread), `StyleSheet.id` is interlocked. Dependencies of sheets and documents are
  recorded (`CollectReferencedAssets`: imports, url() / resource(), templates, inline styles, attribute files);
  `UIAssets.WarmUp` (background loads only) rasterizes printable ASCII plus every character of the documents'
  attribute values into the fonts used and the default font (bold / italic faces only when a sheet, style attribute
  or rich text asks), resolves the documents' element types (`UxmlRegistry` reflection), and adds the fonts so
  `PrepareAsset` uploads their pages.
- Measured 2026-10-20 (Debug, Intel iGPU, probe menu: UXML + 2 USS + template + Georgia + 2048^2 PNG): synchronous
  load + show = 456 ms main-thread update; LoadAsync = 79 ms (first UI document init ~15 ms and first style / layout
  are one-offs; a second document of the same asset: AddComponent 0.4 ms, tree 0.3 ms). A warm 2048^2 RGBA8 upload
  costs ~2 ms (GenerateMipmap is asynchronous); the first mipmap generation of the process costs ~11 ms.
- Leak fixes found with play-mode + reload runs: `UIRuntime.frameDocuments` cleared after input, pooled UI events
  reset on `Dispose`, the hover chain buffers cleared, `SceneManager.combinedRenderers/Behaviours` cleared in
  `UnloadAll` (several loaded scenes, DontDestroyOnLoad), `VisualElement`'s type-name cache keyed weakly
  (`ConditionalWeakTable`: game scripts' element subclasses held Game.dll, 2026-10-22). Verify play-mode work with a reload afterwards
  (`touchscript=N;file` with a `/*v1*/` file), not only with the startup reload; the leak warning lists what holds
  the old scripts (see "Leak report" above).

## Destroying objects and the null-check weaver

- `EngineObject` (`LELEngine/Mono/EngineObject.cs`) is the base of `GameObject` and `Behaviour`. Two flags:
  - `dead`: Destroy was called; public as `IsDestroyed`. The object gets no callbacks; `GetComponent` (on a living
    object), `FindObjectOfType`, physics queries and contact begins skip it.
  - `destroyed`: the end-of-frame finish ran (OnDisable, OnDestroy and `Destroyed` once, guarded by
    `destroyCallbacksRun`; resources released). Then `ThrowIfDestroyed` makes `GetComponent`, `AddComponent` and
    `SetActive` throw `ObjectDestroyedException`.
- Code that decides whether something still takes part in the game reads `dead`: lifecycle loops, renderer lists,
  serialization and editor selection. Resource bookkeeping reads `destroyed`: navmesh obstacles, rigidbody shapes,
  `Transform.SetParent` notifications.
- `Scene.Kill` marks a subtree dead and queues every node. `ProcessDestroyQueue` (`EndFrame`, after
  `StartTimedDestroys`) re-collects the subtrees (late children go too) and calls `FinishDestroy` in three passes;
  OnDestroy may destroy more objects, up to 8 rounds per frame. `DestroyImmediate` runs the same `FinishDestroy` at
  once. `Time.frameCount` counts `UpdateScenes` calls.
- The editor and batch mode set `EngineObject.RecordDestroyStackTraces` (`Program.Main`): each Destroy captures the
  caller's stack for the exception message.
- `NullCheckWeaver` (`LELEngine.Editor/Scripting`, Mono.Cecil) rewrites Game.dll and Game.Editor.dll after every
  `ScriptBuilder.Build`: editor, batch `-executeMethod`, player builds. It also rewrites before loading an existing
  build (`ScriptDomain.Initialize`, BatchMode's up-to-date path).
  - The user chose (2026-10-20) to weave the editor scripts too, so tools see the same null semantics. Editor code
    that must see a destroyed object uses `EngineObject.IsNullReference` / `IsDestroyed`.
  - It runs a forward stack-kind analysis per method (`Null` / `Plain` / `Engine` / `Maybe`), because Release IL
    keeps values on the stack across statements and joins (`dup` chains). A backward walk to the producer failed
    there.
  - It rewrites brtrue/brfalse to `IsAlive` / `IsAliveObject`, and ceq / cgt.un against the null constant to
    `NullCheckEquals` / `NullCheckNotEquals` (two arguments, so the `ldnull` stays).
  - `NullChecksWovenAttribute(count)` marks a woven assembly.
  - `ReferenceEquals(x, null)` compiles to `ldnull; ceq` and is woven too; `EngineObject.IsNullReference` is the
    escape hatch.
- **Verify** with a probe project. The 2026-10-20 probe (`DestroyProbe` in that session's scratchpad) checked:
  - every null-check form in Debug and Release IL (Release: build the probe's csproj with `-c Release`, then run
    batch `-executeMethod`, which takes the up-to-date path and weaves);
  - callback order, events, the exception, `Destroy(x, delay)`, components added to dying objects, and physics
    queries;
  - editor play mode (`addcomponent=`, `playframe=`, `stopframe=`);
  - the marker in a player build's Game.dll.
- **Benchmark:** `LELEngine.Benchmarks physics2d-scene` fails because `Meshes/cube.obj` is not copied to its output
  folder. That asset is unrelated to destroying.

## Shader variants, Engine/ParticlesUnlit.shader, built-in assets (2026-10-24)

- Keyword variants, Unity's `shader_feature` made automatic: `[Toggle]` (keyword NAME_ON), `[Toggle(KEYWORD)]` and
  `[KeywordEnum(A, Soft B)]` (NAME_A, NAME_SOFT_B; the value is the option's index) on Float / Int / Range properties
  (`ShaderProperty.Keywords`, `KeywordFor`); since 2026-10-28 a `[Toggle]` on a 2D texture is a map keyword
  (`IsMapKeyword`): on while the material has a texture in that slot (`Material.KeywordState` / `GetKeywords`), drawn
  as the texture field (tooltip names the keyword), not a checkbox. `Material.Program` is the shader's variant for the keywords of the
  material's values (`ShaderProgram.GetVariant(keywords)`: a program per sorted keyword set, made on first use from
  the same file with the keywords as defines, kept with the shader, deleted with it); `UsingShader` stays the asset.
  Every draw path uses `Program` (opaque per-object and GPU-driven buckets, transparent pass, skybox, scene-load
  preparation `AssetLoadScope`); `Material.RenderState` / `renderQueue` / `IsTransparent` are the variant's. The
  choice is cached per material (`VariantChoice`, keyword state packed in a long) and follows `SetFloat` at once.
- Render state per variant: the Properties block is filtered by `ShaderConditionals` (#if / #ifdef / #ifndef / #elif /
  #else / #endif; keywords, defined(), !, &&, ||) before its lines are read, so Blend / ZWrite / Cull / Queue lines can
  depend on keywords. New render state keyword `ShadowCaster Off` (`ShaderRenderState.CastShadows`): no opaque shadow
  map (`MeshRenderer.RenderShadowCastersWith`, the GPU-driven instance flag) and no transmittance
  (`TransparentShadows`). File programs fill `ShaderProgram.BuildLog` (stage and link messages) like generated ones.
- Builds: `BuildDependencies.CollectShaderVariants` (every shipped and built-in material: `ShaderVariant.Of`), checked
  offline by `ShaderValidator` (`LELEngine.Editor/Building`, glslang through Silk.NET.Shaderc 2.22, editor only, natives
  for win / linux / osx; no GL context, so it runs in batch mode, the Build tab and CI alike): every stage of each
  variant and of its instanced program (shaders including Instancing.glsl) as the preamble makes it for three kinds of
  GPU (`ShaderFeatures`: draw parameters ARB, GLSL 4.60 core, none), compiled for OpenGL 4.5 into SPIR-V that is thrown
  away (auto-mapped locations and bindings: SPIR-V for GL wants them); identical code once (fragment stages do not
  depend on the GPU kind; messages say "every kind of GPU" or name the kinds). `ShaderProgram.ReadSources(path,
  defines, features, lineDirectives)` gives the exact sources; with line directives (GL_GOOGLE_cpp_style_line_directive,
  checked copy only) messages read `Shaders/Broken.shader:36` / `BrokenInclude.glsl:6`. glslang declares
  gl_BaseInstanceARB from GLSL 4.50 only, so the ARB profile's vertex stages are checked as `#version 450` (the no-draw-
  parameters profile checks the file's own 430). One that does not compile stops the build (`GameBuilder`
  `ValidateShaderVariants`); not covered: driver limits and bugs, the link between stages. ~350 ms for 13 variants
  (64 stage sources). Listed in `Data/ShaderVariants.txt` (`ShaderVariantList`, "shader|KEYWORDS" lines); the player
  compiles them in the loader step "shader variants" (after the renderer, before the scene). Runtime-made variants
  still compile on first use.
- Program binary cache (`ProgramBinaryCache`, engine): `ShaderProgram.Build` (file programs, compute passes included)
  and `FromSources` (VFX kernels) load a program from the driver's binary (glProgramBinary) when one was saved for the
  same final stage sources, else compile, link (retrievable hint) and save (glGetProgramBinary). Files `<SHA-256 of
  the sources>.bin` in a folder per driver (hash of vendor / renderer / version / GLSL version, `driver.txt`), written
  aside and moved in; a refused binary (driver update) is deleted and compiled again; files unused for 60 days and all
  but the 3 most recent driver folders (only folders with a driver.txt) go at start. Off unless `Folder` is set: the
  player sets `%LOCALAPPDATA%/LELEngine/<title>/ShaderCache` (`shadercache=0` turns it off) and logs `[Player] Program
  binary cache: N loaded, M compiled and saved`. Probe (RTX 3080 Ti, 13 variants + engine passes = 41 programs, 1.9 MB):
  shader variants 6146 ms -> 83 ms, first frames 802 -> 131 ms, start scene shown 7.6 s -> 1.4 s (the 1 s splash);
  without the cache but with NVIDIA's own warm cache the variants still took 3.3 s. The editor uses it too since
  2026-10-26 (`%LOCALAPPDATA%\LELEngine\Editor\ShaderCache`, set in `Program.RunEditor`; `shadercache=0` turns it off):
  a shader edited in the editor has new sources, hence a new key, and compiles.
- `Engine/ParticlesUnlit.shader` (Unity's Particles/Unlit): `BaseMap` x `Color` (HDR) x vertex color; `BlendMode`
  [KeywordEnum] Alpha (SrcAlpha OneMinusSrcAlpha), Premultiply (One OneMinusSrcAlpha, the map's color already
  premultiplied, tint alpha fades all), Additive (SrcAlpha One), Soft Additive (One OneMinusSrcColor, rgb x alpha),
  Multiply (DstColor Zero, rgb lerped to white by alpha), Opaque (opaque pass); `RenderFace` Both / Front / Back;
  `AlphaClip` [Toggle(ALPHA_CLIP)] with `Cutoff`; Opaque + clip draws in the transparent pass at queue GeometryLast+1
  (alpha-tested surfaces are not in the depth prepass). ShadowCaster Off in every mode. No Instancing.glsl: per-object.
  The material editor draws [KeywordEnum] as a combo, [Toggle] as a checkbox, and the variant's keywords.
- Built-in assets: `LELEngine/BuiltinAssets/Engine/...` (Content, copied to `BuiltinAssets/` next to the binaries of
  the editor and every player, like `ShaderLibrary` -> `Shaders/`): `Engine/Meshes/Cube.obj`, `Sphere.obj`, `Quad.obj`,
  `Engine/Materials/Default.material` (`Model.DefaultMaterialPath`). `AssetDatabase.EngineResourceFile` /
  `IsEngineResource` cover both roots for "Engine/..." paths; `ToAbsolute` maps engine paths to their file (they win
  over a project file of the same path), `ToRootAbsolute` is the root path (baked `.sdf` of built-in models in a
  build's Data). Read-only: no meta files (`EnsureMeta` returns null, `SaveMeta` keeps it in memory), no material
  extraction, `Material.Save` refuses them, inspectors draw them disabled, references store the path only. The
  GameObject menu and the default scene use them (`DefaultScene`); the object picker lists them for Mesh / Model /
  Material fields; new projects get only `Materials/Glass.material` and `Ground.material` from the template. Existing
  projects keep their old copies (`Meshes/*.obj`, `Materials/Default.material`) as ordinary assets.
- Verified with a probe project (scratchpad `UnlitProbe`: one quad per mode over a bar, `[ExecuteAlways]` script
  added with `addcomponent=`): editor render, mode switch and undo through `materialfield:` / `materialoption:`,
  switching from code, batch build (ShaderVariants.txt, report), the player's log and frame. glslang: materials of every
  engine material shader (Lit, LitTextured, LitTransparent, Skybox, SkyboxPanoramic, ParticlesUnlit) pass; a project
  shader with errors in one keyword variant (`Shaders/Broken.shader` + include) failed the build with both lines named
  and its other variant passed. Cache: cold / warm runs (frames equal), three binaries with a wrong format refused and
  replaced, `shadercache=0`.

## Refraction: GrabPass, LitTransparent / ParticlesUnlit REFRACTION (2026-10-24)

- Render state line `GrabPass On | Off | <FloatProperty>` (`ShaderRenderState.GrabPass`, `GrabOffsetProperty`), per
  variant like the others: Unity's per-object GrabPass. Right before such an object draws, `TransparentPass` copies the
  scene drawn so far (opaques, sky, the transparent objects before it) into `SceneGrab` (`Rendering/SceneGrab.cs`):
  RGBA16F, rgb the HDR color, a the linear eye depth of the opaque surface (`Engine/SceneGrab.shader`, texelFetch of
  SceneColor + SceneDepth under a scissor per rectangle), bound on texture unit 21 (9, the GI resolve normals unit, on
  contexts with fewer units). Made on the first copy, follows the target size, freed in `ReleaseSceneReferences`.
  The user chose (2026-10-24) the per-object grab over one copy before the transparents: refracting glass must see the
  glass and particles behind it; the cost of more copies was accepted.
- A grab variant always draws in a transparent queue: `Finish` lifts a declared opaque queue to GeometryLast + 1, and
  `Material.renderQueue` does the same for an override.
- Copies are planned per frame (`TransparentPass.PlanGrabs`, only when an entry grabs): every entry's drawn rectangle =
  its bounds projected (mesh bounds x LocalToWorld, VFX system bounds; the whole screen when unknown or crossing the
  camera plane); a grab entry reads that widened by the largest shift its offset property allows
  (`SceneGrab.Widen`: sqrt(2) x strength x |projection| / 2 / nearest w, plus a texel; `GrabPass On` reads the whole
  screen). It joins the latest copy (its rectangle is copied with it) while nothing drawn since overlaps what it reads,
  else a new copy is taken before it; overlapping rectangles of one copy merge (`MaxCopyRects` 16), more than
  `MaxDrawnRects` 32 draws since a copy collapse into their bounding box. Off-screen grab entries take no copy.
  Stats: `TransparentPass.Grab.CopyCount` / `CopiedPixels`; TestGame `stats=1` prints `grabs N (px)`.
- `Engine/GrabPass.glsl` (uniforms set by `SceneGrab.SetUniforms`: lelGrabTexture, lelGrabRect (uv of the readable part,
  the lookups are clamped to it), lelGrabScreen, lelGrabDepth (eye depth = (z*x + y) / (z*z + w) from the projection's
  depth rows, row vectors: x = M44, y = -M43, z = M34, w = -M33), lelGrabAxisX / Y (camera right / up x projection
  scale / 2)): `LelGrabEyeDepth`, `LelGrabUV`, `LelSampleGrab`, `LelSceneRefraction(bend, strength)`. `bend` is the
  normal map's tilt as a world vector across the surface (tangent x n.x + bitangent x n.y, length <= 1); the lookup moves
  against it (light entering glass: a convex bump magnifies) by strength world units at the fragment's distance
  (x gl_FragCoord.w: the same world size at any distance and resolution). Depth mask (the user's original ask: only
  what is behind refracts): keep = saturate((grab depth at the shifted spot - fragment depth) / strength), and a second
  lookup at offset x keep when below 1, so surfaces in front (drawn opaque before, or closer than the shift) are never
  pulled in and no seam shows where the surface meets geometry behind it.
- `Engine/LitTransparent.shader`: `[Toggle(REFRACTION)] Refraction`, `NormalMap` (in the variant it shapes the lighting
  too), `RefractionStrength` (world units, 0.1). The variant: `Blend Off`, `ZWrite On` (the nearest surface wins where
  the object overlaps itself, as Unity's refraction shaders), `GrabPass RefractionStrength`; output = behind x (1 -
  opacity) + scattered x opacity + reflected (the blended look, refracted). Cull Off objects draw back then front faces
  with one grab (the front faces overwrite).
- `Engine/ParticlesUnlit.shader`: `Refraction`, `NormalMap`, `RefractionStrength` (x alpha), `RefractionBlend` (Unity's
  Distortion Blend: own color where alpha - blend > 0; 0.5). Per blend mode: Alpha / Premultiply blend the refracted
  scene normally; Additive adds (behind - scene here); Soft Additive and Multiply switch to `Blend One One` and add
  (their blend over the refracted scene - scene here); Opaque goes to queue GeometryLast + 1. 74 keyword combinations.
- `Material.SetUniforms` uploads declared defaults for numeric properties a material lacks (`ApplyMissingProperties`,
  `ShaderProperty.DefaultValueProperty`; Color / Emissive / Roughness keep `ResetStandardUniforms`): a program keeps
  uniform values between draws, so an old material (saved before the shader gained a property) showed the previous
  material's value. The material inspector shows those effective values.
- Limitations: transparent objects in front that sort before the grab object are in the copy without depth (they can
  be refracted); triangles of one draw read the same copy; screen space: what is off screen or hidden is not there (the
  shift falls back to the unshifted pixel, a thin seam beside foreground objects). VFX graph outputs have no refraction.
- Verified: TestGame `refraction=1` (`BuildRefraction`: bumpy pane with a red post in front, bumpy sphere, haze and
  additive particle quads; procedural normal maps): with the mask the post stays sharp, without it (keep = 1) it smears
  into the glass as in Unity; 3 copies for 4 grab objects. Probe project (scratchpad `RefractionProbe`, editor script
  calling `ShaderValidator` through reflection): all 74 variants pass glslang and link on GL, render states per variant
  as above; a batch build lists exactly the used variants in ShaderVariants.txt; an old LitTransparent material shows
  Refraction Strength 0.1 in the inspector. Default room vs. HEAD build (animate=0): differences below run-to-run noise.

## Mesh distance field atlas: shapes, settling, freeing, growing (2026-10-24)

- `DistanceFieldAtlas` (`Rendering/DistanceField`) keys fields by mesh and shape (`ShapeKey`: |scale| / its largest
  component, thousandths; every uniform scale is (1000, 1000, 1000)), built at that shape (`MeshDistanceField.Shape`,
  bounds and distances in shape units). `LumenScene` draws a field at `UniformScale(scale, shape)` (the largest u with u
  x shape inside the scale on every axis: the scale's largest component for its own shape, smaller for a borrowed one):
  the object table's bounds, padding and mesh bounds are times u (`SceneObject.Scale` = shape x u), and `atlasSize.w` =
  u multiplies the sampled distance in `SceneObjectDistance` (Engine/SceneObjects.glsl, the only atlas read). Surface
  cache cards keep the scale they were laid out for (`SurfaceCardSet.Scale`, the capture's `objectScale`).
- `LumenScene.ResolveField`: a renderer seen for the first time gets its shape's field (built when missing); after its
  shape changes it builds none until the shape held `ShapeSettleSeconds` (0.25 s of `Time.timeD`; frame counts settled
  at the extremes of a sine at 1000 fps), meanwhile it takes the atlas's field of the new shape if there is one, else
  keeps its previous one (`IsValid`), else has none. States per renderer (`fieldStates`), pruned when renderers go.
  Before: effects scaling every frame (New Frontier's flashes, 0.5 -> 2) made a field per frame (and a GPU readback
  each) until the 1024 slices were full, then "Atlas full" once per new scale, and later meshes got no field.
- Room: slices come first fit from a free list (merged neighbours). No fit: the fields unused for `EvictAfterFrames`
  (30) frames are freed (`IsValid` false; the copy kept on the mesh re-uploads them when needed again), then the texture
  doubles (`Grow`: new texture, glCopyImageSubData, `[SDF] Distance field atlas grown to N slices`) up to `MaxDepth` =
  min(4096, GL_MAX_3D_TEXTURE_SIZE) (32 MB); then the request fails, warned once (`ReportFull`), and fails at once for
  the rest of that frame. `GLCapabilities.Max3DTextureSize`. Old `.sdf` files and caches stay valid: a key always meant
  a field built at key / 1000 (`BakedDistanceField.ShapeKey`, same file layout).
- Verified: TestGame frames vs. before (animate=0): VCT max 2 of 255 (mean 0.024: shape quantization, half floats in
  shape units), Lumen within run-to-run noise; 13 fields instead of 14 in the room. `sdfatlas=1` (`SdfAtlasProbe`):
  30 shapes grow the atlas to 2048, 30 other shapes a second later reuse the room of the first (no growth), 120 more
  grow it to 4096 and warn once (99 fields, 64 objects without), a box whose shape changes every frame builds no field
  for 3.5 s and one 0.25 s after it holds. New Frontier's Flight scene firing for ~500 frames: 4 fields, no warning.

## TrailRenderer, Gradient, ParticlesUnlit tiling and soft particles (2026-10-24)

- `TrailRenderer` (`Mono/Behaviours/TrailRenderer.cs`, global namespace, `[ExecuteAlways]`): Unity's settings as
  properties over `m_` fields (`time`, `minVertexDistance`, `autodestruct`, `emitting`, `widthCurve` x
  `widthMultiplier`, `startWidth` / `endWidth`, `colorGradient`, `startColor` / `endColor`, `numCornerVertices`,
  `numCapVertices`, `alignment` View / TransformZ, `textureMode` Stretch / Tile / DistributePerSegment /
  RepeatPerSegment, `textureScale` (x: Tile and RepeatPerSegment, y: across), `receiveShadows`, `receiveGI`,
  materials (`Materials`, `SetMaterials`, `sharedMaterial(s)`); `Clear`, `AddPosition(s)`, `Get/SetPosition(s)`,
  `positionCount` (index 0 = oldest). `LateUpdate` advances the trail's own clock (frames that update it: a paused
  game keeps the trail; time spent disabled is added on `OnEnable`, as Unity's points age by Time.time), adds a point
  when the object moved `minVertexDistance`, drops points older than `time` (no tail interpolation, as Unity), and
  autodestructs in play mode. Points survive disabling (pooled objects call `Clear`). `Reset` (Add Component, the
  GameObject menu's Trail) assigns the built-in `Engine/Materials/Default-Line.material` (ParticlesUnlit, alpha).
- Drawing: `TransparentPass.CollectTrails` takes the trails from `context.Behaviours` (on the camera's layers) and
  builds each for this camera (`BuildGeometry`: the object while emitting, then the points newest first; t =
  distance / length from the object for width, color and Stretch u) with `LineGeometryBuilder`
  (`Rendering/LineGeometry.cs`: two vertices per point across `cross(along, facing)`, facing = towards the camera or
  the stored Z; corner fans around the inner vertex, cap half discs; world space, normal = facing, tangent = along,
  bitangent = -side) into one stream per camera (`DynamicGeometry`, orphaned buffers, the standard vertex layout).
  Each material slot is an entry sorted with the meshes (queue, then the bounds' centre), drawn with
  `modelMatrix` = identity; opaque materials too (they draw in the transparent pass before the transparent queues:
  no depth prepass, shadows or GI from trails). GrabPass works for trails (bounds rectangle).
- `Gradient` (`Mono/Gradient.cs`): Unity's API (`GradientColorKey` / `GradientAlphaKey`, at most 8 of each, sorted,
  `mode` Blend / Fixed (the next key's value) / PerceptualBlend (Oklab), `Evaluate`, `SetKeys`); values used as
  stored (no color space conversion, as Unity's trails with Apply Active Color Space off). Inspector:
  `GradientEditor` (`FieldDrawer`, `EditorGUILayout.GradientField`): a bar with the checkerboard; a popup with the
  mode, alpha keys above the bar and color keys below (click selects, drag moves, click beside the bar adds, Delete
  removes), the selected key's color / alpha and location, undo through the inspector context. Anchors
  `gradientbar`, `gradientkey:c0` / `gradientkey:a0`.
- `Engine/ParticlesUnlit.shader`: `BaseMap_ST` (Unity's _ST: tiling xy, offset zw); the material editor draws any
  `<texture>_ST` Vector property as Tiling / Offset rows under its texture. Soft particles: `[Toggle(SOFT_PARTICLES)]`
  with Unity's `SoftParticlesNearFadeDistance` / `SoftParticlesFarFadeDistance` (fade = saturate((scene depth -
  fragment depth - near) / (far - near)) on the tint's alpha; Unity's legacy InvFade = near 0, far 1 / InvFade), not
  in Opaque mode. New render state line `SceneDepth On` (`ShaderRenderState.SceneDepth`; such variants stay in a
  transparent queue like GrabPass ones): the transparent pass takes the opaque depth copy the VFX soft particles use
  (`VfxRuntime.CaptureDepth`) once and binds it (`SceneDepthTexture`, unit 22) for `Engine/SceneDepth.glsl`
  (`LelEyeDepth`, `LelSceneEyeDepth`, `LelSoftParticleFade`). Since 2026-10-26 the copy is taken right before the
  first entry that does not draw like an opaque (`TransparentPass.DrawsLikeOpaque`: queue <= GeometryLast + 1, no
  blending, ZWrite On, not a SceneDepth reader, not VFX), so the opaque surfaces the prepass cannot draw (alpha clip,
  vertex functions) are in it. Before, it was taken before them: New Frontier's FORGE3D sun (vertex function,
  GeometryLast + 1) was missing, its corona plates (Depth Fade) saw the sky and cut into the sphere with a hard line.
- Porting Unity's legacy particle materials to ParticlesUnlit: Legacy Particles/Additive computes 2 x vertex color x
  _TintColor x texture (alpha too) with Blend SrcAlpha One and Cull Off, so Color = 2 x _TintColor, BlendMode
  Additive, RenderFace Both; _MainTex scale / offset -> BaseMap_ST; _InvFade -> soft particles (far 1 / InvFade) when
  the Unity project ran with soft particles. FORGE3D/Additive: the same without the doubling.
- Verified: TestGame `trails=1` (`BuildTrails`: tapering additive figure eight, alpha trail with round corners and
  caps that toggles emitting, twisting TransformZ ribbon (opaque), Tile checker, and two additive glows half sunk into
  a slab, soft and hard), with `night=1 glass=0 emitters=0 gi=0` for contrast; New Frontier's Photon in editor play
  mode (scratch probe script, removed); the gradient editor scripted on a new scratch project (select, drag to 50%,
  Delete, undo; Reset's default material saved as a path); a batch build (glslang on the SOFT_PARTICLES variant).

## Skybox orientation

- `Engine/Skybox.shader` and `Engine/SkyboxPanoramic.shader`: `Tilt` (degrees about X, applied first) then `Rotation`
  (yaw), 2026-10-22; the GI's sky lookup (`Sky.glsl`, `giSkyboxTilt` / `giSkyboxRotation` from `SkyEnvironment`) does
  the same. The yaw turns the opposite way to Unity's skybox `_Rotation`. Unity skies (left-handed) seen from a
  right-handed camera come out mirrored; mirror the faces (flip each horizontally, swap +X / -X) to keep the image.

## Second UV set, textures past 8, lelTime, FBX pivots and tangents, sRGB output (2026-10-25)

Added for New Frontier's port of the FORGE3D planets (Unity shaders reading uv2, ten textures, `_Time`):
- The display transform (`BuiltinShaders.TonemapFragment`, after exposure and the optional ACES) encodes with the exact
  sRGB transfer function (linear below 0.0031308, 1.055 x^(1/2.4) - 0.055 above), the curve sRGB textures are decoded
  with and Unity encodes with; it was x^(1/2.2), which lifted the deep shadows (up to 9 / 255 against Unity's frames).
  Verified on TestGame: the new frames are the old ones re-encoded (vct and gi=0 within 1 / 255; lumen, refraction
  and trails within their run-to-run noise). The images in docs/ were made before: their darkest parts are a little
  brighter than the engine draws now.
- `Vertex.texcoord2` (Unity's uv2, zero when a mesh has none): attribute `vTexCoord2`, location 7
  (`VertexLayout.TexCoord2Location`), after the bitangent (the other offsets are unchanged; `Vertex.Size` 80 bytes).
  The Assimp backend reads texture coordinate channel 1. Imported-model format 2 (`ModelAssets.FormatVersion`): every
  model imports once again. Anything that builds vertices from others (`WithTangents`, the import's scaling) keeps it.
- Material textures past 8 (`Material.TextureUnitAt`): a material's first 8 textures go on units 0..7, the rest on
  `Material.ExtraTextureUnitStart` (23, above the engine's reserved units 8..22) and up, as far as the context has
  units (`Material.MaxTextures`: 8 + GL_MAX_TEXTURE_IMAGE_UNITS - 23, 17 on 32-unit GPUs); past that a warning.
- `uniform vec4 lelTime` (`Lighting.TimeName`, set with the other lighting uniforms): Unity's `_Time` from
  `Time.timeD`: t / 20, t, 2t, 3t. It runs in edit mode too (the window advances `Time.timeD`); play mode restarts it.
- Assimp import (backend version 2):
  - `IMPORT_FBX_PRESERVE_PIVOTS` on, and `Visit` folds the `<model>_$AssimpFbx$_<part>` helper chain back into one
    node per FBX model: the transform parts (pre / post rotation, pivots, offsets) make its local transform, the
    geometric ones (`Geometric*`, 3ds Max object offsets: they move the model's own geometry, not its children) are
    baked into its mesh (positions, normals by the inverse transpose, triangles turned round when they mirror), as
    Unity does. Before, Assimp merged them into the node, so a mesh used on its own lost them (FORGE3D's ring: -90
    degrees about X and x2.54 that Unity has in the mesh). Children hanging off the chain keep the node as parent.
  - Tangents come from `Mesh.WithTangents` (as for .obj files), no `CalculateTangentSpace`: Assimp's bitangents point
    along -v, the engine's lit shaders (and Unity) read the normal map's green along +v, so normal-mapped FBX models
    had their bumps lit upside down. Identical vertices that only Assimp's tangents had split now join (ak.fbx 7380 ->
    7284 vertices; nodes and bounds unchanged).
- Verified: TestGame A/B against HEAD (lumen, vct, gi=0, refraction, trails): identical or below run-to-run noise;
  ak.fbx / Axe_HighPoly.FBX imported by the old and new backend (same nodes and bounds, flipped bitangents); New
  Frontier's planets against Unity's renders (40 shots, mean difference 0.3..4 / 255, the rest texture compression).

## LitTextured: workflows, maps, GI surfaces (2026-10-28)

Added for New Frontier's ships (Unity Standard materials: metallic / occlusion maps, specular setup, tiling, the burnt
wrecks' albedo-tinted highlight):
- `Engine/LitTextured.shader` properties: `Color`, `DiffuseMap` + `DiffuseMap_ST` (Unity's main tiling / offset, shared by
  the normal, mask and specular maps), `NormalMap` + `NormalScale`, `Emissive`, `EmissionMap` + `EmissionMap_ST` (its own),
  `Workflow` [KeywordEnum(Scene, Metallic, Specular)], `Roughness` (0 = unset -> 0.6), `Metallic`, `SpecularColor`,
  `SpecularMap` (rgb x SpecularColor), `MaskMap` (HDRP layout: r metallic in place of Metallic, g occlusion, a smoothness
  x `SmoothnessScale` in place of Roughness; linear), `OcclusionStrength` (Unity's: lerp 1 -> map, indirect light only).
  Map keywords NORMAL_MAP, EMISSION_MAP, MASK_MAP, SPECULAR_MAP: a material without a map never samples it (the old
  shader always sampled the default normal and emission textures). Every material has a WORKFLOW_* keyword (an enum's
  option 0 is one too), so existing materials now draw with the WORKFLOW_SCENE variant.
- Scene workflow = the old shader's lighting (Blinn-Phong with the scene's Specular Strength / Shine, indirect specular
  x specStrength), proved identical: TestGame materials=1 at gi=0, legacy sphere region bit-identical against HEAD.
  Metallic / Specular = Unity's BRDF1 (`Engine/StandardBRDF.glsl`: Disney diffuse, GGX with roughness^2 clamped 0.002,
  Unity's Smith joint approximation, x pi in the engine's units, surface reduction, grazing Fresnel lerp; specular
  colour from metallic (0.04 dielectric) or SpecularColor, monochrome energy conservation). GI terms: gi.diffuse = flat
  ambient x the GI's occlusion + indirect diffuse, gi.specular = the GI's reflection, both x the mask's occlusion; the
  scene's specStrength is not applied (the material's reflectance is).
- Mask smoothness gives roughness max(1 - a x scale, 0.03): the GI reads roughness 0 as "unset" (probe path, G-buffer).
- GI captures (`Engine/GISurface.glsl`, C# `Rendering/GISurface.cs`, used by `VoxelGIPass` and `SurfaceCachePass`, which
  had the same code twice): albedo and emission with the material's tiling; for WORKFLOW_METALLIC / SPECULAR the bounce
  colour is the diffuse colour plus the specular colour x 0.45 (Unreal's EnvBRDFApproxFullyRough for Lumen cards: a metal
  keeps its colour instead of turning black); `CaptureKey` hashes all of it. Mask / specular maps on units 2 / 3.
- Fixed: the captures read textures upside down (`1 - v`, a leftover from before 2026-10-16, when images were not yet
  stored bottom row first). Verified with TestGame materials=1's two-tone panel: cacheview=1 / voxels=1 now match the
  render (HEAD had the halves swapped).
- G-buffer: `Engine/GBuffer.shader` variants NORMAL_MAP / MASK_MAP (its Properties block declares the defaults of what
  they read). `GeometryPrepass.MapsOf(material)` = the bits of those keywords in the material's variant; such buckets
  (`OpaqueBucket.GeometryMaps`) go last in the GPU scene's instance table (after the per-object parts), the prepass draws
  everything before them in one call (`GpuScene.DrawCameraPlainGeometry`, an extra cull segment only when map buckets
  exist) and each map bucket with its variant and `material.SetUniforms`. Per-object path the same per sub-mesh. So the
  screen-space GI (VCT resolve, Lumen screen probes) integrates with the normal map and the mask's roughness, and the
  resolve's upsampling compares the shading normal with the same normal (it compared the mapped one with the vertex one).
- Verified: TestGame lumen (dumpframe=400) / vct / gi=0 against HEAD identical (max 1 / 255; a time-based dump in Lumen
  differs by convergence alone, use dumpframe); probe project `-executeMethod VariantCheck.Run` (scratchpad `mat/Probe`):
  glslang on all 48 LitTextured keyword combinations + 4 G-buffer variants + the 3 capture shaders (438 stage sources, 3
  GPU kinds) and the GL driver links all 109 programs with their instanced ones. GPU cost (TestGame materials=1 close-up,
  9 samples x 2 runs): geometry prepass 0.166 -> 0.177 ms, opaque 0.584 -> 0.639 ms with the PBR spheres filling the view.
- TestGame `materials=1` (`BuildMaterials`, code-made 64 x 32 UV sphere, procedural checker / mask / stripes / two-tone
  textures): Scene workflow + normal map, gold metal, red dielectric, mask map tiled 2 x 2 with a normal map, Specular
  workflow with a copper checker specular map, emission map tiled 4 x 1, a rough Metallic block, the V-check panel.

## Detail maps and LOD groups (2026-10-30)

Added for New Frontier's port of Unity's SpaceStonesPack (Standard materials with Detail Albedo x2, LODGroup prefabs):
- `Engine/LitTextured.shader`: `[Toggle(DETAIL_ALBEDO_MAP)] DetailAlbedoMap` ("gray" default) + `DetailAlbedoMap_ST`,
  `[Toggle(DETAIL_NORMAL_MAP)] DetailNormalMap` + `DetailNormalScale`, both on the first UV set with the detail tiling
  (Unity's `_UVSec` 0; no detail mask). Albedo x detail x 4.59479380 (Unity's x2 in gamma space = unity_ColorSpaceDouble
  of a linear project); the detail normal only with NORMAL_MAP (as Unity's `NormalInTangentSpace`): Unity's
  UnpackScaleNormal (xy x scale, z rebuilt) and BlendNormals (xy added, z multiplied, normalized) on the normal map's
  tangent-space normal. 3 x 64 keyword combinations.
- `Engine/GBuffer.shader` variant DETAIL_NORMAL_MAP: `GeometryPrepass.DetailNormalMapBit` (4, set only with
  `NormalMapBit`), `MapKeywords` / programs for 8 bit combinations, so the screen-space GI sees the detail bumps.
- GI captures (`Engine/GISurface.glsl`, `Rendering/GISurface.cs`): the detail albedo on texture unit 4 (`voxDetailAlbedoMap`,
  `voxDetailST`), the result clamped to 1 (the coverage accumulators are fixed point), `CaptureKey` hashes it.
- `LODGroup` (`Mono/Behaviours/LODGroup.cs`, global namespace, `[ExecuteAlways]`): Unity's API and serialized names
  (`m_LocalReferencePoint`, `m_Size`, `m_FadeMode`, `m_AnimateCrossFading`, `m_LODs` of struct `LOD`
  `screenRelativeTransitionHeight` / `fadeTransitionWidth` / `renderers` (MeshRenderers)); `size`, `localReferencePoint`,
  `lodCount`, `GetLODs` / `SetLODs` (copies), `ForceLOD`, `RecalculateBounds`, `GetRelativeHeight(camera)` = size x
  largest |lossyScale| / 2 / (distance to the reference point x tan(FoV / 2)) (Unity's LODGroupGUI formula),
  `CurrentLOD` / `CurrentRelativeHeight` of the last selection. Enabled groups register themselves (`OnEnable`, a static
  list, edit mode too: the target-texture renderers get no behaviours list, so the list is the source).
- Selection (`LODGroup.Select(camera)`, called by `Renderer.RenderFrame` before `Partition` with the stack's lighting
  camera; target-texture cameras with their own): per group the first level whose height <= relative height x
  `ProjectSettings.LOD.Bias` (Unity's lodBias), at least `MaximumLevel` (Unity's maximumLODLevel), none below the last
  level (culled); stamps on the renderers (`MeshRenderer.lodHidden` / `lodOutOfGI` = the selection's number, so nothing
  has to be reset). `Partition` leaves hidden renderers out of every camera list (so out of drawing, the prepass, shadows
  and the GPU scene) and builds the lighting camera's `RenderContext.GIRenderers`: level 0 of every group in place of
  the drawn level (Unity's lighting uses LOD 0; switching levels changes nothing in the GI: VCT's static cache counts
  renderers, Lumen's cards and fields would be recaptured). `VoxelGIPass`, `SurfaceCachePass`, `GlobalDistanceFieldPass`
  read `GIRenderers ?? Renderers`. Without active groups the lists are as before. One selection per frame for a camera
  stack (overlay cameras draw the lighting camera's levels: the GPU scene's tables are shared). No cross-fade.
- `ProjectSettings.LOD` (`LODSettings`: `Bias` 1, `MaximumLevel` 0), `QualitySettings.lodBias` / `maximumLODLevel`
  (global `QualitySettings`, Unity's names over the live settings); Project Settings > Lighting > Level of Detail.
- Editor: `Inspector/LODGroupInspector.cs` (custom part below the fields: the levels as Unity's bar with their heights
  and the culled part, the level the last frame drew, Recalculate Bounds with undo). `SerializationUtility.NicifyName`
  keeps an acronym's plural one word ("m_LODs" -> "LODs", was "LO Ds").
- Verified: probe project (scratchpad `stones/VariantProbe`): glslang + GL link of 192 LitTextured combinations, the 8
  G-buffer variants and the captures (204 variants, 405 programs with the instanced ones); TestGame lumen / vct / gi=0 /
  materials=1 (three GI modes) / refraction=1 against HEAD at frame 400: identical (0 of 255); New Frontier's pack shots
  against Unity (mean 0.75-1.8 / 255); a rock's levels from 10 to 1500 units, bias 1 and 2, GI off and on, forced and
  disabled (the levels Unity's formula gives).

## Input system (Rewired-like, 2026-10-22)

- Runtime in `LELEngine/InputSystem`: `InputConfiguration` (a ScriptableObject asset: `Settings`, action categories,
  `Actions` (Axis / Button, category, behavior, positive / negative names), map categories, layouts, `Behaviors`
  (digital axis simulation, mouse sensitivity, button dead zone, double / short / long press times), `Players` (keyboard /
  mouse on start, gamepads, starting maps), `Maps` (per controller type, map category and layout; `ActionElementMap`:
  element code, contribution, axis range, invert, modifiers)), `ControllerTypes` (Keyboard = OpenTK `Keys` codes, Mouse =
  `MouseElement`, Gamepad = `GamepadElement`: the standard layout GLFW maps controllers onto; names, element lists),
  `Controllers.cs` (`KeyboardController` / `MouseController` polling `Input`, `GamepadController` from GLFW slots or
  virtual: `SetButton` / `SetAxis`, radial stick dead zone), `InputPlayer` (Rewired's Player: `GetButton` / `Down` / `Up`,
  `GetNegativeButton*`, `GetButtonDoublePressDown`, `GetButtonTimedPress*`, `GetButtonShortPress*` / `LongPress*`,
  `GetButtonTimePressed`, `GetAnyButton*`, `GetAxis` / `Raw` / `Previous` / `Delta` / `2D`, `GetBindingName` for prompts,
  `LastActiveController`, `AddController`, `LoadMap`, `SetMapsEnabled`; per action the largest analog value wins, keys
  push ±1 through the behavior's digital axis), `InputManager` (ReInput: started on the first frame with Project
  Settings > Input > Configuration, updated from `Input.BeginFrame` with real time; gamepads connect / disconnect /
  auto-assign / go back to their previous player; `CreateVirtualGamepad`; `SaveUserData` / `LoadUserData` /
  `ClearUserData` to `UserDataPath`: LocalAppData/LELEngine/<exe>/ in a player, the project's Library in the editor;
  `Shutdown` when play mode ends), `InputMapper` (runtime remapping: listens after release, Escape cancels, timeout,
  conflicts Replace / Swap / Allow / Cancel, `Finished` event). `Input.GetMouseButtonDown` / `Up` were added for it.
- The type `InputAction` clashes with OpenTK's GLFW `InputAction`: files using both namespaces alias it
  (`using InputAction = LELEngine.InputSystem.InputAction;`).
- Editor: `InputSystem/InputManagerWindow.cs` (Window > Input Manager; double click on the asset; Project Settings >
  Input tab names the configuration; Create > Input Configuration): pages Settings, Players (starting maps), Actions,
  Action / Map Categories, Layouts, Input Behaviors (FieldDrawer), Keyboard / Mouse / Gamepad Maps (per action rows; the
  element button listens for the next key (with modifiers), mouse button / wheel or gamepad button / stick (GLFW in edit
  mode, `EditorGamepads`); the arrow picks from every element), Debug (play mode: every player's actions live, gamepads
  with owners). Edits go through the inspector context (undo steps, the asset saves when an edit ends). Builds ship the
  configuration (`BuildDependencies`: "Project Settings > Input").
- Test hooks: `inputmanager=N;Page` (KeyboardMaps, GamepadMaps, Debug, ...), anchors `input:Page`,
  `binding:Action/Element`, `listen:Action`, `action:Name`; `key=N;G` presses for listening. Verified 2026-10-22 on New
  Frontier: a virtual gamepad flew the ship through the actions (stick 1.0, aim, 17 shots from the trigger, a skill),
  a held key went down / held / up, `LastActiveController` and the HUD's binding labels followed the device; listening
  bound G and undo restored Space (the asset file byte-identical).

## Project panel, asset moves, model import (2026-10-22)

- `AssetDatabase.MoveAsset(from, to)` renames / moves a file (with its meta) or a folder (case-only renames through a
  temporary name), rebuilds the index and calls `InternalStorage.AssetMoved`, which re-keys every path-keyed cache
  (materials, shaders, textures and sprites, models and meshes, prefabs and resolved prefabs, ScriptableObjects, physics
  materials, audio clips and projects, UI sheets / documents / fonts, VFX graphs) and sets the loaded objects'
  `AssetPath` (setters made internal): loaded assets keep their identity and save to the new path. `MovedPath` maps a
  path. `EditorApplication.MoveAsset` also moves the scene path, prefab stages, the selection, the inspector lock,
  Project.json scene lists and the project settings' audio project / input configuration paths; not in play mode.
- `ProjectPanel`: folder tree with folder icons (open / closed), the shown folder's parents opened, a breadcrumb above the list (it never scrolls); the
  file list shows folders first as rows (click selects, double click opens, context menu Open / Rename / Show in Explorer),
  files with icons per kind (`EditorIcons.KindOfFile` / `ColorOf`: folder, scene, script, shader, material, texture (its
  thumbnail), model, audio clip / project, VFX, UXML, USS, font, physics material, input configuration, text, prefab /
  variant, ScriptableObject). Rename: F2 or the context menu, inline field where it started (tree or list), Enter / click
  away commits after the panel is drawn, Escape cancels. A click on empty space (here or in the hierarchy) clears the
  selection and the inspector (`ClearSelection`); the panel follows the editor's selection (a GameObject selected
  elsewhere clears the file highlight).
- Unity-like expansion (`DrawFileRow`, recursive, pixel indent): a model file has an arrow (anchor `expand:<path>`), and
  only such rows have room for it (the user's call: other files line up with the folders); expanded, it
  shows its companion files (an OBJ's material library from its `mtllib` lines and the material and texture assets of
  its import (`ModelImporter.ExtractedGuids`), same folder; listed only there, not on their own: `listedCompanions`) as
  full rows and its
  meshes as sub-asset rows (anchor `project:<model>/<mesh>`; a click
  selects the model, a drag carries `DragItem.FromFile(model, mesh)` on to a Mesh field). Renaming a companion .mtl
  rewrites the OBJ's `mtllib` line. A pinged or selected companion opens its owner. Shift ranges and multi-file drags
  use the rows as drawn last frame (`shownRows`).
- Models import completely as soon as they are in the project: `ModelAssets.ImportOutdated` (new / changed file,
  settings or importer; with `verifyMaterials` also an import whose extracted material files (of materials a mesh
  uses) are gone) runs when a
  project opens (editor and batch mode) and after every asset refresh (watcher); a loaded model that reimported is
  replaced by reloading the scene. Model import settings: `importMaterials` (Materials header) chooses extracted
  material assets or the default material.
- The inspector draws the materials of the selected object's renderers below its components (full material editors, as
  Unity); engine and runtime materials are shown disabled.

## Thumbnails (2026-11-03)

- Lists and object fields never load a texture for its icon (`AssetThumbnails.TryGet(path)`, `Inspector/ObjectPicker.cs`):
  a 32 x 32 RGBA thumbnail (4 KB) from `Library/ThumbnailCache/<hash of the path>.thumb` (the header holds the file's
  stamp, so a changed file makes a new one), made when missing on a worker thread, one at a time (a 4K image decodes to
  64 MB). Only visible rows ask (`ImGui.IsRectVisible`), so nothing queues: a row scrolled away starts no work; until it
  is ready a row draws its icon. The stamp is checked once a second per thumbnail (no asset reload sees a file nobody loaded).
- Why (the user's report, 2026-11-03: pinging a texture in a material's inspector took the editor down, and the whole GPU
  on another machine): `TryGet(path)` was `TextureAssets.LoadTexture`, and the file list has no clipper, so the ping's
  folder went whole to the GPU in one frame, mips included, and stayed there: New Frontier's `SpaceStonesPack/Textures/MainMaps/Emission`
  is 225 images, ~19 GB. The same in the picker of any texture field (every texture of the project: ~29 GB).
- The texture importer's inspector still shows the real texture (the preview is its point); so do runtime textures in fields.
- Verified on a scratch project with a copy of that folder, under a memory guard (never against the old code at full
  size, nor on New Frontier itself: it can hurt the GPU): `click "button:Albedo"` in the material's inspector, then
  `button:pick`. GPU memory of the process unchanged, 21 thumbnails for the rows shown, no other full texture loaded; cold 3 s
  for the 10 visible rows and +400 MB of transient RAM, warm no decode and +15 MB. The picker of a Sprite field lists by the import settings
  (`TextureAssets.SpriteNames`), not `LoadSprites`, which loaded every image of the project: on the same copy 266 entries,
  no image loaded, no GPU or RAM change, picking one loads that one; names equal to `LoadSprites` for a sheet and a single
  sprite. Object fields draw a whole-file sprite by its thumbnail; a sprite of a sheet still needs the sheet's texture.

## Console, editor preferences, theme (2026-10-22)

- `Debug` records the caller's stack for every message (`Debug.SetStackTraceLogType(LogType, StackTraceLogType)`,
  Unity's None / ScriptOnly / Full; ScriptOnly drops System/Microsoft/OpenTK/Hexa/Silk frames; nothing is captured
  without a `MessageLogged` subscriber). Stdout still gets the text (stack only for exceptions, plus inner exceptions
  as `--- Caused by` lines); `Debug.Output` routes it: the editor points it at the real stdout and takes the message
  from `MessageLogged`, so `ConsoleLog` no longer parses Debug text (only other console writes, as before).
- Direct console writes (the engine's 70-odd `Console.WriteLine`s: `[ProbeGather]`, `[SDF]`, ...) get the writer's
  call stack too (`Debug.CaptureStackTrace(mode, skipCaller)`, skipping ConsoleLog and System writer frames).
- Every entry is `ConsoleSource.Game` (a frame in the project folder, a diagnostic of a project file, or logged inside
  `ConsoleLog.BeginGame/EndGame`: play mode frames, the play scene's start, Step) or `Editor`. Preferences > Console >
  Editor messages hides the editor's plain messages (not counted either); its warnings and errors always show. The
  game's messages are filtered in the console window as before. The details' meta line names the source.
- `ConsoleLog` entries are `ConsoleEntry` objects (text, stack, type, time, play-mode frame, weak context reference so
  unloaded script objects can go, `Sticky` for script build diagnostics and the build's failure summaries: no Clear and
  no `MaxEntries` trim removes them, `ScriptDomain.OnBuildFinished` replaces them with the finished build's (they stay
  while a build runs), `ScriptDomain.Dispose` drops them). `RemovalVersion` tells the panel to rebuild instead of
  appending new rows.
- Compile errors of the game or editor scripts (`ScriptDomain.HasErrors`; test errors only unload the tests) block
  play mode and game builds (2026-10-30): play asked for while scripts compile (`ScriptDomain.IsCompiling`: a build
  running or waiting) waits for that build (`EditorApplication.IsWaitingToPlay`, the Play button lit, Ctrl+P again
  cancels; CLI `play` waits and fails on errors) and starts only without errors; the Build tab's button is disabled
  while compiling and fails at once with errors (`buildgame=` hook too). Batch `-build` already failed on them.
- `ConsolePanel` (Console Pro style): toolbar Clear + options (Clear on Recompile, columns, lines per row, source view,
  library frames, stack trace capture, copy / save shown messages to `Logs/Console-*.txt`, Preferences), Collapse,
  Clear on Play, Error Pause (these stay in the console, not in the Preferences window), type toggles with counts,
  search (regex / case / call stacks), Tags menu ([Tag] prefixes), custom filter chips (click toggles, Alt+click solo,
  right-click edit; matches tinted). Rows via a clipper; narrow consoles drop Time/Source columns. Two parts only
  (list, then the message with its call stack; no separate source pane, the user's call): stack frames with the
  code around their line inline (`SourceSnippet`, line ± `SourceContextLines`, default 1 = three lines, shared
  indentation removed; a preview: cut at the console's edge, no wrapping, no scrollbars, a click opens the line), shown for
  project frames and the frame the message opens, a click toggles it on the others. Everything else in a narrow
  console fits the width: list rows get two wrapped lines with "...", the message wraps, frames shorten the method
  and put the file on a second line. The details child has no horizontal scrollbar on purpose (with it, wrapping
  followed last frame's content width and kept the content wider than the window).
  Project frames bright, engine dim, libraries hidden, logging wrappers (`HiddenFrames` prefixes) skipped as the open
  target; lambdas / iterators shown as their method.
- `SourceFiles` + `CSharpLexer`: frames' files (cached, reloaded on change), C# highlighting with line states for
  multi-line comments / strings; double-click opens the line. `ExternalScriptEditor` opens files at a line (Auto: Rider, Visual Studio,
  VS Code where they install; Custom with `$(File) $(Line) $(Column) $(ProjectPath) $(SolutionPath)`); script assets
  opened from the project panel and inspectors go through it too.
- `EditorPreferences` (`%APPDATA%\LELEngine\Editor\preferences.json`, outside projects): Theme, Console, External
  Tools, Toggles (the windows' toggles by name: `EditorMenu.Toggle`, `GetToggle` / `SetToggle`); saved 0.4 s after `MarkChanged`, re-read when another editor changes the file. Automated runs use defaults and
  save nothing unless `preferencesfile=path`. Edit > Preferences... is `PreferencesWindow`.
- `EditorStyle`: presets Dark / Midnight / Light / Classic, editor colors `EditorColor` (Warning, Error, Link, Code*
  ...) plus every ImGuiCol overridable by name, text size via `style.FontSizeBase`. `ImGuiController.ApplyStyle`
  re-applies it per window when `EditorPreferences.ThemeVersion` changes (Audio Studio adds its own style on top).
  Monospace font per context: `EditorFonts.PushMono(size)` (Consolas / Cascadia Mono / ImGui default).
- Hierarchy folding (2026-10-24): rows start folded; `HierarchyExpansion` keeps the unfolded rows (`Expanded`) per project folder + scene / prefab path
  (object ids) in `%APPDATA%\LELEngine\Editor\hierarchy.json`, never in the project; saved 0.5 s after a change and
  in `SaveEditorState`. Automated runs read it and save nothing unless `hierarchyfile=path`. The editor has no
  `exitseconds=` (that is TestGame's): use `exitframe=`, and keep it small: New Frontier's Flight scene runs far
  slower than 1000 fps in the editor (a script reload with the leak check finished well within 2000 frames;
  `exitframe=30000` ran for minutes). Always `WaitForExit(timeout)` and stop the process on a timeout.
- Scene open last and Edit > Reload Scripts On Play are the user's, per project folder in `%APPDATA%\LELEngine\Editor\windows.json`
  (`WindowStates.LastScenes` / `ReloadScriptsOnPlay`, saved with the window states), not in Project.json (2026-10-27).
- Automated runs read the user's `layout.ini` (and the other windows' ini files) but never save them
  (`ImGuiController.ReadOnlyLayouts`), and their projects stay out of `recent.json`: a test that docked the console
  elsewhere once moved the user's real console.
- Test hooks: `log=N;Type;text` (nested calls, Exception with an inner cause), `consoleselect=N;text`,
  `preferences=N;Section`, `dockwindow=N;Window;Target` (dock next to another window, e.g. Console;Inspector for a
  tall narrow console), anchors `console:row`, `consoleframe:i`, `consolefilter:Name`, `preferences:Section`.

## GI method: fixed while running, only the active one allocates (2026-10-22)

- `Lighting.GIMethod` is the method that runs: `GI.Mode` latched at the first frame. In a player and in the editor's
  play mode it never changes: a later `GI.Mode` write logs one warning (`[GI] The GI method cannot change while the
  game runs`) and is ignored. The editor sets `Lighting.AllowGIMethodChange` in edit mode only (`EditorApplication.Update`),
  so Project Settings, undo or a reload from disk switch it there (`[GI] Method A -> B`); the GI Method combo is
  disabled in play mode. Engine code reads `GIMethod` (or `GI.VoxelConeTracingActive` / `LumenActive` /
  `DistanceFieldActive`), never `GI.Mode`. TestGame sets the method at start (`gi=vct`); its G toggle is gone.
- Every GI pass makes its shaders and textures on its first active frame and frees them when its part is not used:
  `VoxelGIPass` (VCT; only the selected mips' directional textures and shader variants, coverage accumulators only
  with `VoxelCoverage`), `GIResolvePass` (VCT + `ScreenSpaceResolve`, the variant in use), `GlobalDistanceFieldPass`
  (`DistanceFieldActive`: Lumen, or VCT with an SDF trace mode; `VoxelCones` builds no field and no mesh SDFs),
  `SurfaceCachePass` / `RadianceCachePass` / `ScreenProbeGatherPass` (Lumen), `LumenScene` (made by its first
  `Update`; surface cache only in Lumen; `Renderer.ReleaseLumenScene` when neither needs it), the GI's sky cubemap
  (`SkyEnvironment.Clear` with GI off), debug views (on first show). With GI off nothing is allocated.
- A renderer that does not draw (hidden editor view) still frees at once: `Lighting.BeginFrame` raises
  `GIUsageChanged` when (method, scene GI on, distance field used, resolve used) changes, and every `Renderer` calls
  `RenderPass.ReleaseUnusedResources` on its passes. Releases are logged (`[VoxelGI] Volume released`, `[SDF] Global
  distance field released`, `[SurfaceCache] Atlas released`, `[GI] Scene distance fields and tables released`).
- Measured on TestGame (128^3): VCT volume ~112 MB aniso / ~98 MB SH / ~85 MB iso (was ~126 MB with every kind of
  mips), Lumen no voxel volume, VCT no surface cache (46 MB), `trace=voxel` no SDF. Frames of the 6 configurations
  (lumen, vct, vct trace=voxel, voxmips=sh, voxmips=iso, gi=0) match the previous build within 1 level (run-to-run
  noise). Verified in the editor with menu items setting `GI.Mode` in edit mode, play mode and after stop.

## GI sky lighting (2026-10-22)

- The GI never samples the skybox texture itself: `SkyEnvironment.LightingCubemap` resamples it into its own
  cubemap (`FaceSize` 128, RGBA16F, box filter of taps x taps per texel in `Engine/SkyLighting.shader`, full mip
  chain), rebuilt when the source changes. Escaped cones read the mip of their aperture (`SkyRadiance(dir, aperture)`
  in `Sky.glsl`: lod = log2(4 * aperture * faceSize / pi)), rays the diffuse level (`giSkyboxLod`). Before, every cone
  read a fixed level of the skybox as imported: a skybox without mips put a sharp sky into rough reflections.

## Scene view navigation (2026-10-22)

- `SceneViewPanel`: the wheel moves 20% (`WheelShare`) of the distance to the point under the mouse (picked object,
  else the ground plane), so it works at any distance; middle-drag pans by the focus distance (world per pixel =
  2 * focusDistance * tan(fov / 2) / view height). `FocusDistance` follows wheel, pick, frame (F) and is saved with
  the view. Test hooks: `wheel=N;sceneview;notches|...`, `mdrag=N;sceneview;dx,dy|...`, `logsceneview=N|M`
  (position, forward, focus distance); anchor `sceneview`.

## Physics 2D gizmos in the scene view (2026-11-02)

- `Inspector/Physics2DGizmos.cs` (one instance in `SceneViewPanel`). The user's rule: the selected object draws the gizmo
  of a physics component only while it is expanded in the inspector (as in Unity), and those gizmos edit the component.
  Only the selection's own components: children are not in the inspector, so they draw nothing (they did before).
  Disabled components draw nothing. `InspectorPanel.IsExpanded` / `EditorApplication.IsExpandedInInspector`: the
  panel records each header's state by component id as it draws it (not during a `WindowCapture`, whose fresh ImGui context
  starts every header open), so the answer survives a hidden or closed inspector; never drawn = expanded.
- Everything draws on the view window's ImGui draw list, not `DebugDraw`: `DebugDraw` is depth tested (outlines hid inside
  meshes) and only reaches the screen with a new render. `DrawCapture` calls `Draw` too, so layout captures show them.
- Outlines are what the physics builds, not the scaled field values: keep them in step with the colliders' `BuildShapes`
  (`MinExtent`, a circle's radius times the larger scale, a capsule computed in world units, so both stay true shapes
  under a non-uniform scale; the edge radius as a dim contour around the box or the lines). `ShapeSpace` is the physics'
  own space: `PlaneBasis.PlanarPose` (no owner: a body's frame changes nothing in the world) and the placement of
  `Physics2DMath.Place`, so a tilted object's shape lies flat in the simulation plane at the object's height, scaled by
  its axes projected on the plane, never in the object's own plane (the first version did that, 2026-11-02: New Frontier's
  asteroids, rotated in 3D with scale (x, y, z) different per axis, drew tilted up to 87 degrees and up to 39% too large
  against r x max(projected X, projected Z), which Box2D's bounds match on all 60). Back by ray / plane intersection, no
  matrix inversion; an edge-on plane or a non-number gives no point, so nothing bad reaches a field.
- Handles: box 4 sides + 4 corners, capsule 4 sides (the opposite side stays: size and offset change, only on the axes the
  handle moves; Alt: around the center; a capsule's long side stops at its width, where the physics would stop it),
  circle 4 radius handles (the center stays), polygon / edge points (a press on a line adds a point and drags it, Ctrl +
  click removes one down to 3 / 2), a diamond at the center of every shape moves the offset. Values are computed from
  the state at the press every frame (`Drag`), so Alt can change during a drag. Joints only show their anchors.
- With the transform gizmo: `Update` runs before `ImGuizmo.Manipulate` and puts one `InvisibleButton` under the mouse when
  a handle is hot: ImGuizmo's `CanActivate` refuses over a hovered item, so a handle on one of its axes takes the press
  and no grey "disabled" gizmo flickers (`ImGuizmo.Enable(false)` would). Exceptions: the diamond and the new-point spot give
  way where `ImGuizmo.IsOver()` (object moves come first; the diamond at the pivot, offset 0, needs Alt). A hot handle
  blocks the click pick; wheel, middle and right mouse work over handles.
- Undo: `Edits.Begin` at the press (again every frame: an undo during the drag ended the edit), `RequestCommit` at the release:
  one step per drag, also for add + drag. During a drag `EditorApplication.MarkEdited` (dirty, prefab stage's "edit settles"
  clock, no new render: collider values show nowhere in the image, and heavy scenes stay smooth); `MarkDirty` at the end
  (`PrefabInspector.OverridesOf` caches by `SceneVersion`).
- `SceneViewPanel.ApplyWorldMatrix` ignores a non-finite matrix: losing the OS window's focus during a transform gizmo drag
  sets ImGui's mouse to -FLT_MAX (`ClearInputMouse`), ImGuizmo's ray and result become NaN and they used to land in
  the Transform (position, rotation, scale; seen once by accident, then every time with `focuslost=`).
- Test hooks: `ldrag=N;anchor;dx,dy|...` (the left button by pixels from where the anchor was at N, press N + 1, release N + 11),
  `altdrag=` (Alt held), `focuslost=N|M`; anchors `gizmo:BoxCollider2D/Right`, `/Top`, `/TopLeft`, `/Center`, `/Point2`,
  `/Line1` (middle of a line). Local +X is on the left of a camera looking along +Z (`Right` is on screen left).
- Verified (scratchpad `GizmoProbe` of session 69059cbb: `GizmoSetup.Run` builds a row of objects, the camera in
  `Library/EditorState.json`): logged values of every handle against the pixels moved, undo after each; a side handle on
  the gizmo's axis leaves the Transform alone, the diamond at the pivot moves the object, with Alt the offset; rotated 30
  degrees + scale (2, 0.5), mirrored, non-uniform circle and capsule, two colliders on one object; collapse / expand one of
  them (click, CLI `click "header:Box Collider 2 D"`); click on a handle keeps the selection, elsewhere picks; wheel and pan
  over handles; project with the XZ plane and a tilted camera; play mode (`collider.bounds` of the live shapes follows,
  stop restores); prefab instance (overrides logged, undo) and prefab stage (Auto Save wrote the file, the instance followed);
  a script reload with the Scene tab hidden (old assembly unloaded); `screenshot Scene` with and without a size;
  New Frontier's `PelicanHull` read only (camera almost on the shape: lines cut at the near plane). Not checked by a
  run: the red colour of a point while Ctrl is held and a hover without a press (no hook holds Ctrl alone).
- Against the physics (scratchpad `GizmoXZ/Assets/Editor/GizmoCheck.cs`, batch `-executeMethod`, XZ plane, a copy of New Frontier's
  Flight scene and 300 random objects: rotations in 3D, scales per axis, negative ones, tilted parents, static and kinematic
  bodies): the real handle positions (`AddHandles` through reflection) in play mode, a step of 0.03 outwards must be outside
  `collider.OverlapPoint`, inwards inside (edges: `ClosestPoint`), and rays must find the same local point: 1706 points and
  3412 rays, none wrong. The first version's formula on the asteroids as a control: 60 of 60 wrong.

## GI volume placement

- `SceneGISettings.FollowCamera` + `FollowCameraLookAhead` (2026-10-22): the volume's centre is the camera position
  plus `RenderContext.CameraForward` x look-ahead, snapped to voxels (`GlobalIlluminationSettings.UpdateGridPlacement`,
  called by `VoxelGIPass` and `GlobalDistanceFieldPass`). For top-down / third-person cameras: one volume covers the
  camera's surroundings and the ground it looks at.

## AnimationCurve and the curve editor

- `LELEngine/Mono/AnimationCurve.cs` (2026-10-22): Unity's API (`Keyframe` time / value / in / out tangent,
  `Evaluate` = cubic Hermite with slopes scaled by the segment length, `preWrapMode` / `postWrapMode` Clamp / Loop /
  PingPong, `AddKey`, `MoveKey`, `RemoveKey`, `SmoothTangents`, presets `Constant` / `Linear` / `EaseInOut` / `EaseIn` /
  `EaseOut`). `Keyframe.tangentMode` (`TangentMode`: Free = 0 so keys without a mode keep their tangents, ClampedAuto =
  monotone Fritsch-Carlson slopes flat at ends and extremes, Linear, Constant = step, Broken) is what
  `RecalculateTangents` derives. Infinite tangents (Unity's steps) evaluate as Constant; presets never store infinity
  (JSON). Serialized inline as `m_Keys` / `m_PreWrapMode` / `m_PostWrapMode`.
- Editor: `FieldDrawer.DrawValue` hands `AnimationCurve` fields to `CurveEditor.DrawField`
  (`LELEngine.Editor/Inspector/CurveEditor.cs`): a preview on the field line, a click opens a popup editor (preset
  buttons mapped onto the curve's ranges, grid, keys, tangent handles of the selected key, right click key menu,
  double click adds, Delete removes, wheel zoom with Shift / Ctrl for one axis, middle or Alt drag pans, F frames,
  key fields, wrap modes). Edits use the inspector's undo (`context.BeginEdit` / `EndEdit`, `Track` for drag
  fields). `EditorGUILayout.CurveField`. Test anchors: `curvekey:N`, `curvecanvas` (e.g. `drag=N;curvekey:2;curvecanvas`).

## Component lookups and Rigidbody2D settings

- `GameObject` / `Behaviour`: `GetComponent<T>` / `GetComponents<T>` take interfaces (`where T : class`), plus
  `GetComponentInChildren` / `GetComponentsInChildren` (depth first, self first) and `GetComponentInParent` /
  `GetComponentsInParent` (nearest first), generic and by `Type`, with `includeInactive` (inactive = `activeSelf` off
  on the object or an ancestor) and list overloads. Dead components are skipped unless the searching object is dead
  itself (same rule as `GetComponent`). Added 2026-10-22 for the New Frontier port.
- `Rigidbody2D` settings are properties over Unity's serialized names (`m_BodyType`, `m_Material`, `m_Simulated`,
  `m_UseAutoMass`, `m_Mass`, `m_LinearDamping`, `m_AngularDamping`, `m_GravityScale`, `m_CollisionDetection`,
  `m_SleepingMode`, `m_Interpolate`, `m_Constraints`); setters push into the live body at once (`SettingsChanged`,
  `MassChanged`; deferred to `SyncToBody` while the world steps), and so does `OnValidate`. Since 2026-10-22: documents
  saved earlier with the plain names (`mass`, `bodyType`, ...) lose those values until re-saved.

## Inspector fields, drag and drop, polymorphism, ScriptableObjects

- Serialization (`LELEngine/Serialization`): an `Object` slot declared as an interface or `EngineObject`
  (`SerializationUtility.CanHoldReference`) holds an inline instance, a scene reference or a ScriptableObject asset.
  Binary: type id `-2` + uint64 id / `-3` + asset value in place of the Object's type id; JSON: `{"$ref": id}` /
  `{"guid", "path"}`; generic data keeps the Object tag with a `ulong` / `AssetReference` value. `ValueTag.Dictionary`
  (appended to the enum: values are bytes in files) stores count, key tag, value tag per value; JSON writes string /
  enum / integer / bool / char keys as an object, other keys as `[{"key", "value"}]`. Collections may nest
  (`List<List<T>>`, `Dictionary<K, List<T>>`): binary values carry their element tags; the JSON `$types` spec is
  recursive (`Dictionary<Int32,Array<Vector3>>`, `SchemaField.Element` / `Key`) so typeless conversion stays exact.
  `Documents.WriteObject` writes the root inline (`WriteInlineObject`): a ScriptableObject root is not a reference.
- `ScriptableObject` (`LELEngine/Assets`, an `EngineObject`): ".asset" Object documents loaded by
  `ScriptableObjectAssets` (type peeked with `Documents.RootTypeName`, instance cached before its fields are read,
  then Awake / OnEnable; `UnloadAll` on script reload and project close; `ReloadChanged` after outside edits; `Save`
  creates the meta file at once). `AssetDatabase.FindAssets` / `AcceptsFile` filter ".asset" files by stored class;
  interface / EngineObject requests load the file's natural type (`LoadMainAsset`). `Resources.Load` /
  `LoadAll` by path inside any folder named Resources.
- Editor (`LELEngine.Editor/Inspector`): `DragAndDrop` (payloads: hierarchy `LEL_GAMEOBJECT`, project `LEL_ASSET`,
  component headers and object fields `LEL_REFERENCE`; `TryResolve` maps a GameObject to its component of the slot's
  type, interfaces included), `ObjectField` (drop with green / red outline, click pings, double click selects, picker
  with Scene / Assets tabs), `FieldDrawer` (polymorphic fields: `TypeSelector` lists concrete types, [Serializable] not
  required), `CollectionDrawers` (lists: grip reorder, index, remove, count, add, drop to append; dictionaries: table,
  new-key row). Rows decorate any field kind through `InspectorContext.PendingRow` (taken by `BeginLine` / `Header`).
  The hierarchy selects on mouse release (a drag keeps the selection); the inspector has a lock (top right).
- References unfold in place only with `[InlineEditor]` (Odin's; on a list or dictionary it applies to the elements
  through `InspectorContext.InlineElements`), 2026-10-24; without it they are a plain object field. `InlineEditors`
  (`Inspector/InlineEditors.cs`): a ScriptableObject's fields, a scene component's fields (scene undo, override marks),
  a GameObject's components, a `Prefab`'s root components, also on interface / polymorphic fields holding a reference.
  A prefab is edited on a live copy (`PrefabPreview`, its file loaded as a detached scene, reloaded when the file's time
  changes, dropped in `ClearTypeCaches`); `EditTracker` captures it and on commit defers `RecordPrefabFiles` (one undo
  step, the scene reloads). Nested fields record test anchors as `nestedfield:Type.field` (top level stays `field:`).
  Verified on a scratchpad copy of New Frontier: drag in an inline Ship.prefab wrote the file, undo restored it.
- Icons (2026-10-24): `IconKind.Transform` (move arrows), `IconKind.ScriptComponent` (outline page with "< >", project
  scripts' components) and `.cs` files share the outline page drawing with ScriptableObjects (lines); `ReferenceColor`
  colors prefabs, scripts (green) and ScriptableObjects (blue) in object fields and headers from `ColorOf`, so the
  inspector matches the project panel. Project panel rows share one layout (`IconInset` 1, `LabelGap` 5, children's
  icons under the owner's name, the tree's `RenderArrow` on expandable files, tree rows full width and touching like
  the hierarchy's, rename fields as tall as a row with the text in place). `tree:` and `hierarchy:` anchors start at
  the label (full-width rows: a click at +40 px would hit a nested row's arrow).
- Inspector colors (2026-10-24): `[InspectorColor(InspectorColorKind.Physics)]` (theme colors `EditorColor.Component*`,
  Preferences > Theme) or `[InspectorColor(r, g, b)]` (fixed) on a component class gives its header a bar and tint and
  its body a faint background; the engine's components carry it too (`Mono/Attributes.cs`, read in `InspectorPanel`).
  Materials below the components are framed and tinted more strongly (`EditorColor.Material`).
- ImGui's asserts are on in the editor's own panels (only user callbacks run with error recovery): an unbalanced
  Push / Pop aborts the process (`Assertion failed ... "Calling PopStyleColor() too many times!"`, exit code 127).
  Pop by the condition that pushed, not by state the widget in between changed (the inspector lock crashed on its
  first click that way until 2026-10-24).
- Test hooks (TestHooks / TestAnchors): `drag=N;from;to|...` (press N, hover N+10, drop N+12), `click=`,
  `doubleclick=`, `rclick=`, `ctrlclick=` (N;anchor|...), anchors `hierarchy:Name`, `project:Folder/File.ext`,
  `project:(empty)`, `component:Type`, `field:Type.field[@fraction]`, `row:Label/i[/row]`, `button:AddEntry`,
  `button:InspectorLock` (the real lock button; `lockinspector=N` locks without clicking), `materialfield:Name` (a
  material's [KeywordEnum] combo, closed, or [Toggle] checkbox), `materialoption:Name/i` (option i of the open combo);
  `logfield=N;Object;Component.field|...`, `pingasset=N;path|...`, `lockinspector=N`, `undo=N|M`, `redo=`,
  `expandinspector=1`, `inspectorscroll=N;px`, `screenshotframe=a,b,c` (folder/fNNNN). Each hook name holds one
  value (repeat with `|`), and `Start-Process` splits arguments at spaces: no spaces in anchors.
- Verified 2026-10-20 with a probe project (scratchpad `Probe`: interface / abstract / EngineObject fields,
  dictionaries, nested lists, ScriptableObjects, Resources): batch `-executeMethod ProbeTests.Setup` / `Verify`
  (JSON, typeless JSON <-> binary, snapshot, duplicate remap), a game build and the player's log, scripted drags.

## Prefabs (variants, nested prefabs, overrides)

- `.prefab` files are scene documents of kind "Prefab" (one root, no settings): `SceneSerializer` (partials
  `.Document` generic model, `.Instances` instantiation). Scene and prefab entries are GameObjects or prefab instance
  records (`PrefabInstanceRecord`: prefab ref, parent, `PropertyModification`s, removed objects / components, added
  components; objects added under instance objects are ordinary entries after it). Binary documents are version 3:
  every entry starts with a kind byte. A variant is a prefab whose root is an instance of its base; nested prefabs
  are instance records inside a prefab. `Documents.Convert` handles both kinds typelessly (`ReadDocument` /
  `WriteDocument`), `DocumentReferences` sees prefab refs, builds ship prefabs used by scenes / Resources.
- Ids: an instance's object id is `instanceId ^ sourceId` (`PrefabIds`, XOR as Unity: associative, so nested
  prefabs, unpacking one level and connecting objects to a new prefab keep ids). References inside a prefab are in
  its own space; reading an instance's data resolves them through `SerializationContext.InstanceId` (`Find`:
  id, else id ^ InstanceId). Modification values hold ids of the document containing the instance.
- `PrefabResolver` / `PrefabCache` (engine, `Serialization/PrefabResolver.cs`): a `ResolvedPrefab` is the prefab
  with nested instances and variant bases expanded and their modifications applied, as generic data (typeless, any
  thread, shared and immutable); `Origin` / `OriginId` per object give the nested chain; cycles are detected
  ([ThreadStatic] resolving set). Instantiation (`SceneSerializer.BuildInstance`) reads unmodified components from the
  resolved prefab's cached binary and modified ones from a per-instance patch (generic node + modifications), both
  with the typed reader. `Prefab.Instantiate` / `GameObject.Instantiate(GameObject | Prefab)` / `Behaviour`'s
  protected `Instantiate` overloads are the runtime API.
- Live link: `EngineObject.prefabInstance` + `prefabSourceId` (`PrefabInstance`: Id, Prefab, Root, `Source` = the
  resolved data the instance was built from). Overrides are never recorded while editing: `PrefabOverrides.Compute`
  diffs the live objects (generic `ToNode`) against `Source` when a document is written (fields per path, a
  polymorphic object of the same class per field, another class / null / a reference as the whole value, list
  elements + `#size` (more than 32 element overrides and half the list: the whole list), dictionary entries by key
  incl. removed keys; root name / position / rotation are always written "default overrides", hidden in the UI).
  So a prefab change reaches instances by saving the scene to bytes and loading it again (`ReloadScene`,
  `PrefabUtility.ReloadScene`). Unused modifications (target gone) are kept and written back. A missing prefab's
  instance is a placeholder holding its record verbatim (`MissingRecord`); orphaned children keep
  `GameObject.unresolvedParent`.
- `PrefabUtility` (engine, public): queries, `SaveAsPrefabAsset` (an instance root becomes a variant; unpack first
  for an original), `CreateVariant`, `UnpackPrefabInstance` (OutermostRoot relinks nested instances), revert (live)
  and apply (load the target prefab as a scene with `EditPrefab`, set the value through a mapping context, save) of
  properties / added and removed components and objects / everything; `GetApplyTargets` lists the prefab chain
  (instance's prefab, then nested prefabs or bases down to the defining one; mask = scene id ^ stage id).
- Editor: `EditorApplication.Prefabs.cs` (prefab mode: `OpenPrefab` stacks `StageFrame`s with the replaced scene's
  snapshot, undo history and dirty flag; Auto Save (default) writes when an edit settles; leaving restores the
  snapshot, so instances rebuild; play / open scene / close leave stages first; `RecordPrefabFiles` = undo record
  of prefab files + meta files and scene snapshots, then reload; `Defer` runs scene-replacing actions after the
  frame's panels). `Prefabs/PrefabMenus.cs` (apply / revert as undoable, deferred actions), `PrefabInspector.cs`
  (prefab bar Open / Select / Overrides list, bold labels + blue margin bar via `InspectorContext.Overrides` /
  `OverrideTarget` / `Path` kept by `FieldDrawer` / `CollectionDrawers`, right-click Apply / Revert, "+" added and
  greyed removed components), `PrefabAssetInspector.cs`. Hierarchy: blue names (red missing), prefab icons on
  instance and nested roots (variant marker), "+" on added objects, ">" opens the prefab, breadcrumb in prefab mode;
  dragging a hierarchy object on a project folder creates a prefab (an instance root asks Original / Variant).
  Scene view: a click selects the instance root first, prefabs drop where the mouse points. Bold font:
  `EditorFonts.PushBold` (segoeuib.ttf per ImGui context).
- Verified 2026-10-21 with a probe project (scratchpad `PrefabProbe`, batch `-executeMethod ProbeTests.Run`, 67
  checks: overrides of every kind incl. polymorphic lists / dictionaries / interface references, JSON / binary /
  typeless conversion, base changes reaching variants, nested apply to outer / inner prefab, structure overrides,
  unpack, runtime instantiate, missing prefab round trip, cycles, Resources.Load), scripted editor runs (hooks
  `instantiateprefab=`, `openprefab=`, `createprefab=`, `setfield=`, `applyprefab=`, `undoat=`, `replacetext=`, ...;
  screenshots), a game build and the player's log.

## Player build output

- `GameBuilder` ships only what the build uses (`BuildDependencies`): the build scenes, every file under a folder
  named Resources, and what those use, transitively: asset references anywhere in documents (read typeless with
  `DocumentReferences`, nested and polymorphic values, lists, dictionaries included), a model's imported materials
  (else the built-in default material), a project shader's #include files, USS / UXML url() / resource() / src. The
  rest is left out; `Library/BuildReport.txt` lists each shipped file with its reason, the built-in engine resources
  used (`BuildDependencies.EngineResources`: not shipped in Data, the player has them; followed for their own
  dependencies) and the files left out. Stored
  type names are checked against the engine and the player's scripts (Mono.Cecil): types of Editor scripts or missing
  classes are reported. `Data/` is deleted before it is written.
- `GameBuilder` (`LELEngine.Editor/Building`) ships assets the player uses as they are:
  - documents (scenes, materials, meta files) as binary LELB, models as their imported binary;
  - images as KTX 1.1 (`KtxFile`) baked by `TextureBaker`: every mip level (box filter, in linear light for sRGB),
    BC7 by `Bc7Encoder` (own, mode 6 only, fast) or by BCnEncoder.Net for `TextureImporter.compression =
    BC7HighQuality` (16-38 s per 1K image), `None` keeps RGBA8; HDR stays RGBA16F, volumes RGBA8. Cached in
    `Library/TextureCache` (`TextureBaker.Version` in the stamp: bump it when the bake changes). Mode 6 has one p-bit
    per end for color and alpha together: since 2026-10-24 fully transparent ends and texels keep alpha 0 (white with
    alpha 0, the usual particle texture, decoded to alpha 1/255 and additive particles drew their quad faintly). Sprites
    and the images USS / UXML reference (`url()`, `resource()`) stay PNG: the UI draws images as they are;
  - `Data/.assetindex` (GUID to path): the player reads it instead of every meta file;
  - `<model file>.sdf` next to each model: the GI's mesh distance fields at every shape the scenes draw the mesh at
    (shape keys: the scale over its largest component, in thousandths; every uniform scale is shape 1) and at shape 1,
    baked by `DistanceFieldBaker` with the renderer's GPU build
    (needs the GL context: batch mode bakes on its own thread, the Project Settings window before its build task),
    cached in `Library/DistanceFieldCache`. Without them a big mesh's field takes seconds when its scene appears; the
    editor still builds them on first use (then keeps them on the `Mesh`).
- Batch builds: `LELEngine.Editor.exe -batchmode -projectPath <folder> -build <output> -logFile <file>` (the WinExe
  opens no console window, `LELEngine.Editor.Console.exe` does). The log has `Baked N textures to KTX ...` and
  `Distance fields: ...` lines.
- Reference (heavy test scene, Intel iGPU, warm): scene read 925 -> 47 ms (PNG decode was 898 ms of it), worst frame
  after activation 2.9 s -> ~100 ms with baked distance fields; the rest is the GI (surface cache, global SDF)
  catching up.

## Voxel GI (voxel cone tracing)

- `VoxelGIPass` lights the volume from scratch every frame, no state carried between frames: 1 +
  `BounceIterations` injection passes ping-pong two volumes, each bounce pass tracing the one before
  (`BounceCells`: the hemisphere cones once per 2x2x2 voxels around their average normal, `VoxelBounceCells.shader`).
  Coverage voxelization (`VoxelCoverage`): 8x MSAA rasterization splatted over the two nearest voxels along the
  projection axis into R32UI fixed-point accumulators (`VoxelizeCoverage.shader`, order independent), resolved by
  `VoxelResolveGeometry.shader` (occupancy = 2 x weight). Mips (`VoxelMips`, `VoxelMipMode`): isotropic; anisotropic
  (six views per level side by side along x in one atlas, `VoxelAnisoBase` / `VoxelAnisoMip`, `AnisotropicMipLevels`);
  SH L1 (isotropic volume + three half-resolution gradient textures of (radiance, coverage) x (1 - n . d),
  `VoxelSHBase.shader`, plain box mips). Sampled through `Engine/VoxelRadiance.glsl`, directional data on texture units
  18-20 in material shaders (`Lighting.VoxelDirectionalTextureUnit`), 12-14 in the volume's own compute passes.
- Passes that know the mips' kind compile it in (`LEL_VOXEL_ISO` / `LEL_VOXEL_ANISO` / `LEL_VOXEL_SH` variants of
  GIResolve, inject, bounce cells): the runtime branch the material shaders keep made the resolve ~50% slower.
- The coarse anisotropic levels change in steps while objects move (which half of a metres-wide cell an object is in
  decides what it hides): a faint pulse of the whole GI every few voxels of motion; `anisolevels=3` is smooth but loses
  most of the light the directional views keep. SH moves smoothly (linear) but keeps less light and shows more wall
  streaks. 2026-10-20, night ghost view, floor/back/green 8-bit means, far-field per-frame change max under a moving
  box, GPU ms: iso 80/81/69, 0.038, 5.7; aniso 98/99/86, 0.103, 8.2; SH 91/93/82, 0.048, 8.7; Lumen 123/152/106.
- Smoothness without history: the resolve turns each pixel's six-cone set around the normal and moves its samples
  by a world-anchored jitter (`ConeJitter`; 3D ordered dither per cell of ~3x3 resolve texels, 1 cm to half a voxel,
  bounce cells per cell) and filters the diffuse with edge-aware a-trous passes (`ResolveFilterPasses`,
  `GIFilter.shader`, guide texture = normal + depth from the resolve). The SDF opacity floor counts only the part of
  a cone its band covers ((r / R)^2), and half steps use opacity exponent 0.6: the full-disc floor drew sharp bands on
  the walls. Tilting the cone sets was tried and dropped (side cones dip below the surface, -5% in the furnace).
- Test hooks (TestGame): `furnace=1` (closed box, every wall albedo `furnacealbedo` and emission `furnaceemission`,
  `furnacewall` = half thickness; the expected radiance per bounce count is printed, the walls are logged as
  `[Regions]` of the HDR scene color; Lumen reaches ~98%), `bounces=N`, `bouncecells=0|1`, `bouncecones=6|1`,
  `voxcoverage=0|1`, `voxmips=iso|aniso|sh`, `anisolevels=N`, `shadowtaps=1|4`, `conejitter=0|1`, `jittercell=m`,
  `gifilter=N`, `voxels=1 voxelmip=N` (volume view). Scratchpad scripts of 2026-10-20 measured motion with
  `mover=300` (object) and `autocam=1` (camera, mean |second time difference|). There is no fixed capture time step
  (capturefps was removed: it made motion uneven in real time); motion per frame follows the real frame time.

## Audio (FMOD-like events, banks, Audio Studio)

- Runtime in `LELEngine/Audio`: `AudioModel.cs` (the data model: `AudioEvent`, `AudioTrack`, instruments Single /
  Multi / Scatterer / Event / Snapshot, markers Destination / Transition / TransitionRegion / Loop / Tempo /
  SustainPoint, `TransitionTimeline` (on a transition: `Length`, `SourceLength`, `DestinationStart`, `AudioFade`s),
  `AudioSnapshot` (mixer snapshot: `SnapshotBusValue`s with a volume and effect values, `Intensity` envelope),
  `AudioParameter`, `AudioBus`, `AudioBank`, effects LowPass / HighPass / Reverb / Delay, automation curves, AHDSR
  envelopes, `BeatGrid`), `AudioProject.cs` (the ".lelaudio" asset: JSON document, one per game, cached by path;
  `AudioImporter` meta settings of audio files; `AudioSettings` in `ProjectSettings.Audio`), `AudioClip.cs` (wav / ogg
  / mp3 assets, decoded on first use), `AudioDecoders.cs` (WAV built in, NVorbis, NLayer), `AudioDsp.cs` (biquad,
  Freeverb, delay), `AudioOutput.cs` (WASAPI shared mode on its own thread through COM interop, event driven,
  AUTOCONVERTPCM to 48 kHz float stereo; device loss / default device change reopens; no device = silent real-time
  pacing), `AudioMixer.cs` (`Mixer`: commands in, notifications out, voices, buses, limiter), `EventPlayback.cs` (the
  audio-thread instance: timeline cursor in output samples, boundaries handled at the exact sample, parameter
  sheets, quantized transitions, loops, scatterers, nested events, live replace), `AudioCompiled.cs` (immutable
  `CompiledEvent` per event, `EventCompiler`), `AudioBankFile.cs` (bank file + `AudioBankCompiler`: in-memory banks
  from the project for the editor, bank files for builds), `AudioSystem.cs` (the facade: `Initialize`, `Update`,
  `LoadBank(Async)`, `CreateInstance`, `PlayOneShot`, global parameters, buses, listener, `RenderOffline` for tests),
  `EventInstance.cs` (the game's handle, callbacks on the main thread), `SnapshotInstance.cs` (a started snapshot:
  `Stop`, `SetIntensity`), `AudioComponents.cs` (`AudioListener`, `AudioEventEmitter`, `AudioBankLoader`,
  `AudioParameterTrigger`, `AudioSnapshotTrigger`; `AudioEventReference` fields get an event picker).
- Threads: the game thread only posts `MixerCommand`s (parameters by id strings, so indices never go stale) and
  reads volatile fields; the audio thread owns every `EventPlayback`; "Audio streaming" fills `StreamingReader` ring
  buffers (prefetched when an instance is created). The mixer renders sub-blocks of 256 frames; inside a sub-block the
  instance splits at every boundary (instrument start / end, marker, loop end, quantization point), so loops and
  bar-quantized transitions are sample accurate. Voices beyond `MaxVoices` go virtual (quietest first). Every stop or
  retrigger has at least a 4 ms fade (no clicks); `Stop(Immediate)` fades over 2 ms.
- Semantics (as FMOD): timelocked (sync) instruments start at the offset the cursor enters at and stop when it
  leaves; async ones play whole from their start; parameter sheet instruments trigger when the parameter enters
  their range; a transition region jumps at the next grid point while its conditions hold; an event ends when the
  cursor passed its content and nothing plays (unless Persistent); `MaxInstances` + stealing; built-in parameters
  Distance / Direction / Elevation / Speed; global parameters live in the mixer; automation replaces the automated
  value (volume dB, pitch st, pan, effect parameters).
- Snapshots: the project's `Snapshots` list is the priority (first wins); `Mixer.AdvanceSnapshots` applies the active
  ones per block on top of `BusState.BaseVolumeDb` / `BaseEffects` (`EffectiveVolumeDb`): Overriding moves the scoped
  bus volume / effect value towards the snapshot's by its intensity, Blending adds dB x intensity; the `Intensity`
  envelope's attack / release fade it in and out. Started by `AudioSystem.StartSnapshot(name, intensity)` (a
  `SnapshotInstance`), by `AudioSnapshotTrigger`, or owned by a `SnapshotInstrument` region (ends with it);
  `StopAllSnapshots`, `ActiveSnapshots`. Doppler: `AudioSpatializer.DopplerScale` x `AudioSettings.DopplerScale` with
  `SpeedOfSound`; `EventPlayback.UpdateSpatial` takes (c + listener velocity . dir) / (c + source velocity . dir)
  from the velocities `SetListener` / `Set3DAttributes` carry, clamped 0.25..4 and smoothed per block into the voice
  step. Transition timelines: a `TransitionMarker` / `TransitionRegion` with a `Timeline` compiles to a
  `CompiledTransition`; on the jump `EventPlayback.BeginTransition` moves the timelocked voices playing at the marker
  to `SourceVoices` (they keep playing, fading out, until `SourceLength`), `ActivateDestination` triggers the
  destination's instruments at `DestinationStart` with the fade in, instruments with `Sheet == "transition:<id>"`
  play on the transition's own timeline (`TransitionTimeline.SheetId`), `EndTransition` continues on the
  destination at destination + (Length - DestinationStart). A seek or a live replace aborts a running transition
  (playback continues on the destination). `ActiveTransitionIndex` / `TransitionSeconds` are published for the editor.
- Banks: `Data/Audio/Banks/<Bank>.bank` written by `GameBuilder` (step 4c) through `AudioBankBuilder` (editor):
  header + binary document (`BankDocument`: events, globals, buses, snapshots, sample table, the bank list) + 16-byte aligned
  sample data, in-memory sounds first; the player reads up to the in-memory end in one read (PCM16 slices without
  copies), streamed sounds open the file on demand. Load type per audio file: Decompress On Load (PCM16), Compressed
  In Memory (encoded file decoded at load), Streaming. The player loads the master bank, then the banks marked
  AutoLoad (`GameLoader` step "audio"); the editor loads every bank of the project in memory from the clips.
- Editor in `LELEngine.Editor/Audio`: `AudioStudioWindow` (own OS window like the UI Builder), `AudioStudio` partials
  (core: undo snapshots, live update through `AudioSystem.ReloadFromProject` after each edit; Browser: Events /
  Banks / Parameters / Snapshots / Assets panels; Timeline: Event Editor with ruler, logic lane, track lanes, drag /
  trim / drop / context menus; Deck: property editors incl. playlists, conditions, envelopes, effects, automation
  curve editor; Mixer: strips with faders and meters), `AudioInspectors` (event picker for `AudioEventReference`
  fields, emitter Play / Stop, snapshot trigger picker, audio file importer, audio project asset, Project Settings >
  Audio), `AudioBankBuilder`. In edit mode the scene view camera is the listener; play mode uses `AudioListener` /
  `Camera.main`.
- The studio looks like FMOD Studio (2026-10-21): `StudioUi` (palette, `ApplyStyle` on the studio's own ImGui
  context, widgets: `Knob` (drag up / down, Shift fine, double click resets; scales Linear / Logarithmic / Volume /
  Quadratic; a drag accumulates in normalized units, so stepped values move; `ImGuiP.MarkItemEdited` so `Track`
  sees one undo step), `TransportButton`, `IconToggle`, `TimeDisplay` (the LCD; ImGui 1.92 font sizes are line
  heights, the digits use `PushFont(GetFont(), 36)`), `TextToggle` (SOLO / MUTE), `Meter`, `Chevrons`, `FlagLabel`,
  `Fade`, `Handle`). `AudioStudio.Transport.cs`: stop / play / pause, LCD (TIME or BEATS bar.beat.sixteenth, state),
  loop playback and follow toggles, the GAME selector and a knob per parameter; snap / zoom sit on the sheet tabs'
  row. Live preview: `UpdateLive` collects `AudioSystem.Instances` of the selected event (not the studio's preview);
  `Shown` = the preview, else the followed game instance (newest by default): its playhead (cyan), parameter values
  (read-only knobs), LCD state "LIVE", track meters (`EventPlayback.TrackPeaks`); other game instances draw thin
  cyan playheads; the Events panel marks events the game plays. Deck and Overview use knob rows (`DeckKnob`,
  `KnobRow`): instrument volume / pitch / randomization, AHDSR, track volume / pan / pitch, spread, Doppler, effect
  parameters (reset = a new effect's value).
- Timeline (`AudioStudio.Timeline.cs`): ruler (time or bars, `+n` inside a transition timeline), logic track in three
  rows (transition regions with chevrons, quantization and timeline badges / transition flags; destination, tempo
  and sustain flags; loop regions), marker lines through the lanes, track headers (name, SOLO, MUTE, volume knob,
  meter), lanes built from `Piece`s (`LayoutPieces`: overlapping pieces of a lane share it in rows per overlap
  cluster, `StackPieces`), the overview strip (`DrawMinimap`, click / drag scrolls). Transition timelines open inline
  (`expandedTransitionId`; the badge, a double click on the marker, the Deck or the menu; Esc closes): `TransitionView`
  maps display time in three `Segment`s: event time before the transition point P (a region's end), transition time
  [P, P + Length], destination time after (display = P + DestinationStart + (t - D)). Pieces: `Normal` (cut at P),
  `Source` (timelocked instruments playing at P, extended to SourceLength, fade-out drawn, end edge = SourceLength,
  fade handle), `Destination` (from D on, the fade-in drawn, the start edge of pieces at D = DestinationStart, fade
  handle), `TransitionClip` (sheet `transition:<id>`, moved / trimmed in transition time). The logic rows inside show
  the timeline header (end handle), the source and destination regions with their handles. Instruments dropped or
  added inside go on the transition timeline (`PlacementAt`). Snapshots panel (list order = priority), snapshot Deck,
  the Mixer in snapshot scope while a snapshot is selected. `AudioStudio.LayoutVersion`
  (`audio-studio-layout.ini.version`): bump it when the default layout gains a panel.
- Track Solo (`AudioTrack.Solo`, compiled `CompiledTrack.Silent` = muted or another track soloed) is for auditioning:
  `AudioBankCompiler.WriteFile` clears it in the bank document, builds play every track. `EventInstance.GetTrackPeak`
  reads the last block's peak of a track.
- Test hooks: `audiostudio=N;asset`, `audiostudioscreenshot=f1,f2;folder`, `audiocmd=N:cmd|cmd;M:cmd` (commands in
  `AudioStudio.RunTestCommand`: newproject=, open=, newevent=, addsingle=Clip,Start,Length,
  addsingleto=Track,Clip,Start,Length[,loop], addtransitionclip=Track,Clip,Start,Length, addloop=, addtempo=,
  adddest=Name,Pos, addtransition=Pos,DestName, addregion=Start,Length,DestName[,Quantization],
  addtransitiontimeline[=MarkerName] (opens it inline), closetransition, transition=Length,Source,Dest,FadeOut,FadeIn,
  selectmarker=Name, selecttrack=Name, addeffect=lowpass|highpass|reverb|delay, solo=Track, beats=0|1, snap=0|1,
  loop=0|1, addparam=, sheet=, param=Name:Value, newsnapshot=, selectsnapshot=, snapshotbus=Bus,dB, snapshotstart,
  snapshotstop, addsnapshotinstrument=Name,Start,Length, play, stop, undo, redo, save, buildbanks=, zoom=, scroll=,
  report (also the open transition timeline, pieces, live instances), mouse= / click= / drag=x1,y1,x2,y2 (injected
  mouse, window pixels)). ImGui's Text* and SetTooltip treat `%` as a printf format: escape it as `%%` (the knob
  tooltip escapes its own text; draw-list text is literal).
- Decoders (`AudioDecoders.cs`): MP3 does not go through `NLayer.MpegFile` (1.16 drops frames after a seek without
  advancing its position and mis-sizes MPEG-2 Layer III frames): the decoder indexes the frames itself (ID3v2, Xing /
  VBRI / LAME tag, gapless trim) and feeds `MpegFrameDecoder`; Vorbis seeks are not NVorbis's `SamplePosition`
  (64 / 448 frames late in 0.10.5) but a fresh reader on a page window with the granule derived from the pages.
  Both give sample-exact `Seek(x)` + `Read` (verified against linear decodes); keep that when touching them.
- Verification (2026-10-21): an offline harness (`AudioSystem.Initialize(offline: true)` + `RenderOffline`, scratchpad
  `AudioTest`) checked 114 properties by measuring the rendered signal (RMS, zero-crossing frequency, burst counts):
  loop gaps at exact seconds, a bar-quantized transition at 1.500 s, parameter sheets, scatterer spawns, streaming
  with seeks, fades, polyphony, callbacks (8 beats in the test song), nested events, automation + low pass, 3D
  rolloff and pan, global parameter conditions, sustain points, bank file round trip (2 ms load, snapshots included),
  Doppler (440 Hz -> 489 / 400 / 484 Hz), snapshots (overriding, blending, intensity, priority, effect value,
  instrument), transition timelines (source extension, crossfade midpoint, fill on the transition sheet, continuation
  on the destination, transition to End), solo and track peaks (and the bank file dropping the solo). A probe project
  (scratchpad `AudioProbe`, its `[ExecuteAlways]` script plays Music/Loop: a live instance in edit and play mode) ran
  the scripted studio (screenshots; injected drags of a destination piece's start and the source fade handle, undo),
  a build and the player (snapshot from the bank).
  Offline rendering outruns the streaming thread (render in paced chunks when testing streams).

## GPU particles (VFX)

- Runtime in `LELEngine/VFX` (graph asset, compiler, codegen, runtime, instance), shaders in
  `LELEngine/ShaderLibrary/Engine/VFX`, editor in `LELEngine.Editor/VFX`, scenes in `TestGame.Scenes/VfxTestScene.cs`
  (showcase) and `TestGame.Scenes/VfxStressScene.cs` (load test).
- Frame flow: `VisualEffect.LateUpdate` only queues (`VfxInstance.Simulate`); the renderer's first pass
  (`VfxSimulationPass` -> `VfxRuntime.RunSimulation`) runs the CPU half of every queued effect, packs every system's
  parameter block into one uniform buffer, then dispatches all systems in phases (frame kernel `VfxFrame.shader`,
  all initialize kernels, all update kernels, per GPU-event layer) with one barrier per phase.
- The per-system counters live in one shared pool (`VfxRuntime.CounterBuffer`, a 64-uint block per system). Their
  layout is defined three times and must stay in sync: `VfxCodegen.Counter*` (C#), `VFX_C_*` in `VfxCommon.glsl`,
  `C_*` in `VfxFrame.shader`.
- Generated GLSL must avoid reserved words: `active`, `half`, `output`, `input`, `filter`, `sample`, `common`,
  `partition` all fail to compile on NVIDIA.
- Debug switches (environment): `LEL_VFX_DUMP=1` writes every generated kernel and draw program plus the compile
  messages to `%TEMP%\LELEngine\VFX`; `LEL_VFX_DEBUG=1` logs every system's counters (spawn, alive, dead, draw
  arguments, alive bound) once per 60 frames; `LEL_VFX_CHECKSORT=1` reads every sort back every 120 frames and
  logs OK or FAILED (stalls, debugging only).
- Showcase: `TestGame.exe vfx=1`. `LEL_VFX_ONLY=puffs|fireworks|cubes|ribbons` runs one effect, `LEL_VFX_SOFT=0`
  and `LEL_VFX_SORT=0` switch soft particles and GPU sorting off.
- Load test: `TestGame.exe vfx=1 vfxstress=1 gi=0 exitseconds=12` prints `[VfxStress]` lines every two seconds
  (CPU per stage, GPU per pass, dispatches, barriers, bytes allocated). Parts: `vfxbig=N` (one N-particle system,
  default 1M), `vfxsorted=N`, `vfxsmall=N`, `vfxidle=N`, `vfxbehind=N`; 0 leaves a part out.
  `LEL_VFX_BIGSORTED=1` makes the big system alpha blended (sorted), `=2` alpha blended unsorted. In GPU-bound runs
  the driver's wait for the GPU shows up inside the simulation pass's "readback" time; that is not CPU work.
- Editor automation (`LELEngine.Editor.exe project=<path> ... screenshot=<folder> screenshotframe=N exitframe=M
  maximize=1`): `createvfx=N;<Assets/... .vfx>` writes the default graph, opens it and adds an effect to the scene;
  `vfxgraph=N;<path>` opens the window on an existing asset; `dumpview=N;<folder>` writes the scene view's own image.
  See `LELEngine.Editor/TestHooks.cs` for the rest.
- The editor's saved layout (`%APPDATA%\LELEngine\Editor\layout.ini`) leaves the scene view a few pixels tall on
  this machine; for scene-view screenshots back the file up, delete it for the run, and copy it back afterwards.
- `uint` uniforms must be set with `ShaderProgram.SetUint`: `SetInt` on a uint uniform is a GL error, the value
  stays 0, and an indirect dispatch sized from it hangs the GPU for seconds per frame.
- Added 2026-11-06 (ports of Shuriken effects need them): builtin `previousLocalToWorld` (`Operator.PreviousLocalToWorld`;
  uniform matrix slot 2 `vfxPreviousLocalToWorld`, `VfxEvalContext.PreviousLocalToWorld` = the transform of the last
  simulated frame, the current one on the first) and `Block.SpreadAlongPath` (Initialize: this frame's spawns spread
  evenly from the previous to the current emitter position, so a trail emitted per unit of distance does not bunch per
  frame); `Block.RandomForce` (Min / Max, PerFrame or PerParticle, World or Local); `Block.LimitSpeed` (Max Speed,
  Dampen as `1 - pow(1 - dampen, dt * 60)`); `Block.FlipbookOverLife` rewritten (Frame Count, Cycles, Start Frame;
  WholeSheet, or RandomRow with Rows: a sheet whose rows are variants). Quad and strip outputs: `UvRotation` (0 / 90 /
  180 / 270, turns the texture's u axis along the quad's up or the strip: wide spark textures on stretched quads) and,
  in Simple UV mode, `UV Scale` / `UV Offset` inputs (per particle, e.g. a custom attribute; Time x speed into UV
  Offset scrolls a beam); `VfxCodegen.EmitUv`.
- A block's locals carry its salt (`triggerAmount_<salt>`, `randomForce_<salt>`): two blocks of one kind in a context
  compiled to a GLSL redefinition before; a CustomCode block opens its scope before its input declarations for the
  same reason (two CustomCode blocks declaring `Length` in one Update).
- A texture whose shape lives in alpha or packed channels with black RGB draws nothing through an additive output
  (`FragColor = rgb * a`); such textures come from shaders that compute the colour from the channels (KriptoFX's
  `Tint * G + Core * R`): bake that into the texture before using it in a graph.

## Volumetric objects (clouds, fog, storms; 2026-11-07)

- Two components, both `VolumetricObjectBase` (`LELEngine/Volumetrics`, `[ExecuteAlways]`, every setting): `VolumetricObject`
  for volumes seen from outside (marched at `Resolution` Half, even steps, `MaxSteps` 64) and `VolumetricObjectHQ`, which
  also looks right from inside (Full resolution, `MaxSteps` 160, steps growing with the distance: `NearStepScale` 0.02,
  `MinStep` 0.25 voxel). Both march with a jitter (interleaved gradient noise, a golden-ratio offset per frame) under a
  temporal filter (`TemporalFilter`, `HistoryWeight` 0.9: reprojected at the march's light-weighted mean distance
  (`FragDistance`) through the previous view-projection and the object's previous transform, the history clamped to the
  3 x 3 range; the editor keeps refreshing until a history held 32 frames, `SettleFrames`). Without it a reduced
  resolution shows the dither raw: the user found it unacceptable at Quarter (2026-11-07), so it is on for both presets.
- The user's direction (2026-11-07): general, physically consistent features that can each be tuned and switched off
  come before matching New Frontier's Unity look ("too much likeness to NF, too little functionality").
- Settings: `Shape` Box / Cylinder (axis local y) / Sphere (ellipsoid) with `Size` in local units, the boundary's fade and
  wandering (`SideSoftness`, `CapSoftness`, `Top/Bottom/SideRoughness`); `Noise` Perlin / Billow / Ridged / Worley /
  PerlinWorley, `NoiseSize` (local units of the big shapes: a bigger shape holds more of the same cloud), `Octaves`
  (capped down to about three voxels), `Roughness`, `Warp`, `Seed`, `Coverage`, `Softness`, clumps (`ClumpSize` x noise
  size, `ClumpCoverage`, `ClumpSoftness`); `Density` (optical depth through one noise size of full cloud), `ShadowReach`
  (x noise size); detail (`DetailStrength`, `DetailSize`, `EdgeBand`, `DetailShade`, `DetailWind`: a tiling 96^3 texture
  eats the edges); lighting (`Sun`, `SunStrength`, `ReceiveShadows` + `ShadowStrength`, `SkyStrength`,
  `AmbientStrength`, `OwnAmbient` + `AmbientColor`, `Albedo`, `PhaseG`, `MultipleScattering`: second and third orders at
  half and quarter optical depth, emission, `Exposure`, `Opacity`); GI (`ReceiveGI`, `GIStrength`, `GICellSize`,
  `GIUpdateFrames`); lights (`SceneLights`, up to 8 `Lights` inside: `VolumeLight` position, core radius, colour,
  intensity; `LightGlow`); quality (`VoxelSize` kept when the shape grows, up to 256^3 voxels and 512 per axis, then the
  voxel grows; `StepSize` in voxels; `TemporalFilter`, `HistoryWeight`; `RenderQueue`).
- A light of its own: `Sun` may be a disabled `DirectionalLight`, which lights only the cloud (the scene's main light is
  its first enabled one): a backdrop's own sun, but then no scene shadows (the shadow map is the main light's).
  `OwnAmbient` replaces the scene's flat ambient light by `AmbientColor`.
- Scene shadows (`ReceiveShadows`, `ShadowStrength` 1 = the sun fully blocked): per step one comparison in the main
  light's cascade the point falls in (`VolSceneShadow`, faded out towards the shadow distance, lit outside the cascades),
  on the sun term only; only for a cloud whose sun is the main light (`Sun` null or `DirectionalLight.This`). The scene's
  shadow `Distance` must reach the cloud: New Frontier's Flight went from 250 to 600 (its storm lies 200-450 units from
  the camera; before, the shadows existed and were never seen there).
- GI (`ReceiveGI`): a coarse grid in the object's space (`GICellSize` voxels a cell, at most 64 a side, a full pass over
  `GIUpdateFrames` frames, `VolumetricGI.shader`): per cell the GI's light from the six local axes (VCT: one cone of
  aperture 1; Lumen: the radiance cache's SH, `SampleRadianceCacheSH`; outside the GI volume or without GI: the sky),
  each dimmed by the cloud towards that side (openness 0.08 + 0.92 e^-tau/2 within `ShadowReach`), stored as a
  first-order SH in two RGBA16F textures with linear filtering (A = the mean radiance, B = the luminance's directional
  term 5/12 sum L e). The march scatters it towards the eye with the phase, a + g (b . ray) (b's colour from a), x
  `GIStrength`, in place of the sky term (`SkyStrength` applies while the GI is off or unavailable:
  `VolumetricRuntime.SceneGIAvailable`). The grid update is ~0.1 ms (`Volumetrics` pass).
- Scene lights (`SceneLights`): `Light` components whose range reaches the shape take the transport's slots left after
  the cloud's own `Lights`, nearest first, whatever their intensity (a flash going dark keeps its slot: no rework when it
  flickers); `Light.VolumetricStrength` scales what the cloud takes (see "Local lights").
- Made on the GPU from the settings, no bake (`VolumetricData`, compute shaders in `ShaderLibrary/Engine/Volumetrics`):
  density R8 with mips (`VolumetricGenerate.shader`, noise in `VolumetricNoise.glsl`), occupancy (densest voxel per 4^3
  block plus a voxel's border: the march jumps empty blocks), lit RGBA8 with mips (density, sqrt(sun optical depth / 16),
  ambient openness: one fetch a step; `VolumetricLight.shader`), the lights' transport at half resolution (2 x RGBA16F,
  linear, `VolumetricLightTransport.shader`: an own light's core 1 / (1 + (d / r)^2) x e^-tau + glow (four times as wide)
  x e^-0.15 tau; a scene light's physical falloff in world units, its cone and (e^-tau + glow x e^-0.15 tau), held times
  (range / 4)^2 so half floats keep it; their intensity and colour stay free every frame: `VolumeLightSlot.Color`), the
  GI grid, the shared detail. Work goes in
  z slices within `VolumetricRuntime.WorkPerFrame` (8 M voxels a frame, at most 1 M a dispatch) into new textures that
  swap in when complete: a changed shape or noise rebuilds after the settings held 0.2 s (edit mode), the sun turning
  against the object relights (at most every 0.1 s), a moved light reworks its transport. Seed offset: `new
  Random(Seed)` x 1000, the same numbers on any .NET.
- Drawing: `VolumetricPass` (Frame scope) advances the builds; `TransparentPass` sorts each volume with the other
  transparents (centre distance, `RenderQueue`; the camera's culling mask applies); `VolumetricRuntime.Draw`: the opaque
  depth at the march's resolution (`VolumetricDepth.shader`, checkerboard of nearest and farthest), the march
  (`VolumetricMarch.shader`, variant `VOL_HQ`; RGBA16F colour + R32F distance), the temporal filter, the composite
  (`VolumetricComposite.shader`: nothing where the shape's entry is behind the scene, 2 x 2 depth-aware weights at
  reduced resolutions, premultiplied). The march samples on units 0..5 and the GI grid on 6 / 7 (`GIAUnit`, `GIBUnit`)
  and keeps the lighting's units free (shadow map 15, transmittance 10, GI sky 16); the GI grid's compute pins
  `RadianceCacheSH` 12, `RadianceCacheAtlas` 13, `GlobalSdfObjectIds` 8, `giSkybox` 16: every sampler type needs a unit
  of its own (two types on one unit fail the dispatch).
- API: `Ready`, `DensityAt(world)` / `DensityAtLocal(local)` (CPU, trilinear from the R8 voxels, read back once per
  build), `EvaluateDensity(localPoints, densities)` (the noise exactly at those points through the generate kernel's
  `VOL_POINTS` variant, a GPU round trip: picking places by density without the voxels' 8-bit ties), `Transmittance(from,
  to, samples)` (CPU, through the voxels), `Rebuild`, `Describe` (grid, memory, state), `GridFor`.
- Editor: `VolumetricObjectInspector` below the fields (Describe, what the preset does, Rebuild) and the selected object's
  shape as a wire outline in the scene view while the component is expanded; `EditorApplication` keeps refreshing
  while builds run (`VolumetricRuntime.NeedsRefresh`).
- Gotchas: `half`, `near`, `far`, `exit` are reserved in GLSL; the occupancy jump keeps the pixel's step phase
  (`range.x + (ceil((exit - range.x) / step - jitter) + jitter) x step`): restarting at the block's face drew contour
  lines; a cloud under a black scene ambient and no sky shows only its sunlit side; the ACES display transform crushes
  dark volumes (about 0.21 x in the toe) and saturates them, so content from gamma-space shaders needs its albedo and
  lights matched against reference frames, not converted.
- Debug: `LEL_VOL_DEBUG=1` logs every completed build (grid, filled share) and every 120th frame the draws' rectangles
  and their march's centre texel (a readback: it stalls).
- TestGame `volumetrics=1` (`VolumetricTestScene`: a storm cylinder with two flickering lights, an HQ fog bank box, a
  glowing sphere with a light inside, a ridged wisp at a quarter resolution); `volcube=1` a cube in the fog, `volhq=1` the
  storm as the HQ preset, `volgi=0` the volumes without GI.
- Measured on this machine's AMD Radeon 780M (OpenGL, 1280 x 720): the four test volumes 0.33-0.43 ms, inside the fog bank
  0.35 ms, HQ at the dense storm's centre 2.5 ms (5.1 ms with `MinStep` 0.1, the same image); a backdrop cylinder of
  3740 x 892 x 3740 at 10-unit voxels (12.6 M voxels, about 69 MB) filling a 1253 x 709 view 0.54 ms, 0.61 ms with its
  8 lights lit. TestGame lumen, vct, gi=0, refraction, materials against the commit before: identical (max 1 / 255;
  trails differ from run to run).
- Since 2026-11-07 (temporal filter on both presets, scene shadows, GI, scene lights, RGBA16F transport) that backdrop
  (New Frontier's storm, Lumen) costs 0.90 ms calm and 1.04 ms with its 8 flash lights lit: the temporal filter 0.08 ms,
  GI + shadows 0.18 ms, the rest the wider transport and the distance target; the GI grid 0.11-0.15 ms in the
  `Volumetrics` pass. TestGame lumen, vct, gi=0, refraction, materials against the commit before: identical (max 2 / 255).

## Local lights (Light component; 2026-11-07)

- `Light` (`Mono/Behaviours/Light.cs`, global namespace, `[ExecuteAlways]`): `Type` Point / Spot (`SpotAngle`,
  `InnerSpot`), `Color`, `Intensity` (radiance units as a directional light's Strength: a white surface facing the light one
  unit away reflects Intensity), `Range`, `SourceRadius`; GI: `AffectsGI`, `IndirectStrength`, `GIShadows`; volumes:
  `AffectsVolumes`, `VolumetricStrength` (HDRP's Volumetric Multiplier: a flash can light the ships around brightly and
  its cloud less), `VolumetricShadows` (clouds between the light and what it lights dim it). Falloff (`Falloff(d)`, the
  shaders' formula): fade^2 / max(d^2, SourceRadius^2), fade = saturate(1 - d^4 / range^4) (Frostbite's window); a spot's
  cone saturate((dot(-L, forward) - cos outer) x 1 / (cos inner - cos outer))^2. The direct light has no shadow maps
  (geometry does not block it on surfaces); the GI's injection does block it.
- `Rendering/LightBuffer` (static): every frame (`Renderer.RenderFrame`, after `Lighting.BeginFrame`) the lights that shine
  (enabled, intensity, range, colour), at most `Light.MaxLights` = 32, those whose range comes nearest to the camera first;
  a std140 uniform block at binding 7, 6 vec4 per light (position + range, radiance + GI strength, spot direction + cos
  outer, spot scale / flags / source radius, the clouds' transmittance towards +x -x +y and -y +z -z);
  `Lighting.SetUniforms` calls `EnsureBound` (programs drawn before the first frame never read an unbound block).
  `Engine/Lights.glsl`: `LelLight`, `LelLightCount`, `LelLightHas`, `LelLightFalloff`, `LelLightCloudTransmittance` (the
  six axes blended by dir^2), `LelLightShape`, `LelLightAt`.
- Lit materials: `Lit`, `LitTextured` (Scene workflow: Blinn-Phong as the sun; Metallic / Specular:
  `LelStandardBRDFDirect` in `StandardBRDF.glsl`) and `LitTransparent` add every light after the sun (unshadowed).
- GI: VCT's `VoxelInject.shader` adds each GI light to the voxels (`VoxelLightVisibility`: a march through the voxels'
  occupancy towards it, voxel steps growing to an eighth of the distance, at most 48; none with `GIShadows` off); Lumen's
  `CardLighting.shader` to the cards (`CardLightVisibility`: `TraceSdf` through the global distance field;
  `SurfaceCachePass` sets the SDF uniforms of the lighting program). `radiance.w` = `IndirectStrength` (0: not in the GI).
- Lumen's idle budgets: `LightBuffer.GIVersion` goes up when a GI light's list entry changes (on / off, moved, brightness,
  colour, cone, transmittance: a hash per light), `GIChanges` = the boxes of their reach before and after (kept until the
  next change, so every renderer of a frame reads the same); `LumenScene.LightsChanged` restarts the quiet count and adds
  them as change regions. Before, a light changing alone left the radiosity and the radiance cache at their idle budgets
  (a flash would have reached the GI late). Verified on New Frontier's storm running 600 frames: 497 flash changes, 497
  frames with `LightsChanged`, none missed; steady lights raise nothing.
- Volumes: `VolumetricRuntime.UpdateLightTransmittance(shining)` gives each light with `VolumetricShadows` the
  transmittance of the volumes within its range towards the six axes (CPU, 24 samples through the voxels to its range:
  e^-tau plus the cloud's glow, LightGlow x (e^-tau)^0.15, at most 1) in `Light.CloudTransmittance`; volumes take scene
  lights into their transport (`SceneLights`, see "Volumetric objects").
- Editor: `LightInspector` (below the fields: whether it shines and how bright across its range; the selected light's reach
  as a wire outline in the scene view while the component is expanded: a point light's range sphere, a spot's cone),
  GameObject > Point Light / Spot Light.
- TestGame `lights=1` (`GITestScene.BuildLocalLights`: a point light, a spot, a lamp in a patch of fog; `night=1` puts the
  sun down). Measured (780M, 1280 x 720, night): whole frame on the GPU gi=0 1.79 ms, VCT ~9.3 ms, Lumen ~6.8 ms. Without
  lights TestGame lumen, vct, gi=0, refraction, materials match the commit before (max 2 / 255).

## Animation system (clips, Animator, state machines; 2026-10-28)

- Runtime in `LELEngine/Animation`: `FloatCurve` (a curve as flat arrays: times, values, tangents, modes; binary
  documents bulk-copy them; `ToAnimationCurve` / `FromAnimationCurve` for the curve editor), `AnimationClip` (".anim":
  `bindings` of path + component type + property path + `AnimationValueKind` with one `FloatCurve` per channel, string
  and reference keys for discrete kinds; `events`; `loop`, `frameRate`, `cycleOffset`, `lengthOverride`, `rootMotion`
  channels; `Load` cache per path, `Save` + meta, `ToBytes` / `ApplyBytes`, `Version` + `Changed` bump on every edit),
  `AnimationEvent` (+ `IAnimationEventPayload`: any [Serializable] class as the payload; `AnimationEventDispatcher`
  calls the method by name on the GameObject's components, picking the most specific overload: event, payload,
  reference, float, int, string, bool, none; `IAnimationEventReceiver`; a warning once per missing receiver),
  `AnimationBinder` (resolves a binding to a `BoundProperty` with typed setters: Transform channels direct, depth-one
  fields through the serializer's `FieldAccessor<T>`, C# properties through compiled expressions, nested paths
  "a.b", "items[2]" and struct chains through `MemberPath` get-modify-set; specials `active`, `enabled`,
  `localEulerAngles`; `GetAnimatableProperties` lists what the clip editor offers), `AnimationPose` (`PoseLayout` =
  union of the bindings of a set of clips as `PoseSlot`s bound to a hierarchy with the objects' values as defaults and
  last-written values; `Pose` accumulates weighted samples (nlerp for rotations, heaviest-wins for discrete kinds),
  `Lerp` / `Override` / `Additive` between poses, `Apply` writes touched slots; `ClipSampler` per animator and clip with
  per-channel segment hints, additive sampling against frame 0, root motion deltas, event collection across loop
  boundaries), `AnimatorParameters` (`AnimatorValue<T>` subclasses are the parameter types: Float, Int, Bool,
  Trigger, String, Vector2/3/4, Quaternion, Color, Object (EngineObject reference), Asset, plus any game class
  deriving from `AnimatorValue<T>`; `ParameterState<T>` is the live slot, no boxing in `Set<T>`), `AnimatorConditions`
  (`ParameterCondition` with modes compiled per animator into delegates through `AnimatorValue.CompileCondition`
  (Comparer / EqualityComparer of T; compare with a constant or another parameter), `ConditionGroup` any / all,
  `ScriptCondition` subclasses evaluated on the main thread), `AnimatorController` (".animator": parameters, layers
  (weight, Override / Additive, `AnimationMask` by transform paths), `AnimatorStateMachine` (states, sub-machines,
  any-state / entry transitions, the machine's own exit transitions, default state, notes, groups, node positions and
  view), `AnimatorState` (motion = `ClipMotion` or `BlendTree` (1D, 2D simple / freeform directional / cartesian,
  direct; gradient band weights in `BlendTreeMath`), speed + parameter, motion time parameter, cycle offset, write
  defaults, loop mode, tag, transitions, `StateMachineBehaviour` instances by value, color, note),
  `AnimatorTransition` (destination state / machine / exit, conditions, exit time in cycles or seconds, fixed or
  normalized duration, offset, interruption source + ordered, can transition to self, mute, solo),
  `transitionDefaults`, `Validate` problems), `RuntimeController` (the compiled, immutable form cached per controller
  version: layers, flattened states, transitions with solo / mute applied, any-state lists per state innermost first,
  blend trees with parameter indices), `AnimatorPlayer` + `LayerPlayer` + `StatePlayback` (per animator: parameter
  slots, compiled conditions, pose layout bound to the hierarchy, state machine advance with transitions,
  interruptions (the blended pose freezes as the new source), entry / exit resolution through sub-machines, blend
  sampling, events, root motion, behaviour callbacks queued for the main thread), `Animator` (the component: Unity's
  API `SetFloat` / `GetBool` / `SetTrigger` / `Set<T>` / `Play` / `CrossFade` / `GetCurrentAnimatorStateInfo` /
  `IsInTransition` / layer weights / `Rebind` / `Update(dt)`; `applyRootMotion` (own frame deltas, or
  `IAnimatorMoveHandler` / `AnimatorMove` take over), `keepStateOnDisable`, `allowParallelUpdate`, `updateMode`
  Normal (after Update, before navigation and LateUpdate) / AnimatePhysics (each fixed step before the physics step)),
  `AnimationSystem` (per scene (`Scene.AnimationSystem`), registered by the animators' OnEnable: rebuilds stale
  players on the main thread, advances them with `Parallel.For` from `ParallelThreshold` (8) animators on
  (`LEL_ANIM_PARALLEL=0` off; controllers with script conditions stay serial), applies poses and callbacks in scene
  order; `LastUpdateMilliseconds` etc.).
- Sub-machines as assets (the user's ask, 2026-10-28): `AnimatorStateMachine.controller` + `controllerLayer` make a
  sub-machine node play a layer of another `.animator` asset (its states, transitions, behaviours; the node's own
  content is ignored), `parameterMappings` (`ParameterMapping` child id -> parent id, same value type and trigger-ness)
  feed the asset's parameters from the owner's; unmapped ones are private per animator, named "Node/Parameter"
  (`animator.Set("Combat/Power", ...)`). Compile: `ParameterScope` per asset instance (`RuntimeMachine.Scope` for the
  node's own exit transitions, `ContentScope` inside), private parameters appended to `RuntimeController.Parameters`
  as clones with the scoped name, `RuntimeTransition.Scope` resolves condition ids, `RuntimeController.Assets` +
  `IsCurrent` rebuild the players when any embedded asset changes (`AnimatorController.CombinedVersion`,
  `CollectControllers`, `Embeds` for loop checks); a loop or a missing layer cuts the node with a `Warnings` entry
  (logged once per animator). Editor: the node shows "Asset: X", the inspector has the asset field, the layer, the
  mapping table with "(own value)" / parent parameters of the same type / "+ New ... and map it", and Map by name /
  Create missing / Unmap all; double click looks inside read-only (`AnimatorPage.External` / `Embedding`,
  breadcrumbs cross the asset, every edit callback refuses, "Open X" opens the asset as the document); dropping an
  `.animator` on the canvas or "Sub-State Machine from Asset/..." in the create popup makes such a node.
- Assets: `.anim` and `.animator` are documents (`Documents.Extensions`, JSON in the project, binary in builds, typeless
  conversion); registered in `AssetDatabase.RegisterTypes`; caches rekeyed on moves and cleared with the project
  (`InternalStorage`); `Save` creates the meta file. Clips and controllers may hold script types (behaviours, custom
  parameter values, event payloads): the editor windows snapshot their open document before a script reload and clear
  both caches (`AnimatorWindow.OnDomainUnloading` / `OnDomainLoaded`, hooked in `ClearTypeCaches` and `ScriptDomain`).
- `SerializationUtility.MaxDepth` is 32 (was 10): a controller nests layer / machine / state / transition / group /
  condition / value, deeper with sub-machines. Values past the limit are written as null with a warning
  (`WarnDepth`), which is how the loss showed up (thresholds and nested conditions vanished silently).
- Node graph editor, a system of its own (`LELEngine.Editor/NodeEditor`, namespace `LELEngine.Editor.Graphs`, public
  for editor scripts): `GraphView` (immediate mode: `Begin`, `AddNode` / `AddEdge` / `AddGroup` / `AddNote`, `End`;
  pan / zoom / grid / snapping / alignment guides / minimap / rubber band / multi-drag / inline rename (F2) /
  node handle drags and "Make Transition" for node-to-node edges, port drags for port edges, a searchable create
  popup (`CreateEntries`), context menus (`ContextMenu` + built-in items), shortcuts, nudging, `DropTarget` for
  drag and drop; every change is a callback (`NodesMoved`, `ConnectRequested`, `DeleteRequested`,
  `DuplicateRequested`, `Copy` / `PasteRequested`, `RenameRequested`, `NodeDoubleClicked`), so undo stays the host's),
  `GraphTypes` (`GraphNode` with header / subtitle / lines / ports / badge / progress bar / outline / custom draw,
  `GraphEdge` styles Bezier / FlowBezier / Arrow (lanes for both directions, label, progress dot) / Line, groups,
  notes, menu entries), `GraphLayout` (layered, force-directed, align, distribute). Nodes record `node:<Title>` test
  anchors (screenshots, `TryGetPoint`); the canvas is not an element. The VFX canvas still has its own code.
- Animator window (`LELEngine.Editor/Animation`): `AnimatorWindow` (IEditorPanel "Animator": menu, toolbar with
  breadcrumbs (layer > machines > blend tree) and the LIVE state, sidebar layers + parameters (live values and
  trigger buttons in play mode), `AnimatorGraph` on `GraphView` (states, sub-machine nodes, Entry / Any State / Exit,
  transitions grouped per direction with counts, muted dashed, solo yellow, default-state arrow; blend tree pages
  with live weights; drop .anim assets to make states; `AutoLayout` = breadth first from the default state),
  `AnimatorInspector` (state, the transitions of an edge (timing, interruption, a two-bar preview, conditions of any
  parameter type with the inspector's value widgets, groups, script conditions), sub-machine, Entry, Any State, note,
  group, blend tree + children with a 2D blend space, layer with a mask editor from the selected object's hierarchy,
  parameter, controller defaults), `AnimatorDocument` (whole-controller undo snapshots as `AnimatorRecord`, layout-only
  edits do not rebuild the players; `EditTracker` also records `AnimatorController` / `AnimationClip` targets, so the
  field drawers get undo), `AnimatorComponentInspector` (the Animator component: layers with progress, parameters,
  unresolved properties; the .animator / .anim asset inspectors). The page re-resolves its machine by id after undo
  (`ResolvePage`): `ApplyBytes` replaces the controller's objects. The copy buffer is a document
  (`AnimatorClipboard` bytes), never script-type instances (that leaked the old assembly on reload).
- Animation window (`AnimationWindow`, IEditorPanel "Animation"): rows per binding (expandable channels, the value at
  the playhead editable = a key), dope sheet (diamonds, drag with frame snapping, box select, double click adds) or
  curves (auto-fit range, value drag, tangent handles of the one selected key, Ctrl + wheel zooms values), ruler with
  frames, events lane (drag, double click adds, inspector with the target's methods as suggestions and a polymorphic
  payload), Add Property picker (the target hierarchy's GameObjects, components and their animatable properties),
  Preview (samples on the target in edit mode through `PreviewSession`, restores the defaults when off) and Record
  (every change of a bound property, inspector or scene view, keys at the playhead), playback, Key All (K), reverse,
  tangent modes. The target is the selection's Animator object (or the selection), lockable.
- Hooks: `EditorApplication` (fields, `Draw`, `GetPanels`, `Reset`, `OpenAnimator(path | controller)`,
  `OpenAnimationWindow`, Window menu, shortcuts, window states with the last asset per project), `ProjectPanel`
  (Create Animator Controller / Animation Clip, double click opens the window), `EditorIcons` (`AnimatorController`,
  `AnimationClip`), `AssetInspectors`, `CustomInspectors`.
- Verify (2026-10-28, scratchpad `AnimProbe` of session 8ce1f98f: `-batchmode -executeMethod AnimProbe.Run` 121
  checks: curves, clip JSON / binary / typeless round trip, sampling of every kind (nested struct, list element, C#
  property, GameObject active, enum, string, reference to a ScriptableObject asset), the state machine (exit time,
  conditions, trigger consumed, any state, script condition, condition group with a custom-typed parameter,
  sub-machine entry / exit with behaviour callbacks, Play / CrossFade, live edit rebuild, disable reset), root motion
  along the facing across a loop, parallel = serial bit for bit (30 animators), layers (override with mask, additive),
  sub-machines playing another asset (reload by GUID, binary round trip, mapped trigger and bool, private "Combat/Power",
  exit through the node's transitions, rebuild on the asset's edit, a loop cut with a warning);
  `AnimEditorEdits.Run` 24 checks through the window's model by reflection (create, rename, connect, default state,
  undo / redo, delete, duplicate, copy / paste, parameters, validate, save / reload, play mode LIVE); `AnimEditorProbe.Run`
  layout screenshots of both windows; a game build (`-build`) whose player log shows the binary assets loading and
  the states changing; `TestGame.exe anim=1` (24 animated cubes, `animcount=N`). Headless probes step a scene made
  with `SceneManager.CreateScene` through `scene.Update(dt)` (it is not in the loaded list, so
  `SceneManager.UpdateScenes` skips it). `-executeMethod` hands the method the whole command line as `args`.
- Not done (follow-ups): skinned meshes and bone animation import from models (Assimp has the data), animating
  material properties per renderer (`MaterialPropertyBlock` exists since 2026-10-31; the binder does not write into
  one yet), humanoid retargeting / IK, moving the VFX canvas onto `GraphView`.

## Timeline (Unity's Timeline with Audio Studio's logic; 2026-11-02)

- The user's ask: "just Unity's Timeline, extended with what FMOD / Audio Studio has: transitions, regions, markers,
  parameters"; each animated object a channel, any object of the scene (not only children), identified not by name;
  during playback only the properties with keys are overridden; recording, values exposed by custom scripts too. Their
  correction while it was built: not a whole FMOD-style window, no deck, no snapshots; a plain editor panel.
- Runtime in `LELEngine/Animation`: `TimelineAsset.cs` (".timeline", JSON in the project, binary in builds, same cache /
  stamps / `ApplyBytes` as clips: `TimelineChannel` (name, color, mute, solo, clips), `TimelineClip` (a clip placed:
  `start`, `length`, `startOffset` = trim, `speed`, `loop`, `fadeIn` / `fadeOut`, `conditions`), parameters, markers; the
  markers, parameters, conditions, fades and `BeatGrid` are the audio model's classes (`AudioMarker` etc.: pure data,
  nothing of sound)), `Timeline.cs` (the component: `asset`, `playOnAwake`, `updateMode`, `ignoreTimeScale`, `speed`,
  `endBehaviour` Hold / Restore / Loop, `Play` / `Stop(fade)` / `KeyOff` / `SetParameter` / `SetBinding`, events and
  `ITimelineReceiver`), `TimelinePlayer.cs` (the playback, also the editor's preview). `AnimationSystem` ticks timelines
  after the scene's animators, so they override them.
- Identity: `GameObject.Id` (random 64 bits, in the scene file, stays through renames and reparenting) was there
  already; scene references are written by it. `TimelineBinding` (on the component, not the asset: an asset knows no
  scene): `channel` id + `target` reference (`{"$ref": id}`: copies of a subtree, prefabs and undo work as for every
  reference) + `targetId` / `targetName` as plain values (`OnBeforeSerialize`), so a lost reference (another loaded scene,
  a deletion undone) is found again (`ResolveTarget`, `SceneManager.FindObjectById`, new) and a missing one still has a
  name. A clip's own bindings keep their paths: a timeline-recorded clip has path "" (the channel's object itself); an
  existing .anim with child paths still works by name below the channel's object. An object has one channel (the window
  refuses a second; two channels on one object work, later wins, but their base values mix).
- Playback (`TimelinePlayer`): a port of `EventPlayback`'s cursor to ticks (48000 per second): `Step` stops at every
  boundary (`NextBoundary`, `HandleBoundary`), loops first, then transitions, pending quantized transitions, clips ending,
  clips starting, sustain points. Loop regions (conditions, `MaxLoops` = jumps per pass), transition markers and
  regions (conditions, probability, quantization on the tempo markers' grid, `Crossfade`: old voices release while new
  ones attack), destination markers (`MarkerReached`), sustain points (`KeyOff`; clips keep playing), jump to End. A
  `Voice` per pass of a clip. Not ported on purpose (the user's "no deck etc."): transition timelines (`Crossfade`
  instead), parameter sheets, automation curves, playlists, scatterers, nested timelines, AHDSR, effects, buses, snapshots.
- What is written (`ChannelRuntime`, one `PoseLayout` per channel; `ClipSampler.Sample` got an `under` argument: a channel
  without keys, only x of a position or y of an Euler rotation, samples as the value underneath):
  - base value = what the property holds without the timeline. `RefreshBase` each frame: current != last written means
    someone else wrote it: that is the base now and the property is free again (`written` and the hold cleared).
  - `Write`: playing clips averaged by weight, over the underlying value (hold, else base) as far as they cover it
    (`cover` = sum of weights, at least 1 is theirs alone). A property never played is never written. Writes read back
    (setters that clamp or normalize do not look like outside changes).
  - Hold: a clip ending without a fade out leaves its last value (`CaptureHeld`); `ResolveHeld` recomputes it after every
    jump, so the pose at a position does not depend on how the cursor got there (a loop back, a seek).
  - Overlap: partly overlapping clips crossfade (`TimelineChannel.GetFades`), but only between each other: the end of
    the first is a share (`OverlapShare`), not a fade to the base, so a property only the first has keys for stays at its
    key and then holds (the first version faded it back to the base: a ship flew back to its start); one only the second
    has fades in. An explicit fade out does go back to the base. `Restore` / Stop in the editor put back the base values
    of what was written. A live edit rebuilds (`EnsureCurrent` by asset + clip versions + targets), carrying base values
    over by channel and slot; what no clip animates any more goes back at once.
- `[Animatable]` (`Mono/Attributes.cs`, also a display name): private, unserialized or hidden fields and properties
  exposed to animation (`AnimationBinder.CollectExposed`, `ResolveMember` finds marked private properties). Public and
  serialized values were exposed already.
- Editor in `LELEngine.Editor/Animation`: `TimelineWindow` partials (IEditorPanel "Timeline", Window > Timeline, double
  click a .timeline, or select an object with a Timeline; Project panel: Create Timeline; no asset: "Create Timeline"
  makes component and asset): `TimelineWindow.cs` (host / document, toolbar, parameters as sliders, preview), `.Canvas.cs`
  (ruler, logic lane drawn with `StudioUi`'s shapes, channel headers S / M / R + object field + activity bar, clips with
  curves and key ticks, expanded rows of keyed properties with draggable diamonds, all mouse input), `.Edit.cs` (popups on
  right or double click instead of a deck: `DrawMarkerPopup`, `DrawClipPopup`, `DrawParameterPopup`; every other
  command). `TimelineDocument` (+ `TimelineRecord`: one undo step holds the asset, the clips keyed and the component's
  bindings), `TimelineComponentInspector`. Edit mode: any playhead move starts a preview (`TimelinePlayer.Preview`: no
  events), Stop restores; the scene save restores around the write (`BeforeSceneWrite`), play mode ends it first. Play
  mode: the window follows `host.Player`. The timeline and the clips keyed are written when an edit ends; the bindings
  live in the scene, saved with it. The host is found again by id after play mode, undo and reloads; a script reload
  clears the cache and loads the document again (`OnDomainUnloading`). Files changed on disk: `ReloadChanged`.
- Record (`TimelineRecorder`): watches the armed channels' objects: active, transform, serialized fields of every
  component, [Animatable] members (`GetAnimatableProperties(component, false)`: no C# properties, mostly the same values
  again), whatever the clips already key. Paused (and the game not running: there scripts change everything): a value
  that differs from the last pose becomes a key at the playhead, into the clip under it or a new `<Timeline> Clips/<Object>
  Recorded.anim`; a property new to the clip also gets its old value at the clip's start; the object is put back at
  once, so the base stays its own value and Stop restores. `Resync` after a seek: a pose written by the window is no
  change. Playing: the armed channels stop writing (`SetCapture`), `Persistent` keeps the player past the content, keys
  at the frame rate at the real tick times only while a value changes, `Simplify` thins them when it ends (line within
  0.2 % of the range), edit mode restores the objects. Works in play mode: the recording lands in the asset and stays.
- Verified (2026-11-02, probes in two throwaway projects): batch `-executeMethod
  TimelineProbe.Run`, 121 checks (keyed properties only, a script's writes before / during / after, partial channels,
  fades, overlaps, loops, transitions, quantization, crossfade, sustain, clip conditions, trim / speed / loop, seeks,
  end behaviours, [Animatable], events and markers, live edits, discrete kinds, rotations, mute / solo, identity,
  JSON / binary round trips, scene reload, a copied subtree). GUI editor with `exec` probes injecting `ImGui.GetIO()`
  mouse events: scrub, move / trim / fade / reassign clips, regions, popups, keys, parameters, playback through loop /
  transition / sustain, Stop restoring, recording paused and playing, undo of all of it, scene save without preview values
  (57 checks); play mode (13: play on awake, binding in the game's copy, markers, recording a script in the game,
  back in edit mode); hierarchy and project drops, a file changed on disk (9); scrubbing while recording, [Animatable]
  recorded, the logic menu (11); deleted object + undo, rename + reparent, the clip's file after a recording, Create Timeline (12). A built game
  (`-build`): binary .timeline and clips shipped, loop / transition / sustain and markers in `Player.log`. A real game
  project's 462 tests pass and the window opens in it. Canvases are not elements: test with injected mouse events and reflection on the window's fields. Play
  mode reloads the scripts: a probe's types go stale there (`AddComponent("Name")`, not `AddComponent<T>`).
- Known limits: a Timeline inside a prefab instance shows `targetId` / `targetName` as overrides (hidden fields, harmless);
  undoing a recording leaves the new .anim file behind (unused).

## UI and runtime additions for New Frontier's station UI (2026-10-29)

Added while porting NF's UI Toolkit screens (station hub, equipment, market, popups, pause and main menu) onto
`LELEngine.UIElements`; verified with the port's probe (a virtual gamepad driving the panel, screenshots, a render
texture read back), see the port's `PORTING.md` "UI".
- `Time.timeScale` (clamped at 0) scales `deltaTime` / `time`; `Time.unscaledDeltaTime` / `unscaledTime` do not
  (`Window.OnUpdateFrame`; `ResetTime` resets all). `Application.isPlaying` / `Application.Quit()` (closes the game
  window; logs in the editor). `Screen` (`Mono/Screen.cs`): `width` / `height` / `size`, `fullScreen` (window state),
  `currentResolution`, `resolutions` (the primary monitor's video modes), `SetResolution(w, h, fullscreen)`.
- Panel navigation from code: `IPanel.SendNavigationMove(direction)`, `SendNavigationSubmit()`,
  `SendNavigationCancel()` dispatch the same events the keyboard path makes (`Panel.Input.cs`: target = the focused
  element or the tree), so a gamepad's d-pad / A / B drive focus like arrows / Enter / Esc. `FocusController.IgnoreEvent(evt)`
  marks a navigation event handled: `PanelRootElement` then leaves the focus ring alone (a spatial navigation of the
  game's own, or a popup swallowing the move).
- Painter2D (`UI/Rendering/MeshGenerationContext.cs`) rewritten: sub paths (`MoveTo` starts one), `LineTo`,
  `BezierCurveTo`, `QuadraticCurveTo`, `Arc`, `ClosePath`; `Fill(FillRule)` ear-clips each sub path with a 1 px feather
  for anti-aliasing; `Stroke()` honours `lineCap` (Butt / Round / Square), `lineJoin` (Miter / Bevel / Round),
  `miterLimit`, feathered sides; `antialiasing` switches the feathers off.
- SVG as UI textures: `UI/Vector/SvgRasterizer.cs` (`internal`, `Rasterize(svgText, name)` -> `ImageData`): rect
  (rx) / circle / ellipse / line / polyline / polygon / path (M L H V C S Q T A Z, relative and absolute), groups with
  transforms (matrix / translate / scale / rotate / skew), solid fills and strokes, linear / radial gradients (stops,
  href inheritance, objectBoundingBox / userSpaceOnUse), opacities, caps / joins, fill rules; scanline coverage with
  4 sub-scanlines; raster scale clamp(256 / longest side, 1, 8), at most 1024 px; straight-alpha RGBA. Hooked by
  `UIAssets.TextureExtensions` (".svg") and `TextureAssets.LoadForUI`. Not covered: text, clip paths, filters, masks.
- Cameras rendering into textures: `Camera.targetTexture` (a `RenderTexture`, not serialized) and
  `Camera.backgroundColor` (alpha 0 = transparent). `Renderer.RenderFrame` first renders every enabled camera with a
  target through a child `TargetRenderer` of its own (`AddTargetTexturePasses`: shadows, opaque, transparent, post
  process; no sky, GI, VFX or UI; the camera's culling mask), `Framebuffer.AttachColor(RenderTexture)` (borrowed:
  never resized or deleted with the framebuffer), `RenderSettings.PreserveAlpha` (tonemap uniform `KeepAlpha`: the
  output keeps the scene colour's alpha, 0 where only the clear colour is) and `RenderSettings.KeepClearColor` (the
  scene's `EnvironmentSettings.BackgroundColor` is applied to every renderer's settings each frame by
  `SceneSettings.ApplyTo`; this keeps the camera's own). `SceneManager.MainCamera` skips cameras with a target. The
  child renderers are released when their camera goes, is disabled or loses its texture, and in `Dispose`.
- Render textures in the UI: `Background.FromRenderTexture(rt)` / `StyleBackground(RenderTexture)`;
  `Background.renderTexture`, `IsEmpty`, `FromReference`. Inline styles store the texture or render texture as the
  value's reference (`InlineStyle.backgroundImage`, `StyleApplier.BackgroundImage`), the renderer draws them flipped
  vertically (`UIRenderer.AddBackgroundImage` with `flipV`, `ResolveTexture` takes the handle). A view binding a
  camera's texture must bind again when the texture is created later (NF's `ShipPreviewRig.TextureChanged`).
- Fixes found by the port: `TypeRegistry.Register / Unregister` clear `UxmlRegistry` (tags of a game assembly loaded
  after the first UXML parse resolved to "unknown element"); `FlexLayout.ComputeNode` lays out the children of a text
  element (`requiresMeasure`) and takes the larger of the text and the children (a Button's glyph and label were never
  placed); `TransformUpdater` marks display: none subtrees (`VisualElement.hiddenByDisplay`: world opacity 0, render
  data dirty) so the renderer stops drawing them from their last transforms, and recomputes the whole subtree when
  shown again; `UIRenderer.UpdateMeshes` iterates a snapshot (elements added during a repaint threw "Collection was
  modified").
- `BaseVisualElementPanel.SettleLayout` (2026-11-02): what `GeometryChangedEvent` callbacks change (an element placing
  itself by its new size) is laid out in the same `Update` / `ValidateLayout`, up to `MaxLayoutPasses` (5, Unity's limit;
  past it the rest waits for the next frame and the panel warns once). Before, it waited for the next frame, which drew
  the state in between: NF's tooltip one frame out of place wherever its side depends on its size. Layout getters do not
  validate during `Update`, so test this through the geometry events per `Update` (they read nothing lazily), not `layout`.
- Layout quirk kept (Yoga's): measuring a column container of known width, a child with `flex-basis: 0` counts as
  its basis plus padding, not its min-height, so the container measures short and the child overflows at layout
  time. NF's settings fields give the dropdown input `flex-basis: auto`.
- `FlexLayout.ResolveFlexibleLength` (fixed 2026-10-31) returned a line's free space with every child frozen at its
  min / max counted twice (the first pass's delta and again the second pass's): a centred row whose growing child
  stopped at `max-width` shifted left by that child's growth. Seen on NF's SHIP page on wide screens (the hangar capped
  at 540 px pushed the weapon slot over the ship list). Now Yoga's `original - distributed`; only justify-content and
  auto margins read it.
- Scene documents: objects must follow their parent in the file (`[Scene] 'X': parent N not found ... kept at the
  root` otherwise); hand-written objects go after the parent's entry.

## MaterialPropertyBlock, [PerRendererData], ICustomRenderer (2026-10-31)

- Game effects belong in the game's repo, not here (the user's correction, 2026-10-31): a plume component written for
  one game was moved out; the engine keeps only the general features it needed (below).
- `MaterialPropertyBlock` (`Shaders/MaterialPropertyBlock.cs`, Unity's): float / int / vector / color / matrix / texture
  / float[] / vec4[] values by uniform name, written in place (no allocations when a block is updated every frame).
  `Material.SetUniforms(program, block)` uploads the material's values, then the block's (textures on the units after
  the material's: `TextureProperty.Bind`, `FloatProperty.UploadFloat` are the shared upload helpers). Names outside the
  shader's Properties block stay in the program for the next draw (a material uploads only declared names): shaders
  declare their per-renderer values with `[PerRendererData]` (`ShaderProperty.PerRendererData`; the material editor
  leaves them out and says how many there are).
- `MeshRenderer.SetPropertyBlock` / `GetPropertyBlock` / `HasPropertyBlock` (a copy, not serialized, as Unity): applied
  by the transparent pass and the per-object opaque draws (`Render`, `RenderSubMesh`); a renderer with a block leaves
  the GPU-driven buckets (`GpuScene.BuildTables`: per object, like shaders without an instanced variant). Depth, shadow
  and GI passes draw geometry only and ignore it.
- `Rendering/ICustomRenderer.cs`: a component (a game's script too) that draws a mesh it builds itself, shaped by its
  shader from per-draw values: `Material` and `PrepareDraw(out mesh, out block, out boundsMin, out boundsMax)`
  (false: nothing this frame). `TransparentPass.CollectCustomRenderers` takes them from the frame's behaviours on the
  camera's layers (active and enabled, a material whose program links), `GeometryPool.Prepare`s the mesh (code-built
  meshes are fine) and adds one entry each at the centre of its bounds (sorting; grab rectangles from the local bounds
  with the world matrix); drawn with the component's transform and the block on top of the material, no shadows, no
  GI. The inspector lists the material in the component's material section (`InspectorPanel`).
## When things are written: the scene on its Save, the rest as it changes (2026-11-02)

- The rule (the user's): the scene changes in memory and goes to its file on the user's action (Ctrl+S). Asset settings,
  project settings and editor settings are written as soon as they change: no Save / Revert buttons, no dirty marks and
  no question on close for them. Undo is the way back, and writes the file too.
- Exceptions, on a separate action (the user's correction, the same day): what would hang or compute a lot on a change,
  and the tools started in a window of their own.
  - Import settings of textures and models: a working copy until Apply (meta file, reimport; a model also reloads the
    scene from its current state) or Revert; not undo steps.
  - VFX Graph (it generates): `VfxGraphDocument` is dirty until Save or Ctrl+S in its window (the graph's, not the scene's);
    Revert; its close asks. Edits still apply live to the components playing it.
  - Audio Studio: Save or Ctrl+S, Revert, its close asks. Build Banks is an action of its own: Save builds none.
  - UI Builder: Save or Ctrl+S, Save As, the USS editor's Save and Revert (typed USS applies live, unwritten); its close asks.
  - Closing the editor (its window, File > Exit, `quit`) and File > Close Project ask about these three first, one question
    listing the files (`ConfirmToolChanges`, "Unsaved assets": Save All, Don't save, Cancel); the scene's own question comes
    after it. Save All that cannot write something stops the close, nothing lost (a new UI document: Save As opens in its
    window). Don't save reverts them at once, whatever the scene's answer. `quit --discard` asks nothing, as for the scene.
  - Opening another document over unsaved changes asks too, in the tool's own question: `VfxGraphWindow.Show`,
    `AudioStudio.Open` / `CreateProject` (also from an inspector: the event to show waits), `UIBuilder.Open` / `NewDocument` /
    `OpenStyleSheet`, closing a style sheet. What waited runs after the answer (a new UI document goes through Save As
    first), Cancel keeps everything, the document already open asks nothing and keeps its changes.
  - Still lost without a question, as before: import settings not applied when the selection changes or the editor closes.
- Written when an edit ends: `AnimationClipDocument`, `AnimatorDocument`, `TimelineDocument` and the clips it keyed:
  `EndEdit` -> `EditTracker.SaveAfterEdit`, at once, or at the release while a widget or a drag goes on editing (a recorded
  or dragged value can be an edit per frame: no write per frame); their undo records -> `EditTracker.SaveAsset`. Inspectors:
  `EditTracker.Commit` -> `SaveAsset`, for materials and ScriptableObjects as before and now controllers and clips. With
  these windows in front Ctrl+S saves the scene (only the VFX Graph window keeps it for itself). Audio import settings are
  written at once as before (one discrete choice: the banks rebuild in memory, only that clip decodes again).
- Project: `ProjectSettings.asset` when an edit ends (as before), Project.json as it changes, now with the build's
  `BuildOutputFolder` (relative to the project, empty: `Build/<project>`) and `BuildRelease`.
- Editor: preferences and hierarchy as before (0.4 s, 0.5 s after a change). `EditorApplication.UpdateEditorState`, twice a
  second and with nothing held, compares and writes: the scene view (`Library/EditorState.json`, with Nav, gizmo tool and
  space now), windows.json (`windowsfile=path` gives an automated run a file of its own, and turns these writes on),
  scripted windows (`Library/EditorWindows.txt`). `WindowPlacement.Update`: 0.5 s after a move or resize. ImGui's
  layouts: `IniSavingRate` 0.5 s. Toggles that were not kept at all are preferences now (`EditorMenu.Toggle`): the
  panels and snapping of Animator, VFX Graph, Timeline, Animation, Audio Studio, prefab mode's Auto Save, Test Runner's options.
- The other direction: Build no longer saves the scene by itself, it asks (`AskToSaveScene`: Save; Don't save goes on
  with the file and keeps the changes; Cancel). Nothing else writes a scene without the user asking.
- Left alone on purpose: prefab mode (a stage works like a scene: Auto Save writes as edits settle, on by default; off, the
  stage asks like a scene), prefab overrides' Apply / Revert (they move values between instance and prefab; they are not
  saving), an inline editor of a referenced prefab (writes when an edit ends and rebuilds the scene from its state, as
  before: not measured, a candidate for a separate action if it hitches).
- Verified in the GUI editor on a demo project (a probe in its `Assets/Editor`, `exec`), the scene holding an unsaved change
  all along: what is written at once after every kind of edit, undo, redo, an edit held by a button; what waits, with its
  Save or Apply, Revert, the question on close, Ctrl+S in the VFX Graph window, Save building no banks; every state file;
  the Build question; the question on closing with each answer, a new UI document stopping it, File > Close Project; the
  question before another document with each answer in the three tools; the scene's file untouched until Ctrl+S with the
  Animation window in front (131 checks). Real closes: `quit` and each answer,
  `quit --discard` (the process gone, the file as the answer said). Then the process killed, not closed, and started again (6: build options, a toggle, the scene view, a scripted window, two assets),
  and a script reload over edits left open (7: written, the windows back on their documents, the old assembly unloaded).
  A real game project: 462 tests pass. Not exercised: a real mouse (injected events and scripted clicks instead), Test
  Runner's and Animator's menu toggles (same `SetToggle` path as the Timeline's checked one).

## Scene changed on disk: reload, merge, conflicts (2026-10-31)

- The user's ask: like Unity, the editor follows the open scene's file changed outside it; when the outside changes
  do not collide with the unsaved ones it merges them on its own, else a popup lets the user decide.
- `LELEngine/Serialization/SceneMerge.cs` (public, no types loaded): `SceneMerge.Merge(mineBase, mine, theirsBase,
  theirs, resolve, instanceObjects)` on `SceneDocument`s. The editor's changes are mineBase -> mine (both written from
  live scenes by the same scripts, so a field the scripts gained or lost is no change), the file's are theirsBase ->
  theirs (both file bytes); the result is theirs with the editor's changes on top. Granularity: scene name; settings
  and component data property by property, nested `SerializedNode`s of the same class recursively (a property one side
  lacks compares as the class default, `GenericValues.DefaultProperty`); lists, dictionaries, references and assets
  are one value; GameObject name / active / parent / layer / hide flags; components by id (added, removed, order);
  prefab instances: prefab, parent, each `PropertyModification` (by target + `FullPath`), removed objects / components
  as sets, added components like components; entries by id (added on either side kept; a deletion takes its subtree,
  and a change inside a subtree the other side deleted is ONE conflict "deleted" for the whole subtree); sibling
  order (`MergeOrder`: mine's order when only mine reordered, both reordered differently = conflict; keys only one
  side has go after the key they follow there). Entries are written parents first (`Arrange`: each under the entry
  that owns its parent: an object entry, or the prefab instance holding that object, found through
  `instanceObjects` (default: `PrefabCache` ids combined with `PrefabIds.Combine`), fallback the last instance before
  it). Conflicts (`SceneMergeConflict`: Key, object, component, What, Mine, Theirs) go to `resolve` (null: the file's
  value) and are listed; `MineKept` = 0 means the result is the file.
- `SetSiblingIndex` does nothing on a root object (root order is the scene's object order): test sibling order
  under a parent.
- Editor (`EditorApplication.SceneMerge.cs`): `sceneFileBase` = the file bytes last read (`OpenSceneNow` reads the
  bytes itself) or written (`SaveSceneTo` reads them back). `CheckSceneOnDisk` (after an asset refresh; the edit
  scene's path also in play and prefab mode) notes a new `FileStamp`; `ReloadSceneIfChangedOnDisk` waits for play
  mode, prefab mode, `Scripts.IsIdle` and a pending merge, skips identical bytes, reloads a clean scene
  (`ReloadSceneFromDisk`), else merges: mineBase = `NormalizedScene(sceneFileBase)` (a temporary scene loaded and
  written by the current scripts, then `Unload()`: no callbacks, nothing was awake), mine = `SaveToBytes(Scene)`. No
  conflicts: `ApplyMerge` (loads the merged document as binary, or the file itself when `MineKept` = 0; one
  `SceneSnapshotRecord` "Merge Changes From Disk", so undo gives back the scene as it was; dirty when `MineKept` > 0;
  the file read becomes the base). Conflicts: `pendingMerge` + "Scene changed on disk" popup (`DrawSceneMergeModal`:
  table Object / Value / Here / On disk / Keep with `radio:Here` / `radio:Disk` per row, Keep all mine / from disk,
  Merge, Reload from disk, Keep mine (the file's bytes become the base, the scene stays dirty)). Batch mode has nobody
  to ask: conflicts keep the editor's values and are logged as warnings.
- Script reloads (`ReloadSceneAround`): an edit scene with a file is rebuilt from the file (`RebuildFromFile`) instead
  of the in-memory snapshot: clean = the file; dirty = the same merge, mineBase normalized by the OLD scripts before
  the swap (mine is the snapshot, also old scripts), so the unsaved changes are measured consistently and everything
  else comes from the file read by the new scripts. Fixes: a field renamed in the scene file and then in the script
  (the file reloaded first with the old scripts dropped it; the snapshot carried the loss over the reload).
  Conflicts at that point (the file changed too) load the snapshot and open the popup.
- Verified: batch probe (scratchpad `MergeProbe`, `-executeMethod MergeProbe.Run`, 31 checks: fields, nested
  objects, conflicts and resolutions, lists, transform and header, references, objects added on both sides,
  deletions, delete / change conflicts both ways with subtrees, reparenting, parent conflict, child order with and
  without conflict, components added / removed / removal conflict, a prefab instance's overrides from both sides, an
  object added under an instance's child, an override conflict, an instance reparented). GUI editor (CLI `set`, the
  file edited by PowerShell, `get`, `click radio:Disk`, `click button:Merge`, `undo` / `redo`): auto-merge keeping
  the scene dirty, the popup, the editor's own saves not merging, a field renamed in the file then in the script
  (clean: 5 kept, was 1 before; dirty: kept with the unsaved change), a change on disk during play merged after Stop.
  Not verified: the prefab-mode wait, batch-mode conflicts.

## Files changed on disk: what reloads in the editor (2026-10-31)

- The user's ask: every kind of file changed outside the editor must come back in by itself. Audited and verified in
  a live editor (scratchpad `MergeProbe`, `ReloadProbe.Setup` / `SetupMore` / `Report` via CLI `exec`, files
  edited by PowerShell). The asset watcher (debounced 0.5 s) runs `RefreshAssetsIfChanged` -> `AssetDatabase.Refresh`
  (also drops the meta cache: import settings are read again), then each cache below; the settings watcher runs
  `RefreshSettingsIfChanged`; `ScriptDomain` has its own watcher.
- Already worked: `ProjectSettings/Project.json` (`ReloadSettingsFile`, every field), `ProjectSettings.asset`
  (`ProjectSettings.Use`), models (`ImportChangedModels`), prefabs (`PrefabCache.ReloadChanged`, instances rebuilt),
  ScriptableObjects (`ScriptableObjectAssets.ReloadChanged`), .uss / .uxml (`UIAssets.ReloadChanged`), audio clips and
  the audio project, game / editor / test scripts (domain reload), the open scene (see "Scene changed on disk").
- Did not, now do (all in place: the loaded objects keep their identity, so scenes, components and windows holding
  them follow; one console line "[Editor] Reloaded (changed on disk): ..." from `EditorApplication.ReloadChangedAssets`):
  - shaders and their includes: `ShaderProgram` keeps the files it read (`ReadStages(..., files)`: the .shader and
    every include) and their stamp; `SourceChanged` / `Reload()` read the file again into the same object
    (properties, render state via `ShaderRenderState.CopyFrom`, instancing support), delete the GL program and start
    a new build, and reload the keyword variants and the instanced variant in place (a failed instanced variant may
    link now). `InternalStorage.ReloadChangedShaders` then gives the loaded materials of those shaders the properties
    they gained (`ResetToShaderDefaults(false)`). A shader that does not build logs "[Editor] ... does not build" with
    its log (the old program is gone: the objects draw nothing until the file is fixed, then they come back).
  - materials: `InternalStorage` stamps material files when loaded / registered / saved (`Material.Save` ->
    `MaterialWritten`), `ReloadChangedMaterials` -> `Material.ApplyBytes` (built-in engine materials skipped).
  - images: `TextureAssets.Entry.Sources` (the image files of the asset: one, six faces or volume slices, plus the meta
    file) with their stamp; `ReloadChanged` -> `Reimport` (import settings changed in the meta file too; same shape:
    the texture object is kept). UI images (`LoadForUI`, png / svg of UI documents) keep stamps too and read the file
    again into the same `Texture2D` (they ignore import settings).
  - physics materials 2D, VFX graphs, animation clips, animator controllers: `AssetFileStamps` (new, internal: the
    stamps of one cache, `Note` on load / save, `Forget`, `Moved`, `ReloadChanged(loaded, apply, tag)`), applied with
    `Documents.ReadObject` into the object / `ApplyBytes` (which bump `Version` and raise `Changed`: VFX programs
    recompile, animators re-bind).
- Not covered: fonts (.ttf, `UIAssets` fonts have no stamps), a prefab open in prefab mode changed on disk.
- A domain reload drops the clip / controller caches (a report after a script change reads them fresh: Version 1);
  check in-place reloads in a round without script changes.
- Verified (live editor, Release build): texture 64 -> 32 -> 16 -> 4 px (material's and component's references, the
  UI image too), maxSize in the meta 8 -> 2, material color, shader property added (material got it) and the fragment
  output changed (screenshots: magenta, cyan; GPU-driven instanced variant), a syntax error logged and recovered,
  ScriptableObject, physics material, clip length / controller duration / graph zoom (Version 2 / 2 / 3), model
  bounds, prefab field, gravity in ProjectSettings.asset, StartScene in Project.json, an editor script edit.
