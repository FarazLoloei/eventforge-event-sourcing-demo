namespace EventForge.Infrastructure.Persistence.PostgreSql;

/// <summary>One named verification check and a credential-free description of its outcome.</summary>
public sealed record PostgreSqlCheck(string Name, bool Passed, string Detail);

/// <summary>Outcome of <see cref="PostgreSqlDatabase.VerifyConnectivityAsync"/>. Never contains credentials.</summary>
public sealed record PostgreSqlVerificationResult(bool Succeeded, IReadOnlyList<PostgreSqlCheck> Checks);
