using Contracts.Transactions;
using Grpc.Core;
using TransactionService.Application;
using TransactionServiceContract = Contracts.Transactions.TransactionService;

namespace TransactionService.Grpc.Services;

/// <summary>
/// Server-side implementation of the shared transaction.proto contract
/// (TransactionService.md §5). Thin - all real logic lives in
/// ITransactionSubmissionHandler; this just maps outcomes to gRPC statuses.
///
/// Aliased as TransactionServiceContract because this project's own root
/// namespace is also "TransactionService" - without the alias, the bare name
/// "TransactionService" resolves to that ambient namespace instead of the
/// generated proto service class, even with `using Contracts.Transactions;`
/// in scope.
/// </summary>
public sealed class TransactionGrpcService : TransactionServiceContract.TransactionServiceBase
{
    private readonly ITransactionSubmissionHandler _handler;
    private readonly ITransactionSearchHandler _searchHandler;

    public TransactionGrpcService(ITransactionSubmissionHandler handler, ITransactionSearchHandler searchHandler)
    {
        _handler = handler;
        _searchHandler = searchHandler;
    }

    public override async Task<SubmitTransactionResponse> SubmitTransaction(SubmitTransactionRequest request, ServerCallContext context)
    {
        var submission = new TransactionSubmission(
            request.TransactionNo,
            request.TransactionDatetime,
            request.Amount,
            request.Type,
            request.Status,
            request.Currency);

        var result = await _handler.HandleAsync(submission, context.CancellationToken);

        return result.Outcome switch
        {
            SubmissionOutcome.Accepted => new SubmitTransactionResponse { Accepted = true },
            SubmissionOutcome.ValidationFailed => throw new RpcException(new Status(StatusCode.InvalidArgument, result.ErrorMessage ?? "Validation failed")),
            SubmissionOutcome.SendFailed => throw new RpcException(new Status(StatusCode.Internal, "Failed to send message to SQS")),
            _ => throw new InvalidOperationException($"Unhandled outcome: {result.Outcome}"),
        };
    }

    public override async Task<SearchByTransactionNoResponse> SearchByTransactionNo(SearchByTransactionNoRequest request, ServerCallContext context)
    {
        var result = await _searchHandler.SearchByTransactionNoAsync(request.TransactionNo, context.CancellationToken);

        return result.Outcome switch
        {
            TransactionNoSearchOutcome.Found => new SearchByTransactionNoResponse { Found = true, Transaction = ToProto(result.Transaction!) },
            TransactionNoSearchOutcome.NotFound => new SearchByTransactionNoResponse { Found = false },
            TransactionNoSearchOutcome.InvalidArgument => throw new RpcException(new Status(StatusCode.InvalidArgument, result.ErrorMessage ?? "Invalid request")),
            _ => throw new InvalidOperationException($"Unhandled outcome: {result.Outcome}"),
        };
    }

    public override async Task<SearchByDateRangeResponse> SearchByDateRange(SearchByDateRangeRequest request, ServerCallContext context)
    {
        var result = await _searchHandler.SearchByDateRangeAsync(request.From, request.To, request.Limit, request.Offset, context.CancellationToken);

        if (result.Outcome == DateRangeSearchOutcome.InvalidArgument)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.ErrorMessage ?? "Invalid request"));
        }

        var response = new SearchByDateRangeResponse { HasMore = result.HasMore };
        response.Transactions.AddRange(result.Transactions!.Select(ToProto));
        return response;
    }

    private static Transaction ToProto(TransactionRecord record) => new()
    {
        Id = record.Id,
        TransactionNo = record.TransactionNo,
        TransactionDatetime = record.TransactionDatetime,
        Amount = record.Amount,
        Type = record.Type,
        Status = record.Status,
        Currency = record.Currency,
        SystemDatetime = record.SystemDatetime,
    };
}
