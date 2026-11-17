using FabLog.FabPad;

namespace FabLog.DemoTests;

public class ModelVariantSelectionTests
{
    [Fact]
    public void Missing_cuda_provider_cannot_select_a_cuda_variant()
    {
        ModelVariantInfo[] variants =
        [
            new("qwen-cuda-gpu:4", "CUDAExecutionProvider"),
            new("qwen-openvino-gpu:4", "OpenVINOExecutionProvider"),
            new("qwen-generic-cpu:4", "CPUExecutionProvider"),
        ];

        var result = ModelVariantSelection.SelectCandidates(variants, ["OpenVINOExecutionProvider"]);

        Assert.Equal(new[] { "qwen-openvino-gpu:4", "qwen-generic-cpu:4" }, result.Select(v => v.Id));
    }

    [Fact]
    public void Cpu_remains_available_without_downloadable_providers()
    {
        ModelVariantInfo[] variants =
        [
            new("qwen-cuda-gpu:4", "CUDAExecutionProvider"),
            new("qwen-generic-cpu:4", "CPUExecutionProvider"),
        ];

        var result = ModelVariantSelection.SelectCandidates(variants, []);

        Assert.Equal("qwen-generic-cpu:4", Assert.Single(result).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Compatible_variants_keep_foundry_order_without_an_override(string? forcedVariant)
    {
        ModelVariantInfo[] variants =
        [
            new("qwen-openvino-gpu:4", "OpenVINOExecutionProvider"),
            new("qwen-generic-cpu:4", "CPUExecutionProvider"),
            new("qwen-cuda-gpu:4", "CUDAExecutionProvider"),
        ];

        var result = ModelVariantSelection.SelectCandidates(variants,
            ["CUDAExecutionProvider", "OpenVINOExecutionProvider"], forcedVariant);

        Assert.Equal(variants, result);
    }

    [Fact]
    public void Unknown_provider_metadata_is_not_assumed_compatible()
    {
        ModelVariantInfo[] variants =
        [
            new("unknown", null),
            new("empty", ""),
            new("unregistered", "CUDAExecutionProvider"),
        ];

        Assert.Empty(ModelVariantSelection.SelectCandidates(variants, []));
    }

    [Fact]
    public void Provider_names_are_case_insensitive()
    {
        ModelVariantInfo[] variants =
        [
            new("new-accelerator:2", "NewExecutionProvider"),
            new("new-accelerator:1", "NewExecutionProvider"),
        ];

        var result = ModelVariantSelection.SelectCandidates(variants, ["newexecutionprovider"]);

        Assert.Equal(variants, result);
    }

    [Fact]
    public void Forced_variant_overrides_foundry_order_without_other_fallback_candidates()
    {
        ModelVariantInfo[] variants =
        [
            new("qwen-openvino-gpu:4", "OpenVINOExecutionProvider"),
            new("qwen-cuda-gpu:4", "CUDAExecutionProvider"),
            new("qwen-cuda-gpu:3", "CUDAExecutionProvider"),
            new("qwen-generic-cpu:4", "CPUExecutionProvider"),
        ];

        var result = ModelVariantSelection.SelectCandidates(variants,
            ["CUDAExecutionProvider", "OpenVINOExecutionProvider"], " CUDA-GPU ");

        Assert.Equal("qwen-cuda-gpu:4", Assert.Single(result).Id);
    }

    [Fact]
    public void Forced_variant_with_missing_provider_fails_instead_of_falling_back_to_cpu()
    {
        ModelVariantInfo[] variants =
        [
            new("qwen-cuda-gpu:4", "CUDAExecutionProvider"),
            new("qwen-generic-cpu:4", "CPUExecutionProvider"),
        ];

        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelVariantSelection.SelectCandidates(variants, [], "cuda-gpu"));

        Assert.Contains("CUDAExecutionProvider", error.Message);
        Assert.Contains("No fallback", error.Message);
    }

    [Fact]
    public void Missing_forced_variant_fails_instead_of_selecting_another_variant()
    {
        ModelVariantInfo[] variants = [new("qwen-generic-cpu:4", "CPUExecutionProvider")];

        var error = Assert.Throws<InvalidOperationException>(() =>
            ModelVariantSelection.SelectCandidates(variants, [], "cuda-gpu"));

        Assert.Contains("No model variant matches", error.Message);
    }
}
