using System.Collections.Generic;
using LELEngine;
using LELEngine.Shaders;

/// <summary>
///     Draws a <see cref="Mesh" /> with one material per sub-mesh: sub-mesh i uses <see cref="Materials" />[i] (the
///     last material when there are fewer materials than sub-meshes). Opaque sub-meshes draw in the opaque passes,
///     transparent ones (see <see cref="Material.IsTransparent" />) in the transparent pass; a renderer can have both.
/// </summary>
[ExecuteAlways]
public sealed class MeshRenderer : Behaviour
{
	#region PublicFields

	/// <summary>The mesh drawn; it uploads into the shared geometry buffers (Rendering.GeometryPool) on first use.</summary>
	public Mesh Mesh => mesh;

	/// <summary>The material of each sub-mesh.</summary>
	public IReadOnlyList<Material> Materials => materials;

	public int SubMeshCount => mesh?.SubMeshCount ?? 0;

	public bool CastShadows = true;
	public bool ReceiveShadows = true;

	/// <summary>Voxelized into the GI volume (emits / bounces light onto others).</summary>
	public bool ContributesToGI = true;

	/// <summary>Samples indirect light from the GI volume.</summary>
	public bool ReceiveGI = true;

	/// <summary>
	///     Transform, mesh and material do not change at runtime. Static renderers are voxelized once into a
	///     cached volume instead of every frame. Call Lighting.GI.InvalidateStatic() after changing one.
	/// </summary>
	public bool IsStatic;

	/// <summary>Some sub-mesh has a transparent material (it draws in the transparent pass).</summary>
	public bool HasTransparentParts
	{
		get
		{
			for (int i = 0; i < PartCount; i++)
			{
				if (IsSubMeshTransparent(i))
				{
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>Some sub-mesh has an opaque material (or none).</summary>
	public bool HasOpaqueParts
	{
		get
		{
			for (int i = 0; i < PartCount; i++)
			{
				if (!IsSubMeshTransparent(i))
				{
					return true;
				}
			}

			return false;
		}
	}

	#endregion

	#region PrivateFields

	[SerializeField] private Mesh mesh;
	[SerializeField] private List<Material> materials = new List<Material>();

	/// <summary>Sub-meshes to consider (a renderer without a mesh still reports its material).</summary>
	private int PartCount => mesh != null ? mesh.SubMeshCount : 1;

	#endregion

	#region PublicMethods

	/// <summary>Material of sub-mesh <paramref name="subMesh" />: its slot, else the last material, else null.</summary>
	public Material GetMaterial(int subMesh)
	{
		if (materials.Count == 0)
		{
			return null;
		}

		return materials[subMesh < materials.Count ? subMesh : materials.Count - 1];
	}

	public bool IsSubMeshTransparent(int subMesh)
	{
		Material m = GetMaterial(subMesh);
		return m != null && m.IsTransparent;
	}

	/// <summary>Replaces every material slot.</summary>
	public void SetMaterials(IEnumerable<Material> slots)
	{
		materials.Clear();
		if (slots != null)
		{
			materials.AddRange(slots);
		}
	}

	/// <summary>Replaces every material slot.</summary>
	public void SetMaterials(params Material[] slots)
	{
		SetMaterials((IEnumerable<Material>)slots);
	}

	/// <summary>Sets the material of one slot (the list grows as needed, repeating its last material).</summary>
	public void SetMaterial(int slot, Material material)
	{
		while (materials.Count <= slot)
		{
			materials.Add(materials.Count > 0 ? materials[materials.Count - 1] : null);
		}

		materials[slot] = material;
	}

	public void SetMesh(Mesh value)
	{
		mesh = value;
	}

	/// <summary>
	///     Full material draw of the opaque sub-meshes: per sub-mesh its material shader is activated and gets the
	///     camera, lighting and material uniforms, then the sub-mesh draws.
	/// </summary>
	public override void Render()
	{
		if (mesh == null)
		{
			return;
		}

		for (int i = 0; i < mesh.SubMeshCount; i++)
		{
			Material m = GetMaterial(i);
			ShaderProgram shader = m?.Program;
			if (shader == null || m.IsTransparent)
			{
				continue;
			}

			shader.Use();
			shader.RenderState.ApplyCull();
			transform.SetModelMatrix(shader);
			(Camera.current ?? Camera.main)?.SetUniforms(shader);
			Lighting.SetUniforms(shader, ReceiveShadows, ReceiveGI);
			m.SetUniforms();
			DrawSubMesh(i);
		}
	}

	/// <summary>
	///     Per-object part of a draw grouped by shader: the model matrix and material uniforms of sub-mesh
	///     <paramref name="subMesh" />, then its draw. The caller has activated the sub-mesh's material shader and set
	///     the camera and lighting uniforms on it for this object's ReceiveShadows / ReceiveGI flags.
	/// </summary>
	public void RenderSubMesh(int subMesh)
	{
		Material m = GetMaterial(subMesh);
		ShaderProgram program = m?.Program;
		if (mesh == null || program == null)
		{
			return;
		}

		transform.SetModelMatrix(program);
		m.SetUniforms();
		DrawSubMesh(subMesh);
	}

	/// <summary>
	///     Draws the opaque geometry with an externally provided program (depth prepass, shadow map, voxelization):
	///     every sub-mesh, or only the opaque ones when some are transparent. Only the model matrix is set; the
	///     caller sets everything else. The program must be active.
	/// </summary>
	public void RenderWith(ShaderProgram program)
	{
		RenderWith(program, false);
	}

	/// <summary>
	///     <see cref="RenderWith(ShaderProgram)" /> for shadow maps: the opaque sub-meshes whose material casts shadows
	///     (not those of a shader with ShadowCaster Off).
	/// </summary>
	public void RenderShadowCastersWith(ShaderProgram program)
	{
		RenderWith(program, true);
	}

	/// <summary>The sub-mesh's material casts shadows (no material: it does).</summary>
	public bool IsSubMeshShadowCaster(int subMesh)
	{
		Material m = GetMaterial(subMesh);
		return m == null || m.RenderState.CastShadows;
	}

	/// <summary>One sub-mesh with an externally provided program (only the model matrix is set).</summary>
	public void RenderSubMeshWith(ShaderProgram program, int subMesh)
	{
		if (mesh == null)
		{
			return;
		}

		transform.SetModelMatrix(program);
		DrawSubMesh(subMesh);
	}

	/// <summary>Binds the shared geometry and draws every sub-mesh in one call. No uniforms are touched.</summary>
	public void DrawGeometry()
	{
		LELEngine.Rendering.GeometryPool.Draw(mesh);
	}

	/// <summary>Binds the shared geometry and draws one sub-mesh. No uniforms are touched.</summary>
	public void DrawSubMesh(int subMesh)
	{
		LELEngine.Rendering.GeometryPool.DrawSubMesh(mesh, subMesh);
	}

	#endregion

	#region PrivateMethods

	private void RenderWith(ShaderProgram program, bool shadowCastersOnly)
	{
		if (mesh == null)
		{
			return;
		}

		transform.SetModelMatrix(program);
		bool every = !HasTransparentParts;
		for (int i = 0; every && shadowCastersOnly && i < PartCount; i++)
		{
			every = IsSubMeshShadowCaster(i);
		}

		if (every)
		{
			DrawGeometry();
			return;
		}

		for (int i = 0; i < mesh.SubMeshCount; i++)
		{
			if (!IsSubMeshTransparent(i) && (!shadowCastersOnly || IsSubMeshShadowCaster(i)))
			{
				DrawSubMesh(i);
			}
		}
	}

	#endregion
}
