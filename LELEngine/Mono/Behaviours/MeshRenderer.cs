using System.Collections.Generic;
using LELEngine;
using LELEngine.Shaders;

/// <summary>
///     Draws a <see cref="Mesh" /> with one material per sub-mesh: sub-mesh i uses <see cref="Materials" />[i] (the
///     last material when there are fewer materials than sub-meshes). Opaque sub-meshes draw in the opaque passes,
///     transparent ones (see <see cref="Material.IsTransparent" />) in the transparent pass; a renderer can have both.
/// </summary>
[ExecuteAlways]
[InspectorColor(InspectorColorKind.Rendering)]
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
	///     Draws nothing while set: no camera, shadow map or GI pass takes the renderer, as if it were disabled (Unity's
	///     forceRenderingOff). Not saved, and the component stays enabled: for what a script hides for a while without
	///     changing the object's own state (an edit-mode preview that puts something else in its place: a scene or a
	///     prefab saved meanwhile keeps the renderer as it was, and nothing has to be put back after a reload).
	/// </summary>
	public bool forceRenderingOff
	{
		get => renderingOff;
		set
		{
			if (renderingOff == value)
			{
				return;
			}

			renderingOff = value;
			// A static batch's part: the batch shows or hides its triangles.
			batch?.PartChanged();
		}
	}

	/// <summary>
	///     The renderer's object is static (<see cref="GameObject.isStatic" />: the renderer has no flag of its own; until
	///     2026-11-20 it had, and files of that time are read into the object's): transform, mesh and material do not
	///     change at runtime. Static renderers are voxelized once into a cached volume instead of every frame; call
	///     Lighting.GI.InvalidateStatic() after moving or re-materialing one.
	/// </summary>
	public bool IsStatic => gameObject != null && gameObject.isStatic;

	/// <summary>
	///     The static batch that draws this renderer's mesh as a part of its own (static batching, in a built game and
	///     in the editor's play mode: <see cref="StaticBatch" />): the cameras and the shadow maps leave the renderer out
	///     while the batch shows its part, the GI still takes the renderer itself. Null for a renderer that draws itself.
	/// </summary>
	public StaticBatch Batch => batch != null && batch.Draws(this) ? batch : null;

	/// <summary>The renderer has shader values of its own (<see cref="SetPropertyBlock" />).</summary>
	public bool HasPropertyBlock => propertyBlock != null && !propertyBlock.isEmpty;

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

	// LODGroup's selection stamps: equal to the frame's selection when its group's level leaves the renderer out of
	// drawing / out of the GI (LODGroup.IsHidden / IsOutOfGI).
	[System.NonSerialized] internal int lodHidden;
	[System.NonSerialized] internal int lodOutOfGI;

	// Frustum culling (Rendering.ViewCulling): the mesh's world bounds (centre, half size) as of the rendered frame
	// boundsEpoch, and whether the view cullStamp sees them.
	[System.NonSerialized] internal int boundsEpoch;
	[System.NonSerialized] internal LELEngine.Mathematics.Vector3 worldCenter;
	[System.NonSerialized] internal LELEngine.Mathematics.Vector3 worldExtent;
	[System.NonSerialized] internal int cullStamp;
	[System.NonSerialized] internal bool cullVisible;

	// forceRenderingOff's value; never saved.
	[System.NonSerialized] private bool renderingOff;

	// The renderer's own shader values (a copy of the block given to SetPropertyBlock); not saved, as in Unity.
	[System.NonSerialized] private MaterialPropertyBlock propertyBlock;

	// The static batch holding this renderer as a part (set by the batch when it is read; see Batch).
	[System.NonSerialized] internal StaticBatch batch;

	// The static batch whose mesh this renderer draws (the batch's own renderer): the transparent pass sorts it by parts.
	[System.NonSerialized] internal StaticBatch ownBatch;

	/// <summary>Sub-meshes to consider (a renderer without a mesh still reports its material).</summary>
	private int PartCount => mesh != null ? mesh.SubMeshCount : 1;

	#endregion

	#region PublicMethods

	// A static batch's part: the batch shows or hides its triangles as the renderer comes and goes.
	public override void OnEnable()
	{
		batch?.PartChanged();
	}

	public override void OnDisable()
	{
		batch?.PartChanged();
	}

	// Edited in the inspector while a static batch draws it (play mode): the batch's mesh holds what the renderer had,
	// so it draws itself from now on, with what it has now.
	public override void OnValidate()
	{
		batch?.Leave(this);
	}

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

	/// <summary>Replaces every material slot. A renderer a static batch draws leaves its batch (it draws itself again).</summary>
	public void SetMaterials(IEnumerable<Material> slots)
	{
		materials.Clear();
		if (slots != null)
		{
			materials.AddRange(slots);
		}

		batch?.Leave(this);
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
		batch?.Leave(this);
	}

	public void SetMesh(Mesh value)
	{
		mesh = value;
		batch?.Leave(this);
	}

	/// <summary>
	///     Shader values of this renderer on top of its materials (Unity's SetPropertyBlock): the block's values are copied
	///     and uploaded after the material's at every draw of the renderer, in the opaque and the transparent pass. Null or
	///     an empty block removes them. A renderer with values of its own draws one by one (not in a GPU-driven bucket).
	///     Not used by the depth, shadow and GI passes (they draw the geometry only).
	/// </summary>
	public void SetPropertyBlock(MaterialPropertyBlock block)
	{
		if (block == null || block.isEmpty)
		{
			propertyBlock = null;
			return;
		}

		propertyBlock ??= new MaterialPropertyBlock();
		propertyBlock.CopyFrom(block);

		// Values of its own are drawn with its own draw: out of the static batch that drew it.
		batch?.Leave(this);
	}

	/// <summary>Copies the renderer's own shader values into <paramref name="destination" /> (cleared when there are none).</summary>
	public void GetPropertyBlock(MaterialPropertyBlock destination)
	{
		if (destination == null)
		{
			return;
		}

		destination.CopyFrom(propertyBlock);
	}

	/// <summary>The renderer's own values as drawn (null when it has none); read only.</summary>
	internal MaterialPropertyBlock PropertyBlock => propertyBlock;

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
			m.SetUniforms(shader, propertyBlock);
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
		m.SetUniforms(program, propertyBlock);
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
	///     (not those of a shader with ShadowCaster Off) with the engine's depth program. Those whose shader casts its
	///     shadows itself (ShadowCaster Custom) are left to <see cref="RenderOwnShadowCasters" />.
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

	/// <summary>
	///     The program a sub-mesh's material shader draws one of the engine's passes with (<see cref="ShaderPass" />): its
	///     variant of that pass, when the shader declares one and it links; null when the engine's own program draws the
	///     sub-mesh in that pass.
	/// </summary>
	public ShaderProgram GetPassProgram(int subMesh, ShaderPass pass)
	{
		ShaderProgram program = GetMaterial(subMesh)?.Program;
		if (program == null || !ShaderPasses.Declares(program.RenderState, pass))
		{
			return null;
		}

		ShaderProgram variant = program.GetPassVariant(pass);
		return variant != null && variant.IsLinked ? variant : null;
	}

	/// <summary>
	///     Draws one sub-mesh with its material shader's own variant of a pass (<see cref="GetPassProgram" />): the
	///     pass's view, the object (and its world matrix of the previous frame, for the geometry prepass), the material's
	///     values with the renderer's own on top, the shader's cull mode. False, and nothing drawn, when the shader has no
	///     such variant: the engine's program draws the sub-mesh. The variant stays bound.
	/// </summary>
	public bool RenderSubMeshPass(int subMesh, ShaderPass pass, LELEngine.Mathematics.Matrix4 view, LELEngine.Mathematics.Matrix4 projection, LELEngine.Mathematics.Matrix4? previousModel = null)
	{
		if (mesh == null)
		{
			return false;
		}

		ShaderProgram variant = GetPassProgram(subMesh, pass);
		if (variant == null)
		{
			return false;
		}

		variant.Use();
		variant.RenderState.ApplyCull();
		variant.SetMatrix4("viewMatrix", view);
		variant.SetMatrix4("projectionMatrix", projection);
		transform.SetModelMatrix(variant);
		if (previousModel.HasValue)
		{
			variant.SetMatrix4("previousModelMatrix", previousModel.Value);
		}

		GetMaterial(subMesh).SetUniforms(variant, propertyBlock);
		DrawSubMesh(subMesh);
		return true;
	}

	/// <summary>
	///     Draws into a shadow map the opaque sub-meshes whose shader casts its shadows itself (ShadowCaster Custom), each
	///     with its own program; what <see cref="RenderShadowCastersWith" /> leaves out. True when something drew: the
	///     caller's program is no longer bound and the cull mode may have changed.
	/// </summary>
	public bool RenderOwnShadowCasters(LELEngine.Mathematics.Matrix4 view, LELEngine.Mathematics.Matrix4 projection)
	{
		if (mesh == null || !HasOwnShadowCasters)
		{
			return false;
		}

		bool drawn = false;
		for (int i = 0; i < mesh.SubMeshCount; i++)
		{
			if (!IsSubMeshTransparent(i) && IsSubMeshShadowCaster(i))
			{
				drawn |= RenderSubMeshPass(i, ShaderPass.ShadowCaster, view, projection);
			}
		}

		return drawn;
	}

	/// <summary>Some sub-mesh's shader casts its shadows itself (ShadowCaster Custom).</summary>
	internal bool HasOwnShadowCasters
	{
		get
		{
			for (int i = 0; i < PartCount; i++)
			{
				if (IsOwnShadowCaster(i))
				{
					return true;
				}
			}

			return false;
		}
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
		// A texture the material made on the way here ended the pass (Renderer.BeginPass).
		LELEngine.Rendering.Renderer.ContinuePass();
		LELEngine.Rendering.GeometryPool.Draw(DrawnMesh);
	}

	/// <summary>Binds the shared geometry and draws one sub-mesh. No uniforms are touched.</summary>
	public void DrawSubMesh(int subMesh)
	{
		// A texture the material made on the way here ended the pass (Renderer.BeginPass).
		LELEngine.Rendering.Renderer.ContinuePass();
		LELEngine.Rendering.GeometryPool.DrawSubMesh(DrawnMesh, subMesh);
	}

	/// <summary>The transform mirrors (a negative determinant: an odd number of negative scale components along its parents).</summary>
	public bool IsMirrored
	{
		get
		{
			Transform t = transform;
			if (t == null)
			{
				return false;
			}

			// Rotations keep the determinant's sign: the product of the scales' components along the chain is its sign.
			LELEngine.Mathematics.Vector3 scale = t.lossyScale;
			return scale.X * scale.Y * scale.Z < 0f;
		}
	}

	/// <summary>
	///     The mesh the GPU draws: <see cref="Mesh" />, or under a mirroring transform its twin with the triangles wound the
	///     other way (Unity flips the front face for such objects; one winding per draw keeps the GPU-driven buckets whole).
	/// </summary>
	internal Mesh DrawnMesh => mesh == null ? null : IsMirrored ? mesh.Mirrored : mesh;

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
			every = IsSubMeshShadowCaster(i) && !IsOwnShadowCaster(i);
		}

		if (every)
		{
			DrawGeometry();
			return;
		}

		for (int i = 0; i < mesh.SubMeshCount; i++)
		{
			if (!IsSubMeshTransparent(i) && (!shadowCastersOnly || (IsSubMeshShadowCaster(i) && !IsOwnShadowCaster(i))))
			{
				DrawSubMesh(i);
			}
		}
	}

	// The sub-mesh's shader casts its shadows itself, with a variant that links (otherwise the engine's program draws it).
	private bool IsOwnShadowCaster(int subMesh)
	{
		Material m = GetMaterial(subMesh);
		return m != null && m.RenderState.CustomShadowCaster && GetPassProgram(subMesh, ShaderPass.ShadowCaster) != null;
	}

	#endregion
}
