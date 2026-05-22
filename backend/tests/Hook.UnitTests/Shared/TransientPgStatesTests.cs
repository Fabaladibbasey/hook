using Hook.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace Hook.UnitTests.Shared;

public class TransientPgStatesTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.TooManyConnections)]
    [InlineData(PostgresErrorCodes.ConnectionException)]
    [InlineData(PostgresErrorCodes.ConnectionFailure)]
    public void IsTransient_AcceptsKnownTransientSqlStates(string sqlState) =>
        TransientPgStates.IsTransient(sqlState).ShouldBeTrue();

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData("42P01")]
    [InlineData("40000")]
    [InlineData("")]
    public void IsTransient_RejectsNonTransientSqlStates(string sqlState) =>
        TransientPgStates.IsTransient(sqlState).ShouldBeFalse();

    [Fact]
    public void IsTransient_NullArgument_ReturnsFalse() =>
        TransientPgStates.IsTransient((string?)null).ShouldBeFalse();

    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure, true)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, true)]
    [InlineData(PostgresErrorCodes.TooManyConnections, false)]
    [InlineData(PostgresErrorCodes.ConnectionException, false)]
    [InlineData(PostgresErrorCodes.UniqueViolation, false)]
    public void IsTransientFast_OnlyFastCategory(string sqlState, bool expected) =>
        TransientPgStates.IsTransientFast(sqlState).ShouldBe(expected);

    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure, false)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, false)]
    [InlineData(PostgresErrorCodes.TooManyConnections, true)]
    [InlineData(PostgresErrorCodes.ConnectionException, true)]
    [InlineData(PostgresErrorCodes.ConnectionFailure, true)]
    public void IsTransientSlow_OnlySlowCategory(string sqlState, bool expected) =>
        TransientPgStates.IsTransientSlow(sqlState).ShouldBe(expected);

    [Fact]
    public void IsTransient_Exception_WalksInnerChain_OnDbUpdateExceptionWrap()
    {
        // EF Core wraps PostgresException in DbUpdateException on SaveChanges failure.
        // Wolverine retry policy must see through the wrap.
        var pg = NewPg(PostgresErrorCodes.SerializationFailure);
        var wrapped = new DbUpdateException("wrap", pg);

        TransientPgStates.IsTransient(wrapped).ShouldBeTrue();
        TransientPgStates.IsTransientFast(wrapped).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_Exception_WalksTwoLevelChain()
    {
        var pg = NewPg(PostgresErrorCodes.TooManyConnections);
        var wrapped = new InvalidOperationException("outer",
            new DbUpdateException("middle", pg));

        TransientPgStates.IsTransient(wrapped).ShouldBeTrue();
        TransientPgStates.IsTransientSlow(wrapped).ShouldBeTrue();
        TransientPgStates.IsTransientFast(wrapped).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_Exception_NullArgument_ReturnsFalse() =>
        TransientPgStates.IsTransient((Exception?)null).ShouldBeFalse();

    [Fact]
    public void IsTransient_Exception_NonPostgresChain_ReturnsFalse() =>
        TransientPgStates.IsTransient(new InvalidOperationException("nope")).ShouldBeFalse();

    private static PostgresException NewPg(string sqlState) =>
        new(messageText: "test", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);
}
