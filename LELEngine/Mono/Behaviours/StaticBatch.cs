using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Rendering;
using LELEngine.Serialization;
using OpenTK.Mathematics;

/// <summary>
///     A static batch: the meshes of several static renderers (<see cref="GameObject.isStatic" />) merged into the one
///     mesh the <see cref="MeshRenderer" /> next to this component draws, so what took a draw call (and its uniforms) per
///     object takes one per material. Static batching makes them (Project Settings > Graphics): a build for the scenes
///     it ships, the editor for the scenes its play mode starts and loads. Of a scene's static renderers those the
///     renderer draws one by one become batches (transparent materials, opaque ones whose shader has no GPU-driven
///     variant); what the GPU-driven path already draws in a few calls is left as it is.
///     <para>
///         The batched renderers stay on their objects, with their meshes and materials, and are the batch's parts
///         (<see cref="Renderers" />, <see cref="MeshRenderer.Batch" />): while the batch's renderer draws, the cameras
///         and the shadow maps leave the parts out (<c>Renderer.Partition</c>), and the GI goes on taking the parts
///         themselves (the batch's renderer is not in it), so the lighting is what it was without batches. A part that is
///         disabled, deactivated or destroyed is no longer drawn: its triangles in the batch's mesh are hidden
///         (<see cref="GeometryPool.SetIndexRangeVisible" />) and shown again when it comes back. A part whose renderer
///         is given another mesh, other materials or shader values of its own (<see cref="MeshRenderer.SetMesh" />,
///         <see cref="MeshRenderer.SetMaterials(System.Collections.Generic.IEnumerable{Material})" />,
///         <see cref="MeshRenderer.SetPropertyBlock" />, an edit in the inspector) leaves the batch for good and draws
///         itself again. A part that moves does not move in the batch: static objects do not move. Disabling the
///         batch's renderer (or its object) gives every part back to its own renderer.
///     </para>
///     <para>
///         Transparent batches keep their picture: the transparent pass sorts a batch part by part, each where its own
///         renderer would sort (its bounds' centre, its place among equals), and draws the parts that follow each other
///         with one call (<see cref="GeometryPool.DrawRanges" />); another object sorting in between cuts the run, as it
///         would have drawn between the objects.
///     </para>
/// </summary>
[InspectorColor(InspectorColorKind.Rendering)]
public sealed class StaticBatch : Behaviour, ISerializationCallbackReceiver
{
	#region PublicFields

	/// <summary>The renderers whose meshes the batch's mesh is made of, in its order (a destroyed one stays in its place).</summary>
	public IReadOnlyList<MeshRenderer> Renderers => m_Renderers;

	/// <summary>The renderer drawing the batch's mesh (on the same object), or null.</summary>
	public MeshRenderer Renderer => own ??= gameObject?.GetComponent<MeshRenderer>();

	#endregion

	#region PrivateFields

	// The parts, and where each part's triangles are in the batch mesh's indices: part i has the ranges
	// m_PartRanges[i] .. m_PartRanges[i + 1] - 1, each a (first index, index count) pair of m_Ranges (a part has one
	// range per sub-mesh of its own mesh that has triangles).
	[SerializeField] private List<MeshRenderer> m_Renderers = new List<MeshRenderer>();
	[SerializeField] private int[] m_PartRanges = Array.Empty<int>();
	[SerializeField] private int[] m_Ranges = Array.Empty<int>();

	// Per part: the centre of its own mesh's bounds in the batch object's space (where the transparent pass sorts it,
	// as it sorted the part's renderer), and whether its triangles all face one way (a sheet: drawn from both sides it
	// needs no back-faces-first pass).
	[SerializeField] private Vector3[] m_Centres = Array.Empty<Vector3>();
	[SerializeField] private bool[] m_Flat = Array.Empty<bool>();

	[NonSerialized] private MeshRenderer own;

	// Per part: its triangles are hidden in the batch's mesh now.
	[NonSerialized] private bool[] hidden;
	[NonSerialized] private bool queued;

	// Per range: the sub-mesh of the batch's mesh it lies in (worked out from the mesh on first use).
	[NonSerialized] private int[] rangeSubMeshes;
	[NonSerialized] private Mesh rangeSubMeshesOf;

	// Batches with parts that were enabled or disabled since the last frame rendered (main thread).
	private static readonly List<StaticBatch> changed = new List<StaticBatch>();

	#endregion

	#region PublicMethods

	/// <summary>
	///     Sets what the batch is made of (the batcher; see the fields for the layout): the parts' renderers, their index
	///     ranges in the batch's mesh, their bounds' centres in the batch object's space, and which are flat.
	/// </summary>
	public void SetParts(IReadOnlyList<MeshRenderer> renderers, int[] partRanges, int[] ranges, Vector3[] centres, bool[] flat)
	{
		Release();
		m_Renderers = new List<MeshRenderer>(renderers ?? Array.Empty<MeshRenderer>());
		m_PartRanges = partRanges ?? Array.Empty<int>();
		m_Ranges = ranges ?? Array.Empty<int>();
		m_Centres = centres ?? Array.Empty<Vector3>();
		m_Flat = flat ?? Array.Empty<bool>();
		rangeSubMeshes = null;
		Claim();
	}

	/// <summary>
	///     The batch draws <paramref name="part" />'s mesh now: its own renderer is enabled on an active object (the part
	///     itself is then left out of the cameras' lists).
	/// </summary>
	public bool Draws(MeshRenderer part)
	{
		if (dead || part == null || part.batch != this)
		{
			return false;
		}

		MeshRenderer renderer = Renderer;
		return renderer != null && renderer.isActiveAndEnabled;
	}

	public void OnBeforeSerialize()
	{ }

	/// <summary>Read from a scene: the parts' renderers are the batch's from now on.</summary>
	public void OnAfterDeserialize()
	{
		rangeSubMeshes = null;
		Claim();
	}

	public override void OnEnable()
	{
		// The parts as they are now (some may have been disabled before the batch woke up).
		Queue();
	}

	public override void OnDestroy()
	{
		Release();
	}

	#endregion

	#region InternalMethods

	/// <summary>The parts (what the transparent pass sorts one by one).</summary>
	internal int PartCount => m_Renderers.Count;

	/// <summary>The layout is whole: every part has its ranges and its centre (else the batch draws as one mesh).</summary>
	internal bool HasPartData => m_PartRanges.Length == m_Renderers.Count + 1 && m_Centres.Length == m_Renderers.Count;

	/// <summary>The part's own renderer (null when it was destroyed).</summary>
	internal MeshRenderer Part(int part)
	{
		MeshRenderer renderer = m_Renderers[part];
		return ReferenceEquals(renderer, null) || renderer.dead ? null : renderer;
	}

	/// <summary>The part is drawn now (its renderer is enabled on an active object).</summary>
	internal bool IsPartVisible(int part)
	{
		return hidden == null || part >= hidden.Length || !hidden[part];
	}

	internal Vector3 PartCentre(int part)
	{
		return m_Centres[part];
	}

	internal bool IsPartFlat(int part)
	{
		return part < m_Flat.Length && m_Flat[part];
	}

	/// <summary>The part's ranges: indices <paramref name="first" /> .. <paramref name="end" /> - 1 for <see cref="RangeFirst" /> / <see cref="RangeCount" />.</summary>
	internal void GetPartRanges(int part, out int first, out int end)
	{
		first = m_PartRanges[part];
		end = Math.Min(m_PartRanges[part + 1], m_Ranges.Length / 2);
	}

	internal int RangeFirst(int range)
	{
		return m_Ranges[range * 2];
	}

	internal int RangeCount(int range)
	{
		return m_Ranges[range * 2 + 1];
	}

	/// <summary>The sub-mesh of the batch's mesh a range lies in (its material's slot), or -1.</summary>
	internal int RangeSubMesh(int range, Mesh mesh)
	{
		if (rangeSubMeshes == null || !ReferenceEquals(rangeSubMeshesOf, mesh))
		{
			rangeSubMeshesOf = mesh;
			rangeSubMeshes = new int[m_Ranges.Length / 2];
			for (int i = 0; i < rangeSubMeshes.Length; i++)
			{
				rangeSubMeshes[i] = -1;
				int first = m_Ranges[i * 2];
				for (int s = 0; s < mesh.SubMeshCount; s++)
				{
					SubMesh subMesh = mesh.SubMeshes[s];
					if (first >= subMesh.FirstIndex && first < subMesh.FirstIndex + subMesh.IndexCount)
					{
						rangeSubMeshes[i] = s;
						break;
					}
				}
			}
		}

		return range < rangeSubMeshes.Length ? rangeSubMeshes[range] : -1;
	}

	/// <summary>A part was enabled or disabled (its object activated, deactivated or destroyed): its triangles follow before the next frame draws.</summary>
	internal void PartChanged()
	{
		Queue();
	}

	/// <summary>
	///     A part is no longer what the batch's mesh was made of (its renderer got another mesh, other materials or shader
	///     values of its own; an edit in the inspector): it leaves the batch for good. Its triangles in the batch's mesh
	///     are hidden and its own renderer draws again, with what it has now.
	/// </summary>
	internal void Leave(MeshRenderer part)
	{
		if (ReferenceEquals(part, null) || part.batch != this)
		{
			return;
		}

		part.batch = null;
		Queue();
	}

	/// <summary>
	///     Hides and shows the batches' parts that changed since the last frame (called before a frame renders: the GL
	///     thread, and nothing has drawn the meshes yet).
	/// </summary>
	internal static void ApplyChanges()
	{
		if (changed.Count == 0)
		{
			return;
		}

		foreach (StaticBatch batch in changed)
		{
			batch.queued = false;
			if (!batch.dead)
			{
				batch.Refresh();
			}
		}

		changed.Clear();
	}

	#endregion

	#region PrivateMethods

	private void Claim()
	{
		foreach (MeshRenderer part in m_Renderers)
		{
			if (!ReferenceEquals(part, null) && !part.dead)
			{
				part.batch = this;
			}
		}

		// The batch's own renderer knows it draws a batch (the transparent pass sorts it by parts).
		own = gameObject?.GetComponent<MeshRenderer>();
		if (own != null)
		{
			own.ownBatch = this;
		}
	}

	// Gives the parts back to their own renderers and the mesh its hidden triangles.
	private void Release()
	{
		foreach (MeshRenderer part in m_Renderers)
		{
			if (!ReferenceEquals(part, null) && part.batch == this)
			{
				part.batch = null;
			}
		}

		if (!ReferenceEquals(own, null) && own.ownBatch == this)
		{
			own.ownBatch = null;
		}

		Mesh mesh = ReferenceEquals(own, null) ? null : own.Mesh;
		if (mesh != null && hidden != null)
		{
			for (int i = 0; i < hidden.Length; i++)
			{
				if (hidden[i])
				{
					SetVisible(mesh, i, true);
				}
			}
		}

		hidden = null;
		if (queued)
		{
			queued = false;
			changed.Remove(this);
		}
	}

	private void Queue()
	{
		if (!queued && !dead)
		{
			queued = true;
			changed.Add(this);
		}
	}

	// Every part's triangles as its renderer is now: hidden when it is disabled, inactive or gone, or left the batch.
	private void Refresh()
	{
		Mesh mesh = Renderer?.Mesh;
		if (mesh == null || m_PartRanges.Length != m_Renderers.Count + 1)
		{
			return;
		}

		hidden ??= new bool[m_Renderers.Count];
		for (int i = 0; i < m_Renderers.Count && i < hidden.Length; i++)
		{
			MeshRenderer part = m_Renderers[i];
			bool hide = ReferenceEquals(part, null) || part.batch != this || !part.isActiveAndEnabled || part.forceRenderingOff;
			if (hide != hidden[i])
			{
				SetVisible(mesh, i, !hide);
				hidden[i] = hide;
			}
		}
	}

	private void SetVisible(Mesh mesh, int part, bool visible)
	{
		if (part + 1 >= m_PartRanges.Length)
		{
			return;
		}

		for (int range = m_PartRanges[part]; range < m_PartRanges[part + 1] && range * 2 + 1 < m_Ranges.Length; range++)
		{
			GeometryPool.SetIndexRangeVisible(mesh, m_Ranges[range * 2], m_Ranges[range * 2 + 1], visible);
		}
	}

	#endregion
}
