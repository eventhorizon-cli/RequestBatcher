namespace RequestBatcher.Tests;

public sealed class RequestBatchEntryExtensionsTests
{
    [Fact]
    public void SetResponses_FactoryAssignsInInputOrder_InvokesOncePerEntry()
    {
        var entries = CreateEntries();
        var requests = new List<int>();

        entries.SetResponses(request =>
        {
            requests.Add(request);
            return $"response-{request}";
        });

        Assert.Equal(new[] { 3, 1, 2 }, requests);
        Assert.Equal(
            new[] { "response-3", "response-1", "response-2" },
            entries.Select(static entry => entry.GetResponse()).ToArray());
    }

    [Fact]
    public void SetResponses_FactoryThrows_LeavesAllEntriesUnset()
    {
        var entries = CreateEntries();
        var expected = new TestBatchException();
        var requests = new List<int>();

        var actual = Assert.Throws<TestBatchException>(() => entries.SetResponses(request =>
        {
            requests.Add(request);
            return request == 1 ? throw expected : $"response-{request}";
        }));

        Assert.Same(expected, actual);
        Assert.Equal(new[] { 3, 1 }, requests);
        AssertAllResponsesUnset(entries);
    }

    [Fact]
    public void SetResponses_FactoryChangesEntryCount_LeavesAllEntriesUnset()
    {
        var entries = CreateEntries().ToList();

        var exception = Assert.Throws<InvalidOperationException>(() => entries.SetResponses(request =>
        {
            if (request == 3)
            {
                entries.Add(new RequestBatchEntry<int, string>(4));
            }

            return $"response-{request}";
        }));

        Assert.Contains("entry count changed", exception.Message, StringComparison.Ordinal);
        AssertAllResponsesUnset(entries);
    }

    [Fact]
    public void SetResponses_PreassignedEntry_FactoryRunsFullyAndRejectsOverwrite()
    {
        var entries = CreateEntries();
        entries[1].SetResponse("original");
        var requests = new List<int>();

        var exception = Assert.Throws<InvalidOperationException>(() => entries.SetResponses(request =>
        {
            requests.Add(request);
            return $"replacement-{request}";
        }));

        Assert.Contains("already been assigned", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { 3, 1, 2 }, requests);
        Assert.Equal("replacement-3", entries[0].GetResponse());
        Assert.Equal("original", entries[1].GetResponse());
        AssertResponseIsUnset(entries[2]);
    }

    [Fact]
    public void SetResponses_EnumerableWithPreassignedEntry_RejectsOverwrite()
    {
        var entries = CreateEntries();
        entries[1].SetResponse("original");

        Assert.Throws<InvalidOperationException>(() =>
            entries.SetResponses(new[] { "first", "second", "third" }));

        Assert.Equal("first", entries[0].GetResponse());
        Assert.Equal("original", entries[1].GetResponse());
        AssertResponseIsUnset(entries[2]);
    }

    [Fact]
    public void SetResponses_EnumerableEnumeratedOnce_AssignsInInputOrder()
    {
        var entries = CreateEntries();
        var enumerationCount = 0;

        IEnumerable<string> Responses()
        {
            enumerationCount++;
            yield return "first";
            yield return "second";
            yield return "third";
        }

        entries.SetResponses(Responses());

        Assert.Equal(1, enumerationCount);
        Assert.Equal(
            new[] { "first", "second", "third" },
            entries.Select(static entry => entry.GetResponse()).ToArray());
    }

    [Fact]
    public void SetResponses_EnumerableThrows_LeavesAllEntriesUnset()
    {
        var entries = CreateEntries();
        var expected = new TestBatchException();
        var enumerationCount = 0;

        IEnumerable<string> Responses()
        {
            enumerationCount++;
            yield return "first";
            throw expected;
        }

        var actual = Assert.Throws<TestBatchException>(() => entries.SetResponses(Responses()));

        Assert.Same(expected, actual);
        Assert.Equal(1, enumerationCount);
        AssertAllResponsesUnset(entries);
    }

    private static RequestBatchEntry<int, string>[] CreateEntries() =>
    [
        new RequestBatchEntry<int, string>(3),
        new RequestBatchEntry<int, string>(1),
        new RequestBatchEntry<int, string>(2),
    ];

    private static void AssertAllResponsesUnset(IEnumerable<RequestBatchEntry<int, string>> entries)
    {
        foreach (var entry in entries)
        {
            AssertResponseIsUnset(entry);
        }
    }

    private static void AssertResponseIsUnset(RequestBatchEntry<int, string> entry) =>
        Assert.Throws<InvalidOperationException>(() => entry.GetResponse());

    private sealed class TestBatchException : Exception;
}
