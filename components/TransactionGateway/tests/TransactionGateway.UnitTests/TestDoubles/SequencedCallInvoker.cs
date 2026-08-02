using Grpc.Core;

namespace TransactionGateway.UnitTests.TestDoubles;

/// <summary>
/// Fake CallInvoker for testing generated gRPC clients without a live server
/// or network - constructing a client with a CallInvoker directly is the
/// standard pattern for this (every generated client has a
/// `TClient(CallInvoker)` constructor for exactly this purpose).
///
/// Each call consumes the next behavior in the sequence; once exhausted, the
/// last behavior repeats (so "always fails" only needs one entry).
/// </summary>
internal sealed class SequencedCallInvoker : CallInvoker
{
    private readonly Func<object>[] _behaviors;
    private int _callCount;

    public SequencedCallInvoker(params Func<object>[] behaviors)
    {
        if (behaviors.Length == 0)
        {
            throw new ArgumentException("At least one behavior is required.", nameof(behaviors));
        }

        _behaviors = behaviors;
    }

    public int CallCount => _callCount;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var index = Math.Min(_callCount, _behaviors.Length - 1);
        _callCount++;

        Task<TResponse> responseTask;
        try
        {
            responseTask = Task.FromResult((TResponse)_behaviors[index]());
        }
        catch (Exception ex)
        {
            responseTask = Task.FromException<TResponse>(ex);
        }

        return new AsyncUnaryCall<TResponse>(
            responseTask,
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.OK, string.Empty),
            () => new Metadata(),
            () => { });
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException("Not needed for TransactionService.SubmitTransaction (unary).");

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotSupportedException("Not needed for TransactionService.SubmitTransaction (unary).");

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException("Not needed for TransactionService.SubmitTransaction (unary).");

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotSupportedException("Not needed for TransactionService.SubmitTransaction (unary).");
}
