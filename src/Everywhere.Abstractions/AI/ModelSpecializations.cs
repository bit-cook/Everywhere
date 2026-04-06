namespace Everywhere.AI;

/// <summary>
/// ModelSpecializations represents specific capabilities or optimizations that an AI model may have for certain tasks.
/// These specializations can be used to identify models that are particularly well-suited for specific use cases, such as generating titles or compressing context.
/// </summary>
[Flags]
public enum ModelSpecializations : uint
{
    None = 0x0,

    TitleGeneration = 0x1,
    ContextCompression = 0x2,
}