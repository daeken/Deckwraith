using Xunit;

// These end-to-end tests each own an in-process PowerShell runspace and some
// launch child MCP servers. Running the test classes concurrently can deadlock
// PowerShell or its child-process pipes on constrained Linux CI runners even
// though every deck and runtime manager is independently isolated.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
