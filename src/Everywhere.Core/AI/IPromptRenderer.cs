using Everywhere.StrategyEngine;

namespace Everywhere.AI;

public interface IPromptRenderer
{
    string RenderPrompt(string prompt);

    string RenderStrategyUserPrompt(string strategyBody, string? userInput, PreprocessorResult? preprocessorResult);
}