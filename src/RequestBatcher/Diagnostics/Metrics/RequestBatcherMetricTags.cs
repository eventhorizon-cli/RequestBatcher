namespace RequestBatcher.Diagnostics.Metrics;

internal static class RequestBatcherMetricTags
{
    internal const string RequestType = "requestbatcher.request.type";
    internal const string BatcherName = "requestbatcher.name";
    internal const string SubmissionKind = "requestbatcher.submission.kind";
    internal const string Outcome = "requestbatcher.outcome";

    internal const string SingleSubmission = "single";
    internal const string MultiSubmission = "multi";
    internal const string Success = "success";
    internal const string Error = "error";
    internal const string Canceled = "canceled";
}
