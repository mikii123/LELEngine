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
  (`LELEngine.Player.exe`, or `LELEngine.Player` for a Linux / macOS player) to the game's name. A player published
  with a runtime identifier is compiled ahead of time (ReadyToRun, 2026-11-19: the publish restores the SDK's
  crossgen package from NuGet once, and an editor's publish copies it to `Crossgen/` next to the editor); the project's
  build setting Compile Ahead Of Time (`BuildReadyToRun`) compiles the game's scripts with it too and fails a build
  that cannot ("First use at load, not in play").
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

## BRUTAL framework in place of OpenTK (2026-11-22, branch `brutal-port`)

- The user's ask: port the engine entirely from OpenTK to the BRUTAL framework (Ahwoo / RocketWerkz, NuGet
  `Ahwoo.BRUTAL.*`, source github.com/ahwootech/BRUTAL): the renderer on OpenGL first, then on Vulkan with the backend
  chosen in Project Settings > Graphics, then an optimization pass for the new backend (ray tracing, GI). On a separate
  branch; picture differences are reported, not tuned away.
- No OpenTK package is left. What the code used of it is the engine's own, under the same type and member names, so a
  game's scripts change only their usings (`OpenTK.Mathematics` -> `LELEngine.Mathematics`, `OpenTK.Windowing.*` ->
  `LELEngine.Windowing`); OpenGL commands of a game's own go through the graphics device now (next section):
  - `LELEngine/Mathematics`: Vector2 / 3 / 4, Vector2i / 3i / 4i, Matrix3, Matrix4, Quaternion, Color4, MathHelper,
    Box2i are OpenTK 4.9.4's source files (MIT) in the engine's namespace, so every result is bit for bit what it was;
    18 members that needed types not carried over are gone (conversions to the double and half vectors, Matrix2
    transforms). BRUTAL's own numerics (`Brutal.Numerics`: float3, float4x4, ...) are a System.Numerics mirror its
    authors call temporary ("math libraries ... should be picked by the developer, not the framework"): they appear only
    where BRUTAL's API takes them (window sizes, monitor positions). Same memory layouts (row-major matrices, xyzw).
  - `LELEngine/Windowing` on `Brutal.GlfwApi` (GLFW 3.5.1, RocketWerkz's fork): `NativeWindow` (its `Handle` is
    BRUTAL's `GlfwWindow`), `GameWindow` (OpenTK's loop as it was: one core for the loop's thread, 8 ms timer period),
    settings, event args, `MouseState` / `KeyboardState`, `MouseCursor`, `Monitors`, `Joysticks`, `GraphicsContext`,
    `WindowSystem` (starts GLFW; its error callback only logs, a refused context version is the constructor's
    exception). `Keys`, `MouseButton`, `KeyModifiers`, `InputAction` are OpenTK's files: the same names and values, so
    saved input configurations read as before. `NativeWindow.Current` = the window whose context is current on the
    thread (`ShaderCompileThread` shares with it). Typed characters come from GLFW's own char callback: BRUTAL's
    `OnChar` narrows code points to 16 bits.
  - OpenGL itself: `Brutal.OpenGlApi.OpenGl.Native.*` (GLAD's pointers), called only by the OpenGL device through
    `Graphics/OpenGL/OpenGLBackend.cs` (internal; it loads `glMaxShaderCompilerThreadsKHR / ARB` itself) with OpenGL's
    values under OpenTK's names (`Graphics/OpenGL/GLEnums.cs`, internal). The first phase of the port kept a static
    `GL` class with OpenTK's signatures for all rendering code; it is gone with the move to the device.
- The editor keeps Hexa.NET.ImGui (BRUTAL has no ImGuizmo); only its backend's window and graphics calls moved.
  Generated script projects reference `Brutal.*.dll` next to the editor where they referenced `OpenTK*.dll`
  (`ProjectGenerator.BrutalAssemblies`): an existing project's .csproj files are rewritten on the next open.
- BRUTAL's packages bring analyzers for BRUTAL's own conventions (1,400 "prefer a UTF-8 string" warnings on the
  editor's ImGui calls): `Directory.Build.targets` removes them from every compilation.
- License (BRUTAL License v1.0, not OSI): a distributed game must ship BRUTAL's NOTICE.txt, name its copyright in the
  credits and show the Brutal logo before gameplay. `LELEngine/NOTICE.txt` and `LELEngine/Licenses/` (BRUTAL's license
  and third-party notices, OpenTK's MIT license for the math) are Content: they reach the editor, the players and
  every game build. Not done: the logo on the splash screen and the credits line are each game's to add.
- Platforms: BRUTAL ships natives for win-x64, linux-x64 and osx-arm64 only (OpenTK's GLFW had win-x86 / arm64,
  linux-arm / arm64 and osx-x64 too).
- Verified on the 780M against a build of the commit before (scratchpad `tools/` of session 9cb23804: `regress.ps1` +
  `imgdiff`, `edtest.ps1`, `inputtest.ps1`): TestGame's 18 scenes at frame 400 (lumen, vct, gi=0, materials,
  refraction, lights, suns, outdoor, gpudriven=0, ssaa, trails, vfx, volumetrics, impostors, ui, physics, anim, nav)
  identical or within the old build's own run-to-run noise (max 0-6 of 255; the time-driven four differ as two runs of
  the old build do); the GUI editor's Scene, Game, Inspector and play-mode screenshots and a built player's frame bit
  for bit; keys posted to the window (WM_KEYDOWN) toggle what they toggled; batch `-createProject` and `-build`; the
  Audio Studio and UI Builder windows open on the shared context. Not checked: Linux and the Docker build, gamepads,
  fullscreen switching and the launcher by hand, a real mouse.
- How the port was made (tools in that scratchpad): `apiscan` (Roslyn over every project: each OpenTK symbol used,
  with signatures and enum values: 521 graphics, 217 math, ~350 windowing symbols), `glenums` (the enum file from
  OpenTK's metadata), `mathshake` (copies source files into a namespace and removes members that no longer compile).

## Graphics device: the engine's own API, OpenGL and Vulkan under it (2026-11-23, branch `brutal-port`)

- The user's ask, after a first design laid OpenGL's commands over Vulkan: Vulkan "not as an emulator; if anything the
  other way round, OpenGL should be the emulated one". So the engine renders through an API of its own, shaped like
  Vulkan (`LELEngine/Graphics`, namespace `LELEngine.Graphics`), and each graphics API is a device under it:
  `Vulkan/VulkanDevice` natively, `OpenGL/GLDevice` laying it over OpenGL's state machine. Nothing outside
  `LELEngine/Graphics/OpenGL` and `LELEngine/Graphics/Vulkan` gives API commands: no `GL` class, GL enums, object
  handles or texture units in rendering code, the editor or games.
- The API (`GraphicsTypes.cs`, `GpuResources.cs`, `CommandList.cs`, `GraphicsDevice.cs`, `TexelConversion.cs`):
  `Gfx.Device` (`GraphicsDevice.Current`), `Gfx.Commands`, `Gfx.Capabilities` (`DeviceCapabilities`; the engine's
  `GraphicsCapabilities` reads it). Objects: `GpuTexture` / `GpuBuffer` made from descriptors with usages
  (`TextureDescriptor`, `TextureUsage` Sampled / Storage / Attachment / SliceImages: a 3D texture whose slices are
  bound as 2D images, below; `BufferDescriptor`, `BufferUsage`), samplers
  apart from textures (`Samplers.LinearClamp` ..., `GetSampler(SamplerDescriptor)`), `GpuProgram` (`ProgramDescriptor`:
  the GLSL stages as the engine writes them, the attribute locations; it holds its uniform values and its samplers'
  textures by slot), `GpuFence`, `GpuTimestamp`. The engine's wrappers keep their names: `Texture.Gpu`,
  `RenderTexture.Gpu`, `Framebuffer.Begin`, `ShaderProgram.SetTexture(name, texture[, sampler])` (by name: a sampler not
  given a texture reads a black dummy), `GeometryPool.Bind`.
- Commands (`CommandList`): render passes with load operations (`BeginRenderPass(descriptor)`, the
  `(color, depth, loads)` and `(width, height, samples)` overloads, `EndRenderPass`); draws only inside a pass;
  `State` (`RenderState`: depth test / write / compare, culling, blend factors and operations, colour mask, depth bias,
  wireframe, conservative rasterization where `DeviceCapabilities.ConservativeRasterization`: part of a pipeline on
  Vulkan), viewport, scissor, program, vertex input (`VertexInputLayout`), vertex and
  index buffers, uniform and storage buffers and images by binding number (the shaders' `layout(binding = N)`), draws
  (indexed, instanced, indirect, indirect count, ranges), dispatches (direct, indirect), buffer and texture copies,
  blits, texture clears, mip generation, copies into buffers for readbacks, timestamps. Dispatches, copies, blits,
  texture clears and `CommandList.GenerateMipmaps` end an open pass (begin it again after them); uploads
  (`GpuBuffer.SetData`, `GpuTexture.SetData`, `GpuTexture.GenerateMipmaps`) may be given anywhere, also inside a pass
  (a texture made the moment a draw first needs it).
- Conventions on every API: clip space y up and z from -1 to 1, the first row of a texture or target is the lowest,
  counter-clockwise front faces. Shaders stay GLSL as written for OpenGL (loose uniforms, samplers without bindings,
  blocks and images with `binding = N`); the Vulkan device compiles them itself (below).
- Choosing the API (the user's ask: in Project Settings > Graphics): `GraphicsSettings.GraphicsApi` (OpenGL by
  default; the combo under Rendering). Builds write it into boot.json (`BootConfig.GraphicsApi`: the player's window
  comes before the project settings are read); the player and TestGame take `graphics=opengl|vulkan`; the editor runs a
  project on its API (`ProjectGraphicsApi`; `graphics=` on its command line, `-graphics` in batch mode, wins).
  `Window.Open` makes the first window: a Vulkan device that cannot be made on the machine
  (`GraphicsApiUnavailableException`; the device's `Destroy()` releases what it made, the window is destroyed) is
  replaced by OpenGL (`GraphicsDevice.FellBackFrom`; Player.log `[Player] Graphics API OpenGL (Vulkan is not available
  on this machine)`), OpenGL steps from 4.6 down to 4.3. The device is made once per process: a project chosen in the
  project chooser (or after Close Project) on another API opens in a new editor process, and Restart Editor (under the
  combo while the setting and the running device differ) closes the editor and starts it again on the same project; it
  refuses while anything is unsaved (`EditorApplication.RestartEditor`). The shader compile helper process runs on
  OpenGL only (it exists for OpenGL drivers that hold a process while it compiles).
- The OpenGL device (`Graphics/OpenGL`): commands run as they are given. A state cache (what the context was last
  told: render state, program, vertex array, framebuffer, textures and samplers per unit, buffer ranges, viewport,
  scissor) is the only thing that sets OpenGL state, so it is always right. A pass binds a framebuffer object made for
  its attachments (cached per attachment set, dropped when one of its textures is deleted) and clears what its load
  operations say; a program's samplers take texture units 0, 1, ... in slot order and the device binds their textures
  and samplers there before a draw (an int set on a sampler is ignored); the last unit is the device's own for uploads
  and reads; vertex arrays are made per layout (`glBindVertexBuffer`); a draw or dispatch of a program that writes
  images or storage blocks puts a full `glMemoryBarrier` before the next command. Uploads of half floats convert on the
  CPU. Programs: parallel compile (KHR / ARB), binaries (`GetBinary`), labels with `LEL_GRAPHICS_LABELS=1`. A window's
  back buffer is colour only (no depth / stencil bits: draws with depth testing in a second window sharing the context
  were tested against whatever its depth buffer held).
- The Vulkan device (`Graphics/Vulkan`; BRUTAL's `Ahwoo.BRUTAL.Vulkan`, `.Vulkan.Abstractions`, `.Vulkan.Vma`,
  `.ShaderC`): Vulkan 1.3 core (dynamic rendering, synchronization2, the extended dynamic states), VMA, and
  `VK_EXT_depth_clip_control` (required: the engine's clip z -1..1). The engine's conventions without a flip anywhere:
  viewports as given, `frontFace` clockwise, a back buffer of its own (RGBA8 at the window's size) blitted flipped into
  the swapchain at present (vsync through present modes). Programs: each stage's GLSL compiled by shaderc (Vulkan 1.3
  target, relaxed rules, auto bind / map, macros `LEL_VULKAN`, `gl_VertexID`, `gl_InstanceID` without the base
  instance, `gl_PointCoord` turned to the engine's rows), reflected with `SpirvModule` and linked by the device: one
  descriptor set, bindings dense over all stages, each stage's loose uniforms (its `gl_DefaultUniformBlock`) a
  dynamic uniform buffer copied into frame memory when a value changed, varyings matched by name, the shaders' per-kind
  `binding = N` of blocks and images mapped to the set; slots in OpenGL's naming (`FlattenMembers`). Pipelines cached
  per program, render state, vertex layout, topology and attachment formats, and on disk; descriptor sets cached over
  submissions; commands ordered by the uses of their resources (all three: "The Vulkan device's ordering" below).
  Uploads and `GenerateMipmaps` inside a pass go ahead of the stream into a setup command buffer while the stream has
  not used the resource in the submission, else the pass is suspended and resumed (~3 us). Unset or disposed bindings
  get stand-ins (black textures, a zero buffer). Meanings kept equal to the OpenGL device: SRGBA8 textures are UNORM
  images with an sRGB view for sampling (the OpenGL device never enables `GL_FRAMEBUFFER_SRGB`: draws, blending,
  clears and blits work on the stored bytes, mips filter in linear light); Depth24 is D32F on AMD (no 24-bit depth),
  its constant depth bias x4 (OpenGL's unit 2^-22: equal from depth 0.5 on). `Destroy()` releases everything when the
  device is given up; the constructor throws with the reason when the machine cannot run it (no loader, no 1.3 GPU, a
  missing feature). Validation: `LEL_VULKAN_VALIDATION=1` with the Khronos layer installed (or found through
  `VK_LAYER_PATH`), `VK_KHRONOS_VALIDATION_VALIDATE_SYNC=true` for synchronization; `LEL_VULKAN_DISABLE=rayqueries`,
  `conservative` (VK_EXT_conservative_rasterization, asked for where the GPU has it); `LEL_VULKAN_SHADER_INFO` (the
  driver's statistics and disassembly of every pipeline: "Voxel GI and distance fields on Vulkan" below).
- The devices' regression test: a console project in the scratchpad of session 9cb23804 (`vktest`; copies `vktest-rt`,
  `vktest-perf` in session 2a1d1c21's) runs the same device calls on both APIs in two processes and compares what they
  read back (526+ results: clears, passes, every blend pair, formats, samplers, mips, layers, cubes, 3D, BC7, blits,
  copies, buffers, uniforms of every type, blocks, images, compute, every draw kind with the draw parameters, points,
  lines, bias, wireframe, passes without attachments, fences, timestamps, hidden windows presented and resized, binary
  round trips, geometry stages, ray queries, conservative rasterization, the fallback to OpenGL in one process) and
  builds every engine shader
  variant on both (170 programs, pipelines on Vulkan). Known faults of AMD's OpenGL driver it shows: the first counted
  indirect draw after a plain indirect draw of an earlier pass draws nothing; a pass without attachments is
  rasterized single-sampled whatever its sample count.
- Pictures, Vulkan against OpenGL (TestGame frame 400, 780M; Vulkan repeats run to run): equal but for `vct` (max
  105, mean 13: AMD's OpenGL driver ignores the sample count of the coverage voxelization's pass without attachments,
  so each covered pixel counts 1/8 and walls wash out; Vulkan gives what the shader intends) and isolated pixels (equal
  depths where walls meet, navmesh debug lines on surfaces, impostor silhouettes). Not aligned: reported to the user.
- How the move was checked (scratchpad tools of session 9cb23804; `check.ps1`, `regress.ps1`, `imgdiff`): TestGame
  runs hidden with a fixed time step (`hidden=1 fixeddt=0.0166667`: frame 400 is the same moment in every run; the
  same two switches were added to a build of the engine before the move for the baseline), its 18 scenes against
  that baseline within the baseline's own noise (max 1-6 of 255; `vfx` is not deterministic), ~30 variants of the GI,
  media, impostor and GPU-driven paths against the build each step started from (max 1-6), the editor's panels
  (Inspector, Project, Hierarchy, Scene, Game, Console, Lighting, Project Settings, UI Builder, Audio Studio, the
  frames it drew, the scene view's depth probe) against the editor before the move (identical; the 3D views within 1),
  a built player's frame bit for bit, New Frontier's unit tests. The editor's `hidden=1` (never shows a window; needs
  a project on the command line) was added for this. On Vulkan: TestGame's 18 scenes with validation (no messages),
  the editor's same CLI session on a project set to Vulkan (panels identical to OpenGL's, 3D views within 2, the depth
  probe within 1e-6), a built player started on Vulkan by its boot.json (its frame within 2 of OpenGL's), New
  Frontier's 535 tests in a batch editor with `-graphics vulkan` and its Flight scene in the Game view (edit mode
  within 1 of OpenGL). OpenGL's frame times before and after the move (TestGame, 780M, frame trace medians): GPU the
  same (lumen 6.72 / 6.72 ms, vct 8.88 / 8.96), CPU work 0.1-0.2 ms more a frame (the device's state and binding
  layer).
- Notes in later sections written before 2026-11-23 name OpenGL details that are gone: texture units and their
  constants (samplers are given textures by name now), `GL.*` calls, `GLCapabilities` (now `GraphicsCapabilities`),
  `Texture.Handle` / `RenderTexture.Handle` (now `.Gpu`), `GLState`, `ShaderType` (now `ShaderStage`).

## Hardware ray tracing for Lumen's rays (acceleration structures, ray queries; 2026-11-23, branch `port-rt`)

- Part of the user's optimization pass "for the new graphics backend, with ray tracing, GI and the other features in
  mind": Lumen's rays trace the scene's triangles with ray queries on a Vulkan device whose GPU has them, the distance
  fields everywhere else (OpenGL, GPUs without ray queries, the setting off).
- API (`LELEngine/Graphics`, nothing of the GI in it): `GpuAccelerationStructure` made by
  `GraphicsDevice.CreateAccelerationStructure(AccelerationStructureDescriptor)`: a bottom level from
  `AccelerationStructureTriangles` (vertex buffer range: offset, stride, count, `Float3` / `Float2` positions; index
  buffer range: offset, `UInt16` / `UInt32`, or none; triangle count; opaque), kept by the structure and read by every
  build; a top level for at most `MaxInstances`; or `CompactCopyOf(source)` at the source's `CompactedSize`.
  `AccelerationStructureFlags` AllowUpdate, AllowCompaction, PreferFastTrace, PreferFastBuild. Commands (each ends an
  open pass like a dispatch): `BuildBottomLevels(span, update)` (one command, refit with `update`), `BuildBottomLevel`,
  `BuildTopLevel(topLevel, ReadOnlySpan<AccelerationStructureInstance>, update)` (instance: engine `Matrix4` transform,
  24 bit custom index, mask, `AccelerationStructureInstanceFlags` TwoSided / FlipFacing / ForceOpaque / ForceNotOpaque,
  the bottom level), `CopyAccelerationStructure` (compacts into a compact copy, clones otherwise),
  `SetAccelerationStructure(binding, topLevel)` (the shaders' `layout(binding = N) uniform accelerationStructureEXT`, a
  number space of its own; null: an empty top level). Facing as rasterization takes it: a triangle a ray sees
  counter-clockwise (cross(v1 - v0, v2 - v0) pointing back at it) is hit on its front. Build inputs need
  `BufferUsage.AccelerationStructureInput` (devices without ray queries ignore it). `DeviceCapabilities.RayQuery`;
  OpenGL: false, `CreateAccelerationStructure` and the commands throw `NotSupportedException`.
- Vulkan (`Graphics/Vulkan/VulkanAccelerationStructure.cs`, `VulkanCommandList.AccelerationStructures.cs`; the device,
  command list and program classes are partial): the device asks for VK_KHR_acceleration_structure, VK_KHR_ray_query,
  VK_KHR_deferred_host_operations and the features acceleration structure, ray query, buffer device address when the
  GPU has all of them, and starts without them otherwise (`LEL_VULKAN_DISABLE=rayqueries` makes it start without them
  on any GPU); VMA then allocates with device addresses. A structure lives in a buffer of its own (sizes from
  vkGetAccelerationStructureBuildSizesKHR); builds work in one device scratch buffer (grown as asked; builds of one
  command side by side, aligned to minAccelerationStructureScratchOffsetAlignment; at most 64 MB a command); a top
  level's instances go through the frame's memory into its own instance buffer by a copy before each build (at a
  16-byte aligned place in it: VMA starts such a buffer at any multiple of 4, which the validation layer caught in the
  scenes, VUID-vkCmdBuildAccelerationStructuresKHR-pInfos-03715); a structure
  made with AllowCompaction writes its compacted size into a query after every full build (read once the submission
  has finished: `CompactedSize` never waits); unset bindings trace an empty top level built ahead of the stream.
  Builds, copies and size queries are ordered by their uses like every command ("The Vulkan device's ordering" below):
  the vertex and index inputs and the scratch buffer at the build stage, the instance copy before the top level's
  build, a build's writes before the rays that trace the structure (compute and fragment shaders) and before its size
  query; a top level being traced marks its bottom levels used by the submission (their destruction waits for it).
  SPIR-V reflection: `SpirvResourceKind.AccelerationStructure` (OpTypeAccelerationStructureKHR); programs bind it as
  VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR; shaderc compiles GL_EXT_ray_query for Vulkan 1.3 as is.
- Engine: `Rendering/MeshAccelerationStructures` (graphics thread): a bottom level per mesh of the geometry pool, of all
  its triangles as the pool holds them (the pool's buffers carry `AccelerationStructureInput` on such devices), made the
  first time it is asked for and built with the others of that frame in one command (`BuildPending`), PreferFastTrace +
  AllowCompaction, swapped for a compacted copy once the GPU told its size when that saves an eighth or more, made again
  when the mesh's hidden ranges change, freed with the mesh's ranges (`GeometryPool.Release` / `Clear`) and all of them
  when Lumen's rays stop tracing triangles. `Rendering/Lumen/SceneRayTracing` (one per `LumenScene`, so per renderer):
  instance i of the top level = GI object i of this frame (the lighting camera's GI renderers, the objects of the
  distance field passes), custom index = its row of the object table, its mesh's bottom level placed in the GI's
  scaled-local space (`CreateScale(o.Scale) * rotation * translation`, as the cards are captured); built again when the
  instances (count, bottom levels, rows) changed, refitted when only placements did (built again after 60 refits),
  kept otherwise; plus a table of each object's first index and base vertex in the pool. `LumenScene.Update` builds it
  after the tables, `LumenScene.SetUniforms` binds it: acceleration structure 0, storage 19 (pool vertices), 20 (pool
  indices), 21 (the ranges). GPU profiler section `GlobalSDF.rayTracing` (the scene's update with the builds).
- Shaders: `Engine/SceneRayQuery.glsl` (`TraceSceneRay`: the nearest hit's distance, object and triangle normal facing
  back along the ray, read from the pool through the object's range and the instance's transform; `SceneRayClear`:
  shadow rays, first hit ends them; both bounded by the GI volume as the distance field is: a ray from outside it hits
  nothing, one leaving it ends there; `SampleSurfaceCacheAtSceneHit`: the cards of the known object, no search). The
  callers have the distance field code unchanged in the `#else` of `#ifdef LEL_RAY_QUERY`: `ProbeTrace.shader` (screen
  probe rays after the screen trace, near and far), `RadianceCacheTrace.shader`, `CardRadiosityProbes.shader`,
  `CardLighting.shader` (local lights' GI shadows). The define is a variant the passes ask for
  (`Lighting.RayTracingDefines`, `ShaderProgram.RayQueryDefine`); the preamble adds `#extension GL_EXT_ray_query :
  require` with it where `ShaderFeatures.RayQueries` (`GraphicsCapabilities.RayQueries`), and drops it elsewhere.
  Still on the distance fields: the object at a screen trace's hit (the field's object ids), the radiance cache probes'
  validity (inside geometry) and the far field's clearance test, the debug views, effects. The voxel cone tracing's SDF
  modes trace the top level too since 2026-11-23 ("Voxel GI and distance fields on Vulkan" below).
- Setting: `GlobalIlluminationSettings.HardwareRayTracing` (default on; Project Settings > Lighting, "Ray Tracing",
  shown for both methods since VCT reads it too), taken with the method (`Lighting.HardwareRayTracing`,
  `HardwareRayTracingActive`: fixed while the game
  runs, the editor switches it in edit mode and the passes make their tracing programs again); the console says which:
  `[GI] Rays: hardware ray tracing (...)` or `[GI] Rays: distance fields (why)`. TestGame `hwrt=0|1`.
- Verified on the 780M (Vulkan 1.4.315, driver 2.0.353), scratchpad tools of session 2a1d1c21 (`vktest-rt`, a copy of
  the device tests of session 9cb23804; `tools/vkrun.ps1`, `gitimes.ps1`, `giparse.ps1`, `regions`): the device tests'
  `rayQuery` (bottom levels of a 16-bit indexed quad with vertex and index offsets, an unindexed triangle, two
  geometries of one mesh; a top level placing them translated, scaled, turned; 12 rays: distances, custom and instance
  indices, primitive and geometry indices, barycentrics, facing, masks, back faces culled, an interval too short; an
  update of the top level, a refit of a bottom level, a compacted copy, an unset binding, top levels of 1..5 instances
  side by side) against the same rays traced on the CPU in doubles: 527 equal, 6 known (OpenGL's, as before), 0
  different, no validation messages (sync validation too); a device started without ray queries refuses them with
  `NotSupportedException` and compares the same; all 174 engine programs (the 4 ray query variants among them) build
  on both APIs. TestGame on Vulkan with validation: the GI scenes with `hwrt=1` without messages; `hwrt=0` the 18
  scenes equal to the frames before within the run-to-run noise (max 0-4, vfx not deterministic); OpenGL's check 0
  different. Not checked: the setting in the editor by eye and its switch in edit mode, a GPU without ray queries
  (only the switch above), Linux.
- What the triangles change in the picture (frame 400, `hwrt=0` -> `hwrt=1`, of 255): mean 0.03-2.3, max 1
  (volumetrics) to 119 (lights); brighter where the distance field closed gaps and grazed surfaces (under the table and
  the box, the spheres' lower halves and their shadows: +6 to +7 on the floor under the table in materials and lights,
  up to +37 on a sphere's underside), changes on the transparent objects (they read the radiance cache, whose probes
  trace triangles now: up to -52 on the night scene's faceted glass ball) and per facet of the faceted ball (±13: a
  hit's normal is the triangle's, not the field's gradient). The ray traced frames repeat as the others do (max 1
  between runs).
- GPU (1280 x 720, medians of three interleaved runs; the iGPU clocks lower under the heavier sphere tracing, so even
  sections that trace nothing ran up to 20 % slower with `hwrt=0`): `RadianceCache.trace` 0.74-1.24 -> 0.08-0.15 ms,
  `ProbeGather.trace` 0.78-2.51 -> 0.65-2.03, `SurfaceCache.radiosity` 0.13-0.90 -> 0.15-0.60, `SurfaceCache.lighting`
  the same; the GI's sections 3.3-7.5 -> 2.5-5.0 ms, the frame 5.1-11.5 -> 4.5-9.3 ms (lumen 11.49 -> 8.37, outdoor
  7.09 -> 5.19). Moving objects (lumen `animate=1`): the top level refitted every frame, `GlobalSDF.rayTracing` 0.03
  ms; the GI 7.95 -> 4.76 ms. Bottom levels: TestGame's meshes 0.02-2.75 MB, a third to a half of it left after
  compaction.

## The Vulkan device's ordering, caches and speed (2026-11-23, branch `port-vkperf`)

- The ask: a performance pass of the Vulkan device, at least as fast as OpenGL on every TestGame scene (GPU and CPU),
  no picture changed. Before it images lived in `GENERAL` and a full barrier (all commands, all memory) stood at each
  change between passes, dispatches and transfers; descriptor sets were cached for one submission.
- Ordering by use (`Graphics/Vulkan/VulkanSync.cs`; the device's, command list's, resources' and programs' partial
  `*Sync.cs` files): a `SyncState` per buffer and per subresource of an image (level x layer; one for the texture
  while all are alike) holds its layout, the stages and accesses of its last write, the stages that read it since and
  those the write is visible to. Every command notes its uses (a pass's attachments; what the program's descriptor set
  binds, by the program's stages: sampled textures, images, uniform and storage blocks, traced acceleration
  structures; vertex, index and indirect buffers; the sources and destinations of copies, blits, clears, uploads and
  mip generation; build inputs, scratch and instances), and `Sync.Apply` records only what a use has to wait for, as
  one `vkCmdPipelineBarrier2` before the command: image barriers where a layout changes, one memory barrier for the
  rest, with the stages and accesses of both sides. Layouts: attachments `ATTACHMENT_OPTIMAL`, sampled
  `READ_ONLY_OPTIMAL`, transfers `TRANSFER_SRC` / `DST`, textures shaders write as images `GENERAL` for every use; a
  use that overwrites a subresource whole (a pass that clears it or does not load it, an upload of a whole level)
  leaves its contents behind (`UNDEFINED`). A program's next draw skips the look when no state changed since its last
  (`SyncEpoch`). AMD's GPUs fetch vertices in the vertex shader: vertex and index reads count the pre-rasterization
  stages too (with the input stages alone the driver let a buffer be rewritten under a draw that had not read it yet).
- Passes (dynamic rendering): rendering begins with the pass's first draw or clear, after that draw's barriers; a
  draw that needs a barrier on a resource the stream has used in the submission ends the rendering and begins it
  again keeping the attachments; a barrier on one it has not used goes ahead of the stream into the setup command
  buffer and the pass goes on. Clears of whole attachments are load operations (also `ClearTexture` of a texture that
  can be an attachment). A pass whose draws sample its depth attachment renders it read-only while they do
  (`READ_ONLY_OPTIMAL`, no depth writes whatever the render state says, store op none).
  A pass that draws into level k > 0 of a texture its draws sample gives their samplers a view of levels 0..k-1
  (downsampling chains; a pass that read levels under the one it draws into would see them clamped: the engine has
  none); one that draws into level 0 of a texture it samples (a feedback loop OpenGL tolerates) takes that texture
  into `GENERAL` for good (`VulkanTexture.EnterGeneralMode`). A readback of a resource the submission being
  recorded has not used is copied in a command buffer sent alone (`SubmitAsideAndWait`): the CPU waits for what was
  sent, not for the frame being recorded. The swapchain image's acquire is waited for at the transfer stage of the
  blit into it.
- Caches: descriptor sets are kept over submissions by what they bind (`VulkanDeviceCaches.cs`; up to 16 pools, then
  the cache starts again); the sets that name a destroyed buffer, view, acceleration structure or set layout are
  forgotten (VMA hands a freed handle to the next object). A dynamic buffer rewritten while the GPU uses it moves to
  another storage; small ones (up to 64 KB) keep 16 spare storages (others 4), so they cycle through the same handles
  and the sets that name them stay cached. A program's loose uniforms are copied into frame memory only when a value
  changed. The `VkPipelineCache` is kept on disk (`VulkanPipelineCache.cs`:
  `pipelines.vkcache` in `ProgramBinaryCache.DriverFolder`, read with the first pipeline, written a second after
  pipelines stop coming and when a window's context goes).
- Timestamps (`VulkanDeviceTimestamps.cs`): AMD's driver spends 1-2 us in a host reset or a read of one query (per
  query, also when one call reads a whole pool), and the GPU profiler writes and reads dozens a frame. Queries given
  back are reset together when the free ones run out (a call per run of neighbours), and the GPU copies a submission's
  values into mapped memory at its end (`vkCmdCopyQueryPoolResults`), where a stamp reads its value once the
  submission has finished: a stamp written and read ~1.0 / 1.7 us -> 0.3 / 0.15 us (OpenGL 0.3 / 0.3).
- `TextureUsage.SliceImages`: an image made with `VK_IMAGE_CREATE_2D_VIEW_COMPATIBLE_BIT_EXT` is laid out slice by
  slice on AMD and samples ~2x slower (the GI's 3D textures). Only a 3D texture whose slices are bound as 2D images
  (`SetImage` with a layer) asks for it; both devices throw when a slice of one made without it is bound.
- `LEL_VULKAN_STATS=1` prints once a second the barriers, renderings ended in a pass, descriptor sets written and
  descriptor cache restarts per frame. TestGame's scenes: 21-81 barriers a frame (vct 195), 0-4 renderings ended for
  a barrier, 0-0.9 sets written, no cache restart.
- Verified (scratchpad tools of session 2a1d1c21: `vktest-final`, the device tests with the ray query ones and new
  ones for this: a texture through passes, samplers, copies, blits and clears; depth sampled while a pass tests
  against it; clears as load operations; uploads into an attachment of the open pass; dispatches writing what draws
  read; transfer chains with readbacks; layers and 3D slices; a slice bound without `SliceImages` refused): 573
  results, 567 equal to OpenGL's, 6 known, none different, no message of the validation layer with synchronization
  validation, also with `LEL_VULKAN_DISABLE=rayqueries`. TestGame's 18 scenes on Vulkan with `hwrt=1` and `hwrt=0`
  against the frames before (max 0-6 of 255, `vfx` not deterministic) without validation messages, synchronization
  validation on the GI and ray tracing scenes; OpenGL's check 0 different.
- Speed (780M, TestGame 1280 x 720, Debug builds, 600 frames hidden with a fixed step, the median of three
  interleaved rounds; `scratchpad/vkperf.ps1`): Vulkan's GPU time a frame before -> after (`hwrt=1`): lumen 7.78 ->
  6.89 ms, vct 14.90 -> 11.05, gi0 2.53 -> 1.96, materials 7.84 -> 7.08, outdoor 4.51 -> 3.79, nogpudriven 8.20 ->
  7.25, ssaa 4.26 -> 3.63, vfx 3.68 -> 3.05, impostors 4.11 -> 3.35, nav 1.18 -> 0.84 (OpenGL: 6.60, 9.16, 1.22,
  6.51, 3.51, 6.60, 1.81, 2.80, 3.16, 0.37); with `hwrt=0` lumen 8.74 -> 7.18, vct 15.09 -> 10.75. The device's
  pieces (`vktest bench`): a 2048² shadow map with 1024 quads 0.64 -> 0.14 ms (OpenGL 0.11), 100 dependent dispatches
  0.23 -> 0.17 (0.21), 20 small passes 0.076 -> 0.042 (0.015). The render thread: 0.04-0.17 ms less a frame with the
  timestamps' change, 0-0.17 ms over OpenGL's after it.
- Still slower than OpenGL on the GPU in every scene (0.2-0.6 ms in the GI scenes with ray tracing, up to 1.8 ms with
  super sampling). Most of it: this driver (AMD 2.0.353, 780M) never compresses or fast-clears colour attachments (a
  1280 x 720 RGBA8 clear 0.041 ms against OpenGL's 0.001, draws into it ~40 % slower), whatever the layouts, with
  only the attachment usage, in memory of its own, on a device made without any optional feature (scratch
  experiments); depth is compressed and fast-cleared. So GeometryPrepass (two RGBA16F targets cleared and drawn)
  takes 0.45-0.60 ms against OpenGL's 0.08-0.10 (1.16 against 0.18 with super sampling), PostProcess about twice
  OpenGL's, Shadows +0.3 ms (its RGBA32F transmittance target cleared each frame). VCT's bounces 1.53 against 1.14
  ms (cone tracing through 3D textures: the shaders as compiled for Vulkan or the textures' tiling, not looked into).
  The bench's CPU per draw: the same draw again 98 -> 161 ns (OpenGL 83), materials 263 -> 376 (353; the ordering's
  bookkeeping when the textures change), a uniform per draw 266 -> 124 (112).

## Voxel GI and distance fields on Vulkan: conservative coverage, ray-traced cones (2026-11-23, branch `vk-sdfvct`)

- The ask: VCT was the slowest scene on Vulkan (11.05 ms against OpenGL's 9.16): find where the time goes and fix it;
  conservative rasterization for the coverage voxelization where the device has it; VCT's distance field parts through
  the scene's top level where the device has ray queries (no distance fields then, as Lumen under ray tracing); the
  distance field build for when it is still needed; settings and switches.
- Conservative rasterization (API): `RenderState.ConservativeRasterization` (part of the pipeline on Vulkan: bit 32 of
  the pipeline key's state, `VkPipelineRasterizationConservativeStateCreateInfoEXT` overestimating, no extra size) and
  `DeviceCapabilities.ConservativeRasterization`. Vulkan: VK_EXT_conservative_rasterization where the GPU has it and
  overestimates by half a pixel at most (`VulkanDeviceConservative.cs`; `LEL_VULKAN_DISABLE=conservative` makes a
  device without it); a device without it (OpenGL always: its picture stays as it is) ignores the state.
- Coverage voxelization (`VoxelGIPass.VoxelizeCoverage`, `Engine/VoxelizeCoverage.shader` `LEL_VOXEL_CONSERVATIVE`):
  where `GlobalIlluminationSettings.ConservativeVoxelization` (default on; Project Settings > Lighting, the voxel cone
  tracing settings) and the device can, the pass has one sample and rasterizes conservatively: one fragment per voxel
  column a triangle touches, whose coverage is the exact area of the triangle inside the column (Green's theorem over
  the three edges clipped to the column, no loops or arrays), and whose depth, texture coordinates and normal are the
  triangle's at the column centre's clamped barycentric point (flat corners from the geometry stage: the centre of a
  touched column may lie outside the triangle). Elsewhere 8x multisampling as before (the covered samples / 8). The
  console's volume line says which (`conservative rasterization` / `8x coverage samples`); TestGame
  `voxconservative=0|1`.
- Ray-traced cones (`Lighting.VoxelRayTracingActive` = VCT runs, its trace mode is SDF Detail or SDF Cones, the
  distance field setting is on, and `Lighting.HardwareRayTracing`: the project's Hardware Ray Tracing, now read by both
  GI methods, and a device with ray queries). Then `GlobalIlluminationSettings.DistanceFieldActive` is false for VCT: no
  global distance field, no mesh distance fields (`LumenScene` collects its objects without fields: their own scale,
  mesh bounds in the table, `LumenScene.ReleaseDistanceFields`), and `GlobalDistanceFieldPass` only updates the scene
  (objects and top level, profiler section `GlobalSDF.rayTracing`). `Lighting.SceneRayTracingActive` (Lumen's or VCT's
  rays) is what builds the top level now (`LumenScene.UpdateRayTracing`, `Renderer.ReleaseUnusedGI`). The programs that
  trace (`GIResolve`, `VoxelBounceCells`, `VoxelInject`) have `LEL_RAY_QUERY` variants
  (`Engine/VoxelConeTracingCore.glsl` includes `Engine/SceneRayQuery.glsl` with them;
  `Lighting.SetVoxelRayTracingUniforms`): SDF Detail's near field is a
  ray (`TraceSceneRay`, the hit's triangle normal where the field's gradient was), an SDF cone's distance to the
  surfaces is the distance along its axis to the first triangle a ray along the axis meets (`SceneRayHitDistance`: one
  ray per cone, nothing read of the hit; the floor of each sample's opacity is the field's formula with that distance,
  the same band and coverage). The material shaders have no such variant: their per-fragment cones (transparent
  materials, cameras of a stack other than the lighting camera, `resolve=0`) are voxel cones then. The console:
  `[VoxelGI] Cones: SDF cones through the scene's triangles (...)` or `... through the distance fields (why)`.
- Where VCT's time went on Vulkan (780M, per pass against OpenGL, TestGame `gi=vct`): the bounce passes (+0.5 ms
  each), all of it the bounce cells' cone tracing of the anisotropic mips with the distance field's floor (iso mips +
  SDF: equal to OpenGL; aniso + voxel cones: +30 %; aniso with one slab read instead of up to three: equal): the code
  AMD's Vulkan compiler makes puts every conditional slab fetch in a branch of its own with its descriptor loaded again
  before it and a wait after it (VK_AMD_shader_info's disassembly; OpenGL's code cannot be looked at, the same shader
  runs faster there). Not the 3D textures: none
  asks for 2D views any more, and the device bench (`VKTEST_VOLUME=1`, cubes and the 384 x 64 x 64 atlas, screen-like
  and 4^3-block-like reads) samples them as fast as OpenGL. Wave32 for compute (subgroup size control; the driver picks
  wave64) gained 8 % on that bounce, nothing beyond the run-to-run noise elsewhere: not kept. SPIR-V optimized by
  shaderc (performance) changed the registers by one or two: not kept. Fixed in the engine: the slab fetches are
  issued first and weighted after (`SampleAnisoLevel`: the resolve and the transparent materials 2.47 -> 2.25 and
  1.75 -> 1.58 ms on Vulkan, OpenGL unchanged), the anisotropic mip shader's per-thread axis index (scratch memory on
  Vulkan) replaced by constant indices per axis, the coverage shader's per-fragment component index the same. The rest
  of the gap is the colour attachments' missing compression (Shadows +0.3, GeometryPrepass +0.4, filter and post +0.15
  ms: "The Vulkan device's ordering" above).
- `LEL_VULKAN_SHADER_INFO=1|folder` (`VulkanDeviceShaderInfo.cs`): with VK_AMD_shader_info, every pipeline made prints
  its stages' VGPRs, SGPRs, LDS and scratch bytes (compute: the work group), and with a folder writes each stage's
  disassembly there.
- The distance fields where they are still built (OpenGL, no ray queries, Hardware Ray Tracing off, Lumen until its
  rays stop needing them): the global field's compose (`Engine/SdfCompose.shader`, static rebuild, scrolled slabs,
  dynamic regions) skips an object for a texel when the distance to the object's field box is already no nearer than
  the texel's nearest so far (`SceneObjectDistance` is never below it outside the box): no atlas read, and the same
  field (a skipped object could not have lowered the texel's distance; both APIs). The mesh field build: subgroup folds
  on Vulkan and fewer flushes ("Mesh distance field atlas" above). What could run on an async compute queue (none of it
  reads the frame's raster work): the mesh field
  builds, the compose passes, the top level's build; of VCT the region clears and the block compaction (the injection
  needs the grid shadow maps, the bounce the injection, the resolve the geometry prepass).
- Tried and dropped: tracing the bounce by compacted lists of the cells with geometry (full work groups instead of a
  block's 16 of 64): slower on both APIs and both trace paths (Vulkan, block dispatch -> compacted: SDF cones 1.63 ->
  2.63 ms a bounce, ray traced 0.82 -> 1.06): a compacted group mixes cells of several blocks and surfaces, every slab
  branch and every cone length is taken. Counting the ray's hit as a thin surface the cone passes (`|hit - distance|`):
  more light through walls (the day room +0.6 against +0.24 of 255 on average), not kept.
- Measured (780M, TestGame 1280 x 720 Debug, 900 frames hidden with a fixed step, the base build and this one run in
  turns three times; other sessions' engine processes shared the GPU, so each run counts its least disturbed second
  of the third on: scratchpad `sdfvct/passperf.ps1 -Aggregate Min`), GPU ms a frame, Vulkan before -> after (OpenGL
  after): vct 11.28 -> 9.15 (9.23), voxcoverage=0 10.70 -> 8.83 (8.79), voxmips=iso 7.28 -> 6.62 (6.64), voxmips=sh
  11.93 -> 10.78 (11.11), trace=voxel 7.36 -> 7.07 (5.97), trace=sdf 7.02 -> 7.25 (5.79), resolve=0 17.27 -> 9.38
  (15.45), bounce=0 7.59 -> 6.94 (6.54), static=0 12.19 -> 10.13 (9.97), hwrt=0 (the distance fields) 11.55 -> 11.27
  (9.62); lumen 6.79 -> 6.74, lumen hwrt=0 7.16 -> 7.05, lumen hwrt=0 animate=1 7.38 -> 7.37 (OpenGL unchanged in all,
  within 0.1-0.3). By pass (vct, Vulkan): a bounce 1.64 -> 0.83 (rays instead of the field's samples), GIResolve 2.30
  -> 2.70 (seven rays a pixel), Transparent 1.68 -> 0.72 (voxel cones in the materials), mips 0.34 -> 0.33; the field
  path (hwrt=0): GIResolve 2.34 -> 2.16, Transparent 1.74 -> 1.63, the bounce unchanged (1.64 -> 1.68, OpenGL 1.21).
  The coverage voxelization of everything every frame (static=0, `VoxelGI.dynamic`): 0.53 -> 0.51 ms (OpenGL 0.44);
  the same build with `voxconservative=0` 0.52, and the static voxelization every frame (`hwrt=0 staticrebuild=1`,
  `VoxelGI.static` with its clears) 2.75 multisampled against 2.79 conservative: the same cost. The static field's
  compose every frame (`staticrebuild=1`, `GlobalSDF.static`, against this build with the compose and build shaders of
  the commit before): lumen hwrt=0 1.20 -> 0.38 ms on Vulkan (OpenGL 0.42 -> 0.40), vct hwrt=0 1.16 -> 0.38 (0.47 ->
  0.43). The dynamic objects' compose (lumen hwrt=0 animate=1, `GlobalSDF.dynamic`) 0.09 -> 0.06 (OpenGL 0.07 ->
  0.05). The first second's field builds and compose (`GlobalSDF`, load): lumen hwrt=0 2.0-2.5 -> 0.10-0.18, vct
  hwrt=0 3.2-4.8 -> 0.02-0.09 (OpenGL 4.2-5.4 before and after: its flushes leave no gaps in the GPU's time; Vulkan's
  section had the GPU waiting between the per-chunk submissions). VRAM: VCT under ray tracing
  builds no global field (128^3 x 2 x (R16F + R16UI): 16 MB) and no mesh field atlas (8 MB at least), and adds the top
  level and bottom levels (0.01-0.02 MB in TestGame); conservative rasterization changes no memory (the accumulators
  are the same, the pass has no attachments).
- What the pictures do (frame 400, against the Vulkan frames of the commit before, of 255): conservative coverage alone
  (`hwrt=0`, `trace=voxel`): max 11-12, mean 0.06-0.08, nothing over 8 (slightly darker walls, brighter boxes: exact
  coverage instead of eighths); against OpenGL unchanged (OpenGL's driver ignores the multisampling, a covered column
  counts an eighth: max 105, mean 13.0 before and after). The ray-traced cones: vct max 50, mean 0.79 (2.5 % over 8),
  brighter along the room's edges and corners (a cone running beside a wall no longer counts it: the field did), a
  little darker on the floor under the objects; trace=sdf max 67, mean 0.87; the emitter-lit night room 3.0 darker on
  average (max 108: the field also counted the emitters a cone passed close by); materials=1 max 207, mean 1.3 (the
  reflective and transparent balls: their reflections' cones and the glass's own cones, voxel cones in the
  materials); resolve=0 max 44, mean 3.9 (every material traces voxel cones). OpenGL: every VCT variant and Lumen
  within max 1 of the frames before (their own run-to-run noise).
- Verified: the device tests (scratchpad `sdfvct/vktest`, vktest-final with `TestsConservative.cs` and the volume
  bench): 577 results, 571 equal to OpenGL's, the 6 known, none different, no message of the validation layer with
  synchronization validation; `conservative` (three triangles and two slivers in a pass without attachments, the
  fragments counted per pixel against the pixels worked out on the CPU: centres without it, every touched pixel with
  it; the state on and off in one frame) the same with `LEL_VULKAN_DISABLE=conservative`; every engine shader variant
  builds on both APIs (183 programs). TestGame on Vulkan with validation and synchronization validation: the 13 VCT
  variants, lumen, lumen hwrt=0, vct and lumen animate=1, vct staticrebuild=1, vct voxconservative=0, no message of
  core validation and none of what this work touched; one run of them had none at all, a second (the GPU shared with
  other sessions' runs) one or two SYNC-HAZARD-WRITE-AFTER-READ in about half the scenes: the back buffer's layout
  transition at the start of a frame after the present's blit read it (the blit not in the barrier's source), which
  the build of the commit before shows the same way (gi=0 too). OpenGL's check (`check.ps1`): 0 different. Not
  checked: the settings in the editor by eye (Hardware Ray Tracing now shown with VCT, Conservative Voxelization), a
  GPU without ray queries or without conservative rasterization (only the switches above), Linux.
- Left: VCT's bounce through the distance field on Vulkan (+0.45 ms each against OpenGL: the driver's code for the
  conditional slab fetches in compute; the ray-traced path does not have it); the ray-traced cones' quality (one ray
  per cone sees only what lies on the axis: more rays per cone, or the hit's plane, would bring the field's soft
  occlusion back at their cost); the material shaders' per-fragment cones under ray tracing (a ray query variant of the
  materials); what still reads the distance fields under VCT with ray tracing has none (VFX collisions, the SDF debug
  view); the GIResolve's rays (+0.4 ms) could stop where the field's band no longer counts.

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
- Mesh assets (2026-11-19): `ModelAssets.MeshExtension` ".mesh" is a model file holding imported data (`LELMODEL`),
  one node and one mesh; see "Mesh Baker, MeshCombiner, mesh assets" below.

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
- `Game.Editor.dll` loads into the same collectible `GameLoadContext` as `Game.dll`. Everything holding a type of it must
  be dropped in `EditorExtensions.OnDomainUnloading` (called from `EditorApplication.ClearTypeCaches`).
- Editor script components (2026-11-19, the user's ask for a generator that is "editor only": "there is the editor
  scripts' assembly: make it there, and make the build simply ignore such components in the scene"). A `Behaviour` in an
  Editor folder is a component like any other in the editor: `ScriptDomain.LoadDomain` registers `Game.Editor.dll` in
  `TypeRegistry` (unregistered in `UnloadDomain`; before, it was left out on purpose: "components there could not exist
  in a player"), so Add Component lists it, scenes and prefabs store it ("Type, Game.Editor") with its serializable
  classes, and it survives saving, reopening and script reloads. Without the assembly (its build failed) it loads as a
  `MissingComponent` with its data kept, as any missing script. Game builds leave such components out
  (`Building/EditorScriptComponents.Strip`, on documents as generic data, by the assembly name in the stored type):
  `GameBuilder.ConvertDocument` strips every scene and prefab it writes (log: "Scenes\Main.scene: 1 editor script
  component left out"; those on objects and those added to prefab instances), and `BuildDependencies.AddDependencies`
  strips before it collects types and references, so `CheckTypes` no longer reports them and what only they reference is
  not shipped. Not stripped: an instance's modifications of an editor component inside a prefab (they find no target and
  do nothing), editor types stored elsewhere (a ScriptableObject asset, a game component's polymorphic field: reported
  by `CheckTypes` as before). In the editor's play mode the component is there (it is the editor).
  Verified with scratchpad `edonly/test.ps1` of session 0ec30c68 (scratch project: an editor component with a list of a
  serializable class, a prefab reference and a scene object reference, on a scene object and inside a prefab): data kept
  over save + reopen and over a script reload; batch `-build` left one out of the scene and one out of the prefab, no
  built file holds the type's name, no Game.Editor.dll in the build, the player ran 40 frames and exited 0 with a clean
  log. `GameLoadContext` must
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
  SHA-256 of every .cs under Assets with its size and modification time, and since 2026-11-23 of the module version ids
  of the engine, editor and test framework assemblies (a game built against another engine threw MissingMethodException
  where the engine's API had changed: New Frontier's tests after the move to the graphics device), taken when the build
  starts), and the build is
  current while the scripts give the same value and the generated projects are older than it (2026-10-25). Before, only
  the newest script time counted, so a deleted, renamed or moved script left the old types loaded ("Scripts are up to
  date"). A build without the record (older engines, an IDE build) is built again once. Running editors also rebuild
  on the file watcher's events; `dotnet build` itself drops deleted files (the SDK hashes its Compile items).
- Editor start with scripts newer than their build (2026-11-09, the user's report: components added since the last run
  logged as missing): they are built before the scene opens (`ScriptDomain.BeginStartup` starts the build,
  `StartupBuildRunning`, `FinishStartup` loads the result; log "compiling them before the scene opens"), so the scene
  opens with the new types. Before, the old Game.dll loaded, the scene read the new components as missing, and the
  build's reload followed. The editor's window keeps drawing meanwhile (see "Editor start"); batch mode waits
  (`Initialize`, `WaitForStartupBuild`). A failed build loads the old assembly as before (errors in the console, no
  second build until an edit).
- Windows with an `InspectorContext` of their own (Timeline, Animation, Input Manager) release it in
  `OnDomainUnloading`: the context keeps the scene it last drew with, and a Timeline opened and closed once held the
  unloaded scene, and with it the old scripts, through every reload ("still referenced ... timelineWindow.context.Scene").
  Reproduced and verified with scratchpad `storm/timelineleak.ps1` (CLI `open Timeline`, `close Timeline`, then a reload).
- Screenshots' own renderers (`WindowCapture`: a Scene / Game view captured at a size renders with a renderer of its own)
  are released at a reload (`EditorApplication.ClearTypeCaches` -> `WindowCapture.ReleaseViews`, 2026-11-09): their
  `LumenScene.lastMaterials` kept the unloaded scene's materials, and with them the old scripts. Verified: a sized
  `screenshot Scene` then play -> "Previous script assembly unloaded".
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
- The GC's own roots (2026-11-09, the user's ask after the tracer kept finding nothing: "make the leak's debug info tell
  you more"): `LeakRoots` (`LELEngine.Editor/Scripting`, package `Microsoft.Diagnostics.Runtime` = ClrMD 4.2, the editor
  only, approved by the user) takes a snapshot of the editor's own process the moment the unload check gives up
  (`DataTarget.CreateSnapshotAndAttach`, PssCaptureSnapshot: ~0.3 s with no frame drawn, the copy then read on the worker
  thread; the main thread's trace waits for the snapshot, as its walk would hold the old objects on the main stack in the
  copy) and searches it with ClrMD's `GCRoot` from every root to the objects of the old scripts' types (by their loader
  allocator handle, so List<OldType> and OldType[] count) and to the old loader allocator object (held by their `Type`s,
  methods, delegates). Second warning, one line per place: `stack of thread N (started in X), in Y.Method`, strong / pinned
  / ref-counted (COM) / dependent handles, the finalizer queue, or a static field named from the slot of the runtime's
  statics array (`StaticNames`: `ClrStaticField.GetAddress` of every loaded type), e.g. `static LightBuffer.giLights ->
  Dictionary<Light, LightBuffer+GIState>._entries -> ...Entry[][5] -> Light on 'Flash 6'.gameObject -> ... -> SpaceStorm
  on 'Space Storm'`. Left out: weak handles (ClrMD lists kinds it does not know, the runtime's weak interior pointers
  (kind 10), as roots) and roots the old loader allocator holds itself (the old scripts' own statics). Then the check goes
  on (GC passes every 250 ms for 10 s, every 2 s for 40 more) and logs `unloaded after all, N s after the reload` when
  the hold was only for a while. ~1.6 s for the search on New Frontier.
- Found with it (2026-11-09): `LightBuffer` kept the last frames' lights in its static GI tracking (`giLights` /
  `giSeen`, swapped each frame) and `shining`, so a light lit in the edit scene held that scene and the old scripts after
  a reload until two frames rendered in play mode, which took over the 4 s check (New Frontier's storm preview: the
  flash lights). `Light.OnDisable` / `OnDestroy` now call `LightBuffer.Forget` (out of the lists at once, the reach kept in
  `giGone` for the next frame's GI change), and `candidates` is cleared after use. Verified with scratchpad
  `storm/selectleak.ps1` (`-AllFlashes`, `-SetTime 0.754693`, `-Click -Button 'Next Strike'`: all three leaked, now
  "unloaded (4-5 GC passes)"); the report itself checked by switching the fix off for one run.
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
  elements (`GetElements`, `Click`, `DoubleClick`, `OpenContextMenu`, `Input`, `Scroll`, `Key`, `TypeText`), actions
  (`GetActions`, `RunAction`, `ReserializeAllAssets`), scene and objects (`OpenScene` / `NewScene` with `UnsavedChanges`,
  `SaveScene(path)`, `FindObject` "A/B" / name / "#id",
  `Select`, `SelectAsset`, `DeleteAsset` (CLI `delete-asset`), `DescribeHierarchy`, `DescribeObject`, `GetField`, `SetField`, `Reparent`, `Rename`,
  `AddComponent`), `Play` / `Stop` / `Pause` / `Step` / `Undo` / `Redo` / `RefreshAssets`, console, waits
  (`NextFrame`, `WaitUntil`, `WaitForIdle`). Everything runs on the main thread between frames (`AutomationContext`,
  pumped by `EditorAutomation.Pump` from `MainWindow.OnUpdateFrame`); async code continues there.
- CLI: every editor (GUI or batch with `-listen`) serves a named pipe per project (`LELEngine.Editor.` + 16 hex of the
  SHA-256 of the lowercased project path; owner and ACL = the user, the client checks the owner). `LELEngine.Editor.Console
  [-projectPath P] -command <words...>` (everything after `-command` is the command), `-script file`, `-json`; exit 0 /
  1 failed / 2 no editor. `-command help` lists the commands (`AutomationCommands`). Screenshots default to
  `<project>/Library/Screenshots/`. Batch: `-batchmode -projectPath P -listen` serves until `quit [--save|--discard]`;
  `-executeMethod Type.Method` may return void / bool / int / Task / Task<T> (`string[] args` optional).
- Commands are actions, not UI (2026-11-19, the user, after a `menu` + `click "button:Reserialize"` lost its question
  twice: "it has to fire the ACTION, not the menu"; "everything has to fire like that"). The main menu's items are
  `EditorAction`s (`EditorActions.cs`; registered in `EditorApplication.Actions.cs`: path = menu path, `Run` the action,
  `Click` the menu's own click when it asks first, label / shortcut / enabled / ticked / separator). The menu bar draws them
  (`EditorActions.DrawMenu`; the hierarchy's Create Child draws the GameObject ones with `createParent`), automation runs
  `Run` (`EditorApplication.RunAction`; an editor script's [MenuItem] method is called at once): CLI `action <path>` /
  `actions [filter]` (`menu` / `menus` the same), `reserialize` (answers with the summary when the run ended). The menu
  pass that ran `DrawMenus` in a hidden ImGui context (`EditorMenu.List` / `Execute`) is gone; `EditorMenu` only draws.
  An action asks nothing and opens no window: what needs the user's word throws `InvalidOperationException` with the
  reason (the command fails with it): unsaved scene / prefab / tool documents for `scene new` / `scene open` / `quit` /
  File > New Scene / Close Project / Exit unless `--save` / `--discard` (`UnsavedChanges`, `SettleUnsaved`: checked before
  anything changes; prefab stages are left either way), Save Scene As (`scene save <path>`), `play` with a prefab that
  has unsaved changes and no Auto Save, a disabled action. Only commands acting on windows touch the UI (click, input,
  key, type, context, scroll, focus, screenshot, elements).
- One command at a time (`AutomationServer.RunQueue`, a channel of every connection's commands in arrival order): the
  next starts when the one before has answered; before, connections ran in parallel and interleaved at their awaits. And
  commands start only `EditorAutomation.SettleFrames` (8) ticks after a project's load: in its first frames the windows
  restore and the editor drops the focus they took by appearing (`startupFocusFrames`: `ImGui.SetWindowFocus(null)`, which
  closes ImGui's popups, so a question opened then vanished: the 'button:... is not shown any more (its window is closed)'
  failure). Verified with scratchpad `autorel/stress.ps1` of session 0ec30c68: 8 fresh starts with the action as the very
  first command, 8 ran; a `status` sent 0.3 s into another client's `wait 120` answered after it; 20 clients at once all
  answered; actions, their refusals and `--save` / `--discard`; the File menu and the hierarchy's Create Child still drawn
  and clickable; batch `-listen` the same.
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
  one undo step per input. A slider is two elements: `input:Path` (the slider) and `input:Path/value` (its number
  field, see "Sliders" below): `click` the field, `type`, `key Enter` types as a person would. Combos: click, then the option's element. Verified 2026-10-26 on a scratch `Mover`
  component (float, int, string, bool, Vector3, Color4, enum, nested class, List, Dictionary, Transform): 10 inputs,
  values in the inspector, 10 undos back to the start.
- Editor scripts: `EditorGUILayout.Button / Toggle / TextField / Foldout / Popup / Tabs` (and `Field` /
  `PropertyField`) are elements; a raw `ImGui.Button` in a script window is not (by design: no hook into raw ImGui).
  `Popup(label, index, options)` (2026-11-12) is a drop-down after the label column: `combo:Label`, its options
  `item:Label/Option` while open (options may repeat: each is drawn under its own index id). `Tabs(id, index, labels)`
  is a tab strip (`tab:Label`) whose index the caller keeps (a serialized field, so a window opens on its last tab):
  ImGui owns which tab shows, so the strip is told (`SetSelected`) when the caller's index is not the tab it showed
  last, and when its ImGui state is new: not drawn the frame before in this context (first shown, behind another
  dock tab, or drawn in a screenshot's layout context), where ImGui would start on the first tab and hand index 0
  back. Verified in a script window with three tabs and ~20 drop-downs: CLI `click combo:X` + `click item:X/Y`,
  `click tab:Y`, layout screenshots in batch on the asked tab, the index kept over a script reload.
- Verify recipe (scratchpad `AutoProbe` of 2026-10-26: `ProbeWindow` + `AutoTest.Run`): GUI editor `project=P focus=0`,
  then the CLI: `windows`, `elements "Probe Window"`, `click "button:Press me"`, `input "input:Note" text`,
  `click hierarchy:Sphere` + `status`, `click "checkbox:Cast Shadows"` + `get`, `undo`, screenshots of covered
  windows; batch `-executeMethod AutoTest.Run` and `-listen` + `quit`. Start / stop test editors from the PowerShell
  tool (a Bash-run `Start-Process` hangs on inherited handles).

## Play mode: the keyboard is the game's (2026-11-19)

- The user's ask: in play mode the editor takes no input that can collide with the game ("Tab, for example"); the only
  input it takes then is play management (the play and pause shortcuts). `EditorApplication.GameHasKeyboard` = play mode
  (playing or paused) with the Game view focused (`GameViewPanel.HasInputFocus`). Then the main window's ImGui context
  gets no key presses and no typed text (`ImGuiController.KeyPressFilter` = `UIReceivesKeyPress`, `TextInputFilter`)
  but Ctrl+P and Ctrl+Shift+P (`IsPlayShortcut`: P with Control, without Alt / Super), which `HandleShortcuts` runs in
  the next frame as before; the game does not get those two presses (`OnWindowKeyDown`). Releases always reach ImGui
  (a key let go while held back never stays down there); the game still gets keys only while playing, as before.
- Why at ImGui's input and not in each shortcut: everything in the editor reads keys through ImGui (no window reads
  `IsKeyDown` itself), and ImGui does things of its own with them: `NavEnableKeyboard` makes Tab / the arrows move a
  highlight over the Game window's own widgets and Space / Enter press them (Tab, then Space, would toggle Maximize On
  Play), Ctrl+Tab switches windows (the Game view would lose its focus, and the game its input), Alt alone toggles the
  menu layer (ImGui's behavior read from its rules, not reproduced before the fix);
  and `HandleShortcuts` (Ctrl+S / Z / Y / D / N, Ctrl+Shift+B) and the windows' own keys run regardless of focus. A
  blocked press is held back whole, its modifiers too. Deciding per event in one place also keeps it independent of the
  order of the window's event handlers (the controller subscribes before the editor): a shortcut run inside the event
  would stop play before the controller asks, and the controller would then pass Ctrl+P on and start play again.
- Automation is untouched: CLI `key` / `type` in play mode go to the game (`GameKeyboard`), `--editor` / `--window`
  and the test hooks feed ImGui directly (`io.AddKeyEvent`), past the filter. Another window clicked during play takes
  the keyboard back for the editor (the inspector, the hierarchy's Delete, ...); the other editor windows (Audio
  Studio, UI Builder, launcher) have contexts of their own without a filter.
- The mouse: while the game plays, an `InvisibleButton` ("##game", left / right / middle) lies over the Game view's
  image, so a press there belongs to an item. An `Image` has no id: to ImGui a press on it was one on the window's
  empty space, which starts moving the window, and dragging (firing while aiming) moved a floating Game window or dock
  node. `IsMouseOverImage` is read from that button (the image's `IsItemHovered` turns false while another item is
  active, which would have stopped the wheel reaching the game during a held press). A grabbed cursor already sent ImGui
  no positions (`ImGuiController.OnMouseMove`).
- Verified with `dotnet build` only (the user's rule). To check by hand: play, click the Game view, press Tab, the
  arrows, Space, Escape, Alt, Ctrl+Tab, Ctrl+S / Z / D: the game reacts, the editor does nothing (no highlight on Maximize
  On Play, the focus stays, nothing saved, undone or duplicated); Ctrl+Shift+P pauses and resumes and Ctrl+P stops from
  the Game view; click the inspector during play and typing works there; undock the Game window and drag over its image
  while playing: it stays in place.

## Editor start: the windows first, the project over frames (2026-11-09)

- The user's ask: the editor's own windows show first, as they will be but without what is not loaded yet (an empty
  hierarchy, a scene view that renders nothing), and the heavy loading comes after. Before, `OpenProject` ran whole inside
  `MainWindow.OnLoad` (the editor's constructor), so the window stayed blank until the scene was open. A first version
  drew stand-ins (empty windows with the step in their middle) and the user corrected it: the real windows, empty.
- `EditorApplication.OpenProject` reads the project's files and runs the first step at once (errors there are the
  caller's, as before), then `OpenProjectSteps` (an iterator; each `yield return` names the next step) advances one step
  per `Update` once `LoadingFramesFirst` (3) loading frames were drawn: reading the assets, importing models, the audio
  project, the views (renderers, the scene camera, file watchers), the scripts (a stale build: one frame per poll of
  `Scripts.StartupBuildRunning` while it compiles in its own process), the scene. ImGui docks windows from the layout in
  the next frame's NewFrame: frame 1 shows the menu bar and the empty dock space only, frame 2 the windows (measured). The
  frame the load ends in goes on as any other (`RestoreWindowStates` opens the real windows before it draws). An error
  in a later step closes the project and shows the chooser (automated runs end). Batch mode runs every step at once.
- While it loads (`IsLoadingProject`, `LoadingStatus`) `DrawUI` draws the editor as always: the menu bar (menus and play
  controls disabled, "Project: step... (N s)" as its status), the dock space, and every panel through its own `Draw`. The
  first step sets the built-in windows' open flags from windows.json (`PrepareLoadingWindows`: the flags
  `RestoreWindowStates` sets, which after the load also gives the document windows their documents). Each panel shows what
  is there: the hierarchy and the lighting panel nothing (no "No scene" while loading), the scene view its toolbar over
  an empty view (no image, camera input or picking: its last image may be another project's; an `ImGui.Dummy` takes the
  view's place, as ImGui asserts on a window grown by `SetCursorPos` alone), the game view nothing, the inspector "Nothing
  selected", the Project panel, Project Settings and the Input Manager their content once `AssetsReady` (the asset
  database still holds the previous project before), the document windows their empty state. Every window but the
  console takes no input (`BeginLoadingInputLock`: ImGui's disabled state, which windows begun inside it inherit, with
  `DisabledAlpha` 1 so they look as they will); the console is live (the load's messages). The scripted windows of
  `Library/EditorWindows.txt` cannot draw before their types load: empty windows under their ImGui names
  (`EditorExtensions.SavedWindowNames`, "Probe Window###ProbeWindow") stand where the layout docks them until the real
  ones take over the same ImGui windows. `startupFocusFrames` covers the first frames. Nothing renders the views, automation
  waits (`EditorAutomation.Pump` returns, `IsBusy` true), `SaveEditorState` writes nothing until `projectReady` (a closed
  or failed load must not replace the user's camera, windows or layout: verified, closing during the compile left them
  byte-identical).
- Logs: `[Editor] First frame drawn N ms after the process started (window and GL context at N ms, editor made at N ms)`,
  `[Editor] Project P loaded in N ms, its windows up meanwhile (step N ms, ...)`. New Frontier (Debug, 780M): first
  frame with the windows ~1.3 s (the window and GL context alone ~1.0 s), then assets ~0.3 s, scripts 0.07 s (current) or
  ~5-6 s (compiling, the windows drawing), the scene ~1.3-2 s (still one blocking step: the window shows "Opening
  Scenes/Flight.scene" meanwhile).
- Asset index beside the first frames (2026-11-19, the user's report: "Reading the assets 8595 ms" on a start right after
  `Editor.cmd` rebuilt the editor). `OpenProject` runs in the editor's constructor, before the first frame, and the load's
  first step waited for `LoadingFramesFirst` frames: the step's time held the window's first frames (ImGui's start, every
  window drawn the first time, the JIT of a fresh build) and the read. The wait is now its own entry ("First frames"); the
  index (`AssetDatabase.Initialize`, missing metas made) is read by `Task.Run` from the first step while those frames
  draw, the "Reading the assets" step waits for what is left of it (`WaitForAssetIndex`; `EndProjectLoad` waits too, so a
  load closed meanwhile cannot finish its index after the next project's), and the summary ends with "asset index read in
  N ms on a worker thread". `AssetDatabase.Rebuild` reads the meta files with `Parallel.For` and registers them in the
  listing's order (the same duplicate-GUID warning). The I/O measured warm on New Frontier (2,859 metas, 2 MB JSON, 357
  folders): ~260 ms in one thread. Built only; not measured in a running editor yet.
- The scripts' startup build starts in that first step too (the user's ask, same day): `ScriptDomain.BeginStartup` is split
  into `StartStartupBuild` (any thread: IDE project, watcher, the build when the scripts are stale; reads only the
  project's files, the generated projects go to the project's root) and `LoadCurrentAssemblies` (main thread, no build);
  `BeginStartup` = both (batch `Initialize`). The editor runs `StartStartupBuild` by `Task.Run` beside the asset index and
  keeps the domain in `startupScripts` until the scripts' step hands it to `Scripts` (40-odd places read `Scripts`), so
  the build's process compiles while the first frames, assets, models, audio and views load; "Compiling scripts" is only
  what is left of it and the summary adds "scripts compiled in N ms beside the load" (`ScriptDomain.LastBuildMilliseconds`).
  A load closed before that step drops the domain (`DropUnloaded`: watcher off, the build runs to its end unread, no
  unload and no `ClearTypeCaches`). Built only; not measured in a running editor yet.
- Frames of automated runs count from the end of the load: `screenshotframe=`, `exitframe=` (`MainWindow.frame`) and the
  test hooks (`TestHooks.Update` runs after it), so frame N means the scene's Nth frame as before. `loadingshots=folder`
  writes per step the frame shown while it runs (`folder/loadNN/frame_000.bmp`, `[Test] Loading frame NN: step`).
  Scratchpad `storm/startload.ps1` (`-Stale` adds a probe script so the build runs), `storm/closeload.ps1` (closes during
  the compile, compares the state files), `storm/winprobe.ps1` (scratch project with a scripted window saved open and
  every built-in window open through `windowsfile=`: all of them draw while loading without an assert, the scripted
  window's stand-in, then the real windows in their places).

## Frame pacing and vsync (2026-11-12)

- A game reported "stuttering". Measured (a probe recording every frame's wall interval, `Time.deltaTime`, GC, and how
  far the camera moved; scratchpad `stutter/` of session 7fcb5476): each frame advanced the game by the time of the
  frame before, measured when the update started, so with the CPU's noise in it. Under vsync at 60 Hz (the editor) the
  display showed frames evenly while `deltaTime` went from 13.6 to 20.2 ms: a camera at 78 m/s moved 0.9 m in one
  frame and 1.8 m in the next (camera step vs its median: p99 27 %, max 37 %). The player had vsync off: 160 fps on a
  60 Hz display, so the frames the display picked were 12 to 19 ms of game time apart. No GC or allocation spikes.
- `Window.NextDeltaTime` (engine): the interval between the last two presents (`MarkPresented` after the deferred
  `SwapBuffers`; under vsync it returns at the refresh) made even by `FramePacing` (`Mono/FramePacing.cs`): the median
  of the last 8 intervals, snapped to a whole number of refresh periods under vsync when that median is within 6 % of
  one (the refresh rate from GLFW's video mode of the monitor under the window's middle, looked at every 120 frames);
  the time this takes away is owed and paid back at 10 % a frame, at most a tenth of the step, and only past half a
  step (the measurements' own noise stays unpaid: a present 23.4 ms then the next 13.4 ms at 60 Hz). The first 8
  frames (start, after a stall) and stalls over 0.25 s pass as measured. `Window.PaceFrames = false` gives the old
  measured `e.Time`. `Present()` (the splash screen) is no frame of the loop and records no present.
- First version snapped each interval on its own (20 % tolerance): where the swap does not wait for the refresh (this
  machine's remote desktop virtual display, frames of 36 ms) it snapped 36 to 33.3 and the paid-back debt made the
  steps uneven again; then a mean of 8 (one loading frame raised 8 steps). Hence median + snap of the median.
- The editor's play mode ticks the scenes with `Time.deltaTime` (it passed the raw `e.Time`, unscaled, while scripts
  read the scaled `Time.deltaTime`).
- `GraphicsSettings.VSync` (Project Settings > Graphics > Display, default on): the player sets `host.VSync` from it
  before its loop (`[Player] VSync on` in Player.log); `vsync=0|1` on the player's command line overrides. The editor
  always had vsync on.
- Results (780M, 60 Hz virtual display): editor `deltaTime` p1..p99 16.62..16.80 ms, camera step max 3.4 %; built
  game, vsync on: `deltaTime` 16.667 every frame after the start, camera step max 2.2 % (the plane's own speed
  change); vsync off: 5.8..6.9 ms, max 17 %. Tests: `FramePacingTests` in the game project (noisy vsync, a missed
  refresh caught up without a step over 1.1 periods, 36 ms frames not forced onto 33.3, no vsync, 60 -> 30 fps, a
  stall). Seen once, not explained: bursts of 40..110 ms frames in the editor with GPU and swap normal (the CPU, other
  processes on the machine?); a second run had none. A probe for that: a thread sleeping 1 ms and recording its
  oversleep, which tells a machine-wide stall from the game's.

## Views render on demand: Always Refresh, Application.RequestRepaint (2026-11-21)

- The user's report: with Always Refresh off, the editor on a game's scene still took 30 % of the GPU all the time.
  Cause: `EditorApplication.RenderViews` rendered both views every frame whenever the scene held an enabled
  [ExecuteAlways] component of the game's scripts (`EditModeScriptsRunning`), an effect that simulated
  (`VfxRuntime.AnyActive`) or a UI panel with transitions or scheduled callbacks (`UIRuntime.AnyPanelAnimating`): guesses
  that such things move on screen. One edit-mode component standing still kept the scene and the game view rendering for
  ever, against what the toggle's own tooltip said.
- Now the views render every frame only in play mode, with Always Refresh, while a cloud is being made or settling
  (`VolumetricRuntime.NeedsRefresh`), and in frames that were asked for; otherwise on changes (`ViewRefresh`: camera,
  size, `SceneVersion`, then the settle frames) and they keep their last image. The three guesses are gone
  (`AnyActive` / `AnyPanelAnimating` stay as plain facts for whoever wants them).
- Asking: `Application.RequestRepaint(frames = 1)` (engine, so a game's scripts can call it; needs no check around it:
  where every frame is drawn anyway it changes nothing). A request counts as a continuous frame, so the views settle for
  `CameraSettleFrames` after the last one. The editor takes one request per frame (`TakeRepaintRequest`, internal).
  Who asks:
  - a script that plays something in edit mode (an [ExecuteAlways] component's preview): in every frame it moves, and
    no longer when it stands still. What only moves by itself (effects, animated materials, UI transitions, a script
    that never asks) moves on screen with Always Refresh, as in Unity;
  - the effect whose inspector shows (`VisualEffectInspector`) and the effects of the graph open in the VFX Graph
    window, while they simulate: the one being worked on is seen running without Always Refresh;
  - the user's input (`ImGuiController`: a key, text, a mouse button down or up, the wheel: 30 frames; a button held
    while the mouse moves: as long as it goes). What the user does may change what the views show through code that
    tells nobody (an editor script's button calling into a component); moving the mouse alone draws nothing;
  - automation: every command but the reading ones (help, status, windows, screenshot, elements, actions, hierarchy,
    inspect, get, console, wait, refresh, quit) ends with `NotifySceneChanged` (`AutomationCommands`): what `exec` or
    an action did shows, and a polling `status` leaves an idle editor idle.
- Measured on a game's scene with a volumetric cloud, voxel GI and an edit-mode weather component (780M, the editor's
  default layout, GPU share of the editor's process from `\GPU Engine(pid_N_*engtype_3D)\Utilization Percentage`,
  1 s samples; scratchpad `refresh/measure*.ps1` of session 0ec30c68): Always Refresh off 77 % before, 7-8 % now (the
  UI itself, drawn every frame; CPU 45 -> 23 % of a core); on: 64 %. Off, by the samples: a click (WM_LBUTTONDOWN / UP
  posted to the editor's own window) 3 s of rendering, an object added by `exec` 6 s, an edit-mode preview of pieces
  flying apart for as long as they moved (14 s), a preview's reset 6 s, each back to 7 % after.
- Gotchas: the frame trace's `gpu_ms_lagging` / `gpu_heaviest_pass` keep their last values while no view renders (timer
  queries of the last rendered frame): they do not show an idle editor, the GPU counter does. `cli` is PowerShell's
  alias of Clear-Item: a helper function of that name never runs.
- Not checked: other games' edit-mode scripts (any that animate in edit mode without asking now stand still unless
  Always Refresh is on); effects and UI transitions were not looked at on screen after the change, only the rule; a real
  key press (the click went through the window's message queue, keys only by reading the handlers).

## Super sampling anti-aliasing (2026-11-17)

- The user's ask: SSAA, optimal, with settings, designed around NVIDIA's DSR / DLDSR factors (my reading of "their
  hardware supersampling with its multipliers": DSR is a driver feature with no API for a game, DLAA / DLSS have no
  OpenGL SDK). `SuperSamplingSettings` (`Rendering/SuperSampling.cs`, in `GraphicsSettings.SuperSampling`, Project
  Settings > Graphics > Anti-aliasing; `QualitySettings.superSampling` / `superSamplingFilter` /
  `superSamplingSmoothness` / `superSamplingFactors` at run time): `Factor` = the pixel count against the window's, DSR's
  list (1, 1.20, 1.50, 1.78, 2.00, 2.25, 3.00, 4.00; 1.78 taken as 16/9 = 4/3 per axis), `Filter` Gaussian (DSR's,
  `Smoothness` 0..1, default 0.33 as DSR's 33 %: sigma = 0.25 + 0.5 x smoothness window pixels) / Area (exact footprint
  average) / Lanczos (Lanczos-2, the crisp DLDSR-like look). The combo labels say what a factor makes of 1080p.
- `Renderer`: `Width` / `Height` stay the output's (what `Resize` sets: hosts, dumps, `BindOutput`, the camera's
  aspect); `RenderWidth` / `RenderHeight` are the scene's (`SceneTarget`, every pass's `Resize`, `RenderContext.Width`),
  times `SuperSamplingScale` per axis. `RenderFrame` calls `ResizeScene(false)`, so a changed factor applies on the next
  frame. `RenderSettings.SuperSampling` (a reference, null = off) is set by the hosts every frame: `GameHost` (player,
  TestGame) and the editor's Game view (also its captures) from the project; the Scene view and cameras' texture
  renderers never super sample. `RenderContext.OutputWidth/Height` (the UI draws there) and `SuperSampling` (the scale).
- Resolve (`PostProcessPass`, `Engine/SuperSampling.shader`): separable, across then down; across reads the HDR scene,
  applies exposure and the tonemap per sample and writes linear display values (RGBA16F, window width x scene height),
  down filters those, encodes sRGB and dithers at the window's pixels. Filtering after the tonemap bounds every sample,
  so edges against bright light are anti-aliased (an HDR average lets one hot sample light the pixel); no full-size
  tonemapped copy is written. The UI draws after it at the window's size.
- Optimal: soft light keeps the window's density, so its cost does not grow. Lumen's screen probes: spacing x scale
  (a multiple of 4 for the adaptive halves / quarters) and resolve scale / scale; the VCT screen-space resolve: resolve
  scale / scale; clouds march at the whole number of scene pixels nearest to resolution x scale on a log scale
  (`VolumetricRuntime.MarchScale`: 1.33 -> 1, 1.5 -> 2, Half at 1.33 -> 3; the view targets are kept per scale up to 8).
  Shadows, voxels, distance fields and the surface cache never depended on the screen.
- Saved with the project settings and shipped as a build's defaults (2026-11-19, the user's report: "the super sampling
  settings are not saved in the project settings"). The Graphics tab's Super Sampling and Filter combos changed the value
  before `InspectorContext.Track(true)` took the edit's "before" state, so the edit showed no difference: no undo step,
  no save (`EditTracker.Commit`), the value lived in memory until the editor closed (the Build button's
  `SaveProjectSettings` wrote it by chance; CI builds read the file). Now `BeginEdit` / change / `EndEdit`. The player
  starts with the build's settings; `ssaa=` / `ssaafilter=` / `ssaasmooth=` on its command line override them
  (`Program.ApplySuperSampling`, run by `GameLoader.SettingsLoaded` right after the settings are read, before the
  renderer's targets are made; Player.log `[Player] Super sampling 2.25x Lanczos (command line)`), and the game changes
  them at run time (`QualitySettings`). Verified on a scratch project: GUI editor + CLI `click combo:Super Sampling`,
  the item, `combo:Filter` / `item:Filter/Lanczos`: the file written at once, two undos and two redos written too; batch
  `-build`, the player without arguments (2.25x Lanczos from the build), `ssaa=4 ssaafilter=area`, `ssaa=1`,
  `ssaafilter=gaussian ssaasmooth=0.5`, invalid values (ignored). Not saved anywhere by the engine: a player's own
  choice between runs is the game's to keep (its options file).
- TestGame `ssaa=2.25 ssaafilter=gaussian|area|lanczos ssaasmooth=0.33`. Measured once (780M, 1280 x 720 window,
  default room, Lumen; pass times in ms, off / 2.25x Gaussian / 4x Area): GeometryPrepass 0.16 / 0.23 / 0.33, Opaque
  0.59 / 1.31 / 1.70, Transparent 1.12 / 1.62 / 1.96, PostProcess 0.09 / 0.36 / 0.36, ProbeGather.trace 3.1 / 2.4 /
  2.0 (no growth); the log's `[Renderer] Super sampling 2.25x: the scene at 1920x1080 for 1280x720`; the box's diagonal
  edge staircased off, smooth at 4x. Editor: the Graphics tab draws the section (New Frontier, settings untouched).
  Not checked: Lanczos and Smoothness by eye, a player build, GI quality at the larger probe spacing.

## Player startup and splash screen

- `LELEngine.Player/Program.cs`: boot.json (source-generated STJ, `BootConfigJson`), the window opens hidden without
  its renderer (`GameWindowOptions`), `Window.Show` puts the splash screen up as the first image (cloaked by DWM
  until presented: a swap on a hidden window is dropped and the window shows white), then `GameLoader` loads on a
  background thread: asset index, project settings and Game.dll while the window opens, then (the main thread hands
  over the GL context) renderer, the start scene's shader variants (only those: see "Shader compilation the game
  controls"), start scene and two offscreen warm-up frames (`GameHost.WarmUp`). The main thread pumps events, takes
  the context back, waits out `Duration`, then `Run()` (lifecycle on the main thread).
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
- A load for the running game reads through the prefabs it meets (2026-11-19, next section): what the game will
  instantiate later is loaded and prepared with the scene.

## First use at load, not in play: prefabs read through, effects compiled, fields kept (2026-11-19)

The user's report: destroying a ship stutters badly on a cold start, the Flight scene loads long; "fix every problem you
find". Measured with the hitch profiler (section after this one) on a built game driven by a script (fly at three
ships, fire, a missile salvo, kill; scratchpad `coldrun.ps1` / `report.ps1` of session e607714c). Nothing a scene's
prefabs use was loaded with the scene: a `Prefab` field is a handle, its file and everything in it were read at the
first `Instantiate`, on the main thread, one file after another, and its programs compiled where a frame first drew them.
- What that cost (780M; cold = a new folder and title: no driver cache, no program binaries; warm = the second start):
  the first shot 447 ms cold / 235 ms warm (bullet and muzzle prefabs, their textures, the muzzle's effect graph: 205 ms
  to compile cold, 49 ms from binaries, and 153 ms of JIT warm, 378 of 840 methods the VFX compiler's), the first missile
  294 / 86, the first hit 106 + 630 / 68 (one frame loads the prefab, the next compiles its graph), the first missile
  explosion 119 + 835, a first kill of a hull 40-95 / 25-53 (the wreck prefab, its material and a 4096 x 4096 texture
  read on the main thread). After the scene appeared: 1.9 s (every ship's model, materials and 42 textures loaded in
  `Start`, 27 distance fields built), 1.6 s (the GPU doing those fields), 3.6 s and 0.9 s (the clouds' programs compiled
  where they were first used: only cold; 142 + 133 ms warm). In the editor the same loads decode source images (a 4096
  x 4096 PNG at a kill), which is where it "stutters so much"; the editor run of the script was dropped at the user's
  word, the editor's play mode was checked without UI (below).
- `Prefab.Preload()` (public): loads every asset the prefab's components reference (`ResolvedPrefab.GetAssetReferences`:
  the generic data walked once, each file as its main asset type) and builds its instantiation data.
  `AssetLoadScope.Begin(background, preloadPrefabs)`: a scope with `PreloadsPrefabs` calls it for every prefab it
  records (`RecordDependencies`: fields of the scene's components, of ScriptableObjects, of other prefabs, at any
  depth; `VfxGraph`s record their textures now too). On: `SceneLoadOperation.Read` (every `LoadScene` /
  `LoadSceneAsync`, the player's start scene), `ResourceRequest` (`Resources.LoadAsync` of a prefab), the editor's play
  mode. Off: the editor opening a scene to edit it (it would load every effect's textures for nothing). The loads run on
  the loading thread inside the scene's `TextureImportBatch`, so the textures decode on worker threads.
  The price: everything reachable loads with the scene (New Frontier's Flight: 141 textures, 64 models, 59 materials,
  60 prefabs, 25 effect graphs, where 32 textures loaded before and the rest later). The same textures were uploaded
  in a session before (717 megapixels then, 734 now): no more GPU memory, only earlier. A project that references its
  whole item database from every scene loads it whole; an opt-out per field does not exist yet.
- Prepared with the load (`SceneManager.PrepareAsset`, public `PrepareAssets(assets, gpuDriven)` for hosts that read a
  scene themselves): an effect graph's programs (`VfxRuntime.Prepare`; `StartShaderBuilds` starts graphs for 40 ms at
  most, then leaves the rest to their own steps: AMD compiles inside the submit, ~20 ms a program, and 25 graphs held one
  frame of a background load for 6 s). `VfxRuntime.StartPrograms(graph)` / `GetPrograms` (which finishes) and
  `ShaderProgram.StartFromSources` (a generated program built like a file program: started, then waited for by its first
  use; same binary cache key as `FromSources`): a graph's programs are started together, then checked
  (`VfxProgramSet.Started`).
- The clouds' programs: `VolumetricRuntime.StartPrograms` when a first cloud is enabled (`Register`): every compute and
  march program is made at once, so their builds run while the cloud's voxels are read, not in the frames it first draws.
- Distance fields. The build's baker (`DistanceFieldBaker`) follows ScriptableObject assets now (a ship's model and
  wreck named by its data asset: `Classify`, `CollectAssetReferences` with `DocumentReferences`), takes every prefab the
  build ships (`GameBuilder` passes `dependencies.Included`) and asks for a mesh referenced without a name under its
  first mesh's name; New Frontier: 64 fields baked where 26 were, the player builds none (27 before, 190-245 ms and
  the GPU's seconds). The editor keeps them between sessions: `DistanceFieldAtlas.Store` (`IDistanceFieldStore`; the
  editor's `DistanceFieldBaker.Store` over `Library/DistanceFieldCache`, the baker's own cache, with `stamps.txt` so a
  model is hashed again only when its file or meta changed): an atlas asks it before building and hands it what it
  built. A build at run time no longer waits for the GPU: `KeepOnMesh` copies the texels into a pixel buffer behind the
  build and `BeginFrame` takes them when the fence signals (`CollectReadbacks`; the read made a frame wait 40-60 ms a
  field with the GPU busy). `KeepsFields = false` on the baker's atlas.
- Editor play mode: `EnterPlayMode` = `CanEnterPlayMode` / `LeaveEditScene` / `StartPlayScene` (not inlined: the first
  two hold the edit scene, and with them on the stack while the game's copy loads for seconds the script reload's leak
  check found the old scripts "on the stack of EnterPlayMode"; the views' caches are dropped before the reload for the
  same reason). `StartPlayScene` reads the snapshot in a preloading scope and prepares its assets; the console says
  `[Editor] Play: the scene read in N ms (...), its N assets prepared in N ms (N shader programs compiled for the first
  time on this GPU driver...)`. Without UI on New Frontier: the first play of a session 4.3 s (2.0 s read, 109 textures
  decoded on worker threads, 1.7 s prepared), the second 0.65 s, no field built (the store), scripts unloaded each time.
  With no program binaries at all (a batch editor has no binary cache, the driver's own was empty) the first play took
  31 s, 27 of them compiling: once per GPU driver in the GUI editor, where the binaries are kept.
- Found on the way, a build-only bug: components added to prefab instances were lost in a built game whenever the scene
  also held an editor script component. `EditorScriptComponents.Strip` rewrites such a document as JSON; the generic JSON
  read took the added component's `"gameObject"` key as one of its fields too, written back after the key as a double
  (the id is past 2^63), and the last duplicate won: object id 0, "has lost its GameObject in the prefab; left out"
  (New Frontier's training dummies had no input and dropped no loot in builds). `ReadDocumentComponent(..., added)` drops
  that property. Checked in the built scene's bytes (the id back in all 18 records) and the player's log.
- Results (780M, a built game, the same script; the kill frames' work, the frames before them 11-14 ms):
  first kill of three hulls 23 / 15 / 33 ms cold and 13-34 ms warm (40-95 / 25-53 before), no load in them; first shot
  76-125 ms (50-77 of it JIT; 235-447 before), first hit and first missile explosion under 100 ms (up to 835). Flight
  from the menu, warm: active 1.6 s after NEW GAME with the menu drawing (read 0.89 s on the loading thread, GPU 0.65 s
  over 19 frames, activated in 0.24 s); as the start scene: read 0.82 s + GPU 0.63 s (0.67 + 0.23 before, when the rest
  came later on the main thread). New files (a build just copied) cost ~10 ms each to open the first time (the virus
  scanner): 3.6-4.0 s of reading then, which no code here changes.
- Code compiled ahead of time (ReadyToRun, the user's go-ahead the same day: "Tak, włącz R2R"). What was left after the
  above was the JIT: code that runs for the first time compiles in that frame (first shot 50-77 ms, the frame a scene
  starts in ~260 ms, the menu's first frame 136 ms). `LELEngine.Player.csproj` sets `PublishReadyToRun` whenever it is
  published with a runtime identifier: the self-contained player (a development editor's own publish, `PlayerFiles`,
  and an editor's `dotnet publish`) and the framework-dependent player of an editor published with a RID; the SDK
  restores `microsoft.netcore.app.crossgen2.<rid>` from NuGet for it (35 MB, once). LELEngine.dll 2.4 -> 6.9 MB,
  OpenTK.Graphics 5.1 -> 9.6 MB. The game's scripts: `Building/ReadyToRunCompiler.cs` runs that package's
  `crossgen2` on the woven Game.dll after it is copied into the build (`GameBuilder` step 3b:
  input from `Library/PlayerScripts`, references = the build folder's managed assemblies, a response file; 1.1 s, 401
  KB -> 1,136 KB; `[Build] Game.dll compiled ahead of time (ReadyToRun, win-x64)...`). Not compiled
  ahead: the packages a game's scripts use, and the editor itself (its play mode compiles as it runs).
  A build setting that is an error when it cannot be kept (the user, the same day: "I want this r2r to be a setting in
  the build settings, and if it fails it is to be an error"): `ProjectInfo.BuildReadyToRun` (Project.json, default
  off; Build tab "Compile Ahead Of Time (ReadyToRun)"; batch `-readyToRun [true|false]`) -> `BuildSettings.ReadyToRun`.
  On: `ReadyToRunCompiler.CheckPlayer` before anything is built (the player has a runtime identifier, crossgen has a
  target for it, the player's LELEngine.dll holds native code (the CLI header's managed native header), a compiler is
  there, a framework-dependent player has .NET's assemblies of its platform to compile against: `PlayerSelfContained/`
  of the same platform, else the editor's own runtime when it is of that platform), then `Compile` in any configuration
  and for either player; each returns the error's text, the build logs "Error: ..." and fails (batch: exit 1). Off:
  nothing is compiled or checked (the player's own assemblies are what they were published as). The compiler:
  `<editor>/Crossgen/<version>/tools/` first (the editor's publish copies the package's tools there after the players
  are published, and fails without them: a CI image keeps the NuGet folder in a cache mount, which a cached engine
  layer must not depend on), then the NuGet packages folder (development editors); `crossgen2.exe`, `crossgen2` or
  `crossgen2.dll` through `dotnet`. Checked on Windows (development editor, New Frontier): self-contained builds
  (Game.dll 396 -> 1,124 KB), `-selfContained false` fails at once ("the player in ...Player is not published for a
  platform", exit 1, no output folder: `dotnet build`'s `Player/` is portable IL), an empty `NUGET_PACKAGES` fails
  ("crossgen compiler is neither next to the editor ... nor in the NuGet packages").
  Measured warm through the menu: the frame the scene activates in 623 -> 497 (engine only) -> 436-453 ms (JIT 2,549
  methods 263 ms -> 1,411 / 153 -> 843-851 / 63-97: what is left are generic instantiations, compiled expressions and
  stubs), no hitch at the first shot (109-125 ms before), the menu's first frame 164 -> 80-94 ms, Flight active 1.4 s
  after NEW GAME (1.6). A framework-dependent build is as before (checked: IL assemblies, runs).
  `RuntimeHelpers.PrepareMethod` over the assemblies was considered first and dropped: preparing can run static
  constructors early, on another thread.
- Left, and why. A cold driver: the Flight load compiles the 25 graphs' ~270
  programs itself (6-8 s once per driver; the helper process compiles only material variants), the clouds' programs
  3.6 s in the frame the scene activates, and AMD finishes a program at its first draw (~1 s after each of the first
  two kills, in the swap, cold only). A scene activating is one frame of 0.6-0.8 s (Awake, OnEnable, Start, first
  physics step, first render). `Scene.RebuildIfDirty` looks at every component after any spawn (0.5-4 ms on 5,300).
- Verify recipes (all without the editor's UI): `coldrun.ps1 -Name X` copies a build to a new folder with its own
  boot.json title (cold), `-Reuse` runs it again (warm), `-Refresh` copies a new build over it keeping the binaries;
  the player's `exec=Namespace.Type.Method` starts the game's test script; `batchsmoke.ps1` runs the editor with
  `-batchmode -listen`: open, play, `screenshot Game` (renders: the store, the programs), stop, play again, the log's
  lines. TestGame `vfx=1`, `volumetrics=1` and the default room render as before (looked at, no A/B); New Frontier's
  510 unit tests pass.

## Hitch profiler (FrameProfiler, 2026-11-19)

- `LELEngine/Mono/FrameProfiler.cs` (public, static): where a frame's CPU time went and what was done for the first
  time in it. Off unless asked for: `LEL_HITCH=1` (or a threshold in ms, default 25; a frame is a hitch when it is
  over that and over 1.7 x the median of the last 32), `LEL_HITCH_EVENTS=1` (every event as it happens, also outside
  frames and on worker threads), `LEL_FRAME_TRACE=file.csv` (a line per frame), `LEL_HITCH_LOG=file` (reports and
  events there: a console line costs a call stack in the hosts' logs, which would be measured), `LEL_HITCH_JIT=1` (a
  line per method the runtime compiles, with its thread and tier, stamped with the profiler's clock: which code ran
  for the first time in a hitch). `Configure(...)` sets the same from code.
- Frames: the hosts call `BeginFrame` / `EndFrame` (`GameHost`, the editor's `MainWindow`); scopes
  (`using (FrameProfiler.Begin("name"))`, the main thread's, free when off) are marked in the hosts, `Scene.UpdateScenes`
  (phases, each lifecycle callback kind, the physics step, the lists' rebuild), `Renderer` (every pass),
  `Prefab.Instantiate`; waits that are no work (`AddWait`: the swap, the wait for frames queued before a resize) are
  taken out. Events (`Event` / `EventSince(Timestamp, ...)`, any thread): asset loads over 0.2 ms with who asked (a
  load, or "the running game": a first use in a frame), shader programs (source read, binary cache hit, compile
  submitted, waited for, binary saved), textures (header / file read, decode, import waits, uploads), meshes, models,
  materials, prefabs (file resolved, instantiation data built), instantiations, scene reads and starts, distance
  fields, effect graphs, the clouds' programs, callbacks over 1 ms. Texts built with formatting are behind
  `if (started != 0)`: nothing is built when the profiler is off.
- A hitch report: the scope tree, the frame's events with their sum, the GPU passes (timer queries, a few frames
  old), the JIT's methods and time on this thread and on every thread, GC counts and pause, bytes allocated, and when
  a thread that only sleeps 1 ms at a time woke much later than usual, a line saying the process was held up from
  outside. `Write(text)` adds a line to its log (a test's marks), `ResetStatistics` / `WorstFrameMs` / `HitchCount`
  measure a stretch.
- The player's `exec=Namespace.Type.Method` (test hook, `Program.InstallTestHooks`): calls a static method of the
  game's scripts in the game loop's first frame (a scripted scenario of a built game).

## Game view: toolbar, debug overlay, draw statistics (2026-11-20)

- The user's ask: the Game view's toolbar as low as the Scene view's, and a toggle there for a debug overlay showing the
  game's statistics with simple performance graphs. The row (`GameViewPanel.DrawToolbar`) has the Scene view's frame
  padding (4, 1) and margins: `checkbox:Maximize On Play`, `checkbox:Debug Overlay` (`GameViewPanel.ShowOverlay`, the
  editor preferences' toggle "Game/Debug Overlay"). The row is 16 px high (23 before); the image starts at y 42 of the
  window (58). What follows the row is an item on every path (`ImGui.Dummy(viewSize)` without a renderer or scene): the
  cursor moved alone is ImGui's assert.
- `GameViewOverlay` (`LELEngine.Editor/Panels`) draws a panel in the image's top right corner with the window's draw list
  (no UI elements, no input taken; window captures show it): FPS and the frame time (average, shortest, longest of the
  last second), CPU (the game's update, the view's render), GPU (the view's timer queries, the three heaviest passes),
  graphs of the last 300 frames (frame time with 16.7 / 33.3 guides; game, render and GPU; what the game's update
  allocated each frame with the garbage collections marked by generation), the view's and the scene's sizes, draw
  calls and triangles, renderers in the cameras' lists, GPU-driven instances, lights, the loaded scenes' objects,
  updating components (`ActiveBehaviours`), 2D physics bodies, particles alive / capacity, the managed heap, the
  process's working set, KB allocated a frame and the collections so far. Graph scales are fixed steps (4 .. 100 ms,
  16 KB .. 16 MB); what is above the last is clipped and marked red, so one long frame (entering play mode) does not
  flatten the rest.
- `GameViewStats` samples every editor frame whether the overlay shows or not: `EditorApplication.Update` wraps the
  game's update (`BeginUpdate` / `EndUpdate`: Stopwatch and `GC.GetAllocatedBytesForCurrentThread`), `RenderViews` and
  `FrameWithoutUI` end the frame (`EndFrame` with the Game view's renderer: its render is counted when
  `Renderer.LastFrame.Frame` changed).
- Engine: `DrawStatistics` (`Rendering/`, static, GL thread) counts every draw call the engine makes (`GeometryPool`,
  `GpuScene`'s multi-draws, `DynamicGeometry`, `FullscreenQuad`, `DebugDraw`, `VertexBuffer`, `UIRenderer`, VFX outputs):
  draw calls, triangles of the draws whose counts the CPU gives, and indirect draws (their triangles are the GPU's and
  not counted). `Renderer.LastFrame` (`RenderFrameStatistics`): per `RenderFrame` (target cameras included) its CPU
  time, the counters' differences, cameras, renderers in the lists (`Partition`), GPU-driven instances, lights, output
  and scene sizes; `Frame` counts every `RenderFrame` call (FrameIndex leaves out frames without a camera). Players
  have it too (no overlay there yet).
- Verified (780M, New Frontier's Flight): batch editor with `preferencesfile=` (a scratch file with the toggle on: the
  user's preferences untouched), `screenshot Game` in edit and play mode; the GUI editor (`focus=0`) in play mode:
  12 FPS, 81 ms frames, game 5.3 ms, render 12.0 ms CPU, GPU 68 ms (GlobalSDF.dynamic 33, VoxelGI.dynamic 18), 1,158
  draw calls (54 indirect), 20.8 M triangles, 513 opaque renderers, 522 GPU-driven instances, 30-40 KB allocated a frame;
  `click "checkbox:Debug Overlay"` turned it off and on.

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
  (64 stage sources). Listed in `Data/ShaderVariants.txt` (`ShaderVariantList`, "shader|KEYWORDS" lines, then one
  "scene|path|line numbers" line per build scene); the player compiles only its start scene's in the loader step
  "start scene's shaders" (after the renderer, before the scene), the game asks for the rest (next section).
  Runtime-made variants still compile on first use.
- Shader compilation the game controls (2026-11-15, the user's ask: the splash compiled every variant of the build;
  it must be short, compile only what the first scene needs, and the game's logic in the next scenes starts the full
  compile). Build: `BuildDependencies` records what each asset uses (`uses`, engine resources included; `UsedBy` =
  transitive), `CollectShaderVariants` returns a `ShaderVariantSet`: every variant, and per build scene the variants
  of the materials in its closure plus the always included shaders (scripts load those by path, in any scene); the
  build log names each scene's count. Player: `GameLoader.CompileShaderVariants` compiles `set.Of(startScene)` (all
  when the start scene has no list, e.g. a `scene=` override outside the build) and sets `ShaderCompilation.ListFile`.
  Runtime API (`LELEngine.Shaders.ShaderCompilation`): `CompileAll()` (one operation, returned again while it runs and
  after; dropped by `SceneManager.UnloadAll`), `CompileScene(path)`, `Compile(variants)`, `AllVariants`,
  `VariantsOf(scene)`; a `ShaderCompileOperation` (`Progress`, `DoneCount`, `IsDone`, `Failures`, `Completed`, awaitable,
  `Complete()` = waits for the rest) is finished on the main thread (`SceneManager.Update`: continuations, `Completed`,
  the log line `[Shader] Compiled <name>: N programs for M variants (K compiled already) in X ms over F frames (...)`).
  Editor play mode: `EditorApplication.CollectBuildShaderVariants` (registered as `ShaderCompilation.EditorVariants`)
  makes the build's set from the project on a worker thread at the first request of a play session (`IsListing`,
  progress 0 meanwhile); on New Frontier the first listing of an editor session takes ~9 s (later ones ~0.1 s), which
  on the main thread held play mode's first frame and tripped the script reload's leak check.
- Where it compiles (the user, after the first version stalled the menu: "try a separate context"):
  1. `ShaderCompileThread` (default, `ShaderCompilation.UseCompileThread`, `LEL_GL_DISABLE=compilethread` off): a hidden
     1 x 1 GLFW window sharing the main context, made on the main thread at the first operation (`TryCreate`, GLFW's hint
     defaults restored after); a worker thread makes it current only while it runs an operation (GLFW may destroy the
     window at exit when idle) and sets 0 driver compiler threads on it. Operations run there in order, the main thread
     only polls. On the compile thread `ShaderProgram.StartBuild` builds whole (compile, link, results, `glFinish` in
     `MarkBuilt`) before `built` (volatile, set last) flips, so other contexts only see finished programs; every program
     has a `buildLock` (`EnsureBuilt` / `StartBuild` / `Reload` / `Delete`), new programs are made unbuilt inside the cache
     locks (`InternalStorage.GetOrCreateShader`, `GetVariant`, the instanced variant: internal constructor `start: false`)
     and built after leaving them, the static `started` list is locked and never holds compile-thread programs, a
     program another context started is left to it (`BuildStartedElsewhere`), `ProgramBinaryCache` starts under a lock
     with atomic counters. `ShaderCompilation.DropAll` waits (max 10 s) for the thread to go idle.
  2. `ShaderCompileHelper` (player only: `Executable` = the game's own exe, `shaderhelper=0` off; needs the binary
     cache): the compile thread first checks which variants' programs (the shader's own, the variant, its instanced one)
     the binary cache lacks (`ShaderProgram.BinariesCached`: sources read and hashed as the constructor would, no GL), starts
     the game exe with `compileshaders=<request file in %TEMP%>` for those (GL version, data and cache folders, instanced,
     parent pid, variant lines; `Program.Main` runs `RunHelper` before anything else: hidden context of that version,
     `GLCapabilities`, `AssetDatabase`, cache folder; chunks of 8 started together, `done <i>` per variant on stdout; it
     quits when the parent is gone), waits for each `done` and loads the binary. A variant the helper did not report
     (it failed or ended) compiles on the thread. Dropping kills the helper.
  Why both: AMD's OpenGL driver (780M) holds every context of a process while one compiles. Main thread with parallel
  compile: the operation used 1.8 s of main-thread time but the menu drew ~24 frames in 14-15 s; compile thread: ~19
  frames in 14-17 s (0 or 64 driver threads alike); a second process compiling cold did not slow another player at all
  (900 frames 17.0 s alone, 17.1 s beside it). With the helper, cold: `CompileAll` 7.1-7.5 s over 308-324 frames (the
  helper's driver uses its own threads); warm: helper skipped ("all in the binary cache"), 0.55-0.67 s over 14-17 frames
  (loading a LitTextured binary still holds the main context 30-40 ms on AMD). The editor has no helper: compile thread
  only (its binary cache is usually warm: 633 ms for 19 programs).
- Measured on the 780M (New Frontier, 14 variants: Flight uses all 14, the menu 1; 22 programs with the instanced ones).
  The AMD driver keeps its own GL cache per executable path (`%LOCALAPPDATA%\AMD\OglCache`; the program binary hint gives
  other entries): a build copied to a new folder is cold for the driver, and a copy with its own boot.json `Title` has
  its own (empty) program binary cache too (`%LOCALAPPDATA%\LELEngine\<title>`, delete after). Flight as start scene,
  cold: the splash compiled 11.6 s (0.5-0.7 s warm); menu as start scene, cold: splash 1.7 s for its 1 variant, menu's
  first frame ~4 s after the start.
- Verify recipe (scratchpad `nfbuild` of session 72b68768): batch `-build`, the player with `scene=Scenes/Main.scene`
  (the build's start scene stays as set), cold copies as above, `screenshot=dir screenshotframe=4` (the player keeps one
  frame) for the menu's progress line, `exitframe=90` during the helper's compile (clean exit, no helper or request file
  left); NEW GAME in the editor through a temporary editor script run by CLI `exec`
  (`button.clickable.SimulateSingleClick(null)`): batch play mode's `key Enter` did not reach the menu's focused button.
  Also checked: a project shader edited on disk reloads in the editor (Reload under the lock), TestGame lumen / vct render.
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
- Builds in chunks, over frames (2026-11-20, the user's report: the editor crashed right after a Mesh Baker bake of a
  few hundred models, twice). The bake wrote its .mesh (~5 M triangles, a flat 44 x 6 x 44 field), then the field's build
  was one dispatch of every texel against every triangle, one thread per texel: 11,616 threads looping 5 M triangles
  ran for seconds, Windows reset the driver (System log `nvlddmkm` 153, WER `LiveKernelEvent` 141 = TDR) and NVIDIA's
  GL driver ended the process (0xc0000409 in nvoglv64.dll). Now (`Engine/SdfBuild.shader`): a work group of 64 threads
  per texel, each taking every 64th triangle, folded in shared memory (unrolled: no barrier in a loop), so a flat field
  fills the GPU; a dispatch takes one chunk of the triangles (`PairsPerDispatch` 2^27 texel x triangle pairs; chunks of
  whole 64s of triangles, 3,072-byte aligned ranges, every bound range under GL's 128 MB storage block minimum), every
  texel's nearest squared distance and winding kept between chunks in a per-build buffer, the last chunk writes the
  atlas; chunks are flushed once `FlushPairs` (2^24) pairs went to the GPU since the last flush (since 2026-11-23: a
  long chunk on its own, the small fields of a load together with the frame; every chunk was a submission of its own on
  Vulkan). On Vulkan the work group's lanes fold with subgroup operations (`subgroupMin` / `subgroupAdd`, then the
  subgroups' results: one barrier instead of five; OpenGL keeps the shared-memory folds). A frame spends at most
  `PairsPerFrame` (2^27; its first chunk always runs),
  the smallest build first (`BeginFrame` continues them); `GetOrBuild` / `Find` give a field only once `IsBuilt` (the
  renderer keeps its previous field or has none meanwhile, as for a settling shape); a build nobody asked for in
  `EvictAfterFrames` frames is dropped (its slices free); eviction skips builds going on; a build whose field another
  atlas kept on the mesh meanwhile takes that copy. `BuildsOverFrames = false` (the build's `DistanceFieldBaker`) runs a
  build whole, still in short dispatches. Triangles are uploaded per build a block at a time from the mesh's vertices and
  indices (`GetTrianglePositions` keeps its copy on the mesh for good). Chunk boundaries depend only on the field and the
  mesh, so a field is the same bit for bit however the frames cut it. Fields differ from the old single-thread sums only
  in the winding's rounding (the sign is read at 0.5 of a whole turn): the editor's cache stays valid.
- Verified with scratchpad `SdfCheck` of session f827bec7 (real driver, hidden context; RTX 5070 Ti): icosphere, box,
  box at shape (1, 0.25, 0.5), torus, open quad, a 4-chunk icosphere all equal to a CPU reference in doubles (0 signs
  differ, distances within half-float precision, < 1e-3); over frames == at once, a second build bit-identical; 5.12 M
  triangles over an 800-wide disc (44 x 6 x 44, 59.5 G pairs): 445 dispatches over 445 frames, a frame median 1.46 ms,
  max 11 ms, the first frame 115 ms (the 245 MB upload), 64 sampled texels equal to the reference; a 327,680-triangle
  build dropped after 30 frames unasked (slices 44 -> 0). glslang (`ShaderValidator`, scratchpad `GlslangCheck`): 0
  errors, 0 warnings. Not run: the editor itself (the bake again), the 780M (expect several times the frame times).

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
  `Material.ExtraTextureUnitStart` (27 since 2026-11-16, above the engine's reserved units 8..26: 23 to 26 are the
  GI's media volume, see "Volumetric objects: more lights, light into the GI"; it was 23) and up, as far as the
  context has units (`Material.MaxTextures`: 8 + GL_MAX_TEXTURE_IMAGE_UNITS - 27, 13 on 32-unit GPUs); past that a
  warning.
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

## Impostors, a material shader's own passes (2026-11-19)

The user's ask: an impostor system (a few quads in place of the whole mesh that look like the mesh), integrated with the
lighting and the engine's other features. Octahedral impostors, baked into atlases and lit at run time:
- `Impostor` (`Rendering/Impostors/Impostor.cs`, global namespace, a ScriptableObject, Create > Impostor): `SourcePrefab` /
  `SourceModel`, `Settings` (`ImpostorSettings`: `Frames` 12 (2..32 views a side), `FrameSize` 128 (a power of two, 16..1024),
  `Hemisphere`, `Supersampling` 4, `Border` 2) and what a bake made: `Mesh`, `Material`, `ShapeMap`, `BaseMap`,
  `SpecularMap`, `EmissionMap` (hidden `m_` fields). Run time: `Impostor.Bake(gameObject, settings)` (in memory, on the GL
  thread: objects the game builds), `AddRenderer`, `Setup(renderer)`, `Instantiate(scene, parent)`,
  `AddToLODGroup(group, 0.01, 0.15)` (a child of the group's object draws it as the last level; a group without levels
  gets its own renderers as the first). An impostor is baked in its object's space: its renderer has that object's
  transform. Its renderer is out of the GI's volumes (`ContributesToGI` false; a LOD group gives the GI level 0 anyway).
- Proxy mesh (`ImpostorMesh`): the box of the object's bounds, so culling, LOD group bounds, picking and the instance
  table see the object's real place and size. Each corner carries a corner of a hexagon around the unit circle in its
  texture coordinates (the two opposite corners its centre); the vertex stage lays those out as a billboard facing the
  view, three faces cover it and the other three face away and are culled: six triangles drawn.
- Bake (`ImpostorBaker`): `CollectRenderers` (enabled mesh renderers, of LOD groups the first level, opaque sub-meshes, no
  impostor materials: a prefab holding its own impostor bakes the same); per view (`ImpostorMath.FrameDirection`: an
  octahedral map of the sphere or of its upper half) an orthographic capture around the bounding sphere into four
  RGBA16F targets and Depth32F at Supersampling x the frame, each surface drawn by its shader's `LEL_SURFACE_CAPTURE`
  variant (render state `SurfaceCapture On`: Lit and LitTextured write diffuse, normal, specular colour + smoothness,
  emission + occlusion per workflow) or, without one, by `Engine/ImpostorCapture.shader` (the GI captures' values);
  `Engine/ImpostorResolve.shader` filters the covered samples into the frame; the coverage is a distance field made on
  worker threads from the capture's depth mask (Felzenszwalb, spread max(2, frame / 16) texels, 0.5 at the outline); empty
  texels take the nearest covered one's values. Atlases (`ImpostorBake`): Shape (rg the normal's x, y in the view's axes,
  b depth, a coverage; linear), Base (rgb diffuse sRGB, a smoothness with a specular atlas, else occlusion), Specular
  (rgb sRGB, a occlusion; only when 0.5 % of the texels have a specular colour; `SceneHighlights` when as many have none),
  Emission (rgb / the brightest, sRGB; the scale goes to the material's `Emissive` alpha; only when something emits).
- A bake repeats exactly (the user's rule for written files, see "Deterministic documents"): each view is drawn twice,
  depth first, then EQUAL with blend MAX from a clear of -60000. Faces lying on one another (doubled faces, panels on a
  hull) have equal depths and the GPU does not pick the same winner every time: on the 780M 10 of 29 repeated bakes of a
  2,224 triangle ship differed in 1 to 3 texels, 2 of an axe, 5 of a car, 0 of a group of cubes and spheres; one capture
  sample a texel showed whole 2 x 2 blocks changing colour with the same normal and depth; a pass-through blend (strict
  order) did not help. Now 0 of 4 x 29, and 12 bakes over 3 editor sessions wrote byte-identical files. TestGame
  `impostorrepeat=N`; scratchpad `repeat.ps1`, `determinism.ps1`, `atlasdiff.ps1` of session 733c7c4a.
- Shader (`Engine/Impostor.shader`, `Engine/Impostor.glsl`; C# mirror `ImpostorMath`): per pixel the three views nearest
  to the ray's direction (the grid cell cut along its shorter diagonal), each hit on its plane and shifted by the depth it
  stored (`Parallax`, refined `ParallaxSteps` 2 times), weighted by direction and by how well its depth agrees with the
  ray (`Agreement` 24: parts hidden from a view do not smear); the blended coverage below `Cutoff` is discarded; the
  surface's own depth goes to `gl_FragDepth` (`layout(depth_greater)`: the billboard stands in front of the sphere), so
  impostors intersect geometry and receive shadows where their surface is. Lighting from the rebuilt normal and world
  position: with a specular atlas LitTextured's BRDF (`SPECULAR_MAP`; `MIXED_WORKFLOW` keeps the scene's highlight where
  the atlas is black), else the scene's Blinn-Phong as Lit; every directional light with its shadows, local lights,
  `GetIndirectLighting` (Lumen and VCT), emission. Material values: tint, cutoff, parallax, steps, agreement, shadow bias.
- A material shader's own passes (new, general): render state lines `ShadowCaster Custom`, `GeometryPrepass Custom`,
  `SurfaceCapture On` (`ShaderRenderState.CustomShadowCaster` / `CustomGeometryPrepass` / `SurfaceCapture`); the pass draws
  the object with the same file compiled with the pass's define next to the material's keywords and the instanced define
  (`ShaderPass`, `ShaderPasses`, `ShaderProgram.GetPassVariant`: `LEL_SHADOW_CASTER`, `LEL_GEOMETRY_PREPASS`,
  `LEL_SURFACE_CAPTURE`) and `Material.SetUniforms`. `GpuScene` puts such buckets last (`PlainBucketCount`,
  `FirstOwnPassBucket`; the shadow cull has a segment for the plain casters and one per own-pass bucket,
  `DrawShadowBucket`; `OwnPassParts` for what cannot be bucketed); `GeometryPrepass`, `ShadowPass`, `GridShadowMap` (local
  lights) and `MeshRenderer.RenderSubMeshPass` / `RenderOwnShadowCasters` draw them. `LelInverseModelMatrix()` in
  Engine/Instancing.glsl. The impostor's prepass variant writes normal, roughness and motion ((model - previous model)
  x the surface point); its forward variant tests `Cutoff - 0.01` and writes depth x (1 - 3e-7) so LEQUAL passes where
  the prepass wrote; its shadow variant pushes the surface `ShadowBias` radii from the light.
- Pass variants build with their program, not at the first draw: `ShaderProgram.BuildPassVariants()` (the passes of
  `ShaderPasses.Drawn` the render state declares; the capture variant compiles when a bake runs) from
  `SceneManager.PrepareAsset`, `ShaderVariantList.Compile`, both paths of `ShaderCompileOperation`, the compile helper;
  `ShaderProgram.BinariesCached` asks for them too; `ShaderValidator` checks them (`ReadPassDefines`). A build's log:
  "Compiled 10 shader programs for the start scene's 3 variants" (6 the impostor's: variant, instanced, two passes each).
- Found on the way: `TextureProperty.Bind` asked for the texture's handle before activating its unit, so a texture
  uploading on its first use bound itself on the unit of the texture bound before it, which then drew black (the first
  view of the first bake; any material whose second texture uploads at its first draw). And a JSON document saved with
  a UTF-8 byte order mark (a text editor, PowerShell 5.1's `Set-Content -Encoding utf8`) read as no document: `IsJson`
  took the mark, the parser did not, so an asset showed as "Missing script" (`JsonDocumentFormat.WithoutByteOrderMark`).
- Editor (`LELEngine.Editor/Impostors/ImpostorAssets.cs`, `EditorApplication.Impostors.cs`, `Inspector/ImpostorInspector.cs`):
  a bake writes, next to the asset on the first bake and into the files the asset references after that (moved or renamed
  files stay the user's, a new file never replaces another), the atlases as PNG with their import settings (mipmaps,
  trilinear, clamp, anisotropy 1, the file's size; Shape linear and `compression` None, the colours sRGB and BC7; the
  compression is set once, then the user's), the proxy as an .obj (written only when the box changed: the model imports
  again and the scene is rebuilt, `ReloadScene`), the material (its tint, cutoff, parallax and bias stay) and the asset.
  An atlas a later bake has no use for stays on disk (a log line). No bake while the game plays; a bake is no undo step.
  Entry points: Create Impostor on a prefab or model file (Project panel context menu, Assets > Create Impostor: "Name
  Impostor.asset" next to it, baked, selected), the asset's inspector (Bake / Bake Again, what it made, the baked files
  as references, a look at an atlas with the texels outside the object dimmed), Assets > Bake Impostor / Bake All
  Impostors, the LOD Group inspector's Add Impostor field (drop or pick an asset: one undo step), automation
  (`EditorAutomation.CreateImpostor` / `BakeImpostor` / `BakeAllImpostors` / `AddImpostorLevel`; CLI `impostor create
  <prefab or model>`, `impostor bake <asset>|all`, `impostor add-level <object> <asset>`). `Png.Encode(..., bottomUp)`.
  ImGui reads its texts as printf formats: the LOD Group inspector's "Last frame" line printed "% x" as a stray hex
  number (and CascadeSplitsEditor's tooltip had "% ("): "%%" now.
- Why the Shape atlas ships uncompressed (probe project's build, the impostor at 1.6 x its frame size, whole frame
  against the uncompressed build): BC7 mean 0.489 / 255 with 1.21 % of the pixels over 8 (ragged outlines, holes, ragged
  shadow), BC7 High Quality 0.327 (and a 58 s build), the same build twice 0.002. 12.6 MB against 3.1 MB for 1536 x 1536.
- Measured (780M, 1280 x 720, Debug). Mesh against impostor in the same place, 186 x 186 crops, mean of 255: gi=0 group
  1.37, axe 0.59, ship 2.32 (a car whose model's normals face away 4.75: such normals are stored mirrored towards the
  view); Lumen at frame 400 group 0.45, axe 0.55, ship 3.21 (turned 240 degrees 0.56, 1.21, 3.28, and the same while
  the objects turn there); VCT 0.39, 0.53, 3.42 (the ship's smooth specular surface reflects the GI, and its rebuilt
  normals are softer than the mesh's). Ship over six views by
  parallax steps 1 / 2 / 3 / 4: 3.88 / 3.41 / 3.34 / 3.31; without the agreement weight 4.92. The group under Lumen
  before `MIXED_WORKFLOW` 3.19, after 0.44 (VCT 3.13, 0.41). GPU frame: 900 ships as meshes 6.12 ms, as impostors 3.76;
  3,600 ships 24.83 ms (prepass 11.32, opaque 10.85, shadows 2.57) against 6.56 (1.90, 2.31, 2.19); the CPU's render
  time is the same either way (one instance each). Bake of 12 x 12 views of 128 at 4 samples: ~1.1 s in the editor (2,224
  triangles), 8 x 8 of 64: 0.15 s. The prepass's motion vectors, turning 0.6 degrees a frame (`impostormotion=60`: moved
  pixels and mean vector per prop, x 1000): ship mesh 6182 (-6.54, 0, -4.62), impostor 6064 (-6.81, 0, -4.76); car 2146
  (-13.30, 0, 5.12) / 2114 (-13.32, 0, 5.10); the same GPU-driven and per object. (A Lumen A/B of the turning objects
  did not change when the vectors were zeroed or reversed in the built shader: it proves nothing about them.)
- Verify recipes. TestGame `impostors=1` (`ImpostorTestScene`: a ship (specular workflow, specular and emission maps), an
  axe (scene workflow, normal map), a car, a group of renderers with a glowing and a metal part; `impostorswap=frame`
  shows the meshes until then and the impostors in their places after; `impostorfield=N` ships with LOD groups; the other
  `impostor...` options are listed in Program.cs): scratchpad `ab.ps1` (two runs, per prop crops, `compare.ps1`),
  `sweep.ps1` (six views x variants), `perf.ps1`, `regress.ps1` (the engine's scenes against a build of the commit before:
  lumen, vct, gi=0, materials, refraction, lights + night, three suns, outdoor, gpudriven=0 identical at frame 400, max 1 of
  255; trails differ as two runs of the old build do, they move with real time). Checked there: with and without the
  prepass, `gpudriven=0`, three shadowed suns, local lights, a non-uniform scale, any yaw and pitch, the hemisphere,
  odd frame counts, cutting into the ground. Editor: scratchpad `ImpostorProbe` (`editor.ps1` batch `-listen` or `-Gui`,
  `cli.ps1`, `build.ps1`, `player.ps1`; `Assets/Editor/ImpostorProbe.cs` makes the materials, prefabs and scene): create
  from a prefab and from a model, bake again in place, settings changed on disk then baked (8 x 8 of 64, upper half),
  errors (no source, no impostor, play mode), the three actions, the context menu and the inspector's button and picker
  by `click`, add-level with undo / redo and inside prefab mode (the prefab saved, its instances followed), play mode,
  a player build (atlases as KTX, the proxy as LELMODEL, 40 stage sources through glslang) and its frame.
- Limits and not verified: no cross-fade (the engine has none: the switch is a pop; an impostor drawn larger than its
  frame size blurs, so keep the level's height under FrameSize / the view's height); transparent sub-meshes are not
  baked; a hemisphere impostor seen from below shows its horizon views; the shadow it casts is rebuilt from the light's
  nearest views (soft detail, the bias). Only the 780M ran it (glslang passes for the three GPU profiles). Not checked:
  a re-bake that changes the box while prefab mode is open (the scene behind it keeps the old proxy until it reloads).

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
- Delete (2026-11-21; before, assets could only be deleted outside the editor): `AssetDatabase.DeleteAsset(path)` sends
  a file with its meta, or a folder, to the Recycle Bin (`RecycleBin`: shell `SHFileOperationW` with FOF_ALLOWUNDO;
  deleted for good off Windows, `DeletesToRecycleBin`) and rebuilds the index; loaded copies stay loaded, as after a
  delete outside the editor. `EditorApplication.DeleteAssets(paths)` refuses play mode, the open scene and prefabs open
  in a stage (or a folder holding them), clears the selection and drops the deleted paths from Project.json's start /
  build scenes and the project settings' audio project / input configuration. `ProjectPanel`: Delete in the context
  menus (file rows, "Delete N Files" on a multi-selection; folder rows; tree) and the Del key (tree: the shown folder;
  list: the picked folder or the selected files), then the "Delete assets" dialog (Enter / Delete confirms, Escape /
  Cancel). The panel's row clicks honour automation's `--ctrl` / `--shift` (`UIAutomation.InvokedCtrl`), as the
  hierarchy's do. Verified on a probe project through the CLI (`context`, `click "menu:Delete"`, `click
  "button:Delete"`, `key Delete` + `key Enter`, `delete-asset`): files, metas and folders in the Recycle Bin, the open
  scene's folder refused.
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
  Unity's None / ScriptOnly / Full; ScriptOnly drops System/Microsoft/OpenTK/Hexa/Silk frames), in every host since
  2026-11-19 (before, only with a `MessageLogged` subscriber). `Debug.Output` routes the text: the editor points it at
  the real stdout and takes the message from `MessageLogged`, so `ConsoleLog` never parses Debug text.
- Direct console writes (the engine's 70-odd `Console.WriteLine`s: `[ProbeGather]`, `[SDF]`, ...) get the writer's
  call stack too (`Debug.CaptureStackTrace(mode, skipCaller)`, skipping log writers' and System writer frames).
- Text logs show the stack at once (2026-11-19, the user's rule: "the editor's console hides the stack trace, but in
  CLI mode, in the logs in files and everywhere else logs must show the stack trace right away"). Every message is
  written out with its stack on the lines after it, the format of exception stacks: Debug's messages (an exception's
  own stack, else the caller's), direct console writes (`ConsoleLogWriter`, engine, `Mono/ConsoleLogWriter.cs`: a
  `TextWriter` over `Console.Out` that writes a message at a time, the lines one write completes, then the writer's
  stack as the first line's type records it; text bringing its own "   at" lines, such as an exception written whole,
  and empty lines as they are; `Flush` writes an open line without a stack), build diagnostics (their
  `   at file:line N`). `ConsoleLogWriter.Install()` in the player (after the Player.log tee, so not in the shader
  compile helper, whose stdout is its protocol), TestGame, batch mode (after the `-logFile` tee) and the editor's
  `RunEditor` (the project chooser too); `ConsoleLog` derives from it and takes its place (`ConsoleOutput`: the output
  under the installed writer while it is still the console's), adding the entries in `OnMessage`. CLI `console`
  (`ConsoleMessage.ToString`) and JUnit `system-out` print the stack under each message too; the console window's
  export and copy did already (`ToFullText`). Not touched: `Console.Error` (the CLI client's own errors, the helper),
  the CLI client's output, the benchmarks' report. Costs a stack capture (file and line from the PDBs) per message in
  players too, as Unity's logs.
- Only warnings, errors and exceptions since 2026-11-20 (the user's word: "daj tylko dla warningów i błędów", after a
  CI build's output in Forgejo, 15,874 lines of which 14,992 stack frames, crashed the browser). `Debug.WritesStackTrace(type)`
  (type != Log) decides for `Debug`'s text and for `ConsoleLogWriter` (the line's `TypeOf`: "[Warning]", "[Error]",
  "[Exception]", a "[Shader]" line with "error"); a plain message is written alone and its stack is not even captured
  unless someone keeps it: `MessageLogged` subscribers and `ConsoleLog` (`KeepsPlainStackTraces`): the editor's console
  still shows any message's stack on demand. NF's `-runTests -build` locally: 706 lines, 151 of them stacks (the 6
  warnings and errors), 510 test lines without one.
- Batch mode imports what waits before it runs anything (`BatchEditor.Run`: `ImportPendingChanges` while it waits for
  `!IsBusy`), as a CLI command does: a build the load asked for (tests older than the scripts) waited for a command that
  never came, so `-runTests` printed "No tests in this project" and exited 0 (found 2026-11-20 on New Frontier).
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
- A new selection, wherever it comes from (a scene view pick, a field, the CLI), unfolds its parents in the hierarchy
  and scrolls its row to the middle when it is out of view (2026-11-08, the user's ask; `HierarchyPanel.revealSelection`,
  the same unfolding as an object field's ping, no flash); the highlight is the row's selection. Verified: selecting
  Flight's Game UI/Preview Stage/Preview Camera Root/Preview Orbit/Preview Camera with the list scrolled to its end
  unfolded the four parents and brought the row into view.
- A selected object a game script destroys is no longer selected (`EditorApplication.Selected` is null once it is dead,
  2026-11-10; Unity's Selection does the same). The editor is not null-check woven, so before it stayed selected in
  play mode and the scene view's gizmos asked it for components: `ObjectDestroyedException` inside a CLI screenshot's
  capture (`SceneViewPanel.RenderCapture` -> `DrawGizmos` -> `VisualEffectInspector.DrawGizmos`), then ImGui's "Missing
  End()" assert ended the process. Found by a game test that killed the ship whose child was selected; verified with
  the same run (kill, then `screenshot Scene`: no exception).
- Temporary and read-only objects (2026-11-14, the user's ask: a scene's storm showed a fold arrow over nothing, its
  script's lights and bolts being `HideAndDontSave`; "show them, as non-editable temp objects"): a row's arrow counts
  only children the hierarchy lists (`TempObjects.HasVisibleChildren`).
  `HideFlags.DontSave` (on the object or a parent) shows dimmed with a "temp" tag at the row's right end and a tooltip;
  new `HideFlags.NotEditable` (4; `DontSaveAndNotEditable` for a script's own helpers) is shown but never changed by the
  editor: inspector values, header (name, active, layer), component toggles and materials disabled (headers still
  fold; no component menu, no Add Component), a note under the header; hierarchy rename / drag / drop / Duplicate /
  Delete / Create Child off; no transform gizmo, collider handles take no drag; `DeleteSelected` / `DuplicateSelected`
  refuse with a warning; automation `rename` / `set` / `reparent` / `addcomponent` throw (`TempObjects.RequireEditable`).
  Scripts change such objects freely. Verified on a game's scene: the storm's eight flash lights listed under it,
  tagged, the inspector disabled, CLI rename / set refused, a Delete from the context menu refused and logged.
- Scene visibility (2026-11-19, the user's ask: the eye beside an object in the hierarchy; Unity's): a column at the
  hierarchy's left (`HierarchyPanel.DrawVisibility`, element `button:Visibility <name>`); a click hides the object and
  everything under it in the scene view or shows them, Alt + click the object alone; a closed eye on hidden objects, a dim
  eye on one with hidden objects under it, an open eye on a hovered row; row menu "Show All in Scene View". The scene view
  draws nothing of a hidden object and its clicks and navigation depth go through it; the game view, play mode's game and
  builds draw it as always. Engine: `Renderer.HiddenObjects` (`Func<GameObject, bool>`, null = none; `RenderContext.Hides`):
  `Partition` leaves the renderers out of every list (drawing, shadows, GI, GPU scene), trails and custom renderers
  (`TransparentPass`), clouds (`VolumetricRuntime.Collect`, `VolumetricMedia`) and effects (`VfxInstance.Owner`) are
  skipped; only the scene view's renderer gets a filter (`SceneVisibility.Filter`, also its sized captures). The state is
  the user's view, not the scene's data: `SceneVisibility` keeps object ids per project and scene / prefab in
  hierarchy.json (`HierarchyExpansion` entries have `Hidden` beside `Expanded`), no undo, the scene is not dirtied; a new
  scene keeps its hidden objects when first saved. Verified on a scratch project by CLI (`click "button:Visibility Cube"`,
  sized screenshots): the cube and its shadow gone from Scene, still in Game, in play mode too; a parent hid its child,
  shown again; the dim eye; kept over a restart (`hierarchyfile=`); the scene not dirtied. Not checked by a run: Alt + click
  and the hover eye (no CLI modifier or hover), effects, clouds and trails hidden (same check as the meshes).
- Reordering in the hierarchy (2026-11-22, the user's ask: only re-parenting was possible, the order is wanted for
  readability). As in Unity: an object dragged on to the upper 30 % of a row goes in front of it, the lower 30 % after it
  (an unfolded row's: in front of its first child), the middle under it (as before), the empty area below the rows makes
  it the last root (it only un-parented before; in prefab mode the prefab root's last child, and in front of / after the
  prefab root's own row means its first / last child). A green line with a small circle at the level it goes to shows
  where (drawn after the rows, `DrawDropMark`), a frame the row it goes under. A selection moves together in the
  hierarchy's order. One undo step ("Move X" within its parent, "Re-parent X" otherwise), nothing when it stands there
  already. Logic in `EditorApplication.MoveObjects(objects, parent, before, reorder, refuse)` (`EditorApplication.Hierarchy.cs`,
  also automation's): the rules the drop had (the prefab stage's root, an instance's own objects, read-only objects,
  into its own subtree), and under an object of a prefab instance the added objects stand after the prefab's own: a saved
  scene makes the instance's objects first and puts the added ones under them after, so an added object between the
  prefab's would not stay there (the drop goes after the last own child with a warning; the CLI fails instead). Engine:
  `Transform.GetSiblingIndex` / `SetSiblingIndex` work for root objects too (the place among the scene's roots: the
  roots' order is the scene's object list, `Scene.GetRootIndex` / `SetRootIndex`; -1 and a no-op before), plus
  `SetAsFirstSibling` / `SetAsLastSibling`. Saved as it is (documents are written in hierarchy order). CLI: `reparent
  <object> <parent|none> [index]`, `reorder <object> <index|first|last>`. Verified on a scratch project in a batch
  editor (scratchpad `order/` of session 47efe346): roots and children by CLI, undo / redo, save and reopen, a no-op that
  does not dirty the scene, a probe through `MoveObjects` (two objects into a parent before a third, two siblings moved
  together, a selection with one inside the target, a drop on its own place and on its own parent, a prefab instance:
  an added object kept after the prefab's own over a save and reopen, an instance's own object refused). Not run: the
  drag itself in the GUI editor (no CLI drag), the line's look.
- The same places for a prefab or model file dragged from the Project panel (2026-11-22, the user's go-ahead): the rows'
  zones and the line as for objects (`HierarchyPanel.PlaceOnRow`; only files the hierarchy instantiates show a mark),
  `InstantiatePrefab(path, parent, position, before)` / `InstantiateModel(path, parent, before)` put the new instance in
  front of `before` (an instance's own children first, as above). And Duplicate (Ctrl+D) puts each copy right after its
  original, as in Unity (it went after the last sibling; a copy of an instance's own object goes after the prefab's own,
  with the warning). Verified in the batch editor: Duplicate of one and of two objects (the copies, by id, after their
  originals; undo / redo), a probe: a prefab in front of a root and of a child, a model in front of a first child, a
  prefab under an instance in front of its own object (after them, warned), an instance's own object duplicated; the
  scene saved and reopened the same.
- A double click on a hierarchy row frames the object in the scene view, with or without children, and never folds
  it: only the arrow folds (2026-11-19, the user's ask; no `OpenOnDoubleClick`, and a double click on the arrow, which
  toggles twice, frames nothing).
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
  elsewhere once moved the user's real console. Since 2026-11-08 they never write the user's `Library/EditorState.json`
  (scene camera) or `windows.json` either (`EditorStateWritable`, `WindowStatesWritable`): runs that placed the camera
  for a test moved the user's. Files of their own: `layoutfile=`, `editorstatefile=`, `windowsfile=` (with it the run
  also restores the windows from that file at start, as the user's editor does); tests copy the user's files there.
- Each dock's front tab survives a restart (2026-11-08, the user's report: Console and Inspector share places, the wrong
  one came to the front): the layout records each dock node's `Selected` tab, but ImGui writes the ini only when a window
  moves or resizes, so `EditorApplication.TrackFrontTabs` writes it (`ImGuiController.SaveLayout`) half a second after
  the set of front windows changed, and `SaveEditorState` at close (no longer only ImGui's own save in `DestroyContext`).
  At start every window focused itself as it appeared (ImGui focuses appearing windows), in drawing order, and the last
  docked one, Console, brought its tab over the saved one: the first UI frames of a project drop that focus
  (`startupFocusFrames`). Tab ids in the ini are ImGui's hash of "#TAB" seeded with the window's id, CRC32C since
  ImGui 1.92 (Project 0x9C21DE82, Console 0xEA83D666, Inspector 0x36DC96AB, Lighting 0x29B5A1D1, Scene 0xE601B12F,
  Game 0xD1EB2482, Hierarchy 0xBABDAE5E). The user's `layout.ini` had not been written since 2026-10-22 although their
  editor (Editor.cmd, Release) wrote it when reproduced: not explained, and no longer depends on ImGui's exit save.
- Maximize On Play (2026-11-17, the user's ask; Unity's): a checkbox in the Game window's toolbar row (above the image:
  the game's height is the rest), kept in the preferences' toggles (`Game/Maximize On Play`, `GameViewPanel.MaximizeOnPlay`).
  While the game plays or is paused the Game window is drawn as a window of its own ("Game###GameMaximized": undocked,
  unsaved, fixed over the main viewport's work area, so the menu bar with Play stays) and `DrawUI` draws no other window
  (the scene view is `Hide()`n: no render; script windows keep their Update without GUI; deferred work and modals still
  run); the docked "Game" is simply not submitted, so the layout never changes. Back in edit mode every window appears
  again and ImGui focuses each in turn (Console took its dock's front tab): the start's focus drop (`startupFocusFrames`)
  runs again. Verified on New Frontier's Flight (`preferencesfile=` with the toggle, `playframe=`, `stopframe=`,
  `screenshotframe=`): full-size game while playing, the layout and front tabs before and after identical. A
  `SetCursorPos` with no item after it (the toolbar's gap while the scene loads) is ImGui's assert, 0xC0000409.
- Scene window Maximize / Minimize (2026-11-18, the user's ask): a video player's full screen button at the right end of
  the Scene toolbar (corners out: `button:maximize`; corners in: `button:minimize`; `Icon.Maximize` / `Icon.Minimize`). A
  right-click menu on the tab came first; the user wanted the icon instead. `SceneViewPanel.Maximized` draws the window as
  Maximize On Play draws the Game window ("Scene###SceneMaximized", `IEditorPanel.ImGuiName` then names it, so `windows`,
  `elements`, `click` and screenshots find it) and `DrawUI` draws no other window (`GameView.Hide()`: no render, no game
  input). Play switches to the game (the user's correction: Unity would keep the Scene maximized): `EnterPlayMode` sets
  `Maximized` false and the Game window takes the focus (its tab in front) once the windows are back; with Maximize On
  Play the maximized Game takes over at once. After Minimize the start's focus drop runs, then the Scene window takes the
  focus (`focusAfterRestore`; `restoreFocusTo` = "Game" for Play's restore). Maximized during play (Maximize On Play off)
  it covers the Game window. Kept in windows.json (`Maximized`, set after the project's load), cleared by Reset Layout
  and by closing the window.
- Scene toolbar (2026-11-18, the user's ask: "the toolbox as icons, not text" and a lower row): Move / Rotate / Scale and
  Local / World are `EditorGui.IconButton`s drawn as Unity's icons (`Icon.Move` cross of arrows, `Rotate` arc, `Scale` box
  with an arrow, `World` globe, `Local` cube; tooltips name them and their keys); element names stay `button:Move` etc.
  The row pushes `FramePadding` (4, 1): 19 px buttons instead of 23, 2 px above. Always Refresh and Nav stay checkboxes
  with text (Unity keeps them out of its tools too).
  - `TestAnchors.CurrentWindowId` read the window through `ImGuiP.GetCurrentWindow()`, which sets `WriteAccessed`; after
    that ImGui's `IsItemHovered` refuses the title bar / tab item `Begin` leaves, so `BeginPopupContextItem` right after
    `Begin` (a tab's context menu) never saw the mouse once an element was recorded. It reads the context's
    `CurrentWindow` now.
  - `VolumetricMediaPass` kept the last frame's clouds through `ReleaseSceneReferences`: a view that drew no frame (Game
    under the maximized Scene, or behind its tab) held the old scene and scripts after the play reload ("unloaded after
    all, 26.8 s"). Cleared now ("unloaded (5 GC passes)").
  - Verified on New Frontier with copies of the user's state files (scratchpad `maxscene/` of session 0ec30c68:
    `start.ps1`, `input.ps1` with SendInput at absolute positions, `stop.ps1`): Maximize / Minimize and the tool buttons by
    real clicks (one Minimize click of the first run did nothing, not reproduced in 5 more), `windows` before and after
    identical, a real click on Play while maximized (layout back, Game tab in front; with Maximize On Play the maximized
    Game), a restart opens maximized, screenshots
    of the maximized window (live and laid out at a size), the user's files unchanged.
- The editor crashed when exiting after a transform gizmo drag (access violation in cimguizmo.dll at 0x33914 / 0x33923;
  WER logged 50 of them on 2026-11-02 and the user's on 2026-11-08): cimguizmo.dll links its own copy of ImGui, whose
  context pointer (`ImGuizmo.SetImGuiContext`) still pointed at the destroyed context, and ImGuizmo's static buffers
  were freed through that copy's `MemFree` at process exit, which reads the context. `ImGuiController.Dispose` points
  ImGuizmo at the current context (null) after `DestroyContext`. Reproduced and verified with `ldrag=60;sceneview@0.5,0.5;150,0`
  on a scratch project's cube (exit code 0, no WER event); look in the Application event log (Id 1000) for crashes the
  user did not mention.
- Test hooks: `log=N;Type;text` (nested calls, Exception with an inner cause), `consoleselect=N;text`,
  `preferences=N;Section`, `dockwindow=N;Window;Target` (dock next to another window, e.g. Console;Inspector for a
  tall narrow console), anchors `console:row`, `consoleframe:i`, `consolefilter:Name`, `preferences:Section`;
  `focuswindow=N;Name|M;Name`, `logfronttabs=N|M` (`[Test] Frame N front tabs: Hierarchy|Scene|...`), `logdocks=N|M`
  (each docked window: submitted, dock, tab id, front, the node's and tab bar's selected tab).

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
  (`DistanceFieldActive`: Lumen, or VCT with an SDF trace mode whose rays do not trace the scene's triangles;
  `VoxelCones`, and the SDF modes under `Lighting.VoxelRayTracingActive`, build no field and no mesh SDFs),
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

- `SceneViewPanel`: the wheel moves 20% (`WheelShare`) of the distance to the point under the mouse at the navigation
  depth (below), so it works at any distance; middle-drag pans at the navigation depth taken at the press (world per
  pixel = 2 * focusDistance * tan(fov / 2) / view height). `FocusDistance` is the last of those depths (frame, F, sets
  it too) and is saved with the view. Test hooks: `wheel=N;sceneview;notches|...`, `mdrag=N;sceneview;dx,dy|...`,
  `logsceneview=N|M` (position, forward, focus distance); anchor `sceneview`.
- Navigation depth (2026-11-08; the user: after a few zooms here and there a pan barely moved or raced):
  `NavigationDepth(mouse)` is taken from what is there at that moment, never kept from an earlier zoom: (1) under the
  cursor, the nearer of the mesh pick and a cloud (`VolumetricObjectBase.Raycast`: a CPU march through the volume's
  density to where the light through it falls to half; clouds write no depth and are not picked); (2) the surface
  drawn nearest to the cursor within 0.1 of the view's height (`SceneDepthProbe`: after the view renders, a compute
  pass reduces its depth to 64 x 36 cells, each the nearest of 4 x 4 texels, read back through a buffer and a fence a
  frame or two later, no stall; a cell is placed with the camera that drew it, so its depth stays right after the
  camera moved); (3) the median over a 16 x 9 grid of rays, each the nearer of the drawn surface and a cloud; (4) the
  view shows nothing: the distance from the camera to the nearest renderer's or volume's box (boxes holding the camera
  left out); (5) the last depth. Before, it was the last zoom's pick or the ground plane y = 0, kept as the focus
  distance: New Frontier plays in XY with the camera looking along Z, so it sees that plane edge on and a ray met it
  near or thousands of units away depending on the cursor's height, and the next pan over empty space used that.
- Why not the GI's global distance field (the user's idea: the occupancy around the camera): it exists only while the
  GI traces distance fields and only inside the GI's volume, it holds distances only within `SdfBandVoxels` (8 in New
  Frontier) of a surface, clouds are not in it, and a pan needs the depth along the ray through the cursor, not the
  nearest surface in any direction (it may be behind the camera). The nearest box is that idea without the limits,
  used only when the view shows nothing.
- Test: `logcamera=` lines end with `focus F drawn M source S at (x, y)` (S = mesh, cloud, near, view, around or last;
  the point of the view it was taken for). Scratchpad `storm/realnav.ps1` (real input, `-Absolute`): wheel notches at
  three places with strokes at each, per stroke the depth, its source and the camera's units per pixel; New Frontier's
  Flight with copies of the user's state files. 2026-11-08: over the storm `cloud` 635-890 (a volume: after the camera
  moved, the ray through the same pixel meets another part of it), over empty space `view` 960-1012, after zooming out
  1416-1839, `around` 1993-2359 once a 321 px pan (relative input, the pointer's acceleration) had moved everything out
  of the view; absolute input (200 px strokes) 889, 967-988, 1425-1497 at the three places; no frame over 50 ms. `storm/pantest.ps1` (scripted 200 px pan over the storm, image shift by profile correlation): depth from
  the cloud (1287), the bright asteroid behind it moved 188.9 px (parallax; the old focus depth gave about 283 px).
- Camera drags (2026-11-08; the user found panning and turning inconsistent): look (right button) and pan (middle
  button) start on a press over the view and last while the button is held, over the view or not; both read the motion
  from the first pixel (`CameraDragDelta`). Before, a pan stopped when the cursor left the view and started past ImGui's
  6 px drag threshold, turning did neither, and both stopped at the screen's edge.
- The drags move by the cursor's motion as the UI sees it (`io.MouseDelta`), never with the cursor captured: the
  frame of the press applies none (ImGui takes the positions after a press in the next frame, so it is all from before
  the press), the frame of the release still applies its motion, and `ReleaseCameraDragIfAbandoned` ends a drag only
  when the controls missed a frame (it ended every drag on its release before the motion was read). GLFW keeps the
  mouse captured by the window while a button is held, so a drag goes on outside the window up to the screen's edge.
  History (2026-11-08, both shipped and both reported broken by the user): (1) the cursor hidden and grabbed with
  OpenTK's per-event deltas (c654fb5): GLFW warps the cursor when it captures (to the window's centre) and when it lets
  go, and the warps came in as motion, up to 1117 px a frame (pitch -75 after horizontal strokes only: "jumps and
  resets rotation and position"); (2) GLFW raw mouse motion with positions measured against the capture's (uncommitted):
  clean with a local mouse, but the user works through Chrome Remote Desktop, which injects absolute positions (as do
  pen tablets and virtual machines), and a captured cursor turns them into the distance from the window's centre, so a
  pan went towards one side only. Do not capture the cursor for editor drags.
- Real-mouse test (scripted hooks feed ImGui and never exercise the OS input path): scratchpad `storm/realdrag.ps1`
  starts the editor focused (`screenshot=` with an unreached frame keeps it an automated run without `focus=0`), with
  `logcamera=<file>` (every frame: drag, the drag's motion, camera, yaw, pitch, the view's screen rectangle), and sends
  OS input with user32 `SendInput`, relative moves or `-Absolute` (MOUSEEVENTF_ABSOLUTE, as Chrome Remote Desktop: the
  pointer keeps its own position), middle / right button, in strokes left and right (`-StepPixelsY` for diagonals);
  `realdrag-check.ps1` reports the largest motion in a frame, the pitch's range, per stroke the summed motion and the
  camera at the start and the end. Now: absolute and human-paced relative strokes, straight and diagonal, all equal
  and the camera back exactly at its start (yaw, pitch, position). Strokes larger than the view leave the cursor
  outside it (at the screen's edge), and a press there starts no drag, as before. Verified: a 900 px middle drag running
  past the view's edge moved the camera the full 900 px at the pan's depth (even steps).
- Flying (right button + WASD/QE) moves by the frame time averaged over about ten frames (`smoothDeltaTime`, Unity's
  smoothDeltaTime), not the raw one (2026-11-07). The user saw New Frontier's storm and the position gizmo "jump in
  random directions" when flying sideways, more the farther away, never when turning: with a volume filling the view
  the editor is GPU bound and its measured frame times alternate (11 / 23 ms, mean 17.5) while the display shows frames
  at an even rate, so raw steps came out uneven (camera steps 1.0-2.7 units, image steps 0.4-1.1 px); turning follows
  the mouse per frame. Smoothed: camera steps 1.55-2.03, image steps changing 0.05 px a frame instead of 0.33. Not a
  rendering fault: the volume, an opaque cube beside it and the camera's logged path agreed in every capture.
- Motion tests: `movecamera=N;dx,dy,dz;frames` (the camera moved evenly along its own axes: x screen right, y up, z
  forward), `rfly=N;sceneview;keys;frames` (the right button and keys such as D held: a real flight at the editor's
  frame times, each frame's dt and camera position logged), `dumpview=f1,...;folder` for every frame of it; measure the
  image's shift per frame by correlating column / row luminance profiles of a region (sub-pixel; still frames give 0.00
  px, so the method's own noise is nil). `reportframes=` tells whether the game view rendered too (with Scene and Game
  as tabs of one dock only the shown one renders). `setfield=N;Object;Component.field;value` takes short component
  names (`VolumetricObject`): an unresolved type no longer throws.

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
- Scrolling (2026-11-12): a fast camera moves the volume nearly every frame (75 m/s over 1.5 m voxels). Before, every
  move rebuilt the whole global SDF (3.9 ms on the 780M) and cleared the radiance cache (0.6 ms, and the probes never
  converged while moving). Both are toroidal now, like Lumen's clipmaps:
  - Global SDF: grid voxel v is stored in texel `(v + sdfScroll) mod res` (`GI.SdfScroll`, uniform `sdfScroll`,
    `SdfStorageVoxel` in Engine/DistanceField.glsl; `SampleSdf` clamps to the edge texels' centres itself, the textures
    wrap with GL_REPEAT). A move by whole voxels composes only the entering slabs (static from the static objects, the
    working field from it plus the dynamic objects) and shifts last frame's dynamic regions (`ScrollStaticField`,
    profiler `GlobalSDF.scroll`, ~0.2 ms); a size / object count / static version change or a jump past the grid still
    rebuilds (scroll back to 0). Verified: after (52, 1, 106) voxels of scrolling the field and ids were bit-identical to
    a rebuild at the same place (scratch probe dumping both in grid order).
  - Radiance cache: with `FollowCamera` its grid snaps to whole probe spacings (within half a spacing of the volume)
    and probes are stored at slot `(coord + rcScroll) mod n` (`RadianceCacheProbeIndex` / `ProbeCoord` in
    Engine/RadianceCache.glsl, `RadianceCacheBinding.Scroll`). A move keeps every probe still inside with its history;
    the entering slab's slots are emptied by Engine/RadianceCacheReset.shader and traced first (priority list, up to a
    quarter of `RadianceCacheProbesPerFrame` a frame); the round robin keeps its place across the list rebuild.
  - Fixed volumes keep scroll 0 and render as before (TestGame `animate=0 dumpframe=400`, Lumen and VCT: the A/B
    difference equals the run-to-run one, max 2/255; note TestGame's orbs move with real time unless `animate=0`).

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
- `[ShowIf(member)]` / `[ShowIf(member, value)]` and `[HideIf(...)]` (`Mono/Attributes.cs`, Odin's; 2026-11-08): the
  inspector shows a field only while a field, property or parameterless method of the same object is true or equals
  the value (several: all must hold); `FieldDrawer.IsShown`, checked before the field's headers; a missing member
  shows the field and warns once. `Light`'s `SpotAngle` and `InnerSpot` are `[ShowIf(nameof(Type), LightType.Spot)]`
  (the user's ask: a light shows only its type's settings).
- Sliders (2026-11-09, the user's ask: a slider could only be dragged, the value not typed): `EditorGui.SliderFloat` /
  `SliderInt`, which every slider of the editor goes through (`[Range]` fields, material ranges, VFX slots, Animator and
  Timeline weights, Audio Studio, UI Builder, Project Settings, Preferences, `EditorGUILayout.Slider` / `IntSlider`), draw
  Unity's slider: the slider and a number field beside it (`GetFontSize() x 4.5` wide, 40 % of a narrow item), both in
  the caller's item width, in one ImGui group (so `IsItemActive`, `IsItemDeactivatedAfterEdit`, `Track`'s undo steps and
  `IsItemHovered` see either; the group is the last item). A typed value applies as it is typed and is clamped to the
  slider's range, either way round (Unity's; ImGui's Ctrl+click typing goes past it). A format that is the number alone
  ("%.3f", "%d") shows in the field only: the slider's text is drawn transparent, so its rounding to the format stays; a
  format with words or units ("%.0f px", "weight %.2f", "-inf dB", "default") stays on the slider too, the field showing
  its number ("%.1f"; none: "%.3f"). A visible label ("Alpha") goes after the field. `IsItemDoubleClicked` leaves out a
  double click over the field (it selects text there; the Audio Studio's dB sliders reset on a double click). Not changed:
  the Audio Studio mixer's vertical faders (raw `ImGui.VSliderFloat`, Ctrl+click types) and its knobs. Verified on New
  Frontier: scratchpad `slider/slidertest.ps1` (CLI: the field typed 0.5 and 7 -> 1, one undo step each, `input` on
  slider and field, "abc" refused, Preferences' font sliders keep "17 px") and `slider/realslider.ps1` (SendInput, the
  focused editor: an absolute pointer as Chrome Remote Desktop's and a local cursor click into Side Softness's field,
  Ctrl+A, typed, Enter: 0.4, 0.05; the slider still drags; three undos back to the start). Elements are placed in their
  window: add the window's place from `windows` for screen positions.
- The Transform header's right-click menu (2026-11-08): Reset Position / Reset Rotation / Reset Scale and Reset All
  (local values, one undo step each, `InspectorPanel.ResetTransform`); other components keep Remove Component / Reset.
  Verified with the CLI: `context "header:Transform"`, `click "menu:Reset Position"`, values read back, undo.
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
  first click that way until 2026-10-24). Never `return` between a `BeginChild` and its `EndChild`: the object picker
  closed itself on a double click from inside its list's child window, and ImGui's "Must call EndChild() and not End()!"
  ended the editor (0xC0000409; fixed 2026-11-14: a flag, closed after `EndChild`; reproduced and verified with a real
  SendInput double click, scratchpad `balance/realpicker.ps1`). A scan of the editor found no other such return.
- Test hooks (TestHooks / TestAnchors): `drag=N;from;to|...` (press N, hover N+10, drop N+12), `click=`,
  `doubleclick=`, `rclick=`, `ctrlclick=` (N;anchor|...), anchors `hierarchy:Name`, `project:Folder/File.ext`,
  `project:(empty)`, `component:Type`, `field:Type.field[@fraction]` (`anchor@fx,fy`: that point of any anchor's rectangle,
  `sceneview@0.5,0.5` the view's centre; plain anchors press 40 px in on their first line), `row:Label/i[/row]`, `button:AddEntry`,
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
- Prefab mode's light (2026-11-17, the user's ask: "give that scene its own light, always along the camera"): a stage is
  a scene of the prefab's objects alone, with no light, so it showed only what glows. Before the several-directional-
  lights change (2026-11-14) it was lit by accident: the last scene's sun colour and strength stayed in the old static
  carrier, shining down. Now `EditorApplication.AimStageLight` (called by `SceneViewPanel.Render` / `RenderCapture` right
  before rendering) keeps a "Prefab Stage Light" in the stage's scene, a `DirectionalLight` turned like the scene
  camera (a headlight: white, strength 1, no shadows, not in the GI or in volumetric objects, which would rework on
  every camera move), `HideAndDontSave | NotEditable`: out of the hierarchy, the prefab's file (`SavePrefabScene`
  skips DontSave roots, `StageRoot` too) and the undo snapshots; made again after an undo replaced the stage's scene.
  A prefab's own lights shine besides it. Verified on a game's ship model prefab: lit from two views, a rename saved
  by Auto Save without the light in the file, undo reverted it and the stage stayed lit. The stage's environment
  (ambient, sky) is still the prefab scene's defaults, black.
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
- Always included shaders (2026-11-12; found with a game whose built player died before its first frame:
  `DirectoryNotFoundException ... Shaders\Shaders\Atmosphere.shader`). A shader a script loads by its path
  (`new ShaderProgram("Shaders/X.shader")`) is used by nothing the build follows, so it was not shipped, and
  `ShaderProgram.ResolveShaderPath` fell back to the engine's library folder (hence the doubled `Shaders\Shaders`). The
  user's ask: a field in Project Settings to drag such shaders into; their correction: not on the Build tab ("it runs
  the build, it is not for settings"). So: `ProjectSettings.Graphics` (`GraphicsSettings.AlwaysIncludedShaders`, a list
  of `IncludedAsset`: GUID + last path as plain strings, so reading the settings loads and compiles nothing: the player
  reads them before it has a renderer), edited on the Graphics tab (`ProjectSettingsWindow.DrawGraphics`,
  `Tab.Graphics` appended to the enum: the tab is saved by name): a bordered list that takes shader files dropped on it
  (`DragAndDrop.AcceptFiles`: project files by path, nothing loaded; anchor `includedshaders`), a row each (click
  pings the file, X removes), an "Add Shader..." combo of the project's other shaders; undoable, saved with the
  settings. `BuildDependencies.Collect` adds them with the reason "Project Settings > Graphics > Always Included
  Shaders" (their #include files follow as for any shader; a missing one is a build warning) and
  `CollectShaderVariants` lists each without keywords: glslang checks them and the player compiles them while it
  loads. Verified on that game: a real drag through the test hooks (`projectsettings=1500;Graphics
  pingasset=2500;Shaders/Atmosphere.shader drag=3000;project:Shaders/Atmosphere.shader;includedshaders`), the combo,
  X, undo / redo through the CLI, the settings file, a `-build` (six shaders in `Data/Shaders`, twelve variants
  checked, the missing entry's warning), and the built player (`screenshot=folder screenshotframe=500 exitframe=560
  focus=0`: exit code 0, the frame with the game's own fog and smoke shaders drawn).
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
  Coverage voxelization (`VoxelCoverage`): 8x MSAA rasterization (conservative rasterization with the exact covered
  area where the device has it: "Voxel GI and distance fields on Vulkan") splatted over the two nearest voxels along the
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
  `voxcoverage=0|1`, `voxconservative=0|1`, `voxmips=iso|aniso|sh`, `anisolevels=N`, `shadowtaps=1|4`, `conejitter=0|1`,
  `jittercell=m`, `staticrebuild=1` (the static voxel geometry and global distance field built again every frame),
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
- Output lighting `LitPerVertex` (2026-11-10, for SmokeOn's particle smoke): `VfxShade` (sun with its shadow map, ambient,
  the GI volume) runs in the vertex stage for a white surface (`v.lighting`, a new member of `VfxVaryings`, 1 for the
  other modes) and the fragment multiplies its colour by it; two-sided picks the side the camera sees (no
  `gl_FrontFacing` there); nothing in the shadow pass. Why: `Lit` evaluates the shadow map and the radiance cache
  probes for every fragment of every layer, so large overlapping particles cost with their area: SmokeOn's hover
  cloud (~690 lit quads) took 25 ms of `Transparent.Draw` on the 780M, 2.2 ms unlit, 0.4-0.6 ms per vertex. Verified
  on quads (Octagon, normal bending, soft particles) only; mesh and strip outputs and `CastShadows` with it are not.
- Output setting `LowResolution` (2026-11-10, blended outputs without ZWrite; `VfxOutputDescription.LowResolution`,
  `VfxDrawEntry.LowResolution`): `TransparentPass` draws every such entry, in their sorted order, into the layer of
  `Rendering/LowResolutionTransparency` at the place the last of them sorts: half the scene target's size, RGBA16F
  cleared to (0, 0, 0, 1) (alpha = what the scene shows through), Depth32F filled from the transparent pass's depth copy
  with the farthest of each 2 x 2 (`Engine/LowResolutionDepth.shader`); the draws use blend states 4-6
  (`VfxRuntime.SetDrawState`: the colour as usual, alpha x (1 - a)); `Engine/LowResolutionComposite.shader` lays it
  over the scene (Blend One SrcAlpha) from the four nearest texels weighted by the eye-depth match, so outlines in front
  stay sharp. Soft particles read the depth copy at `gl_FragCoord * 2` (codegen), points get half the viewport height.
  Profiler section `Transparent.LowResolution`. Order against other transparent objects is the layer's, not each
  particle's (HDRP's low-resolution transparency has the same limit). Why: SmokeOn's dense particle smoke (5-7k lit
  quads up to 10 m) cost 2-3 ms of fill at full resolution on the 780M, 0.5-0.6 ms in the layer.
- Materials in the same layer (2026-11-22): render state line `LowResolution On` (`ShaderRenderState.LowResolution`,
  per variant, so a `[Toggle(KEYWORD)]` and `#if KEYWORD` around it make it a material's switch; it sends a shader
  without a queue to Transparent). `TransparentPass` draws meshes, trails, custom renderers and static batch parts of
  such materials into the layer with the particle outputs, all in their sorted order (`DrawsLowResolution`, the layer
  laid over the scene where the last of them sorts); the per-entry drawing is one method for both
  (`DrawMaterialEntry(..., lowResolutionLayer)`), so the full-resolution path is the old one (frames of a large
  transparent scene before / after: max 2 of 255, as two runs of the new build). Only blends the layer can hold go
  there (`ShaderRenderState.DrawsLowResolution` / `LowResolutionProblem`): colour factors as the shader's with the
  target's factor One or OneMinusSrcAlpha and a source factor that does not read the target, no ZWrite, no GrabPass;
  `ApplyLowResolution` replaces the shader's alpha factors by Zero, (One for additive, OneMinusSrcAlpha otherwise), as
  the particles' states 4-6. Anything else draws at full resolution and the shader's load says why (`[Shader]
  LowResolution On in X draws at full resolution: ...`); so do the material's objects when there is no depth copy to
  build the layer from. `Engine/SceneDepth.glsl` reads the depth copy at `gl_FragCoord x lelSceneDepthPixelScale`
  (clamped to the copy; `SceneDepthTexture.SetUniforms(..., pixelScale)`: 2 in the layer), so `SceneDepth On` shaders
  need no change. Profiler sections `Transparent.LowResolution` (the layer's depth and its draws) and
  `Transparent.LowResolutionComposite` (the lay-over). Measured (RTX 3080 Ti, 3440 x 1318 with super sampling 2.25,
  300 large additive soft sheets over a field of rocks, the camera inside them): their cost against none 2.6-4.1 ms at
  full resolution, 1.3-1.7 ms in the layer (its depth and clear ~0.4 ms, the lay-over 0.2 ms); pictures against full
  resolution: mean 0.03-0.11 of 255, 0.2-0.3 % of the pixels over 2 (in the gaps between objects in front).
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
  3 x 3 range; the editor keeps refreshing until a history held 32 frames, `SettleFrames`; a history is continued by
  its renderer's own frame count, `Renderer.FrameIndex`: the volumetric pass's shared count went up once per renderer,
  so an editor drawing its scene and game views in one frame restarted every history each frame). The history is checked
  as the GI's screen probe history is (`ProbeIntegrate.shader`, 2026-11-08): a second target (RG16F: log2(1 + the light's
  distance), 0 = no light; the frames it holds); of the four history texels around the reprojected point only those
  whose light lay within 20 % (`HistoryDistanceTolerance`) of this pixel's distance from last frame's camera count,
  weighted bilinearly, a partial match for fewer frames; a new value weighs 1 / n up to 1 / (1 - `HistoryWeight`); the
  history target is cleared whole each frame. Before, the history was read blindly with one bilinear fetch: where an
  asteroid in front of New Frontier's storm had hidden the cloud (the march stops there: no light, alpha 0), moving
  sideways uncovered cloud whose history was that emptiness, the loose 3 x 3 clamp (the asteroid's empty texels are in
  the box) let it through, and see-through blue outlines trailed every asteroid. A/B against the commit before on the
  same flight (a worktree of it built apart): outlines gone, the cloud as smooth (neighbour difference 0.55 both, 1.0
  without the filter). Without it a reduced
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
  its first enabled one): a backdrop's own sun, but then no scene shadows (a light that is off has no shadow maps).
  Since 2026-11-14 the scene's other directional lights light a cloud too, through the lights' transport (see
  "Directional lights: several at once"); `SceneLights` off keeps them out.
  `OwnAmbient` replaces the scene's flat ambient light by `AmbientColor`.
- Scene shadows (`ReceiveShadows`, `ShadowStrength` 1 = the sun fully blocked): per step one comparison in the cascade
  of the sun's the point falls in (`VolSceneShadow`, faded out towards the shadow distance, lit outside the cascades),
  on the sun term only; only for a cloud whose sun casts shadows (one of the shadowed directional lights; before
  2026-11-14 only the main light, `Sun` null or `DirectionalLight.This`). The scene's
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
- Moving lights (2026-11-08): when the same lights stay in the same slots and only move (or change reach, cone), the
  transport is worked out again only in the box of where the changed ones reached before and reach now, in the drawn
  textures and at once (`VolumetricData.TryUpdateLightsInPlace`, the kernel's `volRegionMin` / `volRegionMax`; at most 2 M
  voxels and a third of the grid, else the whole transport); a light's part is zero outside its reach, so the result is
  the whole build's (TestGame `vollight=2` moving then still against `vollight=3` still: max 1 / 255). A whole rework
  (a light entering or leaving the reach, the set changing) writes into a kept spare pair of textures instead of making
  new ones. Before, every frame of a light moving in a cloud's reach made the whole transport again in a new pair of
  textures (~25 MB for New Frontier's storm, made and dropped per frame). Measured (TestGame `volbig=1 vollight=1 gi=0`,
  a storm of New Frontier's voxel count): the volumetric pass 0.70 -> 0.21 ms, GPU 1.70 -> 1.16 ms a frame. The user
  reported the editor slowing down and stuttering badly while dragging a light into the storm's reach; in New Frontier's
  editor neither engine stuttered in four reproductions (a lamp moved by a script and dragged through the gizmo's path,
  `dragselected=`, ranges 60 and 600: 16.7 ms frames throughout), so that report is not reproduced yet. In play mode the
  frame time doubles during the storm's flash bursts whatever the lamp does (Lumen's surface cache: the eight flash
  lights, range 2500, change every frame).
- Scene lights (`SceneLights`): `Light` components whose range reaches the shape take the transport's slots left after
  the cloud's own `Lights`, whatever their intensity (a flash going dark keeps its slot: no rework when it flickers);
  `Light.VolumetricStrength` scales what the cloud takes (see "Local lights"). Since 2026-11-16 the lights past the
  eight slots are not dropped: the longest ranges keep the slots and the rest are baked into one texture (next
  section).
- Made on the GPU from the settings (`VolumetricData`, compute shaders in `ShaderLibrary/Engine/Volumetrics`), unless
  kept (see "Made clouds kept" below):
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
  Random(Seed)` x 1000, the same numbers on any .NET. A transport voxel counts 1 + 0.7 x lights voxels of the budget
  (`TransportCostPerLight`; New Frontier's eight flash lights took 68 ms of the 780M in one frame, now spread over two).
- Made clouds kept (2026-11-08, the user asked whether the volumes could load faster, in parallel, and be stored for
  builds as textures are): measured on New Frontier's storm (12.6 M voxels, 780M), opening Flight made it with 70-94 ms of
  the GPU's generate kernel and 37-87 ms of its light kernel over 3 frames, entering play mode made it again (the editor
  unloads the scene and loads a copy), leaving play mode once more; a player made it at its first frames (586 ms of
  generate on a first run: the noise kernel compiles then). `VolumeCache` (engine) keeps them:
  - Keys that stay the same across runs and machines (`VolumetricObjectBase.DensityKey`: SHA-256 of
    `VolumeFile.GeneratorVersion`, every setting the density depends on and the grid; `LitKey`: the density key plus
    Density, ShadowReach, NoiseSize, TauMax; the sun is stored beside the lit voxels and compared within `SunTolerance`).
    .NET's HashCode (ShapeHash and co.) is seeded per process: never a file key. Bump `GeneratorVersion` when the
    generate or light kernels change what they make.
  - GPU: `VolumetricData.Dispose` parks a complete cloud's density, occupancy, lit voxels and CPU copy
    (`VolumeCache.Park`, kept `ParkSeconds` 120, at most `MaxParkedBytes` 768 MB); the next object's first build takes
    them (`TryAdopt`: drawn at once when the lit key and sun fit, else relit), then works out its lights' transport.
    Play / stop in the editor: `[Volumetrics] Space Storm: kept ... (nothing made)`.
  - Files (`VolumeFile`, ".lelvol"): density R8 and lit RGBA8 at level 0 with the lit key and sun, in chunks of 8 z slices
    compressed apart (Deflate, written and unpacked in parallel; the storm's 63 MB in 25 MB); mips and occupancy are made
    after the upload. `VolumeCache.Folder`: the editor's `Library/VolumeCache` (`Writable`: a cloud the GPU made, still
    drawn `WriteDelay` 1 s later, is read back through pixel buffers and a fence, no stall, and written on a worker thread;
    files unused 30 days or past 2 GB go when a project opens), a player's `Data/Volumes`. An object read from a document
    starts its file's read at once (`ISerializationCallbackReceiver.OnAfterDeserialize` -> `Prefetch`, the loading
    thread included), so it runs beside the rest of the scene's load; its first build takes it (`WaitForFile`) and
    uploads it at most `UploadBytesPerFrame` (32 MB) a frame (phase `Upload`); the CPU copy for `DensityAt` comes from it.
    `Rebuild()` always makes the cloud. Frames of the storm made and read: identical bytes (scene view dumps).
  - Builds: `VolumeBaker` (step 4b' of `GameBuilder`) lists the volumetric objects of the build's scenes (prefab instances
    too) and Resources prefabs as detached copies (`DocumentValues.FromNode`, prefetch suppressed), copies their files from
    `Library/VolumeCache` into `Data/Volumes`, and makes the missing ones' density when it has the GL context (batch:
    `MakeDensity`; the player lights them: the lit voxels need the scene's sun). Log: `Volumes: 1 clouds (1 from the
    editor's cache, 0 made now), 23.8 MB.` `GameLoader` sets the folder before the scene is read.
  - Measured: editor open from the file 31 ms of the GPU for the upload (mips and occupancy included) instead of 108-180 ms;
    play and stop nothing; the player (warm shader cache) storm ready 471 ms after its build began (upload 36 ms) against
    1081 ms made (generate 586 + light 77 ms), and no density readback. Each object's first cloud logs
    `[Volumetrics] <name>: made|read from its file|kept ... in N frames, T ms`; `LEL_VOL_PROFILE=1` adds every phase's GPU
    time (the GPU drained around each step: measuring only) and the readback's.
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
  to, samples)` (CPU, through the voxels), `Rebuild`, `Describe` (grid, memory, state), `GridFor`, `CloudVersion`
  (2026-11-09: a number that changes whenever another density is drawn, `VolumetricData.DensityVersion`, made or taken
  from a kept cloud; 0 while not ready: scripts placing things by density redo them on a change without hashing the
  `DensityKey`, a SHA-256).
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
  storm as the HQ preset, `volgi=0` the volumes without GI, `voltemporal=0` without their temporal filter, `vollight=1` a
  lamp (scene Light) flying through the storm (2: stopping at 6 s, 3: still), `volbig=1` the storm with New Frontier's
  voxel count; editor hook `dragselected=N;dx,dy,dz;frames` drags the selection through the gizmo's path; `vsync=1`
  paces TestGame by the display as the editor is (motion tests at a game's frame rate, with `autocam=1` and `dump=N`).
- Measured on this machine's AMD Radeon 780M (OpenGL, 1280 x 720): the four test volumes 0.33-0.43 ms, inside the fog bank
  0.35 ms, HQ at the dense storm's centre 2.5 ms (5.1 ms with `MinStep` 0.1, the same image); a backdrop cylinder of
  3740 x 892 x 3740 at 10-unit voxels (12.6 M voxels, about 69 MB) filling a 1253 x 709 view 0.54 ms, 0.61 ms with its
  8 lights lit. TestGame lumen, vct, gi=0, refraction, materials against the commit before: identical (max 1 / 255;
  trails differ from run to run).
- Since 2026-11-07 (temporal filter on both presets, scene shadows, GI, scene lights, RGBA16F transport) that backdrop
  (New Frontier's storm, Lumen) costs 0.90 ms calm and 1.04 ms with its 8 flash lights lit: the temporal filter 0.08 ms,
  GI + shadows 0.18 ms, the rest the wider transport and the distance target; the GI grid 0.11-0.15 ms in the
  `Volumetrics` pass. TestGame lumen, vct, gi=0, refraction, materials against the commit before: identical (max 2 / 255).

## Volumetric objects: more lights, light into the GI (2026-11-16)

The user's three asks: the sun's Affects Volumes did nothing; many lights in a cloud (lamps of a station in a storm) no
longer got into it ("how far can the count go before performance drops?"); clouds should contribute to the GI, with
switches.

- The sun's switches: a cloud took its sun (`Sun` or the main light) as colour x strength x `SunStrength`, whatever the
  light's `AffectsVolumes` / `VolumetricStrength` said (only the other directional lights and the local lights honoured
  them). `VolumetricObjectBase.SunRadiance` is the sun's light on the cloud now (zero with `AffectsVolumes` off, times
  `VolumetricStrength`); the march and the GI's media volume read it.
- Live or baked is each light's own setting, `Light.Mode` (`LightMode` Live, the default, / Baked; under Volumes, shown
  with Affects Volumes; `VolumeMode` / `LightVolumeMode` until 2026-11-19, see "Lights without a limit"), never the
  engine's choice (2026-11-17, the user's rule: "nie dawać silnikowi decydować"; until then the longest ranges took the
  live channels, so a shield's short break flash near the game's storm joined the
  baked lights and reworked all 22 of them as it came and as it went: 8 and 2 ms of GPU in one frame each, a hitch in
  combat). `MaxLights` 8, `MaxBakedLights` 64; `GatherLightSlots(slots, baked)`: live = the cloud's own `Lights`, the
  scene's other directional lights, then the Live scene lights that reach the shape in `Light.Active` order (the order
  they were enabled: a light that comes later never takes another's channel); live lights past the 8 channels are left
  out of that cloud (`VolumetricObjectBase.LiveLeftOut`, a warning once per cloud, and the inspector's line says how
  many). A script that makes a light sets its mode right after `AddComponent`: clouds gather their lights when the
  frame renders, so it is never in the wrong set for a frame. Baked = the Baked lights that reach the shape, nearest
  first, summed with their colours into one RGBA16F texture at the transport's resolution (`VolumetricData.LightsC`;
  `VolumetricLightTransport.shader` variant `VOL_BAKED`, 16 lights a dispatch, later pages add to the texture; build
  phase `Baked` after `Lights`). Any change of a baked light (place, range, colour, intensity, coming or going:
  `VolumeLightSlot.BakedColor` is in `LightsHash`) reworks its part: in place when the boxes fit the frame's budget
  (`TryUpdateLightsInPlace`: the lights are matched by their `Light`, in any order; a light that came adds the box it
  reaches, one that went the box it reached, each box baked again with the lights there are now; one box around all of
  them or one each, whichever is fewer voxels; `BakeRegion` bakes every light reaching a box), else as a build in the
  background. A change of the live set (a light's mode switched, a live light coming or going) is a build in the
  background. A build that only changes one kind keeps the other's textures (`KeepLive` / `KeepBaked`). The inspector's
  line (`Describe`) counts both and their textures. TestGame's lamps (`vollamps=N`) are Baked, `vollamplive=1` Live.
- Dynamic lights (`LightMode.Dynamic`, 2026-11-17, the user's ask: lights moving every frame with each bullet):
  worked out in the march (`VolumetricMarch.shader` variant `VOL_DYNAMIC_LIGHTS`), with nothing made beforehand, so they
  move, come and go every frame for nothing. `VolumetricObjectBase.GatherDynamicLights` (each draw, per cloud and camera):
  the Dynamic lights whose range reaches the shape, the nearest to the camera first, at most `MaxDynamicLights` 128
  (the rest left out, warned once); `VolumetricRuntime.SetDynamicLights` writes them into a std140 uniform buffer on
  binding 8 (`VolDynamicLights`: local position + range, radiance x volumetric strength + source radius, spot direction
  + cos outer, spot scale; orphaned per cloud). The pixel's ray first keeps up to 16 (`VOL_DYNAMIC_PER_RAY`, eight-bit
  indices packed in a uvec4) whose range sphere it crosses inside the shape and in front of the scene; every step that is
  not an empty block adds `VolDynamicStretch` for each of them, at the end of the step (after its own light: fewer
  registers live): the light it scatters along a stretch of the ray from where the last stretch ended (an empty block
  moves that to where the ray leaves it) to the end of the step's cell of the grid without the jitter (t - jitter dt ..
  + dt), so the stretches are the same every frame and nothing of it moves with the jitter. The scene light of the
  transport (falloff fade^2 / (d^2 + r^2) in world units, cone, e^-tau + glow e^-0.15 tau, tau from two samples at 1/4
  and 3/4 of the way at the level of their spacing; the transport takes five): 1 / u (u = b^2 + x^2 in units of the
  range) integrated exactly over the stretch (one arctangent for the angle the stretch spans), and fade^2, the cone, tau
  and the density (with the detail's erosion) at the stretch's centre of light (the 1 / u-weighted mean); the light
  through the cloud in front is the step's, raised back over the step with its density. Their light goes to the march
  target's third attachment (`FragDynamic`, RGBA16F, added the first time a cloud has dynamic lights) and the composite
  adds it after the temporal filter (`volDynamic`, unit 3, `volDynamicActive`), upsampled as the rest.
  Not in the transport (`GatherLightSlots` skips them) nor in the cloud's light in the GI (the media volume). The
  march's variants are 16 now (`MarchProgram(hq, lights, baked, dynamic)`), a preset's 8 made together. The inspector's
  line counts them ("N dynamic").
  - Ghosting (2026-11-17, the user: "tragiczny ghosting" behind New Frontier's PhotonBullet light, range 72, source
    radius 0.1, in the Flight storm, VolumetricObject at full resolution: even steps of ~28 units): the first version took
    one sample of the light a step, so a small light's peak was hit or missed by up to half a step (per-frame noise of
    hundreds of percent), the temporal filter's 3 x 3 clamp opened to that noise and kept the old glow for ~10 frames
    (HistoryWeight 0.9). Now nothing of a dynamic light goes through the history (no trail by construction), and the
    exact integral keeps it smooth without it: frame-to-frame change around the resting bullet 10.6 -> 1.1 mean, p99
    55 -> 6 (0..255). The exact integral is also brighter where steps are long against the light (the old per-step weight
    T (1 - e^-k dt) under-counts light at a point: ~0.85 of it in TestGame's lamp scene; Dynamic is now ~1.1x Live
    there, was 0.94x). Tried and dropped (in the
    march's comment): working each light out once after the march (two sides or six equal-angle segments), which either
    needed the light through the cloud kept along the ray (a profile of 8 knots: +0.1 ms on every ray of the cloud) or
    missed the clumps (two density samples a light: holes, a third of the light).
  - Noise (same day, the user's screenshots of dither and squares in lit clouds, "jeszcze gorzej"): a cheaper version took
    the density from the step's own sample (except for the stretch holding the peak) and let steps without cloud hand
    their stretch to the next: both move with the jitter, and with no temporal filter on this light that showed as dither
    and as the occupancy blocks' squares. Hence the grid without the jitter and the density at every stretch's centre of
    light. TestGame's lamp in the clumpy storm: frame-to-frame change 0.4 (0..255, temporal filter on).
  - Its cost (TestGame, 780M, 1280 x 720, the clouds' draw over no lights; the GPU shared with another process, so the
    absolute times vary run to run): the lamp +0.29 ms, 64 tracers +1.0 ms (the first version: +0.06 / +0.17 to +0.35).
    New Frontier's Flight with the bullet 15 units from the scene camera (its reach covers the view), an earlier
    version of the fix: 7.3-7.9 -> 7.9-8.1 ms. Tried for less, no gain or worse: the third target is free; reading the
    density only at the peak (cheaper, but the noise above); evaluating only steps with cloud (as cheap, twice the
    noise); stretches merged to two voxels (dearer: a second call site); a midpoint rule for far stretches; the fade's
    polynomial integral replaced by fade^2 at the centre of light (kept: simpler).
  - Verified (TestGame, night, the storm from inside): the same lamp (`vollight=3`) Live and Dynamic: the frames look the
    same, Dynamic 0.93-0.94 of Live's light (the two-sample tau). Tracers (`volbullets=N`: lights of range 6 crossing
    the storm at 60 u/s, `volbulletshadows=0` their Volumetric Shadows off): the clouds' draw (`Transparent.Volumetrics`,
    780M, 1280 x 720) 0.76 ms with none, +0.14 / +0.26 / +0.44 ms with 16 / 64 / 128; 20 lamps Dynamic 1.07 ms against
    0.90 Baked. CPU: +1.2 ms a frame for 64 moving lights, of which ~0.65 ms is `UpdateLightTransmittance` (each moved
    light's six-way cloud transmittance for surfaces, for the up to 32 lights that shine): bullets should have Volumetric
    Shadows off. The rest is per-light bookkeeping (light list, slots).
- The march's lights are compile-time variants (`VOL_LIGHTS`: the two live textures, `VOL_BAKED_LIGHTS`: the baked one;
  `VolumetricRuntime.MarchProgram(hq, lights, baked)`, a preset's four made together): a fetch switched off by a
  uniform in the loop still cost 0.14 ms of 1.45 on the 780M (the driver flattens the branch).
- `VolumetricRuntime.UpdateLightTransmittance` (each light's six-way transmittance through the clouds, on the CPU) is
  worked out again only when its key changes (the light's place and range, the clouds in reach: which, their
  `CloudVersion`, transform, glow, density; `Light.CloudTransmittanceKey`): it was 144 voxel samples per light and
  cloud every frame, 1.6 ms of the CPU for thirty lamps in a storm.
- Measured (780M, 1280 x 720, TestGame `volumetrics=1 volbig=1 gi=0 camera=0,17,2 look=0,14,42`: a storm of 11.6 M
  voxels marched at half resolution filling the view): no light lit 1.16 ms; the eight live channels lit + 0.06-0.11 ms
  (two fetches a step); the baked texture + 0.1-0.2 ms for any number of lights (9 / 30 / 60 lamps: 1.85 / 1.88 /
  1.86 ms against 1.77 with eight live), 13 MB there (16.5 MB for a 400 x 90 x 400 cloud). More live channels would
  have cost a texture of that size and about 0.05 ms per four lights, and 0.7 voxels of work per light and voxel on
  every rework. A baked light that changes costs its box: thirty lamps of range 9 pulsing every frame 2.1 ms in the
  `Volumetrics` pass, of range 3 0.7 ms; steady ones nothing, and the CPU went from 2.43 to 1.89 ms a frame with thirty
  lamps (the transmittance key). So baked lights suit lights that stay as they are; ones that flicker should be Live
  or the cloud's own. With the storm's own lights lit or dark and eight
  scene lights or fewer the frames are the commit before's bit for bit (TestGame `gi=0 volumetrics=1 volflash=0|2
  camera=0,17,2 look=0,14,42 dumpframe=400`: max 0 of 255).
- TestGame: `vollamps=N` steady scene lamps on rings in the storm (`vollamprange=`, default 9), `vollamppulse=1`
  their brightness changing every frame, `volflash=0|2` the storm's own lights dark / lit all the time.

Clouds in the GI:

- Switches: per object `ContributeGI` (on) and `GIContribution` (0..10, how much of its light the GI takes; 0 still
  hides); project `GlobalIlluminationSettings.VolumesInGI` (`VolumeGIMode` Off / Fast / Accurate, default Fast),
  `VolumeMediaSides` (on: a cloud as it looks from the reader's side, below), `VolumeMediaResolution` (64, 16..128)
  and `VolumeMediaReach` (8); a light's `AffectsGI` / `IndirectStrength` count for the cloud's sun and the live lights
  (`SetCloudLightUniforms(..., forGI)`, `VolumeLightSlot.IndirectStrength`; the baked lights' texture holds their sum:
  they count as seen).
- The media volume (`Volumetrics/VolumetricMedia.cs`, `Rendering/Passes/VolumetricMediaPass.cs`: Frame scope, after
  the shadow pass, one per renderer, published as `Lighting.Media`): a world-aligned cube of voxels, made again every
  frame. It covers the contributing clouds' boxes within `VolumeMediaReach` GI volumes around the GI volume's centre
  (clouds far larger than the GI volume count: New Frontier's storm is 4000 units, its GI volume 384); the voxel is
  that box's longest side over one less than the resolution, rounded up to a quarter of a power of two (a cloud that
  turns keeps its voxels; with all of the resolution the voxel more the cube needs doubled its side for one in ten
  sizes of cloud), the cube's side a power of two, the part in use in its middle (`MediaBinding.Used`, uniform
  `giMediaUsed`: the coarsest levels are centred on the clouds; with them in a corner the one voxel of the top level
  stood half the texture off), on the voxel grid (fixed in the world: nothing swims when a GI volume follows the
  camera).
  - `VolumetricMedia.shader` (one dispatch per cloud over the voxels its box covers, adding to the clouds before): the
    cloud's light as the march has it (sun with its orders and the scene's shadows, other directional lights, ambient,
    the GI it receives or the sky, emission, live and baked lights) at the phase's mean, the detail at its mean (the
    detail texture's coarsest level: thin clouds are much thinner drawn than their voxels say), times `Exposure` and
    `GIContribution`; a = how much of a path across the voxel the cloud covers (1 - e^-depth), rgb premultiplied.
    A second texture (`MediaSun`): of that light the directional lights' luminance (w) and that times the mean cosine
    of its scattering towards the light (xyz).
  - Levels, not a box filter (with box-filtered coverage a small thick cloud became a wide solid one in the coarse
    levels: thirteen times its light in wide cones). A level's voxel is the clouds in it as seen from outside, from
    each of the six sides: `FacePlus` / `FaceMinus` (RGBA16F, levels above the finest) hold the luminance seen from the
    + and - side of x, y, z (premultiplied by what is covered) and in a the coverage along x and along y; `Media` the
    mean light (rgb, its colour from the eight voxels below, scaled to the looks' mean) and the mean coverage of the
    three axes (a, so along z = 3 a - the other two); `MediaSun` from the eight below. The looks and coverages are the
    means of columns (`VolumetricMediaColumns.shader`): rows of finest voxels along each axis put together front to back
    from both ends (light from either end, coverage), a level's columns made from the level below's (two end to end)
    and as thin as two finest voxels, or a sixteenth of the level's voxel from where that is wider (the top levels'
    voxels would average thousands: 0.27 ms for the top level's one voxel). The first version put the eight voxels
    below one in front of the other: that takes the front voxel's coverage as spread evenly over its face, while a
    cloud's parts lie behind one another (an opaque ball covered 1.37 times its outline; a flat storm seen from its edge
    showed two thirds of every part through the one in front: its dark side 5 times too bright). Checked:
    `LEL_MEDIA_LOOKS=1` logs the six looks and three coverages from the finest voxels (CPU) and from the top level:
    exact with full columns; with the thin ones the storm's within 4-30 % (its darkest side 0.0082 for 0.0063, the
    mean 6 % over).
- Reading it (`Engine/GIMedia.glsl`): `GIMediaRead` (the level whose voxels are as large as the footprint, fractional;
  a path n voxels of that level long lets (1 - coverage)^n through, n at most 1, counting only the stretch's length
  within the part in use along the cone and, where that part is thinner than the voxel, a crossing of it as one: a
  coarse voxel is as large as all the clouds and its filter reaches a voxel further, so a stretch that ended above a
  flat storm took it in, and the next one again), the phase put back for the direction as a Henyey-Greenstein lobe of
  the stored mean cosine, once per cone at its first cloud; `GIMediaSegment` (a wide cone: the mean over its
  cross-section, and where that is thick a walk along its middle, front to back, halving its steps down to a voxel,
  whose light replaces the mean: a thick cloud shows its near side; a narrow cone: the walk alone); `GIMediaStride` (a
  stretch is 1.25 cone diameters, jittered 0.7-1.3 with the pass's cone jitter: equal stretches drew shells around
  every surface point); `GIMediaRange`; `GIMediaApply`.
  - Sides (the wide reads, from the second level on, `VolumeMediaSides`, uniform `giMediaSides`): the three looks the
    read faces and the three coverages blended by the squares of its direction's parts; their ratio is the clouds'
    light from there, the root of the coverages' squares so blended what they cover (an ellipsoid's outline: exact for
    one and a ball, a box shows up to 1.4 times more between its axes). Two more fetches a wide read.
  - A footprint larger than the whole volume reads the volume as one, as the cone sees it from its start: the share of
    the cone's solid angle the volume's face takes (face^2 / distance^2 / (2 pi (1 - cos half angle)) / 1.03), whole
    within 0.7 of the cone's angle from its axis and down to nothing at 1.3, seen from the cone's start towards the
    volume's middle (which side shows), and along the cone the share of the volume's length the stretch covers. The
    planar share it replaced (the cone's cross-section where it is read, fading over the footprint) took a 30 degree
    cone's angle as small (a flat storm read from up to 90 degrees off the axis) and the fast gather's 60 degree cone at
    a third of a ball's light; the fade along the axis took a small cloud in at 0.65 (stretches are longer than it).
- Voxel cone tracing: Accurate = the resolve's `GI_MEDIA` variant (`GIResolvePass`, three more programs, started ahead
  while the scene has volumetric objects: `Lighting.MediaAlongCones` / `MediaAlongConesExpected`): `ConeMedia` reads the
  next stretch ahead of the cone's samples, `ConeEscape` goes on through the clouds beyond the GI volume before the sky.
  Compiled in, because the reads switched off by a uniform cost an eighth of the resolve. Fast, and always the volume's
  bounces (`VoxelInject.shader`, `VoxelBounceCells.shader`) = `ApplyCloudsToGather` / `ApplyCloudsToCone`: one cone of
  sixty degrees either side of the normal (and one along the reflection), counted where the gather's own cones got out
  past the surfaces, in place of the sky there. Materials that trace themselves (transparent ones, effects, the
  cameras of a stack other than its lighting camera) take the clouds in the same way (`ApplyMediaToIndirect` in
  `Engine/VolumeLighting.glsl`). Reading the clouds along the bounces' cones cost 0.8 ms on New Frontier's Flight.
- Lumen: the screen probes' rays (`ProbeTrace.shader`: `TraceSurfaces` + `GIMediaApply`) and the radiosity rays
  (`CardRadiosityProbes.shader`) put the clouds in front of what they reach (`SampleRadianceCache` has an overload
  that returns how far that is). The radiance cache itself holds no clouds: the clouds' own GI reads its SH, and their
  light would come back to them. For the same reason `VolumetricData.UpdateGI` switches the media off for its cones.
- Texture units 23 to 26 (`Lighting.MediaTextureUnit`, `MediaSunTextureUnit`, `MediaFacePlusTextureUnit`,
  `MediaFaceMinusTextureUnit`; `Lighting.SetMediaUniforms`); the materials' further textures start at 27 (13 a material
  on 32-unit GPUs); a context with fewer than 27 units gets no media volume.
- Known answers (TestGame `volumetrics=1 night=1 linear=x voltest=N camera=0,7,-9 look=0,0,0`,
  `VolumetricTestScene.BuildLightTest`: a black, opaque cloud that gives off 1 over a grey plate of 0.5; `linear=x` = no
  tonemapping, exposure x; the plate's light in pixels 600..680 x 300..360 over what it should be; scratchpad
  `vol/known.ps1` of session 72b68768):
  1 a ceiling 240 x 6 x 240 (plate 0.5): voxel cone tracing 0.58 Accurate / 0.50 Fast, Lumen 1.13;
  2 a ball 6 across 10 above (0.045 under it): voxel cone tracing 0.82 / 0.70, Lumen 2.81; the same ball as an emissive
  mesh (4) 0.67, so cloud and mesh agree within the method's own error (Lumen over-reads small bright things: 1.9 for
  the mesh);
  3 the ball at 20: 0.99 (voxel cone tracing Accurate), 2.58 (Lumen).
  Before the columns and the solid-angle share: 0.48 / 0.31 / 1.17, 1.07 / 0.71 / 3.0, 1.25 / 3.0.
- A cone's read against the truth (`mediaview=1`: the volume itself, 2: as a cone reads it, `mediaaperture=x`, `mediafaces=0`
  without the sides; `VolumetricMediaDebugPass`; scratchpad `vol/conecheck.ps1`: the drawn picture's light over the
  sky integrated over the cone's solid angle through the frame's middle, pixels weighted by cos^3 of their angle,
  TestGame's camera has a 75 degree vertical field of view) for the storm alone (`volflash=2 night=1 linear=2.5
  volstormonly=1 look=0,10,42`, lit from inside near its top), a 0.577 cone: from its dark side (`camera=0,40,150`)
  0.72 with the sides, 2.07 without; from its lit side (`camera=0,40,-70`) 0.71 / 0.91; from above (`camera=0,130,41`)
  1.16 / 0.53. Without the sides the dark side read brighter than the lit one (it is half as bright). From above,
  narrower cones read less (0.71 at 0.1, 0.58 at 0.3: the coarse levels). A glowing ball (`voltest=2 night=1
  linear=0.8 camera=0,10,-25 look=0,10,0`): a Lumen ray (0.18) 0.90, a 0.577 cone 0.87.
- What it does not do: the coarse levels under-read a cloud as narrow cones see it (0.6-0.7, above), and an opaque layer
  thinner than the cone is wide (the ceiling). The sides blend by the squares of the direction's parts: seen at an
  angle, a flat cloud's top counts less than the share of it a camera sees (from 15 degrees above its edge 0.07 of the
  weight, where a box's faces would give it 0.22; the dark and the lit side still read alike, 0.71-0.72). Voxel cone tracing
  and Lumen differ by up to three times on small bright clouds. The fast gather has a cloud's light from all of the half
  space at once.
- Costs (780M, measured while another process used about half of the GPU, so high): the volume 0.2 ms (kernel) + 0.35-0.40
  ms (levels) a frame in TestGame (three clouds; uncontended the levels took 0.2), 0.08-0.14 + 0.31-0.45 ms on Flight
  (one); 9.7 MB at 64 (four RGBA16F textures and the columns), 128 about 2.5 ms and 80 MB. Resolve, TestGame
  1280 x 720 at half resolution (`camera=0,1.6,6 look=0,1.6,20` in the fog / `camera=0,40,150 look=0,10,42` afar): 1.89 ->
  2.04 Fast / 3.43 Accurate, 0.17 -> 0.17 / 0.21; the sides cost nothing measurable. Without contributing clouds
  nothing: the frames of lumen / vct / vct trace=voxel / gi=0 / materials=1 and the storm without GI are the build's
  before the media volume (max 1-2 of 255, `dumpframe=400`; the storm with `gi=0` bit for bit), and the volumetric
  scene's with `volgiout=0` within its run-to-run difference when both are paced by the display (`vsync=1`: the fog's
  detail drifts with the clock, so unpaced builds of different speed differ by 59 of 255).
- `LEL_PASS_STATS=1`: every renderer logs its passes' GPU times every 120th frame (`[PassStats]`), in the editor's
  views and in players (not in batch screenshots: their frames are drawn back to back and the queries are not in yet).
- Verified besides: the three trace modes (Fast and Accurate) and Lumen without shader errors; New Frontier's Flight in
  the batch editor (the storm takes 8 + 13 lights, three views with and without the storm in the GI: +1-3 % on the
  station, no artefacts: that storm is dark, albedo 0.1, and its flashes light the cloud at 3 %) and in the editor
  (`LEL_PASS_STATS`, no errors), its 510 tests, and a batch build (17 material variants, 112 stage sources through glslang).
- TestGame: `volstormonly=1` (of the clouds only the storm lights the GI: one thick cloud without the others' light),
  `mediafaces=0` (`VolumeMediaSides` off).

## Directional lights: several at once (2026-11-14)

- The user's ask: "the whole lighting system does not support several directional lights; add it to every built-in
  shader and every lighting mode, mind the optimizations". Before, only `DirectionalLight.This` (the active scene's
  first one) existed for the engine: three plain uniforms (`LDirectional.*`) set per program per draw.
- `DirectionalLight` (`Mono/Behaviours/DirectionalLight.cs`): `Color`, `Strength`, and per light `CastShadows`,
  `AffectsGI` + `IndirectStrength`, `AffectsVolumes` + `VolumetricStrength` (all on / 1 by default: a scene saved
  before reads them so). `Towards`, `Radiance`, `Shines`. `Scene.DirectionalLights` (enabled ones, scene order; no
  static registry: a preview or detached scene's lights never leak in), `SceneManager.GetDirectionalLights` (active
  scene first, then the other loaded ones).
- `Rendering/DirectionalLightBuffer` (static): `Update()` from `Lighting.BeginFrame` gathers the lights that shine
  (enabled, strength, colour), at most `DirectionalLight.MaxLights` = 4: `SceneManager.MainLight` first when it shines,
  then the others by luminance x strength (stable); `Shining`, `Main`, `IndexOf`, `CastsShadows(i)` (it asks, shadows
  are on, and it is one of the first `ShadowSettings.ShadowedLights` of the list that ask), `ShadowedCount`,
  `GIVersion` (a hash of what the GI takes: `LumenScene.SunChanged` compares it). A std140 uniform block on binding 6,
  2 vec4 per light (towards + flags, radiance + GI strength), uploaded once a frame; `EnsureBound` from
  `Lighting.SetUniforms`; `Forget` from the light's `OnDisable` / `OnDestroy` (no static list holds a light that is
  off). `Engine/DirectionalLights.glsl`: `LelDirectionalCount`, `LelDirectionalTowards(i)`, `LelDirectionalRadiance(i)`,
  `LelDirectionalGIStrength(i)`.
- Shaders written for one sun keep working: `LDirectional.*` (and `lightPosition` / `lightColor`) carry the first light
  of the list (`Lighting.Directional`), `SampleShadowRGB` / `SampleShadow` are light 0's shadows. No light shining:
  strength 0 (before, a removed light kept shining from straight above with its last colour). The engine's own shaders
  no longer declare `LDirectional`.
- Shadows (`Engine/Shadows.glsl`, `Rendering/ShadowUniforms`): one atlas holds every map; the layout is a uniform
  block on binding 5 (fade, biases, per light its first tile and tile count, per tile matrix + sphere + rectangle),
  uploaded once per shadow pass instead of ~12 uniform calls (and an array allocation) per program per draw. Plain
  uniforms left: `shadowsEnabled`, the samplers. `SampleLightShadowRGB(light, pos, normal, toLight)`,
  `ShadowLightHasMap`, `ShadowLightCascade`, `ShadowFade`. `ShadowPass`: the first shadowed light's cascades as
  before (`Cascades`, `MapSize`: a scene with one shadowed light has the atlas it always had), the others'
  (`AdditionalLightCascades` Four, `AdditionalLightMapSize` 1024) in a column beside it; every tile GPU-culled and
  drawn as before; transparent casters asked for once per pass (`TransparentShadows.HasCasters` / `DrawCasters`).
  `Lighting.ShadowCascades` (`ShadowCascadeData`: `TileCount`, `LightFirst`, `LightCount`, `Matrices`, `Spheres`,
  `Rects`), `Lighting.CameraShadows`. Project Settings > Lighting > Shadows shows the three new settings under
  "Several directional lights".
- Lit shaders (`Lit`, `LitTextured` all three workflows, `LitTransparent`, `VFX/VfxLighting.glsl`): a loop over the
  lights, each behind its own shadows; no shadow lookup for a light the surface faces away from (also the one-light
  case: the opaque pass got ~0.08 ms cheaper in the test room). `StandardBRDF.glsl`: `LelStandardBRDFIndirect` (the
  GI's part) + `LelStandardBRDFDirect` per light replace the one-light `LelStandardBRDF` in `LitTextured` (kept for
  project shaders).
- GI: `VoxelInject.shader` and `CardLighting.shader` add every light with a GI strength, shadowed by
  `Rendering/GridShadowMap` (now shared by `VoxelGIPass`, which had its own copy): one grid-covering tile per light
  that both goes into the GI and casts shadows, side by side, its own `ShadowUniforms` bound for the dispatch (the
  camera's is bound again by the next `Lighting.SetShadowUniforms`). No tile and no caster drawing for lights out of
  the GI, without shadows, or with shadows off.
- Volumetric objects: a cloud's sun stays `Sun` or the main light (the lit voxels; since 2026-11-16 its
  `AffectsVolumes` and `VolumetricStrength` count there too, `VolumetricObjectBase.SunRadiance`). The other shining lights with
  `AffectsVolumes` take slots of the lights' transport (with `SceneLights`; after the cloud's own lights, before the
  local scene lights): `VolumeLightSlot.KindDirectional`, the slot holds e^(-tau / 4) of the cloud towards the light
  out to the shadow reach (`VolumetricLightTransport.shader`), the march rebuilds e^-tau and the softer orders from
  it and scatters it as the sun (albedo, phase, `SunStrength` x the light's `VolumetricStrength`), from the light's
  exact direction; the slot's direction is snapped to ~0.35 degrees (`SnapDirection`) so a slowly turning light
  reworks the transport a step at a time. Scene shadows fall into a cloud from its sun's maps when its sun is any of
  the shadowed lights (`volSunShadowLight`), not only the main light.
- Editor: `DirectionalLightInspector` (below the fields: "The main light (1 of 2 directional lights)" / "Shining" /
  "Not shining: more than 4..." / "Off", and "Casts shadows" or why not; a ring and rays along the light in the scene
  view while the component is expanded).
- Fixed on the way (it was in HEAD): the `ShadowTransmittance` sampler got its unit only while the frame had a
  transparent shadow caster; without one it stayed on unit 0 beside a sampler of another type and the GI's lighting
  program did not run: no light went into the voxels (TestGame `gi=vct glass=0`, the outdoor yard: an empty
  radiance volume, no bounce at all) or the cards (New Frontier's Flight in edit mode: no sun bounce).
  `TransparentShadows.SetUniforms` and `Lighting.SetShadowUniforms` / `GridShadowMap.SetUniforms` now give the shadow
  samplers their units always (also found as: night scene, no light shining, VCT lost its local lights' GI).
- TestGame: `suns=N` (the sun and N - 1 coloured lights from other sides), `sunshadows=N`, `suncascades=1|2|4`,
  `sunmap=size`, `sungi=0`, `sunvol=0`, `suncast=0`, `sunspin=deg/s` (`GITestScene.BuildExtraSuns`, every test scene).
- Verified (780M, 1280 x 720): one light against the commit before (`animate=0 dumpframe=400`): gi=0, vct (also
  trace=voxel, voxmips=sh / iso), lumen, outdoor, lights, materials, refraction, night within 0-5 / 255 where the
  frame has transparent casters (without them the GI is brighter: the fix above; volumetrics, vfx, trails differ by
  their own run-to-run noise, measured head against head). 2 / 3 / 6 suns: own shadows per light, the budget
  (`sunshadows=1`: the second unshadowed), 6 asked: the 4 brightest; the second sun's bounce in Lumen and VCT
  (`sungi=0` removes it), the storm cloud lit by it (`sunvol=0` removes it). 195 keyword variants of the lit shaders
  pass glslang (3 GPU kinds) and 390 programs link (scratch probe `VariantCheck`); New Frontier's 16 own shaders
  (planets reading `LDirectional`) validate and link; its 510 tests pass; its Flight scene in the editor equals the
  commit before plus the sampler fix exactly.
- Cost (same machine, the test room): one light is not slower (gi=0 GPU 1.25 -> 1.17 ms, vct 8.94 -> 8.73, lumen
  6.34 -> 6.25). A second light without shadows: +0.01 ms. With shadows (4 cascades of 512): lumen 6.25 -> 7.03 ms
  (Shadows +0.33, Opaque +0.10, SurfaceCache.shadow +0.16), vct 8.73 -> 9.97. Four lights all shadowed: lumen 8.38,
  vct 12.10. Volumes: 3.23 -> 3.38 ms with four lights.

## Local lights (Light component; 2026-11-07)

- `Light` (`Mono/Behaviours/Light.cs`, global namespace, `[ExecuteAlways]`): `Type` Point / Spot (`SpotAngle`,
  `InnerSpot`, shown in the inspector for spots only: `[ShowIf]`), `Color`, `Intensity` (radiance units as a directional light's Strength: a white surface facing the light one
  unit away reflects Intensity), `Range`, `SourceRadius`; GI: `AffectsGI`, `IndirectStrength`, `GIShadows`; volumes:
  `AffectsVolumes`, `VolumetricStrength` (HDRP's Volumetric Multiplier: a flash can light the ships around brightly and
  its cloud less), `VolumetricShadows` (clouds between the light and what it lights dim it). Falloff (`Falloff(d)`, the
  shaders' formula): fade^2 / max(d^2, SourceRadius^2), fade = saturate(1 - d^4 / range^4) (Frostbite's window); a spot's
  cone saturate((dot(-L, forward) - cos outer) x 1 / (cos inner - cos outer))^2. The direct light has no shadow maps
  (geometry does not block it on surfaces); the GI's injection does block it.
- `Rendering/LightBuffer` (static): every frame (`Renderer.RenderFrame`, after `Lighting.BeginFrame`) every light that
  shines (enabled, intensity, range, colour), however many, in `Light.Active` order: a std430 storage buffer on binding
  16 (`ListBinding`; grown by doubling), 6 vec4 per light (position + range, radiance + GI strength, spot direction + cos
  outer, spot scale / flags / source radius, the clouds' transmittance towards +x -x +y and -y +z -z), and its header,
  a std140 uniform block on binding 7 (`LelLightBlock`, 128 bytes, `WriteHeader`: the count, the clusters' grid, the
  camera's view-projection, depth row and slicing). `Update` binds the plain header (no clusters: y = 0, every light
  is visited) with an empty mask buffer on 17; `UseClusters` / `UseNoClusters` switch the header and masks bound, and
  `Bind` / `EnsureBound` (called by `Lighting.SetUniforms` for every draw: programs drawn before the first frame never
  read an unbound block) bind the ones in use. `Count` is what was uploaded (`Forget` takes a light out of `Shining` at
  once, the buffer keeps it until the next update).
  A light disabled or destroyed leaves the list and the GI tracking at once (`LightBuffer.Forget` from `Light.OnDisable` /
  `OnDestroy`; its reach waits in `giGone` for the next frame's change): static lists must never hold a light that is
  off (see "The GC's own roots": it held an unloaded scene and the old scripts).
  `Engine/Lights.glsl`: `LelLight`, `LelLightCount`, `LelLightHas`, `LelLightFalloff`, `LelLightCloudTransmittance` (the
  six axes blended by dir^2), `LelLightShape`, `LelLightAt`; the walks over lights (`LelLightIterator`, `LelNextLight`):
  `LelLightsAt(position)` (a cluster's), `LelAllLights()`, and with `LEL_LIGHT_GROUPS` (compute shaders)
  `LelLightGroupCullBox` / `LelLightGroupCullPoints` + `LelGroupLights()`. A loop over the lights is
  `for (LelLightIterator lights = LelLightsAt(p); LelNextLight(lights);) { int i = lights.light; ... }`.
- Lights without a limit (2026-11-19). The user's report: "with many dynamic lights in the volumetric, the baked lights
  disappear; why do they share a limit?". The clouds always had a pool per mode (8 Live, 64 Baked, 128 Dynamic), but the
  shaders' list was one pool of 32 lights, those whose range came nearest to the camera: New Frontier's bullet lights
  (Dynamic, range 72: the camera inside their reach, score 0) took every place, and the station's lamps (Baked) further
  away left the list, so their light on surfaces and in the GI went out (their part of the clouds' baked texture
  stayed). A first fix gave each mode its own 32 (and the user had `VolumeMode` renamed, as it then decided the
  surfaces' list too: "no to zmień nazwę z volume mode"); the user rejected it ("what are separate limits for, if the
  limit is shared anyway?": one buffer every pixel looped over whole, its parts' places wasted) and chose clustered
  lighting with no limit at all. `Light.MaxLights` is gone. `Light.Mode` / `LightMode` kept the new name and went back
  under the Volumes header (it concerns only the clouds again; at the top, Baked read as Unity's baked surface light).
  The serializer has no former names: the documents' `"VolumeMode"` keys (and their `$types` lines) were renamed in
  place to `"Mode"`; a document still holding `"VolumeMode"` loads its lights as Live.
- Clusters (`Rendering/Passes/LightClusterPass`, `Engine/LightClusters.shader`): the first pass of every camera
  (`EveryCamera`, also in `AddTargetTexturePasses`) cuts its view into tiles of about 64 render pixels (even steps of NDC)
  x 32 slices of depth, logarithmic from the near to the far plane (the first reaching back to the camera, the last to
  1.01 x far), and gives each cluster a bit per light whose range sphere reaches the cluster's view-space box (the
  bounds of its part of the view, through the inverse projection, with 0.002 x its far depth of room). On the GPU, one
  thread per cluster and 32 lights, into a storage buffer per camera of the frame (output on binding 18: VFX caches
  what it bound on 0..15; a later camera never writes masks an earlier one's draws read), then `UseClusters`. A lit
  point finds its cluster from its world position through the matrices of the camera the clusters were made for (the
  header), not from `gl_FragCoord`: any draw (an overlay camera, a preview, another renderer's camera) gets a cluster
  that holds every light reaching that point, and a point outside that view (or before any clusters) visits every
  light. Memory: tiles x 32 x ceil(lights / 32) x 4 bytes (1080p, 130 lights: ~330 KB); a pixel reads ceil(lights / 32)
  mask words and tests only its cluster's lights.
- Lit materials: `Lit`, `LitTextured` (Scene workflow: Blinn-Phong as the sun; Metallic / Specular:
  `LelStandardBRDFDirect` in `StandardBRDF.glsl`), `LitTransparent` and `Impostor` add the lights of their point's
  cluster after the sun (unshadowed).
- GI: VCT's `VoxelInject.shader` adds the GI lights to the voxels (`VoxelLightVisibility`: a march through the voxels'
  occupancy towards it, voxel steps growing to an eighth of the distance, at most 48; none with `GIShadows` off); Lumen's
  `CardLighting.shader` to the cards (`CardLightVisibility`: `TraceSdf` through the global distance field;
  `SurfaceCachePass` sets the SDF uniforms of the lighting program). `radiance.w` = `IndirectStrength` (0: not in the GI).
  Both cull the lights per work group (`LEL_LIGHT_GROUPS`, a mask of 256 words in shared memory, 8192 lights; more: every
  thread visits every light): `VoxelInject` against its 8^3 block's box a voxel wider, before any thread returns;
  `CardLighting` against the bounds of its texels' points (`atomicMin` / `atomicMax` on order-preserving float bits),
  its start reworked to find which texels hold a card without returning (barriers need the whole group).
- Checked (2026-11-19, the rule: no app runs) with two scratch harnesses of session eb8f85ec. `ShaderCheck`: glslang
  through the editor's `ShaderValidator` (a `ProjectReference` to the editor, reflection on the validator, a broken probe
  shader that must fail): Lit, LitTextured (Scene / Metallic / Specular, maps), LitTransparent (+ REFRACTION), Impostor
  (+ SPECULAR_MAP, MIXED_WORKFLOW, EMISSION_MAP), CardLighting, VoxelInject (ISO / ANISO / SH), LightClusters: 170 stage
  sources for three kinds of GPU, 0 errors (it caught `active`, a reserved word). glslang passed what NVIDIA's driver
  rejected in the user's Flight scene: `gl_WorkGroupSize` used (by the work group culling) before the shader's
  `layout(local_size_...) in;` (error C7594); GI compute shaders now declare their size before the includes.
  `GLCheck`: the same variants (with their instanced and own-pass programs) compiled and linked by the machine's real
  driver in a hidden 1 x 1 GLFW window, as the player's compile helper does (`ShaderCompileHelper.RunHelper`: GL 4.6
  core, `GLCapabilities.Initialize`, no program binary cache), plus a probe with the C7594 order that must fail: all
  linked on the RTX 5070 Ti (96 storage bindings, 16 fragment blocks); not checked on the AMD 780M. glslang checks the
  GLSL specification only; a driver can be stricter (or more lenient): compile engine shader changes with GLCheck too.
  NVIDIA also warned C7050 ("Li", "radiance" might be used before being initialized) in the GI loops of VoxelInject and
  CardLighting, already before this change (HEAD's VoxelInject compiled by GLCheck: the same warning): the test
  `giStrength <= 0.0 || !LelLightAt(..., Li, radiance)` is split in two now, and the driver compiles every variant
  without a warning.
  Not run in a scene by me: many bullets near the camera, the lamps keeping their light, the passes' GPU times.
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

## Camera shake (CameraShake, profiles, sources; 2026-11-12)

- The user's ask: camera shake as a built-in feature for every project (it was a game's own code): script calls for an
  impact and for a constant shake (start, stop), "other things that make it cinematic", and procedural, realistic
  profiles after handheld film cameras, with parameters and an overall 0..1 multiplier per profile. Their correction
  while it was built: a separate component that works on the transform, no state applied only while rendering ("so
  there are no inconsistencies later"). So: no renderer or scene hooks; `LELEngine/CameraShake/` is components and data only.
- `CameraShake` (component, global namespace as `Camera`): in LateUpdate it adds up the shakes playing on it and writes
  them to its own object's local position and rotation, and to its camera's `FoV`. The rest pose is whatever the transform
  holds that the component did not write (`TakeRest`: position and rotation compared apart, bit for bit, with what was
  written last): the pose at the start, and any pose something else sets later. So the object may hang under what steers
  the camera (the reliable way: order of scripts does not matter) or be set anew every frame before this LateUpdate
  (same-object setters that run after it overwrite the shake; ones that move it step by step from the shaken pose pile
  the shake up: not supported). With nothing shaking the transform is not touched; after a shake the rest pose is put
  back exactly (no drift). Offsets are in the object's own axes, in world metres whatever the parents' scale. Not
  `[ExecuteAlways]`: nothing moves in edit mode. `OnDisable` takes the shake off and ends its instances.
- Picture axes (`CameraShakePose`): position x right / y up / z forward, rotation x tilt up / y pan right / z roll
  clockwise. The engine's cameras look along +Z with +Y up, which puts the picture's right at -X (`Matrix4.LookAt` is
  right-handed): `LocalPosition` = (-x, y, z), `LocalRotation` = Qy(-pan) Qx(-tilt) Qz(roll).
- What plays: `Profiles` (list of `CameraShakeSlot`: profile + strength 0..1, playing all the time; another profile in
  an entry fades over; `StopAll` leaves them), `Play(profile, strength, fadeIn)` -> `CameraShakeInstance` until
  `Stop(fadeOut)` (constant: `Strength` / `Speed` set while it plays, followed over `Smoothing` 0.05 s so setting it
  never jolts), `Impact(strength, profile, direction, duration)` (one shot, ends by itself), and the world's shakes when
  `Listen`: static `ImpactAt` / `PlayAt` (position, radius; `InnerRadius`, `Falloff`, `PropagationSpeed`: a far camera
  feels it later, `PushesAway`: the kick pushes cameras away from it) and `ImpactEverywhere` / `PlayEverywhere`;
  `CameraShakeSource` is the component for those (Constant / Impact, `PlayOnEnable`, reach, animatable `Strength`;
  a profile given later starts it, another fades over). `Listener` (a Transform, 2026-11-12): the world's shakes are
  measured from it (distance, delay, push direction) instead of the camera's rest position; they still turn the camera
  in its own axes. For a camera far from what it shows (a top-down camera 100 units above the player: an explosion next
  to the player must count as next to it); a destroyed listener falls back to the camera. The world's list is static and moves on once a frame with the
  first camera's LateUpdate (`Time.frameCount`); with no camera enabled, impacts are dropped and only un-stopped constant
  shakes are kept (a stale impact would play to the next camera). An ended instance forgets its camera (`End`: whoever
  still holds it, the inspector's trial included, holds nothing of a scene).
- Camera's own controls: `Strength`, `RotationAmount` / `PositionAmount` / `FieldOfViewAmount`, soft limits `MaxAngle` /
  `MaxOffset` / `MaxFieldOfView` (untouched to 0.6 of the limit, then tanh towards it: stacked impacts never throw the
  camera about), `LensCompensation` (0: physical angles, a long lens shakes more in the picture; 1: same size in the
  picture as at `LensReference`), `Seed`. Static `GlobalStrength` (the player's setting), `Main`, `Active`, `World`.
  `Pose` (what was written), `Intensity` (0..1 from the shakes' levels, not their swings: for pad rumble, sound).
- `CameraShakeProfile` (ScriptableObject asset, Create > Camera Shake Profile): layers that add up, each a pure function
  of time and a seed (`CameraShakeNoise`: no state, so previews, tests and late arrivals agree, and the frame rate is not
  in it). Operator layers: `Sway` (5 octaves, spectrum 1 / f^(3 - 2 Roughness), amplitude coming and going), `Tremor`
  (narrow band, in spells), `Breathing` (uneven, in quicker than out), `Steps` (bob once a step with overtones, sway and
  roll once a stride), `Reframing` (holds, then one eased move: `Holds`, moves start at 0..0.7 of a cell and last at most
  0.3 of it, so they never run into each other); `Vibration` (3 octaves, not smoothed by the weight); the kick
  (`KickPosition` / `KickRotation` / `KickFieldOfView`: a damped spring's answer to a knock, normalized to its farthest;
  an impact with a direction pushes along it as far as the profile kicks). `Weight` is a 12 dB / octave low pass applied
  per octave (cutoff 14 Hz at 0 down to 1.3 Hz at 1), `Pivot` moves the lens with the turning (parallax), `Rotation` /
  `Position` scale per axis, `Strength` 0..1 all of it. Playing: `Attack`, `Release`, `Duration`, `Decay`,
  `IgnoreTimeScale`. Amounts are in units of "a careful operator": 1 of Sway is 0.42 / 0.5 / 0.22 degrees RMS of tilt /
  pan / roll and 12 / 8 / 9 mm; the Handheld preset comes to about 0.7 degrees RMS at 3.7 degrees a second (hand shake
  from the 1 / focal length rule is 1 to 2 degrees a second per axis).
- Noise calibration (scratch program, measured): one 1D gradient noise stands at zero on every lattice point, so
  `Signal` sums two on lattices that do not line up (offset per seed, 0.8731 apart in frequency): RMS 0.199 (x 5.025 =
  1), 0.573 zero-crossing cycles per cell (x 1.745 cells per cycle: the nominal frequency is the mean swing rate), peaks
  to 4.8 RMS.
- Presets (`CameraShakePreset`, `Load` / `Create` / shared `Get`): HandheldSteady, Handheld, HandheldTelephoto,
  HandheldWide, ShoulderRig, Documentary, Walking, Running, Shaky, Phone; Aerial, Vehicle, Engine, Rumble; Impact,
  Explosion, Recoil, Landing. A new asset is Handheld (`Reset`, same as the field initializers).
- Editor (`Inspector/CameraShakeInspector.cs`): a profile draws, above its values, a viewfinder that plays it over a
  drawn scene (far lines 5 degrees apart: turning; posts 2 and 3.5 m away: moving against them; Pause, Impact, Lens =
  the viewfinder's field of view), its measured size, and Preset + Load (undoable, saved); in play mode Impact In Game /
  Play In Game on `CameraShake.Main`. Also inline, where a profile unfolds (`[InlineEditor]`: the camera's list, a
  source). `CameraShake` in play mode: intensity, pose, what plays, Impact / Stop All. `CameraShakeSource`: Play /
  Impact / Stop, reach spheres in the scene view. ImGui's `Text` / `BulletText` take format strings: a percent sign in
  an interpolated string must be doubled (the level column vanished until it was).
- Verified in a game project (2026-11-12): 41 unit tests on stages stepped without rendering (rest pose exact after
  impacts and stops, an outside setter, a moving rig, directions of every axis, strengths multiplying, limits, frame
  rate independence 30 vs 120 fps, time scale 0 with and without `IgnoreTimeScale`, the list, world impacts by
  distance / delay / push, sources, field of view kick, lens compensation, preset sizes and order, step cadence, no
  jumps at 0.25 ms in any preset); the editor through the CLI (inspector screenshots, preview frames differing, Load +
  undo writing the asset file, the list with an inline profile, play mode: per-frame recording of Handheld = 0.8 degrees
  RMS with at most 0.14 degrees between frames at 60 fps, a world explosion pushing away and giving the field of view
  back, the rest pose after Stop); "Previous script assembly unloaded" after a play session with shakes; a `-build`
  ships the profile assets and the built player runs with them (it reads the two profiles with the scene; its shake
  was not measured there). Not verified: a real long session's feel (only measured, not watched).

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
- Drop down lists (`GenericDropdownMenu`, 2026-11-19, the user's report: "a click on the drop down takes the same click
  as choosing an entry"): the list opens on the field's press and an item acts on a release; the list went above the
  field when it ran past the panel's bottom, but only 24 px up (fields are 32 px in NF's theme), so it lay over the
  field and the opening click's release chose the item under the pointer. Now an item acts only after a press inside
  the list (`pressedInside`: the opening press, even dragged onto an item, chooses nothing), and the list is placed once
  at its first size: below the field, else above it by its real height when it fits, else on the roomier side, kept in
  the panel. Verified in NF's main menu (play mode, clicks through `BaseVisualElementPanel.ProcessPointerDown / Up` from
  a CLI `exec` probe): both settings fields open without choosing; a 30-entry test field at the panel's bottom opened
  above it, its opening press released over an item chose nothing, a click on an item chose it and closed the list.
- USS transitions of `display` and `visibility` (2026-11-19, the user's ask: menu views fading through each other, "if the
  UI system has a sensible animation system use it, else extend it"; it had CSS transitions for every interpolable
  property, with delays, easings and the Run / Start / End / Cancel events, but no keyword could take part).
  `StyleAnimationKind.Discrete` (`AnimValue.e`, the enum value), CSS's `transition-behavior: allow-discrete`: while the
  transition runs the shown keyword wins (flex over none, visible over hidden), so an element going to `display: none`
  stays in the layout until the end and one coming appears at the start, after its delay; on the raw progress, not
  eased. Only when transition-property names them (never through `all`, so existing `all` transitions are unchanged).
  The usual fade through: `transition-property: opacity, display` with the duration as the delay on the shown state and
  no delay on the hidden one (`opacity: 0; display: none`): toggling one class fades the old view out, swaps them in the
  layout and fades the new one in. New Frontier's main menu / settings do this (`Screens/MainMenu/MainMenu.uss`,
  `MainMenuScreen.SetSettings`; focus on `TransitionEndEvent` of opacity). Verified in the GUI editor (a probe sampling
  `resolvedStyle` each frame): out 0-150 ms in, both `none` at 150, in 150-300 ms, focus moved at the end, both ways.
  Batch editors update a runtime panel only when its view renders (a screenshot): per-frame UI checks need the GUI editor.

## PlayerPrefs and Application.persistentDataPath (2026-11-19)

- The user's ask: PlayerPrefs, kept on disk in the persistent data path, never in the registry; in the editor in a
  folder of the project's own outside Assets; `persistentDataPath` in the engine as well.
- `Application.persistentDataPath` (`Mono/Application.cs`): a built game's is `PersistentDataPathFor(boot.Title)` =
  LELEngine/<title> in the user's local app data (the player sets it first thing in `Run`; its Player.log fallback and
  `ShaderCache` were already there and now come from it); the editor's is the project's `PersistentData` folder beside
  Assets (`ProjectInfo.PersistentDataPath`, set when a project opens; batch editors too), in new projects' .gitignore and
  New Frontier's, kept when Library is cleared; other hosts (TestGame, tools): LELEngine/<executable's name>. Made when
  something writes. Setting it saves PlayerPrefs to the old folder and drops what was read.
- `PlayerPrefs` (`Mono/PlayerPrefs.cs`, namespace LELEngine, Unity's API): `Set/GetInt`, `Set/GetFloat` (finite only),
  `Set/GetString`, `HasKey`, `DeleteKey`, `DeleteAll`, `Save`, plus `GetKeys`, `FilePath`. One file, `PlayerPrefs.json`:
  `{ "ints": {}, "floats": {}, "strings": {} }`, keys sorted, text unescaped (relaxed encoder), written beside and moved
  over (`.tmp`), only when something changed (setting the same value is no change). Read lazily, thread safe (one lock).
  A key holds one type (setting another type replaces it; reading as another type gives the default). An unreadable
  file: a warning, a copy as `PlayerPrefs.json.unreadable`, empty values. Saved by `Save`, when the player's loop ends
  (after `host.Run`), when the editor leaves play mode (`ExitPlayMode`) and when it closes a project. Edit > Clear All
  PlayerPrefs (the log names the file).
- `Screen` remembers what the game sets (Unity's Screenmanager keys): `fullScreen` = and `SetResolution` write
  `Screen.FullScreen` / `Screen.Width` / `Screen.Height` and save at once, in the editor too (where they are not
  applied). The player opens its window with them (boot.json's size and mode are the defaults, `fullscreen=` wins; log
  `[Player] Window 1100x650 windowed (the game's last choice, PlayerPrefs)`); full screen opens at the display's own mode,
  so a smaller full screen mode is set again by `Screen.SetResolution` after the window is made. F12's toggle (`Window`)
  is not remembered.
- `InputManager.UserDataPath` (remapped controls) is `InputUserData.json` in `persistentDataPath` unless set: a built game
  had it under LELEngine/<executable's name>, the editor in Library; both now in the persistent data folder.
- Verified on a scratch project (`PrefsProbe`: counts its runs without calling Save, keeps a float and a string with
  quotes and Polish letters, asks for 1100x650 on its first run, quits at frame 40): editor play twice -> the project's
  `PersistentData/PlayerPrefs.json` with runs 2 and the screen keys, Clear All PlayerPrefs emptied it; the built game
  three times -> runs 1, 2, 3 in LocalAppData/LELEngine/SsaaProbe, the second and third opened at 1100x650; a truncated
  file -> the warning, the `.unreadable` copy, runs from 1. New Frontier's settings menu (editor play, Full Screen picked
  by pointer events) wrote `Screen.FullScreen: 1` to `NewFrontier/PersistentData`. Not run: a full screen start (it takes
  the display), a remapped control saved to the new path.

## UI text: distance field range, shadows and outlines (2026-11-11)

- The user's report: small text with `text-shadow` (SmokeOn's fps label, 15 px at panel scale 0.52) showed the glyph
  quads as boxes of the shadow's colour, "the same problem TextMeshPro had for years". Cause: the shadow and the outline
  are drawn from the glyph's distance field, which knew distances only up to `FontAsset.SdfSpread` (8 atlas pixels) and
  holds one value from there to the edge of the glyph's quad; on small text 8 atlas pixels are ~1.3 screen pixels, less
  than the blur, so the shadow stood at ~18 % at the quad's edge and stopped there.
- Now `SdfSpread` is 24 (the glyph padding follows: spread + 1), the atlas pages are R16 (`AtlasPage.pixels` ushort,
  `GlyphSdf.Render` returns ushort: the wide range keeps large text's edges exact), and `UIShaders` keeps everything
  drawn outside the glyph inside what the field knows (`known` = half its range in screen pixels - 1): the shadow is
  `smoothstep(-reach, reach, d)` with reach = 0.5 + blur px clamped to `known` (a blur wider than that at that size
  gets softer, never a box), the outline width is clamped the same way. Synthesized bold is in atlas pixels
  (`TextGenerator.SynthBoldAtlasPixels` 1.28: the old 0.08 of the 8-pixel range), so its weight did not change.
- Glyph rasterization: each row only tests the segments within the range of the row, rows of larger glyphs run in
  parallel (`GlyphSdf.RenderRow`): printable ASCII of a fresh font 19-37 ms (Segoe UI, Georgia, Impact, Consolas;
  with the wider range and the old loop it was 60-90 ms). One 1024 page still holds all of it.
- Verified: SmokeOn's HUD before / after (crops of the fps label), a board of 8-48 px labels with 3 px shadows, a hard
  2 px shadow, an 8 px blur on 12 px text, 2 px outlines at 10 and 24 px, synthesized bold (Impact) at 14 and 40 px;
  TestGame `ui=1` (title shadow, rich text, controls) looks as before.

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
- `MeshRenderer.forceRenderingOff` (2026-11-21, Unity's): while set the renderer draws nothing, as if disabled, in
  every list the frame builds (`Renderer`'s partition: cameras, shadow maps, GI; a static batch hides its part,
  `StaticBatch.Refresh`), and the scene view's clicks pick through it. A property over a `[NonSerialized]` field: never
  saved, the component stays enabled and the object active. For what a script hides for a while without changing the
  object's own state: an edit-mode preview that puts something else in a mesh's place used to have only `enabled` /
  `SetActive`, which a scene or prefab saved meanwhile (or the edit scene kept across play mode) took with it. Nothing
  puts it back but the script (a scene reloaded from its file, or after a script reload, has it off). Verified with a
  game's edit-mode preview in a batch editor (scene view captures: the hidden meshes gone while set, drawn again when
  cleared, the scene never marked changed) and its tests (the renderer stays `enabled`); not exercised: a static
  batch's part hidden this way.
- `Rendering/ICustomRenderer.cs`: a component (a game's script too) that draws a mesh it builds itself, shaped by its
  shader from per-draw values: `Material` and `PrepareDraw(out mesh, out block, out boundsMin, out boundsMax)`
  (false: nothing this frame). `TransparentPass.CollectCustomRenderers` takes them from the frame's behaviours on the
  camera's layers (active and enabled, a material whose program links), `GeometryPool.Prepare`s the mesh (code-built
  meshes are fine) and adds one entry each at the centre of its bounds (sorting; grab rectangles from the local bounds
  with the world matrix); drawn with the component's transform and the block on top of the material, no shadows, no
  GI. The inspector lists the material in the component's material section (`InspectorPanel`).
## Mesh Baker, MeshCombiner, mesh assets (2026-11-19)

- The user's ask: a "mesh baker" that merges meshes into one. First written as a component; the user's correction the
  same day: an editor window, "you select objects and bake in that window, and it replaces them all with one".
- `LELEngine/Mesh/MeshCombiner.cs` (public, works in a game too): `MeshCombiner.Combine(IReadOnlyList<CombineInstance>,
  name)` -> `CombinedMesh` (`Mesh`, `Materials` = one per sub-mesh, `InstanceCount`). `CombineInstance` = mesh, `Transform`
  (mesh space to the combined mesh's space, row vectors as `Transform.LocalToWorld`), `Materials` (a renderer's slots: the
  last repeats). One sub-mesh per material (by reference, in the order of first use; no material is one more), every
  instance's vertices appended whole (`baseVertex` per instance), the indices sub-mesh after sub-mesh. Normals go through
  the cofactors of the linear part (the inverse transpose without its division: no inverse to fail on a flattened object),
  times -1 for a mirroring transform; tangents and bitangents through the linear part; a mirroring transform (negative
  determinant) swaps two corners of each triangle. Instances fill their own ranges, in parallel from 65536 vertices.
  More than int.MaxValue - 64 vertices or indices throws.
- Mesh assets: `ModelAssets.MeshExtension` (".mesh", in `ModelAssets.Extensions`) = the imported model format
  (`LELMODEL`, what the import cache and builds hold) written as a project file: one node, one mesh, no materials (they
  are the renderer's). `ModelAssets.SaveMesh(assetPath, mesh)` writes it whole (a dot file beside it, then moved), makes
  the meta file, forgets the model loaded from the old file (`GeometryPool.Release` of its meshes) and returns the mesh of
  the file as loaded: the object to reference. Being a model file, everything that knows models knows it: the project
  panel (model icon, the mesh as a sub-asset row), Mesh fields and their picker, dragging it into the scene or the
  hierarchy (the default material), `BuildDependencies`, the build (shipped as it is, with its `.mesh.sdf`), the release
  of unused meshes. No importer reads it (`IsImportedFile`), `ImportOutdated` leaves it alone, and the inspector shows
  what it holds instead of import settings (`ModelImporterInspector.DrawMeshAsset`).
  - A reference names a mesh inside its file; a mesh asset is its one mesh whatever the reference calls it
    (`LoadMesh`: found by name, else the only mesh of a .mesh file), and `SaveMesh` over a file keeps the name it had.
    Found by the probe: another mesh asset copied over the file left the scene's renderers without a mesh.
  - `FormatVersion` (2) is now the format of project files too: a later version has to go on reading the versions
    mesh assets were written in (a cache or a build is made again, a mesh asset is not).
  - Changed on disk (a checkout, another program): nothing imports it, so `ModelAssets` keeps the stamps of the loaded
    mesh files (`AssetFileStamps.TakeChanged`, `TakeChangedMeshFiles`) and `EditorApplication.ImportChangedModels` reloads
    the scene around unloading them, as for a reimported model ("[Editor] Reloaded (changed on disk): ...").
- Editor: `Meshes/MeshBaking.cs` (internal: `Describe`, `Bake`, `MeshBakeRecord`), `Extensions/MeshBaker.cs` (public for
  editor scripts: `MeshBaker.Describe / Bake / BakeSelection`, `MeshBakeOptions`, `MeshBakeSummary`, `MeshBakeOriginals`),
  `Panels/MeshBakerWindow.cs` (Window > Mesh Baker, `IEditorPanel` "Mesh Baker", open state saved with the windows).
  - What is baked: the objects given without those under another of them, each with everything under it; renderers
    active, enabled, with triangles, not temporary (`HideFlags.DontSave`); of an enabled LOD group (under the objects or
    above them) only level `LodLevel` (clamped to the group's levels; a renderer in several levels counts for the one
    asked); renderers with a transparent material unless `IncludeTransparent` is off (on by default since the user's
    question the same day, "why doesn't it merge transparent ones? it should": it was off because one mesh's transparent
    parts sort as one object and blend in the mesh's order where they overlap on screen, a rare case; a renderer is taken
    whole or not at all).
  - Where the result stands: one object baked -> its parent, local transform and sibling index (the pivot stays);
    several -> under their parent when they share one (else the scene, in prefab mode the stage root), at the middle of
    the meshes' world bounds. The mesh is in the baked object's space (`renderer.LocalToWorld * baked.WorldToLocal`).
    Its MeshRenderer takes the first source's flags (shadows, GI, static) and the first object's layer.
  - The asset: `<Name>.mesh` in `Folder` (relative to Assets, made when missing; empty: the folder of the scene or of the
    edited prefab), never over a file (" 1", " 2"...). `Name` empty: the object's name, "Baked Mesh" for several.
  - Originals: Delete (default, "replaces them all with one"), Deactivate, Keep.
  - One undo step (`MeshBakeRecord`): the scene before and after as snapshots, the selection, and the asset: undo
    deletes the file and its meta (their bytes held by the record only while it is undone), redo writes them back
    under the same GUID. A file renamed or moved since is left where it is.
  - Refused with a reason (`MeshBakeSummary.Problem`, shown in the window, logged by `Bake`): nothing selected, play mode,
    the root of the edited prefab, a read-only object, no meshes, too many vertices, a folder outside Assets.
  - The window works out its summary when the selection or an option changes, and at most every 0.3 s while only the
    scene changes; Bake runs after the frame (`Defer`). `MeshBaker.IncludeTransparent` is a preference toggle (default on); the
    other options last the session.
- Not done: no texture atlas (materials stay as they are: one sub-mesh each), no re-bake of a baked object (the originals
  are gone; undo, or Keep / Deactivate and bake again), collision shapes are not merged (the originals' colliders go
  with them on Delete). One baked mesh gets one distance field and one set of surface cards: a large merged area is
  coarser in the GI than its parts were.
- A renderer with a mirroring scale drew inside out until the same day (see "Mirroring transforms" below); now it and
  its baked copy look the same.
- Verified: scratchpad `bake/BakeProbe` of session 40ffdb3b (`Assets/Editor/BakeProbe.cs`, batch `-executeMethod
  BakeProbe.Run`: 68 checks: the combiner alone (sub-meshes, counts, bounds, every triangle facing its normals under a
  mirrored and a squashed transform), a hierarchy with a non-uniform and a mirrored scale, a nested child, a LOD group, a
  transparent and an inactive object (same triangles, world bounds, surface and centroids before and after; pivot;
  sub-mesh per material), the asset and its GUID, undo / redo with the file, several objects with a name, a folder and
  each Originals, siblings under a turned and scaled parent, folders refused, the scene saved and opened again, the
  file changed on disk, `SaveMesh` over a file). GUI editor through the CLI: the window's elements (`input:Name`,
  `input:Folder`, `input:LOD Level`, `checkbox:Include Transparent`, `combo:Originals`, `button:Bake`), a bake of three
  selected objects, scene screenshots before and after (the same picture, but for the mirrored sphere), undo and redo.
  A player build of the probe's scene: the .mesh files shipped with their distance fields, the scene drawn. Not run:
  New Frontier's tests (the worktree held LFS pointers only). Not exercised: a bake in prefab mode, the refusals of
  play mode and of a read-only object. A mesh of millions of triangles (2026-11-20): the bake itself worked, its
  distance field's single dispatch crashed the editor through a driver reset; fixed in the atlas ("Builds in chunks,
  over frames" in the distance field section).

## Mirroring transforms: the mesh drawn wound the other way (2026-11-19)

- Found while testing the Mesh Baker: a renderer whose transform mirrors (a negative determinant: an odd number of
  negative scale components along the chain, as (-1, 1, 1) or (-1, -1, -1); (1, -1, -1) is a half turn, not a mirror)
  drew inside out: back-face culling kept the faces that pointed inwards after the mirror. Unity flips the front face for
  such objects; the user's ask was to fix it in the engine.
- Fix: such a renderer draws `Mesh.Mirrored` (internal, made on first use): the same vertices, every triangle with two
  corners swapped. `MeshRenderer.IsMirrored` (public: product of `lossyScale`'s components < 0, cheap, no matrix) and
  `MeshRenderer.DrawnMesh` (internal) pick it; `DrawGeometry` / `DrawSubMesh` (every per-object draw: opaque, prepass,
  shadows, transparent, transparent shadows, GI captures) and `GpuScene` (`Prepare` and the instance's index range in
  `Write`, so the GPU-driven buckets, prepass and shadow multi-draws) draw it. One winding per draw command keeps the
  buckets whole: no `glFrontFace` switching, no separate buckets. ICustomRenderer meshes in `TransparentPass` the same
  (by the world matrix's determinant).
- On the GPU a twin is only an index range over its source's vertices (`GeometryPool.Upload`: `MirrorSource` prepared
  first, `PoolBaseVertex` = the source's, `PoolVertexCount` 0); `GeometryPool.Release(source)` releases the twin first
  (its base vertex would point into reused space). A renderer that stops mirroring leaves the twin's indices in the pool
  until its mesh is released.
- Normals needed nothing: the shaders' normal matrix is transpose(inverse(model)) (`GpuScene.NormalMatrix`, the
  per-object uniform), right for a mirror. CPU users of `renderer.Mesh` (picking, navmesh, distance fields, Lumen cards,
  the Mesh Baker) keep the original mesh: winding does not matter to them, and `MeshCombiner` turns mirrored triangles
  itself.
- A first version recursed for ever in `GeometryPool.Release(mesh?.MirroredIfMade)` (null -> null): a stack overflow
  the Mesh Baker probe caught at once (exit 0xC00000FD).
- Verified on the probe project of the Mesh Baker section (`BakeProbe.BuildMirrors` / `CheckMirrors`: rows of spheres,
  cubes, a static row and a transparent row, columns plain, (-1,1,1), (-1,-1,-1), (1,-1,-1), under a parent scaled
  (-1,1,1); `IsMirrored` right for all 20), GUI editor scene view screenshots: every one solid and lit with its shadow,
  the same after the scene was thrown away and built again (twins released and uploaded again); the 68 Mesh Baker checks
  pass. Not compared frame by frame against the old engine (unmirrored renderers take the same path: `DrawnMesh` is
  `Mesh`), not looked at in the voxel GI mode separately (its passes draw without culling), no game view or player run.

## Static objects, static batching (2026-11-20)

The user's ask: a Static setting on objects, which a build's static batching uses; the renderer takes it from its object
and has none of its own; Static and Layer set on a parent ask in a popup whether the children change too. Added while it
was made: "static batching has to work in the editor at run time too" and "you could use the Library cache".
- `GameObject.isStatic` (per object, not inherited; setting it in a scene calls `Lighting.GI.InvalidateStatic()`).
  `MeshRenderer.IsStatic` is read-only and gives the object's flag. Files: `"static": true` on a JSON object entry
  (left out when false), a bool after the layer in binary documents (`BinaryDocument.Version` 4),
  `DocumentObject.Static` / `PrefabObject.Static` for the typeless paths, a prefab instance's override as the header
  property `m_IsStatic` (`PropertyModification.StaticProperty`, next to `m_Layer`: overrides, apply / revert, the
  inspector's override mark, `SceneMerge`).
- Files of before (`Serialization/LegacyStatic.cs`): a MeshRenderer's `"IsStatic": true` makes its object static, read
  typed (JSON, binary below version 4), typeless (documents converted for builds) and as an instance's modification or
  added component (path "IsStatic" becomes `m_IsStatic` of the renderer's object). Nothing writes the old field: a file
  loses the line when it is saved (Force Reserialize All Assets for all at once).
- Editor: `checkbox:Static` in the inspector's header after `combo:layer`. `EditorApplication.ObjectFlags.cs`:
  `RequestStatic` / `RequestLayer` ask when the object has children (popups "Change Static Flag" / "Change Layer":
  `button:Yes, change children`, `button:No, this object only`, `button:Cancel`), `SetStatic` / `SetLayer` are one undo
  step whatever the count (read-only children skipped). Automation: `EditorAutomation.SetStatic(path, value, children)`
  / `SetLayer`, CLI `static <object> on|off [--children|--self]`, `layer <object> <name|0..31> [--children|--self]`
  (an object with children and no answer: the command fails, as every action that would ask); `hierarchy` prints
  "(static)", `inspect` ", static".
- Static batching (`GraphicsSettings.StaticBatching`, Project Settings > Graphics > Batching, default on; batch
  `-staticBatching [true|false]`): `Building/StaticBatcher.cs` reads a scene document as generic data (prefab instances
  through their resolved prefabs and modifications) and merges the static renderers that draw one by one: transparent
  materials (any blending; not GrabPass) and opaque ones whose shader has no GPU-driven variant. Left as they are, each
  counted in the log line: opaque materials the GPU-driven path draws, renderers of LOD groups, shaders with the new
  render state line `Batching Off` (Unity's DisableBatching; `Engine/Impostor.shader` has it) or with their own depth
  passes, renderers mixing opaque and transparent materials, disabled renderers and inactive objects, meshes over
  `MaxVertices` (65,536). Groups: layer, Cast / Receive Shadows, Receive GI, kind (opaque together; transparent per list
  of materials); a group is ordered along a Morton curve through the parts' centres and cut at `MaxVertices`, so a batch
  is a cluster of neighbours; a part alone is no batch.
- A batch is an object "Static Batch N" at the middle of its parts: a `MeshRenderer` of the merged mesh
  (`MeshCombiner`, one sub-mesh per material, `ContributesToGI` false) and a `StaticBatch` (`Mono/Behaviours`: the
  parts' renderers, each part's index ranges in the mesh, its bounds' centre in the batch's space, whether it is flat).
  The parts stay on their objects (`MeshRenderer.Batch`): `Renderer.Partition` leaves them out of the cameras' and the
  shadow maps' lists while the batch draws and still gives them to the GI, so the lighting does not change. A part
  disabled, deactivated or destroyed is hidden in the batch (`GeometryPool.SetIndexRangeVisible` collapses its index
  ranges, `Mesh.HiddenIndexRanges`; applied by `StaticBatch.ApplyChanges` before a frame renders) and shown again when
  it comes back. A part given another mesh, other materials or a property block (`SetMesh`, `SetMaterials`,
  `SetMaterial`, `SetPropertyBlock`, or any edit of the renderer in the inspector: `OnValidate`) leaves its batch for
  good and draws itself (`StaticBatch.Leave`). A part that moves does not move in the batch (static objects do not move).
- Transparent batches keep the picture exactly: `TransparentPass.CollectBatchParts` makes an entry per visible part,
  sorted where the part's own renderer sorted (its centre, its id among equals), and `DrawBatchRun` draws the parts
  that follow each other in the order with one `glMultiDrawElementsBaseVertex` (`GeometryPool.DrawRanges`); something
  else sorting in between cuts the run. Two-sided materials: one call when the order of draws cannot show
  (`ShaderRenderState.OrderIndependent`: additive, multiply, screen without depth writes; or no blending with depth
  writes), else flat parts (all triangles facing one way) go together with culling off and the others back faces then
  front faces, one by one. A first version sorted a batch as one object: 230 sampled pixels differed by more than 8 of
  255 where alpha blended quads overlapped an additive batch, and alpha blended materials were left out; hence parts.
- Where the batches are made. A build: `GameBuilder` step 4a, per scene it ships; the batches' objects are appended to
  the built scene (`StaticBatcher.Append`), the meshes go to `Data/Library/StaticBatches/`; the log says per scene what
  was merged and why the rest was not. The editor's play mode (`EditorApplication.StaticBatching.cs`): the game's copy of
  the open scene gets them from its snapshot before it starts (`StaticBatcher.Apply`), and every scene the game then
  loads through the hook `SceneManager.SceneRead` (set only while playing); stopping unloads the batches' meshes
  (`ModelAssets.UnloadUnder`). The scenes in the project never hold batches.
- Library cache (`Building/StaticBatchCache.cs`, `Library/StaticBatches`): an entry per scene path and document kind
  (the file, the play snapshot) with the hash of the document it was made for (the batcher's version and the engine's and
  the editor's builds in it), the asset files the result depends on (prefabs, models, materials, shaders) with their
  stamps, and the batches' descriptions (`StaticBatchData`); good while all of that is the same, else made again in its
  place, so entries do not pile up as a scene is edited. Mesh files are named by the hash of their bytes
  (`ModelAssets.WriteMesh`): the same batch of another version of the scene is the same file, and a file never changes
  under something that loaded it. Meshes no entry names go an hour after their last use, entries unused for 30 days too.
  The meshes are referenced by path only (`AssetDatabase.StaticBatchPrefix` "Library/StaticBatches/", mapped to the
  project's Library by `AssetDatabase.StaticBatchFolder` in the editor and to the build's Data in a player).
- GPU-driven opaque renderers are not merged, measured (780M, 4,096 static cubes of Engine/Lit, a built game, medians
  of 150-180 frames): CPU work 11.5 ms a frame as they are and 10.4 / 13.5 ms (two runs) merged into 2 batches, GPU
  17.2 and 16.7 ms: nothing to gain. (A first measurement gave 136 and 157 ms: both were the surface cache's full
  atlas writing a console line per object and frame, next section.)
- Verified on a scratch project (scratchpad `static/` of session 0fc0dee3: `Probe`, editor script `StaticProbe`, game
  script `PartProbe`; 90 static renderers: additive and alpha blended quads, two-sided alpha cubes among them, one-sided
  quads with mirrored scales, a mesh of two materials, opaque unlit cubes, lit cubes, a LOD group, a Batching Off
  shader, prefab instances with an override of the flag). The flag: 33 checks (JSON, binary, JSON to binary to JSON,
  old files typed / typeless / converted, an old instance override, the commands with and without children, one undo
  each, prefab revert). Play mode in a batch editor: 75 renderers in 5 batches; Game view shots with batching on and
  off, as loaded, with parts hidden / destroyed / changed, and with them back: equal within the noise of two runs (max
  1 of 255). A built game with and without `-staticBatching`: the three frames bit-identical, 84.3 against 178.3 draw
  calls a frame. The cache: the second play and a new editor session from the Library (1-4 ms against 16-39), made again
  after a material's file, an object or a static part changed, old meshes deleted. Scenes the game loads (sync and
  async) batched. GUI editor through the CLI: both popups with their three answers, one undo, an object without
  children asked nothing, the setting written at once and undone, a renderer edited in the inspector while playing
  left its batch. Scripts unloaded after a play session with batches. New Frontier: 510 tests pass, Flight plays in a
  batch editor without errors (nothing is static there, so nothing is batched).
- Not done or not checked: the same scene loaded twice at once shares its batches' meshes (a part hidden in one copy is
  hidden in the other for opaque batches); a static part moved at run time stays where it was in its batch; only the
  780M ran it; no Linux player.

## Frustum culling of what draws one by one (ViewCulling, 2026-11-21)

- The user's ask: every renderer culled by the camera's frustum by itself (nothing draws that the camera does not see),
  optimized, working with the other features. Before, only the GPU-driven instances (`InstanceCull.shader`), particle
  systems (`VfxInstance.CollectDraws`) and volumetric objects (their screen rectangle) were; whatever the CPU draws one
  by one drew on every camera of its layers.
- `Rendering/Frustum.cs`: a struct of six world planes from a view-projection matrix; its box test is the shader's (the
  box's reach along each plane's normal), so the CPU and the GPU leave out the same things. Conservative (a box by a
  corner may pass), a NaN passes. `Rendering/ViewCulling.cs`: one per view. `RenderContext.Culling` is a camera's
  (`Renderer.CreateContext` begins it; every camera of a stack and every target-texture camera has its own);
  `ShadowPass` has one for the tile it draws, `GridShadowMap` one for its light. `IsVisible(MeshRenderer[, world])`
  stamps its answer on the renderer (`cullStamp` / `cullVisible`: one test per view whatever the sub-meshes and the
  passes that ask) and works the world bounds out once per rendered frame whatever the views (`boundsEpoch`,
  `worldCenter` / `worldExtent`; `ViewCulling.NewFrame` at the start of `RenderStack`: `Transform.LocalToWorld` is not
  cached). Boxes for the rest: `IsVisible(world, localMin, localMax)`, `IsVisible(worldMin, worldMax)`.
- Who asks. `OpaquePass`: `PerShader` (a part off the layers or outside the view has no program: nothing bound for it),
  `PerObject`. `GeometryPrepass`: the path without tables and `DrawOwnPassParts` (the motion is still recorded for a
  culled renderer: it comes back with the motion it has). `TransparentPass`: `Collect` (no entry: nothing sorted, bound
  or drawn); a static batch first whole, then part by part (`StaticBatch.PartCentre` +- `PartExtent`: `m_Extents`, the
  box around each part in the batch's space, from `StaticBatcher`; cache `Version` 4, magic "LELSB004"; a batch of an
  older build has none and is culled only whole); trails (`TrailRenderer.BuildGeometry(builder, culling, ...)`: the
  points with half their widths bound the strip whatever it faces, tested before colours and triangles are made);
  custom renderers after `PrepareDraw`, by the bounds it gives, before the mesh is uploaded. Shadow maps cull with
  their own views, never the camera's (a caster outside the picture still shadows it): `ShadowPass` per tile (the path
  without tables, `OwnPassParts`, `TransparentShadows.DrawCasters(..., culling)`), `GridShadowMap` per light (the GI's
  maps draw every caster from the CPU each frame: those outside the grid as the light sees it are left out). The GI's
  lists are not culled by anything.
- `RenderSettings.FrustumCulling` (default on; `LEL_FRUSTUM_CULLING=0` turns it off for every renderer of a run, to
  compare pictures and costs; a target-texture camera takes its screen renderer's). `RenderFrameStatistics.
  CulledRenderers`: what the cameras' culling left out (not the shadow maps', not the GPU's); the Game view's overlay
  shows "N culled" on the Renderers line when it is not 0.
- Bounds are the contract. A mesh renderer is culled by its mesh's bounds, as the GPU-driven ones always were: a shader
  that moves vertices out of them is culled wrongly (there is no bounds override per renderer or shader yet). A custom
  renderer by what `PrepareDraw` returns, which has to hold what the shader draws: one whose shader widens a ribbon in
  screen pixels pads its bounds for `Camera.current`.
- Verified (780M, a batch editor, a temporary editor script switching the Game view's setting and writing
  `LastFrame`, `screenshot Game` 1280x720, on against off in one session). A game's flight scene (513 opaque
  renderers, nearly all GPU-driven): edit mode 549 -> 266 draw calls, 10.5 M -> 2.6 M triangles; play mode, paused, 798
  -> 428 and 10.7 M -> 2.4 M: by the counts the GI's shadow maps (the camera's own culling left out 1 there, the
  shadow tiles draw from the tables; not measured apart); pictures equal within the GI's noise (on / off pairs differ
  as two on captures do). A probe scene (961 static
  transparent quads, 441 cubes with a property block, 77 trails, 45 custom renderers, a camera seeing a tenth): 3,946
  -> 933 draw calls, 5.6 -> 1.9 ms of the frame's CPU work, 1,310 left out (the total measured; by kind, from where
  they stand: 826 quads, 370 cubes, 71 trails, 43 custom, 5 cubes beside the view kept by the box test), pictures
  bit-identical with objects crossing every edge; its play mode (the quads one static batch): 869 left out (826 of the
  961 parts, 43 custom), 1,463 -> 582 draw calls, bit-identical; GPU-driven off, the scene's own camera on a floor in
  shadow: 7,931 -> 1,004 draw calls, 7.1 -> 2.0 ms, bit-identical.
- Not done or not checked: transparent shadow casters and `OwnPassParts` casters under a tile's culling (no test
  scene had such casters: those lines never ran); a camera stack and a target-texture camera (each has its own
  culling by construction, not run); a built game; the game's tests; only the 780M.

## Surface cache atlas full: asked once a frame for nothing, no line per object (2026-11-20)

- Found with the measurement above: a Lumen scene of 4,096 GI objects had 136 ms of CPU work a frame in a built game
  (330 ms in a batch editor), all in `LumenScene.Update` (the frame trace named `Render > GlobalSDF`, the pass that
  calls it first). The 1024 x 1024 surface cache atlas holds the cards of 2,485 of its objects; for each of the other
  1,600 `SurfaceCacheAtlas.GetOrAllocate` ran again every frame: made its cards, took the room of those that still
  fitted for nothing, and wrote "[SurfaceCache] Atlas full, object without cards" with `Console.WriteLine` (881,914
  lines, 43 MB, in one run of about 550 frames; 85 to 200 microseconds per such object and frame). The distance field
  itself did nothing in those frames (static field built, nothing dynamic).
- Now: the six card sizes are worked out first (`CardSize`) and tried on a copy of the shelf state: all six fit or
  nothing is taken or made (an object without cards costs a few sums a frame, and nothing is kept per such object:
  spawned and destroyed ones leave nothing behind). One warning per atlas says how many objects fitted and what to
  change (Surface Cache Atlas Size, Texels Per Meter, Contributes To GI). An object without cards gives the GI no
  bounced light, as before. Mixed sizes fare better than before: a large object that does not fit no longer takes
  the room smaller ones after it could use.
- `LumenScene` allocates nothing per object in a frame: its `SceneObject`s are pooled (slot i of every frame is the
  same instance; unused ones let go of their renderer) and the object table's rows are one array that only grows
  (it was a `List.ToArray()` of 1 MB a frame); the buffer is sized to the array, so it is re-specified as rarely.
- Measured after (780M, the same scene, a built game): CPU work 11.4-11.5 ms a frame (136), GPU 16.7-17.2 ms (39.5),
  2 KB allocated a frame (1,825), 58 frames a second (7); `LumenScene.Update` 3.8-4.1 ms for the 4,098 objects in a
  batch editor (330). The frame at number 60 against the old engine's: mean 0.073-0.076 of 255, as two runs of the
  same build differ (0.067-0.069; this scene's frames are not repeatable to the bit). The static batching probe as
  before (15 of 15, pictures with and without batches within 1 of 255 after the first play), New Frontier's 510
  tests pass and Flight plays in a batch editor without errors or warnings (its log never had the line: its scene
  has no surface cache).
- Not done: cards are still never given back while a scene lives (a destroyed renderer's six cards stay taken until
  `Renderer.InvalidateSceneCaches`), so a long session that makes and destroys many GI objects ends with a full
  atlas and new objects without cards; it no longer costs frame time. What is left per object and frame in
  `LumenScene.Update` (about a microsecond: transforms, the inverse matrix, the material's colours looked up by
  name) is paid for static objects too.

## Selection of several objects (2026-11-19)

- The user's ask, with the Mesh Baker: "add multi-select to the editor right away".
- `EditorApplication.Selection.cs`: `Selected` stays the one object everything written for a single selection uses (the
  inspector, the gizmo's pivot, component gizmos, the windows following the selection): with several selected it is the
  active one, the object selected last. `SelectedObjects` (all of them, in the order selected), `IsSelected`,
  `ToggleSelected` (a Ctrl click), `SelectObjects(objects, active)`, `SelectedRoots()` (those none of whose parents is
  selected, in the hierarchy's order: what an action on the selection takes). Setting `Selected` (and `Select(go)`)
  selects that object alone. Destroyed objects leave the list by themselves (`PruneSelection`, also for objects whose
  scene unloaded in play mode).
- A scene built again keeps the selection by ids: `CaptureSelection()` (the active object's id first) /
  `RestoreSelection(scene, ids)` in `ReplaceScene`, `RestoreSnapshot(snapshot, ulong[])`, play and stop, script reloads,
  prefab stages (`StageFrame.Selection`), the scene's reload and merge from disk; `SceneSnapshotRecord` and
  `PrefabFilesRecord` hold `ulong[] SelectionBefore / After`, so undo brings back every selected object.
- Hierarchy: a click selects, Ctrl click toggles, Shift click selects the rows from the anchor (the row clicked last
  without Shift, or the active object when the selection came from elsewhere) to the clicked one over the rows as drawn
  the frame before (`shownRows`; with Ctrl added to the selection). The click still counts on release, so a press on one
  of several selected rows drags them all: a drop re-parents `SelectedRoots()` as one undo step. The context menu on a
  selected row keeps the selection (Duplicate and Delete take it all), on another row it selects that row.
- Scene view: Ctrl or Shift click toggles the object under the mouse (a click on nothing keeps the selection); every
  selected object's bounds are drawn, the active one's brightest; F frames them all. The gizmo sits on the active object;
  the others move with it by the same world delta (`inverse(before) * after`: a turn or a scale is about the active
  object's pivot), only `SelectedRoots()` (a child selected with its parent moves once). One object: an edit of its
  transform as before (`Edits`); several: the transforms' states at the drag's start and end as one `GroupRecord`
  ("Move / Rotate / Scale N objects").
- `DeleteSelected` / `DuplicateSelected` take `SelectedRoots()` ("Delete N objects"; the copies are selected, the active
  one's copy active). The inspector edits the active object and says "N objects selected: the inspector shows X" under
  its header. No editing of several objects' fields at once, no rubber band in the scene view, no Ctrl+A.
- Editor scripts: `Selection.gameObjects` (get / set), `Selection.topLevelGameObjects`; `activeGameObject` as before.
- Automation: `EditorAutomation.Select(path, add)`, `SelectObjects(paths)`, `GetSelection()`; CLI `select <object>
  --add`, `status` lists the active object "and N more: ..."; `click <element> --ctrl / --shift` (`UIAutomation.Invoke`
  carries the keys, `InvokedCtrl` / `InvokedShift`: code that reads the keyboard for a click reads these for an invoked
  one). Test entry `exec TestHooks.DragSelection dx dy dz` and the `dragselected=` hook go through the gizmo's own path
  (`SceneViewPanel.BeginGizmoDrag` / `MoveSelectedAsGizmo` / `EndGizmoDrag`).
- Verified (GUI editor through the CLI, the probe above): Ctrl clicks adding and removing rows, a Shift range, the
  highlighted rows and the scene view's boxes on screenshots, the inspector's line, the context menu's Duplicate on two
  selected rows (the copies selected) and its undo (both originals selected again), a context menu on an unselected
  row; `DragSelection 1 2 3` on four selected objects (a parent, its child and two others): each root moved once, the
  child with its parent, one undo and one redo for all; one object still one edit. Batch: `Selection.gameObjects`,
  `Select(path, true)`, Duplicate / Delete of two objects and their undo with the selection. Not exercised: a real mouse
  (Ctrl / Shift read from the keyboard, the drag of several rows, a real gizmo drag with a turn or a scale).

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

## Deterministic documents, Force Reserialize All Assets (2026-11-18)

- The user's report: saves produced empty changes, elements swapped or only line breaks changed, on different machines.
  Found in New Frontier's history (semantic diff of the user's commits): (1) 1078 files written by other tools (Unity
  converters, agents' edits) with one-line objects (`{ "guid": ..., "path": ... }`), `+` escapes, no `$types`,
  another entry order; the editor's first save of each rewrote it; (2) the `$types` table in the order the writer met the
  types, so adding or moving one object reordered it (CapsuleModel.prefab, 23d3795); (3) one reorder-only file:
  ProjectSettings.asset's `LOD` before `GI` (3f33bb4), not what the engine writes now (not explained; .NET 10's
  GetFields gave declaration order in every probe, including after GetField / GetMember lookups).
- The JSON writer (`JsonDocumentWriter`, the only one) now writes the same bytes for the same contents anywhere:
  `NewLine = "\n"` (was Environment.NewLine: CRLF on Windows, LF elsewhere), `$types` sorted by type name (ordinal),
  dictionaries' entries sorted by key (`SortEntries`: text keys (strings, chars, enums by name) ordinal, numbers by
  value, bools; reference keys keep their order), fields in declaration order (`SerializationUtility.Collect` sorts by
  `MetadataToken`; reflection promises no order). Binary documents (snapshots, builds) are unchanged.
- Assets > Force Reserialize All Assets (`AssetReserializer`, `EditorApplication.Reserialize.cs`; also Assets > Refresh =
  `ImportChangesNow`): asks first, then every document of Assets (Documents.Extensions, hidden folders left out) and
  ProjectSettings.asset, in ordinal order, ~40 ms of them a frame (menu bar status "Reserializing N / M: file"; `IsBusy`,
  play waits): read fresh from the file with the live types (never the loaded copy: windows may hold unsaved edits),
  written as the editor writes it (scenes `SaveToBytes`, prefabs `SavePrefabToBytes` of the one root as prefab mode,
  other documents `ReadObject` + `WriteObject` with the asset named after its file as every Save does), and the file
  written only when the bytes differ. Left alone and listed: types not loaded (missing scripts), a prefab without one
  root, the VFX graph / audio project open with unsaved changes. Not in play or prefab mode. The open scene's file is
  read from disk too (its unsaved changes stay in the editor) and taken as the editor's own write (`sceneFileBase`,
  `sceneStamp`), so it neither reloads nor merges; the other assets reload in place through the watcher afterwards.
  Images are read from their headers only: `TextureImportBatch.BeginOnDemand` (an on-demand batch's textures read their
  file when their texels are first asked for; a later load meeting one adopts it into its own batch, `Adopt`).
- Verified on a copy of New Frontier (scratchpad `reser/` of session 0ec30c68: `run.ps1`, `openscene.ps1`, `jsoncmp/` = a
  semantic JSON diff; Planets.scene left out of the copy): 4454 files, 79 s, 2249 written (1669 with content changes for
  git, the rest CRLF -> LF only), peak 2.9 GB; the semantic diff shows only normalisation (key order, escapes, entry order
  by hierarchy) and what the editor adds (missing `$types`, shader GUIDs, fields classes gained, a VFX setting, the
  always-written root overrides of nested prefabs), nothing lost. Run again in a new process: 0 written; again with
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_PROCESSOR_COUNT=2`: 0 written. Flight open with an unsaved rename: 20
  files written, no reload or merge, the rename kept. NF's 510 tests pass on the reserialized copy.

## Files changed on disk: what reloads in the editor (2026-10-31)

- The user's ask: every kind of file changed outside the editor must come back in by itself. Audited and verified in
  a live editor (scratchpad `MergeProbe`, `ReloadProbe.Setup` / `SetupMore` / `Report` via CLI `exec`, files
  edited by PowerShell). The asset watcher (debounced 0.5 s) runs `RefreshAssetsIfChanged` -> `AssetDatabase.Refresh`
  (also drops the meta cache: import settings are read again), then each cache below; the settings watcher runs
  `RefreshSettingsIfChanged`; `ScriptDomain` has its own watcher.
- Changes on disk import when the editor is in front (2026-11-18, the user's ask: reverting files one by one in git or
  an IDE broke the editor, which imported every step: builds of half-reverted scripts, scene reloads and merges of half
  the change). `EditorApplication.ImportsPaused` (asked once a frame, `EditorFocus.IsInFront`: on Windows the foreground
  window belongs to the editor's process, so the Audio Studio, the UI Builder and its own dialogs count; elsewhere the
  main window's focus): the watchers only note changes, and `RefreshAssetsIfChanged`, `RefreshSettingsIfChanged`,
  `ReloadSceneIfChangedOnDisk`, the start of a script build (`ScriptDomain.Update(startBuilds)`) and the inspector's
  prefab preview wait. Back in front everything imports together ("[Editor] Back in front: importing the files changed
  meanwhile"), as Unity's Auto Refresh. Waiting changes are not busy work (`IsBusy`, CLI `wait idle`).
  Verified with scratchpad `bgimport/test.ps1` of session 0ec30c68 (scratch project, another process's window put in
  front): in the background a new script written three times (the middle one does not compile) and the scene's camera
  renamed on disk: nothing for 4 s; in front: one build without errors and one scene reload; in front a change imports
  at once; in the background `refresh` imports at once.
- Automated and batch runs wait too (2026-11-19, the user's correction: they imported in the background, so an agent's
  edits one file after another each started a build that the next edit made useless). They are never in front: a
  command is the automation coming back to the editor. Every CLI command but `quit`, `help` and `refresh` first calls
  `EditorApplication.ImportPendingChanges` ("[Editor] A command came: importing the files changed meanwhile"), and
  `refresh` / Assets > Refresh / `EditorAutomation.RefreshAssets` call `ImportChangesNow` (also compares the scripts on
  disk with the last build, `ScriptDomain.BuildChangedNow(checkFiles)`: a file written a moment ago). Either starts an
  import on request (`importingOnRequest`): the editor imports as in front until nothing is left (`ImportWorkLeft`:
  changes noted, a build running or waiting, the open scene's reload; `IsBusy` counts the scene's waiting reload too), so
  a command followed by `wait idle` sees every change loaded. Test hooks that write files (`touchscript=`,
  `replacetext=`) call `RequestImport` (the next frame imports). The command runs at once, not after the import: wait
  for `Busy no` before reading what changed. `pauseimports=` is gone.
- Scripts changed while a build runs (2026-11-19, same correction): the build is not loaded ("[Scripts] Scripts changed
  while they compiled: that build is dropped, the next one compiles every change"; `ScriptDomain.buildOld`, checked
  against `startedSources` = `SourceStamp(project, true)` when the build started; the watcher's late word about a file
  the build already has clears `sourcesChanged`), so the scripts reload once. It is not killed: measured on a 300-file
  scratch project, `dotnet build` killed 1.2 / 2 / 3 s in still wrote obj/Game.dll 1.0-5.8 s later, with
  `-m:1 -nodeReuse:false` as well (the compiler server, VBCSCompiler, compiles on after its client dies);
  `UseSharedCompilation=false` would stop that but costs 3-4 s per build. And a compile that ends after a script was
  written leaves obj/ newer than that script, so MSBuild (dates only) skipped the next compile and the old code loaded
  as the new one: this was already so before (an edit during a build could leave old code loaded, silently), and showed
  as soon as builds were dropped. `StartBuild` now deletes the obj/ assemblies (`ScriptBuilder.IntermediateAssemblies`)
  when the scripts changed since the last build and none is newer than them (`CompiledLooksNewer`; also a file put back
  with an older date), and for Rebuild Scripts (`RequestBuild(true)`), so the compile runs.
  Verified with scratchpad `bgimport/test2.ps1` (fresh project, 300 scripts, builds ~3 s, an automated editor only
  watched through its stdout): a checkout of 300 scripts + the scene in the background imported nothing for 4 s, one
  command gave one build, one domain reload and one scene reload; three writes (one broken) the same, loading the last;
  a second write 0.3 / 0.8 / 1.5 / 2.2 s after a build started: that build dropped, one reload, the last version loaded
  each time (before the forced compile: the 0.8 s case loaded the first version); 3.5 s (after the build ended): two
  builds, as it should. Hooks: `touchscript=` + `replacetext=` with no command: one script reload, one scene reload.
  Batch `-listen`: nothing in 5 s, `status` imported, the new version loaded.
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
