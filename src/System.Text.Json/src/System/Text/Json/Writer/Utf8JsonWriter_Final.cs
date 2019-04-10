﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Text.Json
{
    /// <summary>
    /// Provides a high-performance API for forward-only, non-cached writing of UTF-8 encoded JSON text.
    /// It writes the text sequentially with no caching and adheres to the JSON RFC
    /// by default (https://tools.ietf.org/html/rfc8259), with the exception of writing comments.
    /// </summary>
    /// <remarks>
    /// When the user attempts to write invalid JSON and validation is enabled, it throws
    /// a <see cref="InvalidOperationException"/> with a context specific error message.
    /// Since this type is a ref struct, it does not directly support async. However, it does provide
    /// support for reentrancy to write partial data, and continue writing in chunks.
    /// To be able to format the output with indentation and whitespace OR to skip validation, create an instance of 
    /// <see cref="JsonWriterState"/> and pass that in to the writer.
    /// </remarks>
    public partial class Utf8JsonWriter_Final
    {
        private const int StackallocThreshold = 256;
        private const int DefaultGrowthSize = 4096;

        private readonly IBufferWriter<byte> _output;
        private Memory<byte> _memory;

        private Stream _stream;
        private byte[] _buffer;

        private int _buffered;

        private bool _inObject;
        private bool _isNotPrimitive;
        private JsonTokenType _tokenType;
        private readonly JsonWriterOptions _writerOptions;
        private BitStack _bitStack;

        // The highest order bit of _currentDepth is used to discern whether we are writing the first item in a list or not.
        // if (_currentDepth >> 31) == 1, add a list separator before writing the item
        // else, no list separator is needed since we are writing the first item.
        private int _currentDepth;

        private int Indentation => CurrentDepth * JsonConstants.SpacesPerIndent;

        /// <summary>
        /// Returns the total amount of bytes written by the <see cref="Utf8JsonWriter"/> so far
        /// for the current instance of the <see cref="Utf8JsonWriter"/>.
        /// This includes data that has been written beyond what has already been committed.
        /// </summary>
        public long BytesWritten
        {
            get
            {
                Debug.Assert(BytesCommitted <= long.MaxValue - _buffered);
                return BytesCommitted + _buffered;
            }
        }

        /// <summary>
        /// Returns the total amount of bytes committed to the output by the <see cref="Utf8JsonWriter"/> so far
        /// for the current instance of the <see cref="Utf8JsonWriter"/>.
        /// This is how much the IBufferWriter has advanced.
        /// </summary>
        public long BytesCommitted { get; private set; }

        /// <summary>
        /// Tracks the recursive depth of the nested objects / arrays within the JSON text
        /// written so far. This provides the depth of the current token.
        /// </summary>
        public int CurrentDepth => _currentDepth & JsonConstants.RemoveFlagsBitMask;

        private const int MinimumBufferSize = 4_096;

        /// <summary>
        /// Gets the custom behavior when writing JSON using
        /// the <see cref="Utf8JsonWriter"/> which indicates whether to format the output
        /// while writing and whether to skip structural JSON validation or not.
        /// </summary>
        public JsonWriterOptions Options => _writerOptions;

        public Utf8JsonWriter_Final(IBufferWriter<byte> bufferWriter, JsonWriterOptions options = default)
        {
            _output = bufferWriter ?? throw new ArgumentNullException(nameof(bufferWriter));
            _stream = default;
            _buffered = 0;
            BytesCommitted = 0;
            _memory = _output.GetMemory();
            _buffer = default;

            _inObject = default;
            _isNotPrimitive = default;
            _tokenType = default;
            _currentDepth = default;
            _writerOptions = options;

            // Only allocate if the user writes a JSON payload beyond the depth that the _allocationFreeContainer can handle.
            // This way we avoid allocations in the common, default cases, and allocate lazily.
            _bitStack = default;
        }

        public Utf8JsonWriter_Final(Stream stream, JsonWriterOptions options = default)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _output = default;
            _buffered = 0;
            BytesCommitted = 0;
            _memory = default;
            _buffer = new byte[MinimumBufferSize];

            _inObject = default;
            _isNotPrimitive = default;
            _tokenType = default;
            _currentDepth = default;
            _writerOptions = options;

            // Only allocate if the user writes a JSON payload beyond the depth that the _allocationFreeContainer can handle.
            // This way we avoid allocations in the common, default cases, and allocate lazily.
            _bitStack = default;
        }

        public void Flush(bool isFinalBlock = true)
        {
            if (isFinalBlock && !_writerOptions.SkipValidation && (CurrentDepth != 0 || _tokenType == JsonTokenType.None))
                ThrowHelper.ThrowInvalidOperationException_DepthNonZeroOrEmptyJson(_currentDepth);

            if (_stream != null)
            {
                Span<byte> lastSpan = _buffer.AsSpan(0, _buffered);
                _stream.Write(lastSpan);
                lastSpan.Clear();
                _stream.Flush();
            }
            else
            {
                _output.Advance(_buffered);
            }

            BytesCommitted += _buffered;
            _buffered = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushHelperStream()
        {
            Span<byte> lastSpan = _buffer.AsSpan(0, _buffered);
            _stream.Write(lastSpan);
            lastSpan.Clear();
            BytesCommitted += _buffered;
            _buffered = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushHelperIBW()
        {
            _output.Advance(_buffered);
            BytesCommitted += _buffered;
            _buffered = 0;
        }

        public async Task FlushAsync(CancellationToken cancellationToken = default, bool isFinalBlock = true)
        {
            if (isFinalBlock && !_writerOptions.SkipValidation && (CurrentDepth != 0 || _tokenType == JsonTokenType.None))
                ThrowHelper.ThrowInvalidOperationException_DepthNonZeroOrEmptyJson(_currentDepth);

            if (_stream != null)
            {
                await _stream.WriteAsync(_buffer, 0, _buffered, cancellationToken);
                _buffer.AsSpan(0, _buffered).Clear();
                await _stream.FlushAsync();
                BytesCommitted += _buffered;
                _buffered = 0;
            }
        }

        /// <summary>
        /// Writes the beginning of a JSON array.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartArray()
        {
            WriteStart(JsonConstants.OpenBracket);
            _tokenType = JsonTokenType.StartArray;
        }

        /// <summary>
        /// Writes the beginning of a JSON object.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartObject()
        {
            WriteStart(JsonConstants.OpenBrace);
            _tokenType = JsonTokenType.StartObject;
        }

        private void WriteStart(byte token)
        {
            if (CurrentDepth >= JsonConstants.MaxWriterDepth)
                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.DepthTooLarge, _currentDepth);

            if (_writerOptions.IndentedOrNotSkipValidation)
            {
                WriteStartSlow(token);
            }
            else
            {
                WriteStartMinimized(token);
            }

            _currentDepth &= JsonConstants.RemoveFlagsBitMask;
            _currentDepth++;
            _isNotPrimitive = true;
        }

        private void WriteStartMinimized(byte token)
        {
            if (_memory.Length - _buffered < 2)
            {
                GrowAndEnsure();
            }

            Span<byte> output = _memory.Span;
            if (_currentDepth < 0)
            {
                output[_buffered++] = JsonConstants.ListSeparator;
            }
            output[_buffered++] = token;
        }

        private void WriteStartSlow(byte token)
        {
            Debug.Assert(_writerOptions.Indented || !_writerOptions.SkipValidation);

            if (_writerOptions.Indented)
            {
                if (!_writerOptions.SkipValidation)
                {
                    ValidateStart();
                    UpdateBitStackOnStart(token);
                }
                WriteStartIndented(token);
            }
            else
            {
                Debug.Assert(!_writerOptions.SkipValidation);
                ValidateStart();
                UpdateBitStackOnStart(token);
                WriteStartMinimized(token);
            }
        }

        private void ValidateStart()
        {
            if (_inObject)
            {
                Debug.Assert(_tokenType != JsonTokenType.None && _tokenType != JsonTokenType.StartArray);
                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.CannotStartObjectArrayWithoutProperty, tokenType: _tokenType);
            }
            else
            {
                Debug.Assert(_tokenType != JsonTokenType.StartObject);
                if (_tokenType != JsonTokenType.None && (!_isNotPrimitive || CurrentDepth == 0))
                {
                    ThrowHelper.ThrowInvalidOperationException(ExceptionResource.CannotStartObjectArrayAfterPrimitiveOrClose, tokenType: _tokenType);
                }
            }
        }

        private void WriteStartIndented(byte token)
        {
            if (_currentDepth < 0)
            {
                if (_buffer.Length <= _buffered)
                {
                    GrowAndEnsure();
                }
                _buffer[_buffered++] = JsonConstants.ListSeparator;
            }

            if (_tokenType != JsonTokenType.None)
                WriteNewLine();

            int indent = Indentation;
            while (true)
            {
                bool result = JsonWriterHelper.TryWriteIndentation(_buffer.AsSpan(_buffered), indent, out int bytesWritten);
                _buffered += bytesWritten;
                if (result)
                {
                    break;
                }
                indent -= bytesWritten;
                GrowAndEnsure();
            }

            if (_buffer.Length <= _buffered)
            {
                GrowAndEnsure();
            }
            _buffer[_buffered++] = token;
        }

        /// <summary>
        /// Writes the beginning of a JSON array with a property name as the key.
        /// </summary>
        /// <param name="utf8PropertyName">The UTF-8 encoded property name of the JSON array to be written.</param>
        /// <param name="escape">If this is set to false, the writer assumes the property name is properly escaped and skips the escaping step.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the specified property name is too large.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartArray(ReadOnlySpan<byte> utf8PropertyName, bool escape = true)
        {


            ValidatePropertyNameAndDepth(utf8PropertyName);

            if (escape)
            {
                WriteStartEscape(utf8PropertyName, JsonConstants.OpenBracket);
            }
            else
            {
                WriteStartByOptions(utf8PropertyName, JsonConstants.OpenBracket);
            }

            _currentDepth &= JsonConstants.RemoveFlagsBitMask;
            _currentDepth++;
            _isNotPrimitive = true;
            _tokenType = JsonTokenType.StartArray;
        }

        /// <summary>
        /// Writes the beginning of a JSON object with a property name as the key.
        /// </summary>
        /// <param name="utf8PropertyName">The UTF-8 encoded property name of the JSON object to be written.</param>
        /// <param name="escape">If this is set to false, the writer assumes the property name is properly escaped and skips the escaping step.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the specified property name is too large.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartObject(ReadOnlySpan<byte> utf8PropertyName, bool escape = true)
        {


            ValidatePropertyNameAndDepth(utf8PropertyName);

            if (escape)
            {
                WriteStartEscape(utf8PropertyName, JsonConstants.OpenBrace);
            }
            else
            {
                WriteStartByOptions(utf8PropertyName, JsonConstants.OpenBrace);
            }

            _currentDepth &= JsonConstants.RemoveFlagsBitMask;
            _currentDepth++;
            _isNotPrimitive = true;
            _tokenType = JsonTokenType.StartObject;
        }

        private void WriteStartEscape(ReadOnlySpan<byte> utf8PropertyName, byte token)
        {
            int propertyIdx = JsonWriterHelper.NeedsEscaping(utf8PropertyName);

            Debug.Assert(propertyIdx >= -1 && propertyIdx < int.MaxValue / 2);

            if (propertyIdx != -1)
            {
                WriteStartEscapeProperty(utf8PropertyName, token, propertyIdx);
            }
            else
            {
                WriteStartByOptions(utf8PropertyName, token);
            }
        }

        private void WriteStartByOptions(ReadOnlySpan<byte> utf8PropertyName, byte token)
        {
            ValidateWritingProperty(token);
            if (_writerOptions.Indented)
            {
                WriteStartIndented(utf8PropertyName, token);
            }
            else
            {
                WritePropertyNameMinimized(utf8PropertyName, token);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteStartIndented(ReadOnlySpan<byte> utf8PropertyName, byte token)
        {
            WritePropertyNameIndented(utf8PropertyName);
            if (_buffer.Length <= _buffered)
            {
                GrowAndEnsure();
            }

            _buffer[_buffered++] = token;
        }

        private void WriteStartEscapeProperty(ReadOnlySpan<byte> utf8PropertyName, byte token, int firstEscapeIndexProp)
        {
            Debug.Assert(int.MaxValue / JsonConstants.MaxExpansionFactorWhileEscaping >= utf8PropertyName.Length);
            Debug.Assert(firstEscapeIndexProp >= 0 && firstEscapeIndexProp < utf8PropertyName.Length);

            byte[] propertyArray = null;

            int length = JsonWriterHelper.GetMaxEscapedLength(utf8PropertyName.Length, firstEscapeIndexProp);
            Span<byte> escapedPropertyName;
            if (length > StackallocThreshold)
            {
                propertyArray = ArrayPool<byte>.Shared.Rent(length);
                escapedPropertyName = propertyArray;
            }
            else
            {
                // Cannot create a span directly since it gets passed to instance methods on a ref struct.
                unsafe
                {
                    byte* ptr = stackalloc byte[length];
                    escapedPropertyName = new Span<byte>(ptr, length);
                }
            }

            JsonWriterHelper.EscapeString(utf8PropertyName, escapedPropertyName, firstEscapeIndexProp, out int written);

            WriteStartByOptions(escapedPropertyName.Slice(0, written), token);

            if (propertyArray != null)
            {
                ArrayPool<byte>.Shared.Return(propertyArray);
            }
        }

        /// <summary>
        /// Writes the beginning of a JSON array with a property name as the key.
        /// </summary>
        /// <param name="propertyName">The UTF-16 encoded property name of the JSON array to be transcoded and written as UTF-8.</param>
        /// <param name="escape">If this is set to false, the writer assumes the property name is properly escaped and skips the escaping step.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the specified property name is too large.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartArray(string propertyName, bool escape = true)
            => WriteStartArray(propertyName.AsSpan(), escape);

        /// <summary>
        /// Writes the beginning of a JSON object with a property name as the key.
        /// </summary>
        /// <param name="propertyName">The UTF-16 encoded property name of the JSON object to be transcoded and written as UTF-8.</param>
        /// <param name="escape">If this is set to false, the writer assumes the property name is properly escaped and skips the escaping step.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the specified property name is too large.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartObject(string propertyName, bool escape = true)
            => WriteStartObject(propertyName.AsSpan(), escape);

        /// <summary>
        /// Writes the beginning of a JSON array with a property name as the key.
        /// </summary>
        /// <param name="propertyName">The UTF-16 encoded property name of the JSON array to be transcoded and written as UTF-8.</param>
        /// <param name="escape">If this is set to false, the writer assumes the property name is properly escaped and skips the escaping step.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the specified property name is too large.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartArray(ReadOnlySpan<char> propertyName, bool escape = true)
        {


            ValidatePropertyNameAndDepth(propertyName);

            if (escape)
            {
                WriteStartEscape(propertyName, JsonConstants.OpenBracket);
            }
            else
            {
                WriteStartByOptions(propertyName, JsonConstants.OpenBracket);
            }

            _currentDepth &= JsonConstants.RemoveFlagsBitMask;
            _currentDepth++;
            _isNotPrimitive = true;
            _tokenType = JsonTokenType.StartArray;
        }

        /// <summary>
        /// Writes the beginning of a JSON object with a property name as the key.
        /// </summary>
        /// <param name="propertyName">The UTF-16 encoded property name of the JSON object to be transcoded and written as UTF-8.</param>
        /// <param name="escape">If this is set to false, the writer assumes the property name is properly escaped and skips the escaping step.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when the specified property name is too large.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the depth of the JSON has exceeded the maximum depth of 1000 
        /// OR if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteStartObject(ReadOnlySpan<char> propertyName, bool escape = true)
        {


            ValidatePropertyNameAndDepth(propertyName);

            if (escape)
            {
                WriteStartEscape(propertyName, JsonConstants.OpenBrace);
            }
            else
            {
                WriteStartByOptions(propertyName, JsonConstants.OpenBrace);
            }

            _currentDepth &= JsonConstants.RemoveFlagsBitMask;
            _currentDepth++;
            _isNotPrimitive = true;
            _tokenType = JsonTokenType.StartObject;
        }

        private void WriteStartEscape(ReadOnlySpan<char> propertyName, byte token)
        {
            int propertyIdx = JsonWriterHelper.NeedsEscaping(propertyName);

            Debug.Assert(propertyIdx >= -1 && propertyIdx < int.MaxValue / 2);

            if (propertyIdx != -1)
            {
                WriteStartEscapeProperty(propertyName, token, propertyIdx);
            }
            else
            {
                WriteStartByOptions(propertyName, token);
            }
        }

        private void WriteStartByOptions(ReadOnlySpan<char> propertyName, byte token)
        {
            ValidateWritingProperty(token);
            if (_writerOptions.Indented)
            {
                WritePropertyNameIndented(propertyName);
                if (_buffer.Length <= _buffered)
                {
                    GrowAndEnsure();
                }
                _buffer[_buffered++] = token;
            }
            else
            {
                WritePropertyNameMinimized(propertyName, token);
            }
        }

        private void WriteStartEscapeProperty(ReadOnlySpan<char> propertyName, byte token, int firstEscapeIndexProp)
        {
            Debug.Assert(int.MaxValue / JsonConstants.MaxExpansionFactorWhileEscaping >= propertyName.Length);
            Debug.Assert(firstEscapeIndexProp >= 0 && firstEscapeIndexProp < propertyName.Length);

            char[] propertyArray = null;

            int length = JsonWriterHelper.GetMaxEscapedLength(propertyName.Length, firstEscapeIndexProp);
            Span<char> escapedPropertyName;
            if (length > StackallocThreshold)
            {
                propertyArray = ArrayPool<char>.Shared.Rent(length);
                escapedPropertyName = propertyArray;
            }
            else
            {
                // Cannot create a span directly since it gets passed to instance methods on a ref struct.
                unsafe
                {
                    char* ptr = stackalloc char[length];
                    escapedPropertyName = new Span<char>(ptr, length);
                }
            }
            JsonWriterHelper.EscapeString(propertyName, escapedPropertyName, firstEscapeIndexProp, out int written);

            WriteStartByOptions(escapedPropertyName.Slice(0, written), token);

            if (propertyArray != null)
            {
                ArrayPool<char>.Shared.Return(propertyArray);
            }
        }

        /// <summary>
        /// Writes the end of a JSON array.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteEndArray()
        {


            WriteEnd(JsonConstants.CloseBracket);
            _tokenType = JsonTokenType.EndArray;
        }

        /// <summary>
        /// Writes the end of a JSON object.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if this would result in an invalid JSON to be written (while validation is enabled).
        /// </exception>
        public void WriteEndObject()
        {


            WriteEnd(JsonConstants.CloseBrace);
            _tokenType = JsonTokenType.EndObject;
        }

        private void WriteEnd(byte token)
        {
            if (_writerOptions.IndentedOrNotSkipValidation)
            {
                WriteEndSlow(token);
            }
            else
            {
                WriteEndMinimized(token);
            }

            SetFlagToAddListSeparatorBeforeNextItem();
            // Necessary if WriteEndX is called without a corresponding WriteStartX first.
            if (CurrentDepth != 0)
            {
                _currentDepth--;
            }
        }

        private void WriteEndMinimized(byte token)
        {
            if (_memory.Length <= _buffered)
            {
                GrowAndEnsure();
            }
            Span<byte> output = _memory.Span;
            output[_buffered++] = token;
        }

        private void WriteEndSlow(byte token)
        {
            Debug.Assert(_writerOptions.Indented || !_writerOptions.SkipValidation);

            if (_writerOptions.Indented)
            {
                if (!_writerOptions.SkipValidation)
                {
                    ValidateEnd(token);
                }
                WriteEndIndented(token);
            }
            else
            {
                Debug.Assert(!_writerOptions.SkipValidation);
                ValidateEnd(token);
                WriteEndMinimized(token);
            }
        }

        private void ValidateEnd(byte token)
        {
            if (_bitStack.CurrentDepth <= 0)
                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.MismatchedObjectArray, token);

            if (token == JsonConstants.CloseBracket)
            {
                if (_inObject)
                {
                    Debug.Assert(_tokenType != JsonTokenType.None);
                    ThrowHelper.ThrowInvalidOperationException(ExceptionResource.MismatchedObjectArray, token);
                }
            }
            else
            {
                Debug.Assert(token == JsonConstants.CloseBrace);

                if (!_inObject)
                {
                    ThrowHelper.ThrowInvalidOperationException(ExceptionResource.MismatchedObjectArray, token);
                }
            }

            _inObject = _bitStack.Pop();
        }

        private void WriteEndIndented(byte token)
        {
            // Do not format/indent empty JSON object/array.
            if (_tokenType == JsonTokenType.StartObject || _tokenType == JsonTokenType.StartArray)
            {
                WriteEndMinimized(token);
            }
            else
            {
                WriteNewLine();
                int indent = Indentation;
                // Necessary if WriteEndX is called without a corresponding WriteStartX first.
                if (indent != 0)
                {
                    // The end token should be at an outer indent and since we haven't updated
                    // current depth yet, explicitly subtract here.
                    indent -= JsonConstants.SpacesPerIndent;
                }
                while (true)
                {
                    bool result = JsonWriterHelper.TryWriteIndentation(_buffer.AsSpan(_buffered), indent, out int bytesWritten);
                    _buffered += bytesWritten;
                    if (result)
                    {
                        break;
                    }
                    indent -= bytesWritten;
                    GrowAndEnsure();
                }

                if (_buffer.Length <= _buffered)
                {
                    GrowAndEnsure();
                }
                _buffer[_buffered++] = token;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteNewLine()
        {
            // Write '\r\n' OR '\n', depending on OS
            if (Environment.NewLine.Length == 2)
            {
                if (_buffer.Length <= _buffered)
                {
                    GrowAndEnsure();
                }
                _buffer[_buffered++] = JsonConstants.CarriageReturn;
            }

            if (_buffer.Length <= _buffered)
            {
                GrowAndEnsure();
            }
            _buffer[_buffered++] = JsonConstants.LineFeed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateBitStackOnStart(byte token)
        {
            if (token == JsonConstants.OpenBracket)
            {
                _bitStack.PushFalse();
                _inObject = false;
            }
            else
            {
                Debug.Assert(token == JsonConstants.OpenBrace);
                _bitStack.PushTrue();
                _inObject = true;
            }
        }

        private void GrowAndEnsure()
        {
            if (_stream != null)
            {
                if (_memory.Length >= int.MaxValue / 4)
                {
                    FlushHelperStream();
                }
                else
                {
                    var newBuffer = new byte[checked(_buffer.Length * 2)];

                    Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _buffered);

                    _buffer = newBuffer;
                    _memory = _buffer.AsMemory();
                }
            }
            else
            {
                FlushHelperIBW();
                Debug.Assert(_buffer.Length < DefaultGrowthSize);
                _memory = _output.GetMemory(DefaultGrowthSize);
            }
        }

        private void GrowAndEnsure(int growBy)
        {
            if (_stream != null)
            {
                int max = Math.Max(growBy, _memory.Length);
                if (_memory.Length >= int.MaxValue - max)
                {
                    FlushHelperStream();

                    _buffer = new byte[MinimumBufferSize];
                    _memory = _buffer.AsMemory();
                }
                else
                {
                    var newBuffer = new byte[_memory.Length + max];
                    Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _buffered);
                    _buffer = newBuffer;
                    _memory = _buffer.AsMemory();
                }
            }
            else
            {
                FlushHelperIBW();
                Debug.Assert(growBy < DefaultGrowthSize);
                _memory = _output.GetMemory(DefaultGrowthSize);
            }
        }

        private void GrowAndEnsure(int minimumSizeRequired, int maximumSizeRequired)
        {
            if (_stream != null)
            {
                int max = Math.Max(maximumSizeRequired, _memory.Length);
                if (_memory.Length >= int.MaxValue - max)
                {
                    FlushHelperStream();

                    _buffer = new byte[MinimumBufferSize];
                    _memory = _buffer.AsMemory();
                }
                else
                {
                    var newBuffer = new byte[_memory.Length + max];
                    Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _buffered);
                    _buffer = newBuffer;
                    _memory = _buffer.AsMemory();
                }
            }
            else
            {
                FlushHelperIBW();
                Debug.Assert(maximumSizeRequired < DefaultGrowthSize);
                _memory = _output.GetMemory(DefaultGrowthSize);
            }
        }

        private static void ThrowInvalidOperationException(int capacity)
        {
            throw new InvalidOperationException($"Cannot advance past the end of the buffer, which has a size of {capacity}.");
        }

        private void CopyLoop(ReadOnlySpan<byte> span)
        {
            while (true)
            {
                if (span.Length <= _buffer.Length - _buffered)
                {
                    span.CopyTo(_buffer.AsSpan(_buffered));
                    _buffered += span.Length;
                    break;
                }

                span.Slice(0, _buffer.Length - _buffered).CopyTo(_buffer.AsSpan(_buffered));
                span = span.Slice(_buffer.Length - _buffered);
                _buffered = _buffer.Length;
                GrowAndEnsure();
            }
        }

        private void SetFlagToAddListSeparatorBeforeNextItem()
        {
            _currentDepth |= 1 << 31;
        }

        public void Clear()
        {
            ClearHelper();
        }

        private void ClearHelper()
        {
            if (_stream != null)
            {
                Span<byte> lastSpan = _buffer.AsSpan(0, _buffered);
                lastSpan.Clear();
            }
            else
            {
                _memory = _output.GetMemory();
            }

            _buffered = 0;
            BytesCommitted = 0;
        }
    }
}
