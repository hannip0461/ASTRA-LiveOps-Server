namespace Astra.Domain;

public sealed class ContentUnavailableException(string message) : InvalidOperationException(message);

public sealed class ContentMismatchException(string message) : InvalidOperationException(message);

public sealed class ContentVersionConflictException(string message) : InvalidOperationException(message);

public sealed class ContentVersionInactiveException(string message) : InvalidOperationException(message);
