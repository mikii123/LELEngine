using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Mathematics;

/// <summary>Which way the strip of a <see cref="TrailRenderer" /> faces (Unity's LineAlignment).</summary>
public enum LineAlignment
{
	/// <summary>Towards the camera at every point.</summary>
	View = 0,

	/// <summary>Along the Z axis the transform had when each point was added.</summary>
	TransformZ = 1
}

/// <summary>How a <see cref="TrailRenderer" /> lays its texture along the trail (Unity's LineTextureMode).</summary>
public enum LineTextureMode
{
	/// <summary>Once over the whole trail (u 0 at the object, 1 at the end).</summary>
	Stretch = 0,

	/// <summary>Once per world unit of length (times Texture Scale x).</summary>
	Tile = 1,

	/// <summary>Once over the whole trail, as if its points were evenly spaced.</summary>
	DistributePerSegment = 2,

	/// <summary>Once per segment between two points (times Texture Scale x).</summary>
	RepeatPerSegment = 3
}

/// <summary>
///     A trail behind a moving object (Unity's TrailRenderer): while <see cref="emitting" />, every frame the object
///     has moved <see cref="minVertexDistance" /> from the last point adds a point, and every point is removed
///     <see cref="time" /> seconds after it was added. The trail runs from the object (start: width curve and gradient at
///     0) through the points, newest first, to the oldest one (end: 1), parameterized by length; it is a strip as wide as
///     <see cref="widthMultiplier" /> x <see cref="widthCurve" /> facing the camera (or the transform's Z axis), with
///     rounded corners and ends when asked, built anew for every camera. Each material draws the whole trail, sorted
///     among the transparent objects by its queue and the trail's bounds; a trail with an opaque material draws in the
///     transparent pass too, before the transparent queues (no depth prepass, shadows or GI from trails). Points are in
///     world space: moving the object never moves the trail it left. Points survive the component being disabled
///     (they keep ageing; <see cref="Clear" /> empties the trail, e.g. for pooled objects).
///     Not ported: shadow casting and bias, lighting data options (normals and tangents are always there), the Static
///     texture mode, BakeMesh, sorting layers.
/// </summary>
[ExecuteAlways]
[InspectorColor(InspectorColorKind.Rendering)]
public sealed class TrailRenderer : Behaviour
{
	#region PublicFields

	/// <summary>Seconds a point lives (the length of the trail in time).</summary>
	public float time
	{
		get => m_Time;
		set => m_Time = Math.Max(0f, value);
	}

	/// <summary>How far the object must move from the last point before a new point is added.</summary>
	public float minVertexDistance
	{
		get => m_MinVertexDistance;
		set => m_MinVertexDistance = Math.Max(0f, value);
	}

	/// <summary>Destroys the GameObject (in play mode) once its trail is gone (the object stayed still for <see cref="time" />).</summary>
	public bool autodestruct
	{
		get => m_Autodestruct;
		set => m_Autodestruct = value;
	}

	/// <summary>New points are added while the object moves; off, the trail stays where it is and fades away.</summary>
	public bool emitting
	{
		get => m_Emitting;
		set => m_Emitting = value;
	}

	/// <summary>Scales <see cref="widthCurve" />.</summary>
	public float widthMultiplier
	{
		get => m_WidthMultiplier;
		set => m_WidthMultiplier = Math.Max(0f, value);
	}

	/// <summary>Width along the trail (0 at the object, 1 at its end), times <see cref="widthMultiplier" />. A copy; setting copies.</summary>
	public AnimationCurve widthCurve
	{
		get => new AnimationCurve(m_WidthCurve ?? AnimationCurve.Constant(0f, 1f, 1f));
		set => m_WidthCurve = value != null ? new AnimationCurve(value) : AnimationCurve.Constant(0f, 1f, 1f);
	}

	/// <summary>Width at the object; setting makes the width curve a line from this to <see cref="endWidth" />.</summary>
	public float startWidth
	{
		get => m_WidthMultiplier * EvaluateWidthCurve(0f);
		set => SetWidths(value, endWidth);
	}

	/// <summary>Width at the end of the trail; setting makes the width curve a line from <see cref="startWidth" /> to this.</summary>
	public float endWidth
	{
		get => m_WidthMultiplier * EvaluateWidthCurve(1f);
		set => SetWidths(startWidth, value);
	}

	/// <summary>Color along the trail (0 at the object, 1 at its end), as vertex colors. A copy; setting copies.</summary>
	public Gradient colorGradient
	{
		get => new Gradient(m_ColorGradient ?? new Gradient());
		set => m_ColorGradient = value != null ? new Gradient(value) : new Gradient();
	}

	/// <summary>Color at the object; setting makes the gradient go from this to <see cref="endColor" />.</summary>
	public Color4 startColor
	{
		get => (m_ColorGradient ?? new Gradient()).Evaluate(0f);
		set => m_ColorGradient = Gradient.Linear(value, endColor);
	}

	/// <summary>Color at the end of the trail; setting makes the gradient go from <see cref="startColor" /> to this.</summary>
	public Color4 endColor
	{
		get => (m_ColorGradient ?? new Gradient()).Evaluate(1f);
		set => m_ColorGradient = Gradient.Linear(startColor, value);
	}

	/// <summary>Extra vertices rounding each joint (0 to 90).</summary>
	public int numCornerVertices
	{
		get => m_NumCornerVertices;
		set => m_NumCornerVertices = Math.Clamp(value, 0, MaxRoundingVertices);
	}

	/// <summary>Extra vertices rounding each end (0 to 90).</summary>
	public int numCapVertices
	{
		get => m_NumCapVertices;
		set => m_NumCapVertices = Math.Clamp(value, 0, MaxRoundingVertices);
	}

	public LineAlignment alignment
	{
		get => m_Alignment;
		set => m_Alignment = value;
	}

	public LineTextureMode textureMode
	{
		get => m_TextureMode;
		set => m_TextureMode = value;
	}

	/// <summary>Texture coordinate scale: x along the trail (Tile and Repeat Per Segment), y across it.</summary>
	public Vector2 textureScale
	{
		get => m_TextureScale;
		set => m_TextureScale = value;
	}

	public bool receiveShadows
	{
		get => m_ReceiveShadows;
		set => m_ReceiveShadows = value;
	}

	/// <summary>Samples indirect light from the GI volume (lit materials).</summary>
	public bool receiveGI
	{
		get => m_ReceiveGI;
		set => m_ReceiveGI = value;
	}

	/// <summary>The materials; each draws the whole trail.</summary>
	public IReadOnlyList<Material> Materials => m_Materials;

	/// <summary>The first material (Unity's sharedMaterial); setting replaces the first slot.</summary>
	public Material sharedMaterial
	{
		get => m_Materials.Count > 0 ? m_Materials[0] : null;
		set => SetMaterial(0, value);
	}

	/// <summary>A copy of the material slots (Unity's sharedMaterials); setting replaces them.</summary>
	public Material[] sharedMaterials
	{
		get => m_Materials.ToArray();
		set => SetMaterials(value);
	}

	/// <summary>Points of the trail (the object's own position is not one of them).</summary>
	public int positionCount => points.Count;

	/// <summary>The built-in material a trail added in the editor starts with (Unity's Default-Line: white, alpha blended).</summary>
	public const string DefaultMaterialPath = "Engine/Materials/Default-Line.material";

	#endregion

	#region PrivateFields

	private const int MaxRoundingVertices = 90;

	[SerializeField] private AnimationCurve m_WidthCurve = AnimationCurve.Constant(0f, 1f, 1f);
	[SerializeField] private float m_WidthMultiplier = 1f;
	[Tooltip("Seconds a point of the trail lives")]
	[SerializeField] private float m_Time = 5f;
	[Tooltip("How far the object moves before the trail gets a new point")]
	[SerializeField] private float m_MinVertexDistance = 0.1f;
	[Tooltip("Destroy the GameObject (play mode) once the trail is gone")]
	[SerializeField] private bool m_Autodestruct;
	[SerializeField] private bool m_Emitting = true;
	[SerializeField] private Gradient m_ColorGradient = new Gradient();
	[Range(0, MaxRoundingVertices)]
	[SerializeField] private int m_NumCornerVertices;
	[Range(0, MaxRoundingVertices)]
	[SerializeField] private int m_NumCapVertices;
	[SerializeField] private LineAlignment m_Alignment = LineAlignment.View;
	[SerializeField] private LineTextureMode m_TextureMode = LineTextureMode.Stretch;
	[SerializeField] private Vector2 m_TextureScale = Vector2.One;
	[SerializeField] private List<Material> m_Materials = new List<Material>();
	[SerializeField] private bool m_ReceiveShadows = true;
	[SerializeField] private bool m_ReceiveGI = true;

	private struct TrailPoint
	{
		public Vector3 Position;

		/// <summary>When it was added, on <see cref="clock" />.</summary>
		public double Time;

		/// <summary>The transform's Z axis when it was added (<see cref="LineAlignment.TransformZ" />).</summary>
		public Vector3 Normal;
	}

	// Oldest first.
	private readonly List<TrailPoint> points = new List<TrailPoint>();

	// Game seconds this trail has lived: advanced by the frames that update it, so a paused game (or editor) keeps the
	// trail as it is; the time the component was off is added when it comes back, as Unity's points age by Time.time.
	private double clock;
	private double disabledAt = -1d;
	private bool hadPoints;

	// The line of the view being built (rendering is single-threaded).
	private static LinePoint[] linePoints = new LinePoint[64];
	private static float[] lineDistances = new float[64];

	#endregion

	#region PublicMethods

	public override void OnEnable()
	{
		if (disabledAt >= 0d)
		{
			clock += Math.Max(0d, Time.timeD - disabledAt);
			disabledAt = -1d;
			RemoveExpired();
		}
	}

	public override void OnDisable()
	{
		disabledAt = Time.timeD;
	}

	public override void LateUpdate()
	{
		clock += Math.Max(0d, Time.deltaTimeD);
		if (m_Emitting)
		{
			Vector3 position = transform.position;
			float spacing = Math.Max(m_MinVertexDistance, 1e-4f);
			if (points.Count == 0 || (position - points[points.Count - 1].Position).LengthSquared >= spacing * spacing)
			{
				AddPoint(position);
			}
		}

		RemoveExpired();
		if (m_Autodestruct && hadPoints && points.Count == 0 && gameObject.scene != null && gameObject.scene.IsPlaying)
		{
			Destroy(gameObject);
		}
	}

	public override void Reset()
	{
		if (m_Materials.Count == 0 && System.IO.File.Exists(AssetDatabase.ToAbsolute(DefaultMaterialPath)))
		{
			m_Materials.Add(InternalStorage.GetOrCreateMaterial(DefaultMaterialPath));
		}
	}

	public override void OnValidate()
	{
		m_Time = Math.Max(0f, m_Time);
		m_MinVertexDistance = Math.Max(0f, m_MinVertexDistance);
		m_WidthMultiplier = Math.Max(0f, m_WidthMultiplier);
		m_NumCornerVertices = Math.Clamp(m_NumCornerVertices, 0, MaxRoundingVertices);
		m_NumCapVertices = Math.Clamp(m_NumCapVertices, 0, MaxRoundingVertices);
		m_WidthCurve ??= AnimationCurve.Constant(0f, 1f, 1f);
		m_ColorGradient ??= new Gradient();
		m_ColorGradient.Sort();
		m_Materials ??= new List<Material>();
	}

	/// <summary>Removes every point: the trail starts again from where the object is.</summary>
	public void Clear()
	{
		points.Clear();
		hadPoints = false;
	}

	/// <summary>Adds a point now (the newest).</summary>
	public void AddPosition(Vector3 position)
	{
		AddPoint(position);
	}

	/// <summary>Adds points now, in order (the last is the newest).</summary>
	public void AddPositions(Vector3[] positions)
	{
		if (positions == null)
		{
			return;
		}

		foreach (Vector3 position in positions)
		{
			AddPoint(position);
		}
	}

	/// <summary>A point's position (0 = the oldest).</summary>
	public Vector3 GetPosition(int index)
	{
		return points[index].Position;
	}

	/// <summary>Moves a point (0 = the oldest).</summary>
	public void SetPosition(int index, Vector3 position)
	{
		TrailPoint point = points[index];
		point.Position = position;
		points[index] = point;
	}

	/// <summary>Copies the points' positions, oldest first, into the array; returns how many were copied.</summary>
	public int GetPositions(Vector3[] positions)
	{
		if (positions == null)
		{
			return 0;
		}

		int count = Math.Min(positions.Length, points.Count);
		for (int i = 0; i < count; i++)
		{
			positions[i] = points[i].Position;
		}

		return count;
	}

	/// <summary>Moves the points (oldest first) to these positions; extra positions are ignored (use AddPositions to add).</summary>
	public void SetPositions(Vector3[] positions)
	{
		if (positions == null)
		{
			return;
		}

		int count = Math.Min(positions.Length, points.Count);
		for (int i = 0; i < count; i++)
		{
			SetPosition(i, positions[i]);
		}
	}

	/// <summary>Material of slot <paramref name="slot" />, or null.</summary>
	public Material GetMaterial(int slot)
	{
		return slot >= 0 && slot < m_Materials.Count ? m_Materials[slot] : null;
	}

	/// <summary>Sets the material of one slot (the list grows as needed).</summary>
	public void SetMaterial(int slot, Material material)
	{
		while (m_Materials.Count <= slot)
		{
			m_Materials.Add(null);
		}

		m_Materials[slot] = material;
	}

	/// <summary>Replaces every material slot.</summary>
	public void SetMaterials(IEnumerable<Material> slots)
	{
		m_Materials.Clear();
		if (slots != null)
		{
			m_Materials.AddRange(slots);
		}
	}

	/// <summary>Replaces every material slot.</summary>
	public void SetMaterials(params Material[] slots)
	{
		SetMaterials((IEnumerable<Material>)slots);
	}

	/// <summary>
	///     Appends the trail as seen from a camera to <paramref name="builder" /> (world space triangles facing the
	///     camera, or the points' Z axes). False when there is nothing to draw (fewer than two distinct points).
	/// </summary>
	internal bool BuildGeometry(LineGeometryBuilder builder, Vector3 cameraPosition, Vector3 cameraForward, bool orthographic, out int firstIndex, out int indexCount, out Vector3 boundsMin, out Vector3 boundsMax)
	{
		firstIndex = indexCount = 0;
		boundsMin = boundsMax = Vector3.Zero;
		int capacity = points.Count + 1;
		if (linePoints.Length < capacity)
		{
			int size = Math.Max(capacity, linePoints.Length * 2);
			linePoints = new LinePoint[size];
			lineDistances = new float[size];
		}

		// The object (while it leaves a trail), then the points from the newest to the oldest.
		int count = 0;
		if (m_Emitting)
		{
			Vector3 head = transform.position;
			count = AddLinePoint(count, head, transform.forward);
		}

		for (int i = points.Count - 1; i >= 0; i--)
		{
			count = AddLinePoint(count, points[i].Position, points[i].Normal);
		}

		if (count < 2)
		{
			return false;
		}

		float length = lineDistances[count - 1];
		Gradient gradient = m_ColorGradient;
		Vector3 viewFacing = -cameraForward;
		for (int i = 0; i < count; i++)
		{
			ref LinePoint point = ref linePoints[i];
			float distance = lineDistances[i];
			float t = length > 1e-6f ? distance / length : i / (float)(count - 1);
			point.Width = m_WidthMultiplier * EvaluateWidthCurve(t);
			point.Color = gradient != null ? gradient.Evaluate(t) : Color4.White;
			point.U = m_TextureMode switch
			{
				LineTextureMode.Tile => distance * m_TextureScale.X,
				LineTextureMode.DistributePerSegment => i / (float)(count - 1),
				LineTextureMode.RepeatPerSegment => i * m_TextureScale.X,
				_ => t
			};

			if (m_Alignment == LineAlignment.View)
			{
				Vector3 toCamera = cameraPosition - point.Position;
				point.Facing = orthographic || toCamera.LengthSquared < 1e-12f ? viewFacing : toCamera;
			}
		}

		return builder.Build(linePoints, count, m_NumCornerVertices, m_NumCapVertices, m_TextureScale.Y, out firstIndex, out indexCount, out boundsMin, out boundsMax);
	}

	#endregion

	#region PrivateMethods

	private void AddPoint(Vector3 position)
	{
		points.Add(new TrailPoint { Position = position, Time = clock, Normal = transform.forward });
		hadPoints = true;
	}

	private void RemoveExpired()
	{
		int expired = 0;
		while (expired < points.Count && clock - points[expired].Time > m_Time)
		{
			expired++;
		}

		if (expired > 0)
		{
			points.RemoveRange(0, expired);
		}
	}

	// Appends a point of the line unless it is where the previous one is (a zero-length segment has no direction).
	private static int AddLinePoint(int count, Vector3 position, Vector3 normal)
	{
		float distance = 0f;
		if (count > 0)
		{
			float step = (position - linePoints[count - 1].Position).Length;
			if (step < 1e-5f)
			{
				return count;
			}

			distance = lineDistances[count - 1] + step;
		}

		linePoints[count] = new LinePoint { Position = position, Facing = normal };
		lineDistances[count] = distance;
		return count + 1;
	}

	private float EvaluateWidthCurve(float t)
	{
		return m_WidthCurve != null && m_WidthCurve.length > 0 ? m_WidthCurve.Evaluate(t) : 1f;
	}

	private void SetWidths(float start, float end)
	{
		if (m_WidthMultiplier <= 0f)
		{
			m_WidthMultiplier = 1f;
		}

		m_WidthCurve = AnimationCurve.Linear(0f, start / m_WidthMultiplier, 1f, end / m_WidthMultiplier);
	}

	#endregion
}
