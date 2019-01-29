// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http
{
    public class Http1OutputProducer : IHttpOutputProducer, IHttpOutputAborter, IDisposable
    {
        private static readonly ReadOnlyMemory<byte> _continueBytes = new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
        private static readonly byte[] _bytesHttpVersion11 = Encoding.ASCII.GetBytes("HTTP/1.1 ");
        private static readonly byte[] _bytesEndHeaders = Encoding.ASCII.GetBytes("\r\n\r\n");
        private static readonly ReadOnlyMemory<byte> _endChunkedResponseBytes = new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes("0\r\n\r\n"));

        private readonly string _connectionId;
        private readonly ConnectionContext _connectionContext;
        private readonly IKestrelTrace _log;
        private readonly IHttpMinResponseDataRateFeature _minResponseDataRateFeature;
        private readonly TimingPipeFlusher _flusher;

        // This locks access to all of the below fields
        private readonly object _contextLock = new object();

        private bool _completed = false;
        private bool _aborted;
        private long _unflushedBytes;
        private bool _autoChunk;
        private readonly PipeWriter _pipeWriter;

        // For chunked responses
        private int _advancedBytesForChunk;
        private Memory<byte> _currentChunkMemory;

        public Http1OutputProducer(
            PipeWriter pipeWriter,
            string connectionId,
            ConnectionContext connectionContext,
            IKestrelTrace log,
            ITimeoutControl timeoutControl,
            IHttpMinResponseDataRateFeature minResponseDataRateFeature)
        {
            _pipeWriter = pipeWriter;
            _connectionId = connectionId;
            _connectionContext = connectionContext;
            _log = log;
            _minResponseDataRateFeature = minResponseDataRateFeature;
            _flusher = new TimingPipeFlusher(pipeWriter, timeoutControl, log);
        }

        public Task WriteDataAsync(ReadOnlySpan<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            return WriteAsync(buffer, cancellationToken).AsTask();
        }

        public ValueTask<FlushResult> WriteDataToPipeAsync(ReadOnlySpan<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<FlushResult>(Task.FromCanceled<FlushResult>(cancellationToken));
            }

            return WriteAsync(buffer, cancellationToken);
        }

        public ValueTask<FlushResult> WriteStreamSuffixAsync()
        {
            return WriteAsync(_endChunkedResponseBytes.Span);
        }

        public ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync(Constants.EmptyData, cancellationToken);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_autoChunk)
            {
                if (_advancedBytesForChunk == 4089)
                {
                    // Chunk is completely done
                    WriteChunkedFromPipe();
                }
                _currentChunkMemory = _pipeWriter.GetMemory(sizeHint);
                var actualMemory = _currentChunkMemory.Slice(5 + _advancedBytesForChunk, _currentChunkMemory.Length - 5 - 2 - _advancedBytesForChunk);

                return actualMemory;
            }
            else
            {
                return _pipeWriter.GetMemory(sizeHint);
            }
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (_autoChunk)
            {
                if (_advancedBytesForChunk == 4089)
                {
                    // Chunk is completely done
                    WriteChunkedFromPipe();
                }
                _currentChunkMemory = _pipeWriter.GetMemory(sizeHint);
                var actualMemory = _currentChunkMemory.Slice(5 + _advancedBytesForChunk, _currentChunkMemory.Length - 5 - 2 - _advancedBytesForChunk);

                return actualMemory.Span;
            }
            else
            {
                return _pipeWriter.GetMemory(sizeHint).Span;
            }
        }

        public void Advance(int bytes)
        {
            if (_autoChunk)
            {
                _advancedBytesForChunk += bytes;
            }
            else
            {
                _pipeWriter.Advance(bytes);
            }
        }

        public void CancelPendingFlush()
        {
            _pipeWriter.CancelPendingFlush();
        }

        public ValueTask<FlushResult> WriteAsync<T>(Func<PipeWriter, T, long> callback, T state, CancellationToken cancellationToken)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return default;
                }

                var buffer = _pipeWriter;
                var bytesCommitted = callback(buffer, state);
                _unflushedBytes += bytesCommitted;
            }

            return FlushAsync(cancellationToken);
        }

        public void WriteResponseHeaders(int statusCode, string reasonPhrase, HttpResponseHeaders responseHeaders, bool autoChunk)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return;
                }

                var buffer = _pipeWriter;
                var writer = new BufferWriter<PipeWriter>(buffer);

                writer.Write(_bytesHttpVersion11);
                var statusBytes = ReasonPhrases.ToStatusBytes(statusCode, reasonPhrase);
                writer.Write(statusBytes);
                responseHeaders.CopyTo(ref writer);
                writer.Write(_bytesEndHeaders);

                writer.Commit();

                _unflushedBytes += writer.BytesCommitted;
                _autoChunk = autoChunk;
            }
        }

        public void Dispose()
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return;
                }

                _log.ConnectionDisconnect(_connectionId);
                _completed = true;
                _pipeWriter.Complete();
            }
        }

        public void Abort(ConnectionAbortedException error)
        {
            // Abort can be called after Dispose if there's a flush timeout.
            // It's important to still call _lifetimeFeature.Abort() in this case.
            lock (_contextLock)
            {
                if (_aborted)
                {
                    return;
                }

                _aborted = true;
                _connectionContext.Abort(error);
                Dispose();
            }
        }

        public ValueTask<FlushResult>  Write100ContinueAsync()
        {
            return WriteAsync(_continueBytes.Span);
        }

        private ValueTask<FlushResult> WriteAsync(
            ReadOnlySpan<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return default;
                }

                if (_autoChunk)
                {
                    // If there is data that was chunked before writing (ex someone did GetMemory->Advance->WriteAsync)
                    // make sure to write whatever was advanced first
                    WriteChunkedFromPipe();
                }

                var writer = new BufferWriter<PipeWriter>(_pipeWriter);
                if (buffer.Length > 0)
                {
                    writer.Write(buffer);

                    _unflushedBytes += buffer.Length;
                }
                writer.Commit();

                var bytesWritten = _unflushedBytes;
                _unflushedBytes = 0;

                return _flusher.FlushAsync(
                    _minResponseDataRateFeature.MinDataRate,
                    bytesWritten,
                    this,
                    cancellationToken);
            }
        }

        // TODO comments here as this is pretty complex
        private void WriteChunkedFromPipe()
        {
            var writer = new BufferWriter<PipeWriter>(_pipeWriter);

            Debug.Assert(_advancedBytesForChunk < 4096);

            if (_advancedBytesForChunk > 0)
            {
                var count = writer.WriteBeginChunkBytes(_advancedBytesForChunk);
                if (count < 5)
                {
                    _currentChunkMemory.Slice(5, _advancedBytesForChunk).CopyTo(_currentChunkMemory.Slice(count));
                }

                writer.Write(_currentChunkMemory.Slice(count, _advancedBytesForChunk).Span);
                writer.WriteEndChunkBytes();
                writer.Commit();
            }
            _advancedBytesForChunk = 0;
        }
    }
}
