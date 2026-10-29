# LELEngine notes

## Building and running on this machine

- Since 2026-10-20 the `dotnet` on PATH is the .NET 10 SDK (10.0.401, `C:\Program Files\dotnet`): plain `dotnet build`
  works and built executables find the runtime. `C:\Users\Admin\.dotnet` holds no SDK any more. The older notes
  (NETSDK1045, `DOTNET_ROOT=C:\Users\Admin\.dotnet`) apply only on a machine whose PATH still has an older SDK.
- Debug outputs land in `<project>/bin/Debug/net10.0/` (Release in `bin/Release/net10.0/`). Shader library files
  are copied to `bin/.../Shaders/Engine/...` at build time. Python is not installed; use PowerShell with
  `System.Drawing` for image work (BMP to PNG, cropping).
- Development GPU: NVIDIA RTX 3080 Ti, OpenGL 4.6. Small test scenes run at 1000+ fps with vsync off, so
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
- Open windows are saved (type, ImGui id, serialized fields) before a reload, before play mode and on close
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
- `ProjectPanel`: folder tree with folder icons (open / closed), the shown folder's parents opened, a breadcrumb; the
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
  unloaded script objects can go, `Sticky` for script build diagnostics: Clear keeps them, `RemoveSticky` at the next
  build). `RemovalVersion` tells the panel to rebuild instead of appending new rows.
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
  Tools; saved 0.4 s after `MarkChanged`, re-read when another editor changes the file. Automated runs use defaults and
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
  (`WindowStates.LastScenes` / `ReloadScriptsOnPlay`, saved with the window states on close), not in Project.json (2026-10-27).
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
  unresolved properties; the .animator / .anim asset inspectors). The page re-resolves its machine by id after undo /
  revert (`ResolvePage`): `ApplyBytes` replaces the controller's objects. The copy buffer is a document
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
  material properties per renderer (no property blocks), humanoid retargeting / IK, timeline / cutscene sequencing,
  moving the VFX canvas onto `GraphView`.

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
- Layout quirk kept (Yoga's): measuring a column container of known width, a child with `flex-basis: 0` counts as
  its basis plus padding, not its min-height, so the container measures short and the child overflows at layout
  time. NF's settings fields give the dropdown input `flex-basis: auto`.
- Scene documents: objects must follow their parent in the file (`[Scene] 'X': parent N not found ... kept at the
  root` otherwise); hand-written objects go after the parent's entry.
