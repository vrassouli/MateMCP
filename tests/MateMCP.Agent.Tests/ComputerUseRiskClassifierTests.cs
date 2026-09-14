using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class ComputerUseRiskClassifierTests
{
    [Theory]
    [InlineData("Delete Device")]
    [InlineData("Remove account")]
    [InlineData("حذف دستگاه")]
    [InlineData("لغو دسترسی")]
    public void Destructive_semantic_targets_are_high_risk(string name)
    {
        var result = ComputerUseRiskClassifier.AssessSemantic("invoke", "button", name);
        Assert.Equal(ComputerUseRiskLevel.High, result.Level);
    }

    [Theory]
    [InlineData("Pay now")]
    [InlineData("Confirm payment")]
    [InlineData("پرداخت")]
    [InlineData("انتقال وجه")]
    public void Financial_semantic_targets_are_high_risk(string name)
    {
        var result = ComputerUseRiskClassifier.AssessSemantic("invoke", "button", name);
        Assert.Equal(ComputerUseRiskLevel.High, result.Level);
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("Submit")]
    [InlineData("ارسال")]
    [InlineData("ذخیره")]
    public void Persistent_mutations_are_sensitive(string name)
    {
        var result = ComputerUseRiskClassifier.AssessSemantic("invoke", "button", name);
        Assert.Equal(ComputerUseRiskLevel.Sensitive, result.Level);
    }

    [Fact]
    public void Credential_field_fill_is_high_risk_without_exposing_literal_value()
    {
        var result = ComputerUseRiskClassifier.AssessSemantic("fill", "textbox", "Credential", "credential-input");
        Assert.Equal(ComputerUseRiskLevel.High, result.Level);
        Assert.Contains("credential", result.Effect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Display_is_not_misclassified_as_payment_due_to_substring()
    {
        var result = ComputerUseRiskClassifier.AssessSemantic("invoke", "button", "Display settings", "display-button");
        Assert.Equal(ComputerUseRiskLevel.Low, result.Level);
    }

    [Fact]
    public void Raw_coordinate_or_keyboard_input_is_conservatively_sensitive()
    {
        var result = ComputerUseRiskClassifier.AssessRaw("click");
        Assert.Equal(ComputerUseRiskLevel.Sensitive, result.Level);
        Assert.Contains("cannot be determined", result.Effect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_scope_contains_risk_action_and_semantic_target()
    {
        var assessment = ComputerUseRiskClassifier.AssessSemantic("invoke", "button", "Delete Device");
        var target = ComputerUseRiskClassifier.PolicyTarget("semantic-action", "invoke", "role=button,name=Delete Device", assessment);
        Assert.Contains("high", target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invoke", target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delete device", target, StringComparison.OrdinalIgnoreCase);
    }
}
