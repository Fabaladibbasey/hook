using System.ComponentModel.DataAnnotations;
using Hook.Features.Matching;

namespace Hook.UnitTests.Matching;

public class MatchingOptionsTests
{
    private static bool TryValidate(MatchingOptions opts, out List<ValidationResult> results)
    {
        results = [];
        return Validator.TryValidateObject(
            opts, new ValidationContext(opts), results, validateAllProperties: true);
    }

    [Fact]
    public void Defaults_Validate()
    {
        var opts = new MatchingOptions();
        Assert.Equal(200, opts.MaxCandidatePoolSize);
        Assert.Equal(8, opts.MaxBranchCount);
        Assert.True(TryValidate(opts, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    public void MaxBranchCount_OutOfRange_FailsValidation(int value)
    {
        var opts = new MatchingOptions { MaxBranchCount = value };
        Assert.False(TryValidate(opts, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void MaxBranchCount_AtBounds_PassesValidation(int value)
    {
        var opts = new MatchingOptions { MaxBranchCount = value };
        Assert.True(TryValidate(opts, out _));
    }
}
