using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class AIAssistant : ObservableObject
    {
        public List<string> AvailableModels
        {
            get => _service.AvailableModels;
        }

        public string CurrentModel
        {
            get => _service.Model;
            set => _service.Model = value;
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            private set => SetProperty(ref _isGenerating, value);
        }

        public bool CanUseResponse
        {
            get => _mode == Mode.CommitMessage;
        }

        public bool HasReviewResult => _reviewResult != null;

        public bool ShowRawText => _reviewResult == null;

        public AIReviewResult ReviewResult
        {
            get => _reviewResult;
            private set
            {
                if (SetProperty(ref _reviewResult, value))
                {
                    OnPropertyChanged(nameof(HasReviewResult));
                    OnPropertyChanged(nameof(ShowRawText));
                    OnPropertyChanged(nameof(CopyText));
                    OnPropertyChanged(nameof(CanCopyResponse));
                }
            }
        }

        public string Text
        {
            get => _text;
            private set => SetProperty(ref _text, value);
        }

        public string Response
        {
            get => _response;
            private set
            {
                if (SetProperty(ref _response, value))
                {
                    OnPropertyChanged(nameof(CopyText));
                    OnPropertyChanged(nameof(CanCopyResponse));
                }
            }
        }

        public string CopyText => ReviewResult?.CopyText ?? Response;

        public bool CanCopyResponse => !string.IsNullOrWhiteSpace(CopyText);

        public AIAssistant(Repository repo, AI.Service service, List<Models.Change> changes)
            : this(repo, service, changes, Mode.CommitMessage)
        {
        }

        public AIAssistant(Repository repo, AI.Service service, List<Models.Change> changes, Mode mode)
        {
            _repo = repo;
            _service = service;
            _mode = mode;
            _cancel = new CancellationTokenSource();
            _reviewTargetName = "staged changes";
            _reviewTargetContext = "Review target: staged changes in the Git index.";

            var builder = new StringBuilder();
            foreach (var c in changes)
                SerializeChange(c, builder);
            _changeList = builder.ToString();
        }

        public AIAssistant(Repository repo, AI.Service service, Models.Commit commit, List<Models.Change> changes)
            : this(repo, service, changes, Mode.Review)
        {
            var shortSHA = commit.SHA.Length > 10 ? commit.SHA.Substring(0, 10) : commit.SHA;
            _reviewTargetName = $"commit {shortSHA}";
            _reviewTargetContext = BuildCommitReviewTargetContext(commit);
        }

        public async Task GenAsync()
        {
            if (_cancel is { IsCancellationRequested: false })
                _cancel.Cancel();
            _cancel = new CancellationTokenSource();

            var agent = new AI.Agent(_service);
            var builder = new StringBuilder();
            builder.AppendLine(_mode == Mode.CommitMessage ? "Asking AI to generate commit message..." : $"Asking AI to review {_reviewTargetName}...").AppendLine();

            var responseBuilder = new StringBuilder();
            var foundResponse = false;

            Text = builder.ToString();
            Response = string.Empty;
            ReviewResult = null;
            IsGenerating = true;

            try
            {
                Action<string> onUpdate = message =>
                {
                    builder.AppendLine(message);

                    if (foundResponse)
                    {
                        if (message.Equals("# Token Usage", StringComparison.Ordinal))
                            foundResponse = false;
                        else
                            responseBuilder.AppendLine(message);
                    }
                    else if (message.Equals("# Assistant", StringComparison.Ordinal))
                    {
                        foundResponse = true;
                    }

                    Dispatcher.UIThread.Post(() => Text = builder.ToString());
                };

                if (_mode == Mode.CommitMessage)
                    await agent.GenerateCommitMessageAsync(_repo.FullPath, _changeList, onUpdate, _cancel.Token);
                else
                    await agent.GenerateReviewAsync(_repo.FullPath, _changeList, _reviewTargetContext, onUpdate, _cancel.Token);

                Response = responseBuilder.ToString().Trim();
                if (_mode == Mode.Review)
                    ReviewResult = AIReviewResult.TryParse(Response);
            }
            catch (OperationCanceledException)
            {
                // Do nothing and leave `IsGenerating` to current (may already changed), so that the UI can update accordingly.
                return;
            }
            catch (Exception e)
            {
                builder
                    .AppendLine()
                    .AppendLine("[ERROR]")
                    .Append(e.Message);

                Text = builder.ToString();
                Response = string.Empty;
                ReviewResult = null;
            }

            IsGenerating = false;
        }

        public void Use(string text)
        {
            _repo.SetCommitMessage(text);
        }

        public void Cancel()
        {
            _cancel?.Cancel();
        }

        public void DismissFinding(AIReviewFinding finding)
        {
            if (finding == null || ReviewResult?.Findings == null)
                return;

            if (ReviewResult.Findings.Remove(finding))
            {
                OnPropertyChanged(nameof(ReviewResult));
                OnPropertyChanged(nameof(CopyText));
                OnPropertyChanged(nameof(CanCopyResponse));
            }
        }

        private void SerializeChange(Models.Change c, StringBuilder builder)
        {
            var status = c.Index switch
            {
                Models.ChangeState.Added => "A",
                Models.ChangeState.Modified => "M",
                Models.ChangeState.Deleted => "D",
                Models.ChangeState.TypeChanged => "T",
                Models.ChangeState.Renamed => "R",
                Models.ChangeState.Copied => "C",
                _ => " ",
            };

            builder.Append(status).Append('\t');

            if (c.Index == Models.ChangeState.Renamed || c.Index == Models.ChangeState.Copied)
                builder.Append(c.OriginalPath).Append(" -> ").Append(c.Path).AppendLine();
            else
                builder.Append(c.Path).AppendLine();
        }

        private static string BuildCommitReviewTargetContext(Models.Commit commit)
        {
            var builder = new StringBuilder();
            builder
                .AppendLine("Review target: changes introduced by the following commit.")
                .Append("Commit SHA: ").AppendLine(commit.SHA)
                .Append("Base revision: ").AppendLine(commit.FirstParentToCompare)
                .Append("Target revision: ").AppendLine(commit.SHA)
                .Append("Author: ").AppendLine(commit.Author.ToString())
                .Append("Author time: ").AppendLine(Models.DateTimeFormat.Format(commit.AuthorTime))
                .Append("Subject: ").AppendLine(commit.Subject)
                .AppendLine("When you call GetDetailChangesInFile for this review, always include both baseRevision and revision with the exact values above.");

            return builder.ToString();
        }

        public enum Mode
        {
            CommitMessage,
            Review,
        }

        private readonly Repository _repo = null;
        private readonly AI.Service _service = null;
        private readonly Mode _mode = Mode.CommitMessage;
        private readonly string _changeList = null;
        private string _reviewTargetName = string.Empty;
        private string _reviewTargetContext = string.Empty;
        private CancellationTokenSource _cancel = null;
        private bool _isGenerating = false;
        private string _text = string.Empty;
        private string _response = string.Empty;
        private AIReviewResult _reviewResult = null;
    }

    public class AIReviewResult
    {
        public string Summary { get; set; } = string.Empty;
        public ObservableCollection<AIReviewFinding> Findings { get; set; } = [];
        public string ResidualRisks { get; set; } = string.Empty;
        public bool HasFindings => Findings.Count > 0;
        public string FindingCountText => HasFindings ? $"{Findings.Count} 个问题" : "未发现明确问题";
        public string CopyText
        {
            get
            {
                var builder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(Summary))
                    builder.AppendLine(Summary).AppendLine();

                if (!string.IsNullOrWhiteSpace(ResidualRisks))
                {
                    builder.AppendLine("Residual risks:");
                    builder.AppendLine(ResidualRisks).AppendLine();
                }

                if (Findings is { Count: > 0 })
                {
                    builder.AppendLine("Findings:");
                    for (var i = 0; i < Findings.Count; i++)
                    {
                        builder.Append(i + 1).Append(". ").AppendLine(Findings[i].CopyText);
                        if (i + 1 < Findings.Count)
                            builder.AppendLine();
                    }
                }

                return builder.ToString().Trim();
            }
        }

        public static AIReviewResult TryParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                var json = NormalizeJson(raw);
                var result = JsonSerializer.Deserialize(json, AIReviewJsonContext.Default.AIReviewResult);
                if (result == null)
                    return null;

                result.Findings ??= [];
                foreach (var finding in result.Findings)
                    finding.Normalize();

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeJson(string raw)
        {
            var text = raw.Trim();
            if (!text.StartsWith("```", StringComparison.Ordinal))
                return text;

            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd >= 0)
                text = text.Substring(firstLineEnd + 1);

            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 3);

            return text.Trim();
        }
    }

    public class AIReviewFinding
    {
        public string Severity { get; set; } = "info";
        public string File { get; set; } = string.Empty;
        public int Line { get; set; } = 0;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;

        public string SeverityLabel => Severity switch
        {
            "critical" => "Critical",
            "high" => "High",
            "medium" => "Medium",
            "low" => "Low",
            _ => "Info",
        };

        public string Location => Line > 0 ? $"{File}:{Line}" : File;
        public string CopyText
        {
            get
            {
                var builder = new StringBuilder();
                builder.Append('[').Append(SeverityLabel).Append("] ");
                if (!string.IsNullOrWhiteSpace(Location))
                    builder.Append(Location).Append(" - ");
                builder.AppendLine(Title);

                if (!string.IsNullOrWhiteSpace(Description))
                    builder.AppendLine(Description);

                if (!string.IsNullOrWhiteSpace(Suggestion))
                    builder.Append("Suggestion: ").AppendLine(Suggestion);

                return builder.ToString().Trim();
            }
        }
        public bool IsCritical => Severity == "critical";
        public bool IsHigh => Severity == "high";
        public bool IsMedium => Severity == "medium";
        public bool IsLow => Severity == "low";
        public bool IsInfo => Severity == "info";

        public void Normalize()
        {
            Severity = Severity?.Trim().ToLowerInvariant() switch
            {
                "critical" => "critical",
                "high" => "high",
                "medium" => "medium",
                "low" => "low",
                _ => "info",
            };

            File ??= string.Empty;
            Title ??= string.Empty;
            Description ??= string.Empty;
            Suggestion ??= string.Empty;
        }
    }

    [JsonSerializable(typeof(AIReviewResult))]
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    internal partial class AIReviewJsonContext : JsonSerializerContext
    {
    }

}
