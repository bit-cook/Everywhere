using Everywhere.Chat;
using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;
using ZLinq;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for file attachment contexts.
/// Provides commands based on file types.
/// </summary>
public sealed class FileStrategy : StrategyBase, IBuiltInStrategy
{
    public override string Id => "builtin.file";
    public override IDynamicResourceKey Name { get; } = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_Name);
    public override IDynamicResourceKey Description { get; } = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_Description);
    public override int Priority => 40;

    protected override IStrategyCondition Condition =>
        new FileCondition { MinCount = 1 };

    public override IEnumerable<StrategyCommand> GetCommands(StrategyContext context)
    {
        var files = context.Attachments.AsValueEnumerable().OfType<FileAttachment>();
        var fileCount = files.Count();
        if (fileCount == 0)
        {
            yield break;
        }

        // Analyze file type distribution
        var extensions = files
            .Select(f => Path.GetExtension(f.FilePath).ToLowerInvariant())
            .Where(e => !string.IsNullOrEmpty(e))
            .ToHashSet();

        var hasDocuments = extensions.Any(e => DocumentExtensions.Contains(e));
        var hasImages = extensions.Any(e => ImageExtensions.Contains(e));
        var hasData = extensions.Any(e => DataExtensions.Contains(e));
        var hasCode = extensions.Any(e => CodeExtensions.Contains(e));

        // Universal: Summarize file(s)
        yield return new StrategyCommand
        {
            Id = "summarize",
            Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_SummarizeCommand_Name),
            Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_SummarizeCommand_Description),
            Icon = LucideIconKind.FileText,
            Priority = 100,
            SystemPrompt = """
                You are an expert at analyzing and summarizing files.
                Summarize the provided file(s), highlighting:
                - Main content and purpose
                - Key information or findings
                - Notable sections or structure
                Adjust your summary based on the file type.
                """,
            UserMessage = "Please summarize this file."
        };

        // Document-specific commands
        if (hasDocuments)
        {
            yield return new StrategyCommand
            {
                Id = "extract-key-points",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_ExtractKeyPointsCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_ExtractKeyPointsCommand_Description),
                Icon = LucideIconKind.ListChecks,
                Priority = 90,
                SystemPrompt = """
                    You are a document analysis expert.
                    Extract the key points from the document(s):
                    - Main arguments or findings
                    - Important facts and figures
                    - Conclusions or recommendations
                    Present them as a clear, organized list.
                    """,
                UserMessage = "Please extract the key points from this document."
            };

            yield return new StrategyCommand
            {
                Id = "translate-document",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_TranslateDocumentCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_TranslateDocumentCommand_Description),
                Icon = LucideIconKind.Languages,
                Priority = 85,
                SystemPrompt = """
                    You are a professional document translator.
                    Translate the document content while:
                    - Preserving formatting and structure
                    - Maintaining technical accuracy
                    - Keeping the original tone
                    """,
                UserMessage = "Please translate this document."
            };
        }

        // Image-specific commands
        if (hasImages)
        {
            yield return new StrategyCommand
            {
                Id = "describe-image",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_DescribeImageCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_DescribeImageCommand_Description),
                Icon = LucideIconKind.Image,
                Priority = 95,
                SystemPrompt = """
                    You are an expert at image analysis and description.
                    Describe the image in detail:
                    - Main subjects and objects
                    - Colors, composition, and style
                    - Any text visible in the image
                    - Context or setting
                    """,
                UserMessage = "Please describe this image."
            };

            yield return new StrategyCommand
            {
                Id = "extract-text-ocr",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_ExtractTextCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_ExtractTextCommand_Description),
                Icon = LucideIconKind.ScanText,
                Priority = 90,
                SystemPrompt = """
                    You are an OCR specialist.
                    Extract all visible text from the image.
                    Preserve the layout and structure as much as possible.
                    If text is unclear, indicate uncertainty.
                    """,
                UserMessage = "Please extract the text from this image."
            };
        }

        // Data file commands
        if (hasData)
        {
            yield return new StrategyCommand
            {
                Id = "analyze-data",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_AnalyzeDataCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_AnalyzeDataCommand_Description),
                Icon = LucideIconKind.ChartBar,
                Priority = 95,
                SystemPrompt = """
                    You are a data analysis expert.
                    Analyze the provided data and provide:
                    - Overview of the data structure
                    - Key statistics and patterns
                    - Notable trends or anomalies
                    - Actionable insights
                    """,
                UserMessage = "Please analyze this data."
            };

            yield return new StrategyCommand
            {
                Id = "visualize-data",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_VisualizeDataCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_VisualizeDataCommand_Description),
                Icon = LucideIconKind.ChartLine,
                Priority = 85,
                SystemPrompt = """
                    You are a data visualization expert.
                    Based on the data provided:
                    - Suggest appropriate chart types
                    - Explain what each visualization would show
                    - Provide code snippets for creating them (Python/JavaScript)
                    """,
                UserMessage = "How should I visualize this data?"
            };
        }

        // Multiple files: Compare
        if (fileCount > 1)
        {
            yield return new StrategyCommand
            {
                Id = "compare",
                Name = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_CompareCommand_Name),
                Description = new DynamicResourceKey(LocaleKey.Strategy_BuiltIn_File_CompareCommand_Description),
                Icon = LucideIconKind.GitCompare,
                Priority = 95,
                SystemPrompt = """
                    You are a file comparison expert.
                    Compare the provided files and highlight:
                    - Similarities and differences
                    - Structural changes
                    - Content additions or removals
                    Present the comparison in a clear, organized format.
                    """,
                UserMessage = "Please compare these files."
            };
        }
    }

    private static readonly HashSet<string> DocumentExtensions =
    [
        ".pdf", ".doc", ".docx", ".odt", ".rtf",
        ".txt", ".md", ".markdown",
        ".ppt", ".pptx", ".odp"
    ];

    private static readonly HashSet<string> ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp",
        ".webp", ".svg", ".ico", ".tiff", ".tif"
    ];

    private static readonly HashSet<string> DataExtensions =
    [
        ".csv", ".xlsx", ".xls", ".json", ".xml",
        ".parquet", ".sqlite", ".db"
    ];

    private static readonly HashSet<string> CodeExtensions =
    [
        ".py", ".js", ".ts", ".jsx", ".tsx",
        ".cs", ".java", ".kt", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp",
        ".rb", ".php", ".swift", ".scala"
    ];
}
