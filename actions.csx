#r "nuget: System.CommandLine, 2.0.0-beta1.20371.2"
#r "nuget: Flurl.Http, 2.4.2"
#r "nuget: AngleSharp, 0.14.0"
#r "nuget: AngleSharp.XPath, 1.1.7"
#r "nuget: SmartReader, 0.7.5"

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
using SmartReader;

Console.WriteLine(":D");

// return await InvokeCommandAsync(Args.ToArray());

private async Task<int> InvokeCommandAsync(string[] args)
{
    const string ErrorLogFilePath = "data/.error.log";

    const string WasLosInFeedFilePath = "data/WasLosIn.xml";
    const string GeesteFeedFilePath = "data/Geeste.xml";

    Command BuildScrapeCommand(string name, Func<Task> action) 
    {
        return new Command(name)
        {
            Handler = CommandHandler.Create(async () =>
            {
                Console.WriteLine(name);
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    var message = $"{ex.GetType().Name}: {ex.Message}";
                    Console.WriteLine(message);
                    File.AppendAllText(ErrorLogFilePath, message);
                }
            })
        };
    }

    var scrape = new Command("scrape")
    {
        BuildScrapeCommand("WasLosIn", async () => await UpdateWasLosInFeedAsync(WasLosInFeedFilePath)),
        BuildScrapeCommand("Geeste", async () => await UpdateGeesteFeedAsync(GeesteFeedFilePath)),
    };
    scrape.Handler = CommandHandler.Create(async () =>
    {
        foreach (var command in scrape.Where(s => s is Command).Cast<Command>())
        {
            await command.InvokeAsync(string.Empty);
        }
    });

    var push = new Command("push")
    {
        Handler = CommandHandler.Create(async () =>
        {
            if (Debugger.IsAttached)
                return;

            var dataPath = Path.GetDirectoryName(WasLosInFeedFilePath)!;

            if (!(await Git.GetChangesAsync(workingDirectory: dataPath)).Any())
                return;

            await Git.ConfigUserAsync(name: "GitHub Actions", email: "actions@users.noreply.github.com", workingDirectory: dataPath);

            await Git.StageAllAsync(workingDirectory: dataPath);

            await Git.CommitAsync("update", workingDirectory: dataPath);

            await Git.PushAsync(workingDirectory: dataPath);
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

private async Task UpdateWasLosInFeedAsync(string file)
{
    const int ItemLimit = 25;

    var uri = new Uri($"https://www.waslosin.de/feed");

    var baseUrl = uri.GetLeftPart(System.UriPartial.Authority);

    var feedXml = await uri.ToString().GetStringAsync();

    var itemLinks = Feed.ReadItemLinks(feedXml).ToList();

    if (itemLinks.Count <= 0)
        return;

    var existingItems = new List<FeedItem>();

    if (File.Exists(file))
        existingItems.AddRange(await Feed.ReadItemsAsync(File.ReadAllText(file)));

    var newItemLinks = itemLinks.Where(l => !existingItems.Any(i => i.Link == l)).ToList();

    var newItems = new List<FeedItem>();

    foreach (var itemLink in newItemLinks)
    {
        if (newItems.Count >= ItemLimit)
            break;

        var reader = new Reader(itemLink);

        reader.AddCustomOperationStart(e =>
        {
            var elements = new List<IElement>();

            elements.AddRange(e.QuerySelectorAll(".obi_random_banners_posts")); // ads
            elements.AddRange(e.QuerySelectorAll("#mvp-content-bot")); // author

            foreach (var element in elements)
                element.Remove();
        });

        var article = await reader.GetArticleAsync();

        var item = new FeedItem()
        {
            Link = itemLink,
            Title = article.Title,
            Description = article.Content,
            Image = !article.Content.Contains(article.FeaturedImage) ? article.FeaturedImage : null,
            Published = article.PublicationDate
        };

        newItems.Add(item);
    }

    if (newItems.Count <= 0)
        return;

    var feed = await Feed.WriteAsync
    (
        channel: new FeedChannel()
        {
            Title = "Was Los In",
            Description = "Lingen, Meppen, Papenburg & Nordhorn",
            Link = new Uri(baseUrl),
        },
        items: newItems.Concat(existingItems).Take(ItemLimit)
    );

    File.WriteAllText(file, feed);
}

private async Task UpdateGeesteFeedAsync(string file)
{
    const int ItemLimit = 50;

    var uri = new Uri($"https://www.geeste.de/rathaus-und-buergerservice/veroeffentlichungen/pressemeldungen/pressemeldungen.html");

    var baseUrl = uri.GetLeftPart(System.UriPartial.Authority);

    var existingItems = new List<FeedItem>();

    if (File.Exists(file))
        existingItems.AddRange(await Feed.ReadItemsAsync(File.ReadAllText(file)));

    var newItems = new List<FeedItem>();

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

        var elements = document.DocumentElement.SelectNodes("//article").Cast<IElement>();
        foreach (var element in elements)
        {
            if (newItems.Count >= ItemLimit)
                break;
        
            var link = (element.SelectSingleNode("./a") as IElement)?.GetAttribute("href");

            if (string.IsNullOrWhiteSpace(link))
                continue;

            if (!link.StartsWith(baseUrl))
                link = Flurl.Url.Combine(baseUrl, link);

            if (existingItems.Any(i => i.Link == link))
                continue;

            var articleDocument = await context
                .OpenAsync(link)
                ;

            var articleHtml = articleDocument.DocumentElement.QuerySelector("#content")?.InnerHtml;

            if (string.IsNullOrWhiteSpace(articleHtml))
                continue;

            var reader = new Reader(link, articleHtml);

            var article = await reader.GetArticleAsync();

            var item = new FeedItem()
            {
                Link = link,
                Title = element.SelectSingleNode("./h2")?.TextContent.Trim(),
                Description = article.Content,
                Published = DateTime.ParseExact(element.SelectSingleNode("./text()")?.TextContent ?? string.Empty, format: "dd.MM.yyyy", provider: CultureInfo.InvariantCulture)
            };

            newItems.Add(item);
        }
    }

    var feed = await Feed.WriteAsync
    (
        channel: new FeedChannel()
        {
            Title = "Gemeinde Geeste - Pressemeldungen",
            Description = "Gemeinde Geeste - Pressemeldungen",
            Link = uri,
        },
        items: newItems.Concat(existingItems).Take(ItemLimit)
    );

    File.WriteAllText(file, feed);
}