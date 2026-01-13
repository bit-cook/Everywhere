#if DEBUG
// #define DEBUG_VISUAL_TREE_BUILDER
#endif

using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Everywhere.Interop;
using ZLinq;
#if DEBUG_VISUAL_TREE_BUILDER
using System.Diagnostics;
using Everywhere.Chat.Debugging;
#endif

namespace Everywhere.Chat;

public enum VisualTreeDetailLevel
{
    [DynamicResourceKey(LocaleKey.VisualTreeDetailLevel_Minimal)]
    Minimal,

    [DynamicResourceKey(LocaleKey.VisualTreeDetailLevel_Compact)]
    Compact,

    [DynamicResourceKey(LocaleKey.VisualTreeDetailLevel_Detailed)]
    Detailed,
}

/// <summary>
///     This class builds an XML representation of the core elements, which is limited by the soft token limit and finally used by a LLM.
/// </summary>
/// <param name="coreElements"></param>
/// <param name="approximateTokenLimit"></param>
/// <param name="detailLevel"></param>
public partial class VisualTreeXmlBuilder(
    IReadOnlyList<IVisualElement> coreElements,
    int approximateTokenLimit,
    int startingId,
    VisualTreeDetailLevel detailLevel
)
{
    private static readonly ActivitySource ActivitySource = new(typeof(VisualTreeXmlBuilder).FullName.NotNull());

    /// <summary>
    /// Builds the XML representation of the visual tree for the given attachments as core elements.
    /// It groups the visual elements from the attachments and constructs the XML using a weighted priority traversal algorithm.
    /// </summary>
    /// <param name="attachments"></param>
    /// <param name="approximateTokenLimit"></param>
    /// <param name="startingId"></param>
    /// <param name="detailLevel"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static IReadOnlyDictionary<int, IVisualElement> BuildPopulateXml(
        IReadOnlyList<ChatVisualElementAttachment> attachments,
        int approximateTokenLimit,
        int startingId,
        VisualTreeDetailLevel detailLevel,
        CancellationToken cancellationToken)
    {
        using var builderActivity = ActivitySource.StartActivity();

        var result = new Dictionary<int, IVisualElement>();
        var validAttachments = attachments
            .AsValueEnumerable()
            .Select(x => (Attachment: x, Element: x.Element?.Target))
            .Where(t => t.Element is not null)
            .ToList();

        if (validAttachments.Count == 0)
        {
            return result;
        }

        // 1. Group core elements by their root element. Key is tuple (ProcessId, NativeWindowHandle of the ancestor TopLevel)
        var groups = validAttachments
            .AsValueEnumerable()
            .GroupBy(x =>
            {
                var current = x.Element!;
                while (current is { Type: not VisualElementType.Screen and not VisualElementType.TopLevel, Parent: { } parent })
                {
                    current = parent;
                }

                return (x.Element!.ProcessId, current.NativeWindowHandle);
            })
            .ToArray();

        var totalElements = validAttachments.Count;
        var totalBuiltElements = 0;

        foreach (var group in groups.AsValueEnumerable())
        {
            var groupElements = group.AsValueEnumerable().Select(x => x.Element!).ToList();
            var groupCount = groupElements.Count;

            // 2. Build XML for each root group
            // Allocate token limit relative to the number of elements in the group
            var groupTokenLimit = (int)((long)approximateTokenLimit * groupCount / totalElements);

            var xmlBuilder = new VisualTreeXmlBuilder(
                groupElements,
                groupTokenLimit,
                startingId,
                detailLevel);

            var xml = xmlBuilder.BuildXml(cancellationToken);

            // 3. for attachments in the same group
            // First attachment gets the full XML, others got null.
            var isFirst = true;
            foreach (var (attachment, _) in group.AsValueEnumerable())
            {
                if (isFirst)
                {
                    attachment.Xml = xml;
                    isFirst = false;
                }
                else
                {
                    attachment.Xml = null;
                }
            }

            foreach (var kvp in xmlBuilder.BuiltVisualElements.AsValueEnumerable())
            {
                result[kvp.Key] = kvp.Value;
            }

            startingId += xmlBuilder.BuiltVisualElements.Count;
            totalBuiltElements += xmlBuilder.BuiltVisualElements.Count;
        }

        builderActivity?.SetTag("xml.detail_level", detailLevel);
        builderActivity?.SetTag("xml.length_limit", approximateTokenLimit);
        builderActivity?.SetTag("xml.built_visual_elements.count", totalBuiltElements);

        return result;
    }

    /// <summary>
    /// Traversal distance metrics for prioritization.
    /// Global: distance from core elements, Local: distance from the originating node.
    /// </summary>
    /// <param name="Global"></param>
    /// <param name="Local"></param>
    private readonly record struct TraverseDistance(int Global, int Local)
    {
        public static implicit operator TraverseDistance(int distance) => new(distance, distance);

        /// <summary>
        /// Resets the local distance to 1 and increments the global distance by 1.
        /// </summary>
        /// <returns></returns>
        public TraverseDistance Reset() => new(Global + 1, 1);

        /// <summary>
        /// Increments both global and local distances by 1.
        /// </summary>
        /// <returns></returns>
        public TraverseDistance Step() => new(Global + 1, Local + 1);
    }

    /// <summary>
    /// Defines the direction of traversal in the visual element tree.
    /// It determines how a queued node is expanded.
    /// </summary>
    private enum TraverseDirection
    {
        /// <summary>
        /// Core elements
        /// </summary>
        Core,

        /// <summary>
        /// parent, previous sibling, next sibling
        /// </summary>
        Parent,

        /// <summary>
        /// previous sibling, child
        /// </summary>
        PreviousSibling,

        /// <summary>
        /// next sibling, child
        /// </summary>
        NextSibling,

        /// <summary>
        /// next child, child
        /// </summary>
        Child
    }

    /// <summary>
    /// Represents a node in the traversal queue with a calculated priority score.
    /// </summary>
    private readonly record struct TraversalNode(
        IVisualElement Element,
        IVisualElement? Previous,
        TraverseDistance Distance,
        TraverseDirection Direction,
        int SiblingIndex,
        IEnumerator<IVisualElement> Enumerator
    )
    {
        public string? ParentId { get; } = Element.Parent?.Id;

        /// <summary>
        /// Calculates the final priority score for the Best-First Search algorithm.
        /// Lower value means higher priority (Min-Heap).
        /// <para>
        /// The scoring formula is a multi-dimensional weighted product:
        /// <br/>
        /// <c>FinalScore = -(TopologyScore * IntrinsicScore)</c>
        /// </para>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>1. Topology Score (Distance Decay):</b>
        /// Represents the relevance of the element based on its position in the tree relative to the Core Element.
        /// <br/>
        /// <c>Score_topo = BaseScore / (Distance + 1)</c>
        /// <br/>
        /// - Spine nodes (Ancestors) get a 2x boost.
        /// - Non-spine nodes decay linearly with distance.
        /// </para>
        /// <para>
        /// <b>2. Intrinsic Score (Type Weight):</b>
        /// Represents the inherent importance of the element type.
        /// <br/>
        /// - Interactive controls (Button, Input): 1.5x
        /// - Semantic text (Label): 1.2x
        /// - Containers: 1.0x
        /// - Decorative: 0.5x
        /// </para>
        /// <para>
        /// <b>3. Intrinsic Score (Size Weight):</b>
        /// Represents the visual prominence of the element.
        /// <br/>
        /// <c>Score_size = 1.0 + (Area / ScreenArea)</c>
        /// <br/>
        /// Larger elements are considered more important context.
        /// </para>
        /// <para>
        /// <b>4. Noise Penalty:</b>
        /// Tiny elements (&lt; 5px) receive a 0.1x penalty to filter out visual noise.
        /// </para>
        /// </remarks>
        public float GetScore()
        {
            // Core elements have the highest priority
            if (Direction == TraverseDirection.Core) return float.NegativeInfinity;

            // 1. Base score based on topology
            var score = Direction switch
            {
                TraverseDirection.Parent => 2000.0f,
                TraverseDirection.PreviousSibling => 10000f,
                TraverseDirection.NextSibling => 10000f,
                TraverseDirection.Child => 1000.0f,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (Distance.Local > 0) score /= Distance.Local; // Linear decay with local distance
            score -= Distance.Global - Distance.Local;

            // We only calculate element properties when direction is Parent or Child
            // because when enumerating siblings, a small weighted element will "block" subsequent siblings.
            var weightedElement = Direction switch
            {
                TraverseDirection.Parent => Element,
                TraverseDirection.Child => Previous,
                _ => null
            };
            if (weightedElement is not null)
            {
                // 2. Intrinsic Score (Type Weight)
                score *= GetTypeWeight(weightedElement.Type);

                // 3. Intrinsic Score (Size Weight)
                // Logarithmic scale for area: log(Area + 1)
                // Larger elements are usually more important containers or focal points.
                var rect = weightedElement.BoundingRectangle;
                if (rect is { Width: > 0, Height: > 0 })
                {
                    var area = (float)rect.Width * rect.Height;
                    // Normalize against a reference screen size (e.g., 1920x1080)
                    const float screenArea = 1920f * 1080;
                    var sizeFactor = 1.0f + (area / screenArea);
                    score *= sizeFactor;
                }

                // 4. Penalty for tiny elements (likely noise or invisible)
                if (rect.Width is > 0 and < 5 || rect.Height is > 0 and < 5)
                {
                    score *= 0.1f;
                }
            }

            // PriorityQueue is a min-heap, so we return negative score to make high scores come first.
            return -score;
        }

        private static float GetTypeWeight(VisualElementType type)
        {
            return type switch
            {
                // Semantic text: High value
                VisualElementType.Label or
                    VisualElementType.TextEdit or
                    VisualElementType.Document => 2.0f,

                // Structural containers: High value
                VisualElementType.Panel or
                    VisualElementType.TopLevel or
                    VisualElementType.TabControl => 1.5f,

                // Interactive controls: Medium value
                VisualElementType.Button or
                    VisualElementType.ComboBox or
                    VisualElementType.CheckBox or
                    VisualElementType.RadioButton or
                    VisualElementType.Slider or
                    VisualElementType.MenuItem or
                    VisualElementType.TabItem => 1.0f,

                // Decorative/Less important: Low value
                VisualElementType.Image or
                    VisualElementType.ScrollBar => 0.5f,

                _ => 1.0f
            };
        }
    }

    /// <summary>
    /// Represents a node in the XML tree being built.
    /// This class is mutable to support dynamic updates of activation state during traversal.
    /// </summary>
    private class XmlVisualElement(
        IVisualElement element,
        string? parentId,
        int siblingIndex,
        string? description,
        IReadOnlyList<string> contentLines,
        int selfTokenCount,
        int contentTokenCount,
        bool isSelfInformative,
        bool isCoreElement
    )
    {
        public IVisualElement Element { get; } = element;

        public string? ParentId { get; } = parentId;

        public int SiblingIndex { get; } = siblingIndex;

        public string? Description { get; } = description;

        public IReadOnlyList<string> ContentLines { get; } = contentLines;

        /// <summary>
        /// The token cost of the element's structure (tags, attributes, ID) excluding content text.
        /// </summary>
        public int SelfTokenCount { get; } = selfTokenCount;

        /// <summary>
        /// The token cost of the element's content text (Description, Contents).
        /// </summary>
        public int ContentTokenCount { get; } = contentTokenCount;

        public XmlVisualElement? Parent { get; set; }

        public HashSet<XmlVisualElement> Children { get; } = [];

        /// <summary>
        /// Indicates whether this element should be rendered in the final XML.
        /// This is determined dynamically based on <see cref="VisualTreeDetailLevel"/> and the presence of informative children.
        /// </summary>
        public bool IsVisible { get; set; } = isSelfInformative;

        /// <summary>
        /// Indicates whether this element is intrinsically informative (e.g., has text, is interactive, or is a core element).
        /// If true, <see cref="IsVisible"/> is always true.
        /// </summary>
        public bool IsSelfInformative { get; } = isSelfInformative;

        /// <summary>
        /// Indicates whether this element is a core element.
        /// </summary>
        public bool IsCoreElement { get; } = isCoreElement;

        /// <summary>
        /// The number of children that have informative content (either self-informative or have informative descendants).
        /// </summary>
        public int InformativeChildCount { get; set; }

        /// <summary>
        /// Indicates whether this element has any informative descendants.
        /// </summary>
        public bool HasInformativeDescendants { get; set; }

        // Self-informative elements are always active (rendered).
    }

    /// <summary>
    ///     The mapping from original element ID to the built sequential ID starting from <see cref="startingId"/>.
    /// </summary>
    public Dictionary<int, IVisualElement> BuiltVisualElements { get; } = [];

    private readonly HashSet<string> _coreElementIdSet = coreElements
        .Select(e => e.Id)
        .Where(id => !string.IsNullOrEmpty(id))
        .ToHashSet(StringComparer.Ordinal);

    private StringBuilder? _xmlBuilder;

#if DEBUG_VISUAL_TREE_BUILDER
    private VisualTreeRecorder? _debugRecorder;
#endif

    private const VisualElementStates InteractiveStates = VisualElementStates.Focused | VisualElementStates.Selected;

    public string BuildXml(CancellationToken cancellationToken)
    {
        if (coreElements.Count == 0) throw new InvalidOperationException("No core elements to build XML from.");

        if (_xmlBuilder != null) return _xmlBuilder.ToString();
        cancellationToken.ThrowIfCancellationRequested();

#if DEBUG_VISUAL_TREE_BUILDER
        _debugRecorder = new VisualTreeRecorder(coreElements, approximateTokenLimit, "WeightedPriority");
#endif

        // Priority Queue for Best-First Search
        var priorityQueue = new PriorityQueue<TraversalNode, float>();
        var visitedElements = new Dictionary<string, XmlVisualElement>();

        // 1. Enqueue core nodes
        TryEnqueueTraversalNode(priorityQueue, null, 0, TraverseDirection.Core, coreElements.GetEnumerator());

        // 2. Process the Queue
        ProcessTraversalQueue(priorityQueue, visitedElements, cancellationToken);

        // 3. Dispose remaining enumerators
        while (priorityQueue.Count > 0)
        {
            if (priorityQueue.TryDequeue(out var node, out _))
            {
                node.Enumerator.Dispose();
            }
        }

        // 4. Generate XML
        return GenerateXmlString(visitedElements);
    }

#if DEBUG_VISUAL_TREE_BUILDER
    private void TryEnqueueTraversalNode(
#else
    private static void TryEnqueueTraversalNode(
#endif
        PriorityQueue<TraversalNode, float> priorityQueue,
        in TraversalNode? previous,
        in TraverseDistance distance,
        TraverseDirection direction,
        IEnumerator<IVisualElement> enumerator)
    {
        if (!enumerator.MoveNext())
        {
            enumerator.Dispose();
            return;
        }

        var node = new TraversalNode(enumerator.Current, previous?.Element, distance, direction, direction switch
        {
            TraverseDirection.PreviousSibling => previous?.SiblingIndex - 1 ?? 0,
            TraverseDirection.NextSibling => previous?.SiblingIndex + 1 ?? 0,
            _ => 0
        }, enumerator);
        var score = node.GetScore();
        priorityQueue.Enqueue(node, score);

#if DEBUG_VISUAL_TREE_BUILDER
        _debugRecorder?.RecordStep(
            node.Element,
            "Enqueue",
            score,
            $"Parent: {node.ParentId}, Previous: {node.Previous?.Id}, Direction: {node.Direction}, Distance: {node.Distance}",
            0,
            priorityQueue.Count);
#endif
    }

    private void ProcessTraversalQueue(
        PriorityQueue<TraversalNode, float> priorityQueue,
        Dictionary<string, XmlVisualElement> visitedElements,
        CancellationToken cancellationToken)
    {
        var accumulatedTokenCount = 0;

        while (priorityQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remainingTokenCount = approximateTokenLimit - accumulatedTokenCount;
            if (remainingTokenCount <= 0)
            {
#if DEBUG_VISUAL_TREE_BUILDER
                _debugRecorder?.RecordStep(
                    priorityQueue.Peek().Element,
                    "Stop",
                    0,
                    "Token limit reached",
                    accumulatedTokenCount,
                    priorityQueue.Count);
#endif
                break;
            }

#if DEBUG_VISUAL_TREE_BUILDER
            if (!priorityQueue.TryDequeue(out var node, out var priority)) break;
#else
            if (!priorityQueue.TryDequeue(out var node, out _)) break;
#endif
            var element = node.Element;
            var id = element.Id;

#if DEBUG_VISUAL_TREE_BUILDER
            _debugRecorder?.RegisterNode(element, node.GetScore());
#endif

            if (visitedElements.ContainsKey(id))
            {
#if DEBUG_VISUAL_TREE_BUILDER
                _debugRecorder?.RecordStep(element, "Skip", priority, "Already visited", accumulatedTokenCount, priorityQueue.Count);
#endif
                continue;
            }

            // Process the current node and create the XmlVisualElement
            CreateXmlVisualElement(visitedElements, node, remainingTokenCount, ref accumulatedTokenCount);

#if DEBUG_VISUAL_TREE_BUILDER
            _debugRecorder?.RecordStep(
                element,
                "Visit",
                priority,
                $"Parent: {node.ParentId}, Previous: {node.Previous?.Id}, Direction: {node.Direction}, Distance: {node.Distance}",
                accumulatedTokenCount,
                priorityQueue.Count);
#endif

            // Check limit again after adding this node
            if (accumulatedTokenCount > approximateTokenLimit) break;

            // Add more nodes to the queue based on traversal direction
            PropagateNode(priorityQueue, node);
        }
    }

    private void CreateXmlVisualElement(
        Dictionary<string, XmlVisualElement> visitedElements,
        TraversalNode node,
        int remainingTokenCount,
        ref int accumulatedTokenCount)
    {
        var element = node.Element;
        var id = element.Id;

        // --- Determine Content and Self-Informativeness ---
        string? description = null;
        string? content = null;
        var isTextElement = element.Type is VisualElementType.Label or VisualElementType.TextEdit or VisualElementType.Document;
        var text = element.GetText();
        if (element.Name is { Length: > 0 } name)
        {
            if (isTextElement && string.IsNullOrEmpty(text))
            {
                content = TruncateIfNeeded(name, remainingTokenCount);
            }
            else if (!isTextElement || name != text)
            {
                description = TruncateIfNeeded(name, remainingTokenCount);
            }
        }
        content ??= text is { Length: > 0 } ? TruncateIfNeeded(text, remainingTokenCount) : null;
        var contentLines = content?.Split(Environment.NewLine) ?? [];

        var hasTextContent = contentLines.Length > 0;
        var hasDescription = !string.IsNullOrWhiteSpace(description);
        var interactive = IsInteractiveElement(element);
        var isCoreElement = _coreElementIdSet.Contains(id);
        var isSelfInformative = hasTextContent || hasDescription || interactive || isCoreElement;

        // --- Calculate Token Costs ---
        // Base cost: indentation (approx 2), start tag (<Type), id attribute ( id="..."), end tag (</Type>)
        // We approximate this as 8 tokens.
        var selfTokenCount = ShouldIncludeId(detailLevel, element.Type) ? 8 : 6;

        // Add cost for bounds attributes if applicable (x, y, width, height)
        if (ShouldIncludeBounds(detailLevel, element.Type))
        {
            selfTokenCount += 20; // Approximate cost for pos="..." size="..."
        }

        var contentTokenCount = 0;
        if (description != null) contentTokenCount += EstimateTokenCount(description) + 3; // +3 for description="..."
        contentTokenCount += contentLines.Length switch
        {
            > 0 and < 3 => contentLines.Sum(EstimateTokenCount),
            >= 3 => contentLines.Sum(line => EstimateTokenCount(line) + 4) + 8, // >= 3, +4 for the indentation, +8 for the end tag
            _ => 0
        };

        // Create the XML Element node
        var xmlElement = visitedElements[id] = new XmlVisualElement(
            element,
            node.ParentId,
            node.SiblingIndex,
            description,
            contentLines,
            selfTokenCount,
            contentTokenCount,
            isSelfInformative,
            node.Direction == TraverseDirection.Core);

        // --- Update Token Count and Propagate ---

        // If the element is self-informative, it is active immediately.
        if (xmlElement.IsVisible)
        {
            accumulatedTokenCount += xmlElement.SelfTokenCount + xmlElement.ContentTokenCount;
        }

        // Link to parent and propagate updates
        if (node.ParentId != null && visitedElements.TryGetValue(node.ParentId, out var parentXmlElement))
        {
            parentXmlElement.Children.Add(xmlElement);
            xmlElement.Parent = parentXmlElement;

            // If the new child is informative (self-informative or has informative descendants),
            // we need to notify the parent.
            // Note: A newly created node has no descendants yet, so HasInformativeDescendants is false.
            // So we only check IsSelfInformative.
            if (xmlElement.IsSelfInformative)
            {
                PropagateInformativeUpdate(parentXmlElement, ref accumulatedTokenCount);
            }
        }
        // If we traversed from parent direction, above method cannot link parent-child.
        else if (node is { Direction: TraverseDirection.Parent })
        {
            foreach (var childXmlElement in visitedElements.Values
                         .AsValueEnumerable()
                         .Where(e => e.Parent is null)
                         .Where(e => string.Equals(e.ParentId, id, StringComparison.Ordinal)))
            {
                xmlElement.Children.Add(childXmlElement);
                childXmlElement.Parent = xmlElement;

                if (xmlElement.IsSelfInformative)
                {
                    PropagateInformativeUpdate(childXmlElement, ref accumulatedTokenCount);
                }
            }
        }
    }

#if DEBUG_VISUAL_TREE_BUILDER
    private void PropagateNode(
#else
    private static void PropagateNode(
#endif
        PriorityQueue<TraversalNode, float> priorityQueue,
        in TraversalNode node)
    {
#if DEBUG_VISUAL_TREE_BUILDER
        Debug.WriteLine($"[PropagateNode] {node}");
#endif

        var elementType = node.Element.Type;
        switch (node.Direction)
        {
            case TraverseDirection.Core:
            {
                // In this case, node.Enumerator is the core element enumerator
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    0,
                    TraverseDirection.Core,
                    node.Enumerator);

                // Only enqueue parent and siblings if not top-level
                if (elementType != VisualElementType.TopLevel)
                {
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        1,
                        TraverseDirection.Parent,
                        node.Element.GetAncestors().GetEnumerator());

                    var siblingAccessor = node.Element.SiblingAccessor;

                    // Get two enumerators together, prohibited to dispose one before the other, causing resource reallocation.
                    var previousSiblingEnumerator = siblingAccessor.BackwardEnumerator;
                    var nextSiblingEnumerator = siblingAccessor.ForwardEnumerator;

                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        1,
                        TraverseDirection.PreviousSibling,
                        previousSiblingEnumerator);
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        1,
                        TraverseDirection.NextSibling,
                        nextSiblingEnumerator);
                }

                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    1,
                    TraverseDirection.Child,
                    node.Element.Children.GetEnumerator());
                break;
            }
            case TraverseDirection.Parent when elementType != VisualElementType.TopLevel:
            {
                // In this case, node.Enumerator is the Ancestors enumerator
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Step(),
                    TraverseDirection.Parent,
                    node.Enumerator);

                var siblingAccessor = node.Element.SiblingAccessor;

                // Get two enumerators together, prohibited to dispose one before the other, causing resource reallocation.
                var previousSiblingEnumerator = siblingAccessor.BackwardEnumerator;
                var nextSiblingEnumerator = siblingAccessor.ForwardEnumerator;

                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Reset(),
                    TraverseDirection.PreviousSibling,
                    previousSiblingEnumerator);
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Reset(),
                    TraverseDirection.NextSibling,
                    nextSiblingEnumerator);
                break;
            }
            case TraverseDirection.PreviousSibling:
            {
                // In this case, node.Enumerator is the Previous Sibling enumerator
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Step(),
                    TraverseDirection.PreviousSibling,
                    node.Enumerator);

                // Also enqueue the children of this sibling
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Reset(),
                    TraverseDirection.Child,
                    node.Element.Children.GetEnumerator());
                break;
            }
            case TraverseDirection.NextSibling:
            {
                // In this case, node.Enumerator is the Next Sibling enumerator
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Step(),
                    TraverseDirection.NextSibling,
                    node.Enumerator);

                // Also enqueue the children of this sibling
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Reset(),
                    TraverseDirection.Child,
                    node.Element.Children.GetEnumerator());
                break;
            }
            case TraverseDirection.Child:
            {
                // In this case, node.Enumerator is the Children enumerator
                // But note that these children are actually descendants of the original node's sibling.
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Step(),
                    TraverseDirection.NextSibling,
                    node.Enumerator);

                // Also enqueue the children of this child
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    node.Distance.Reset(),
                    TraverseDirection.Child,
                    node.Element.Children.GetEnumerator());
                break;
            }
        }
    }

    private string GenerateXmlString(Dictionary<string, XmlVisualElement> visualElements)
    {
        _xmlBuilder = new StringBuilder();
        foreach (var rootElement in visualElements.Values.AsValueEnumerable().Where(e => e.Parent is null))
        {
            InternalBuildXml(rootElement, 0);
        }

#if DEBUG_VISUAL_TREE_BUILDER
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var filename = $"visual_tree_debug_{timestamp}.json";
        var debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
        _debugRecorder?.SaveSession(debugPath);
#endif

        return _xmlBuilder.TrimEnd().ToString();

        void InternalBuildXml(XmlVisualElement xmlElement, int indentLevel)
        {
            // If not active, we don't render this element's tags, but we might render its children.
            // This acts as a "passthrough" for structural containers that are not interesting enough to show.
            if (!xmlElement.IsVisible)
            {
                foreach (var child in xmlElement.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex)) InternalBuildXml(child, indentLevel);
                return;
            }

            var indent = new string(' ', indentLevel * 2);
            var element = xmlElement.Element;
            var elementType = element.Type;

            // Start tag
            _xmlBuilder.Append(indent).Append('<').Append(elementType);

            // Add ID if needed
            var id = BuiltVisualElements.Count + startingId;
            BuiltVisualElements[id] = element;
            if (ShouldIncludeId(detailLevel, elementType))
            {
                _xmlBuilder.Append(" id=\"").Append(id).Append('"');
            }

            // Add coreElement attribute if applicable
            if (xmlElement.IsCoreElement)
            {
                _xmlBuilder.Append(" coreElement=\"true\"");
            }

            // Add bounds if needed
            if (ShouldIncludeBounds(detailLevel, elementType))
            {
                // for containers, include the element's size
                var bounds = element.BoundingRectangle;
                _xmlBuilder
                    .Append(" pos=\"")
                    .Append(bounds.X).Append(',').Append(bounds.Y)
                    .Append('"')
                    .Append(" size=\"")
                    .Append(bounds.Width).Append('x').Append(bounds.Height)
                    .Append('"');
            }

            // For top-level elements, add pid, process name and WindowHandle attributes
            if (xmlElement.Element.Type == VisualElementType.TopLevel)
            {
                var processId = xmlElement.Element.ProcessId;
                if (processId > 0)
                {
                    _xmlBuilder.Append(" pid=\"").Append(processId).Append('"');
                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        _xmlBuilder.Append(" processName=\"").Append(SecurityElement.Escape(process.ProcessName)).Append('"');
                    }
                    catch
                    {
                        // Ignore if process not found
                    }
                }

                var windowHandle = xmlElement.Element.NativeWindowHandle;
                if (windowHandle > 0)
                {
                    _xmlBuilder.Append(" windowHandle=\"0x").Append(windowHandle.ToString("X")).Append('"');
                }
            }

            if (xmlElement.Description != null)
            {
                _xmlBuilder.Append(" description=\"").Append(SecurityElement.Escape(xmlElement.Description)).Append('"');
            }

            // Add content attribute if there's a 1 or 2 line content
            if (xmlElement.ContentLines.Count is > 0 and < 3)
            {
                _xmlBuilder.Append(" content=\"").Append(SecurityElement.Escape(string.Join('\n', xmlElement.ContentLines))).Append('"');
            }

            if (xmlElement.Children.Count == 0 && xmlElement.ContentLines.Count < 3)
            {
                // Self-closing tag if no children and no content
                _xmlBuilder.Append("/>").AppendLine();
                return;
            }

            _xmlBuilder.Append('>').AppendLine();
            var xmlLengthBeforeContent = _xmlBuilder.Length;

            // Add contents if there are 3 or more lines
            if (xmlElement.ContentLines.Count >= 3)
            {
                foreach (var contentLine in xmlElement.ContentLines.AsValueEnumerable())
                {
                    if (string.IsNullOrWhiteSpace(contentLine))
                    {
                        _xmlBuilder.AppendLine(); // don't write indentation for empty lines
                        continue;
                    }

                    _xmlBuilder
                        .Append(indent)
                        .Append("  ")
                        .Append(SecurityElement.Escape(contentLine))
                        .AppendLine();
                }
            }

            // Handle child elements
            foreach (var child in xmlElement.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex)) InternalBuildXml(child, indentLevel + 1);
            if (xmlLengthBeforeContent == _xmlBuilder.Length)
            {
                // No content or children were added, so we can convert to self-closing tag
                _xmlBuilder.Length -= Environment.NewLine.Length + 1; // Remove the newline and '>'
                _xmlBuilder.Append("/>").AppendLine();
                return;
            }

            // End tag
            _xmlBuilder.Append(indent).Append("</").Append(element.Type).Append('>').AppendLine();
        }
    }

    private static bool ShouldIncludeId(VisualTreeDetailLevel detailLevel, VisualElementType type) => detailLevel switch
    {
        VisualTreeDetailLevel.Detailed => true,
        VisualTreeDetailLevel.Compact => type is not VisualElementType.Label,
        VisualTreeDetailLevel.Minimal when type is
            VisualElementType.TextEdit or
            VisualElementType.Button or
            VisualElementType.CheckBox or
            VisualElementType.ListView or
            VisualElementType.TreeView or
            VisualElementType.DataGrid or
            VisualElementType.TabControl or
            VisualElementType.Table or
            VisualElementType.Document or
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        _ => false
    };

    private static bool ShouldIncludeBounds(VisualTreeDetailLevel detailLevel, VisualElementType type) => detailLevel switch
    {
        VisualTreeDetailLevel.Detailed => true,
        VisualTreeDetailLevel.Compact when type is
            VisualElementType.TextEdit or
            VisualElementType.Button or
            VisualElementType.CheckBox or
            VisualElementType.ListView or
            VisualElementType.TreeView or
            VisualElementType.DataGrid or
            VisualElementType.TabControl or
            VisualElementType.Table or
            VisualElementType.Document or
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        VisualTreeDetailLevel.Minimal when type is
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        _ => false
    };

    private static string TruncateIfNeeded(string text, int maxLength)
    {
        var tokenCount = EstimateTokenCount(text);
        if (maxLength <= 0 || tokenCount <= maxLength)
            return text;

        var approximateLength = text.Length * maxLength / tokenCount;
        return text[..Math.Max(0, approximateLength - 2)] + "...omitted";
    }

    /// <summary>
    /// Propagates the information that a child is informative up the tree.
    /// This may cause ancestors to become active (rendered) if they meet the criteria for the current <see cref="detailLevel"/>.
    /// </summary>
    private void PropagateInformativeUpdate(XmlVisualElement? parent, ref int accumulatedTokenCount)
    {
        while (parent != null)
        {
            parent.InformativeChildCount++;

            var wasActive = parent.IsVisible;
            var wasHasInfo = parent.HasInformativeDescendants;

            parent.HasInformativeDescendants = true;

            // Check if activation state changes based on the new child count
            UpdateActivationState(parent);

            if (!wasActive && parent.IsVisible)
            {
                // Parent just became active, so we must pay for its structure tokens.
                accumulatedTokenCount += parent.SelfTokenCount;
                // Note: ContentTokenCount is 0 for non-self-informative elements, so we don't add it.
            }

            // If the parent already had informative descendants, we don't need to propagate the "existence" of info further up.
            // The ancestors already know this branch is informative.
            // However, we DO need to continue if the parent's activation state changed, because that might affect token count?
            // No, token count is updated locally.
            // Does parent activation affect grandparent activation?
            // Grandparent activation depends on grandparent.InformativeChildCount.
            // Grandparent.InformativeChildCount counts children that are "informative" (HasInformativeContent).
            // HasInformativeContent = IsSelfInformative || HasInformativeDescendants.
            // Since parent.HasInformativeDescendants was already true (if wasHasInfo is true), 
            // parent was already contributing to grandparent's InformativeChildCount.
            // So grandparent's count doesn't change.

            if (wasHasInfo) break;

            parent = parent.Parent;
        }
    }

    /// <summary>
    /// Updates the <see cref="XmlVisualElement.IsVisible"/> state of an element based on the current <see cref="detailLevel"/>
    /// and its informative status.
    /// </summary>
    private void UpdateActivationState(XmlVisualElement element)
    {
        // If it's self-informative, it's always active.
        if (element.IsSelfInformative)
        {
            element.IsVisible = true;
            return;
        }

        // Otherwise, it depends on the detail level and children.
        var shouldRender = detailLevel switch
        {
            VisualTreeDetailLevel.Compact => ShouldKeepContainerForCompact(element),
            VisualTreeDetailLevel.Minimal => ShouldKeepContainerForMinimal(element),
            // For Detailed, we render if there are any informative descendants.
            _ => element.HasInformativeDescendants
        };

        element.IsVisible = shouldRender;
    }

    private static bool ShouldKeepContainerForCompact(XmlVisualElement element)
    {
        if (element.Parent is null) return element.InformativeChildCount > 0;

        var type = element.Element.Type;
        return type switch
        {
            VisualElementType.Screen or VisualElementType.TopLevel => element.InformativeChildCount > 1,
            VisualElementType.Document => element.InformativeChildCount > 0,
            VisualElementType.Panel => element.InformativeChildCount > 1,
            _ => false
        };
    }

    private static bool ShouldKeepContainerForMinimal(XmlVisualElement element)
    {
        if (element.Parent is null)
        {
            return element.InformativeChildCount > 0;
        }

        return false;
    }

    private static bool IsInteractiveElement(IVisualElement element)
    {
        if (element.Type is VisualElementType.Button or
            VisualElementType.Hyperlink or
            VisualElementType.CheckBox or
            VisualElementType.RadioButton or
            VisualElementType.ComboBox or
            VisualElementType.ListView or
            VisualElementType.ListViewItem or
            VisualElementType.TreeView or
            VisualElementType.TreeViewItem or
            VisualElementType.DataGrid or
            VisualElementType.DataGridItem or
            VisualElementType.TabControl or
            VisualElementType.TabItem or
            VisualElementType.Menu or
            VisualElementType.MenuItem or
            VisualElementType.Slider or
            VisualElementType.ScrollBar or
            VisualElementType.ProgressBar or
            VisualElementType.TextEdit or
            VisualElementType.Table or
            VisualElementType.TableRow) return true;

        return (element.States & InteractiveStates) != 0;
    }

    // The token-to-word ratio for English/Latin-based text.
    private const double EnglishTokenRatio = 3.0;

    // The token-to-character ratio for CJK-based text.
    private const double CjkTokenRatio = 2.0;

    /// <summary>
    ///     Approximates the number of LLM tokens for a given string.
    ///     This method first detects the language family of the string and then applies the corresponding heuristic.
    /// </summary>
    /// <param name="text">The input string to calculate the token count for.</param>
    /// <returns>An approximate number of tokens.</returns>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return IsCjkLanguage(text) ? (int)Math.Ceiling(text.Length * CjkTokenRatio) : (int)Math.Ceiling(CountWords(text) * EnglishTokenRatio);
    }

    /// <summary>
    ///     Detects if a string is predominantly composed of CJK characters.
    ///     This method makes a judgment by calculating the proportion of CJK characters.
    /// </summary>
    /// <param name="text">The string to be checked.</param>
    /// <returns>True if the string is mainly CJK, false otherwise.</returns>
    private static bool IsCjkLanguage(string text)
    {
        var cjkCount = 0;
        var totalChars = 0;

        foreach (var c in text.AsValueEnumerable().Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)))
        {
            totalChars++;
            // Use regex to match CJK characters
            if (CjkRegex().IsMatch(c.ToString()))
            {
                cjkCount++;
            }
        }

        // Set a threshold: if the proportion of CJK characters exceeds 10%, it is considered a CJK language.
        return totalChars > 0 && (double)cjkCount / totalChars > 0.1;
    }

    /// <summary>
    ///     Counts the number of words in a string using a regular expression.
    ///     This method matches sequences of non-whitespace characters to provide a more accurate word count than simple splitting.
    /// </summary>
    /// <param name="s">The string in which to count words.</param>
    /// <returns>The number of words.</returns>
    private static int CountWords(string s)
    {
        // Matches one or more non-whitespace characters, considered as a single word.
        var collection = WordCountRegex().Matches(s);
        return collection.Count;
    }

    /// <summary>
    ///     Regex to match CJK characters, including Chinese, Japanese, and Korean.
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}|\p{IsCJKCompatibility}|\p{IsHangulJamo}|\p{IsHangulSyllables}|\p{IsHangulCompatibilityJamo}")]
    private static partial Regex CjkRegex();

    /// <summary>
    ///     Regex to match words (sequences of non-whitespace characters).
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"\S+")]
    private static partial Regex WordCountRegex();
}