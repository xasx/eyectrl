namespace EyeCtrl.LM;

using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>One graph input or output, reduced to what feeding it requires.</summary>
internal readonly record struct GraphTensor(string Name, TensorElementType ElementType, int[] Dimensions);

/// <summary>One key-or-value cache slot, linking the input that receives it to the output that produces it.</summary>
internal sealed record KvSlot(string PastName, string PresentName, TensorElementType ElementType, int[] EmptyShape);

/// <summary>
/// What one decoder step must feed a model, derived from the model's own graph signature
/// rather than assumed. The shipped exports are self-describing on this point: they declare
/// <c>attention_mask</c>, <c>position_ids</c> and a <c>past_key_values.N.{key,value}</c> input
/// per layer alongside <c>input_ids</c>, and ONNX Runtime rejects a Run that omits any of them.
/// Anything the plan cannot fill is refused at build time, by name — a graph input we silently
/// fail to supply is exactly the defect this type exists to prevent.
/// </summary>
internal sealed class DecoderIoPlan
{
    internal const string InputIdsInput = "input_ids";
    internal const string AttentionMaskInput = "attention_mask";
    internal const string PositionIdsInput = "position_ids";
    internal const string LogitsOutput = "logits";
    private const string PastPrefix = "past_key_values.";
    private const string PresentPrefix = "present.";

    private DecoderIoPlan(bool needsAttentionMask, bool needsPositionIds, IReadOnlyList<KvSlot> kvSlots)
    {
        NeedsAttentionMask = needsAttentionMask;
        NeedsPositionIds = needsPositionIds;
        KvSlots = kvSlots;
    }

    /// <summary>Whether the graph declares an <c>attention_mask</c> input.</summary>
    public bool NeedsAttentionMask { get; }
    /// <summary>Whether the graph declares a <c>position_ids</c> input.</summary>
    public bool NeedsPositionIds { get; }
    /// <summary>Every key/value cache slot, in graph-input order.</summary>
    public IReadOnlyList<KvSlot> KvSlots { get; }

    /// <summary>Derives the feeding plan from a model's declared inputs and outputs.</summary>
    /// <exception cref="NotSupportedException">
    /// The graph declares an input this plan cannot supply, a past input with no matching
    /// present output, or is missing <c>input_ids</c> or <c>logits</c>.
    /// </exception>
    public static DecoderIoPlan Build(IReadOnlyList<GraphTensor> inputs, IReadOnlyList<GraphTensor> outputs)
    {
        var outputNames = outputs.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (!outputNames.Contains(LogitsOutput))
            throw new NotSupportedException($"Model declares no '{LogitsOutput}' output; cannot read next-token scores.");

        // Verified on the shipped exports: both the fp16 and the q4f16 families emit Float logits;
        // only their KV cache narrows to Float16.
        var logits = outputs.First(o => o.Name == LogitsOutput);
        if (logits.ElementType != TensorElementType.Float)
            throw new NotSupportedException(
                $"Model emits {logits.ElementType} '{LogitsOutput}'; only Float is supported.");

        var hasInputIds = false;
        var needsAttentionMask = false;
        var needsPositionIds = false;
        var kvSlots = new List<KvSlot>();

        foreach (var input in inputs)
        {
            switch (input.Name)
            {
                case InputIdsInput: hasInputIds = true; continue;
                case AttentionMaskInput: needsAttentionMask = true; continue;
                case PositionIdsInput: needsPositionIds = true; continue;
            }

            if (!input.Name.StartsWith(PastPrefix, StringComparison.Ordinal))
                throw new NotSupportedException(
                    $"Model requires input '{input.Name}', which this predictor cannot supply.");

            if (input.ElementType is not (TensorElementType.Float or TensorElementType.Float16))
                throw new NotSupportedException(
                    $"Model input '{input.Name}' is {input.ElementType}; only Float and Float16 caches are supported.");

            var presentName = PresentPrefix + input.Name[PastPrefix.Length..];
            if (!outputNames.Contains(presentName))
                throw new NotSupportedException(
                    $"Model input '{input.Name}' has no matching '{presentName}' output; cannot carry the cache forward.");

            kvSlots.Add(new KvSlot(input.Name, presentName, input.ElementType, EmptyShape(input.Dimensions)));
        }

        if (!hasInputIds)
            throw new NotSupportedException($"Model declares no '{InputIdsInput}' input.");

        return new DecoderIoPlan(needsAttentionMask, needsPositionIds, kvSlots);
    }

    /// <summary>Position of each token in this step, continuing after the cached ones.</summary>
    public static long[] PositionIds(int pastLength, int stepLength)
    {
        var ids = new long[stepLength];
        for (int i = 0; i < stepLength; i++) ids[i] = pastLength + i;
        return ids;
    }

    /// <summary>Attention over every cached token plus this step's — nothing is padded, so all ones.</summary>
    public static long[] AttentionMask(int pastLength, int stepLength)
    {
        var mask = new long[pastLength + stepLength];
        Array.Fill(mask, 1L);
        return mask;
    }

    // Symbolic dimensions arrive as -1. Axis 0 is the batch, and we run exactly one sequence;
    // every other dynamic axis is the past-sequence length, which is empty before the first step.
    // Static dimensions (KV head count, head size) are the model's own and pass through untouched.
    private static int[] EmptyShape(int[] dimensions)
    {
        var shape = new int[dimensions.Length];
        for (int i = 0; i < dimensions.Length; i++)
            shape[i] = dimensions[i] >= 0 ? dimensions[i] : (i == 0 ? 1 : 0);
        return shape;
    }
}
