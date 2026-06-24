// The e2e tests share process-global state (the one GPU, the current directory, Task Scheduler) and must
// never run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
