﻿using ProtoBuf;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Sequences;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace AggressiveNamespace
{
    static class SimpleCache<T> where T : class, IDisposable, new()
    {
        static T _instance;
        public static T Get() => Interlocked.Exchange(ref _instance, null) ?? new T();
        public static void Recycle(T obj)
        {
            if (obj != null)
            {
                obj.Dispose();
                Interlocked.Exchange(ref _instance, obj);
            }
        }
    }
    class MoreDataNeededException : EndOfStreamException
    {
        public long MoreBytesNeeded { get; }
        public MoreDataNeededException(long moreBytesNeeded) { MoreBytesNeeded = moreBytesNeeded; }
        public MoreDataNeededException(string message, long moreBytesNeeded) : base(message) { MoreBytesNeeded = moreBytesNeeded; }
    }
    public class DeserializationContext : IDisposable
    {
        void IDisposable.Dispose()
        {
            _previous = null;
            _resume.Clear();
            AwaitCount = 0;
            _basePosition = default;
            ContinueFrom = 0;
            _pushLock = 0;
        }
        ResumeFrame _previous;
        public long ContinueFrom { get; private set; }
        public (int FieldNumber, WireType WireType) ReadNextField(ref BufferReader<ReadOnlyBuffer> reader)
        {
            // do we have a value to pop?
            var frame = _previous;
            if (frame != null)
            {
                return frame.FieldHeader();
            }

            // store our known-safe recovery position
            long newPos = Position(reader.ConsumedBytes);
            Debug.Assert(newPos >= ContinueFrom); // we should never move backwards
            Verbose.WriteLine($"Setting continue-from to [{newPos}]");
            ContinueFrom = newPos;

            if (reader.End) return (0, default);
            var fieldHeader = reader.ReadVarint();

            int fieldNumber = checked((int)(fieldHeader >> 3));
            var wireType = (WireType)(int)(fieldHeader & 7);
            if (fieldNumber <= 0) throw new InvalidOperationException($"Invalid field number: {fieldNumber}");
            return (fieldNumber, wireType);
        }

        readonly Stack<ResumeFrame> _resume = new Stack<ResumeFrame>();

        internal bool Execute(ref ReadOnlyBuffer buffer, out long moreBytesNeeded)
        {
            moreBytesNeeded = 0;
            _basePosition.Buffer = buffer;
            while (!buffer.IsEmpty && _resume.TryPeek(out var frame))
            {
                long available = buffer.Length, oldOrigin = _basePosition.Absolute;

                ReadOnlyBuffer slice;
                bool frameIsComplete = false;

                long needed;
                if (frame.End != long.MaxValue && available >= (needed = frame.End - Position(0)))
                {
                    frameIsComplete = true; // we have all the data, so we don't expect it to fail with more-bytes-needed 
                    slice = buffer.Slice(0, needed);
                }
                else
                {
                    slice = buffer;
                }
                Verbose.WriteLine($"{frame} executing on {slice.Length} bytes from {Position(0)} to {Position(slice.Length)}; expected end: {(frame.End == long.MaxValue ? -1 : frame.End)}...");

                if (frame.TryExecute(slice, this, out var consumedBytes, out moreBytesNeeded))
                {
                    Verbose.WriteLine($"Complete frame: {frame}");
                    // complete from length limit (sub-message or outer limit)
                    // or from group markers (sub-message only)
                    if (!ReferenceEquals(_resume.Pop(), frame))
                    {
                        throw new InvalidOperationException("Object was completed, but the wrong frame was popped; oops!");
                    }
                    frame.ChangeState(ResumeFrame.ResumeState.Deserializing, ResumeFrame.ResumeState.AdvertisingFieldHeader);
                    _previous = frame; // so the frame before can extract the values
                }
                ShowFrames("after TryExecute");

                if (frameIsComplete && moreBytesNeeded != 0)
                {
                    throw new InvalidOperationException("We should have had a complete frame, but someone is requesting more data");
                }

                buffer = buffer.Slice(consumedBytes);
                SetOrigin(oldOrigin + consumedBytes, buffer);

                if (consumedBytes == 0) return false;

                Verbose.WriteLine($"consumed {consumedBytes} bytes; position now {Position(0)}");
            }
            return true;
        }
        [Conditional("VERBOSE")]
        void ShowFrames(string state)
        {
#if VERBOSE
            if (_resume.Count != 0)
            {
                Verbose.WriteLine($"Incomplete frames: {state}");
                foreach (var x in _resume)
                {
                    Verbose.WriteLine($"\t{x}; expected end: {(x.End == long.MaxValue ? -1 : x.End)}...");
                }
            }
#endif
        }
        internal ResumeFrame Resume<T>(IResumableDeserializer<T> serializer, T value, long end)
        {
            var frame = ResumeFrame.Create<T>(serializer, value, 0, WireType.String, end);
            _resume.Push(frame);
            ShowFrames("after Resume");
            return frame;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal long Position(long offset) => _basePosition.Absolute + offset;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal long Position(int offset) => _basePosition.Absolute + offset;
        internal long PositionSlow(in Position position) => _basePosition.Absolute +
            _basePosition.Buffer.Slice(0, position).Length;

        internal ReadOnlyBuffer Slice(long offet, long length)
            => _basePosition.Buffer.Slice(0, length);
        internal ReadOnlyBuffer Slice(Position from, Position to)
            => _basePosition.Buffer.Slice(from, to);

        internal void ClearPosition()
        {
            _basePosition.Buffer = default;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetOrigin(long absolute, in ReadOnlyBuffer relative)
        {
            Verbose.WriteLine($"Setting origin to [{absolute}-{(absolute + relative.Length)}] ({relative.Length})");
            _basePosition = (absolute, relative);
        }
        (long Absolute, ReadOnlyBuffer Buffer) _basePosition;

        internal T DeserializeSubItem<T>(ref BufferReader<ReadOnlyBuffer> reader,
            IResumableDeserializer<T> serializer,
            int fieldNumber, WireType wireType, T value = default)
        {
            DeserializeSubItem(ref reader, serializer, fieldNumber, wireType, ref value);
            return value;
        }
        internal void DeserializeSubItem<T>(ref BufferReader<ReadOnlyBuffer> reader,
            IResumableDeserializer<T> serializer,
            int fieldNumber, WireType wireType, ref T value)
        {
            var frame = _previous;
            if (frame != null)
            {
                Verbose.WriteLine($"Consuming popped '{typeof(T).Name}'");
                value = frame.Get<T>(fieldNumber, wireType);
                _previous.Recycle();
                _previous = null; // and thus it was consumed!
                return;
            }

            // we're going to assume length-prefixed here
            if (wireType != WireType.String) throw new NotImplementedException();
            int len = checked((int)reader.ReadVarint());

            long startAbsolute = Position(reader.ConsumedBytes);
            var from = reader.Position;
            bool itFits;
            try
            {   // see if we have it all directly
                reader.Skip(len);
                itFits = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                itFits = false;
            }

            if (itFits)
            {
                var slice = Slice(from, reader.Position);
                Verbose.WriteLine($"Reading '{typeof(T).Name}' without using the context stack; {len} bytes needed, {slice.Length} available; {startAbsolute} to {Position(reader.ConsumedBytes)}");

                try
                {
                    _pushLock++;
                    var oldOrigin = _basePosition; // snapshot the old origin
                    SetOrigin(startAbsolute, slice);
                    serializer.Deserialize(slice, ref value, this);
                    SetOrigin(oldOrigin.Absolute, oldOrigin.Buffer); // because after this, the running method is going to keep using Position(reader.ConsumedBytes), which is an *earlier* point
                    _pushLock--;
                }
                catch (MoreDataNeededException ex)
                {
                    throw new InvalidOperationException($"{typeof(T).Name} should have been fully contained, but requested {ex.MoreBytesNeeded} more bytes: {ex.Message}");
                }
            }
            else
            {
                // incomplete object; do what we can
                frame = ResumeFrame.Create<T>(serializer, value, fieldNumber, wireType, startAbsolute + len);
                var slice = Slice(from, reader.Position);


                Verbose.WriteLine($"Reading '{typeof(T).Name}' using the context stack (incomplete data); {len} bytes needed, {slice.Length} available; {startAbsolute} to {frame.End}");

                if (_pushLock != 0)
                {
                    throw new InvalidOperationException($"{typeof(T).Name} should have been fully contained, but didn't seem to fit");
                }
                _resume.Push(frame);
                ShowFrames("in DeserializeSubItem");
                SetOrigin(startAbsolute, slice);
                frame.Execute(slice, this);

                throw new InvalidOperationException("I unexpectedly succeeded; this is embarrassing");
            }
        }
        int _pushLock;
        public int AwaitCount { get; private set; }

        internal void IncrementAwaitCount() => AwaitCount++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong FieldHeader(int fieldNumber, WireType wireType)
            => (((ulong)fieldNumber) << 3) | (uint)wireType;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FieldHeaderLength(int fieldNumber)
        {
            if ((fieldNumber & ~15) == 0) return 1;
            int len = 1;
            uint value = (uint)fieldNumber >> 4;

            do
            {
                value >>= 7;
                len++;
            }
            while ((value & ~127U) != 0);
            return len;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int VarintLength(uint value)
        {
            int len = 1;
            while ((value & ~127U) != 0)
            {
                value >>= 7;
                len++;
            }
            return len;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int VarintLength(ulong value)
        {
            int len = 1;
            while ((value & ~127UL) != 0)
            {
                value >>= 7;
                len++;
            }
            return len;
        }

        //internal long GetLengthSubItem<T>(IResumableDeserializer<T> serializer,
        //    in T value, int fieldNumber, WireType wireType = WireType.String)
        //{
        //    if (value == null) return 0;

        //    if (wireType != WireType.String) throw new NotImplementedException($"Not yet done: {wireType}");

        //    var expectedLength = serializer.GetLength(value, this);
        //    return FieldHeaderLength(fieldNumber) + VarintLength((ulong)expectedLength) + expectedLength;
        //    // return 5 + 10 + expectedLength;
        //}

        

        internal long SerializeSubItem<TBuffer, T>(in TBuffer buffer, IResumableDeserializer<T> serializer,
            in T value, int fieldNumber, WireType wireType = WireType.String)
            where TBuffer : IOutput
        {
            if (value == null) return 0;

            if (wireType != WireType.String) throw new NotImplementedException($"Not yet done: {wireType}");

            // var expectedLength = serializer.GetLength(value, this);
            var expectedLength = serializer.Serialize<NilOutput>(default, value, this);
            var fieldHeader = FieldHeader(fieldNumber, wireType);

            var writer = OutputWriter.Create(buffer);
            long headerLength = writer.WriteVarint(fieldHeader);
            headerLength += writer.WriteVarint((ulong)expectedLength);

            long actualLength = serializer.Serialize(buffer, value, this);
            if (actualLength != expectedLength)
                throw new InvalidOperationException($"Length was incorrect; calculated {expectedLength}, was {actualLength}");
            return headerLength + actualLength;
            
        }
        //internal int GetLengthInt32(int fieldNumber, WireType wireType, int value, int defaultValue)
        //{
        //    if (value == defaultValue) return 0;

        //    int headerLen = FieldHeaderLength(fieldNumber);
        //    switch (wireType)
        //    {
        //        case WireType.Fixed32: return headerLen + 4;
        //        case WireType.Fixed64: return headerLen + 8;
        //        case WireType.Varint: return headerLen + VarintLength((ulong)(long)value);
        //    }
        //    return ThrowInvalidWireType(wireType);
        //}
        internal int WriteInt32<TBuffer>(ref OutputWriter<TBuffer> writer, int fieldNumber, WireType wireType, int value, int defaultValue)
            where TBuffer : IOutput
        {
            if (value == defaultValue) return 0;

            int headerLen  = writer.WriteVarint(FieldHeader(fieldNumber, wireType));
            if (typeof(TBuffer) == typeof(NilOutput))
            {
                switch (wireType)
                {
                    case WireType.Fixed32: return headerLen + 4;
                    case WireType.Fixed64: return headerLen + 8;
                    case WireType.Varint: return headerLen + VarintLength((ulong)(long)value);
                }
            }
            else
            {
                switch (wireType)
                {
                    case WireType.Fixed32: return headerLen + WriteRawUInt32(ref writer, (uint)value);
                    case WireType.Fixed64: return headerLen + WriteRawUInt64(ref writer, (ulong)(long)value);
                    case WireType.Varint: return headerLen + writer.WriteVarint((ulong)(long)value);
                }
            }
            return ThrowInvalidWireType(wireType);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int WriteRawUInt32<TBuffer>(ref OutputWriter<TBuffer> writer, uint value)
            where TBuffer : IOutput
        {
            if (typeof(TBuffer) != typeof(NilOutput))
            {
                writer.Ensure(4);
                var span = writer.Span;
                span[0] = (byte)(value & 0xFF);
                span[1] = (byte)((value >> 8) & 0xFF);
                span[2] = (byte)((value >> 16) & 0xFF);
                span[3] = (byte)((value >> 24) & 0xFF);
                writer.Advance(4);
            }
            return 4;
        }
        static int WriteRawUInt64<TBuffer>(ref OutputWriter<TBuffer> writer, ulong value)
            where TBuffer : IOutput
        {
            if (typeof(TBuffer) != typeof(NilOutput))
            {
                writer.Ensure(8);
                var span = writer.Span;

                uint tmp = (uint)value;
                span[0] = (byte)(tmp & 0xFF);
                span[1] = (byte)((tmp >> 8) & 0xFF);
                span[2] = (byte)((tmp >> 16) & 0xFF);
                span[3] = (byte)((tmp >> 24) & 0xFF);

                tmp = (uint)(value >> 32);
                span[4] = (byte)(tmp & 0xFF);
                span[5] = (byte)((tmp >> 8) & 0xFF);
                span[6] = (byte)((tmp >> 16) & 0xFF);
                span[7] = (byte)((tmp >> 24) & 0xFF);
                writer.Advance(8);
            }
            return 8;

        }
        private static int ThrowInvalidWireType(WireType wireType) => throw new InvalidOperationException($"Invalid wire type: {wireType}");

        static readonly Encoding Encoding = Encoding.UTF8;
        //internal long GetLengthString(int fieldNumber, WireType wireType, string value, string defaultValue)
        //{
        //    if (value == null || value == defaultValue) return 0;
        //    if (wireType != WireType.String) return ThrowInvalidWireType(wireType);

        //    int bytes = Encoding.GetByteCount(value);
        //    return FieldHeaderLength(fieldNumber)
        //        + VarintLength((uint)bytes) + bytes;
        //    // return 5 + 10 + bytes;
        //}
        internal long WriteString<TBuffer>(ref OutputWriter<TBuffer> writer, int fieldNumber, WireType wireType, string value, string defaultValue)
            where TBuffer : IOutput
        {
            if (value == null || value == defaultValue) return 0;
            if (wireType != WireType.String) return ThrowInvalidWireType(wireType);

            if (typeof(TBuffer) == typeof(NilOutput))
            {
                int bytes = Encoding.GetByteCount(value);
                return FieldHeaderLength(fieldNumber) +
                    VarintLength((uint)bytes) + bytes;
            }
            else
            {
                return writer.WriteVarint(FieldHeader(fieldNumber, wireType)) + writer.WriteString(value);
            }
        }

        //internal int GetLengthDouble(int fieldNumber, WireType wireType, double value, double defaultValue)
        //{
        //    if (value == defaultValue) return 0;
        //    int headerLen = FieldHeaderLength(fieldNumber);
        //    switch (wireType)
        //    {
        //        case WireType.Fixed32: return headerLen + 4;
        //        case WireType.Fixed64: return headerLen + 8;
        //    }
        //    return ThrowInvalidWireType(wireType);
        //}

        internal int WriteDouble<TBuffer>(ref OutputWriter<TBuffer> writer, int fieldNumber, WireType wireType, double value, double defaultValue)
            where TBuffer : IOutput
        {
            if (value == defaultValue) return 0;

            int headerLen = writer.WriteVarint(FieldHeader(fieldNumber, wireType));
            if (typeof(TBuffer) == typeof(NilOutput))
            {
                switch (wireType)
                {
                    case WireType.Fixed32: return headerLen + 4;
                    case WireType.Fixed64: return headerLen + 8;
                }
            }
            else
            {
                switch (wireType)
                {
                    case WireType.Fixed32: return headerLen + WriteRawUInt32(ref writer, (uint)BitConverter.SingleToInt32Bits((float)value));
                    case WireType.Fixed64: return headerLen + WriteRawUInt64(ref writer, (ulong)BitConverter.DoubleToInt64Bits(value));
                }
            }
            return ThrowInvalidWireType(wireType);
        }
    }
    public interface IResumableDeserializer<T>
    {
        void Deserialize(in ReadOnlyBuffer buffer, ref T value, DeserializationContext ctx);
        long Serialize<TBuffer>(in TBuffer buffer, in T value, DeserializationContext ctx)
            where TBuffer : IOutput;

        // long GetLength(in T value, DeserializationContext ctx);
    }
    abstract class ResumeFrame
    {
        internal abstract void Recycle();
        public long End { get; private set; }
        public static ResumeFrame Create<T>(IResumableDeserializer<T> serializer, T value, int fieldNumber, WireType wireType, long end)
        {
            return SimpleCache<TypedResumeFrame<T>>.Get().Init(value, serializer, fieldNumber, wireType, end);
        }
        internal T Get<T>(int fieldNumber, WireType wireType)
        {
            ChangeState(ResumeState.ProvidingValue, ResumeState.Complete);
            if (fieldNumber != _fieldNumber || wireType != _wireType)
            {
                throw new InvalidOperationException($"Invalid field/wire-type combination");
            }
            return Get<T>();
        }
        internal T Get<T>() => ((TypedResumeFrame<T>)this).Value;

        internal abstract void Execute(in ReadOnlyBuffer slice, DeserializationContext ctx);
        internal abstract bool TryExecute(in ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes, out long moreBytesNeeded);

        private int _fieldNumber;
        private WireType _wireType;
        ResumeState _state;
        internal (int FieldNumber, WireType WireType) FieldHeader()
        {
            ChangeState(ResumeState.AdvertisingFieldHeader, ResumeState.ProvidingValue);
            return (_fieldNumber, _wireType);
        }
        internal void ChangeState(ResumeState from, ResumeState to)
        {
            if (_state == from) _state = to;
            else throw new InvalidOperationException($"Invalid state; expected '{from}', was '{_state}'");
        }

        sealed class TypedResumeFrame<T> : ResumeFrame, IDisposable
        {
            void IDisposable.Dispose()
            {
                _value = default;
                _serializer = default;
            }
            public override string ToString() => $"Frame of '{typeof(T).Name}' (field {_fieldNumber}, {_wireType}): {_value}";
            internal override void Recycle() => SimpleCache<TypedResumeFrame<T>>.Recycle(this);

            public T Value => _value;
            private T _value;
            private IResumableDeserializer<T> _serializer;

            internal ResumeFrame Init(T value, IResumableDeserializer<T> serializer, int fieldNumber, WireType wireType, long end)
            {
                _value = value;
                _serializer = serializer;
                _fieldNumber = fieldNumber;
                _wireType = wireType;
                _state = ResumeState.Deserializing;
                End = end;
                return this;
            }

            internal override void Execute(in ReadOnlyBuffer slice, DeserializationContext ctx)
            {
                Verbose.WriteLine($"Executing frame for {typeof(T).Name}... [{ctx.PositionSlow(slice.Start)}-{ctx.PositionSlow(slice.End)}]");
                try
                {
                    _serializer.Deserialize(slice, ref _value, ctx);
                }
                finally
                {
                    Verbose.WriteLine($"{typeof(T).Name} after execute: {_value}");
                }
            }
            internal override bool TryExecute(in ReadOnlyBuffer slice, DeserializationContext ctx, out long consumedBytes, out long moreBytesNeeded)
            {
                Verbose.WriteLine($"Executing frame for {typeof(T).Name}... [{ctx.PositionSlow(slice.Start)}-{ctx.PositionSlow(slice.End)}]");

                bool result;
                moreBytesNeeded = 0;
                long started = ctx.Position(0);
                try
                {
                    _serializer.Deserialize(slice, ref _value, ctx);
                    result = true;
                }
                catch (MoreDataNeededException ex)
                {
                    moreBytesNeeded = ex.MoreBytesNeeded;
                    result = false;
                }
                Verbose.WriteLine($"{typeof(T).Name} after execute: {_value}");
                consumedBytes = ctx.ContinueFrom - started;
                return result;
            }
        }

        internal enum ResumeState
        {
            Deserializing,
            AdvertisingFieldHeader,
            ProvidingValue,
            Complete
        }
    }
    internal struct NilOutput : IOutput
    {
        //const int PAGE_SIZE = 4096,
        //    BLOCK_SIZE = PAGE_SIZE - 64;
        //private static readonly byte[] _page = new byte[PAGE_SIZE];

        void IOutput.Advance(int bytes)
            => ThrowNotSupported();
        //{ }

        void IOutput.Enlarge(int desiredBufferLength)
            => ThrowNotSupported();
        //{
        //    if (desiredBufferLength < 0 ||
        //        desiredBufferLength > BLOCK_SIZE)
        //        ThrowOutOfRange();
        //}
        //static void ThrowOutOfRange() =>
        //    throw new ArgumentOutOfRangeException();
        static void ThrowNotSupported([CallerMemberName] string caller = null) =>
            throw new NotSupportedException(caller);
        Span<byte> IOutput.GetSpan() => default;
        //=> new Span<byte>(_page, 0, BLOCK_SIZE);
    }

}
