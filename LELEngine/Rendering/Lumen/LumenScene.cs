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

		/// <summary>World-space bounds of the static GI objects (the "scene" the radiance cache concentrates on).</summary>
		public bool HasStaticBounds { get; private set; }

		public Vector3 StaticBoundsMin { get; private set; }
		public Vector3 StaticBoundsMax { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<SceneObject> objects = new List<SceneObject>();
		private readonly List<SceneObjectData> objectData = new List<SceneObjectData>();
		private CardData[] cardData = new CardData[0];
		private int surfaceCacheSize;

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

			UploadTables();
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

			foreach (SceneObject o in objects)
			{
				Transform t = o.Renderer.transform;
				Matrix4 localToWorld = Matrix4.CreateFromQuaternion(t.rotation) * Matrix4.CreateTranslation(t.position);
				Vector3 scale = o.Field.Scale;

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

				objectData.Add(new SceneObjectData
				{
					WorldToLocal = Matrix4.Invert(localToWorld),
					LocalToWorld = localToWorld,
					BoundsMin = new Vector4(o.Field.BoundsMin, 0f),
					BoundsMax = new Vector4(o.Field.BoundsMax, 0f),
					AtlasOrigin = new Vector4(0f, 0f, o.Field.AtlasZ, 0f),
					AtlasSize = new Vector4(o.Field.Size.X, o.Field.Size.Y, o.Field.Size.Z, 0f),
					Padding = new Vector4(o.Field.TexelSize * DistanceFieldAtlas.Padding, 0f),
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

			SceneObjectData[] objectArray = objectData.Count > 0 ? objectData.ToArray() : new SceneObjectData[1];
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ObjectBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, objectArray.Length * SceneObjectData.Size, objectArray, BufferUsageHint.StreamDraw);

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, CardBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, cardData.Length * CardData.Size, cardData, BufferUsageHint.StreamDraw);
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
