using Microsoft.SemanticKernel;

namespace Everywhere.Extensions;

public static class SemanticKernelExtension
{
    extension(StreamingKernelContent content)
    {
        /// <summary>
        /// Indicates whether the current content is generated from reasoning process.
        /// This is determined by checking if the content's metadata contains a key "reasoning" with a value of true.
        /// </summary>
        public bool IsReasoning => content.Metadata?.TryGetValue("reasoning", out var reasoning) is true && reasoning is true;
    }
}