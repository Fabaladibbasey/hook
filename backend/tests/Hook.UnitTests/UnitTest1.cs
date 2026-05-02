using Hook.Shared.Core;
using Shouldly;

namespace Hook.UnitTests;

public class ResultTests
{
    [Fact]
    public void Success_ShouldReturnSuccessResult()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_ShouldCarryError()
    {
        var error = new Error("test.failed", "Test failed");

        var result = Result.Failure(error);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void GenericResult_ShouldExposeValueOnSuccess()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void GenericResult_ShouldThrowOnValueAccessForFailure()
    {
        Result<int> result = new Error("oops", "oops");

        Should.Throw<InvalidOperationException>(() => _ = result.Value);
    }
}
