// ActivitySource listeners are process-wide: parallel test hosts would record each other's spans.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
