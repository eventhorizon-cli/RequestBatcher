using System.Diagnostics;
using RequestBatcher.PendingRequests;
using RequestBatcher.Scheduling;

namespace RequestBatcher.Diagnostics.Tracing;

// Admission can finish a request before returning its Task. Hold publication until the tracing boundary
// knows whether that Task is the completion itself or a wrapper covering additional asynchronous work.
internal sealed class CallCompletionObserver(Activity activity, SubmissionTraceContext? context) : IRequestCompletionObserver
{
    // Capture while the Execute Activity is current so completion listeners see the submission's ambient values.
    // Null means submission flow was suppressed. The callback starts with completion-thread ambient values;
    // Run restores that thread after callback mutations without importing a submission context.
    private readonly ExecutionContext? _submissionExecutionContext = ExecutionContext.Capture();
    private IRequestCompletionNotification? _completion;
    private bool _attached;
    private bool _direct;
    private bool _completed;

    public void Register(IRequestCompletionNotification completion) => _completion = completion;

    internal bool TryAttach(Task? returnedTask)
    {
        bool publish;
        lock (this)
        {
            if (_attached)
            {
                return _direct;
            }

            _direct = _completion is not null && ReferenceEquals(_completion.Task, returnedTask);
            _attached = true;
            publish = _completed;
        }

        if (publish)
        {
            Publish();
        }

        return _direct;
    }

    public void OnCompleted(IRequestCompletionNotification completion)
    {
        lock (this)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (!_attached)
            {
                return;
            }
        }

        Publish();
    }

    private void Publish()
    {
        try
        {
            if (_direct)
            {
                ExecutionContextIsolation.RunSynchronously(
                    _submissionExecutionContext,
                    static state => ((CallCompletionObserver)state!).EndCall(),
                    this);
            }
        }
        finally
        {
            _completion!.Publish();
        }
    }

    private void EndCall()
    {
        // The captured context already has this Activity. Assigning Current again can materialize its
        // otherwise unused string Id during setter validation, even when the value does not change.
        if (!ReferenceEquals(Activity.Current, activity))
        {
            Activity.Current = activity;
        }
        if (_completion!.Error is { } exception)
        {
            RequestBatcherTracing.RecordFailure(activity, exception);
        }
        else if (_completion.IsCanceled)
        {
            activity.SetTag(RequestBatcherTraceTags.Canceled, true);
        }

        context?.Complete(activity);
        activity.Dispose();
    }
}
