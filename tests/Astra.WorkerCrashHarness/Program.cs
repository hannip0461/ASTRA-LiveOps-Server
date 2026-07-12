using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Astra.Worker;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
    ?? throw new InvalidOperationException("ASTRA_POSTGRES_CONNECTION is required.");
var expectedEventId = Guid.Parse(
    Environment.GetEnvironmentVariable("ASTRA_CRASH_EVENT_ID")
    ?? throw new InvalidOperationException("ASTRA_CRASH_EVENT_ID is required."));
const string workerId = "hard-kill-harness";

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var store = new PostgresOutboxEventStore(dataSource);
var leased = await store.LeaseBatchAsync(workerId, 1, TimeSpan.FromSeconds(2));
var outboxEvent = leased.Single();
if (outboxEvent.EventId != expectedEventId)
{
    throw new InvalidOperationException("Crash harness leased an unexpected event.");
}

await new PostgresOperationalEventHandler(dataSource).HandleAsync(outboxEvent);
Console.WriteLine($"DELIVERED:{outboxEvent.EventId}");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
