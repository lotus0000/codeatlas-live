// One test class runs a real CodeAtlas.AspNetCore app in-process; its ActivityListener is process-wide.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
