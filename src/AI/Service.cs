using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenAI;
using OpenAI.Chat;

namespace SourceGit.AI
{
    public class Service : ObservableObject
    {
        public const string DefaultAIReviewPrompt = """
You are an expert code reviewer. Review the staged git changes before commit.

Focus on actionable issues only:
- Correctness bugs, edge cases, data loss risks, and regressions.
- Performance problems such as unnecessary full scans, blocking UI work, repeated process launches, excessive allocations, or avoidable large diff/file reads.
- Async/lifecycle/threading hazards, especially UI thread blocking and cancellation handling.
- Security, privacy, and unsafe command/path handling.
- Missing tests only when the changed behavior is risky.

Use the detailed-change tool only for files that are relevant to a concrete finding; do not read every file blindly when the change list is large. Skip generated/binary files unless their metadata is suspicious.

Output in Chinese. Put findings first, ordered by severity. For each finding include file path, severity, why it matters, and a concrete fix suggestion. If no actionable issue is found, say so briefly and mention residual risks.
""";

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Server
        {
            get;
            set;
        } = string.Empty;

        public string ApiKey
        {
            get;
            set;
        } = string.Empty;

        public bool ReadApiKeyFromEnv
        {
            get;
            set;
        } = false;

        [JsonIgnore]
        public List<string> AvailableModels
        {
            get;
            private set;
        } = [];

        public string Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }

        public bool AutoFetchAvailableModels
        {
            get => _autoFetchAvailableModels;
            set => SetProperty(ref _autoFetchAvailableModels, value);
        }

        public string AdditionalPrompt
        {
            get;
            set;
        } = string.Empty;

        public string AIReviewPrompt
        {
            get;
            set;
        } = DefaultAIReviewPrompt;

        public void FetchAvailableModels()
        {
            if (!_autoFetchAvailableModels)
            {
                if (!string.IsNullOrEmpty(Model))
                    AvailableModels = [Model];
                return;
            }

            var allModels = GetOpenAIClient().GetOpenAIModelClient().GetModels();
            AvailableModels = new List<string>();
            foreach (var model in allModels.Value)
                AvailableModels.Add(model.Id);

            if (AvailableModels.Count > 0 && (string.IsNullOrEmpty(Model) || !AvailableModels.Contains(Model)))
                Model = AvailableModels[0];
        }

        public ChatClient GetChatClient()
        {
            return !string.IsNullOrEmpty(Model) ? GetOpenAIClient().GetChatClient(Model) : null;
        }

        private OpenAIClient GetOpenAIClient()
        {
            var credential = new ApiKeyCredential(ReadApiKeyFromEnv ? Environment.GetEnvironmentVariable(ApiKey) : ApiKey);
            return Server.Contains("openai.azure.com/", StringComparison.Ordinal)
                ? new AzureOpenAIClient(new Uri(Server), credential)
                : new OpenAIClient(credential, new() { Endpoint = new Uri(Server) });
        }

        private string _name = string.Empty;
        private string _model = string.Empty;
        private bool _autoFetchAvailableModels = true;
    }
}
