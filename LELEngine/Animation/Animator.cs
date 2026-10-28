using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>A component that applies the animator's root motion itself (Unity's OnAnimatorMove): the animator then leaves the transform alone.</summary>
	public interface IAnimatorMoveHandler
	{
		void OnAnimatorMove(Animator animator);
	}

	/// <summary>
	///     Plays an <see cref="AnimatorController" /> on this GameObject's hierarchy (Unity's Animator): the controller's
	///     state machines pick clips by its parameters, blend them and write the animated properties every frame
	///     (after the Update calls, or in every fixed step with <see cref="updateMode" /> AnimatePhysics). Animators
	///     of a scene advance in parallel on worker threads when there are enough of them
	///     (<see cref="AnimationSystem" />); the properties are written and the callbacks delivered on the main thread.
	/// </summary>
	[InspectorColor(0.85f, 0.55f, 0.25f)]
	public sealed class Animator : Behaviour
	{
		#region PublicFields

		/// <summary>The controller played (changing it rebuilds the playback; parameter values of the same ids survive).</summary>
		public AnimatorController controller
		{
			get => m_Controller;
			set
			{
				if (m_Controller == value)
				{
					return;
				}

				m_Controller = value;
				rebuildRequested = true;
			}
		}

		public AnimatorUpdateMode updateMode = AnimatorUpdateMode.Normal;

		/// <summary>Playback speed of every state (1 = as authored).</summary>
		public float speed = 1f;

		/// <summary>
		///     The root transform's animated position and rotation move the object instead of being written to it (a
		///     walk cycle moves the character); <see cref="IAnimatorMoveHandler" /> components take over the application.
		/// </summary>
		public bool applyRootMotion;

		/// <summary>Disabling the component keeps the current states and parameter values (else it restarts at entry).</summary>
		public bool keepStateOnDisable;

		/// <summary>May evaluate on a worker thread together with the scene's other animators.</summary>
		public bool allowParallelUpdate = true;

		/// <summary>Root motion of the last frame (in the root's frame), also with <see cref="applyRootMotion" /> off.</summary>
		public Vector3 deltaPosition { get; private set; }

		public Quaternion deltaRotation { get; private set; } = Quaternion.Identity;

		/// <summary>An animation event fired (after the method named by it was called).</summary>
		public event Action<AnimationEvent> AnimationEventFired;

		/// <summary>A layer entered a state (the layer index and the state).</summary>
		public event Action<int, AnimatorStateInfo> StateEntered;

		/// <summary>Root motion is about to be applied this frame (read deltaPosition / deltaRotation).</summary>
		public event Action<Animator> AnimatorMove;

		/// <summary>The playback (built on the first update or <see cref="Rebind" />); null before.</summary>
		public AnimatorPlayer Player => player;

		public bool isInitialized => player != null && player.IsBuilt;
		public int layerCount => player?.Layers.Length ?? controller?.layers.Count ?? 0;
		public int parameterCount => player?.Parameters.Length ?? controller?.parameters.Count ?? 0;

		/// <summary>The live parameter values (after the first update).</summary>
		public IReadOnlyList<ParameterState> parameters => player?.Parameters ?? Array.Empty<ParameterState>();

		/// <summary>The controller's compiled form (shared), null without a controller.</summary>
		public RuntimeController runtimeController => player?.Controller;

		#endregion

		#region PrivateFields

		[SerializeField] private AnimatorController m_Controller;
		private AnimatorPlayer player;
		private bool rebuildRequested;
		private Dictionary<int, int> parameterByHash;
		private int parameterHashVersion = -1;
		private static readonly Dictionary<string, int> hashCache = new Dictionary<string, int>(StringComparer.Ordinal);

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			AnimationSystem.Register(this);
		}

		public override void OnDisable()
		{
			AnimationSystem.Unregister(this);
			if (!keepStateOnDisable && player != null)
			{
				foreach (LayerPlayer layer in player.Layers)
				{
					layer.Reset();
				}

				foreach (ParameterState parameter in player.Parameters)
				{
					parameter.Reset();
				}
			}
		}

		public override void OnDestroy()
		{
			AnimationSystem.Unregister(this);
			player = null;
		}

		public override void OnValidate()
		{
			rebuildRequested = true;
			if (speed < 0f && float.IsNaN(speed))
			{
				speed = 1f;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>A stable hash of a state, parameter or tag name (FNV-1a), the same in every run and build.</summary>
		public static int StringToHash(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return 0;
			}

			lock (hashCache)
			{
				if (hashCache.TryGetValue(name, out int cached))
				{
					return cached;
				}

				uint hash = 2166136261;
				foreach (char c in name)
				{
					hash ^= c;
					hash *= 16777619;
				}

				int result = unchecked((int)hash);
				hashCache[name] = result;
				return result;
			}
		}

		// ---------------------------------------------------------------- parameters

		public float GetFloat(string name) => Get<float>(name);
		public float GetFloat(int hash) => Get<float>(hash);
		public void SetFloat(string name, float value) => Set(name, value);
		public void SetFloat(int hash, float value) => Set(hash, value);
		public int GetInteger(string name) => Get<int>(name);
		public int GetInteger(int hash) => Get<int>(hash);
		public void SetInteger(string name, int value) => Set(name, value);
		public void SetInteger(int hash, int value) => Set(hash, value);
		public bool GetBool(string name) => Get<bool>(name);
		public bool GetBool(int hash) => Get<bool>(hash);
		public void SetBool(string name, bool value) => Set(name, value);
		public void SetBool(int hash, bool value) => Set(hash, value);
		public void SetTrigger(string name) => Set(name, true);
		public void SetTrigger(int hash) => Set(hash, true);

		public void ResetTrigger(string name)
		{
			GetParameterState(name)?.ResetTrigger();
		}

		public void ResetTrigger(int hash)
		{
			GetParameterState(hash)?.ResetTrigger();
		}

		/// <summary>Sets a parameter of any type (a value of another type converts when it is a number).</summary>
		public void Set<T>(string name, T value)
		{
			ParameterState state = GetParameterState(name);
			if (state == null)
			{
				WarnMissing(name);
				return;
			}

			if (state is ParameterState<T> typed)
			{
				typed.Set(value);
			}
			else
			{
				state.Boxed = value;
			}
		}

		public void Set<T>(int hash, T value)
		{
			ParameterState state = GetParameterState(hash);
			if (state == null)
			{
				return;
			}

			if (state is ParameterState<T> typed)
			{
				typed.Set(value);
			}
			else
			{
				state.Boxed = value;
			}
		}

		/// <summary>A parameter's value (default of the type when missing or of another type).</summary>
		public T Get<T>(string name)
		{
			ParameterState state = GetParameterState(name);
			if (state == null)
			{
				WarnMissing(name);
				return default;
			}

			return state is ParameterState<T> typed ? typed.value : state.Boxed is T boxed ? boxed : Converted<T>(state.Boxed);
		}

		public T Get<T>(int hash)
		{
			ParameterState state = GetParameterState(hash);
			if (state == null)
			{
				return default;
			}

			return state is ParameterState<T> typed ? typed.value : state.Boxed is T boxed ? boxed : Converted<T>(state.Boxed);
		}

		/// <summary>The live slot of a parameter (null when missing); the fastest way to read or write it repeatedly.</summary>
		public ParameterState GetParameterState(string name)
		{
			EnsureBuilt();
			if (player == null)
			{
				return null;
			}

			int index = player.ParameterIndex(name);
			return index >= 0 ? player.Parameters[index] : null;
		}

		public ParameterState GetParameterState(int hash)
		{
			EnsureBuilt();
			if (player == null)
			{
				return null;
			}

			if (parameterByHash == null || parameterHashVersion != player.ControllerVersion)
			{
				parameterByHash = new Dictionary<int, int>();
				for (int i = 0; i < player.Parameters.Length; i++)
				{
					parameterByHash[StringToHash(player.Parameters[i].Name)] = i;
				}

				parameterHashVersion = player.ControllerVersion;
			}

			return parameterByHash.TryGetValue(hash, out int index) ? player.Parameters[index] : null;
		}

		public ParameterState GetParameter(int index)
		{
			EnsureBuilt();
			return player != null && index >= 0 && index < player.Parameters.Length ? player.Parameters[index] : null;
		}

		public bool HasParameter(string name)
		{
			EnsureBuilt();
			return player != null && player.ParameterIndex(name) >= 0;
		}

		// ---------------------------------------------------------------- playback

		/// <summary>Plays a state at once ("Idle", "Sub/Idle"; the layer: -1 = the first layer holding the state).</summary>
		public void Play(string stateName, int layer = -1, float normalizedTime = float.NaN)
		{
			EnsureBuilt();
			if (player == null || !FindState(stateName, ref layer, out RuntimeState state))
			{
				Debug.LogWarning($"[Animator] '{gameObject?.Name}': no state '{stateName}'" + (layer >= 0 ? " on layer " + layer : ""), this);
				return;
			}

			player.Layers[layer].Play(state, normalizedTime);
		}

		public void Play(int stateHash, int layer = -1, float normalizedTime = float.NaN)
		{
			EnsureBuilt();
			if (player == null || !FindState(stateHash, ref layer, out RuntimeState state))
			{
				return;
			}

			player.Layers[layer].Play(state, normalizedTime);
		}

		/// <summary>Blends to a state over <paramref name="normalizedDuration" /> cycles of the current state.</summary>
		public void CrossFade(string stateName, float normalizedDuration, int layer = -1, float normalizedOffset = 0f)
		{
			EnsureBuilt();
			if (player == null || !FindState(stateName, ref layer, out RuntimeState state))
			{
				Debug.LogWarning($"[Animator] '{gameObject?.Name}': no state '{stateName}'", this);
				return;
			}

			LayerPlayer target = player.Layers[layer];
			float seconds = normalizedDuration * Math.Max(target.Current?.Length ?? 1f, 1e-4f);
			target.CrossFade(state, seconds, normalizedOffset);
		}

		/// <summary>Blends to a state over <paramref name="seconds" />.</summary>
		public void CrossFadeInFixedTime(string stateName, float seconds, int layer = -1, float normalizedOffset = 0f)
		{
			EnsureBuilt();
			if (player == null || !FindState(stateName, ref layer, out RuntimeState state))
			{
				Debug.LogWarning($"[Animator] '{gameObject?.Name}': no state '{stateName}'", this);
				return;
			}

			player.Layers[layer].CrossFade(state, seconds, normalizedOffset);
		}

		public bool HasState(int layer, string stateName)
		{
			EnsureBuilt();
			return player != null && layer >= 0 && layer < player.Layers.Length && player.Layers[layer].Layer.FindState(stateName) != null;
		}

		public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer = 0)
		{
			return Layer(layer)?.CurrentInfo ?? default;
		}

		public AnimatorStateInfo GetNextAnimatorStateInfo(int layer = 0)
		{
			return Layer(layer)?.NextInfo ?? default;
		}

		public bool IsInTransition(int layer = 0)
		{
			return Layer(layer)?.InTransition ?? false;
		}

		public AnimatorTransitionInfo GetAnimatorTransitionInfo(int layer = 0)
		{
			return Layer(layer)?.TransitionInfo ?? default;
		}

		/// <summary>The clips sampled on a layer in the last update, with their weights.</summary>
		public List<AnimatorClipInfo> GetCurrentAnimatorClipInfo(int layer = 0)
		{
			LayerPlayer target = Layer(layer);
			return target != null ? new List<AnimatorClipInfo>(target.SampledClips) : new List<AnimatorClipInfo>();
		}

		public float GetLayerWeight(int layer)
		{
			return Layer(layer)?.Weight ?? 0f;
		}

		public void SetLayerWeight(int layer, float weight)
		{
			LayerPlayer target = Layer(layer);
			if (target != null)
			{
				target.Weight = Math.Clamp(weight, 0f, 1f);
			}
		}

		public string GetLayerName(int layer)
		{
			return Layer(layer)?.Layer.Name;
		}

		public int GetLayerIndex(string layerName)
		{
			EnsureBuilt();
			if (player == null)
			{
				return -1;
			}

			for (int i = 0; i < player.Layers.Length; i++)
			{
				if (player.Layers[i].Layer.Name == layerName)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>Rebuilds the playback for the hierarchy (objects added or renamed) and the controller.</summary>
		public void Rebind()
		{
			player = player ?? new AnimatorPlayer(this);
			player.Build();
			rebuildRequested = false;
			LogPending();
		}

		/// <summary>Advances and applies by hand (an animator the scene does not update: disabled, or a manual update mode).</summary>
		public void Update(float deltaTime)
		{
			EnsureBuilt();
			if (player == null)
			{
				return;
			}

			player.Advance(deltaTime);
			if (player.LastError != null)
			{
				Debug.LogException(player.LastError, this);
			}

			player.Apply();
			LogPending();
		}

		/// <summary>Writes the current pose again without advancing (after other code changed the properties).</summary>
		public void ApplyPose()
		{
			player?.FinalPose?.Apply(player.Layout, applyRootMotion && player.RootMotionSampled);
		}

		#endregion

		#region InternalMethods

		/// <summary>Builds the playback when missing or stale (main thread).</summary>
		internal void EnsureBuilt()
		{
			if (player == null)
			{
				if (m_Controller == null)
				{
					return;
				}

				player = new AnimatorPlayer(this);
				player.Build();
				rebuildRequested = false;
				LogPending();
				return;
			}

			if (rebuildRequested || player.NeedsRebuild())
			{
				player.Build();
				rebuildRequested = false;
				LogPending();
			}
		}

		internal void ApplyRootMotion(Vector3 localDelta, Quaternion rotationDelta)
		{
			deltaPosition = localDelta;
			deltaRotation = rotationDelta;
			bool handled = false;
			if (AnimatorMove != null)
			{
				try
				{
					AnimatorMove(this);
				}
				catch (Exception e)
				{
					Debug.LogException(e, this);
				}

				handled = true;
			}

			IReadOnlyList<Behaviour> components = gameObject.Components;
			for (int i = 0; i < components.Count; i++)
			{
				if (components[i] is IAnimatorMoveHandler handler && components[i].enabled)
				{
					handled = true;
					try
					{
						handler.OnAnimatorMove(this);
					}
					catch (Exception e)
					{
						Debug.LogException(e, components[i]);
					}
				}
			}

			if (handled)
			{
				return;
			}

			Transform t = transform;
			if (t == null)
			{
				return;
			}

			t.localPosition += t.localRotation * localDelta;
			t.localRotation = (t.localRotation * rotationDelta).Normalized();
		}

		internal void DeliverCallbacks(List<AnimatorPlayer.Callback> callbacks, List<AnimationEvent> events)
		{
			foreach (AnimatorPlayer.Callback callback in callbacks)
			{
				try
				{
					switch (callback.Kind)
					{
						case AnimatorPlayer.CallbackKind.Enter:
							callback.Behaviour.OnStateEnter(this, callback.Info, callback.Layer);
							break;
						case AnimatorPlayer.CallbackKind.Update:
							callback.Behaviour.OnStateUpdate(this, callback.Info, callback.Layer);
							break;
						case AnimatorPlayer.CallbackKind.Exit:
							callback.Behaviour.OnStateExit(this, callback.Info, callback.Layer);
							break;
						case AnimatorPlayer.CallbackKind.MachineEnter:
							callback.Behaviour.OnStateMachineEnter(this, callback.MachineId);
							break;
						case AnimatorPlayer.CallbackKind.MachineExit:
							callback.Behaviour.OnStateMachineExit(this, callback.MachineId);
							break;
						case AnimatorPlayer.CallbackKind.Move:
							callback.Behaviour.OnStateMove(this, callback.Info, callback.Layer);
							break;
						case AnimatorPlayer.CallbackKind.StateChanged:
							StateEntered?.Invoke(callback.Layer, callback.Info);
							break;
					}
				}
				catch (Exception e)
				{
					Debug.LogException(e, this);
				}
			}

			foreach (AnimationEvent e in events)
			{
				AnimationEventDispatcher.Dispatch(gameObject, e);
				if (AnimationEventFired != null)
				{
					try
					{
						AnimationEventFired(e);
					}
					catch (Exception ex)
					{
						Debug.LogException(ex, this);
					}
				}
			}
		}

		internal void LogPending()
		{
			if (player == null || player.PendingLogs.Count == 0)
			{
				return;
			}

			foreach (string message in player.PendingLogs)
			{
				Debug.LogWarning("[Animator] '" + (gameObject?.Name ?? "?") + "': " + message, this);
			}

			player.PendingLogs.Clear();
		}

		#endregion

		#region PrivateMethods

		private LayerPlayer Layer(int index)
		{
			EnsureBuilt();
			return player != null && index >= 0 && index < player.Layers.Length ? player.Layers[index] : null;
		}

		private bool FindState(string stateName, ref int layer, out RuntimeState state)
		{
			state = null;
			if (layer >= 0)
			{
				if (layer >= player.Layers.Length)
				{
					return false;
				}

				state = player.Layers[layer].Layer.FindState(stateName);
				return state != null;
			}

			for (int i = 0; i < player.Layers.Length; i++)
			{
				state = player.Layers[i].Layer.FindState(stateName);
				if (state != null)
				{
					layer = i;
					return true;
				}
			}

			return false;
		}

		private bool FindState(int hash, ref int layer, out RuntimeState state)
		{
			state = null;
			int first = layer >= 0 ? layer : 0;
			int last = layer >= 0 ? layer : player.Layers.Length - 1;
			for (int i = first; i <= last && i < player.Layers.Length; i++)
			{
				foreach (RuntimeState candidate in player.Layers[i].Layer.States)
				{
					if (candidate.NameHash == hash || candidate.FullNameHash == hash)
					{
						state = candidate;
						layer = i;
						return true;
					}
				}
			}

			return false;
		}

		private void WarnMissing(string name)
		{
			if (m_Controller != null)
			{
				Debug.LogWarning($"[Animator] '{gameObject?.Name}': the controller '{m_Controller.name}' has no parameter '{name}'", this);
			}
		}

		private static T Converted<T>(object value)
		{
			try
			{
				return value != null ? (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture) : default;
			}
			catch (Exception)
			{
				return default;
			}
		}

		#endregion
	}
}
