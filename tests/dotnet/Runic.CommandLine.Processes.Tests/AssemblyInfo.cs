using TUnit.Core;

// Every case starts this executable as a child process and many race its exit against timeouts, cancellation and
// descendant processes. Run them one at a time so that load from other cases cannot change those races.
[assembly: NotInParallel]
