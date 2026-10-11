using TUnit.Core.Helpers;

// Most cases parse, bind and run commands against in-memory consoles, compile in-memory sources or read the frozen
// corpora; a case that changes the culture restores it on its own async flow, and file-writing cases use their own
// temporary directories. Run at most one case per core. Cases that cannot run alongside others are [NotInParallel]:
// the signal cases and the native shell completion cases start child processes with fixed deadlines, and
// app/custom-values-bind-once-and-validate and causes/ranges-precede-custom-validators count calls in static fields.
[assembly: ParallelLimiter<ProcessorCountParallelLimit>]
