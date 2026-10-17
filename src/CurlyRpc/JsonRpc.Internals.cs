using System.Buffers;
using System.Text.Json;

namespace CurlyRpc;

public sealed partial class JsonRpc
{
    /// <summary>Set once a middleware decision asked to close the connection, before its reply is written.</summary>
    private int _dispatchClosed;

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

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
            // Shutdown sets its flag before failing every pending call. Re-check after publishing
            // this call so one racing with (or following) connection loss cannot wait forever.
            if (Interlocked.CompareExchange(ref _shutdownFlag, 0, 0) != 0)
            {
                _pending.TryRemove(id, out _);
                registration.Dispose();
                completion.TrySetException(new ConnectionLostException(
                    "The JSON-RPC connection was lost before the request was sent."));
                return await completion.Task.ConfigureAwait(false);
            }

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

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // Race the whole ping, including the write, against the timeout: a transport whose
                // write never completes (and ignores cancellation) is exactly what keep-alive detects.
                Task ping = Task.Run(() => InvokeCoreAsync(KeepAlivePingMethodName, null, pingCts.Token), CancellationToken.None);
                try
                {
                    await ping.WaitAsync(_keepAliveTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonRpcException ex) when (ex is not ConnectionLostException)
                {
                    // Any reply, including an error such as "method not found" or even a malformed
                    // error object, proves the peer is alive, so the remote end need not implement $/ping.
                }
                catch (TimeoutException)
                {
                    CancelQuietly(pingCts);
                    ObserveFailure(ping);

                    // The peer did not respond within the timeout: treat the connection as lost. Fault
                    // Completion first (so callers see why) then tear the transport down.
                    Shutdown(new ConnectionLostException(
                        "The connection was closed because the peer did not respond to a keep-alive ping within the configured timeout."));
                    RequestClose();
                    return;
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The connection is shutting down.
        }
        catch (ConnectionLostException)
        {
            // The read loop (or another teardown path) already reported why the connection ended.
        }
        catch (Exception ex)
        {
            // An unexpected failure (for example a write error) means liveness can no longer be
            // proven: fault the connection instead of silently disabling keep-alive.
            Shutdown(new ConnectionLostException("The keep-alive ping failed.", ex));
            RequestClose();
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
            if (failure is not null)
            {
                // A framing or protocol failure leaves the stream in an unknown state: close the
                // transport so the peer observes end-of-stream and in-flight handlers are canceled.
                RequestClose();
            }
        }
    }

    private void ReserveInbound(int count, int bytes)
    {
        lock (_gate)
        {
            if ((_maximumPendingInboundRequests == 0 || count <= _maximumPendingInboundRequests - _pendingInboundRequests)
                && (_maximumPendingInboundBytes == 0 || bytes <= _maximumPendingInboundBytes - _pendingInboundBytes))
            {
                _pendingInboundRequests += count;
                _pendingInboundBytes += bytes;
                return;
            }
        }

        // Shutdown can invoke application cancellation callbacks; never hold the admission lock.
        var error = new ConnectionLostException("The pending inbound request limit was exceeded.");
        Shutdown(error);
        RequestClose();
        throw error;
    }

    // Only messages which cannot generate output bypass the budget. In particular ping replies
    // must reserve admission too, otherwise a non-reading peer can accumulate unlimited writes.
    private bool IsImmediateMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (message.TryGetProperty("method", out JsonElement method))
        {
            return IsValidRequestEnvelope(message)
                && !message.TryGetProperty("id", out _)
                && method.GetString() == _cancellationMethodName;
        }

        return message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _);
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
            ReserveInbound(1, utf8.Length);
            _ = HandleAdmittedMessageAsync(default, 1, utf8.Length);
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            int count = 0;
            int maximumBatchSize = _options.MaximumBatchSize;
            if (root.ValueKind == JsonValueKind.Array && maximumBatchSize > 0 && root.GetArrayLength() > maximumBatchSize)
            {
                // Reject the whole batch before looking at any element, with a single error reply.
                ReserveInbound(1, utf8.Length);
                _ = HandleAdmittedMessageAsync(default, 1, utf8.Length, batchTooLarge: true);
                return;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Correlate responses and cancellation before dispatching any batch handler. A
                // handler may itself be awaiting a response carried later in this same batch.
                foreach (JsonElement element in root.EnumerateArray())
                {
                    if (IsImmediateMessage(element))
                    {
                        _ = HandleSingleMessage(element);
                    }
                    else
                    {
                        count++;
                    }
                }

                if (root.GetArrayLength() == 0)
                {
                    count = 1;
                }
            }
            else if (IsImmediateMessage(root))
            {
                _ = HandleSingleMessage(root);
                return;
            }
            else
            {
                count = 1;
            }

            if (count == 0)
            {
                return;
            }

            // Admission precedes cloning, task creation and throttle waiting.
            ReserveInbound(count, utf8.Length);
            _ = HandleAdmittedMessageAsync(root.Clone(), count, utf8.Length);
        }
    }

    private async Task HandleAdmittedMessageAsync(JsonElement root, int count, int bytes, bool batchTooLarge = false)
    {
        try
        {
            switch (root.ValueKind)
            {
                case JsonValueKind.Undefined when batchTooLarge:
                    await SendErrorAsync(RequestId.Null, JsonRpcErrorCodes.InvalidRequest,
                        "Invalid Request: the batch exceeds the maximum batch size.").ConfigureAwait(false);
                    break;
                case JsonValueKind.Object:
                    await HandleSingleMessage(root).ConfigureAwait(false);
                    break;
                case JsonValueKind.Array when root.GetArrayLength() > 0:
                    var elements = new List<JsonElement>();
                    foreach (JsonElement element in root.EnumerateArray())
                    {
                        if (!IsImmediateMessage(element))
                        {
                            elements.Add(element);
                        }
                    }

                    await HandleBatchAsync(elements).ConfigureAwait(false);
                    break;
                default:
                    bool parseError = root.ValueKind == JsonValueKind.Undefined;
                    await SendErrorAsync(RequestId.Null,
                        parseError ? JsonRpcErrorCodes.ParseError : JsonRpcErrorCodes.InvalidRequest,
                        parseError ? "Parse error." : "Invalid Request.").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Shutdown(ex);
            RequestClose();
        }
        finally
        {
            lock (_gate)
            {
                _pendingInboundRequests -= count;
                _pendingInboundBytes -= bytes;
            }
        }
    }

    private async Task HandleBatchAsync(List<JsonElement> elements)
    {
        var responses = new List<byte[]>(elements.Count);

        foreach (JsonElement element in elements)
        {
            // Stop at the first close (for example a failed handshake): later elements must not run.
            if (IsClosing)
            {
                return;
            }

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
            await _handler.WriteMessageAsync(frame, _disposeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // The connection was torn down while assembling the batch response.
        }
    }

    private Task DispatchBatchElementAsync(JsonElement message, List<byte[]> responses)
        => DispatchMessageAsync(message, responses);

    private Task DispatchMessageAsync(JsonElement message, List<byte[]>? responses = null)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return SendErrorAsync(RequestId.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.", batch: responses);
        }

        bool hasMethod = message.TryGetProperty("method", out JsonElement methodElement);
        bool hasId = message.TryGetProperty("id", out JsonElement idElement);

        // A method member identifies a request even when its value is invalid. Conversely, an id
        // alone does not identify a response. Leave response-body validation to HandleResponse.
        if (!hasMethod && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
        {
            if (hasId)
            {
                HandleResponse(message, idElement);
            }

            return Task.CompletedTask;
        }

        bool validId = !hasId || idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null;
        RequestId id = hasId && validId ? ReadRequestId(idElement) : RequestId.Null;
        bool hasParams = message.TryGetProperty("params", out JsonElement paramsElement);
        if (!IsValidRequestEnvelope(message))
        {
            // Only a valid request without an id is a notification. Malformed envelopes must
            // receive Invalid Request even when no id was supplied.
            return SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.", batch: responses);
        }

        string method = methodElement.GetString()!;
        JsonElement? @params = hasParams ? paramsElement.Clone() : null;
        if (!hasId && string.Equals(method, _cancellationMethodName, StringComparison.Ordinal))
        {
            HandleCancellationNotification(@params);
            return Task.CompletedTask;
        }

        // An explicit null id still requires a response; only an absent id is a notification.
        ReadInboundTraceContext(message, out string? traceParent, out string? traceState);
        return DispatchRequestAsync(id, method, @params, isNotification: !hasId, responses, traceParent, traceState);
    }

    private static bool IsValidRequestEnvelope(JsonElement message)
        => message.TryGetProperty("jsonrpc", out JsonElement version)
            && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0"
            && message.TryGetProperty("method", out JsonElement method) && method.ValueKind == JsonValueKind.String
            && (!message.TryGetProperty("id", out JsonElement id)
                || id.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)
            && (!message.TryGetProperty("params", out JsonElement parameters)
                || parameters.ValueKind is JsonValueKind.Object or JsonValueKind.Array);

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

    private Task HandleSingleMessage(JsonElement message)
        => DispatchMessageAsync(message);

    private void HandleResponse(JsonElement message, JsonElement idElement)
    {
        RequestId id = ReadRequestId(idElement);
        if (id.Number is not long key || !_pending.TryRemove(key, out PendingCall? call))
        {
            return;
        }

        // Once removed, this call is no longer covered by connection teardown. Every path
        // below must settle it, including failures while validating or copying the response.
        try
        {
            bool hasError = message.TryGetProperty("error", out JsonElement errorElement);
            bool hasResult = message.TryGetProperty("result", out JsonElement resultElement);
            if (hasError == hasResult)
            {
                throw new JsonRpcException("Invalid JSON-RPC response: expected exactly one of 'result' or 'error'.");
            }

            if (hasError)
            {
                call.Completion.TrySetException(CreateRemoteException(call.Method, errorElement));
            }
            else
            {
                call.Completion.TrySetResult(resultElement.Clone());
            }
        }
        catch (Exception ex)
        {
            call.Completion.TrySetException(ex);
        }
    }

    private async Task DispatchRequestAsync(RequestId id, string method, JsonElement? @params, bool isNotification, List<byte[]>? batch = null, string? traceParent = null, string? traceState = null)
    {
        if (IsClosing)
        {
            return;
        }

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

        // Every request gets its own token source so a streaming result can take ownership of it.
        // It is registered for $/cancelRequest before throttling so a queued request can be canceled.
        CancellationTokenSource? cts = null;
        bool registered = false;
        if (!isNotification)
        {
            try
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            }
            catch (ObjectDisposedException)
            {
                return; // The connection was disposed.
            }

            if (!id.IsNull)
            {
                if (!_inboundCancellations.TryAdd(id, cts))
                {
                    cts.Dispose();
                    await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest,
                        "Invalid Request: a request with the same id is already in progress.", batch: batch).ConfigureAwait(false);
                    return;
                }

                registered = true;
            }
        }

        CancellationToken token = cts?.Token ?? _disposeCts.Token;
        bool throttled = false;
        bool ownershipTransferred = false;

        System.Diagnostics.Activity? activity = null;
        long startTimestamp = 0;
        System.Diagnostics.TagList metricTags = default;
        bool instrumented = false;

        try
        {
            if (_inboundThrottle is not null)
            {
                await _inboundThrottle.WaitAsync(token).ConfigureAwait(false);
                throttled = true;
            }

            if (IsClosing)
            {
                return;
            }

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
                        if (decision.CloseConnection)
                        {
                            // Latch the close before replying so nothing queued behind this request,
                            // including the rest of its batch, is dispatched while the reply is written.
                            Volatile.Write(ref _dispatchClosed, 1);
                        }

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

            // A close may have been requested while the middleware ran (for example by a failed
            // handshake elsewhere in the same batch); never invoke a handler after that.
            if (IsClosing)
            {
                return;
            }

            if (string.Equals(method, EnumeratorNextMethodName, StringComparison.Ordinal))
            {
                await HandleEnumeratorNextAsync(id, @params, isNotification, batch).ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, EnumeratorAbortMethodName, StringComparison.Ordinal))
            {
                await HandleEnumeratorAbortAsync(@params).ConfigureAwait(false);
                return;
            }

            RpcMethodEntry? entry;
            lock (_gate)
            {
                _methods.TryGetValue(method, out entry);
            }

            // Telemetry names come from the registered method table only. An unknown name is
            // attacker-controlled and would otherwise create unbounded span names and metric series.
            string telemetryMethod = entry is null ? JsonRpcDiagnostics.OtherMethodName : method;
            instrumented = true;
            activity = JsonRpcDiagnostics.StartServerActivity(telemetryMethod, id, traceParent, traceState);
            startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            metricTags = new System.Diagnostics.TagList
            {
                { "rpc.system", JsonRpcDiagnostics.RpcSystem },
                { "rpc.method", telemetryMethod },
            };
            JsonRpcDiagnostics.ServerCallsInFlight.Add(1, metricTags);

            if (entry is null)
            {
                JsonRpcDiagnostics.SetError(activity, JsonRpcErrorCodes.MethodNotFound, "Method not found.");
                if (!isNotification)
                {
                    await SendErrorAsync(id, JsonRpcErrorCodes.MethodNotFound, $"Method '{TruncateMethodName(method)}' was not found.", batch: batch).ConfigureAwait(false);
                }

                return;
            }

            object? result = await entry.InvokeAsync(@params, _serializerOptions, token).ConfigureAwait(false);

            if (result is RpcEnumerableResult streaming && cts is not null)
            {
                // The iterator captured this request's token, so the enumeration owns its source from
                // here on: it is canceled and disposed with the enumerator, not with the start request.
                if (registered)
                {
                    _inboundCancellations.TryRemove(new KeyValuePair<RequestId, CancellationTokenSource>(id, cts));
                    registered = false;
                }

                streaming.AttachCancellation(cts);
                ownershipTransferred = true;
            }

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
                await SendEnumerableStartAsync(id, enumerable, batch).ConfigureAwait(false);
            }
            else
            {
                await SendResultAsync(id, result, batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            // Closing cancels pending response writes; do not enqueue another error reply.
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
            if (throttled)
            {
                _inboundThrottle!.Release();
            }

            if (instrumented)
            {
                JsonRpcDiagnostics.ServerCallsInFlight.Add(-1, metricTags);
                JsonRpcDiagnostics.ServerCallDuration.Record(JsonRpcDiagnostics.ElapsedMilliseconds(startTimestamp), metricTags);
                activity?.Dispose();
            }

            if (cts is not null)
            {
                if (registered)
                {
                    // Remove only our own registration; never a later request that reused the id.
                    _inboundCancellations.TryRemove(new KeyValuePair<RequestId, CancellationTokenSource>(id, cts));
                }

                if (!ownershipTransferred)
                {
                    cts.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Whether the connection has stopped (or been asked to stop) dispatching inbound requests. Checked
    /// before middleware and before invoking a handler so work queued behind a close, such as the rest
    /// of a batch after a failed handshake, never runs.
    /// </summary>
    private bool IsClosing
        => Volatile.Read(ref _shutdownFlag) != 0
            || Volatile.Read(ref _dispatchClosed) != 0
            || Volatile.Read(ref _closeRequested) != 0
            || _disposeCts.IsCancellationRequested;

    /// <summary>The longest method name echoed back in a method-not-found error.</summary>
    private const int MaximumEchoedMethodNameLength = 128;

    private static string TruncateMethodName(string method)
        => method.Length <= MaximumEchoedMethodNameLength ? method : string.Concat(method.AsSpan(0, MaximumEchoedMethodNameLength), "...");

    private void HandleCancellationNotification(JsonElement? @params)
    {
        if (@params is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("id", out JsonElement idElement))
        {
            return;
        }

        RequestId id = ReadRequestId(idElement);
        if (!id.IsNull && _inboundCancellations.TryGetValue(id, out CancellationTokenSource? cts))
        {
            // This runs on the read loop: never execute application cancellation callbacks inline,
            // where one that blocks or throws would stall or kill the connection.
            CancelInBackground(cts);
        }
    }

    /// <summary>Cancels <paramref name="cts"/>, running its callbacks inline and swallowing any failure.</summary>
    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch
        {
            // Already disposed, or an application cancellation callback threw. Cancel still runs
            // every callback before throwing, so the source is canceled either way.
        }
    }

    /// <summary>Cancels <paramref name="cts"/> with its callbacks on the thread pool, observing any failure.</summary>
    private static void CancelInBackground(CancellationTokenSource cts)
    {
        Task cancel;
        try
        {
            cancel = cts.CancelAsync();
        }
        catch
        {
            return; // Already disposed.
        }

        ObserveFailure(cancel);
    }

    private static void ObserveFailure(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

        return _handler.WriteMessageAsync(bytes, _disposeCts.Token).AsTask();
    }

    private Exception CreateRemoteException(string method, JsonElement errorElement)
    {
        if (errorElement.ValueKind != JsonValueKind.Object
            || !errorElement.TryGetProperty("code", out JsonElement codeElement)
            || codeElement.ValueKind != JsonValueKind.Number
            || !codeElement.TryGetInt32(out int code)
            || !errorElement.TryGetProperty("message", out JsonElement messageElement)
            || messageElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonRpcException("Invalid JSON-RPC response: 'error' must contain an integer 'code' and a string 'message'.");
        }

        string message = messageElement.GetString()!;

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

    // Positional and by-name arguments are serialized by their *runtime* type (the untyped invoke API
    // has no declared parameter type to use), so under a source-generated JsonSerializerContext every
    // concrete runtime type must be registered. Lazy LINQ projections, iterator results, and anonymous
    // types compile to generated types that cannot be registered; this hint points the caller at the fix.
    private const string RuntimeTypeRegistrationHint =
        "Arguments are serialized by their runtime type, so under a source-generated JsonSerializerContext " +
        "(required for Native AOT) that concrete type must be registered with [JsonSerializable]. Lazy LINQ " +
        "projections (for example '.Select(...)'), iterator ('yield') results, and anonymous types compile to " +
        "generated types that cannot be registered; materialize them to a registered type first (for example " +
        "by calling '.ToArray()').";

    private RawJsonValue? SerializePositionalParameters(string method, object?[]? arguments)
    {
        if (arguments is null || arguments.Length == 0)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (int i = 0; i < arguments.Length; i++)
            {
                object? argument = arguments[i];
                if (argument is null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                try
                {
                    JsonSerializer.Serialize(writer, argument, _serializerOptions.GetTypeInfo(argument.GetType()));
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    throw new NotSupportedException(
                        $"Could not serialize argument at index {i} of JSON-RPC method '{method}': its runtime type " +
                        $"'{argument.GetType()}' has no JSON metadata. {RuntimeTypeRegistrationHint}",
                        ex);
                }
            }

            writer.WriteEndArray();
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }

    private RawJsonValue? SerializeParameterObject(string method, object? argument)
    {
        if (argument is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            try
            {
                JsonSerializer.Serialize(writer, argument, _serializerOptions.GetTypeInfo(argument.GetType()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                throw new NotSupportedException(
                    $"Could not serialize the parameter object of JSON-RPC method '{method}': its runtime type " +
                    $"'{argument.GetType()}' has no JSON metadata. {RuntimeTypeRegistrationHint}",
                    ex);
            }
        }

        return RawJsonValue.FromWritten(buffer.WrittenSpan);
    }

    private RawJsonValue? SerializeResult(object? result)
        => result is null ? null : SerializeToRaw(result);

    private RawJsonValue SerializeToRaw(object value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            try
            {
                JsonSerializer.Serialize(writer, value, _serializerOptions.GetTypeInfo(value.GetType()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                throw new NotSupportedException(
                    $"Could not serialize a JSON-RPC payload of runtime type '{value.GetType()}': it has no JSON " +
                    $"metadata. {RuntimeTypeRegistrationHint}",
                    ex);
            }
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
                return RequestId.FromNumber(idElement);

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
        catch
        {
            // An application cancellation callback threw; the source is canceled regardless and the
            // transport must still be closed.
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
    {
        if (Interlocked.Exchange(ref _shutdownFlag, 1) == 1)
        {
            return;
        }

        Exception? completionError = failure switch
        {
            null => null,
            OperationCanceledException when _disposeCts.IsCancellationRequested => null,
            _ => failure,
        };

        // Every step must run even if an earlier one throws (application cancellation callbacks run
        // inline here): Completion must always complete and enumerators must always be drained.
        try
        {
            foreach (KeyValuePair<long, PendingCall> pending in _pending)
            {
                pending.Value.Completion.TrySetException(new ConnectionLostException(
                    "The JSON-RPC connection was lost before the request completed.", completionError));
            }

            _pending.Clear();

            foreach (KeyValuePair<RequestId, CancellationTokenSource> inbound in _inboundCancellations)
            {
                CancelQuietly(inbound.Value);
                inbound.Value.Dispose();
            }

            _inboundCancellations.Clear();
        }
        finally
        {
            try
            {
                // Serialize the drain with registration so a start cannot publish after shutdown.
                List<RpcEnumerableResult> enumerators = new();
                lock (_gate)
                {
                    foreach (KeyValuePair<long, RpcEnumerableResult> enumerator in _enumerators)
                    {
                        if (_enumerators.TryRemove(enumerator.Key, out RpcEnumerableResult? enumerable))
                        {
                            enumerators.Add(enumerable);
                        }
                    }
                }

                // Disposal cancels each enumeration's own token before waiting for its read gate.
                foreach (RpcEnumerableResult enumerable in enumerators)
                {
                    _ = DisposeReservedEnumeratorAsync(enumerable);
                }
            }
            finally
            {
                if (completionError is null)
                {
                    _completion.TrySetResult();
                }
                else
                {
                    _completion.TrySetException(completionError);
                }
            }
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
