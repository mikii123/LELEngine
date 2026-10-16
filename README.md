# LELEngine

A C# game engine with a Unity-like workflow: GameObjects and components, an editor with scene/game views and
play mode, a standalone player for game builds, Lumen-inspired real-time global illumination, a UI Toolkit-style
UI system and Recast/Detour-style navigation.

Built on .NET 10, OpenTK 4.9 (GLFW) and OpenGL 4.6 (4.3 minimum), developed and tuned on an Intel integrated GPU.

![The editor with the GI test room in the scene view](docs/images/editor.png)
*The editor: hierarchy, scene view with the move gizmo on the selected object, inspector and project browser.*

## Features

**Scene model**
- GameObjects with a Transform hierarchy and `Behaviour` components (`Awake`, `OnEnable`, `Start`, `Update`,
  `LateUpdate`, `OnDisable`, `OnDestroy`, `[ExecuteAlways]`).
- Public and `[SerializeField]` fields are serialized into scenes. One document model, two encodings: the editor
  writes readable JSON (scenes, materials, `.meta` files, physics materials), game builds convert every document
  to the fast binary form (LELB: schema tables, polymorphic values, bulk arrays). The conversion needs no types
  loaded, so a build never runs game code.
- Assets are referenced by GUID from `.meta` sidecar files (`AssetDatabase`), which also hold import settings.
- Textures: 2D, 3D (slice grids), cubemaps (crosses, strips, panoramas, HDR) and sprites (single or sliced sheets),
  configured per file like Unity's texture importer. Materials are documents of typed properties declared by the
  shader's `//Properties` block.

**2D physics (Box2D v3)**
- Box2D v3 in pure C# (Box2D.NET): soft-step solver with sub-steps, sleeping islands, continuous collision, and
  the step spread over worker threads (`Physics2DSettings.WorkerCount`, automatic by default).
- Simulates in the XY plane (side views) or the XZ plane (top-down), a project setting: the same 2D API and
  results in both, with transforms mapped into the chosen plane.
- `Rigidbody2D`, `BoxCollider2D`, `CircleCollider2D`, `CapsuleCollider2D`, `PolygonCollider2D`, `EdgeCollider2D`,
  joints (distance, spring, hinge, slider, fixed, wheel, friction, relative, target), `PhysicsMaterial2D` assets,
  layers with a collision matrix.
- The `Physics2D` API: raycasts, linecasts, overlaps, circle and box casts; `OnCollision*2D` / `OnTrigger*2D`
  callbacks on every component; fixed-step simulation with optional transform interpolation.

**Rendering**
- Pass-based renderer: HDR target, depth prepass, directional shadow maps, post-processing, GPU profiler.
- GPU-driven geometry: one shared geometry pool, a GPU instance table, compute frustum culling and multi-draw
  indirect. Every hardware feature it relies on has a fallback path.
- Global illumination:
  - **Lumen mode** (default): mesh distance fields and a global SDF, surface cache cards with radiosity, a world
    radiance cache, and screen probes with adaptive placement and temporal/spatial filtering.
  - **Voxel cone tracing mode**: an SDF-assisted voxel GI with multi-bounce injection.
- PBR materials (GGX), shaders with `#include` support from the engine shader library.
- Skybox materials (`Engine/Skybox.shader` for cubemaps, `Engine/SkyboxPanoramic.shader` for panoramas) set in
  the scene's environment settings.

![Lumen-style global illumination in the test room](docs/images/lumen-gi.png)
*Lumen mode GI: color bleeding from the red and green walls and light from emissive objects, with no analytic
point lights.*

**UI (`LELEngine.UIElements`)**
- A retained UI modelled on Unity UI Toolkit: one `UIDocument` component per document, with no extra GameObjects.
- UXML and USS: selectors, pseudo-classes, variables, transitions, `@import`. Flexbox layout and SDF text with
  rich text tags.
- Built-in controls, trickle-down and bubble-up events, focus handling, and data binding (property paths,
  binding modes, `INotifyBindablePropertyChanged`).
- A runtime API for building UI in code.

**Navigation (`LELEngine.Navigation`)**
- `NavMeshSurface` bakes a tiled navmesh in the surface's own space, so surfaces can sit on rotated objects.
  Touching surfaces connect automatically.
- Carving obstacles are rebuilt in the background. Crowd agents use local avoidance (`NavMeshAgent`).
- A static `NavMesh` query API: paths, raycasts, position sampling, random points.

**Editor (`LELEngine.Editor`)**
- Project launcher, dockable Dear ImGui interface, hierarchy, inspector, project browser, console, undo/redo and
  gizmos (including 2D collider outlines).
- Asset inspectors: a Unity-style material editor (shader picker, colors, ranges, texture slots with an object
  picker and drag and drop), texture import settings with previews and Apply/Revert, shader, mesh and physics
  material views. Material edits apply live, are undoable and save to the asset file.
- Scene and game views render through the same renderer as the game. Play, pause and step restore the scene
  snapshot when you stop.
- Game scripts compile in the background with `dotnet build`, and a full domain reload swaps them in.
- **UI Builder**: a separate window for editing UXML documents with a live preview, inspector, USS editor and
  canvas picking.
- Project Settings (Edit > Project Settings...) has tabs for lighting quality (shadow map, GI method and its
  tunables), 2D physics and the build. They are saved to `ProjectSettings/ProjectSettings.asset` and go into
  builds. The Lighting panel keeps what belongs to a scene: its environment, shadow reach and GI volume.
- The Build tab (File > Build Settings...) produces a standalone game folder. Batch mode (`-batchmode`) covers
  build machines and scripts.

![Play mode in the game view](docs/images/play-mode.png)
*Play mode: the game view runs the scene's scripts (here the orbiting emitters).*

## Repository layout

| Folder | Contents |
| --- | --- |
| `LELEngine` | Engine runtime: scene model, rendering, GI, UI, navigation, serialization, assets, input |
| `LELEngine.Editor` | The editor application (and the UI Builder) |
| `LELEngine.Player` | Generic player executable used by game builds |
| `TestGame` | Test and measurement harness that runs the scenes below from the command line |
| `TestGame.Scenes` | Test scenes: GI room, navigation, UI showcase, Box2D physics |
| `LELEngine.Benchmarks` | Headless benchmarks: `dotnet run --project LELEngine.Benchmarks -c Release -- physics2d [scenario] [workers=N] [nosleep=1] [plane=xz]` (pyramid, pyramids, rain, chains, queries, rays, closest, overlaps; each reports a state checksum that must match between the planes), and `physics2d-scene` runs the Box2D test scene without a window |

## Building

Requirements: the .NET 10 SDK, Windows, and a GPU with OpenGL 4.3 or newer.

```bash
dotnet build LELEngine.sln -c Release
```

## Running

The editor opens the project launcher, where you create or open a project:

```bash
dotnet run --project LELEngine.Editor -c Release
```

The test harness runs a scene built in code. A few options:

```bash
dotnet run --project TestGame -c Release -- gi=lumen
```

| Option | Effect |
| --- | --- |
| `gi=lumen\|vct\|0` | Global illumination mode |
| `ui=1` | UI showcase scene |
| `nav=1` | Navigation scene with crowd agents |
| `physics=1` | Box2D scene: stack, ball, pendulum, hinge chain, capsules, spring, moving platform, trigger, queries |
| `outdoor=1` | Open-sky yard under the procedural skybox (`sky=0` for the flat sky color, `skybox=path` for a cubemap, panorama or material asset) |
| `sky=1` | Procedural skybox in the GI room: rays that leave the room sample it |
| `stats=1` | GPU and CPU timings in the console |
| `animate=0` | Freeze the moving emitters |

## Writing components

```csharp
using LELEngine;

public sealed class Spinner : Behaviour
{
	public float Speed = 90f;

	public override void Update()
	{
		transform.rotation *= QuaternionHelper.Euler(0f, Speed * Time.deltaTime, 0f);
	}
}
```

In an editor project, put scripts in `Assets/Scripts`. They compile in the background when saved.

A 2D physics script looks like its Unity counterpart:

```csharp
using LELEngine;
using OpenTK.Mathematics;

public sealed class Jumper : Behaviour
{
	public float Impulse = 5f;
	private Rigidbody2D body;

	public override void Start()
	{
		body = GetComponent<Rigidbody2D>();
	}

	public override void OnCollisionEnter2D(Collision2D collision)
	{
		if (Physics2D.Raycast(body.position, new Vector2(0f, -1f), 0.6f))
		{
			body.AddForce(new Vector2(0f, Impulse), ForceMode2D.Impulse);
		}
	}
}
```
