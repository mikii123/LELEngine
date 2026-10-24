using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     Plays a <see cref="VfxGraph" /> asset at the GameObject's transform (Unity's VisualEffect component): the
	///     particles simulate on the GPU every frame (also in edit mode) and draw through the renderer's passes.
	///     Exposed properties of the graph can be overridden per component (<see cref="propertyOverrides" />, the
	///     inspector) or from scripts (<see cref="SetFloat" /> and friends); <see cref="SendEvent" /> raises the events
	///     the graph's spawn contexts listen to.
	/// </summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.Effects)]
	public sealed class VisualEffect : Behaviour
	{
		#region PublicFields

		/// <summary>The graph asset played.</summary>
		public VfxGraph visualEffectAsset
		{
			get => asset;
			set
			{
				if (asset == value)
				{
					return;
				}

				asset = value;
				if (instance != null)
				{
					instance.SetGraph(value);
					instance.SetOverrides(propertyOverrides);
					if (value != null && playOnAwake)
					{
						instance.Play();
					}
				}
			}
		}

		/// <summary>Per-component values of the graph's exposed properties (the inspector shows them below the fields).</summary>
		[HideInInspector] public List<VfxPropertyOverride> propertyOverrides = new List<VfxPropertyOverride>();

		/// <summary>Start playing when the component becomes active.</summary>
		public bool playOnAwake = true;

		/// <summary>Speed of the simulation (1 = real time).</summary>
		public float playRate = 1f;

		/// <summary>Freezes the simulation (the particles stay as they are).</summary>
		public bool pause;

		/// <summary>Random seed; 0 draws one on every play (see <see cref="resetSeedOnPlay" />).</summary>
		public uint startSeed;

		public bool resetSeedOnPlay = true;

		/// <summary>Also simulate in the editor's edit mode (the scene view shows the effect running).</summary>
		public bool simulateInEditMode = true;

		/// <summary>The playing instance (null while disabled or without a GL context).</summary>
		public VfxInstance Instance => instance;

		/// <summary>Alive particles (a few frames behind).</summary>
		public int aliveParticleCount => instance?.AliveCount ?? 0;

		/// <summary>Every output was outside the last view.</summary>
		public bool culled => instance?.Culled ?? false;

		public bool isPlaying => instance != null && instance.Playing;

		/// <summary>Seconds the effect has been playing.</summary>
		public float time => instance?.Time ?? 0f;

		#endregion

		#region PrivateFields

		[SerializeField] private VfxGraph asset;
		[NonSerialized] private VfxInstance instance;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			EnsureInstance();
			if (instance == null)
			{
				return;
			}

			instance.Enable();
			instance.SetOverrides(propertyOverrides);
			if (playOnAwake && asset != null && (gameObject.scene == null || gameObject.scene.IsPlaying || simulateInEditMode))
			{
				instance.Play();
			}
		}

		public override void OnDisable()
		{
			instance?.Disable();
		}

		public override void LateUpdate()
		{
			if (instance == null || asset == null)
			{
				return;
			}

			Scene scene = gameObject?.scene;
			if (scene != null && !scene.IsPlaying && !simulateInEditMode)
			{
				return;
			}

			instance.LocalToWorld = transform.LocalToWorld;
			instance.Layer = gameObject?.layer ?? 0;
			instance.Paused = pause;
			instance.PlayRate = playRate;
			instance.ResetSeedOnPlay = resetSeedOnPlay && startSeed == 0;
			instance.Simulate(Time.deltaTime);
		}

		public override void OnValidate()
		{
			if (instance == null)
			{
				return;
			}

			if (instance.Graph != asset)
			{
				instance.SetGraph(asset);
				if (asset != null && playOnAwake)
				{
					instance.Play();
				}
			}

			instance.SetOverrides(propertyOverrides);
			if (startSeed != 0)
			{
				instance.Seed = startSeed;
			}
		}

		protected internal override void ReleaseResources()
		{
			instance?.Dispose();
			instance = null;
		}

		#endregion

		#region PublicMethods

		public void Play()
		{
			EnsureInstance();
			instance?.Play();
		}

		public void Stop()
		{
			instance?.Stop();
		}

		/// <summary>Kills every particle and restarts.</summary>
		public void Reinit()
		{
			instance?.Reinit();
		}

		/// <summary>Raises a named event the graph's spawn contexts listen to.</summary>
		public void SendEvent(string eventName)
		{
			EnsureInstance();
			instance?.SendEvent(eventName);
		}

		/// <summary>Runs the simulation for a number of steps now (pre-warming).</summary>
		public void Simulate(float stepDeltaTime, int steps = 1)
		{
			if (instance == null)
			{
				return;
			}

			instance.LocalToWorld = transform.LocalToWorld;
			instance.Layer = gameObject?.layer ?? 0;
			for (int i = 0; i < steps; i++)
			{
				instance.Simulate(stepDeltaTime);
			}
		}

		public bool HasProperty(string propertyName)
		{
			return asset?.FindProperty(propertyName) != null;
		}

		/// <summary>The value of an exposed property for this component (its override, else the graph's default).</summary>
		public VfxValue GetProperty(string propertyName)
		{
			foreach (VfxPropertyOverride entry in propertyOverrides)
			{
				if (entry != null && entry.Enabled && entry.Name == propertyName)
				{
					return entry.Value;
				}
			}

			return asset?.FindProperty(propertyName)?.Value;
		}

		/// <summary>Overrides a property for this component (kept in <see cref="propertyOverrides" />, so it is saved with the scene).</summary>
		public void SetProperty(string propertyName, VfxValue value)
		{
			VfxPropertyOverride existing = null;
			foreach (VfxPropertyOverride entry in propertyOverrides)
			{
				if (entry != null && entry.Name == propertyName)
				{
					existing = entry;
					break;
				}
			}

			if (existing == null)
			{
				existing = new VfxPropertyOverride { Name = propertyName };
				propertyOverrides.Add(existing);
			}

			existing.Enabled = true;
			existing.Value = value;
			instance?.SetOverrides(propertyOverrides);
		}

		public void SetFloat(string propertyName, float value) => SetProperty(propertyName, VfxValue.FromFloat(value));
		public void SetInt(string propertyName, int value) => SetProperty(propertyName, VfxValue.FromInt(value));
		public void SetBool(string propertyName, bool value) => SetProperty(propertyName, VfxValue.FromBool(value));
		public void SetVector2(string propertyName, Vector2 value) => SetProperty(propertyName, VfxValue.FromVector2(value));
		public void SetVector3(string propertyName, Vector3 value) => SetProperty(propertyName, VfxValue.FromVector3(value));
		public void SetVector4(string propertyName, Vector4 value) => SetProperty(propertyName, VfxValue.FromVector4(value));
		public void SetColor(string propertyName, Color4 value) => SetProperty(propertyName, VfxValue.FromColor(value));
		public void SetTexture(string propertyName, Texture2D value) => SetProperty(propertyName, VfxValue.FromTexture(value));
		public void SetGradient(string propertyName, VfxGradient value) => SetProperty(propertyName, VfxValue.FromGradient(value));
		public void SetCurve(string propertyName, VfxCurve value) => SetProperty(propertyName, VfxValue.FromCurve(value));
		public void SetMesh(string propertyName, Mesh value) => SetProperty(propertyName, VfxValue.FromMesh(value));

		public float GetFloat(string propertyName) => GetProperty(propertyName)?.ConvertTo(VfxValueType.Float).Float ?? 0f;
		public int GetInt(string propertyName) => GetProperty(propertyName)?.ConvertTo(VfxValueType.Int).Int ?? 0;
		public bool GetBool(string propertyName) => GetProperty(propertyName)?.ConvertTo(VfxValueType.Bool).Bool ?? false;
		public Vector3 GetVector3(string propertyName) => GetProperty(propertyName)?.ConvertTo(VfxValueType.Vector3).Vector3 ?? Vector3.Zero;
		public Vector4 GetVector4(string propertyName) => GetProperty(propertyName)?.ConvertTo(VfxValueType.Vector4).Vector ?? Vector4.Zero;
		public Color4 GetColor(string propertyName) => GetProperty(propertyName)?.ConvertTo(VfxValueType.Color).Color ?? Color4.White;

		/// <summary>Removes a component override (the graph's default applies again).</summary>
		public void ResetOverride(string propertyName)
		{
			propertyOverrides.RemoveAll(entry => entry == null || entry.Name == propertyName);
			instance?.SetOverrides(propertyOverrides);
		}

		/// <summary>World bounds of the effect's systems (false when unknown).</summary>
		public bool GetWorldBounds(out Vector3 min, out Vector3 max)
		{
			if (instance != null && instance.GetWorldBounds(out min, out max))
			{
				return true;
			}

			min = max = transform.position;
			return false;
		}

		#endregion

		#region PrivateMethods

		private void EnsureInstance()
		{
			if (instance != null || !VfxRuntime.Available)
			{
				return;
			}

			instance = new VfxInstance(asset) { Name = gameObject?.Name ?? "VisualEffect" };
			if (startSeed != 0)
			{
				instance.Seed = startSeed;
			}

			instance.LocalToWorld = transform?.LocalToWorld ?? Matrix4.Identity;
			instance.Layer = gameObject?.layer ?? 0;
			instance.SetOverrides(propertyOverrides);
		}

		#endregion
	}
}
