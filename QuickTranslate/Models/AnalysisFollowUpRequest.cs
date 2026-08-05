namespace QuickTranslate.Models;

public sealed record ChatCompletionMessage(string Role, string Content);

public sealed record AnalysisFollowUpExchange(string Question, string Answer);

public sealed record AnalysisSemanticSnapshot(string SystemPrompt, string TargetLanguage);

public sealed record AnalysisFollowUpRequest(
    int TurnNumber,
    IReadOnlyList<ChatCompletionMessage> Messages,
    string ApiBaseUrl,
    string ApiKey,
    string ModelName,
    int QuestionLength,
    int ContextCharacters);
