namespace Core.Exceptions;

public class CacheUnavailableException(string message, Exception inner) : Exception(message, inner);

public class CacheCorruptedException(string message, Exception inner) : Exception(message, inner);

public class CacheSerializationException(string message, Exception inner) : Exception(message, inner);

public class IdentityException(string message, Exception innerException) : Exception(message, innerException)
{
}

public class DataUpdateException(string message, Exception innerException) : Exception(message, innerException);