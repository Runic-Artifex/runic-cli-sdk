using TUnit.Core;
using TUnit.Core.Helpers;

// Every case reads the copied catalog files or runs an in-memory CommandApp. The suite changes no process state:
// no environment variables, current directory, culture, ports, files or child processes. Run at most one case per core.
[assembly: ParallelLimiter<ProcessorCountParallelLimit>]
