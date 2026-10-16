# LELEngine

A C# game engine with a Unity-like workflow: GameObjects and components, an editor with scene/game views and
play mode, a standalone player for game builds, Lumen-inspired real-time global illumination, a UI Toolkit-style
UI system and Recast/Detour-style navigation.

Built on .NET 10, OpenTK 4.9 (GLFW) and OpenGL 4.6 (4.3 minimum), developed and tuned on an Intel integrated GPU.

## Features

**Scene model**
- GameObjects with a Transform hierarchy and `Behaviour` components (`Awake`, `OnEnable`, `Start`, `Update`,
  `LateUpdate`, `OnDisable`, `OnDestroy`, `[ExecuteAlways]`).
- Public and `[SerializeField]` fields are serialized into scenes. Scenes use a fast binary format (LELB) with
  schema tables, polymorphic values and bulk arrays. Older JSON scenes still load.
- Assets are referenced by GUID from `.meta` sidecar files (`AssetDatabase`).

**Rendering**
- Pass-based renderer: HDR target, depth prepass, directional shadow maps, post-processing, GPU profiler.
- GPU-driven geometry: one shared geometry pool, a GPU instance table, compute frustum culling and multi-draw
  indirect. Every hardware feature it relies on has a fallback path.
- Global illumination:
  - **Lumen mode** (default): mesh distance fields and a global SDF, surface cache cards with radiosity, a world
    radiance cache, and screen probes with adaptive placement and temporal/spatial filtering.
  - **Voxel cone tracing mode**: an SDF-assisted voxel GI with multi-bounce injection.
- PBR materials (GGX), shaders with `#include` support from the engine shader library.

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
  gizmos.
- Scene and game views render through the same renderer as the game. Play, pause and step restore the scene
  snapshot when you stop.
- Game scripts compile in the background with `dotnet build`, and a full domain reload swaps them in.
- **UI Builder**: a separate window for editing UXML documents with a live preview, inspector, USS editor and
  canvas picking.
- Build Settings produce a standalone game folder. Batch mode (`-batchmode`) covers build machines and scripts.

## Repository layout

| Folder | Contents |
| --- | --- |
| `LELEngine` | Engine runtime: scene model, rendering, GI, UI, navigation, serialization, assets, input |
| `LELEngine.Editor` | The editor application (and the UI Builder) |
| `LELEngine.Player` | Generic player executable used by game builds |
| `TestGame` | Test and measurement harness that runs the scenes below from the command line |
| `TestGame.Scenes` | Test scenes: GI room, navigation, UI showcase |

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
