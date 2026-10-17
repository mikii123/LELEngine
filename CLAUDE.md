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
