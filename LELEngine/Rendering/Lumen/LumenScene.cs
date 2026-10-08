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

		public DistanceFieldAtlas DistanceFields { get; }
		public SurfaceCacheAtlas SurfaceCache { get; private set; }

		public int ObjectBuffer { get; }
		public int CardBuffer { get; }

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
		private int lastDynamicCount = -1;
		private int lastStaticVersion = -1;
		private Vector3 lastSunDirection;
		private float lastSunStrength;
		private Color4 lastSunColor;
		private CardData[] cardData = new CardData[0];
		private int surfaceCacheSize;
		private int objectBufferBytes;
		private int cardBufferBytes;

		#endregion

		#region Constructors

		public LumenScene()
		{
			DistanceFields = new DistanceFieldAtlas();
			ObjectBuffer = GL.GenBuffer();
			CardBuffer = GL.GenBuffer();
		}

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

			if (SurfaceCache == null || surfaceCacheSize != gi.SurfaceCacheAtlasSize)
			{
				SurfaceCache?.Delete();
				surfaceCacheSize = gi.SurfaceCacheAtlasSize;
				SurfaceCache = new SurfaceCacheAtlas(surfaceCacheSize);
			}

			objects.Clear();
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

		public void Delete()
		{
			DistanceFields.Delete();
			SurfaceCache?.Delete();
			GL.DeleteBuffer(ObjectBuffer);
			GL.DeleteBuffer(CardBuffer);
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
				if (renderer.IsStatic != wantStatic || !renderer.ContributesToGI || renderer.Mesh == null || renderer.Mesh.Verticies.Count < 3 || renderer.Material == null)
				{
					continue;
				}

				MeshDistanceField field = DistanceFields.GetOrBuild(renderer.Mesh, renderer.transform.scale);
				if (field == null)
				{
					continue;
				}

				SurfaceCardSet cards = SurfaceCache.GetOrAllocate(renderer, field, gi.SurfaceCacheTexelsPerMeter, gi.SurfaceCacheMaxCardSize);

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
			}

			return changed;
		}

		private void UploadTables()
		{
			objectData.Clear();
			if (cardData.Length < SurfaceCache.CardCount)
			{
				Array.Resize(ref cardData, Math.Max(SurfaceCache.CardCount, 6));
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
				Vector4 color, emissiveState;
				if (o.Renderer.Material == null || !o.Renderer.Material.TryGetVector4("Color", out color)) color = Vector4.One;
				if (o.Renderer.Material == null || !o.Renderer.Material.TryGetVector4("Emissive", out emissiveState)) emissiveState = Vector4.Zero;
				MaterialState last;
				if (!lastMaterials.TryGetValue(o.Renderer, out last) || last.Color != color || last.Emissive != emissiveState)
				{
					materialChanged = true;
					lastMaterials[o.Renderer] = new MaterialState { Color = color, Emissive = emissiveState };
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
				Vector4 emissive;
				float emissiveIntensity = o.Renderer.Material != null && o.Renderer.Material.TryGetVector4("Emissive", out emissive) ? emissive.W : 0f;

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
					CardInfo = new Vector4i(o.Cards?.FirstCardIndex ?? 0, o.Cards?.Cards.Length ?? 0, o.IsStatic ? 0 : 1, 0)
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
