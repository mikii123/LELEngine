using System;

namespace LELEngine.Audio
{
	/// <summary>The responses a <see cref="BiquadFilter" /> can take (Robert Bristow-Johnson's audio EQ cookbook).</summary>
	public enum BiquadType
	{
		LowPass,
		HighPass,

		/// <summary>Constant 0 dB peak gain.</summary>
		BandPass,
		Notch,
		LowShelf,
		HighShelf,
		Peaking
	}

	/// <summary>
	///     One second-order IIR section (RBJ cookbook coefficients, transposed direct form II) with state for up to
	///     <c>channels</c> channels, processing interleaved buffers in place. Allocation free after construction;
	///     coefficients and state are plain fields, so one owner thread at a time.
	/// </summary>
	public sealed class BiquadFilter
	{
		#region PublicFields

		/// <summary>True when the filter would pass audio unchanged (not configured, a low pass at the top frequency, a shelf or peak at 0 dB): callers may skip <see cref="Process" />, which does nothing then.</summary>
		public bool IsIdentity => identity;

		public BiquadType Type => type;

		/// <summary>Corner / center frequency in Hz as clamped by <see cref="Configure" />.</summary>
		public float Frequency => frequency;

		public float Q => q;

		/// <summary>Gain in dB of the shelves and the peak; 0 for the other types.</summary>
		public float GainDb => gainDb;

		/// <summary>Channels of state the filter holds (the most <see cref="Process" /> accepts).</summary>
		public int MaxChannels => z1.Length;

		#endregion

		#region PrivateFields

		private const float MinFrequency = 10f;
		private const float MaxFrequencyFraction = 0.49f;
		private const float MinQ = 0.1f;

		/// <summary>States below this are flushed to zero at the end of a block, before they reach the denormal range.</summary>
		private const float FlushThreshold = 1e-15f;

		private readonly float[] z1;
		private readonly float[] z2;
		private float b0 = 1f;
		private float b1;
		private float b2;
		private float a1;
		private float a2;
		private bool identity = true;
		private BiquadType type;
		private float frequency;
		private float q = 0.7071f;
		private float gainDb;

		#endregion

		#region Constructors

		/// <param name="channels">Channels of state (1 or 2 in practice).</param>
		public BiquadFilter(int channels = 2)
		{
			if (channels < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(channels));
			}

			z1 = new float[channels];
			z2 = new float[channels];
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Sets the response: the frequency is clamped to [10 Hz, 0.49 x sample rate], <paramref name="q" /> to at
		///     least 0.1; <paramref name="gainDb" /> counts for the shelves and the peak only. The state is kept, so
		///     changing a running filter does not restart it.
		/// </summary>
		public void Configure(BiquadType type, float frequencyHz, float q, float gainDb, int sampleRate)
		{
			if (sampleRate <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(sampleRate));
			}

			float maxFrequency = sampleRate * MaxFrequencyFraction;
			if (!(frequencyHz >= MinFrequency))
			{
				frequencyHz = MinFrequency;
			}

			if (frequencyHz > maxFrequency)
			{
				frequencyHz = maxFrequency;
			}

			if (!(q >= MinQ))
			{
				q = MinQ;
			}

			if (float.IsNaN(gainDb))
			{
				gainDb = 0f;
			}

			gainDb = Math.Clamp(gainDb, -60f, 60f);
			bool hasGain = type == BiquadType.LowShelf || type == BiquadType.HighShelf || type == BiquadType.Peaking;
			if (!hasGain)
			{
				gainDb = 0f;
			}

			this.type = type;
			frequency = frequencyHz;
			this.q = q;
			this.gainDb = gainDb;
			identity = (type == BiquadType.LowPass && frequencyHz >= maxFrequency) || (hasGain && Math.Abs(gainDb) < 0.01f);

			double w0 = 2.0 * Math.PI * frequencyHz / sampleRate;
			double cosW = Math.Cos(w0);
			double sinW = Math.Sin(w0);
			double alpha = sinW / (2.0 * q);
			double amp = Math.Pow(10.0, gainDb / 40.0);
			double sqrtAlpha = 2.0 * Math.Sqrt(amp) * alpha;
			double cb0;
			double cb1;
			double cb2;
			double ca0;
			double ca1;
			double ca2;
			switch (type)
			{
				case BiquadType.LowPass:
					cb0 = (1.0 - cosW) / 2.0;
					cb1 = 1.0 - cosW;
					cb2 = cb0;
					ca0 = 1.0 + alpha;
					ca1 = -2.0 * cosW;
					ca2 = 1.0 - alpha;
					break;
				case BiquadType.HighPass:
					cb0 = (1.0 + cosW) / 2.0;
					cb1 = -(1.0 + cosW);
					cb2 = cb0;
					ca0 = 1.0 + alpha;
					ca1 = -2.0 * cosW;
					ca2 = 1.0 - alpha;
					break;
				case BiquadType.BandPass:
					cb0 = alpha;
					cb1 = 0.0;
					cb2 = -alpha;
					ca0 = 1.0 + alpha;
					ca1 = -2.0 * cosW;
					ca2 = 1.0 - alpha;
					break;
				case BiquadType.Notch:
					cb0 = 1.0;
					cb1 = -2.0 * cosW;
					cb2 = 1.0;
					ca0 = 1.0 + alpha;
					ca1 = -2.0 * cosW;
					ca2 = 1.0 - alpha;
					break;
				case BiquadType.Peaking:
					cb0 = 1.0 + alpha * amp;
					cb1 = -2.0 * cosW;
					cb2 = 1.0 - alpha * amp;
					ca0 = 1.0 + alpha / amp;
					ca1 = -2.0 * cosW;
					ca2 = 1.0 - alpha / amp;
					break;
				case BiquadType.LowShelf:
					cb0 = amp * ((amp + 1.0) - (amp - 1.0) * cosW + sqrtAlpha);
					cb1 = 2.0 * amp * ((amp - 1.0) - (amp + 1.0) * cosW);
					cb2 = amp * ((amp + 1.0) - (amp - 1.0) * cosW - sqrtAlpha);
					ca0 = (amp + 1.0) + (amp - 1.0) * cosW + sqrtAlpha;
					ca1 = -2.0 * ((amp - 1.0) + (amp + 1.0) * cosW);
					ca2 = (amp + 1.0) + (amp - 1.0) * cosW - sqrtAlpha;
					break;
				case BiquadType.HighShelf:
					cb0 = amp * ((amp + 1.0) + (amp - 1.0) * cosW + sqrtAlpha);
					cb1 = -2.0 * amp * ((amp - 1.0) + (amp + 1.0) * cosW);
					cb2 = amp * ((amp + 1.0) + (amp - 1.0) * cosW - sqrtAlpha);
					ca0 = (amp + 1.0) - (amp - 1.0) * cosW + sqrtAlpha;
					ca1 = 2.0 * ((amp - 1.0) - (amp + 1.0) * cosW);
					ca2 = (amp + 1.0) - (amp - 1.0) * cosW - sqrtAlpha;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(type));
			}

			b0 = (float)(cb0 / ca0);
			b1 = (float)(cb1 / ca0);
			b2 = (float)(cb2 / ca0);
			a1 = (float)(ca1 / ca0);
			a2 = (float)(ca2 / ca0);
		}

		/// <summary>Filters <paramref name="frames" /> frames of <paramref name="channels" /> (1 or 2, at most <see cref="MaxChannels" />) interleaved samples in place from float index <paramref name="offset" />.</summary>
		public void Process(float[] buffer, int offset, int frames, int channels)
		{
			if (channels < 1 || channels > z1.Length)
			{
				throw new ArgumentOutOfRangeException(nameof(channels));
			}

			if (identity || frames <= 0)
			{
				return;
			}

			int end = offset + frames * channels;
			if (end > buffer.Length)
			{
				throw new ArgumentException("The buffer is too small for " + frames + " frames", nameof(buffer));
			}

			if (channels == 2)
			{
				ProcessStereo(buffer, offset, end);
			}
			else
			{
				for (int c = 0; c < channels; c++)
				{
					ProcessChannel(buffer, offset + c, end, channels, c);
				}
			}
		}

		/// <summary>Clears the state (the coefficients stay).</summary>
		public void Reset()
		{
			Array.Clear(z1, 0, z1.Length);
			Array.Clear(z2, 0, z2.Length);
		}

		#endregion

		#region PrivateMethods

		private void ProcessChannel(float[] buffer, int start, int end, int stride, int channel)
		{
			float s1 = z1[channel];
			float s2 = z2[channel];
			float cb0 = b0;
			float cb1 = b1;
			float cb2 = b2;
			float ca1 = a1;
			float ca2 = a2;
			for (int i = start; i < end; i += stride)
			{
				float x = buffer[i];
				float y = cb0 * x + s1;
				s1 = cb1 * x - ca1 * y + s2;
				s2 = cb2 * x - ca2 * y;
				buffer[i] = y;
			}

			z1[channel] = Flush(s1);
			z2[channel] = Flush(s2);
		}

		private void ProcessStereo(float[] buffer, int start, int end)
		{
			float l1 = z1[0];
			float l2 = z2[0];
			float r1 = z1[1];
			float r2 = z2[1];
			float cb0 = b0;
			float cb1 = b1;
			float cb2 = b2;
			float ca1 = a1;
			float ca2 = a2;
			for (int i = start; i < end; i += 2)
			{
				float xl = buffer[i];
				float xr = buffer[i + 1];
				float yl = cb0 * xl + l1;
				float yr = cb0 * xr + r1;
				l1 = cb1 * xl - ca1 * yl + l2;
				r1 = cb1 * xr - ca1 * yr + r2;
				l2 = cb2 * xl - ca2 * yl;
				r2 = cb2 * xr - ca2 * yr;
				buffer[i] = yl;
				buffer[i + 1] = yr;
			}

			z1[0] = Flush(l1);
			z2[0] = Flush(l2);
			z1[1] = Flush(r1);
			z2[1] = Flush(r2);
		}

		/// <summary>Zero for a state that decayed below hearing (keeps denormals out of the next block) or went NaN.</summary>
		private static float Flush(float state)
		{
			return float.IsNaN(state) || Math.Abs(state) < FlushThreshold ? 0f : state;
		}

		#endregion
	}

	/// <summary>
	///     Freeverb (Jezar at Dreampoint): eight parallel damped comb filters into four allpasses per channel, the
	///     right channel's lines 23 samples longer, tuned for 44.1 kHz and scaled to the sample rate. Stereo
	///     interleaved, in place, allocation free after construction. 0 dB wet is Freeverb's default wet level.
	/// </summary>
	public sealed class ReverbProcessor
	{
		#region PrivateFields

		private const int CombCount = 8;
		private const int AllpassCount = 4;
		private const int StereoSpread = 23;
		private const float FixedGain = 0.015f;
		private const float ScaleRoom = 0.28f;
		private const float OffsetRoom = 0.7f;
		private const float ScaleDamp = 0.4f;
		private const float AllpassFeedback = 0.5f;
		private const float Denormal = 1e-18f;
		private const float SilenceDb = -80f;

		private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
		private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };

		private readonly float[][] combL = new float[CombCount][];
		private readonly float[][] combR = new float[CombCount][];
		private readonly float[][] allpassL = new float[AllpassCount][];
		private readonly float[][] allpassR = new float[AllpassCount][];
		private readonly int[] combIndexL = new int[CombCount];
		private readonly int[] combIndexR = new int[CombCount];
		private readonly int[] allpassIndexL = new int[AllpassCount];
		private readonly int[] allpassIndexR = new int[AllpassCount];
		private readonly float[] combStoreL = new float[CombCount];
		private readonly float[] combStoreR = new float[CombCount];
		private float feedback;
		private float damp1;
		private float damp2;
		private float wet1;
		private float wet2;
		private float dry;

		#endregion

		#region Constructors

		public ReverbProcessor(int sampleRate)
		{
			if (sampleRate <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(sampleRate));
			}

			double scale = sampleRate / 44100.0;
			for (int i = 0; i < CombCount; i++)
			{
				combL[i] = new float[Math.Max(1, (int)Math.Round(CombTuning[i] * scale))];
				combR[i] = new float[Math.Max(1, (int)Math.Round((CombTuning[i] + StereoSpread) * scale))];
			}

			for (int i = 0; i < AllpassCount; i++)
			{
				allpassL[i] = new float[Math.Max(1, (int)Math.Round(AllpassTuning[i] * scale))];
				allpassR[i] = new float[Math.Max(1, (int)Math.Round((AllpassTuning[i] + StereoSpread) * scale))];
			}

			SetParameters(0.5f, 0.5f, 1f, 0f, 0f);
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     <paramref name="roomSize" />, <paramref name="damping" /> and <paramref name="width" /> in 0..1, wet and dry
		///     levels in dB (at or below -80 dB silent). Only gains and feedback change, so a running reverb takes new
		///     values without a click.
		/// </summary>
		public void SetParameters(float roomSize, float damping, float width, float wetDb, float dryDb)
		{
			roomSize = Clamp01(roomSize);
			damping = Clamp01(damping);
			width = Clamp01(width);
			feedback = roomSize * ScaleRoom + OffsetRoom;
			damp1 = damping * ScaleDamp;
			damp2 = 1f - damp1;
			float wet = DbToGain(wetDb);
			wet1 = wet * (width / 2f + 0.5f);
			wet2 = wet * ((1f - width) / 2f);
			dry = DbToGain(dryDb);
		}

		/// <summary>Processes <paramref name="frames" /> stereo frames in place from float index <paramref name="offset" />.</summary>
		public void Process(float[] buffer, int offset, int frames)
		{
			int end = offset + frames * 2;
			if (end > buffer.Length)
			{
				throw new ArgumentException("The buffer is too small for " + frames + " stereo frames", nameof(buffer));
			}

			for (int i = offset; i < end; i += 2)
			{
				float inL = buffer[i];
				float inR = buffer[i + 1];
				float input = (inL + inR) * FixedGain;
				float outL = 0f;
				float outR = 0f;
				for (int c = 0; c < CombCount; c++)
				{
					float[] line = combL[c];
					int index = combIndexL[c];
					float output = line[index];
					if (Math.Abs(output) < Denormal)
					{
						output = 0f;
					}

					float store = output * damp2 + combStoreL[c] * damp1;
					if (Math.Abs(store) < Denormal)
					{
						store = 0f;
					}

					combStoreL[c] = store;
					line[index] = input + store * feedback;
					combIndexL[c] = index + 1 < line.Length ? index + 1 : 0;
					outL += output;

					line = combR[c];
					index = combIndexR[c];
					output = line[index];
					if (Math.Abs(output) < Denormal)
					{
						output = 0f;
					}

					store = output * damp2 + combStoreR[c] * damp1;
					if (Math.Abs(store) < Denormal)
					{
						store = 0f;
					}

					combStoreR[c] = store;
					line[index] = input + store * feedback;
					combIndexR[c] = index + 1 < line.Length ? index + 1 : 0;
					outR += output;
				}

				for (int a = 0; a < AllpassCount; a++)
				{
					float[] line = allpassL[a];
					int index = allpassIndexL[a];
					float delayed = line[index];
					if (Math.Abs(delayed) < Denormal)
					{
						delayed = 0f;
					}

					line[index] = outL + delayed * AllpassFeedback;
					outL = delayed - outL;
					allpassIndexL[a] = index + 1 < line.Length ? index + 1 : 0;

					line = allpassR[a];
					index = allpassIndexR[a];
					delayed = line[index];
					if (Math.Abs(delayed) < Denormal)
					{
						delayed = 0f;
					}

					line[index] = outR + delayed * AllpassFeedback;
					outR = delayed - outR;
					allpassIndexR[a] = index + 1 < line.Length ? index + 1 : 0;
				}

				buffer[i] = outL * wet1 + outR * wet2 + inL * dry;
				buffer[i + 1] = outR * wet1 + outL * wet2 + inR * dry;
			}

			// A NaN that got into the lines would ring forever: it reaches a comb's store within one lap.
			if (float.IsNaN(combStoreL[0]) || float.IsNaN(combStoreR[0]))
			{
				Reset();
			}
		}

		/// <summary>Clears every delay line (the parameters stay).</summary>
		public void Reset()
		{
			for (int i = 0; i < CombCount; i++)
			{
				Array.Clear(combL[i], 0, combL[i].Length);
				Array.Clear(combR[i], 0, combR[i].Length);
				combIndexL[i] = 0;
				combIndexR[i] = 0;
				combStoreL[i] = 0f;
				combStoreR[i] = 0f;
			}

			for (int i = 0; i < AllpassCount; i++)
			{
				Array.Clear(allpassL[i], 0, allpassL[i].Length);
				Array.Clear(allpassR[i], 0, allpassR[i].Length);
				allpassIndexL[i] = 0;
				allpassIndexR[i] = 0;
			}
		}

		#endregion

		#region PrivateMethods

		private static float Clamp01(float value)
		{
			return !(value > 0f) ? 0f : value > 1f ? 1f : value;
		}

		private static float DbToGain(float db)
		{
			return float.IsNaN(db) || db <= SilenceDb ? 0f : (float)Math.Pow(10.0, db / 20.0);
		}

		#endregion
	}

	/// <summary>
	///     Stereo feedback delay (echo): each channel feeds its own line back through a one-pole low pass. Stereo
	///     interleaved, in place, allocation free after construction. A delay time change moves the read point at
	///     once (a short discontinuity); gains and feedback change without a click.
	/// </summary>
	public sealed class DelayProcessor
	{
		#region PublicFields

		/// <summary>The longest delay the lines hold, in seconds.</summary>
		public float MaxDelaySeconds => (lineL.Length - 1) / (float)sampleRate;

		#endregion

		#region PrivateFields

		private const float MaxFeedback = 0.98f;
		private const float Denormal = 1e-18f;
		private const float SilenceDb = -80f;

		private readonly int sampleRate;
		private readonly float[] lineL;
		private readonly float[] lineR;
		private int writeIndex;
		private int delaySamples = 1;
		private float feedback;
		private float wet;
		private float dry;
		private float lowPassCoefficient = 1f;
		private float lowPassL;
		private float lowPassR;

		#endregion

		#region Constructors

		public DelayProcessor(int sampleRate, float maxDelaySeconds = 5f)
		{
			if (sampleRate <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(sampleRate));
			}

			if (!(maxDelaySeconds > 0f))
			{
				throw new ArgumentOutOfRangeException(nameof(maxDelaySeconds));
			}

			this.sampleRate = sampleRate;
			int maxSamples = Math.Max(1, (int)Math.Ceiling((double)maxDelaySeconds * sampleRate));
			lineL = new float[maxSamples + 1];
			lineR = new float[maxSamples + 1];
			SetParameters(0.3f, 0.3f, -6f, 0f, 20000f);
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     <paramref name="delaySeconds" /> clamped to one sample .. the maximum, <paramref name="feedback" /> to
		///     [0, 0.98], wet and dry levels in dB (at or below -80 dB silent), <paramref name="feedbackLowPassHz" /> the
		///     cutoff of the feedback path (at or above 0.49 x sample rate: no filtering).
		/// </summary>
		public void SetParameters(float delaySeconds, float feedback, float wetDb, float dryDb, float feedbackLowPassHz)
		{
			int maxSamples = lineL.Length - 1;
			long samples = float.IsNaN(delaySeconds) ? 1 : (long)Math.Round((double)delaySeconds * sampleRate);
			delaySamples = (int)Math.Clamp(samples, 1, maxSamples);
			this.feedback = !(feedback > 0f) ? 0f : feedback > MaxFeedback ? MaxFeedback : feedback;
			wet = DbToGain(wetDb);
			dry = DbToGain(dryDb);
			if (float.IsNaN(feedbackLowPassHz) || feedbackLowPassHz >= sampleRate * 0.49f)
			{
				lowPassCoefficient = 1f;
			}
			else
			{
				float cutoff = Math.Max(10f, feedbackLowPassHz);
				lowPassCoefficient = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / sampleRate);
			}
		}

		/// <summary>Processes <paramref name="frames" /> stereo frames in place from float index <paramref name="offset" />.</summary>
		public void Process(float[] buffer, int offset, int frames)
		{
			int end = offset + frames * 2;
			if (end > buffer.Length)
			{
				throw new ArgumentException("The buffer is too small for " + frames + " stereo frames", nameof(buffer));
			}

			int size = lineL.Length;
			int write = writeIndex;
			int read = write - delaySamples;
			if (read < 0)
			{
				read += size;
			}

			float lpL = lowPassL;
			float lpR = lowPassR;
			float coefficient = lowPassCoefficient;
			float fb = feedback;
			float wetGain = wet;
			float dryGain = dry;
			for (int i = offset; i < end; i += 2)
			{
				float inL = buffer[i];
				float inR = buffer[i + 1];
				float delayedL = lineL[read];
				float delayedR = lineR[read];
				if (Math.Abs(delayedL) < Denormal)
				{
					delayedL = 0f;
				}

				if (Math.Abs(delayedR) < Denormal)
				{
					delayedR = 0f;
				}

				lpL += coefficient * (delayedL - lpL);
				lpR += coefficient * (delayedR - lpR);
				lineL[write] = inL + lpL * fb;
				lineR[write] = inR + lpR * fb;
				buffer[i] = inL * dryGain + delayedL * wetGain;
				buffer[i + 1] = inR * dryGain + delayedR * wetGain;
				if (++write >= size)
				{
					write = 0;
				}

				if (++read >= size)
				{
					read = 0;
				}
			}

			writeIndex = write;
			if (float.IsNaN(lpL) || float.IsNaN(lpR))
			{
				Reset();
				return;
			}

			lowPassL = Math.Abs(lpL) < Denormal ? 0f : lpL;
			lowPassR = Math.Abs(lpR) < Denormal ? 0f : lpR;
		}

		/// <summary>Clears the lines and the filter state (the parameters stay).</summary>
		public void Reset()
		{
			Array.Clear(lineL, 0, lineL.Length);
			Array.Clear(lineR, 0, lineR.Length);
			writeIndex = 0;
			lowPassL = 0f;
			lowPassR = 0f;
		}

		#endregion

		#region PrivateMethods

		private static float DbToGain(float db)
		{
			return float.IsNaN(db) || db <= SilenceDb ? 0f : (float)Math.Pow(10.0, db / 20.0);
		}

		#endregion
	}

	/// <summary>Peak and RMS of a block, for meters.</summary>
	public static class AudioAnalysis
	{
		#region PublicMethods

		/// <summary>The largest absolute sample of each channel in the block (mono: both the same; more than two channels: the first two).</summary>
		public static void Peaks(float[] buffer, int offset, int frames, int channels, out float peakLeft, out float peakRight)
		{
			peakLeft = 0f;
			peakRight = 0f;
			if (buffer == null || frames <= 0 || channels < 1)
			{
				return;
			}

			int end = offset + frames * channels;
			if (channels == 1)
			{
				for (int i = offset; i < end; i++)
				{
					float v = Math.Abs(buffer[i]);
					if (v > peakLeft)
					{
						peakLeft = v;
					}
				}

				peakRight = peakLeft;
				return;
			}

			for (int i = offset; i < end; i += channels)
			{
				float l = Math.Abs(buffer[i]);
				float r = Math.Abs(buffer[i + 1]);
				if (l > peakLeft)
				{
					peakLeft = l;
				}

				if (r > peakRight)
				{
					peakRight = r;
				}
			}
		}

		/// <summary>The root mean square of each channel in the block (mono: both the same; more than two channels: the first two).</summary>
		public static void Rms(float[] buffer, int offset, int frames, int channels, out float rmsLeft, out float rmsRight)
		{
			rmsLeft = 0f;
			rmsRight = 0f;
			if (buffer == null || frames <= 0 || channels < 1)
			{
				return;
			}

			int end = offset + frames * channels;
			double sumL = 0.0;
			double sumR = 0.0;
			if (channels == 1)
			{
				for (int i = offset; i < end; i++)
				{
					sumL += (double)buffer[i] * buffer[i];
				}

				rmsLeft = (float)Math.Sqrt(sumL / frames);
				rmsRight = rmsLeft;
				return;
			}

			for (int i = offset; i < end; i += channels)
			{
				sumL += (double)buffer[i] * buffer[i];
				sumR += (double)buffer[i + 1] * buffer[i + 1];
			}

			rmsLeft = (float)Math.Sqrt(sumL / frames);
			rmsRight = (float)Math.Sqrt(sumR / frames);
		}

		#endregion
	}
}
