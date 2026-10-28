namespace Zexoro.Formats.MacOS;

/// <summary>Packing a macOS format failed; the message is for the user.</summary>
public sealed class MacPackException(string message, Exception? innerException = null) : Exception(message, innerException);
