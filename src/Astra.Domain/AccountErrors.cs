namespace Astra.Domain;

public abstract class AccountCommandException(string message) : InvalidOperationException(message);

public sealed class InvalidAccountCommandException(string message) : AccountCommandException(message);

public sealed class InsufficientCurrencyException(string message) : AccountCommandException(message);

public sealed class IdempotencyConflictException(string message) : AccountCommandException(message);

public sealed class MailNotFoundException(string message) : AccountCommandException(message);

public sealed class MailNotEligibleException(string message) : AccountCommandException(message);

public sealed class MailAlreadyClaimedException(string message) : AccountCommandException(message);
