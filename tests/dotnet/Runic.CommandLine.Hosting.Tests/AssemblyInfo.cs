using TUnit.Core;
using TUnit.Core.Helpers;

// Every case classifies, presents or executes commands against in-memory consoles and its own adapter. Only
// classification/parameter-environment-is-copied changes process state: it sets the HOST_VALUE environment variable
// to prove that it does not leak, so it is [NotInParallel]. Run the other cases at most one per core.
[assembly: ParallelLimiter<ProcessorCountParallelLimit>]
