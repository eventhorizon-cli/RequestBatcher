namespace RequestBatcher.Diagnostics;

internal static class RequestBatcherTraceTags
{
    internal const string RequestType = "requestbatcher.request.type";
    internal const string BatcherName = "requestbatcher.name";
    internal const string BatchSize = "requestbatcher.batch.size";
    internal const string SubmissionCount = "requestbatcher.batch.submission.count";
    internal const string Canceled = "requestbatcher.canceled";
    internal const string ErrorType = "error.type";
}
