namespace EyeCtrl.LM.Tests;

using EyeCtrl.LM;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// Shapes and names here mirror the real shipped exports, verified by loading them:
/// Qwen2.5-1.5B q4f16 declares Float KV of [batch,2,past_sequence_length,128] over 28 layers,
/// and SmolLM2-135M fp16 declares Float16 KV (its logits stay Float). Both demand
/// attention_mask and position_ids alongside input_ids.
/// </summary>
public class DecoderIoPlanTests
{
    private static GraphTensor Ids(string name) => new(name, TensorElementType.Int64, [-1, -1]);
    private static GraphTensor Kv(string name, TensorElementType type = TensorElementType.Float)
        => new(name, type, [-1, 2, -1, 128]);
    private static GraphTensor Logits() => new("logits", TensorElementType.Float, [-1, -1, 151936]);

    private static List<GraphTensor> DecoderInputs(int layers, TensorElementType kvType = TensorElementType.Float)
    {
        var inputs = new List<GraphTensor> { Ids("input_ids"), Ids("attention_mask"), Ids("position_ids") };
        for (int i = 0; i < layers; i++)
        {
            inputs.Add(Kv($"past_key_values.{i}.key", kvType));
            inputs.Add(Kv($"past_key_values.{i}.value", kvType));
        }
        return inputs;
    }

    private static List<GraphTensor> DecoderOutputs(int layers, TensorElementType kvType = TensorElementType.Float)
    {
        var outputs = new List<GraphTensor> { Logits() };
        for (int i = 0; i < layers; i++)
        {
            outputs.Add(Kv($"present.{i}.key", kvType));
            outputs.Add(Kv($"present.{i}.value", kvType));
        }
        return outputs;
    }

    [Fact]
    public void Build_DecoderGraph_PairsEachPastInputWithItsPresentOutput()
    {
        var plan = DecoderIoPlan.Build(DecoderInputs(2), DecoderOutputs(2));

        Check.That(plan.KvSlots.Select(s => s.PastName)).ContainsExactly(
            "past_key_values.0.key", "past_key_values.0.value",
            "past_key_values.1.key", "past_key_values.1.value");
        Check.That(plan.KvSlots.Select(s => s.PresentName)).ContainsExactly(
            "present.0.key", "present.0.value",
            "present.1.key", "present.1.value");
    }

    [Fact]
    public void Build_DecoderGraph_RequiresAttentionMaskAndPositionIds()
    {
        var plan = DecoderIoPlan.Build(DecoderInputs(1), DecoderOutputs(1));

        Check.That(plan.NeedsAttentionMask).IsTrue();
        Check.That(plan.NeedsPositionIds).IsTrue();
    }

    [Fact]
    public void Build_EmptyPastShape_KeepsStaticDimsAndZerosTheSequenceAxis()
    {
        var plan = DecoderIoPlan.Build(DecoderInputs(1), DecoderOutputs(1));

        // [-1,2,-1,128] -> [1,2,0,128]: batch 1, no past yet, static head/dim untouched.
        Check.That(plan.KvSlots[0].EmptyShape).ContainsExactly(1, 2, 0, 128);
    }

    [Fact]
    public void Build_Fp16Export_CarriesFloat16KvElementType()
    {
        var plan = DecoderIoPlan.Build(
            DecoderInputs(1, TensorElementType.Float16),
            DecoderOutputs(1, TensorElementType.Float16));

        Check.That(plan.KvSlots[0].ElementType).IsEqualTo(TensorElementType.Float16);
    }

    [Fact]
    public void Build_GraphWithOnlyInputIds_NeedsNothingElse()
    {
        var plan = DecoderIoPlan.Build([Ids("input_ids")], [Logits()]);

        Check.That(plan.NeedsAttentionMask).IsFalse();
        Check.That(plan.NeedsPositionIds).IsFalse();
        Check.That(plan.KvSlots).IsEmpty();
    }

    [Fact]
    public void Build_GraphDeclaringAnInputWeCannotFill_ThrowsNamingIt()
    {
        // The original defect in miniature: an input the caller never supplies must fail
        // loudly at plan time, not as an opaque ORT error swallowed at run time.
        NotSupportedException? caught = null;
        try { DecoderIoPlan.Build([Ids("input_ids"), Ids("token_type_ids")], [Logits()]); }
        catch (NotSupportedException ex) { caught = ex; }

        Check.That(caught).IsNotNull();
        Check.That(caught!.Message).Contains("token_type_ids");
    }

    [Fact]
    public void Build_PastInputWithoutMatchingPresentOutput_Throws()
    {
        Check.ThatCode(() => DecoderIoPlan.Build(DecoderInputs(1), [Logits()]))
            .Throws<NotSupportedException>();
    }

    [Fact]
    public void Build_GraphWithoutLogitsOutput_Throws()
    {
        Check.ThatCode(() => DecoderIoPlan.Build([Ids("input_ids")], [Kv("present.0.key")]))
            .Throws<NotSupportedException>();
    }

    [Fact]
    public void Build_GraphWithoutInputIds_Throws()
    {
        Check.ThatCode(() => DecoderIoPlan.Build([Ids("attention_mask")], [Logits()]))
            .Throws<NotSupportedException>();
    }

    [Fact]
    public void Build_LogitsThatAreNotFloat_Throws()
    {
        // Both shipped families emit Float logits - the fp16 exports convert only their KV cache.
        // Anything else must fail at load rather than as an opaque cast at predict time.
        Check.ThatCode(() => DecoderIoPlan.Build(
                [Ids("input_ids")],
                [new GraphTensor("logits", TensorElementType.Float16, [-1, -1, 49152])]))
            .Throws<NotSupportedException>();
    }

    [Fact]
    public void Build_KvCacheInAnUnsupportedElementType_Throws()
    {
        Check.ThatCode(() => DecoderIoPlan.Build(
                DecoderInputs(1, TensorElementType.BFloat16),
                DecoderOutputs(1, TensorElementType.BFloat16)))
            .Throws<NotSupportedException>();
    }

    [Fact]
    public void PositionIds_Prefill_StartAtZero()
    {
        Check.That(DecoderIoPlan.PositionIds(pastLength: 0, stepLength: 4)).ContainsExactly(0L, 1L, 2L, 3L);
    }

    [Fact]
    public void PositionIds_AfterCachedTokens_ContinueFromPastLength()
    {
        Check.That(DecoderIoPlan.PositionIds(pastLength: 4, stepLength: 1)).ContainsExactly(4L);
    }

    [Fact]
    public void AttentionMask_CoversCachedTokensPlusTheCurrentStep()
    {
        Check.That(DecoderIoPlan.AttentionMask(pastLength: 4, stepLength: 1)).ContainsExactly(1L, 1L, 1L, 1L, 1L);
    }

    [Fact]
    public void Build_LogitsOfAnUnexpectedRank_Throws()
    {
        // RunInference reads sequence and vocabulary from Dimensions[1] and [2]. A rank-2
        // logits output would index past the end mid-generation - straight into the broad
        // catch that made the original defect invisible - so it is refused at load instead.
        Check.ThatCode(() => DecoderIoPlan.Build(
                [Ids("input_ids")],
                [new GraphTensor("logits", TensorElementType.Float, [-1, 151936])]))
            .Throws<NotSupportedException>();
    }

    [Fact]
    public void Build_KnownInputInAnUnexpectedElementType_ThrowsNamingIt()
    {
        // The three known inputs are fed as Int64 tensors; matching them by name alone would
        // let a differently-typed export fail at Run instead of at load.
        NotSupportedException? caught = null;
        try
        {
            DecoderIoPlan.Build(
                [Ids("input_ids"), new GraphTensor("attention_mask", TensorElementType.Float, [-1, -1])],
                [Logits()]);
        }
        catch (NotSupportedException ex) { caught = ex; }

        Check.That(caught).IsNotNull();
        Check.That(caught!.Message).Contains("attention_mask");
    }
}
