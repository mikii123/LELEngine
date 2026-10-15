using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>Growable little-endian byte buffer the binary writers append to.</summary>
	public sealed class ByteWriter
	{
		#region PublicFields

		public int Length => length;

		#endregion

		#region PrivateFields

		private byte[] data;
		private int length;

		#endregion

		#region Constructors

		public ByteWriter(int capacity = 4096)
		{
			data = new byte[Math.Max(16, capacity)];
		}

		#endregion

		#region PublicMethods

		public byte[] ToArray()
		{
			var result = new byte[length];
			Buffer.BlockCopy(data, 0, result, 0, length);
			return result;
		}

		public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(data, 0, length);

		public void Clear()
		{
			length = 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteByte(byte value)
		{
			Ensure(1);
			data[length++] = value;
		}

		public void WriteBool(bool value)
		{
			WriteByte(value ? (byte)1 : (byte)0);
		}

		public void WriteSByte(sbyte value)
		{
			WriteByte((byte)value);
		}

		public void WriteInt16(short value)
		{
			Ensure(2);
			BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(length), value);
			length += 2;
		}

		public void WriteUInt16(ushort value)
		{
			Ensure(2);
			BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(length), value);
			length += 2;
		}

		public void WriteChar(char value)
		{
			WriteUInt16(value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteInt32(int value)
		{
			Ensure(4);
			BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(length), value);
			length += 4;
		}

		public void WriteUInt32(uint value)
		{
			Ensure(4);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(length), value);
			length += 4;
		}

		public void WriteInt64(long value)
		{
			Ensure(8);
			BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(length), value);
			length += 8;
		}

		public void WriteUInt64(ulong value)
		{
			Ensure(8);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(length), value);
			length += 8;
		}

		public void WriteSingle(float value)
		{
			Ensure(4);
			BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(length), value);
			length += 4;
		}

		public void WriteDouble(double value)
		{
			Ensure(8);
			BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(length), value);
			length += 8;
		}

		public void WriteVector2(Vector2 v)
		{
			WriteSingle(v.X);
			WriteSingle(v.Y);
		}

		public void WriteVector3(Vector3 v)
		{
			WriteSingle(v.X);
			WriteSingle(v.Y);
			WriteSingle(v.Z);
		}

		public void WriteVector4(Vector4 v)
		{
			WriteSingle(v.X);
			WriteSingle(v.Y);
			WriteSingle(v.Z);
			WriteSingle(v.W);
		}

		public void WriteQuaternion(Quaternion q)
		{
			WriteSingle(q.X);
			WriteSingle(q.Y);
			WriteSingle(q.Z);
			WriteSingle(q.W);
		}

		public void WriteColor4(Color4 c)
		{
			WriteSingle(c.R);
			WriteSingle(c.G);
			WriteSingle(c.B);
			WriteSingle(c.A);
		}

		public void WriteVector2i(Vector2i v)
		{
			WriteInt32(v.X);
			WriteInt32(v.Y);
		}

		public void WriteVector3i(Vector3i v)
		{
			WriteInt32(v.X);
			WriteInt32(v.Y);
			WriteInt32(v.Z);
		}

		public void WriteVector4i(Vector4i v)
		{
			WriteInt32(v.X);
			WriteInt32(v.Y);
			WriteInt32(v.Z);
			WriteInt32(v.W);
		}

		/// <summary>Length-prefixed UTF-8; null is length -1.</summary>
		public void WriteString(string value)
		{
			if (value == null)
			{
				WriteInt32(-1);
				return;
			}

			int bytes = Encoding.UTF8.GetByteCount(value);
			WriteInt32(bytes);
			Ensure(bytes);
			Encoding.UTF8.GetBytes(value, 0, value.Length, data, length);
			length += bytes;
		}

		public void WriteBytes(ReadOnlySpan<byte> bytes)
		{
			Ensure(bytes.Length);
			bytes.CopyTo(data.AsSpan(length));
			length += bytes.Length;
		}

		/// <summary>Reserves 4 bytes for a length written later with <see cref="PatchInt32" />; returns their position.</summary>
		public int ReserveInt32()
		{
			int position = length;
			WriteInt32(0);
			return position;
		}

		public void PatchInt32(int position, int value)
		{
			BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(position), value);
		}

		#endregion

		#region PrivateMethods

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void Ensure(int bytes)
		{
			if (length + bytes > data.Length)
			{
				Grow(bytes);
			}
		}

		private void Grow(int bytes)
		{
			int size = Math.Max(data.Length * 2, length + bytes);
			Array.Resize(ref data, size);
		}

		#endregion
	}

	/// <summary>Sequential little-endian reader over a byte array.</summary>
	public sealed class ByteReader
	{
		#region PublicFields

		public int Position
		{
			get => position;
			set => position = value;
		}

		public int Length => end;
		public int Remaining => end - position;

		#endregion

		#region PrivateFields

		private readonly byte[] data;
		private readonly int end;
		private int position;

		#endregion

		#region Constructors

		public ByteReader(byte[] data)
			: this(data, 0, data.Length)
		{ }

		public ByteReader(byte[] data, int offset, int count)
		{
			this.data = data;
			position = offset;
			end = offset + count;
		}

		#endregion

		#region PublicMethods

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte ReadByte()
		{
			Check(1);
			return data[position++];
		}

		public bool ReadBool()
		{
			return ReadByte() != 0;
		}

		public sbyte ReadSByte()
		{
			return (sbyte)ReadByte();
		}

		public short ReadInt16()
		{
			Check(2);
			short value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(position));
			position += 2;
			return value;
		}

		public ushort ReadUInt16()
		{
			Check(2);
			ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
			position += 2;
			return value;
		}

		public char ReadChar()
		{
			return (char)ReadUInt16();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadInt32()
		{
			Check(4);
			int value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
			position += 4;
			return value;
		}

		public uint ReadUInt32()
		{
			Check(4);
			uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
			position += 4;
			return value;
		}

		public long ReadInt64()
		{
			Check(8);
			long value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position));
			position += 8;
			return value;
		}

		public ulong ReadUInt64()
		{
			Check(8);
			ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position));
			position += 8;
			return value;
		}

		public float ReadSingle()
		{
			Check(4);
			float value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position));
			position += 4;
			return value;
		}

		public double ReadDouble()
		{
			Check(8);
			double value = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(position));
			position += 8;
			return value;
		}

		public Vector2 ReadVector2()
		{
			return new Vector2(ReadSingle(), ReadSingle());
		}

		public Vector3 ReadVector3()
		{
			return new Vector3(ReadSingle(), ReadSingle(), ReadSingle());
		}

		public Vector4 ReadVector4()
		{
			return new Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		}

		public Quaternion ReadQuaternion()
		{
			return new Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		}

		public Color4 ReadColor4()
		{
			return new Color4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		}

		public Vector2i ReadVector2i()
		{
			return new Vector2i(ReadInt32(), ReadInt32());
		}

		public Vector3i ReadVector3i()
		{
			return new Vector3i(ReadInt32(), ReadInt32(), ReadInt32());
		}

		public Vector4i ReadVector4i()
		{
			return new Vector4i(ReadInt32(), ReadInt32(), ReadInt32(), ReadInt32());
		}

		public string ReadString()
		{
			int bytes = ReadInt32();
			if (bytes < 0)
			{
				return null;
			}

			if (bytes == 0)
			{
				return "";
			}

			Check(bytes);
			string value = Encoding.UTF8.GetString(data, position, bytes);
			position += bytes;
			return value;
		}

		public ReadOnlySpan<byte> ReadBytes(int count)
		{
			Check(count);
			var span = new ReadOnlySpan<byte>(data, position, count);
			position += count;
			return span;
		}

		public void Skip(int count)
		{
			Check(count);
			position += count;
		}

		#endregion

		#region PrivateMethods

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void Check(int bytes)
		{
			if (position + bytes > end)
			{
				throw new InvalidOperationException("Unexpected end of serialized data");
			}
		}

		#endregion
	}

	/// <summary>Raw memory views of arrays for bulk copies.</summary>
	internal static class ArrayBytes
	{
		/// <summary>The elements of a one-dimensional array of unmanaged structs as bytes.</summary>
		public static Span<byte> Of(Array array, int elementSize)
		{
			if (array.Length == 0)
			{
				return Span<byte>.Empty;
			}

			ref byte first = ref Unsafe.As<byte, byte>(ref MemoryMarshal.GetArrayDataReference(array));
			return MemoryMarshal.CreateSpan(ref first, array.Length * elementSize);
		}
	}
}
