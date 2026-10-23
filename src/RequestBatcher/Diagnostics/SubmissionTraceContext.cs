using System.Diagnostics;

namespace RequestBatcher.Diagnostics;

internal class SubmissionTraceContext
{
    private readonly ActivityContext _context;

    private SubmissionTraceContext(ActivityContext context) => _context = context;

    internal ActivityContext Context => _context;

    internal static SubmissionTraceContext Create(ActivityContext context, bool collectBatchLinks) =>
        collectBatchLinks
            ? new RecordedSubmissionTraceContext(context)
            : new SubmissionTraceContext(context);

    internal virtual bool CollectBatchLinks => false;

    internal virtual void AddBatch(ActivityContext batch)
    {
    }

    internal virtual void Complete(Activity activity)
    {
    }

    private sealed class RecordedSubmissionTraceContext(ActivityContext context)
        : SubmissionTraceContext(context)
    {
        private readonly object _gate = new();
        private ActivityContext _firstBatch;
        private List<ActivityContext>? _additionalBatches;
        private bool _hasFirstBatch;
        private bool _completed;

        internal override bool CollectBatchLinks => true;

        internal override void AddBatch(ActivityContext batch)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                if (!_hasFirstBatch)
                {
                    _firstBatch = batch;
                    _hasFirstBatch = true;
                    return;
                }

                (_additionalBatches ??= []).Add(batch);
            }
        }

        internal override void Complete(Activity activity)
        {
            ActivityContext firstBatch;
            List<ActivityContext>? additionalBatches;
            bool hasFirstBatch;

            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                hasFirstBatch = _hasFirstBatch;
                firstBatch = _firstBatch;
                additionalBatches = _additionalBatches;
                _additionalBatches = null;
            }

            if (hasFirstBatch)
            {
                activity.AddLink(new ActivityLink(firstBatch));
            }

            if (additionalBatches is not null)
            {
                foreach (var batch in additionalBatches)
                {
                    activity.AddLink(new ActivityLink(batch));
                }
            }
        }
    }
}
