#:property TargetFramework=net8.0
#:property PublishAot=false
#:property LangVersion=12.0

internal static class CheckHealth
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("CheckHealth [http://host:port/health] (default: http://127.0.0.1:8080/health)");
            return 0;
        }

        if (args.Length > 1 || !Uri.TryCreate(args.FirstOrDefault() ?? "http://127.0.0.1:8080/health", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine("Provide one HTTP health endpoint, or use --help.");
            return 2;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(uri).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }
}
