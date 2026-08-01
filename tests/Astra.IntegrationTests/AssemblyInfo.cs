using Xunit;

// Integration tests share one PostgreSQL database and must not race on global state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
