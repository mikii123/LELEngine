# Graph Report - .  (2026-10-17)

## Corpus Check
- Large corpus: 306 files · ~1,292,638 words. Semantic extraction will be expensive (many Claude tokens). Consider running on a subfolder.

## Summary
- 6609 nodes · 17270 edges · 196 communities (187 shown, 9 thin omitted)
- Extraction: 91% EXTRACTED · 9% INFERRED · 0% AMBIGUOUS · INFERRED: 1569 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- UI Inline Styles
- UI Style Resolution
- UI Hierarchy & Bindings
- Editor Gizmo Hit-Testing
- VFX Operator Nodes
- Lighting & GI Uniforms
- VFX Codegen
- VFX Block Nodes
- VFX Value Editors
- VFX Graph Layout
- Game Assembly Loading
- 2D Physics Joints & Colliders
- UI Stylesheets & Theme
- UI Builder Window
- Framebuffers
- Renderer & Debug Draw
- Box2D Native Bindings
- Navmesh Geometry
- Physics Benchmarks
- Default Scene
- Flex Layout
- UI Rendering Data
- View Renderer
- NavMesh Crowd
- UI Element Pooling
- Physics2D Queries
- VFX Graph Compiler
- VFX Runtime
- Shared Value Types
- Builtin Shaders
- 2D Colliders & Triggers
- TestGame & Math
- UI Events & Menus
- Joint Definitions
- Window Input Events
- Math & Vertex Utilities
- Collider Shapes & Uniforms
- UI Event Dispatch
- Components & Behaviours
- Binary Serialization
- .LogError() cluster
- LumenScene cluster
- JsonDocumentWriter cluster
- VoxelGIPass cluster
- VisualEffect cluster
- AssetDatabase cluster
- BaseVisualElementPanel cluster
- ConsoleLog cluster
- .Apply() cluster
- SurfaceCacheAtlas cluster
- ValueTag cluster
- .LogWarning() cluster
- Material cluster
- ProjectSettingsWindow cluster
- LELEngine.UIElements cluster
- SceneViewPanel cluster
- GpuScene cluster
- TextureAssets cluster
- ObjectPicker cluster
- SerializationUtility cluster
- TextInput cluster
- NavMeshSurface cluster
- LELEngine.Editor cluster
- UIBuilder cluster
- InheritedStyle cluster
- NavMesh cluster
- InspectorContext cluster
- GameHost cluster
- .LogException() cluster
- BaseSlider cluster
- LELEngine README cluster
- NavMeshCarver cluster
- ListView cluster
- TextValueField cluster
- Painter2D cluster
- FontAsset cluster
- UxmlNode cluster
- TileBuilder cluster
- NavMeshAgent cluster
- Binding cluster
- MeshRenderer cluster
- NavTile cluster
- TrueTypeFont cluster
- .Line() cluster
- MaterialProperty cluster
- UQueryBuilder cluster
- .BuildTile() cluster
- HeightfieldLayer cluster
- BaseField cluster
- PointerEventBase cluster
- UIDocument cluster
- LELEngine.csproj cluster
- .Draw() cluster
- EditorExtensions cluster
- UxmlRegistry cluster
- LELEngine.VFX cluster
- EventModifiers cluster
- IVisualElementScheduledItem cluster
- PhysicsTaskSystem cluster
- BasePopupField cluster
- .OpenProject() cluster
- EventCallbackRegistry cluster
- TextGenerator cluster
- .Release() cluster
- .DrawTypedAttribute() cluster
- Manipulators.cs cluster
- ImGuiController cluster
- .StopPropagation() cluster
- UIRuntime cluster
- EditorWindow cluster
- FieldDrawer cluster
- Angle cluster
- Texture2D cluster
- ShaderRenderState cluster
- GeometryPool cluster
- Heightfield cluster
- TextLayoutResult cluster
- .Parse() cluster
- BuilderWindow cluster
- ProjectPanel cluster
- NativeDialogs cluster
- SerializationContext cluster
- CompactHeightfield cluster
- Focusable cluster
- UIBuilder.cs cluster
- TextureImporter cluster
- GameViewPanel cluster
- EnvironmentSettings cluster
- NavMath cluster
- ShaderProperty cluster
- PropertyPathAccess cluster
- IDisposable cluster
- .OnRenderFrame() cluster
- IFileDialog cluster
- Documents cluster
- ScrollView cluster
- Physics2DMath cluster
- Sprite cluster
- GlobalIlluminationSettings cluster
- Cubemap cluster
- Texture cluster
- MainWindow cluster
- NavMeshData cluster
- RadianceCachePass cluster
- TextElement cluster
- NavWanderer cluster
- GIDebugControls cluster
- Collision2D cluster
- Physics2DSettings cluster
- ScreenProbeGatherPass cluster
- DataBinding cluster
- .ReleaseSystems() cluster
- FpsController cluster
- HierarchyPanel cluster
- ConsoleHost cluster
- VfxSpawnerState cluster
- GameInputHost cluster
- .OnAfterDeserialize() cluster
- Foldout cluster
- Physics2DHarness cluster
- UIShowcase cluster
- Attribute cluster
- Editor screenshot (scene view) cluster
- Editor cluster
- EditorGUILayout cluster
- .BuildMenus() cluster
- .Main() cluster
- VertexAttribute cluster
- LayoutNodeData cluster
- GlyphSdf cluster
- GLCapabilities cluster
- .DrawWindow() cluster
- VfxIntrinsics cluster
- TextureImporterInspector cluster
- LayerMask cluster
- ConverterGroup cluster
- PropertyPath cluster
- .MapKey() cluster
- .Guard() cluster
- TypeRegistry cluster
- CustomInspectors.cs cluster
- Extensions cluster
- NodeQueue cluster
- .Update() cluster
- VFX graph node editor cluster
- EditorStyle cluster
- InternalStorage cluster
- VfxInstance.cs cluster
- .Measure() cluster
- .Of() cluster
- Resources cluster
- .ListMembers() cluster

## God Nodes (most connected - your core abstractions)
1. `VisualElement` - 283 edges
2. `EditorApplication` - 125 edges
3. `ShaderProgram` - 110 edges
4. `VfxExpression` - 98 edges
5. `Collider2D` - 93 edges
6. `ComputedStyle` - 92 edges
7. `Scene` - 91 edges
8. `PhysicsScene2D` - 87 edges
9. `Material` - 82 edges
10. `StyleValue` - 82 edges

## Surprising Connections (you probably didn't know these)
- `Editor scripts (custom inspectors, windows, menu items)` --conceptually_related_to--> `Editor scripts API (CustomEditor, EditorWindow, MenuItem)`  [INFERRED]
  CLAUDE.md → README.md
- `Physics2DBenchmark` --references--> `SimulationPlane2D`  [EXTRACTED]
  LELEngine.Benchmarks/Physics2DBenchmark.cs → LELEngine/Physics2D/Physics2DSettings.cs
- `SceneViewPanel` --references--> `Camera`  [EXTRACTED]
  LELEngine.Editor/Panels/SceneViewPanel.cs → LELEngine/Mono/Behaviours/Camera.cs
- `BuilderPreview` --references--> `Framebuffer`  [EXTRACTED]
  LELEngine.Editor/UIBuilder/BuilderPreview.cs → LELEngine/Rendering/Framebuffer.cs
- `BuilderPreview` --references--> `VisualElement`  [EXTRACTED]
  LELEngine.Editor/UIBuilder/BuilderPreview.cs → LELEngine/UI/Binding/VisualElement.Bindings.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **VFX counter layout defined in three places** — claude_vfx_counter_pool, claude_vfx_frame_flow, claude_gpu_particles [INFERRED 0.75]
- **Editor script build and reload pipeline** — claude_game_editor_dll, claude_game_load_context, claude_editor_extensions, claude_editor_window_persistence [INFERRED 0.75]
- **Editor UI screenshots sharing the GITest scene and panel layout** — docs_images_editor, docs_images_play_mode, docs_images_lumen_gi [INFERRED 0.75]
- **VFX authoring to rendered result** — docs_images_vfx_graph, docs_images_vfx_showcase, docs_images_vfx_graph_node_graph [INFERRED 0.75]

## Communities (196 total, 9 thin omitted)

### Community 0 - "UI Inline Styles"
Cohesion: 0.04
Nodes (53): IEquatable, IResolvedStyle, List, InlineStyle, IStyle, Align, DisplayStyle, EasingMode (+45 more)

### Community 1 - "UI Style Resolution"
Cohesion: 0.04
Nodes (31): Color4, Dictionary, List, Type, StyleApplier, BackgroundPositionKeyword, BackgroundSizeType, CursorType (+23 more)

### Community 2 - "UI Hierarchy & Bindings"
Cohesion: 0.03
Nodes (35): Hierarchy, IEnumerable, IEnumerator, bool, IEnumerable, List, object, BindingInfo (+27 more)

### Community 3 - "Editor Gizmo Hit-Testing"
Cohesion: 0.05
Nodes (34): Hit, HitKind, bool, Dictionary, float, HashSet, ImDrawListPtr, int (+26 more)

### Community 4 - "VFX Operator Nodes"
Cohesion: 0.06
Nodes (21): string, VfxOperatorEmitter, VfxOperatorNodes, Action, bool, HashSet, int, object (+13 more)

### Community 5 - "Lighting & GI Uniforms"
Cohesion: 0.06
Nodes (34): GridShadowMap, Matrix4, Lighting, MemoryBarrierFlags, RenderContext, DepthFunction, GLState, RenderContext (+26 more)

### Community 6 - "VFX Codegen"
Cohesion: 0.05
Nodes (43): CurveSlot, int, List, StringBuilder, VfxCodegen, bool, Dictionary, float (+35 more)

### Community 7 - "VFX Block Nodes"
Cohesion: 0.07
Nodes (13): Func, List, Regex, string, VfxBlockEmitter, VfxBlockNodes, bool, Dictionary (+5 more)

### Community 8 - "VFX Value Editors"
Cohesion: 0.04
Nodes (36): EditState, bool, int, object, EditState, VfxValueEditors, float, Matrix4 (+28 more)

### Community 9 - "VFX Graph Layout"
Cohesion: 0.07
Nodes (26): Func, HashSet, IEnumerable, List, string, VfxCompiler, VfxSpawnerDescription, List (+18 more)

### Community 10 - "Game Assembly Loading"
Cohesion: 0.05
Nodes (34): AssemblyLoadContext, AssemblyName, DateTime, GameLoadContext, JsonSerializerOptions, ProjectInfo, Guid, List (+26 more)

### Community 11 - "2D Physics Joints & Colliders"
Cohesion: 0.05
Nodes (27): AnchoredJoint2D, B2BodyType, B2MotionLocks, Quaternion, Vector2, Vector3, PlaneBasis, float (+19 more)

### Community 12 - "UI Stylesheets & Theme"
Cohesion: 0.05
Nodes (25): IAsset, VisualElementStyleSheetSet, string, DefaultTheme, bool, Dictionary, int, List (+17 more)

### Community 13 - "UI Builder Window"
Cohesion: 0.05
Nodes (22): Action, bool, BuilderWindow, byte, double, FileSystemWatcher, GameObject, Scene (+14 more)

### Community 14 - "Framebuffers"
Cohesion: 0.04
Nodes (39): bool, IReadOnlyList, List, TextureWrapMode, Framebuffer, GlobalIlluminationSettings, IReadOnlyList, Matrix4 (+31 more)

### Community 15 - "Renderer & Debug Draw"
Cohesion: 0.04
Nodes (31): LELEngine.Rendering.Lumen, LELEngine.Rendering, LELEngine.Rendering.Passes, LELEngine.Shaders, int, Renderer, string, DebugDrawPass (+23 more)

### Community 16 - "Box2D Native Bindings"
Cohesion: 0.05
Nodes (22): b2CastResultFcn, B2ContactData, b2CustomFilterFcn, B2Manifold, b2OverlapResultFcn, B2WorldId, ContactEvent, Dictionary (+14 more)

### Community 17 - "Navmesh Geometry"
Cohesion: 0.12
Nodes (19): a, b, byte, float, int, IReadOnlyList, List, Random (+11 more)

### Community 18 - "Physics Benchmarks"
Cohesion: 0.06
Nodes (22): LELEngine.Benchmarks, Action, bool, float, Func, int, List, Vector2 (+14 more)

### Community 19 - "Default Scene"
Cohesion: 0.08
Nodes (27): GameObject, Scene, string, DefaultScene, ICollection, float, Matrix4, Quaternion (+19 more)

### Community 20 - "Flex Layout"
Cohesion: 0.13
Nodes (10): Axis, float, List, MeasureMode, Stack, Axis, FlexLayout, int (+2 more)

### Community 21 - "UI Rendering Data"
Cohesion: 0.06
Nodes (30): ElementGpuData, FontPageKey, BackgroundPosition, BaseVisualElementPanel, bool, Color4, ComputedStyle, Dictionary (+22 more)

### Community 22 - "View Renderer"
Cohesion: 0.05
Nodes (26): Scene, ViewRenderer, Camera, float, Matrix4, int, FullscreenQuad, bool (+18 more)

### Community 23 - "NavMesh Crowd"
Cohesion: 0.08
Nodes (22): Circle, a, b, bool, Comparison, Dictionary, float, int (+14 more)

### Community 24 - "UI Element Pooling"
Cohesion: 0.07
Nodes (22): Action, bool, string, InlineEntry, bool, Dictionary, string, StyleAnimationKind (+14 more)

### Community 25 - "Physics2D Queries"
Cohesion: 0.08
Nodes (16): B2QueryFilter, B2ShapeProxy, B2Vector2, int, List, Vector2, Physics2D, SimulationMode2D (+8 more)

### Community 26 - "VFX Graph Compiler"
Cohesion: 0.08
Nodes (16): VfxCompiledGraph, Dictionary, float, int, string, Vector2, VfxCullingMode, VfxGraph (+8 more)

### Community 27 - "VFX Runtime"
Cohesion: 0.06
Nodes (27): RenderContext, bool, Dictionary, double, float, int, IntPtr, IReadOnlyList (+19 more)

### Community 28 - "Shared Value Types"
Cohesion: 0.07
Nodes (17): bool, Color4, Dictionary, float, int, List, long, Matrix4 (+9 more)

### Community 29 - "Builtin Shaders"
Cohesion: 0.06
Nodes (20): string, BuiltinShaders, ComputeShader, Renderer, PostProcessPass, ShaderType, Shader, ActiveUniformType (+12 more)

### Community 30 - "2D Colliders & Triggers"
Cohesion: 0.07
Nodes (24): B2Capsule, B2Circle, B2Filter, B2Polygon, B2Segment, B2BodyId, B2ShapeId, B2Vector2 (+16 more)

### Community 31 - "TestGame & Math"
Cohesion: 0.04
Nodes (29): TestGame, TestGame.Scenes, LELEngine, Mathf, Color4, int, Vector2, Vector3 (+21 more)

### Community 32 - "UI Events & Menus"
Cohesion: 0.05
Nodes (39): Direction, int, List, Rect, string, BlurEvent, ChangeEvent, CustomStyleResolvedEvent (+31 more)

### Community 33 - "Joint Definitions"
Cohesion: 0.08
Nodes (21): B2JointDef, B2JointId, B2MotorJointDef, B2BodyId, B2Transform, B2Vector2, bool, float (+13 more)

### Community 34 - "Window Input Events"
Cohesion: 0.04
Nodes (30): char, KeyboardKeyEventArgs, MouseButtonEventArgs, MouseMoveEventArgs, MouseWheelEventArgs, TextInputEventArgs, CursorState, int (+22 more)

### Community 35 - "Math & Vertex Utilities"
Cohesion: 0.10
Nodes (15): byte, Color4, int, MethodImpl, Quaternion, ReadOnlySpan, Vector2, Vector2i (+7 more)

### Community 36 - "Collider Shapes & Uniforms"
Cohesion: 0.06
Nodes (27): LELEngine.Shaders.Uniforms, CapsuleDirection2D, Collider2D, Color4, GameObject, IReadOnlyList, PlaneBasis, Vector2 (+19 more)

### Community 37 - "UI Event Dispatch"
Cohesion: 0.07
Nodes (15): Button, Stack, Stopwatch, CallbackEventHandler, EventBase, IEventHandler, PropagationPhase, int (+7 more)

### Community 38 - "Components & Behaviours"
Cohesion: 0.06
Nodes (14): Callback, bool, ICollection, Behaviour, Color4, float, DirectionalLight, bool (+6 more)

### Community 39 - "Binary Serialization"
Cohesion: 0.14
Nodes (7): ByteReader, bool, Dictionary, IList, Type, BinaryDocumentReader, BulkListAccess

### Community 40 - ".LogError() cluster"
Cohesion: 0.06
Nodes (16): Dictionary, Encoding, NativeWindow, string, Type, BatchMode, CommandLine, TeeWriter (+8 more)

### Community 41 - "LumenScene cluster"
Cohesion: 0.07
Nodes (33): CardData, bool, Color4, Dictionary, float, GlobalIlluminationSettings, HashSet, int (+25 more)

### Community 42 - "JsonDocumentWriter cluster"
Cohesion: 0.08
Nodes (19): ArrayBufferWriter, SerializedNode, byte, Color4, Dictionary, int, List, Quaternion (+11 more)

### Community 43 - "VoxelGIPass cluster"
Cohesion: 0.08
Nodes (24): GeometryVolume, bool, ComputeShader, float, Framebuffer, GlobalIlluminationSettings, int, IReadOnlyList (+16 more)

### Community 44 - "VisualEffect cluster"
Cohesion: 0.06
Nodes (14): InspectorContext, VisualEffectInspector, Mesh, bool, Color4, float, List, Mesh (+6 more)

### Community 45 - "AssetDatabase cluster"
Cohesion: 0.10
Nodes (13): AssetTypeInfo, Dictionary, Func, List, object, string, Type, AssetDatabase (+5 more)

### Community 46 - "BaseVisualElementPanel cluster"
Cohesion: 0.07
Nodes (19): bool, HashSet, int, List, Rect, Stack, Vector2, int (+11 more)

### Community 47 - "ConsoleLog cluster"
Cohesion: 0.06
Nodes (24): BootConfig, LELEngine.Player, JsonSerializerOptions, string, BootConfig, Encoding, int, List (+16 more)

### Community 48 - ".Apply() cluster"
Cohesion: 0.08
Nodes (16): bool, byte, int, List, object, string, ulong, AssetStateRecord (+8 more)

### Community 49 - "SurfaceCacheAtlas cluster"
Cohesion: 0.07
Nodes (25): LELEngine.Rendering.DistanceField, ComputeShader, Dictionary, int, Mesh, Vector3, Vector3i, DistanceFieldAtlas (+17 more)

### Community 50 - "ValueTag cluster"
Cohesion: 0.11
Nodes (20): JsonDocument, byte, int, KeyValuePair, List, object, string, AssetReference (+12 more)

### Community 51 - ".LogWarning() cluster"
Cohesion: 0.10
Nodes (16): Dictionary, string, Texture, Type, UIAssets, Dictionary, HashSet, List (+8 more)

### Community 52 - "Material cluster"
Cohesion: 0.09
Nodes (10): Color4, IEnumerable, int, IReadOnlyList, List, string, Texture, Vector3 (+2 more)

### Community 53 - "ProjectSettingsWindow cluster"
Cohesion: 0.08
Nodes (17): Behaviour, Dictionary, GameObject, int, List, string, Type, InspectorPanel (+9 more)

### Community 54 - "LELEngine.UIElements cluster"
Cohesion: 0.07
Nodes (13): LELEngine.UIElements, ITransform, float, Rect, Vector2, Rect, Matrix2x3, Rect (+5 more)

### Community 55 - "SceneViewPanel cluster"
Cohesion: 0.10
Nodes (11): ImGuizmoMode, ImGuizmoOperation, bool, float, GameObject, Matrix4, NVector2, Vector3 (+3 more)

### Community 56 - "GpuScene cluster"
Cohesion: 0.10
Nodes (17): CullView, GpuInstance, GpuMaterial, bool, Dictionary, int, IReadOnlyList, List (+9 more)

### Community 57 - "TextureAssets cluster"
Cohesion: 0.10
Nodes (17): Entry, byte, Dictionary, float, int, List, object, string (+9 more)

### Community 58 - "ObjectPicker cluster"
Cohesion: 0.07
Nodes (21): ImTextureRef, IReadOnlyList, EditorGui, Icon, bool, Dictionary, Func, int (+13 more)

### Community 59 - "SerializationUtility cluster"
Cohesion: 0.09
Nodes (14): Type, Action, Dictionary, FieldInfo, Func, int, KeyValuePair, List (+6 more)

### Community 60 - "TextInput cluster"
Cohesion: 0.10
Nodes (10): bool, Color4, float, int, long, string, Vector2, TextField (+2 more)

### Community 61 - "NavMeshSurface cluster"
Cohesion: 0.10
Nodes (15): Behaviour, bool, Dictionary, GameObject, IEnumerable, int, List, Matrix4 (+7 more)

### Community 62 - "LELEngine.Editor cluster"
Cohesion: 0.06
Nodes (16): LELEngine.Editor, LELEngine.Serialization, Action, bool, List, string, BuildSettings, GameBuilder (+8 more)

### Community 63 - "UIBuilder cluster"
Cohesion: 0.11
Nodes (18): CreateStyleSheet(), bool, double, EditorApplication, End, float, int, List (+10 more)

### Community 64 - "InheritedStyle cluster"
Cohesion: 0.09
Nodes (16): Color4, Dictionary, float, InheritedStyle, Comparison, Dictionary, HashSet, int (+8 more)

### Community 65 - "NavMesh cluster"
Cohesion: 0.07
Nodes (14): LELEngine.Navigation, byte, float, int, List, Random, string, Vector3 (+6 more)

### Community 66 - "InspectorContext cluster"
Cohesion: 0.14
Nodes (10): float, object, Scene, InspectorContext, ActiveUniformType, int, List, string (+2 more)

### Community 67 - "GameHost cluster"
Cohesion: 0.08
Nodes (14): GameWindow, FrameEventArgs, MouseCursor, ResizeEventArgs, GameHost, RenderQueue, bool, DllImport (+6 more)

### Community 68 - ".LogException() cluster"
Cohesion: 0.13
Nodes (16): Exception, Behaviour, GameObject, HashSet, IEnumerable, int, JsonElement, List (+8 more)

### Community 69 - "BaseSlider cluster"
Cohesion: 0.10
Nodes (6): bool, string, BaseSlider, Slider, SliderDirection, SliderInt

### Community 70 - "LELEngine README cluster"
Cohesion: 0.07
Nodes (35): Building and running on this machine (.NET 10 SDK), EditorExtensions host / EditorApplication.Extensions, Editor saved layout.ini scene view issue, Editor scripts (custom inspectors, windows, menu items), Open editor window save/restore, Game.Editor.dll second generated project, GameLoadContext (collectible), GPU particles (VFX) (+27 more)

### Community 71 - "NavMeshCarver cluster"
Cohesion: 0.10
Nodes (16): Scene, bool, float, HashSet, int, IReadOnlyList, List, Quaternion (+8 more)

### Community 72 - "ListView cluster"
Cohesion: 0.11
Nodes (16): Action, bool, float, Func, IEnumerable, IList, int, List (+8 more)

### Community 73 - "TextValueField cluster"
Cohesion: 0.09
Nodes (12): bool, string, Vector2, Vector3, BaseCompositeField, DoubleField, FloatField, IntegerField (+4 more)

### Community 74 - "Painter2D cluster"
Cohesion: 0.10
Nodes (18): Color4, Rect, Texture, Image, bool, Color4, float, int (+10 more)

### Community 75 - "FontAsset cluster"
Cohesion: 0.10
Nodes (12): AtlasPage, bool, byte, Dictionary, float, int, IReadOnlyList, List (+4 more)

### Community 76 - "UxmlNode cluster"
Cohesion: 0.14
Nodes (9): Action, Func, int, KeyValuePair, List, string, UxmlDocument, UxmlNode (+1 more)

### Community 77 - "TileBuilder cluster"
Cohesion: 0.14
Nodes (11): bool, byte, int, List, ushort, Contour, MonotoneRegion, PolyMesh (+3 more)

### Community 78 - "NavMeshAgent cluster"
Cohesion: 0.10
Nodes (10): bool, byte, float, int, Quaternion, Vector3, AgentState, NavMeshAgent (+2 more)

### Community 79 - "Binding cluster"
Cohesion: 0.11
Nodes (11): EventArgs, string, BindablePropertyChangedEventArgs, Binding, BindingId, BindingResult, BindingStatus, BindingUpdateTrigger (+3 more)

### Community 80 - "MeshRenderer cluster"
Cohesion: 0.09
Nodes (16): bool, MeshRenderer, GpuScene, IReadOnlyList, RenderContext, OpaquePass, float, int (+8 more)

### Community 81 - "NavTile cluster"
Cohesion: 0.17
Nodes (14): Action, bool, Dictionary, float, HashSet, int, List, Matrix4 (+6 more)

### Community 82 - "TrueTypeFont cluster"
Cohesion: 0.18
Nodes (8): GlyphBox, bool, byte, float, List, GlyphContour, GlyphPoint, TrueTypeFont

### Community 83 - ".Line() cluster"
Cohesion: 0.15
Nodes (13): GameObject, Color4, float, Span, Vector3, NavMeshDebug, bool, Color4 (+5 more)

### Community 84 - "MaterialProperty cluster"
Cohesion: 0.12
Nodes (16): ActiveUniformType, Color4, Cubemap, float, int, string, Texture, Texture3D (+8 more)

### Community 85 - "UQueryBuilder cluster"
Cohesion: 0.12
Nodes (6): Action, bool, Func, List, UQueryBuilder, UQueryState

### Community 86 - ".BuildTile() cluster"
Cohesion: 0.11
Nodes (22): Config, LELEngine.Navigation.Builder, Action, bool, byte, float, int, IReadOnlyList (+14 more)

### Community 87 - "HeightfieldLayer cluster"
Cohesion: 0.09
Nodes (11): Contour, byte, float, NavMeshLayerData, Vector2, Vector3, HeightfieldLayer, RunLength (+3 more)

### Community 88 - "BaseField cluster"
Cohesion: 0.12
Nodes (15): string, BaseField, string, Box, GroupBox, HelpBox, HelpBoxMessageType, Label (+7 more)

### Community 89 - "PointerEventBase cluster"
Cohesion: 0.10
Nodes (13): Vector2, Vector3, ClickEvent, ContextClickEvent, IPointerEvent, PointerCancelEvent, PointerEnterEvent, PointerEventBase (+5 more)

### Community 90 - "UIDocument cluster"
Cohesion: 0.13
Nodes (10): bool, float, int, List, Vector2, Vector2i, PanelScaleMode, PanelScreenMatchMode (+2 more)

### Community 91 - "LELEngine.csproj cluster"
Cohesion: 0.09
Nodes (24): LELEngine.Benchmarks, net10.0, OpenTK (4.9.4), Microsoft.NET.Sdk, LELEngine.Editor, net10.0, OpenTK (4.9.4), Microsoft.NET.Sdk (+16 more)

### Community 92 - ".Draw() cluster"
Cohesion: 0.10
Nodes (6): Type, AssetInspectors, Dictionary, float, string, PhysicsMaterial2D

### Community 93 - "EditorExtensions cluster"
Cohesion: 0.11
Nodes (9): Assembly, Behaviour, bool, Dictionary, GameObject, HashSet, Type, EditorExtensions (+1 more)

### Community 94 - "UxmlRegistry cluster"
Cohesion: 0.11
Nodes (12): Dictionary, FieldInfo, List, object, PropertyInfo, string, Type, UxmlAttributeAttribute (+4 more)

### Community 95 - "LELEngine.VFX cluster"
Cohesion: 0.10
Nodes (10): LELEngine.VFX, LELEngine.Editor.VFX, bool, byte, EditorApplication, string, VfxGraphRecord, string (+2 more)

### Community 96 - "EventModifiers cluster"
Cohesion: 0.20
Nodes (8): Keys, Vector2, PointerCaptureHelper, EventModifiers, Keys, IKeyboardEvent, KeyboardEventBase, KeyUpEvent

### Community 97 - "IVisualElementScheduledItem cluster"
Cohesion: 0.12
Nodes (10): Action, bool, Func, List, long, IVisualElementScheduledItem, IVisualElementScheduler, ScheduledItem (+2 more)

### Community 98 - "PhysicsTaskSystem cluster"
Cohesion: 0.09
Nodes (15): b2EnqueueTaskCallback, b2FinishTaskCallback, b2TaskCallback, Batch, ConcurrentBag, ConcurrentQueue, bool, int (+7 more)

### Community 99 - "BasePopupField cluster"
Cohesion: 0.14
Nodes (11): Enum, Func, List, Rect, string, Type, BasePopupField, DropdownField (+3 more)

### Community 100 - ".OpenProject() cluster"
Cohesion: 0.13
Nodes (6): Action, List, string, ProjectLauncher, List, RecentProjects

### Community 101 - "EventCallbackRegistry cluster"
Cohesion: 0.13
Nodes (10): bool, Delegate, EventCallback, int, List, long, EventCallbackFunctor, EventCallbackFunctorBase (+2 more)

### Community 102 - "TextGenerator cluster"
Cohesion: 0.21
Nodes (8): CharRun, hardBreak, end, List, Stack, TextGenerator, TextSettings, start

### Community 103 - ".Release() cluster"
Cohesion: 0.12
Nodes (5): byte, TextureTarget, Texture3D, NVector2, Texture

### Community 105 - "Manipulators.cs cluster"
Cohesion: 0.11
Nodes (13): Action, RepeatButton, IMouseEvent, Action, int, List, ContextualMenuManipulator, IManipulator (+5 more)

### Community 106 - "ImGuiController cluster"
Cohesion: 0.10
Nodes (14): FocusedChangedEventArgs, ImGuiContextPtr, ImGuiMouseCursor, int, IntPtr, MouseButton, MouseButtonEventArgs, MouseMoveEventArgs (+6 more)

### Community 107 - ".StopPropagation() cluster"
Cohesion: 0.12
Nodes (7): float, int, Vector2, MinMaxSlider, PointerDownEvent, PointerMoveEvent, PointerUpEvent

### Community 108 - "UIRuntime cluster"
Cohesion: 0.12
Nodes (13): Action, Behaviour, bool, Func, IReadOnlyList, KeyModifiers, List, MouseButton (+5 more)

### Community 109 - "EditorWindow cluster"
Cohesion: 0.11
Nodes (5): ImGuiWindowFlags, bool, NVector2, string, EditorWindow

### Community 110 - "FieldDrawer cluster"
Cohesion: 0.21
Nodes (6): FieldInfo, IList, Quaternion, Type, Vector3, FieldDrawer

### Community 111 - "Angle cluster"
Cohesion: 0.12
Nodes (6): AngleUnit, Angle, Rotate, bool, StringBuilder, UIDebug

### Community 112 - "Texture2D cluster"
Cohesion: 0.11
Nodes (11): byte, Color4, float, TextureTarget, Texture2D, Cubemap, int, Texture2D (+3 more)

### Community 113 - "ShaderRenderState cluster"
Cohesion: 0.16
Nodes (7): BlendingFactor, bool, DepthFunction, int, CullMode, MaterialQueue, ShaderRenderState

### Community 114 - "GeometryPool cluster"
Cohesion: 0.19
Nodes (7): IEqualityComparer, int, List, Mesh, GeometryPool, VertexComparer, Vertex

### Community 115 - "Heightfield cluster"
Cohesion: 0.20
Nodes (7): int, List, Vector3, Dir, Heightfield, HeightfieldFilters, Rasterizer

### Community 116 - "TextLayoutResult cluster"
Cohesion: 0.18
Nodes (15): bool, byte, Color4, float, int, MeasureMode, string, Vector2 (+7 more)

### Community 117 - ".Parse() cluster"
Cohesion: 0.14
Nodes (10): ClassDef, Coverage, GposPairSubtable, Dictionary, HashSet, int, ClassDef, Coverage (+2 more)

### Community 118 - "BuilderWindow cluster"
Cohesion: 0.11
Nodes (11): LELEngine.Editor.UIBuilder, LELEngine.UIElements.Rendering, bool, CancelEventArgs, ImGuiController, MainWindow, NativeWindow, string (+3 more)

### Community 119 - "ProjectPanel cluster"
Cohesion: 0.22
Nodes (5): bool, int, List, string, ProjectPanel

### Community 120 - "NativeDialogs cluster"
Cohesion: 0.13
Nodes (9): DllImport, Guid, int, IntPtr, uint, FileOpenDialog, IShellItem, NativeDialogs (+1 more)

### Community 121 - "SerializationContext cluster"
Cohesion: 0.20
Nodes (7): Type, Scene, SerializationContext, IAsset, KeyValuePair, Type, DocumentValues

### Community 122 - "CompactHeightfield cluster"
Cohesion: 0.16
Nodes (10): byte, float, Vector2, CompactHeightfield, bool, int, List, ushort (+2 more)

### Community 123 - "Focusable cluster"
Cohesion: 0.19
Nodes (5): FocusChangeDirection, bool, List, Focusable, FocusController

### Community 124 - "UIBuilder.cs cluster"
Cohesion: 0.20
Nodes (15): ImGuiIOPtr, ImDrawListPtr, Rect, Vector2, DrawBounds(), DrawCanvas(), DrawCanvasImage(), DrawOverlays() (+7 more)

### Community 125 - "TextureImporter cluster"
Cohesion: 0.16
Nodes (14): bool, float, int, List, string, Vector2, Vector2i, Vector4 (+6 more)

### Community 126 - "GameViewPanel cluster"
Cohesion: 0.12
Nodes (7): bool, NVector2, GameViewPanel, int, Matrix4, ulong, ViewRefresh

### Community 127 - "EnvironmentSettings cluster"
Cohesion: 0.16
Nodes (13): bool, Color4, float, Material, Vector3, EnvironmentSettings, SceneGISettings, SceneSettings (+5 more)

### Community 128 - "NavMath cluster"
Cohesion: 0.24
Nodes (6): float, Quaternion, ReadOnlySpan, Span, Vector3, NavMath

### Community 129 - "ShaderProperty cluster"
Cohesion: 0.16
Nodes (9): bool, float, IEnumerable, List, Regex, string, Vector4, ShaderProperty (+1 more)

### Community 130 - "PropertyPathAccess cluster"
Cohesion: 0.29
Nodes (8): Action, Func, IList, PropertyInfo, Type, MemberAccessor, PropertyPathAccess, MemberAccessor

### Community 131 - "IDisposable cluster"
Cohesion: 0.13
Nodes (8): IDisposable, bool, Dictionary, IEnumerable, List, Rect, Vector2, BuilderPreview

### Community 134 - "Documents cluster"
Cohesion: 0.24
Nodes (3): string, DocumentFormat, Documents

### Community 135 - "ScrollView cluster"
Cohesion: 0.19
Nodes (6): string, Vector2, Scroller, ScrollerVisibility, ScrollView, ScrollViewMode

### Community 136 - "Physics2DMath cluster"
Cohesion: 0.22
Nodes (7): B2Rot, B2Transform, B2Vector2, float, IReadOnlyList, List, Physics2DMath

### Community 137 - "Sprite cluster"
Cohesion: 0.12
Nodes (11): IAsset, bool, float, string, Vector2, Vector4, Sprite, int (+3 more)

### Community 138 - "GlobalIlluminationSettings cluster"
Cohesion: 0.21
Nodes (13): bool, Color4, float, int, string, Vector2, Vector3, GIMode (+5 more)

### Community 139 - "Cubemap cluster"
Cohesion: 0.16
Nodes (9): bool, byte, Color4, float, Func, TextureTarget, Vector3, Cubemap (+1 more)

### Community 140 - "Texture cluster"
Cohesion: 0.21
Nodes (9): bool, float, int, string, TextureTarget, FilterMode, Texture, TextureDimension (+1 more)

### Community 141 - "MainWindow cluster"
Cohesion: 0.13
Nodes (10): CancelEventArgs, FrameEventArgs, int, string, MainWindow, byte, int, string (+2 more)

### Community 142 - "NavMeshData cluster"
Cohesion: 0.29
Nodes (14): byte, float, int, Matrix4, object, Vector2, Vector3, NavMeshArea (+6 more)

### Community 143 - "RadianceCachePass cluster"
Cohesion: 0.15
Nodes (8): bool, ComputeShader, float, int, long, Renderer, Vector3, RadianceCachePass

### Community 144 - "TextElement cluster"
Cohesion: 0.22
Nodes (6): bool, int, MeasureMode, string, Vector2, TextElement

### Community 145 - "NavWanderer cluster"
Cohesion: 0.17
Nodes (9): bool, float, int, Random, Vector3, BakeOnStart, NavObstacleMover, NavStats (+1 more)

### Community 146 - "GIDebugControls cluster"
Cohesion: 0.16
Nodes (10): DebugView, bool, Dictionary, double, float, int, List, string (+2 more)

### Community 147 - "Collision2D cluster"
Cohesion: 0.22
Nodes (7): bool, List, Vector2, Collision2D, ContactPoint2D, bool, ContactEvent

### Community 148 - "Physics2DSettings cluster"
Cohesion: 0.21
Nodes (6): bool, float, int, Vector2, Physics2DSettings, SimulationPlane2D

### Community 149 - "ScreenProbeGatherPass cluster"
Cohesion: 0.15
Nodes (10): bool, ComputeShader, Framebuffer, int, IntPtr, Matrix4, Renderer, Vector2 (+2 more)

### Community 150 - "DataBinding cluster"
Cohesion: 0.27
Nodes (6): bool, Delegate, object, BindingContext, BindingMode, DataBinding

### Community 152 - "FpsController cluster"
Cohesion: 0.23
Nodes (5): bool, float, Vector2, Vector3, FpsController

### Community 153 - "HierarchyPanel cluster"
Cohesion: 0.27
Nodes (5): bool, GameObject, int, string, HierarchyPanel

### Community 154 - "ConsoleHost cluster"
Cohesion: 0.32
Nodes (5): DllImport, int, IntPtr, uint, ConsoleHost

### Community 155 - "VfxSpawnerState cluster"
Cohesion: 0.22
Nodes (5): bool, float, Phase, VfxSpawnerState, Phase

### Community 156 - "GameInputHost cluster"
Cohesion: 0.17
Nodes (9): IInputHost, CursorState, Func, Keys, MouseButton, MouseState, NativeWindow, Vector2i (+1 more)

### Community 157 - ".OnAfterDeserialize() cluster"
Cohesion: 0.24
Nodes (4): Behaviour, Scene, EditorSerialization, ISerializationCallbackReceiver

### Community 158 - "Foldout cluster"
Cohesion: 0.21
Nodes (6): bool, string, Foldout, EventCallback, INotifyValueChanged, INotifyValueChangedExtensions

### Community 159 - "Physics2DHarness cluster"
Cohesion: 0.21
Nodes (7): bool, float, int, List, CollisionLogger, Physics2DHarness, PlatformMover

### Community 160 - "UIShowcase cluster"
Cohesion: 0.30
Nodes (8): bool, float, int, string, HudController, HudModel, ShowcaseSettings, UIShowcase

### Community 161 - "Attribute cluster"
Cohesion: 0.29
Nodes (10): Attribute, float, string, ExecuteAlways, HeaderAttribute, HideFlags, HideInInspector, RangeAttribute (+2 more)

### Community 162 - "Editor screenshot (scene view) cluster"
Cohesion: 0.20
Nodes (11): Editor screenshot (scene view), Console panel (SDF, RadianceCache, ProbeGather logs), Hierarchy panel, Inspector panel (Transform, Mesh Renderer), Scene view with transform gizmo, Lumen-style GI render (Cornell-box scene), Real-time global illumination with color bleeding, SDF tracing and radiance cache probes (+3 more)

### Community 163 - "Editor cluster"
Cohesion: 0.22
Nodes (5): Behaviour, string, Type, CustomEditorAttribute, Editor

### Community 164 - "EditorGUILayout cluster"
Cohesion: 0.25
Nodes (3): Behaviour, bool, EditorGUILayout

### Community 165 - ".BuildMenus() cluster"
Cohesion: 0.20
Nodes (10): byte, int, List, MethodInfo, string, MenuNode, WindowState, method (+2 more)

### Community 166 - ".Main() cluster"
Cohesion: 0.31
Nodes (3): Renderer, Game, Program

### Community 167 - "VertexAttribute cluster"
Cohesion: 0.18
Nodes (7): bool, int, VertexAttribute, int, string, VertexLayout, VertexAttribPointerType

### Community 168 - "LayoutNodeData cluster"
Cohesion: 0.27
Nodes (7): bool, float, int, MeasureMode, LayoutNodeData, MeasureCacheEntry, MeasureCacheEntry

### Community 169 - "GlyphSdf cluster"
Cohesion: 0.40
Nodes (5): float, List, GlyphSdf, Segment, Segment

### Community 170 - "GLCapabilities cluster"
Cohesion: 0.29
Nodes (4): GetPName, HashSet, int, GLCapabilities

### Community 172 - "VfxIntrinsics cluster"
Cohesion: 0.49
Nodes (3): Dictionary, Func, VfxIntrinsics

### Community 173 - "TextureImporterInspector cluster"
Cohesion: 0.28
Nodes (6): bool, int, string, TextureImporter, Vector2i, TextureImporterInspector

### Community 174 - "LayerMask cluster"
Cohesion: 0.25
Nodes (3): int, string, LayerMask

### Community 175 - "ConverterGroup cluster"
Cohesion: 0.28
Nodes (5): Dictionary, Func, Type, ConverterGroup, ConverterGroups

### Community 177 - ".MapKey() cluster"
Cohesion: 0.32
Nodes (4): ImGuiKey, KeyboardKeyEventArgs, KeyModifiers, Keys

### Community 179 - "TypeRegistry cluster"
Cohesion: 0.36
Nodes (4): Assembly, IReadOnlyList, List, TypeRegistry

### Community 180 - "CustomInspectors.cs cluster"
Cohesion: 0.25
Nodes (5): Behaviour, Dictionary, CustomInspectors, NavMeshAgentInspector, NavMeshSurfaceInspector

### Community 181 - "Extensions cluster"
Cohesion: 0.29
Nodes (3): Quaternion, Vector3, Extensions

### Community 183 - ".Update() cluster"
Cohesion: 0.36
Nodes (3): Dictionary, MethodInfo, BindingConversion

### Community 184 - "VFX graph node editor cluster"
Cohesion: 0.33
Nodes (7): VFX graph node editor, VFX blocks (turbulence, gravity, set lifetime, color over life), VFX node graph (Spawn, Initialize, Update, Output contexts), Exposed property nodes (Rate, Color), VFX showcase render (cubes, ribbons, fireworks), GPU particle effects (mesh cubes, ribbons, fireworks, puffs), Particle shadows on ground

### Community 185 - "EditorStyle cluster"
Cohesion: 0.29
Nodes (3): ImGuiCol, NVector4, EditorStyle

### Community 187 - "VfxInstance.cs cluster"
Cohesion: 0.29
Nodes (4): Texture2D, Texture3D, VfxDefaults, Texture2D

### Community 189 - ".Measure() cluster"
Cohesion: 0.40
Nodes (3): MeasureMode, Vector2, Vector2

### Community 190 - ".Of() cluster"
Cohesion: 0.40
Nodes (3): Array, Span, ArrayBytes

### Community 191 - "Resources cluster"
Cohesion: 0.40
Nodes (4): LELEngine.Properties, CultureInfo, Resources, ResourceManager

### Community 192 - ".ListMembers() cluster"
Cohesion: 0.50
Nodes (3): List, name, writable

## Knowledge Gaps
- **59 isolated node(s):** `net10.0`, `OpenTK (4.9.4)`, `Microsoft.NET.Sdk`, `net10.0`, `OpenTK (4.9.4)` (+54 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **9 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `VisualElement` connect `UI Hierarchy & Bindings` to `UI Inline Styles`, `UI Style Resolution`, `IDisposable cluster`, `ScrollView cluster`, `UI Stylesheets & Theme`, `TextElement cluster`, `Flex Layout`, `DataBinding cluster`, `UI Element Pooling`, `Foldout cluster`, `UIShowcase cluster`, `Window Input Events`, `UI Event Dispatch`, `LayoutNodeData cluster`, `BaseVisualElementPanel cluster`, `PropertyPath cluster`, `.LogWarning() cluster`, `LELEngine.UIElements cluster`, `.Update() cluster`, `TextInput cluster`, `.Measure() cluster`, `InheritedStyle cluster`, `BaseSlider cluster`, `ListView cluster`, `Painter2D cluster`, `FontAsset cluster`, `Binding cluster`, `UQueryBuilder cluster`, `BaseField cluster`, `UIDocument cluster`, `EventModifiers cluster`, `IVisualElementScheduledItem cluster`, `BasePopupField cluster`, `EventCallbackRegistry cluster`, `.DrawTypedAttribute() cluster`, `Manipulators.cs cluster`, `.StopPropagation() cluster`, `Angle cluster`, `TextLayoutResult cluster`, `Focusable cluster`?**
  _High betweenness centrality (0.201) - this node is a cross-community bridge._
- **Why does `ShaderProgram` connect `Builtin Shaders` to `ShaderProperty cluster`, `Collider Shapes & Uniforms`, `Lighting & GI Uniforms`, `VoxelGIPass cluster`, `UI Stylesheets & Theme`, `Framebuffers`, `Renderer & Debug Draw`, `MeshRenderer cluster`, `ShaderRenderState cluster`, `Material cluster`, `ScreenProbeGatherPass cluster`, `View Renderer`, `MaterialProperty cluster`, `GpuScene cluster`, `UI Rendering Data`, `InternalStorage cluster`, `VFX Runtime`, `Shared Value Types`?**
  _High betweenness centrality (0.102) - this node is a cross-community bridge._
- **Why does `EditorApplication` connect `UI Builder Window` to `IDisposable cluster`, `.OnRenderFrame() cluster`, `Game Assembly Loading`, `MainWindow cluster`, `Physics Benchmarks`, `HierarchyPanel cluster`, `GameInputHost cluster`, `Window Input Events`, `Components & Behaviours`, `AssetDatabase cluster`, `ConsoleLog cluster`, `.Apply() cluster`, `ProjectSettingsWindow cluster`, `SceneViewPanel cluster`, `ObjectPicker cluster`, `LELEngine.Editor cluster`, `InspectorContext cluster`, `GameHost cluster`, `.LogException() cluster`, `.Draw() cluster`, `EditorExtensions cluster`, `.OpenProject() cluster`, `ProjectPanel cluster`, `GameViewPanel cluster`?**
  _High betweenness centrality (0.079) - this node is a cross-community bridge._
- **What connects `net10.0`, `OpenTK (4.9.4)`, `Microsoft.NET.Sdk` to the rest of the system?**
  _59 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `UI Inline Styles` be split into smaller, more focused modules?**
  _Cohesion score 0.03985072637611622 - nodes in this community are weakly interconnected._
- **Should `UI Style Resolution` be split into smaller, more focused modules?**
  _Cohesion score 0.038461538461538464 - nodes in this community are weakly interconnected._
- **Should `UI Hierarchy & Bindings` be split into smaller, more focused modules?**
  _Cohesion score 0.03306939916162087 - nodes in this community are weakly interconnected._