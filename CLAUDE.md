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
  editor's `Program.Main` with `ModelMaterialExtractor` (writes `Materials/<name>.material` with the engine's
  `Engine/LitTextured.shader` / `Engine/LitTransparent.shader`, embedded textures into `Textures/`, never overwrites).
- The lit shaders (`Lit`, `LitTextured`, `LitTransparent`) are engine shaders in `LELEngine/ShaderLibrary/Engine`
  (the GI and shadow contracts live there); new projects get no copies. A material naming a project copy that no
  longer exists (`Shaders/Lit.shader`) loads `Engine/Lit.shader` (`AssetDatabase.EngineShaderFallback`). Imports cache in `Library/ModelCache`
  (`ModelAssets.CacheFolder`, set when a project opens); player builds write the imported binary (`LELMODEL`
  magic) under the model's own file name, so the player never needs Assimp.
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
  (`<Name>.Editor.csproj`, ProjectReference to `<Name>.csproj`, which excludes `Assets\**\Editor\**`). Each project
  has its own obj folder (`Library/obj/<project>/`). Player builds compile only the game project.
- `Game.Editor.dll` loads into the same collectible `GameLoadContext` as `Game.dll` and is not registered in
  `TypeRegistry` (only temporarily while a window state is restored). Everything holding a type of it must be dropped
  in `EditorExtensions.OnDomainUnloading` (called from `EditorApplication.ClearTypeCaches`). `GameLoadContext` must
  not keep its own list of the assemblies it loaded: that kept the old context alive (leak warning after every
  reload); it resolves Game.dll for Game.Editor.dll through its `Assemblies`.
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
  `UnloadAll` (several loaded scenes, DontDestroyOnLoad). Verify play-mode work with a reload afterwards
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
- Test hooks (TestHooks / TestAnchors): `drag=N;from;to|...` (press N, hover N+10, drop N+12), `click=`,
  `doubleclick=`, `rclick=`, `ctrlclick=` (N;anchor|...), anchors `hierarchy:Name`, `project:Folder/File.ext`,
  `project:(empty)`, `component:Type`, `field:Type.field[@fraction]`, `row:Label/i[/row]`, `button:AddEntry`;
  `logfield=N;Object;Component.field|...`, `pingasset=N;path|...`, `lockinspector=N`, `undo=N|M`, `redo=`,
  `expandinspector=1`, `inspectorscroll=N;px`, `screenshotframe=a,b,c` (folder/fNNNN). Each hook name holds one
  value (repeat with `|`), and `Start-Process` splits arguments at spaces: no spaces in anchors.
- Verified 2026-10-20 with a probe project (scratchpad `Probe`: interface / abstract / EngineObject fields,
  dictionaries, nested lists, ScriptableObjects, Resources): batch `-executeMethod ProbeTests.Setup` / `Verify`
  (JSON, typeless JSON <-> binary, snapshot, duplicate remap), a game build and the player's log, scripted drags.

## Player build output

- `GameBuilder` ships only what the build uses (`BuildDependencies`): the build scenes, every file under a folder
  named Resources, and what those use, transitively: asset references anywhere in documents (read typeless with
  `DocumentReferences`, nested and polymorphic values, lists, dictionaries included), a model's imported materials
  (and `Materials/Default.material`), a project shader's #include files, USS / UXML url() / resource() / src. The
  rest is left out; `Library/BuildReport.txt` lists each shipped file with its reason and the files left out. Stored
  type names are checked against the engine and the player's scripts (Mono.Cecil): types of Editor scripts or missing
  classes are reported. `Data/` is deleted before it is written.
- `GameBuilder` (`LELEngine.Editor/Building`) ships assets the player uses as they are:
  - documents (scenes, materials, meta files) as binary LELB, models as their imported binary;
  - images as KTX 1.1 (`KtxFile`) baked by `TextureBaker`: every mip level (box filter, in linear light for sRGB),
    BC7 by `Bc7Encoder` (own, mode 6 only, fast) or by BCnEncoder.Net for `TextureImporter.compression =
    BC7HighQuality` (16-38 s per 1K image), `None` keeps RGBA8; HDR stays RGBA16F, volumes RGBA8. Cached in
    `Library/TextureCache`. Sprites and the images USS / UXML reference (`url()`, `resource()`) stay PNG: the UI
    draws images as they are;
  - `Data/.assetindex` (GUID to path): the player reads it instead of every meta file;
  - `<model file>.sdf` next to each model: the GI's mesh distance fields at every scale the scenes draw the mesh
    (scale keys quantized to 0.001) and at scale 1, baked by `DistanceFieldBaker` with the renderer's GPU build
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
