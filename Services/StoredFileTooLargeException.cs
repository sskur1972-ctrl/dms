namespace DMS.Web.Services;
public sealed class StoredFileTooLargeException(long limitBytes) : IOException($"The file exceeds {limitBytes} bytes.") { }
