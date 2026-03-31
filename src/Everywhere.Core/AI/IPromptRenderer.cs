namespace Everywhere.AI;

public interface IPromptRenderer
{
    string RenderPrompt(string prompt);

    string RenderStrategyUserPrompt(string userMessage, string? argument);
}