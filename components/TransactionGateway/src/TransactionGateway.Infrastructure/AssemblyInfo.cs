using System.Runtime.CompilerServices;

// Lets TransactionGateway.UnitTests construct GrpcTransactionForwarder with a
// fast/no-op retry pipeline instead of waiting through real backoff delays.
[assembly: InternalsVisibleTo("TransactionGateway.UnitTests")]
