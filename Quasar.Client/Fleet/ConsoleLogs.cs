namespace Quasar.Client.Fleet;

public sealed record ServerLogLineWindow(
    IReadOnlyList<string> Lines,
    int StartLine,
    int TotalLineCount,
    long FileLength);

public sealed record ServerLogAppend(
    IReadOnlyList<string> Lines,
    long FileLength,
    bool Reset);

public sealed record LogTextAppend(
    string Text,
    long FileLength,
    bool Reset);

public sealed record LogTailSnapshot(
    string Text,
    bool Truncated,
    long FileLength);

public sealed record LogFileInfo(string Path, string FileName, long Bytes, DateTime LastWriteTimeUtc);
public sealed record ConsoleFiles(string Directory, LogFileInfo[] Files);
public sealed record ConsoleLogRequest(string Server, string Kind, string? Name = null, string Mode = "latest", long Offset = 0, int Start = 0, int Max = 500);
