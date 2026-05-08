; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID  | Category                  | Severity | Notes
---------|---------------------------|----------|------------------------------------------------------------------
POLY1001 | Polyphony.SchemaGenerator | Error    | Verb method must declare its result type ([VerbResult] missing).
POLY1002 | Polyphony.SchemaGenerator | Error    | Verb result type is not registered on PolyphonyJsonContext.
POLY1003 | Polyphony.SchemaGenerator | Error    | Commands class containing [Command] methods must declare its verb group.
POLY1004 | Polyphony.SchemaGenerator | Error    | Verb group declarations conflict across partial-class declarations.
POLY1005 | Polyphony.SchemaGenerator | Warning  | Verb output schema cannot key on a multi-alias command.
POLY1006 | Polyphony.SchemaGenerator | Error    | Command name must not be empty.
