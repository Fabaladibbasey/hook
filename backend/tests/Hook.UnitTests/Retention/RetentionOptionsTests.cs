using System.ComponentModel.DataAnnotations;
using Hook.Shared.Retention;

namespace Hook.UnitTests.Retention;

public class RetentionOptionsTests
{
    private static bool TryValidate(RetentionOptions opts, out List<ValidationResult> results)
    {
        results = [];
        return Validator.TryValidateObject(
            opts, new ValidationContext(opts), results, validateAllProperties: true);
    }

    [Fact]
    public void Defaults_AreReasonable_AndValidate()
    {
        var opts = new RetentionOptions();
        Assert.Equal(7, opts.RetentionDays);
        Assert.Equal(TimeSpan.FromHours(24), opts.SweepInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), opts.StartupDelay);
        Assert.True(opts.Enabled);
        Assert.True(TryValidate(opts, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-1)]
    public void RetentionDays_OutOfRange_FailsValidation(int days)
    {
        var opts = new RetentionOptions { RetentionDays = days };
        Assert.False(TryValidate(opts, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(365)]
    public void RetentionDays_AtBounds_PassesValidation(int days)
    {
        var opts = new RetentionOptions { RetentionDays = days };
        Assert.True(TryValidate(opts, out _));
    }

    [Fact]
    public void SweepInterval_BelowFloor_FailsValidation()
    {
        var opts = new RetentionOptions { SweepInterval = TimeSpan.FromSeconds(30) };
        Assert.False(TryValidate(opts, out _));
    }

    [Fact]
    public void SweepInterval_AboveCeiling_FailsValidation()
    {
        var opts = new RetentionOptions { SweepInterval = TimeSpan.FromDays(2) };
        Assert.False(TryValidate(opts, out _));
    }

    [Theory]
    [MemberData(nameof(SweepIntervalEdges))]
    public void SweepInterval_AtBounds_PassesValidation(TimeSpan ts)
    {
        var opts = new RetentionOptions { SweepInterval = ts };
        Assert.True(TryValidate(opts, out _));
    }

    public static IEnumerable<object[]> SweepIntervalEdges() =>
        new[]
        {
            new object[] { TimeSpan.FromMinutes(1) },
            [TimeSpan.FromDays(1)],
        };

    [Fact]
    public void StartupDelay_AboveCeiling_FailsValidation()
    {
        var opts = new RetentionOptions { StartupDelay = TimeSpan.FromMinutes(31) };
        Assert.False(TryValidate(opts, out _));
    }

    [Fact]
    public void StartupDelay_AtFloor_PassesValidation()
    {
        var opts = new RetentionOptions { StartupDelay = TimeSpan.Zero };
        Assert.True(TryValidate(opts, out _));
    }

    [Fact]
    public void StartupDelay_AtCeiling_PassesValidation()
    {
        var opts = new RetentionOptions { StartupDelay = TimeSpan.FromMinutes(30) };
        Assert.True(TryValidate(opts, out _));
    }

    [Fact]
    public void Enabled_False_StillValidates()
    {
        var opts = new RetentionOptions { Enabled = false };
        Assert.True(TryValidate(opts, out _));
    }
}
