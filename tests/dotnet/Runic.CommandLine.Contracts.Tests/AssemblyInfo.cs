using TUnit.Core;
using TUnit.Core.Helpers;

// Every case checks in-memory contract values or reads the committed corpora. The suite changes no process state:
// no environment variables, current directory, culture, ports, files or child processes. Run at most one case per core.
[assembly: ParallelLimiter<ProcessorCountParallelLimit>]
