using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Telegram.Bot.Types.Enums;
using TelegramMessage = Telegram.Bot.Types.Message;
using MessageEntity = Telegram.Bot.Types.MessageEntity;

public sealed class SaveBotService(string botToken)
{
    const int QueueCapacity = 4;
    const string Python = "/opt/savebot/bin/python";
    static readonly string Worker = Path.Combine(AppContext.BaseDirectory, "savebot", "worker.py");
    static readonly bool WorkerInstalled = File.Exists(Python) && File.Exists(Worker);
    static readonly Regex CandidateUrl = new(
        @"(?<![\w@/])(?:https?://|www\.)[^\s<>""`]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> SupportedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto.ria.com", "www.auto.ria.com",
        "instagram.com", "www.instagram.com", "m.instagram.com",
        "threads.com", "www.threads.com", "threads.net", "www.threads.net",
        "x.com", "www.x.com", "twitter.com", "www.twitter.com", "mobile.twitter.com",
        "tiktok.com", "www.tiktok.com", "m.tiktok.com", "vm.tiktok.com", "vt.tiktok.com",
        "reddit.com", "www.reddit.com", "old.reddit.com", "np.reddit.com", "m.reddit.com",
        "redd.it", "v.redd.it", "i.redd.it"
    };

    readonly HashSet<long> groupIds = ReadIds("SAVE_BOT_GROUP_IDS");
    readonly HashSet<long> privateIds = ReadIds("SAVE_BOT_PRIVATE_CHAT_IDS");
    readonly Channel<TelegramMessage> queue = Channel.CreateBounded<TelegramMessage>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true });

    public bool Allows(TelegramMessage message) => WorkerInstalled && (message.Chat.Type switch
    {
        ChatType.Group or ChatType.Supergroup => groupIds.Contains(message.Chat.Id),
        ChatType.Private => privateIds.Contains(message.Chat.Id),
        _ => false
    });

    // Return false only when an eligible message could not be queued.
    public bool TryEnqueue(TelegramMessage message)
    {
        if (!Allows(message))
            return true;

        var text = message.Text ?? message.Caption;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var entities = message.Text != null ? message.Entities : message.CaptionEntities;
        var hasLink = entities?.Any(entity => entity.Type switch
        {
            MessageEntityType.TextLink => IsSupportedUrl(entity.Url),
            MessageEntityType.Url when entity.Offset >= 0 && entity.Length > 0
                && entity.Offset <= text.Length - entity.Length =>
                IsSupportedUrl(text.Substring(entity.Offset, entity.Length)),
            _ => false
        }) == true;
        hasLink = hasLink || CandidateUrl.Matches(text).Any(match => IsSupportedUrl(match.Value));
        return !hasLink || queue.Writer.TryWrite(message);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!WorkerInstalled)
        {
            if (groupIds.Count > 0 || privateIds.Count > 0)
                Console.Error.WriteLine("Save Bot is configured but its private worker was not bundled in this build.");
            return;
        }

        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await RunWorkerAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Save Bot request failed ({ex.GetType().Name}).");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    static bool IsSupportedUrl(string? value)
    {
        if (value == null)
            return false;

        var url = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? $"https://{value}" : value;
        return Uri.TryCreate(url.TrimEnd('.', ',', '!', '?', ')', ']', '}'), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && SupportedHosts.Contains(uri.Host);
    }

    static HashSet<long> ReadIds(string variable)
    {
        var ids = new HashSet<long>();
        var values = Environment.GetEnvironmentVariable(variable)?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        foreach (var value in values)
        {
            if (long.TryParse(value, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
            else
                Console.Error.WriteLine($"Save Bot skipped an invalid chat ID in {variable}.");
        }
        return ids;
    }

    async Task RunWorkerAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));

        var temp = Directory.CreateTempSubdirectory("savebot-worker-");
        try
        {
            var start = new ProcessStartInfo(Python)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(Worker);
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
            start.Environment["TTOKEN"] = botToken;
            start.Environment["TMPDIR"] = temp.FullName;

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Save Bot.");
            try
            {
                await process.StandardInput.WriteAsync(MessageJson(message).AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);

                if (process.ExitCode != 0)
                    Console.Error.WriteLine($"Save Bot worker exited with code {process.ExitCode}.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    static string MessageJson(TelegramMessage message) => JsonSerializer.Serialize(new
    {
        message_id = message.MessageId,
        date = new DateTimeOffset(message.Date).ToUnixTimeSeconds(),
        chat = new { id = message.Chat.Id, type = message.Chat.Type.ToString().ToLowerInvariant() },
        message_thread_id = message.MessageThreadId,
        is_topic_message = message.IsTopicMessage,
        text = message.Text,
        caption = message.Caption,
        entities = LinkEntities(message.Entities).ToArray(),
        caption_entities = LinkEntities(message.CaptionEntities).ToArray(),
    });

    static IEnumerable<object> LinkEntities(MessageEntity[]? entities)
    {
        foreach (var entity in entities ?? [])
        {
            if (entity.Type is MessageEntityType.Url or MessageEntityType.TextLink)
            {
                yield return new
                {
                    type = entity.Type == MessageEntityType.Url ? "url" : "text_link",
                    offset = entity.Offset,
                    length = entity.Length,
                    url = entity.Url,
                };
            }
        }
    }
}
