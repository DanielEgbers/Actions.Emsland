#r "nuget: System.CommandLine, 2.0.0-beta1.20371.2"
#r "nuget: SimpleExec, 6.2.0"
#r "nuget: Flurl.Http, 2.4.2"
#r "nuget: AngleSharp, 0.14.0"
#r "nuget: AngleSharp.XPath, 1.1.7"
#r "nuget: Microsoft.SyndicationFeed.ReaderWriter, 1.0.2"

#nullable enable

using static SimpleExec.Command;

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Xml;
using System.Xml.Linq;
using Flurl.Http;
using Microsoft.SyndicationFeed;
using Microsoft.SyndicationFeed.Rss;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.XPath;

return await InvokeCommandAsync(Args.ToArray());

private async Task<int> InvokeCommandAsync(string[] args)
{
    const string FeedFilePath = "data/feed.xml";

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

            await GitConfigUserAsync(workingDirectory, "GitHub Actions", "actions@users.noreply.github.com");

            if (!await GitCommitAsync(workingDirectory, "update {files}", Path.GetFileName(FeedFilePath)))
                return;

            await GitPushAsync(workingDirectory);
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
    const string BaseUrl = @"https://www.geeste.de";

    var url = $"{BaseUrl}/rathaus-und-buergerservice/veroeffentlichungen/pressemeldungen/pressemeldungen.html";

    var items = new List<ISyndicationItem>();

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
            .OpenAsync(url)
            ;

        var elements = document.Body.SelectNodes("//article").Cast<IElement>();
        foreach (var element in elements)
        {
            var item = new SyndicationItem()
            {
                Id = BaseUrl + "/" + (element.SelectSingleNode("./a") as IElement)?.GetAttribute("href") ?? string.Empty,
                Title = element.SelectSingleNode("./h2").TextContent.Trim(),
                Description = element.SelectSingleNode("./p").TextContent.Trim(),
                Published = DateTime.Parse(element.SelectSingleNode("./text()").TextContent)
            };
            item.AddLink(new SyndicationLink(new Uri(item.Id)));

            var imageSrc = (element.SelectSingleNode(".//img") as IElement)?.GetAttribute("src");
            if (!string.IsNullOrWhiteSpace(imageSrc))
            {
                item.Description = $@"<img src=""{BaseUrl}{imageSrc}""><br>{item.Description}...";
            }

            items.Add(item);
        }
    }

    if (File.Exists(file) && items.Count <= 0)
        return;

    var feed = await WriteFeedAsync
    (
        title: "Gemeinde Geeste - Pressemeldungen",
        description: "Gemeinde Geeste - Pressemeldungen",
        link: url,
        items: items
    );

    File.WriteAllText(file, feed);
}

private async Task<string> WriteFeedAsync(string title, string description, string link, IEnumerable<ISyndicationItem> items)
{
    using (var stringWriter = new StringWriterWithEncoding(Encoding.UTF8))
    {
        using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings() { Async = true, Indent = false }))
        {
            var feedWriter = new RssFeedWriter(xmlWriter);

            await feedWriter.WriteTitle(title);
            await feedWriter.WriteDescription(description);
            await feedWriter.WriteValue("link", link);

            foreach (var item in items)
            {
                await feedWriter.Write(item);
            }
        }

        return XDocument.Parse(stringWriter.ToString()).ToString();
    }
}

private async Task GitConfigUserAsync(string? workingDirectory, string name, string email)
{
    await RunAsync("git", $"config user.name \"{name}\"", workingDirectory: workingDirectory);
    await RunAsync("git", $"config user.email \"{email}\"", workingDirectory: workingDirectory);
}

private async Task<bool> GitCommitAsync(string? workingDirectory, string message, params string[] files)
{
    var gitStatus = await ReadAsync("git", $"status --short --untracked-files", workingDirectory: workingDirectory);

    var changedFiles = files.Where(f => gitStatus.Contains(f)).ToArray();

    if (changedFiles.Length <= 0)
        return false;

    var changedFilesJoin = $"\"{string.Join("\" \"", changedFiles)}\"";

    await RunAsync("git", $"add {changedFilesJoin}", workingDirectory: workingDirectory);

    var gitCommitMessage = message.Replace($"{{{nameof(files)}}}", changedFilesJoin.Replace("\"", "'"));
    await RunAsync("git", $"commit -m \"{gitCommitMessage}\"", workingDirectory: workingDirectory);

    return true;
}

private async Task GitPushAsync(string? workingDirectory)
{
    await RunAsync("git", "push --quiet --progress", workingDirectory: workingDirectory);
}
 
private class StringWriterWithEncoding : StringWriter
{
    private readonly Encoding _encoding;

    public StringWriterWithEncoding(Encoding encoding)
    {
        _encoding = encoding;
    }

    public override Encoding Encoding => _encoding;
}