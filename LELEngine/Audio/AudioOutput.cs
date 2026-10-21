using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LELEngine.Audio
{
	/// <summary>Produces the output signal: called on the audio thread with the frames the device wants next.</summary>
	public interface IAudioRenderer
	{
		/// <summary>Fills <paramref name="frames" /> frames of interleaved stereo floats into <paramref name="buffer" /> (from index 0).</summary>
		void Render(float[] buffer, int frames);
	}

	/// <summary>
	///     The audio device and its thread. Stereo float at <see cref="SampleRate" /> is rendered by an
	///     <see cref="IAudioRenderer" /> in blocks the device asks for, and written to the default output device through
	///     WASAPI (shared mode, event driven; the device converts the rate when it can, else the output resamples).
	///     Without a device (none plugged in, another OS, <see cref="AudioOutputOptions.Disabled" />) the thread keeps
	///     rendering at real-time pace into nowhere, so events still advance. A device lost or changed (the default
	///     device switched, headphones plugged in) is reopened on the thread; the renderer never sees any of it.
	/// </summary>
	public sealed class AudioOutput : IDisposable
	{
		#region PublicFields

		/// <summary>Rate every block is rendered at (fixed: positions and bank data are in these samples).</summary>
		public const int SampleRate = 48000;

		public const int Channels = 2;

		/// <summary>Most frames one render call asks for.</summary>
		public const int MaxBlockFrames = 8192;

		/// <summary>A device is open and takes the rendered audio.</summary>
		public bool IsDeviceOpen => deviceOpen;

		/// <summary>The device's format ("48000 Hz, 2 channels, float") or why there is none.</summary>
		public string Status => status;

		/// <summary>Fraction of real time spent rendering (smoothed).</summary>
		public float CpuLoad => cpuLoad;

		/// <summary>Blocks the device drained before they were written (audible gaps).</summary>
		public int Underruns => underruns;

		/// <summary>Frames rendered since start (the audio clock).</summary>
		public long RenderedFrames => Interlocked.Read(ref renderedFrames);

		#endregion

		#region PrivateFields

		private readonly IAudioRenderer renderer;
		private readonly AudioOutputOptions options;
		private readonly float[] block = new float[MaxBlockFrames * Channels];
		private Thread thread;
		private volatile bool stopRequested;
		private volatile bool deviceOpen;
		private volatile string status = "not started";
		private volatile float cpuLoad;
		private volatile int underruns;
		private long renderedFrames;
		private string lastError;
		private int errorCount;

		#endregion

		#region Constructors

		public AudioOutput(IAudioRenderer renderer, AudioOutputOptions options = null)
		{
			this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
			this.options = options ?? new AudioOutputOptions();
		}

		#endregion

		#region PublicMethods

		public void Start()
		{
			if (thread != null)
			{
				return;
			}

			stopRequested = false;
			thread = new Thread(Run) { Name = "Audio output", IsBackground = true, Priority = ThreadPriority.Highest };
			thread.Start();
		}

		public void Stop()
		{
			if (thread == null)
			{
				return;
			}

			stopRequested = true;
			if (!thread.Join(2000))
			{
				Debug.LogWarning("[Audio] The audio thread did not stop in time");
			}

			thread = null;
		}

		public void Dispose()
		{
			Stop();
		}

		#endregion

		#region PrivateMethods

		private void Run()
		{
			var clock = Stopwatch.StartNew();
			WasapiDevice device = null;
			double nextDeviceAttempt = 0.0;
			double nextDeviceCheck = 0.0;
			long nullFramesRendered = 0;
			double nullStart = 0.0;
			bool windows = OperatingSystem.IsWindows() && !options.Disabled;
			if (windows)
			{
				WasapiDevice.InitializeThread();
			}

			try
			{
				while (!stopRequested)
				{
					double now = clock.Elapsed.TotalSeconds;
					if (windows && device == null && now >= nextDeviceAttempt)
					{
						device = WasapiDevice.TryOpen(options.BufferMilliseconds, out string message);
						status = message;
						deviceOpen = device != null;
						nextDeviceAttempt = now + (device != null ? 0.0 : 2.0);
						nextDeviceCheck = now + 1.0;
						if (device == null)
						{
							// Silent pacing starts from now.
							nullStart = now;
							nullFramesRendered = 0;
						}
					}

					if (device != null)
					{
						if (now >= nextDeviceCheck)
						{
							nextDeviceCheck = now + 1.0;
							if (device.DefaultDeviceChanged())
							{
								status = "switching to the new default device";
								device.Dispose();
								device = null;
								deviceOpen = false;
								nextDeviceAttempt = now + 0.2;
								continue;
							}
						}

						if (!device.Pump(this))
						{
							status = "device lost (" + device.LastError + "); reopening";
							device.Dispose();
							device = null;
							deviceOpen = false;
							nextDeviceAttempt = now + 0.5;
							nullStart = now;
							nullFramesRendered = 0;
						}
					}
					else
					{
						// No device: render at real-time pace and drop the result.
						long owed = (long)((now - nullStart) * SampleRate) - nullFramesRendered;
						while (owed > 0 && !stopRequested)
						{
							int frames = (int)Math.Min(owed, 1024);
							RenderBlock(frames);
							nullFramesRendered += frames;
							owed -= frames;
						}

						Thread.Sleep(5);
					}
				}
			}
			catch (Exception e)
			{
				status = "audio thread failed: " + e.Message;
				Debug.LogException(e);
			}
			finally
			{
				device?.Dispose();
				deviceOpen = false;
				if (windows)
				{
					WasapiDevice.UninitializeThread();
				}
			}
		}

		/// <summary>Renders one block into <see cref="block" /> (silence when the renderer throws) and accounts for it.</summary>
		private void RenderBlock(int frames)
		{
			frames = Math.Clamp(frames, 0, MaxBlockFrames);
			long start = Stopwatch.GetTimestamp();
			try
			{
				renderer.Render(block, frames);
			}
			catch (Exception e)
			{
				Array.Clear(block, 0, frames * Channels);
				if (e.Message != lastError || errorCount < 3)
				{
					lastError = e.Message;
					errorCount++;
					Debug.LogException(e);
				}
			}

			Interlocked.Add(ref renderedFrames, frames);
			double seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
			double available = frames / (double)SampleRate;
			float load = available > 0 ? (float)(seconds / available) : 0f;
			cpuLoad = cpuLoad * 0.9f + load * 0.1f;
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     One open WASAPI render stream on the default device. Shared mode with automatic format conversion
		///     (Windows 10+): the stream takes stereo float at the renderer's rate directly; when the device refuses that,
		///     the mix format is used and the output converts (channel layout, sample type and rate) itself.
		/// </summary>
		private sealed class WasapiDevice : IDisposable
		{
			#region PublicFields

			public string LastError = "";

			#endregion

			#region PrivateFields

			private const int ShareModeShared = 0;
			private const int StreamFlagsEventCallback = 0x00040000;
			private const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
			private const int StreamFlagsSrcDefaultQuality = 0x08000000;
			private const int BufferFlagsSilent = 2;
			private const int DataFlowRender = 0;
			private const int RoleConsole = 0;
			private const int ClsCtxAll = 23;
			private const uint WaitTimeout = 0x102;
			private static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
			private static readonly Guid IidAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
			private static readonly Guid SubFormatPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
			private static readonly Guid SubFormatFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

			private IMMDeviceEnumerator enumerator;
			private IMMDevice device;
			private IAudioClient client;
			private IAudioRenderClient render;
			private IntPtr eventHandle;
			private IntPtr mmcssHandle;
			private string deviceId;
			private uint bufferFrames;

			// The stream's format (the renderer's when the device converts, else the mix format).
			private int streamRate;
			private int streamChannels;
			private bool streamFloat;
			private int streamBits;
			private bool needsConversion;

			// Conversion to a mix format the device would not convert itself.
			private float[] converted;
			private float[] resampleSource;
			private int resampleSourceFrames;
			private double resamplePosition;
			private byte[] pcmBytes;

			#endregion

			#region PublicMethods

			public static void InitializeThread()
			{
				try
				{
					// The thread does its own COM (multithreaded apartment); S_FALSE / RPC_E_CHANGED_MODE are harmless.
					CoInitializeEx(IntPtr.Zero, 0);
				}
				catch (Exception)
				{
					// Not available: the runtime initializes COM on first use.
				}
			}

			public static void UninitializeThread()
			{
				try
				{
					CoUninitialize();
				}
				catch (Exception)
				{
					// Ignore.
				}
			}

			/// <summary>Opens the default render device at the renderer's format; null (with the reason) when there is none.</summary>
			public static WasapiDevice TryOpen(int bufferMilliseconds, out string message)
			{
				var result = new WasapiDevice();
				try
				{
					if (result.Open(bufferMilliseconds, out message))
					{
						return result;
					}
				}
				catch (Exception e)
				{
					message = "no audio device (" + e.Message + ")";
				}

				result.Dispose();
				return null;
			}

			/// <summary>True when the default render device is no longer the one this stream plays on.</summary>
			public bool DefaultDeviceChanged()
			{
				try
				{
					IMMDevice current;
					if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out current) != 0 || current == null)
					{
						return true;
					}

					string id;
					int hr = current.GetId(out id);
					Marshal.ReleaseComObject(current);
					return hr != 0 || !string.Equals(id, deviceId, StringComparison.Ordinal);
				}
				catch (Exception)
				{
					return true;
				}
			}

			/// <summary>Waits for the device to want data and writes one block; false when the device failed.</summary>
			public bool Pump(AudioOutput output)
			{
				uint wait = WaitForSingleObject(eventHandle, 500);
				if (wait == WaitTimeout)
				{
					// A healthy stream signals every period: check it still exists.
					uint probe;
					int probeResult = client.GetCurrentPadding(out probe);
					if (probeResult != 0)
					{
						LastError = Describe(probeResult);
						return false;
					}

					return true;
				}

				uint padding;
				int hr = client.GetCurrentPadding(out padding);
				if (hr != 0)
				{
					LastError = Describe(hr);
					return false;
				}

				if (padding == 0)
				{
					output.underruns++;
				}

				int frames = (int)(bufferFrames - padding);
				if (frames <= 0)
				{
					return true;
				}

				IntPtr data;
				hr = render.GetBuffer((uint)frames, out data);
				if (hr != 0)
				{
					LastError = Describe(hr);
					return false;
				}

				try
				{
					Fill(output, data, frames);
				}
				finally
				{
					hr = render.ReleaseBuffer((uint)frames, 0);
				}

				if (hr != 0)
				{
					LastError = Describe(hr);
					return false;
				}

				return true;
			}

			public void Dispose()
			{
				try
				{
					client?.Stop();
				}
				catch (Exception)
				{
					// Already gone.
				}

				Release(ref render);
				Release(ref client);
				Release(ref device);
				Release(ref enumerator);
				if (eventHandle != IntPtr.Zero)
				{
					CloseHandle(eventHandle);
					eventHandle = IntPtr.Zero;
				}

				if (mmcssHandle != IntPtr.Zero)
				{
					try
					{
						AvRevertMmThreadCharacteristics(mmcssHandle);
					}
					catch (Exception)
					{
						// Ignore.
					}

					mmcssHandle = IntPtr.Zero;
				}
			}

			#endregion

			#region PrivateMethods

			private bool Open(int bufferMilliseconds, out string message)
			{
				enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
				int hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out device);
				if (hr != 0 || device == null)
				{
					message = "no audio output device (" + Describe(hr) + ")";
					return false;
				}

				device.GetId(out deviceId);
				object activated;
				Guid iid = IidAudioClient;
				hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated);
				if (hr != 0 || activated == null)
				{
					message = "cannot activate the audio device (" + Describe(hr) + ")";
					return false;
				}

				client = (IAudioClient)activated;
				long duration = Math.Clamp(bufferMilliseconds, 10, 500) * 10000L;

				// The renderer's format, converted by the engine of the shared mode mixer.
				IntPtr format = AllocateFormat(SampleRate, Channels, 32, true);
				try
				{
					hr = client.Initialize(ShareModeShared, StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality, duration, 0, format, IntPtr.Zero);
				}
				finally
				{
					Marshal.FreeHGlobal(format);
				}

				if (hr == 0)
				{
					streamRate = SampleRate;
					streamChannels = Channels;
					streamFloat = true;
					streamBits = 32;
					needsConversion = false;
				}
				else
				{
					// The device will not convert: use its mix format and convert here.
					Release(ref client);
					iid = IidAudioClient;
					hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated);
					if (hr != 0 || activated == null)
					{
						message = "cannot activate the audio device (" + Describe(hr) + ")";
						return false;
					}

					client = (IAudioClient)activated;
					IntPtr mix;
					hr = client.GetMixFormat(out mix);
					if (hr != 0 || mix == IntPtr.Zero)
					{
						message = "cannot read the device's format (" + Describe(hr) + ")";
						return false;
					}

					try
					{
						ReadFormat(mix, out streamRate, out streamChannels, out streamBits, out streamFloat);
						hr = client.Initialize(ShareModeShared, StreamFlagsEventCallback, duration, 0, mix, IntPtr.Zero);
					}
					finally
					{
						Marshal.FreeCoTaskMem(mix);
					}

					if (hr != 0)
					{
						message = "cannot open the audio device in its own format (" + Describe(hr) + ")";
						return false;
					}

					if (!streamFloat && streamBits != 16 && streamBits != 32 && streamBits != 24)
					{
						message = "unsupported device sample format (" + streamBits + " bits)";
						return false;
					}

					needsConversion = true;
				}

				hr = client.GetBufferSize(out bufferFrames);
				if (hr != 0 || bufferFrames == 0)
				{
					message = "cannot read the device's buffer size (" + Describe(hr) + ")";
					return false;
				}

				eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
				if (eventHandle == IntPtr.Zero)
				{
					message = "cannot create the audio event";
					return false;
				}

				hr = client.SetEventHandle(eventHandle);
				if (hr != 0)
				{
					message = "cannot set the audio event (" + Describe(hr) + ")";
					return false;
				}

				object service;
				iid = IidAudioRenderClient;
				hr = client.GetService(ref iid, out service);
				if (hr != 0 || service == null)
				{
					message = "cannot get the render client (" + Describe(hr) + ")";
					return false;
				}

				render = (IAudioRenderClient)service;

				// A buffer of silence first: the stream starts without a gap.
				IntPtr data;
				if (render.GetBuffer(bufferFrames, out data) == 0)
				{
					render.ReleaseBuffer(bufferFrames, BufferFlagsSilent);
				}

				try
				{
					uint taskIndex = 0;
					mmcssHandle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
				}
				catch (Exception)
				{
					mmcssHandle = IntPtr.Zero;
				}

				hr = client.Start();
				if (hr != 0)
				{
					message = "cannot start the audio stream (" + Describe(hr) + ")";
					return false;
				}

				int periodMs = (int)(bufferFrames * 1000L / Math.Max(1, streamRate));
				message = $"{streamRate} Hz, {streamChannels} channels, {(streamFloat ? "float" : streamBits + "-bit")}, {bufferFrames} frames ({periodMs} ms) buffer" + (needsConversion ? ", converted by the engine" : "");
				return true;
			}

			/// <summary>Renders and writes <paramref name="frames" /> device frames to <paramref name="data" />.</summary>
			private void Fill(AudioOutput output, IntPtr data, int frames)
			{
				if (!needsConversion)
				{
					int done = 0;
					while (done < frames)
					{
						int count = Math.Min(frames - done, MaxBlockFrames);
						output.RenderBlock(count);
						Marshal.Copy(output.block, 0, data + done * Channels * sizeof(float), count * Channels);
						done += count;
					}

					return;
				}

				// Rate: how many renderer frames these device frames need (linear resampling, continuous position).
				int needed = (int)Math.Ceiling(frames * (double)SampleRate / streamRate) + 2;
				if (resampleSource == null || resampleSource.Length < (needed + 2) * Channels)
				{
					resampleSource = new float[(needed + 2) * Channels + 1024];
				}

				if (converted == null || converted.Length < frames * streamChannels)
				{
					converted = new float[frames * streamChannels];
				}

				// Keep the last two source frames for interpolation continuity.
				int carry = Math.Min(resampleSourceFrames, 2);
				if (carry > 0 && resampleSourceFrames > carry)
				{
					Array.Copy(resampleSource, (resampleSourceFrames - carry) * Channels, resampleSource, 0, carry * Channels);
					resamplePosition -= resampleSourceFrames - carry;
				}

				resampleSourceFrames = carry;
				while (resampleSourceFrames < needed + carry)
				{
					int count = Math.Min(needed + carry - resampleSourceFrames, MaxBlockFrames);
					output.RenderBlock(count);
					Array.Copy(output.block, 0, resampleSource, resampleSourceFrames * Channels, count * Channels);
					resampleSourceFrames += count;
				}

				double step = SampleRate / (double)streamRate;
				if (streamRate == SampleRate)
				{
					step = 1.0;
				}

				for (int frame = 0; frame < frames; frame++)
				{
					int index = (int)resamplePosition;
					float t = (float)(resamplePosition - index);
					int next = Math.Min(index + 1, resampleSourceFrames - 1);
					index = Math.Min(index, resampleSourceFrames - 1);
					float left = resampleSource[index * Channels] * (1f - t) + resampleSource[next * Channels] * t;
					float right = resampleSource[index * Channels + 1] * (1f - t) + resampleSource[next * Channels + 1] * t;
					int o = frame * streamChannels;
					if (streamChannels == 1)
					{
						converted[o] = (left + right) * 0.5f;
					}
					else
					{
						converted[o] = left;
						converted[o + 1] = right;
						for (int c = 2; c < streamChannels; c++)
						{
							converted[o + c] = 0f;
						}
					}

					resamplePosition += step;
				}

				int values = frames * streamChannels;
				if (streamFloat)
				{
					Marshal.Copy(converted, 0, data, values);
					return;
				}

				int bytesPerValue = streamBits / 8;
				if (pcmBytes == null || pcmBytes.Length < values * bytesPerValue)
				{
					pcmBytes = new byte[values * bytesPerValue];
				}

				for (int i = 0; i < values; i++)
				{
					float v = Math.Clamp(converted[i], -1f, 1f);
					if (streamBits == 16)
					{
						short s = (short)Math.Round(v * 32767f);
						pcmBytes[i * 2] = (byte)s;
						pcmBytes[i * 2 + 1] = (byte)(s >> 8);
					}
					else if (streamBits == 24)
					{
						int s = (int)Math.Round(v * 8388607f);
						pcmBytes[i * 3] = (byte)s;
						pcmBytes[i * 3 + 1] = (byte)(s >> 8);
						pcmBytes[i * 3 + 2] = (byte)(s >> 16);
					}
					else
					{
						int s = (int)Math.Round(v * 2147483647.0);
						pcmBytes[i * 4] = (byte)s;
						pcmBytes[i * 4 + 1] = (byte)(s >> 8);
						pcmBytes[i * 4 + 2] = (byte)(s >> 16);
						pcmBytes[i * 4 + 3] = (byte)(s >> 24);
					}
				}

				Marshal.Copy(pcmBytes, 0, data, values * bytesPerValue);
			}

			/// <summary>A WAVEFORMATEXTENSIBLE in unmanaged memory (freed by the caller).</summary>
			private static IntPtr AllocateFormat(int rate, int channels, int bits, bool isFloat)
			{
				var format = new WaveFormatExtensible
				{
					FormatTag = 0xFFFE,
					Channels = (ushort)channels,
					SamplesPerSec = (uint)rate,
					BitsPerSample = (ushort)bits,
					BlockAlign = (ushort)(channels * bits / 8),
					AvgBytesPerSec = (uint)(rate * channels * bits / 8),
					Size = 22,
					ValidBitsPerSample = (ushort)bits,
					ChannelMask = channels == 1 ? 4u : 3u,
					SubFormat = isFloat ? SubFormatFloat : SubFormatPcm
				};
				IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
				Marshal.StructureToPtr(format, memory, false);
				return memory;
			}

			private static void ReadFormat(IntPtr format, out int rate, out int channels, out int bits, out bool isFloat)
			{
				ushort tag = (ushort)Marshal.ReadInt16(format, 0);
				channels = (ushort)Marshal.ReadInt16(format, 2);
				rate = Marshal.ReadInt32(format, 4);
				bits = (ushort)Marshal.ReadInt16(format, 14);
				ushort size = (ushort)Marshal.ReadInt16(format, 16);
				isFloat = tag == 3;
				if (tag == 0xFFFE && size >= 22)
				{
					var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(format);
					isFloat = extensible.SubFormat == SubFormatFloat;
					if (extensible.ValidBitsPerSample > 0 && extensible.ValidBitsPerSample < bits)
					{
						// Container of more bits than are valid (24 in 32): write the container.
					}
				}
			}

			private static string Describe(int hr)
			{
				switch (unchecked((uint)hr))
				{
					case 0x88890004: return "AUDCLNT_E_DEVICE_INVALIDATED";
					case 0x88890008: return "AUDCLNT_E_UNSUPPORTED_FORMAT";
					case 0x8889000A: return "AUDCLNT_E_DEVICE_IN_USE";
					case 0x88890001: return "AUDCLNT_E_NOT_INITIALIZED";
					case 0x8889000E: return "AUDCLNT_E_ENDPOINT_CREATE_FAILED";
					case 0x8889000F: return "AUDCLNT_E_SERVICE_NOT_RUNNING";
					case 0x88890017: return "AUDCLNT_E_CPUUSAGE_EXCEEDED";
					case 0x80070490: return "no device found";
					default: return "HRESULT 0x" + hr.ToString("X8");
				}
			}

			private static void Release<T>(ref T comObject)
				where T : class
			{
				if (comObject != null)
				{
					try
					{
						Marshal.ReleaseComObject(comObject);
					}
					catch (Exception)
					{
						// Ignore.
					}

					comObject = null;
				}
			}

			[DllImport("ole32.dll")]
			private static extern int CoInitializeEx(IntPtr reserved, int coInit);

			[DllImport("ole32.dll")]
			private static extern void CoUninitialize();

			[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
			private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool CloseHandle(IntPtr handle);

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

			[DllImport("avrt.dll", CharSet = CharSet.Unicode)]
			private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

			[DllImport("avrt.dll")]
			private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

			#endregion

			#region NestedTypes

			[StructLayout(LayoutKind.Sequential, Pack = 2)]
			private struct WaveFormatExtensible
			{
				public ushort FormatTag;
				public ushort Channels;
				public uint SamplesPerSec;
				public uint AvgBytesPerSec;
				public ushort BlockAlign;
				public ushort BitsPerSample;
				public ushort Size;
				public ushort ValidBitsPerSample;
				public uint ChannelMask;
				public Guid SubFormat;
			}

			[ComImport]
			[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
			private class MMDeviceEnumeratorComObject
			{ }

			[ComImport]
			[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
			[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
			private interface IMMDeviceEnumerator
			{
				[PreserveSig]
				int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

				[PreserveSig]
				int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

				[PreserveSig]
				int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

				[PreserveSig]
				int RegisterEndpointNotificationCallback(IntPtr client);

				[PreserveSig]
				int UnregisterEndpointNotificationCallback(IntPtr client);
			}

			[ComImport]
			[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
			[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
			private interface IMMDevice
			{
				[PreserveSig]
				int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);

				[PreserveSig]
				int OpenPropertyStore(int access, out IntPtr properties);

				[PreserveSig]
				int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

				[PreserveSig]
				int GetState(out int state);
			}

			[ComImport]
			[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
			[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
			private interface IAudioClient
			{
				[PreserveSig]
				int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

				[PreserveSig]
				int GetBufferSize(out uint bufferFrames);

				[PreserveSig]
				int GetStreamLatency(out long latency);

				[PreserveSig]
				int GetCurrentPadding(out uint padding);

				[PreserveSig]
				int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

				[PreserveSig]
				int GetMixFormat(out IntPtr format);

				[PreserveSig]
				int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

				[PreserveSig]
				int Start();

				[PreserveSig]
				int Stop();

				[PreserveSig]
				int Reset();

				[PreserveSig]
				int SetEventHandle(IntPtr eventHandle);

				[PreserveSig]
				int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
			}

			[ComImport]
			[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
			[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
			private interface IAudioRenderClient
			{
				[PreserveSig]
				int GetBuffer(uint framesRequested, out IntPtr data);

				[PreserveSig]
				int ReleaseBuffer(uint framesWritten, int flags);
			}

			#endregion
		}

		#endregion
	}

	/// <summary>How <see cref="AudioOutput" /> opens the device.</summary>
	public sealed class AudioOutputOptions
	{
		#region PublicFields

		/// <summary>Length of the device buffer: the latency, and the time a late block may be without a gap.</summary>
		public int BufferMilliseconds = 30;

		/// <summary>No device is opened: the renderer runs silently at real-time pace.</summary>
		public bool Disabled;

		#endregion
	}
}
