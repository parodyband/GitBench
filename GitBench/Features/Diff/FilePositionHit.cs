namespace GitBench.Features.Diff;

/// <summary>A place in a file, as the file counts: a one-based line and an offset into that line's
/// own characters. What a question about the code under the pointer is asked in.</summary>
internal readonly record struct FilePositionHit(FileLine Line, RawColumn Column);
