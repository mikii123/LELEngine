using System;
using System.Collections.Generic;
using LELEngine;
using OpenTK.Mathematics;

/// <summary>How a <see cref="LODGroup" /> passes from one level to the next (Unity's LODFadeMode; only None is drawn so far).</summary>
public enum LODFadeMode
{
	None = 0,
	CrossFade = 1,
	SpeedTree = 2
}

/// <summary>
///     One level of a <see cref="LODGroup" /> (Unity's LOD): its renderers draw while the group's height on screen (a
///     share of the view's height, 0..1) is at least <see cref="screenRelativeTransitionHeight" /> and below the
///     previous level's.
/// </summary>
[Serializable]
public struct LOD
{
	public float screenRelativeTransitionHeight;

	/// <summary>Unity's cross-fade width (a share of this level's range); kept, not drawn yet.</summary>
	public float fadeTransitionWidth;

	public List<MeshRenderer> renderers;

	public LOD(float screenRelativeTransitionHeight, MeshRenderer[] renderers)
	{
		this.screenRelativeTransitionHeight = screenRelativeTransitionHeight;
		fadeTransitionWidth = 0f;
		this.renderers = new List<MeshRenderer>(renderers ?? Array.Empty<MeshRenderer>());
	}
}

/// <summary>
///     Unity's LODGroup: levels of detail of an object, each a set of renderers. The renderer picks one level per frame
///     from the camera it renders (for the frame of a camera stack: the stack's lighting camera) by how tall the
///     group's bounds are on screen: <see cref="size" /> x the transform's largest scale at its reference point, over
///     the view's height at that distance, times the project's LOD bias (<see cref="QualitySettings.lodBias" />). Only
///     that level's renderers draw and cast shadows; below the last level's height nothing draws. The global
///     illumination always sees level 0 (the levels' own renderers, whatever the camera picks), as Unity's lighting
///     does: the GI's volumes and caches do not change when a level changes. A renderer may be in several levels.
///     Disabled, the group leaves its renderers alone (all draw).
/// </summary>
[ExecuteAlways]
[InspectorColor(InspectorColorKind.Rendering)]
public sealed class LODGroup : Behaviour
{
	#region PublicFields

	/// <summary>The group's size in its local space (the largest side of its renderers' bounds); world size = size x the largest scale.</summary>
	public float size
	{
		get => m_Size;
		set => m_Size = Math.Max(0f, value);
	}

	/// <summary>The bounds' centre in the group's local space, where the distance to the camera is measured.</summary>
	public Vector3 localReferencePoint
	{
		get => m_LocalReferencePoint;
		set => m_LocalReferencePoint = value;
	}

	public LODFadeMode fadeMode
	{
		get => m_FadeMode;
		set => m_FadeMode = value;
	}

	public bool animateCrossFading
	{
		get => m_AnimateCrossFading;
		set => m_AnimateCrossFading = value;
	}

	public int lodCount => m_LODs.Count;

	/// <summary>The level the last frame drew (for its camera); -1 when nothing drew (smaller than the last level) or never rendered.</summary>
	public int CurrentLOD { get; private set; } = -1;

	/// <summary>The relative height of the last frame's selection (before the bias).</summary>
	public float CurrentRelativeHeight { get; private set; }

	#endregion

	#region PrivateFields

	// Every enabled group of the loaded scenes (edit mode too): what the renderer picks levels for.
	private static readonly List<LODGroup> active = new List<LODGroup>();

	// One value per selection (frame of a camera): a renderer whose stamp equals it is hidden this frame.
	private static int selection;

	[SerializeField] private Vector3 m_LocalReferencePoint;
	[SerializeField] private float m_Size = 1f;
	[SerializeField] private LODFadeMode m_FadeMode;
	[SerializeField] private bool m_AnimateCrossFading;
	[SerializeField] private List<LOD> m_LODs = new List<LOD>();

	private int forcedLevel = -1;

	#endregion

	#region PublicMethods

	/// <summary>Copies of the levels (their renderer lists are copies too).</summary>
	public LOD[] GetLODs()
	{
		var lods = new LOD[m_LODs.Count];
		for (int i = 0; i < lods.Length; i++)
		{
			lods[i] = m_LODs[i];
			lods[i].renderers = new List<MeshRenderer>(m_LODs[i].renderers ?? new List<MeshRenderer>());
		}

		return lods;
	}

	/// <summary>Replaces the levels (heights from the most detailed down: each below the one before).</summary>
	public void SetLODs(LOD[] lods)
	{
		m_LODs.Clear();
		foreach (LOD lod in lods ?? Array.Empty<LOD>())
		{
			LOD copy = lod;
			copy.renderers = new List<MeshRenderer>(lod.renderers ?? new List<MeshRenderer>());
			m_LODs.Add(copy);
		}
	}

	/// <summary>Always draws level <paramref name="index" /> (clamped to the levels); a negative index goes back to picking by height.</summary>
	public void ForceLOD(int index)
	{
		forcedLevel = index;
	}

	/// <summary>
	///     Sets <see cref="localReferencePoint" /> and <see cref="size" /> from the bounds of every level's renderers (their
	///     meshes' bounds in the group's local space): the centre and the largest side.
	/// </summary>
	public void RecalculateBounds()
	{
		Matrix4 toLocal = transform.WorldToLocal;
		Vector3 min = new Vector3(float.MaxValue);
		Vector3 max = new Vector3(float.MinValue);
		foreach (LOD lod in m_LODs)
		{
			if (lod.renderers == null)
			{
				continue;
			}

			foreach (MeshRenderer renderer in lod.renderers)
			{
				if (renderer == null || renderer.Mesh == null)
				{
					continue;
				}

				Matrix4 toGroup = renderer.transform.LocalToWorld * toLocal;
				Vector3 a = renderer.Mesh.BoundsMin;
				Vector3 b = renderer.Mesh.BoundsMax;
				for (int corner = 0; corner < 8; corner++)
				{
					Vector3 p = new Vector3((corner & 1) != 0 ? b.X : a.X, (corner & 2) != 0 ? b.Y : a.Y, (corner & 4) != 0 ? b.Z : a.Z);
					p = Vector3.TransformPosition(p, toGroup);
					min = Vector3.ComponentMin(min, p);
					max = Vector3.ComponentMax(max, p);
				}
			}
		}

		if (min.X > max.X)
		{
			return;
		}

		Vector3 extent = max - min;
		m_LocalReferencePoint = (min + max) * 0.5f;
		m_Size = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
	}

	/// <summary>
	///     The group's height on screen for a perspective <paramref name="camera" />: size x largest scale over the view's
	///     height at the reference point's distance (Unity's relative height, without the bias).
	/// </summary>
	public float GetRelativeHeight(Camera camera)
	{
		Vector3 reference = transform.TransformPoint(m_LocalReferencePoint);
		Vector3 scale = transform.lossyScale;
		float worldSize = m_Size * Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
		float distance = (reference - camera.transform.position).Length;
		float halfAngle = MathF.Tan(camera.FoV * 0.5f * MathF.PI / 180f);
		if (distance * halfAngle <= 1e-6f)
		{
			return float.PositiveInfinity;
		}

		return worldSize * 0.5f / (distance * halfAngle);
	}

	public override void OnEnable()
	{
		if (!active.Contains(this))
		{
			active.Add(this);
		}
	}

	public override void OnDisable()
	{
		active.Remove(this);
		CurrentLOD = -1;
	}

	public override void Reset()
	{
		// Unity's defaults for a new group: three levels at 60 %, 30 % and 10 % of the screen, no renderers yet.
		m_LODs = new List<LOD>
		{
			new LOD(0.6f, null),
			new LOD(0.3f, null),
			new LOD(0.1f, null)
		};
	}

	#endregion

	#region InternalMethods

	/// <summary>
	///     Picks every enabled group's level for a frame rendered from <paramref name="camera" />; the renderer's split of
	///     this frame reads <see cref="IsHidden" /> and <see cref="IsOutOfGI" />. Returns false when no group is active
	///     (no renderer is hidden).
	/// </summary>
	internal static bool Select(Camera camera)
	{
		selection = selection == int.MaxValue ? 1 : selection + 1;
		if (active.Count == 0)
		{
			return false;
		}

		LODSettings settings = ProjectSettings.Current.LOD;
		for (int i = active.Count - 1; i >= 0; i--)
		{
			LODGroup group = active[i];
			if (group.IsDestroyed)
			{
				active.RemoveAt(i);
				continue;
			}

			group.SelectLevel(camera, settings);
		}

		return true;
	}

	/// <summary>Whether the frame's selection leaves <paramref name="renderer" /> out of drawing (and shadows).</summary>
	internal static bool IsHidden(MeshRenderer renderer)
	{
		return renderer.lodHidden == selection;
	}

	/// <summary>Whether the frame's selection leaves <paramref name="renderer" /> out of the GI (a level past 0 of a group).</summary>
	internal static bool IsOutOfGI(MeshRenderer renderer)
	{
		return renderer.lodOutOfGI == selection;
	}

	#endregion

	#region PrivateMethods

	private void SelectLevel(Camera camera, LODSettings settings)
	{
		int count = m_LODs.Count;
		if (count == 0)
		{
			CurrentLOD = -1;
			return;
		}

		int level;
		if (forcedLevel >= 0)
		{
			level = Math.Min(forcedLevel, count - 1);
		}
		else if (camera == null)
		{
			level = 0;
		}
		else
		{
			CurrentRelativeHeight = GetRelativeHeight(camera);
			float height = CurrentRelativeHeight * settings.Bias;
			level = count;
			for (int i = 0; i < count; i++)
			{
				if (height >= m_LODs[i].screenRelativeTransitionHeight)
				{
					level = i;
					break;
				}
			}

			if (level < count)
			{
				level = Math.Max(level, Math.Min(settings.MaximumLevel, count - 1));
			}
		}

		// Every member hidden and out of the GI, then the picked level shown and level 0 back in the GI.
		for (int i = 0; i < count; i++)
		{
			Mark(m_LODs[i].renderers, true, true);
		}

		Mark(m_LODs[0].renderers, null, false);
		if (level < count)
		{
			Mark(m_LODs[level].renderers, false, null);
		}

		CurrentLOD = level < count ? level : -1;
	}

	// Sets what is given (null: left as it is).
	private static void Mark(List<MeshRenderer> renderers, bool? hidden, bool? outOfGI)
	{
		if (renderers == null)
		{
			return;
		}

		foreach (MeshRenderer renderer in renderers)
		{
			if (renderer == null)
			{
				continue;
			}

			if (hidden.HasValue)
			{
				renderer.lodHidden = hidden.Value ? selection : 0;
			}

			if (outOfGI.HasValue)
			{
				renderer.lodOutOfGI = outOfGI.Value ? selection : 0;
			}
		}
	}

	#endregion
}
