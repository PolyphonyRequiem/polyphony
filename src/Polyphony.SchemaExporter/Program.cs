using Polyphony;

// Tiny exporter: reads VerbOutputSchemaCatalog.Json (compile-time
// constant emitted by Polyphony.SchemaGenerator) and writes it to a
// file path passed via `--out <path>`.
//
// Invoked from Polyphony.csproj's AfterBuild target so the artifact
// lands at `artifacts/verb-output-schemas.json` after every build.

string? outPath = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length)
    {
        outPath = args[i + 1];
        break;
    }
}

if (outPath is null)
{
    Console.Error.WriteLine("error: --out <path> is required");
    return 2;
}

var directory = Path.GetDirectoryName(outPath);
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

await File.WriteAllTextAsync(outPath, VerbOutputSchemaCatalog.Json);
Console.WriteLine($"verb-output-schemas: wrote {outPath} ({VerbOutputSchemaCatalog.Json.Length} bytes)");
return 0;
