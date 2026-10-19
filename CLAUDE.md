# LELEngine notes

## Building and running on this machine

- The `dotnet` on PATH is an older SDK that cannot target `net10.0` (error NETSDK1045). Build with the .NET 10 SDK
  at `C:\Users\Admin\.dotnet\dotnet.exe`, and set `DOTNET_ROOT=C:\Users\Admin\.dotnet` before running a built
  executable (`TestGame.exe`, `LELEngine.Editor.exe`), otherwise the runtime is not found.
- Debug outputs land in `<project>/bin/Debug/net10.0/` (Release in `bin/Release/net10.0/`). Shader library files
  are copied to `bin/.../Shaders/Engine/...` at build time. Python is not installed; use PowerShell with
  `System.Drawing` for image work (BMP to PNG, cropping).
- Development GPU: NVIDIA RTX 3080 Ti, OpenGL 4.6. Small test scenes run at 1000+ fps with vsync off, so
  frame-count based automation (`exitframe`, `dumpframe`) covers well under a second: prefer `exitseconds=S` and
  `dump=1 dumpafter=S` for anything time dependent.
- Never run `git commit` or `git push`; the user does that.
- `dotnet publish LELEngine.Editor -c Release -r <rid>` gives an editor ready to build games: the player is published
  into `Player/` by its own `dotnet publish` process (target `PublishPlayer`), for `PlayerRuntimeIdentifier` (default
  the editor's RID; self-contained when set, `PlayerSelfContained=false` to turn off; none: framework-dependent with
  every OS's GLFW), plus `LELEngine.Editor.Console.exe` on Windows. `dotnet build` copies the player's build output
  (`CopyPlayer`). `GameBuilder` renames the player's app host (`LELEngine.Player.exe`, or `LELEngine.Player` for a
  Linux / macOS player) to the game's name.
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

## Player build output

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
