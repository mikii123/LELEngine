using System;
using System.Collections.Generic;
using LELEngine.Rendering.DistanceField;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Lumen
{
	/// <summary>
	///     The scene as the GI sees it (Lumen's "Lumen Scene"): every renderer that contributes to GI, with its
	///     mesh distance field and surface cache cards, uploaded once per frame as GPU tables shared by the
	///     global distance field, the surface cache lighting and the probe gather
	///     (Engine/SceneObjects.glsl, SSBO bindings 1 and 2).
	/// </summary>
	public sealed class LumenScene
	{
		#region PublicFields

		public DistanceFieldAtlas DistanceFields { get; private set; }

		/// <summary>Lumen's surface cache; null unless Lumen runs (voxel cone tracing reads only the distance fields).</summary>
		public SurfaceCacheAtlas SurfaceCache { get; private set; }

		public int ObjectBuffer { get; private set; }
		public int CardBuffer { get; private set; }

		/// <summary>The GPU resources exist: made by the first <see cref="Update" />, never by a renderer whose GI needs no scene.</summary>
		public bool Allocated => DistanceFields != null;

		/// <summary>Objects in table order: static ones first, then dynamic ones.</summary>
		public IReadOnlyList<SceneObject> Objects => objects;

		public int StaticObjectCount { get; private set; }
		public int UpdatedFrame { get; private set; } = -1;

		/// <summary>
		///     True when a dynamic object moved, rotated, rescaled, appeared or disappeared since the previous update.
		///     Consumers that only depend on object placement (dynamic distance field regions) skip their work otherwise.
		/// </summary>
		public bool DynamicChanged { get; private set; } = true;

		/// <summary>True when any GI object's material colour or emission changed since the previous update.</summary>
		public bool MaterialChanged { get; private set; } = true;

		/// <summary>True when the directional light moved or changed colour or strength since the previous update.</summary>
		public bool SunChanged { get; private set; } = true;

		/// <summary>Frames in a row without object, material or sun changes; caches drop to idle budgets when high.</summary>
		public int QuietFrames { get; private set; }

		public bool IsQuiet(int frames)
		{
			return QuietFrames >= frames;
		}

		/// <summary>
		///     World-space boxes around what changed the lighting this frame: where dynamic objects were and are (when they
		///     moved), and objects whose material (colour, emission) changed. The radiance cache updates its probes there first.
		/// </summary>
		public IReadOnlyList<ChangeRegion> ChangeRegions => changeRegions;

		/// <summary>World-space bounds of the static GI objects (the "scene" the radiance cache concentrates on).</summary>
		public bool HasStaticBounds { get; private set; }

		public Vector3 StaticBoundsMin { get; private set; }
		public Vector3 StaticBoundsMax { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<SceneObject> objects = new List<SceneObject>();
		private readonly List<SceneObjectData> objectData = new List<SceneObjectData>();
		private readonly Dictionary<MeshRenderer, TransformState> lastTransforms = new Dictionary<MeshRenderer, TransformState>();
		private readonly Dictionary<MeshRenderer, MaterialState> lastMaterials = new Dictionary<MeshRenderer, MaterialState>();

		// Frame of each dynamic object's last move: the object table flags objects that moved this frame or the one
		// before (cardInfo.w), so the probes that see them keep a short history.
		private readonly Dictionary<MeshRenderer, int> lastMoveFrames = new Dictionary<MeshRenderer, int>();

		// Each dynamic object's world bounds last frame (the region a move leaves), and this frame's change regions.
		private readonly Dictionary<MeshRenderer, ChangeRegion> lastBounds = new Dictionary<MeshRenderer, ChangeRegion>();
		private readonly List<ChangeRegion> changeRegions = new List<ChangeRegion>();
		private int lastDynamicCount = -1;
		private int lastStaticVersion = -1;
		private Vector3 lastSunDirection;
		private float lastSunStrength;
		private Color4 lastSunColor;
		// Never empty, like the object table: the card binding always has storage, also without cards (voxel cone tracing).
		private CardData[] cardData = new CardData[1];
		private int surfaceCacheSize;
		private int objectBufferBytes;
		private int cardBufferBytes;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Rebuilds the object and card tables for this frame (idempotent per frame).
		/// </summary>
		public void Update(int frame, IReadOnlyList<MeshRenderer> renderers, GlobalIlluminationSettings gi)
		{
			if (UpdatedFrame == frame)
			{
				return;
			}
			UpdatedFrame = frame;

			if (DistanceFields == null)
			{
				DistanceFields = new DistanceFieldAtlas();
				ObjectBuffer = GL.GenBuffer();
				CardBuffer = GL.GenBuffer();
			}

			if (!gi.LumenActive)
			{
				ReleaseSurfaceCache();
			}
			else if (SurfaceCache == null || surfaceCacheSize != gi.SurfaceCacheAtlasSize)
			{
				SurfaceCache?.Delete();
				surfaceCacheSize = gi.SurfaceCacheAtlasSize;
				SurfaceCache = new SurfaceCacheAtlas(surfaceCacheSize);
			}

			objects.Clear();
			changeRegions.Clear();
			CollectObjects(renderers, true, gi);
			StaticObjectCount = objects.Count;
			CollectObjects(renderers, false, gi);
			DynamicChanged = DetectDynamicChanges();

			UploadTables();

			Vector3 sunDirection = DirectionalLight.This != null ? DirectionalLight.This.transform.forward : -Vector3.UnitY;
			SunChanged = (sunDirection - lastSunDirection).LengthSquared > 1e-8f || Lighting.Directional.Strength != lastSunStrength || Lighting.Directional.Color != lastSunColor;
			lastSunDirection = sunDirection;
			lastSunStrength = Lighting.Directional.Strength;
			lastSunColor = Lighting.Directional.Color;
			// A static invalidation (objects moved via InvalidateStatic) changes the lighting as much as a dynamic move.
			bool staticChanged = gi.StaticVersion != lastStaticVersion;
			lastStaticVersion = gi.StaticVersion;
			QuietFrames = DynamicChanged || MaterialChanged || SunChanged || staticChanged ? 0 : QuietFrames + 1;
		}

		/// <summary>Something outside the tracked state changed the lighting (static field rebuilt): restart the quiet count.</summary>
		public void MarkChanged()
		{
			QuietFrames = 0;
		}

		/// <summary>
		///     Binds the tables and sets the shared uniforms for shaders that include Engine/SceneObjects.glsl.
		/// </summary>
		public void SetUniforms(ShaderProgram program)
		{
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, ObjectBuffer);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, CardBuffer);
			program.SetInt("sceneObjectCount", objects.Count);
			program.SetTexture("SdfAtlas", TextureTarget.Texture3D, DistanceFields.Texture, SdfAtlasTextureUnit);
			program.SetVector3("sdfAtlasTexels", DistanceFields.TexelCount);
			if (SurfaceCache != null)
			{
				program.SetVector2("surfaceCacheAtlasSize", new Vector2(SurfaceCache.Size, SurfaceCache.Size));
				program.SetTexture("CardLocalPosition", TextureTarget.Texture2D, SurfaceCache.LocalPosition, CardPositionTextureUnit);
			}
		}

		/// <summary>Frees Lumen's surface cache (the objects lose their cards); voxel cone tracing keeps the rest of the scene.</summary>
		public void ReleaseSurfaceCache()
		{
			if (SurfaceCache == null)
			{
				return;
			}

			SurfaceCache.Delete();
			SurfaceCache = null;
			Console.WriteLine("[SurfaceCache] Atlas released");
		}

		public void Delete()
		{
			DistanceFields?.Delete();
			SurfaceCache?.Delete();
			if (ObjectBuffer != 0)
			{
				GL.DeleteBuffer(ObjectBuffer);
			}
			if (CardBuffer != 0)
			{
				GL.DeleteBuffer(CardBuffer);
			}
		}

		#endregion

		#region PrivateFields

		/// <summary>Texture units used by the shared scene uniforms (materials never bind this high).</summary>
		public const int SdfAtlasTextureUnit = 10;

		public const int CardPositionTextureUnit = 9;

		#endregion

		#region PrivateMethods

		private void CollectObjects(IReadOnlyList<MeshRenderer> renderers, bool wantStatic, GlobalIlluminationSettings gi)
		{
			foreach (MeshRenderer renderer in renderers)
			{
				if (renderer.IsStatic != wantStatic || !renderer.ContributesToGI || renderer.Mesh == null || renderer.Mesh.TriangleCount == 0 || renderer.Materials.Count == 0)
				{
					continue;
				}

				MeshDistanceField field = DistanceFields.GetOrBuild(renderer.Mesh, renderer.transform.scale);
				if (field == null)
				{
					continue;
				}

				// Cards only with Lumen's surface cache (see Update).
				SurfaceCardSet cards = SurfaceCache?.GetOrAllocate(renderer, field, gi.SurfaceCacheTexelsPerMeter, gi.SurfaceCacheMaxCardSize);

				objects.Add(new SceneObject
				{
					Index = objects.Count,
					Renderer = renderer,
					Field = field,
					Cards = cards,
					IsStatic = wantStatic
				});
			}
		}

		/// <summary>
		///     The lighting inputs of the renderer's opaque materials: colour and emission summed with per-slot weights
		///     (any change of one changes them), and the strongest emission intensity.
		/// </summary>
		private static void MaterialInputs(MeshRenderer renderer, out Vector4 color, out Vector4 emissive, out float emissiveIntensity)
		{
			color = Vector4.Zero;
			emissive = Vector4.Zero;
			emissiveIntensity = 0f;
			for (int i = 0; i < renderer.SubMeshCount; i++)
			{
				Shaders.Material material = renderer.GetMaterial(i);
				if (material == null || material.IsTransparent)
				{
					continue;
				}

				if (!material.TryGetVector4("Color", out Vector4 c)) c = Vector4.One;
				if (!material.TryGetVector4("Emissive", out Vector4 e)) e = Vector4.Zero;
				color += c * (i + 1);
				emissive += e * (i + 1);
				emissiveIntensity = Math.Max(emissiveIntensity, e.W);
			}
		}

		private static void Prune<TValue>(Dictionary<MeshRenderer, TValue> table, HashSet<MeshRenderer> alive)
		{
			var dead = new List<MeshRenderer>();
			foreach (MeshRenderer renderer in table.Keys)
			{
				if (!alive.Contains(renderer))
				{
					dead.Add(renderer);
				}
			}

			foreach (MeshRenderer renderer in dead)
			{
				table.Remove(renderer);
			}
		}

		// Compares every dynamic object's transform with the one seen last frame.
		private bool DetectDynamicChanges()
		{
			bool changed = false;
			int dynamicCount = 0;
			for (int i = StaticObjectCount; i < objects.Count; i++)
			{
				MeshRenderer renderer = objects[i].Renderer;
				Transform t = renderer.transform;
				TransformState last;
				if (!lastTransforms.TryGetValue(renderer, out last) || last.Position != t.position || last.Rotation != t.rotation || last.Scale != t.scale)
				{
					changed = true;
					lastTransforms[renderer] = new TransformState { Position = t.position, Rotation = t.rotation, Scale = t.scale };
					lastMoveFrames[renderer] = UpdatedFrame;

					// The lighting changes where the object was and where it is now.
					ChangeRegion bounds = WorldBounds(objects[i]);
					if (lastBounds.TryGetValue(renderer, out ChangeRegion previous))
					{
						changeRegions.Add(previous);
					}
					changeRegions.Add(bounds);
					lastBounds[renderer] = bounds;
				}
				dynamicCount++;
			}

			if (dynamicCount != lastDynamicCount)
			{
				changed = true;
				lastDynamicCount = dynamicCount;
			}

			// Destroyed renderers (or renderers that became static / stopped contributing) drop out of the
			// change-tracking tables. Their surface cache cards stay allocated until the scene caches are reset
			// (Renderer.InvalidateSceneCaches).
			if (lastTransforms.Count > dynamicCount || lastMaterials.Count > objects.Count)
			{
				var alive = new HashSet<MeshRenderer>();
				foreach (SceneObject o in objects)
				{
					alive.Add(o.Renderer);
				}

				Prune(lastTransforms, alive);
				Prune(lastMaterials, alive);
				Prune(lastMoveFrames, alive);
				Prune(lastBounds, alive);
			}

			return changed;
		}

		/// <summary>The object's world-space bounding box (its scaled mesh bounds through its rotation and position).</summary>
		private static ChangeRegion WorldBounds(SceneObject o)
		{
			Transform t = o.Renderer.transform;
			Matrix4 localToWorld = Matrix4.CreateFromQuaternion(t.rotation) * Matrix4.CreateTranslation(t.position);
			Vector3 scale = o.Field.Scale;
			Vector3 bmin = o.Renderer.Mesh.BoundsMin * scale;
			Vector3 bmax = o.Renderer.Mesh.BoundsMax * scale;
			Vector3 min = new Vector3(float.MaxValue);
			Vector3 max = new Vector3(float.MinValue);
			for (int corner = 0; corner < 8; corner++)
			{
				Vector3 local = new Vector3((corner & 1) != 0 ? bmax.X : bmin.X, (corner & 2) != 0 ? bmax.Y : bmin.Y, (corner & 4) != 0 ? bmax.Z : bmin.Z);
				Vector3 world = Vector3.TransformPosition(local, localToWorld);
				min = Vector3.ComponentMin(min, world);
				max = Vector3.ComponentMax(max, world);
			}

			return new ChangeRegion(min, max);
		}

		/// <summary>A dynamic object moved this frame or the one before (the surface cache and the caches lag one frame).</summary>
		private bool MovedRecently(SceneObject o)
		{
			return !o.IsStatic && lastMoveFrames.TryGetValue(o.Renderer, out int frame) && UpdatedFrame - frame <= 1;
		}

		private void UploadTables()
		{
			objectData.Clear();
			int cardCount = SurfaceCache?.CardCount ?? 0;
			if (cardData.Length < cardCount)
			{
				Array.Resize(ref cardData, Math.Max(cardCount, 6));
			}

			Vector3 staticMin = new Vector3(float.MaxValue);
			Vector3 staticMax = new Vector3(float.MinValue);
			HasStaticBounds = false;
			bool materialChanged = false;

			foreach (SceneObject o in objects)
			{
				Transform t = o.Renderer.transform;
				Matrix4 localToWorld = Matrix4.CreateFromQuaternion(t.rotation) * Matrix4.CreateTranslation(t.position);
				Vector3 scale = o.Field.Scale;

				// Material state that changes the lighting (colour, emission colour and intensity).
				MaterialInputs(o.Renderer, out Vector4 color, out Vector4 emissiveState, out float emissiveIntensity);
				MaterialState last;
				if (!lastMaterials.TryGetValue(o.Renderer, out last) || last.Color != color || last.Emissive != emissiveState)
				{
					materialChanged = true;
					lastMaterials[o.Renderer] = new MaterialState { Color = color, Emissive = emissiveState };
					changeRegions.Add(WorldBounds(o));
				}

				if (o.IsStatic)
				{
					Vector3 bmin = o.Renderer.Mesh.BoundsMin * scale;
					Vector3 bmax = o.Renderer.Mesh.BoundsMax * scale;
					for (int corner = 0; corner < 8; corner++)
					{
						Vector3 local = new Vector3((corner & 1) != 0 ? bmax.X : bmin.X, (corner & 2) != 0 ? bmax.Y : bmin.Y, (corner & 4) != 0 ? bmax.Z : bmin.Z);
						Vector3 world = Vector3.TransformPosition(local, localToWorld);
						staticMin = Vector3.ComponentMin(staticMin, world);
						staticMax = Vector3.ComponentMax(staticMax, world);
					}
					HasStaticBounds = true;
				}

				// Emission intensity travels in the table so pulsing emitters never recapture their cards.

				objectData.Add(new SceneObjectData
				{
					WorldToLocal = Matrix4.Invert(localToWorld),
					LocalToWorld = localToWorld,
					BoundsMin = new Vector4(o.Field.BoundsMin, 0f),
					BoundsMax = new Vector4(o.Field.BoundsMax, 0f),
					AtlasOrigin = new Vector4(0f, 0f, o.Field.AtlasZ, 0f),
					AtlasSize = new Vector4(o.Field.Size.X, o.Field.Size.Y, o.Field.Size.Z, 0f),
					Padding = new Vector4(o.Field.TexelSize * DistanceFieldAtlas.Padding, emissiveIntensity),
					MeshBoundsMin = new Vector4(o.Renderer.Mesh.BoundsMin * scale, 0f),
					MeshBoundsMax = new Vector4(o.Renderer.Mesh.BoundsMax * scale, 0f),
					CardInfo = new Vector4i(o.Cards?.FirstCardIndex ?? 0, o.Cards?.Cards.Length ?? 0, o.IsStatic ? 0 : 1, MovedRecently(o) ? 1 : 0)
				});

				if (o.Cards == null)
				{
					continue;
				}

				for (int i = 0; i < o.Cards.Cards.Length; i++)
				{
					SurfaceCard card = o.Cards.Cards[i];
					cardData[o.Cards.FirstCardIndex + i] = new CardData
					{
						Rect = new Vector4i(card.X, card.Y, card.Width, card.Height),
						AxisX = new Vector4(card.AxisX, card.ExtentU),
						AxisY = new Vector4(card.AxisY, card.ExtentV),
						AxisZ = new Vector4(card.AxisZ, card.ExtentDepth),
						Origin = new Vector4(card.Center, o.Index)
					};
				}
			}

			if (HasStaticBounds)
			{
				StaticBoundsMin = staticMin;
				StaticBoundsMax = staticMax;
			}
			MaterialChanged = materialChanged;

			// The tables are re-specified only when they grow; otherwise the existing storage is updated in place
			// (glBufferData every frame makes the driver allocate and orphan two buffers per frame).
			SceneObjectData[] objectArray = objectData.Count > 0 ? objectData.ToArray() : new SceneObjectData[1];
			int objectBytes = objectArray.Length * SceneObjectData.Size;
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ObjectBuffer);
			if (objectBytes > objectBufferBytes)
			{
				GL.BufferData(BufferTarget.ShaderStorageBuffer, objectBytes, objectArray, BufferUsageHint.DynamicDraw);
				objectBufferBytes = objectBytes;
			}
			else
			{
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, objectBytes, objectArray);
			}

			int cardBytes = cardData.Length * CardData.Size;
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, CardBuffer);
			if (cardBytes > cardBufferBytes)
			{
				GL.BufferData(BufferTarget.ShaderStorageBuffer, cardBytes, cardData, BufferUsageHint.DynamicDraw);
				cardBufferBytes = cardBytes;
			}
			else
			{
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, cardBytes, cardData);
			}
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		#endregion

		#region NestedTypes

		public sealed class SceneObject
		{
			public int Index;
			public MeshRenderer Renderer;
			public MeshDistanceField Field;
			public SurfaceCardSet Cards;
			public bool IsStatic;
		}

		/// <summary>A world-space box whose lighting changed this frame (see <see cref="ChangeRegions" />).</summary>
		public readonly struct ChangeRegion
		{
			public ChangeRegion(Vector3 min, Vector3 max)
			{
				Min = min;
				Max = max;
			}

			public Vector3 Min { get; }
			public Vector3 Max { get; }
			public Vector3 Center => (Min + Max) * 0.5f;
		}

		private struct TransformState
		{
			public Vector3 Position;
			public Quaternion Rotation;
			public Vector3 Scale;
		}

		private struct MaterialState
		{
			public Vector4 Color;
			public Vector4 Emissive;
		}

		/// <summary>Mirrors SceneObject in Engine/SceneObjects.glsl (std430, 256 bytes).</summary>
		private struct SceneObjectData
		{
			public const int Size = 256;

			public Matrix4 WorldToLocal;
			public Matrix4 LocalToWorld;
			public Vector4 BoundsMin;
			public Vector4 BoundsMax;
			public Vector4 AtlasOrigin;
			public Vector4 AtlasSize;
			public Vector4 Padding;
			public Vector4 MeshBoundsMin;
			public Vector4 MeshBoundsMax;
			public Vector4i CardInfo;
		}

		/// <summary>Mirrors Card in Engine/SceneObjects.glsl (std430, 80 bytes).</summary>
		private struct CardData
		{
			public const int Size = 80;

			public Vector4i Rect;
			public Vector4 AxisX;
			public Vector4 AxisY;
			public Vector4 AxisZ;
			public Vector4 Origin;
		}

		#endregion
	}
}
