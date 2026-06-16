using System.Buffers;
using System.Text.Json;

namespace CurlyRpc;

public sealed partial class JsonRpc
{
    private sealed class PendingCall
    {
        public PendingCall(TaskCompletionSource<JsonElement> completion, string method)
        {
            Completion = completion;
            Method = method;
        }

        public TaskCompletionSource<JsonElement> Completion { get; }

        public string Method { get; }
    }

    // ---- Outbound -----------------------------------------------------------

    private async Task<JsonElement> InvokeCoreAsync(string method, RawJsonValue? @params, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long id = Interlocked.Increment(ref _nextId);
        using System.Diagnostics.Activity? activity = JsonRpcDiagnostics.StartClientActivity(method, id);
        long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var metricTags = new System.Diagnostics.TagList
        {
            { "rpc.system", JsonRpcDiagnostics.RpcSystem },
            { "rpc.method", method },
        };
        JsonRpcDiagnostics.ClientCallsInFlight.Add(1, metricTags);

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCall(completion, method);

        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(
                static state =>
                {
                    var (self, callId) = ((JsonRpc, long))state!;
                    self.CancelOutbound(callId);
                },
                (this, id));
        }

        try
        {
            try
            {
                var wire = new JsonRpcRequestWire { Id = new RequestId(id), Method = method, Params = @params };
                CaptureOutboundTraceContext(out string? traceParent, out string? traceState);
                wire.TraceParent = traceParent;
                wire.TraceState = traceState;
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wire, JsonRpcWireContext.Default.JsonRpcRequestWire);
                await _handler.WriteMessageAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(id, out var failed))
                {
                    failed.Completion.TrySetException(ex);
                }

                registration.Dispose();
                throw;
            }

            try
            {
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }
        catch (Exception ex)
        {
            JsonRpcDiagnostics.SetError(activity, ex);
            throw;
        }
        finally
        {
            JsonRpcDiagnostics.ClientCallsInFlight.Add(-1, metricTags);
            JsonRpcDiagnostics.ClientCallDuration.Record(JsonRpcDiagnostics.ElapsedMilliseconds(startTimestamp), metricTags);
        }
    }

    private void CancelOutbound(long id)
    {
        if (_pending.TryRemove(id, out var call))
        {
            call.Completion.TrySetCanceled();
            _ = SendCancellationAsync(id);
        }
    }

    private async Task SendCancellationAsync(long id)
    {
        try
        {
            RawJsonValue idParams = BuildIdParameters(id);
            var wire = new JsonRpcNotificationWire { Method = _cancellationMethodName, Params = idParams };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wire, JsonRpcWireContext.Default.JsonRpcNotificationWire);
            await _handler.WriteMessageAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the connection may already be gone.
        }
    }

    private ValueTask SendNotificationAsync(string method, RawJsonValue? @params, bool propagateTraceContext = false)
    {
        var wire = new JsonRpcNotificationWire { Method = method, Params = @params };
        if (propagateTraceContext)
        {
            CaptureOutboundTraceContext(out string? traceParent, out string? traceState);
            wire.TraceParent = traceParent;
            wire.TraceState = traceState;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wire, JsonRpcWireContext.Default.JsonRpcNotificationWire);
        return _handler.WriteMessageAsync(bytes, CancellationToken.None);
    }

    // Captures the ambient Activity's W3C trace context for injection into an outbound envelope. Self-guards
    // on the PropagateTraceContext option and on the W3C id format (the legacy hierarchical format has no
    // traceparent representation), so the caller can invoke it unconditionally.
    private void CaptureOutboundTraceContext(out string? traceParent, out string? traceState)
    {
        traceParent = null;
        traceState = null;
        if (!_propagateTraceContext)
        {
            return;
        }

        if (System.Diagnostics.Activity.Current is { IdFormat: System.Diagnostics.ActivityIdFormat.W3C, Id: { } id })
        {
            traceParent = id;
            string? state = System.Diagnostics.Activity.Current.TraceStateString;
            traceState = string.IsNullOrEmpty(state) ? null : state;
        }
    }

    // Extracts the W3C trace context an upstream peer placed on an inbound envelope. Self-guards on the
    // PropagateTraceContext option so a connection that did not opt in ignores the members entirely.
    private void ReadInboundTraceContext(JsonElement message, out string? traceParent, out string? traceState)
    {
        traceParent = null;
        traceState = null;
        if (!_propagateTraceContext)
        {
            return;
        }

        if (message.TryGetProperty("traceparent", out JsonElement traceParentElement)
            && traceParentElement.ValueKind == JsonValueKind.String)
        {
            traceParent = traceParentElement.GetString();
        }

        if (message.TryGetProperty("tracestate", out JsonElement traceStateElement)
            && traceStateElement.ValueKind == JsonValueKind.String)
        {
            traceState = traceStateElement.GetString();
        }
    }

    // ---- Keep-alive ---------------------------------------------------------

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_keepAliveInterval, cancellationToken).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_keepAliveTimeout);

                try
                {
                    await InvokeCoreAsync(KeepAlivePingMethodName, null, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (RemoteInvocationException)
                {
                    // Any response — including an error such as "method not found" — proves the peer is
                    // alive and processing messages, so the remote end need not implement $/ping.
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // The peer did not respond within the timeout: treat the connection as lost. Fault
                    // Completion first (so callers see why) then tear the transport down.
                    Shutdown(new ConnectionLostException(
                        "The connection was closed because the peer did not respond to a keep-alive ping within the configured timeout."));
                    RequestClose();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection is shutting down.
        }
        catch
        {
            // The transport is already gone (a write failed or the handler was disposed); the read loop
            // reports the underlying failure through Completion.
        }
    }

    // ---- Read loop & inbound dispatch --------------------------------------

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadOnlyMemory<byte>? message;
                try
                {
                    message = await _handler.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (message is null)
                {
                    break;
                }

                HandleMessage(message.Value);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            Shutdown(failure);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8);
        }
        catch (JsonException)
        {
            _ = SendErrorAsync(RequestId.Null, JsonRpcErrorCodes.ParseError, "Parse error.");
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    HandleSingleMessage(root);
                    break;

                case JsonValueKind.Array:
                    HandleBatchMessage(root);
                    break;

                default:
                    // Per the spec, anything that is neither an Object nor a non-empty Array is an
                    // Invalid Request and must be answered with a single error response (id: null).
                    _ = SendErrorAsync(RequestId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.");
                    break;
            }
        }
    }

    private void HandleBatchMessage(JsonElement array)
    {
        // An empty batch is not a valid Array of requests; reply with a single error object,
        // not an array (JSON-RPC 2.0 §6).
        if (array.GetArrayLength() == 0)
        {
            _ = SendErrorAsync(RequestId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.");
            return;
        }

        // Detach the elements from the backing document so processing can outlive its disposal.
        var elements = new List<JsonElement>(array.GetArrayLength());
        foreach (JsonElement element in array.EnumerateArray())
        {
            elements.Add(element.Clone());
        }

        _ = HandleBatchAsync(elements);
    }

    private async Task HandleBatchAsync(List<JsonElement> elements)
    {
        var responses = new List<byte[]>(elements.Count);

        foreach (JsonElement element in elements)
        {
            await DispatchBatchElementAsync(element, responses).ConfigureAwait(false);
        }

        // A batch made up entirely of notifications (and/or responses) yields no reply at all.
        if (responses.Count == 0)
        {
            return;
        }

        byte[] frame = AssembleBatchArray(responses);
        try
        {
            await _handler.WriteMessageAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // The connection was torn down while assembling the batch response.
        }
    }

    private Task DispatchBatchElementAsync(JsonElement message, List<byte[]> responses)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            responses.Add(SerializeError(RequestId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.", null));
            return Task.CompletedTask;
        }

        bool hasMethod = message.TryGetProperty("method", out JsonElement methodElement)
            && methodElement.ValueKind == JsonValueKind.String;
        bool hasId = message.TryGetProperty("id", out JsonElement idElement);

        if (hasMethod)
        {
            string method = methodElement.GetString()!;
            JsonElement? @params = message.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement.Clone()
                : null;

            if (!hasId)
            {
                if (string.Equals(method, _cancellationMethodName, StringComparison.Ordinal))
                {
                    HandleCancellationNotification(@params);
                    return Task.CompletedTask;
                }

                ReadInboundTraceContext(message, out string? ntp, out string? nts);
                return DispatchRequestAsync(RequestId.Null, method, @params, isNotification: true, responses, ntp, nts);
            }

            ReadInboundTraceContext(message, out string? tp, out string? ts);
            return DispatchRequestAsync(ReadRequestId(idElement), method, @params, isNotification: false, responses, tp, ts);
        }

        if (hasId)
        {
            // A response object carried inside a batch is correlated to a pending call; it never
            // contributes to the batch reply.
            HandleResponse(message, idElement);
            return Task.CompletedTask;
        }

        responses.Add(SerializeError(RequestId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.", null));
        return Task.CompletedTask;
    }

    private static byte[] AssembleBatchArray(List<byte[]> responses)
    {
        long size = 2 + (responses.Count - 1);
        foreach (byte[] response in responses)
        {
            size += response.Length;
        }

        var result = new byte[size];
        int position = 0;
        result[position++] = (byte)'[';
        for (int i = 0; i < responses.Count; i++)
        {
            if (i > 0)
            {
                result[position++] = (byte)',';
            }

            Buffer.BlockCopy(responses[i], 0, result, position, responses[i].Length);
            position += responses[i].Length;
        }

        result[position] = (byte)']';
        return result;
    }

    private void HandleSingleMessage(JsonElement message)
    {
        bool hasMethod = message.TryGetProperty("method", out JsonElement methodElement)
            && methodElement.ValueKind == JsonValueKind.String;
        bool hasId = message.TryGetProperty("id", out JsonElement idElement);

        if (hasMethod)
        {
            string method = methodElement.GetString()!;
            JsonElement? @params = message.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement.Clone()
                : null;

            if (!hasId)
            {
                if (string.Equals(method, _cancellationMethodName, StringComparison.Ordinal))
                {
                    HandleCancellationNotification(@params);
                    return;
                }

                ReadInboundTraceContext(message, out string? ntp, out string? nts);
                _ = DispatchRequestAsync(RequestId.Null, method, @params, isNotification: true, traceParent: ntp, traceState: nts);
            }
            else
            {
                // A request with an explicit "id": null is still a request and must receive a
                // response with "id": null; only the absence of the id member denotes a notification.
                ReadInboundTraceContext(message, out string? tp, out string? ts);
                _ = DispatchRequestAsync(ReadRequestId(idElement), method, @params, isNotification: false, traceParent: tp, traceState: ts);
            }

            return;
        }

        if (!hasId)
        {
            return;
        }

        HandleResponse(message, idElement);
    }

    private void HandleResponse(JsonElement message, JsonElement idElement)
    {
        RequestId id = ReadRequestId(idElement);
        if (id.Number is not long key || !_pending.TryRemove(key, out PendingCall? call))
        {
            return;
        }

        if (message.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            call.Completion.TrySetException(CreateRemoteException(call.Method, errorElement));
        }
        else if (message.TryGetProperty("result", out JsonElement resultElement))
        {
            call.Completion.TrySetResult(resultElement.Clone());
        }
        else
        {
            call.Completion.TrySetResult(default);
        }
    }

    private async Task DispatchRequestAsync(RequestId id, string method, JsonElement? @params, bool isNotification, List<byte[]>? batch = null, string? traceParent = null, string? traceState = null)
    {
        // Built-in liveness probe: answer immediately and cheaply, ahead of middleware and throttling,
        // so keep-alive works regardless of authentication state or concurrency pressure.
        if (string.Equals(method, KeepAlivePingMethodName, StringComparison.Ordinal))
        {
            if (!isNotification)
            {
                await SendResultAsync(id, null, batch).ConfigureAwait(false);
            }

            return;
        }

        if (_inboundThrottle is not null)
        {
            try
            {
                await _inboundThrottle.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // The connection is shutting down.
            }
        }

        CancellationTokenSource? cts = null;
        if (!isNotification && !id.IsNull)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _inboundCancellations[id] = cts;
        }

        CancellationToken token = cts?.Token ?? _disposeCts.Token;

        System.Diagnostics.Activity? activity = null;
        long startTimestamp = 0;
        System.Diagnostics.TagList metricTags = default;
        bool instrumented = false;

        try
        {
            if (_inboundMiddleware is not null)
            {
                JsonRpcDispatchDecision decision = await _inboundMiddleware
                    .OnRequestAsync(new JsonRpcRequestContext(this, method, @params, isNotification), token)
                    .ConfigureAwait(false);

                switch (decision.Kind)
                {
                    case JsonRpcDispatchDecision.DecisionKind.Respond:
                        if (!isNotification)
                        {
                            await SendResultAsync(id, decision.Result, batch).ConfigureAwait(false);
                        }

                        return;

                    case JsonRpcDispatchDecision.DecisionKind.Reject:
                        JsonRpcDiagnostics.SetError(activity, decision.ErrorCode, decision.Message);
                        if (!isNotification)
                        {
                            await SendErrorAsync(id, decision.ErrorCode, decision.Message ?? "Request rejected.", decision.ErrorData, batch).ConfigureAwait(false);
                        }

                        if (decision.CloseConnection)
                        {
                            RequestClose();
                        }

                        return;
                }
            }

            if (string.Equals(method, EnumeratorNextMethodName, StringComparison.Ordinal))
            {
                await HandleEnumeratorNextAsync(id, @params, isNotification).ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, EnumeratorAbortMethodName, StringComparison.Ordinal))
            {
                await HandleEnumeratorAbortAsync(@params).ConfigureAwait(false);
                return;
            }

            instrumented = true;
            activity = JsonRpcDiagnostics.StartServerActivity(method, id, traceParent, traceState);
            startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            metricTags = new System.Diagnostics.TagList
            {
                { "rpc.system", JsonRpcDiagnostics.RpcSystem },
                { "rpc.method", method },
            };
            JsonRpcDiagnostics.ServerCallsInFlight.Add(1, metricTags);

            RpcMethodEntry? entry;
            lock (_gate)
            {
                _methods.TryGetValue(method, out entry);
            }

            if (entry is null)
            {
                JsonRpcDiagnostics.SetError(activity, JsonRpcErrorCodes.MethodNotFound, "Method not found.");
                if (!isNotification)
                {
                    await SendErrorAsync(id, JsonRpcErrorCodes.MethodNotFound, $"Method '{method}' was not found.", batch: batch).ConfigureAwait(false);
                }

                return;
            }

            object? result = await entry.InvokeAsync(@params, _serializerOptions, token).ConfigureAwait(false);

            if (isNotification)
            {
                if (result is RpcEnumerableResult orphan)
                {
                    await orphan.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }

            if (result is RpcEnumerableResult enumerable)
            {
                await SendEnumerableStartAsync(id, enumerable).ConfigureAwait(false);
            }
            else
            {
                await SendResultAsync(id, result, batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cts is not null && cts.IsCancellationRequested)
        {
            JsonRpcDiagnostics.SetError(activity, RequestCanceledCode, "Request was canceled.");
            if (!isNotification)
            {
                await SendErrorAsync(id, RequestCanceledCode, "Request was canceled.", batch: batch).ConfigureAwait(false);
            }
        }
        catch (LocalRpcException ex)
        {
            JsonRpcDiagnostics.SetError(activity, ex.ErrorCode, ex.Message);
            if (!isNotification)
            {
                await SendErrorAsync(id, ex.ErrorCode, ex.Message, ex.ErrorData, batch).ConfigureAwait(false);
            }
        }
        catch (RpcInvalidParametersException ex)
        {
            JsonRpcDiagnostics.SetError(activity, JsonRpcErrorCodes.InvalidParams, ex.Message);
            if (!isNotification)
            {
                await SendErrorAsync(id, JsonRpcErrorCodes.InvalidParams, ex.Message, batch: batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            JsonRpcDiagnostics.SetError(activity, ex);
            if (!isNotification)
            {
                string message = _exposeExceptionDetails ? ex.Message : "An internal error occurred.";
                await SendErrorAsync(id, JsonRpcErrorCodes.InternalError, message, batch: batch).ConfigureAwait(false);
            }
        }
        finally
        {
            _inboundThrottle?.Release();

            if (instrumented)
            {
                JsonRpcDiagnostics.ServerCallsInFlight.Add(-1, metricTags);
                JsonRpcDiagnostics.ServerCallDuration.Record(JsonRpcDiagnostics.ElapsedMilliseconds(startTimestamp), metricTags);
                activity?.Dispose();
            }

            if (cts is not null)
            {
                _inboundCancellations.TryRemove(id, out _);
                cts.Dispose();
            }
        }
    }

    private void HandleCancellationNotification(JsonElement? @params)
    {
        if (@params is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("id", out JsonElement idElement))
        {
            return;
        }

        RequestId id = ReadRequestId(idElement);
        if (!id.IsNull && _inboundCancellations.TryGetValue(id, out CancellationTokenSource? cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    // ---- Response serialization --------------------------------------------

    private Task SendResultAsync(RequestId id, object? result, List<byte[]>? batch = null)
    {
        var wire = new JsonRpcResultWire { Id = id, Result = SerializeResult(result) };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wire, JsonRpcWireContext.Default.JsonRpcResultWire);
        return EmitAsync(bytes, batch);
    }

    private Task SendErrorAsync(RequestId id, int code, string message, object? data = null, List<byte[]>? batch = null)
    {
        byte[] bytes = SerializeError(id, code, message, data);
        return EmitAsync(bytes, batch);
    }

    private byte[] SerializeError(RequestId id, int code, string message, object? data)
    {
        var detail = new JsonRpcErrorDetail
        {
            Code = code,
            Message = message,
            Data = data is null ? null : SerializeToRaw(data),
        };

        var wire = new JsonRpcErrorWire { Id = id, Error = detail };
        return JsonSerializer.SerializeToUtf8Bytes(wire, JsonRpcWireContext.Default.JsonRpcErrorWire);
    }

    private Task EmitAsync(byte[] bytes, List<byte[]>? batch)
    {
        // Within a batch, responses are buffered and flushed together as one array frame; otherwise
        // each response is written immediately as its own frame.
        if (batch is not null)
        {
            batch.Add(bytes);
            return Task.CompletedTask;
        }

        return _handler.WriteMessageAsync(bytes, CancellationToken.None).AsTask();
    }

    private Exception CreateRemoteException(string method, JsonElement errorElement)
    {
        int code = errorElement.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt32(out int parsedCode)
            ? parsedCode
            : JsonRpcErrorCodes.InternalError;

        string message = errorElement.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()!
            : "The remote peer returned an error.";

        JsonElement? data = errorElement.TryGetProperty("data", out JsonElement dataElement)
            ? dataElement.Clone()
            : null;

        if (code == JsonRpcErrorCodes.MethodNotFound)
        {
            return new RemoteMethodNotFoundException(message, method);
        }

        return new RemoteInvocationException(message, code, data);
    }

    // ---- Serialization helpers ---------------------------------------------

    private RawJsonValue? SerializePositionalParameters(object?[]? arguments)
    {
        if (arguments is null || arguments.Length == 0)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (object? argument in arguments)
            {
                if (argument is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, argument, _serializerOptions.GetTypeInfo(argument.GetType()));
                }
            }

            writer.WriteEndArray();
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }

    private RawJsonValue? SerializeParameterObject(object? argument)
        => argument is null ? null : SerializeToRaw(argument);

    private RawJsonValue? SerializeResult(object? result)
        => result is null ? null : SerializeToRaw(result);

    private RawJsonValue SerializeToRaw(object value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, value, _serializerOptions.GetTypeInfo(value.GetType()));
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }

    private TResult? DeserializeResult<TResult>(JsonElement result)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return (TResult?)result.Deserialize(_serializerOptions.GetTypeInfo(typeof(TResult)));
    }

    private static RawJsonValue BuildIdParameters(long id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteEndObject();
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }

    private static RequestId ReadRequestId(JsonElement idElement)
    {
        switch (idElement.ValueKind)
        {
            case JsonValueKind.Number:
                return idElement.TryGetInt64(out long number)
                    ? new RequestId(number)
                    : new RequestId(idElement.GetRawText());

            case JsonValueKind.String:
                return new RequestId(idElement.GetString()!);

            default:
                return RequestId.Null;
        }
    }

    /// <summary>
    /// Initiates an orderly close of the connection (used by middleware rejecting with close). The
    /// read loop is stopped and, once it has unwound, the transport handler is disposed (honoring
    /// <see cref="JsonRpcOptions.DisposeHandlerOnDispose"/>) so the underlying connection is actually
    /// torn down rather than merely left idle.
    /// </summary>
    private void RequestClose()
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) == 1)
        {
            return;
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = CloseTransportAsync();
    }

    private async Task CloseTransportAsync()
    {
        Task? readLoop = _readLoopTask;
        if (readLoop is not null)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch
            {
                // The read loop surfaces failures through Completion; closing must not throw.
            }
        }

        if (_options.DisposeHandlerOnDispose)
        {
            try
            {
                await _handler.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the handler may already be disposed or the transport already gone.
            }
        }
    }

    private void Shutdown(Exception? failure)
    {        if (Interlocked.Exchange(ref _shutdownFlag, 1) == 1)
        {
            return;
        }

        Exception? completionError = failure switch
        {
            null => null,
            OperationCanceledException when _disposeCts.IsCancellationRequested => null,
            _ => failure,
        };

        foreach (KeyValuePair<long, PendingCall> pending in _pending)
        {
            pending.Value.Completion.TrySetException(new ConnectionLostException(
                "The JSON-RPC connection was lost before the request completed.", completionError));
        }

        _pending.Clear();

        foreach (KeyValuePair<RequestId, CancellationTokenSource> inbound in _inboundCancellations)
        {
            try
            {
                inbound.Value.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            inbound.Value.Dispose();
        }

        _inboundCancellations.Clear();

        foreach (KeyValuePair<long, RpcEnumerableResult> enumerator in _enumerators)
        {
            if (_enumerators.TryRemove(enumerator.Key, out RpcEnumerableResult? enumerable))
            {
                _ = DisposeEnumeratorAsync(enumerable);
            }
        }

        _enumerators.Clear();

        if (completionError is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(completionError);
        }
    }

    private static async Task DisposeEnumeratorAsync(RpcEnumerableResult enumerable)
    {
        try
        {
            await enumerable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the underlying enumerator may already be faulted or the connection gone.
        }
    }

    /// <summary>JSON-RPC error code used when an inbound request is canceled (LSP/JSON-RPC cancellation convention).</summary>
    private const int RequestCanceledCode = -32800;
}
