// One Postgres container per class and real hosts: run classes one after another.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
