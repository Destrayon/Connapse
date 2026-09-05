using Xunit;

// Collections in this assembly must not run concurrently.
//
// LocalStackFixture configures its SDK through process-wide environment
// variables — AWS_ENDPOINT_URL_S3 and friends — which are global state, not per-collection.
// Two collections holding their own container would overwrite each other's endpoint, pointing
// one collection's connector at the other's container, and whichever finished first would
// clear the variables while the other was still running.
//
// The cost is small: the large "Integration Tests" collection already runs sequentially, and
// every collection here contends on Docker anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
