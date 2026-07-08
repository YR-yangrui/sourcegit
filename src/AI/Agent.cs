using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace SourceGit.AI
{
    public class Agent
    {
        public Agent(Service service)
        {
            _service = service;
        }

        public async Task GenerateCommitMessageAsync(string repo, string changeList, Action<string> onUpdate, CancellationToken cancellation)
        {
            var chatClient = _service.GetChatClient();
            if (chatClient == null)
                throw new Exception("Failed to fetch available models from this service. Please check your configuration and try again.");

            var options = new ChatCompletionOptions() { Tools = { ChatTools.GetDetailChangesInFile } };
            var userMessageBuilder = new StringBuilder();
            userMessageBuilder
                .AppendLine("Generate a commit message (follow the rule of conventional commit message) for given git repository.")
                .AppendLine("- Read all given changed files before generating. Only binary files (such as images, audios ...) can be skipped.")
                .AppendLine("- Output the conventional commit message (with detail changes in list) directly. Do not explain your output nor introduce your answer.")
                .AppendLine(_service.AdditionalPrompt)
                .Append("Repository path: ").AppendLine(repo.Quoted())
                .AppendLine("Changed files ('A' means added, 'M' means modified, 'D' means deleted, 'T' means type changed, 'R' means renamed, 'C' means copied): ")
                .Append(changeList);

            var messages = new List<ChatMessage>() { new UserChatMessage(userMessageBuilder.ToString()) };
            await CompleteWithToolsAsync(chatClient, messages, options, onUpdate, cancellation);
        }

        public async Task GenerateReviewAsync(string repo, string changeList, string reviewTargetContext, Action<string> onUpdate, CancellationToken cancellation)
        {
            var chatClient = _service.GetChatClient();
            if (chatClient == null)
                throw new Exception("Failed to fetch available models from this service. Please check your configuration and try again.");

            var prompt = string.IsNullOrWhiteSpace(_service.AIReviewPrompt) ? Service.DefaultAIReviewPrompt : _service.AIReviewPrompt;
            var options = new ChatCompletionOptions() { Tools = { ChatTools.GetDetailChangesInFile } };
            var userMessageBuilder = new StringBuilder();
            userMessageBuilder
                .AppendLine(prompt)
                .AppendLine()
                .AppendLine(Service.AIReviewOutputContractPrompt)
                .AppendLine()
                .Append("Repository path: ").AppendLine(repo.Quoted())
                .AppendLine(reviewTargetContext)
                .AppendLine("Changed files ('A' means added, 'M' means modified, 'D' means deleted, 'T' means type changed, 'R' means renamed, 'C' means copied): ")
                .Append(changeList);

            var messages = new List<ChatMessage>() { new UserChatMessage(userMessageBuilder.ToString()) };
            await CompleteWithToolsAsync(chatClient, messages, options, onUpdate, cancellation);
        }

        private static async Task CompleteWithToolsAsync(ChatClient chatClient, List<ChatMessage> messages, ChatCompletionOptions options, Action<string> onUpdate, CancellationToken cancellation)
        {
            do
            {
                var completion = await CompleteChatStreamingAsync(chatClient, messages, options, cancellation);
                var inProgress = false;

                switch (completion.FinishReason.GetValueOrDefault())
                {
                    case ChatFinishReason.Stop:
                        if (onUpdate != null)
                        {
                            onUpdate.Invoke(string.Empty);
                            onUpdate.Invoke("# Assistant");
                            if (!string.IsNullOrEmpty(completion.Content))
                            {
                                var text = completion.Content.ReplaceLineEndings("\n").Trim();
                                var start = 0;
                                var len = text.Length;
                                if (text.StartsWith("```", StringComparison.Ordinal))
                                {
                                    var idx = text.IndexOf('\n') + 1;
                                    start += idx;
                                    len -= idx;
                                }

                                if (text.EndsWith("\n```", StringComparison.Ordinal))
                                    len -= 4;

                                if (len > 0)
                                    onUpdate.Invoke(text.Substring(start, len));
                                else
                                    onUpdate.Invoke("[No content was generated.]");
                            }
                            else
                            {
                                onUpdate.Invoke("[No content was generated.]");
                            }

                            onUpdate.Invoke(string.Empty);
                            onUpdate.Invoke("# Token Usage");
                            if (completion.Usage != null)
                                onUpdate.Invoke($"Total: {completion.Usage.TotalTokenCount}. Input: {completion.Usage.InputTokenCount}. Output: {completion.Usage.OutputTokenCount}");
                            else
                                onUpdate.Invoke("Not provided by the streaming response.");
                        }
                        break;
                    case ChatFinishReason.Length:
                        throw new Exception("The response was cut off because it reached the maximum length. Consider increasing the max tokens limit.");
                    case ChatFinishReason.ToolCalls:
                        {
                            messages.Add(new AssistantChatMessage(completion.ToolCalls));

                            foreach (var call in completion.ToolCalls)
                            {
                                var result = await ChatTools.ProcessAsync(call, onUpdate);
                                messages.Add(result);
                            }

                            inProgress = true;
                            break;
                        }
                    case ChatFinishReason.ContentFilter:
                        throw new Exception("Omitted content due to a content filter flag");
                    default:
                        break;
                }

                if (!inProgress)
                    break;
            } while (true);
        }

        private static async Task<StreamingCompletion> CompleteChatStreamingAsync(ChatClient chatClient, List<ChatMessage> messages, ChatCompletionOptions options, CancellationToken cancellation)
        {
            var content = new StringBuilder();
            var toolCalls = new List<StreamingToolCallBuilder>();
            ChatTokenUsage usage = null;
            ChatFinishReason? finishReason = null;

            await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();

                foreach (var part in update.ContentUpdate)
                    content.Append(part.Text);

                foreach (var toolCallUpdate in update.ToolCallUpdates)
                {
                    // Tool calls stream as deltas. Rebuild each call before adding the assistant
                    // tool-call message, otherwise the follow-up tool result cannot be matched.
                    var builder = FindOrCreateToolCallBuilder(toolCalls, toolCallUpdate.ToolCallId);
                    builder.Append(toolCallUpdate);
                }

                if (update.Usage != null)
                    usage = update.Usage;

                if (update.FinishReason.HasValue)
                    finishReason = update.FinishReason;
            }

            return new StreamingCompletion()
            {
                Content = content.ToString(),
                ToolCalls = BuildToolCalls(toolCalls),
                FinishReason = finishReason,
                Usage = usage,
            };
        }

        private static StreamingToolCallBuilder FindOrCreateToolCallBuilder(List<StreamingToolCallBuilder> toolCalls, string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                foreach (var item in toolCalls)
                {
                    if (item.Id.Equals(id, StringComparison.Ordinal))
                        return item;
                }
            }
            else if (toolCalls.Count > 0)
            {
                // Some OpenAI-compatible streaming servers send ToolCallId only on the first
                // delta. Treat later id-less deltas as a continuation of the latest tool call.
                return toolCalls[^1];
            }

            var builder = new StreamingToolCallBuilder(id);
            toolCalls.Add(builder);
            return builder;
        }

        private static List<ChatToolCall> BuildToolCalls(List<StreamingToolCallBuilder> builders)
        {
            var toolCalls = new List<ChatToolCall>();
            foreach (var builder in builders)
            {
                var call = builder.Build();
                if (call != null)
                    toolCalls.Add(call);
            }

            return toolCalls;
        }

        private class StreamingCompletion
        {
            public string Content { get; set; } = string.Empty;
            public List<ChatToolCall> ToolCalls { get; set; } = [];
            public ChatFinishReason? FinishReason { get; set; }
            public ChatTokenUsage Usage { get; set; }
        }

        private class StreamingToolCallBuilder(string id)
        {
            public string Id { get; private set; } = id ?? string.Empty;

            public void Append(StreamingChatToolCallUpdate update)
            {
                if (string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(update.ToolCallId))
                    Id = update.ToolCallId;

                if (!string.IsNullOrEmpty(update.FunctionName))
                    _functionName = update.FunctionName;

                if (update.FunctionArgumentsUpdate != null)
                    _arguments.Append(update.FunctionArgumentsUpdate);
            }

            public ChatToolCall Build()
            {
                if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(_functionName))
                    return null;

                return ChatToolCall.CreateFunctionToolCall(Id, _functionName, BinaryData.FromString(_arguments.ToString()));
            }

            private string _functionName = string.Empty;
            private readonly StringBuilder _arguments = new();
        }

        private readonly Service _service;
    }
}
