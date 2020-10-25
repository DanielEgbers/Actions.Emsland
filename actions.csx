#r "nuget: System.CommandLine, 2.0.0-beta1.20371.2"
#r "nuget: Flurl.Http, 2.4.2"
#r "nuget: AngleSharp, 0.14.0"
#r "nuget: AngleSharp.XPath, 1.1.7"

#load "../Actions.Shared/git.csx"
#load "../Actions.Shared/feed.csx"

#nullable enable

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using Flurl.Http;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.XPath;

return await InvokeCommandAsync(Args.ToArray());

private async Task<int> InvokeCommandAsync(string[] args)
{
    const string FeedFilePath = "data/Geeste.xml";

    var scrape = new Command("scrape")
    {
        Handler = CommandHandler.Create(async () =>
        {
            await UpdateFeedAsync(FeedFilePath);
        })
    };

    var push = new Command("push")
    {
        Handler = CommandHandler.Create(async () =>
        {
            if (Debugger.IsAttached)
                return;

            var workingDirectory = Path.GetDirectoryName(FeedFilePath)!;

            await Git.ConfigUserAsync(workingDirectory, "GitHub Actions", "actions@users.noreply.github.com");

            if (!await Git.CommitAsync(workingDirectory, "update {files}", Path.GetFileName(FeedFilePath)))
                return;

            await Git.PushAsync(workingDirectory);
        })
    };

    var root = new RootCommand()
    {
        scrape,
        push,
    };

    root.Handler = CommandHandler.Create(async () =>
    {
        await scrape.InvokeAsync(string.Empty);
        await push.InvokeAsync(string.Empty);
    });

    return await root.InvokeAsync(args);
}

private async Task UpdateFeedAsync(string file)
{
    var uri = new Uri($"https://www.geeste.de/rathaus-und-buergerservice/veroeffentlichungen/pressemeldungen/pressemeldungen.html");

    var items = new List<FeedItem>();

    using
    (
        var context = BrowsingContext.New
        (
            Configuration.Default
                .WithDefaultLoader()
        )
    )
    {
        var document = await context
            .OpenAsync(uri.ToString())
            ;

        var elements = document.Body.SelectNodes("//article").Cast<IElement>();
        foreach (var element in elements)
        {
            var item = new FeedItem()
            {
                Link = (element.SelectSingleNode("./a") as IElement)?.GetAttribute("href"),
                Title = element.SelectSingleNode("./h2")?.TextContent.Trim(),
                Description = element.SelectSingleNode("./p")?.TextContent.Trim() + "...",
                Image = (element.SelectSingleNode(".//img") as IElement)?.GetAttribute("src"),
                Published = DateTime.ParseExact(element.SelectSingleNode("./text()")?.TextContent ?? string.Empty, format: "dd.MM.yyyy", provider: CultureInfo.InvariantCulture)
            };
            items.Add(item);
        }
    }

    if (File.Exists(file) && items.Count <= 0)
        return;

    var baseUrl = uri.GetLeftPart(System.UriPartial.Authority);

    var feed = await Feed.WriteAsync
    (
        channel: new FeedChannel()
        {
            Title = "Gemeinde Geeste - Pressemeldungen",
            Description = "Gemeinde Geeste - Pressemeldungen",
            Link = uri,
        },
        items: items,
        itemLinkBaseUrl: baseUrl,
        itemImageBaseUrl: baseUrl
    );

    File.WriteAllText(file, feed);
}