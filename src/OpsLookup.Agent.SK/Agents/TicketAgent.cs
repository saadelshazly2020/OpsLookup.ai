using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpsLookup.Agent.Data;
using OpsLookup.Agent.Plugins;

namespace OpsLookup.Agent.Agents;

public sealed record TurnRequest(string ThreadId, string Message, string? Locale);

public sealed record TurnResult(
    string ThreadId,
    string Reply,
    int ToolCalls,
    bool LookupFailed,
    bool Error);

/// <summary>
/// The agent loop: prompt -> model -> (tool calls -> model)* -> reply.
/// </summary>
/// <remarks>
/// <para>
/// The direct replacement for the LangGraph state machine, and deliberately the
/// same shape. Two ways out of the model: tool calls present means loop again,
/// plain text means that text is the reply. A clarifying question therefore takes
/// the identical code path as a final answer.
/// </para>
/// <para>
/// The loop is written out rather than handed to <c>FunctionInvokingChatClient</c>.
/// That wrapper would work, but it hides the tool round inside the client, and
/// this application has to report how many tools ran and whether any of them
/// *failed* -- two facts that exist only between a call and its result.
/// </para>
/// <para>
/// A fresh <see cref="Kernel"/> is built per turn. It is only a function
/// registry, so it costs nothing, and it is what lets the per-turn bearer token
/// live in a constructor field rather than in ambient state that concurrent turns
/// on other tickets could collide with.
/// </para>
/// </remarks>
public sealed class TicketAgent(
    IChatCompletionService chat,
    LookupClient lookup,
    ConversationStore store,
    ILogger<TicketAgent> logger)
{
    /// <summary>
    /// Model calls allowed per turn. Each tool round costs two, so 10 permits
    /// four lookups plus the final answer. Matches the graph's limit, which was
    /// the setting under which a fifth lookup failed the whole turn.
    /// </summary>
    public const int DefaultMaxModelCalls = 10;

    public async Task<TurnResult> RunAsync(
        TurnRequest request, string bearerToken, CancellationToken ct = default)
    {
        var stored = await store.LoadAsync(request.ThreadId, ct);
        var conversation = BuildHistory(stored, request.Message, request.Locale);

        var plugin = new LookupPlugin(lookup, bearerToken);
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Plugins.AddFromObject(plugin, "lookup");
        var kernel = kernelBuilder.Build();

        var settings = new PromptExecutionSettings
        {
            // Auto, not Required. The model has to stay free to answer a
            // clarifying question with no tool call at all, which is the common
            // case when a member has not given a national unified ID.
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                kernel.Plugins.SelectMany(p => p)),
        };

        var transcript = new List<StoredMessage> { new("user", request.Message) };
        var toolCalls = 0;
        var lookupFailed = false;
        var modelCalls = 0;

        while (true)
        {
            if (++modelCalls > DefaultMaxModelCalls)
            {
                // Recorded rather than thrown: the caller still gets an
                // answer-shaped response, and the member reads something that
                // explains itself instead of a request that failed.
                var giveUp = ReplyForLanguage(request.Locale,
                    $"I could not finish this lookup within {DefaultMaxModelCalls} model calls. "
                    + "Please ask for one thing at a time, for example a single national unified ID.",
                    "لم أستطع إتمام هذا البحث ضمن عدد محدود من المحاولات. "
                    + "من فضلك اسأل عن شيء واحد في كل مرة، مثل رقم وطني موحد واحد.");

                transcript.Add(new StoredMessage("assistant", giveUp));
                await store.AppendAsync(request.ThreadId, transcript, ct);
                return new TurnResult(request.ThreadId, giveUp, toolCalls, lookupFailed, Error: true);
            }

            // The connector invokes the tools itself when a Kernel is supplied -- SK
            // rejects FunctionChoiceBehavior.Auto without one. That is acceptable:
            // the plugin holds this turn's token in a field, so an auto-invoked
            // call is scoped correctly anyway. What has to be reconstructed is
            // the reporting, so the function call ids present before the call are
            // snapshotted and whatever appears afterwards is this turn's work.
            var seenCallIds = conversation
                .SelectMany(m => m.Items.OfType<FunctionCallContent>())
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);

            var responses = await chat.GetChatMessageContentsAsync(conversation, settings, kernel, ct);
            var last = responses.Count > 0 ? responses[^1] : null;

            // Counted from the results the connector actually recorded, not from
            // the number of calls made. An invocation that failed still counts
            // towards toolCalls, but must not let the reply claim it was answered
            // from the database.
            var newCalls = conversation
                .SelectMany(m => m.Items.OfType<FunctionCallContent>())
                .Where(c => !seenCallIds.Contains(c.Id))
                .ToList();

            toolCalls += newCalls.Count;

            foreach (var result in conversation.SelectMany(m => m.Items.OfType<FunctionResultContent>()))
            {
                if (LookupPlugin.IsErrorResult(result.Result?.ToString()))
                {
                    lookupFailed = true;
                }
            }

            if (newCalls.Count == 0)
            {
                // No tools this round: the model's text is the reply. A
                // clarifying question takes this same path as a final answer.
                var reply = last?.Content ?? string.Empty;
                transcript.Add(new StoredMessage("assistant", reply));
                await store.AppendAsync(request.ThreadId, transcript, ct);
                return new TurnResult(request.ThreadId, reply, toolCalls, lookupFailed, Error: false);
            }

            // A tool round happened. The connector already appended the calls and
            // their results to `conversation`; they are copied into the stored
            // transcript so the next turn replays them.
            foreach (var call in newCalls)
            {
                var text = ResultText(call, conversation);
                logger.LogInformation(
                    "tool {Tool} -> {Result}",
                    call.FunctionName,
                    text.Length > 200 ? text[..200] + "..." : text);

                transcript.Add(new StoredMessage("tool", text, call.FunctionName, call.Id));
            }

            // Persisted as soon as the round completes, so a turn that dies later
            // leaves a coherent record rather than nothing. Scoped to this turn:
            // history is replayed from disk, so an earlier failure on the same
            // ticket would otherwise mark this turn's good answer as failed.
            await store.AppendAsync(request.ThreadId, transcript, ct);
            transcript.Clear();
        }
    }

    /// <summary>
    /// The result recorded for a call, matched on call id.
    /// </summary>
    private static string ResultText(FunctionCallContent call, ChatHistory history)
    {
        var result = history
            .SelectMany(m => m.Items.OfType<FunctionResultContent>())
            .LastOrDefault(r => r.CallId == call.Id);

        return result?.Result?.ToString() ?? string.Empty;
    }

    private async Task<string> InvokeAsync(Kernel kernel, FunctionCallContent call, CancellationToken ct)
    {
        try
        {
            // Resolved by hand because the plugin name is optional on a call: a
            // model may send a bare function name, and InvokeAsync's string
            // overload wants a KernelFunction, not a name.
            var function = call.PluginName is { Length: > 0 } pluginName
                && kernel.Plugins.TryGetPlugin(pluginName, out var plugin)
                ? plugin[call.FunctionName]
                : kernel.Plugins.SelectMany(p => p).First(f => f.Name == call.FunctionName);

            var result = await kernel.InvokeAsync(function, call.Arguments, ct);
            return result.ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool that throws must not take the turn with it. The model sees
            // the failure as text and explains it, which is the behaviour the
            // Python tools had when they returned {"error": ...}.
            logger.LogWarning(ex, "Tool {Tool} failed", call.FunctionName);
            return JsonSerializer.Serialize(
                new { error = $"The lookup tool '{call.FunctionName}' failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Rebuilds the conversation from storage, with this turn's system prompt
    /// prepended and the new user message appended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prompt is resolved per turn rather than once at construction. The
    /// locale arrives on the request and can differ between turns on the same
    /// ticket, so a prompt captured in a field would freeze the thread to
    /// whichever language it started in.
    /// </para>
    /// <para>
    /// Stored tool calls and results are replayed as a paired sequence matched
    /// on call id. A call with no result is dropped rather than sent alone:
    /// providers reject a tool call with no matching tool message, and that
    /// surfaces as an opaque 400 from the model rather than a clear
    /// corrupt-history error.
    /// </para>
    /// </remarks>
    private static ChatHistory BuildHistory(
        IReadOnlyList<StoredMessage> stored, string userMessage, string? locale)
    {
        var conversation = new ChatHistory(SystemPrompts.For(locale));

        var results = new Dictionary<string, (string Name, string Content)>(StringComparer.Ordinal);
        foreach (var message in stored)
        {
            if (message.Role == "tool" && message.CallId is not null && message.Name is not null)
            {
                results[message.CallId] = (message.Name, message.Content);
            }
        }

        foreach (var message in stored)
        {
            switch (message.Role)
            {
                case "assistant" when message.CallId is not null && message.Name is not null:
                    if (results.Remove(message.CallId, out var pair))
                    {
                        // A function call is carried as the items of an assistant
                        // message; FunctionResultContent is a KernelContent with
                        // its own ToChatMessage(), so the result goes through that.
                        var callItems = new ChatMessageContentItemCollection
                        {
                            new FunctionCallContent(message.CallId, pair.Name, null),
                        };
                        conversation.Add(new ChatMessageContent(AuthorRole.Assistant, callItems));
                        conversation.Add(
                            new FunctionResultContent(message.CallId, null, pair.Name, pair.Content)
                                .ToChatMessage());
                    }
                    break;

                case "tool":
                    // Handled above; anything left here has no matching call.
                    break;

                case "user":
                    conversation.AddUserMessage(message.Content);
                    break;

                case "assistant" when !string.IsNullOrWhiteSpace(message.Content):
                    conversation.AddAssistantMessage(message.Content);
                    break;

                case "system":
                    // Skipped on purpose: the prompt is rebuilt from the current
                    // locale, and replaying an old one would pin the thread to
                    // its first language.
                    break;
            }
        }

        conversation.AddUserMessage(userMessage);
        return conversation;
    }

    private static string ReplyForLanguage(string? locale, string en, string ar) =>
        (locale ?? string.Empty).Trim().Equals("en", StringComparison.OrdinalIgnoreCase) ? en : ar;
}