using System.Text.Json;
using RegistryRepair.Windows;

if (!TryReadArgument(args, "--image-root", out string? imageRoot) ||
    !TryReadArgument(args, "--output", out string? output))
{
    Console.Error.WriteLine(
        "Usage: RegistryRepair.CorpusTool --image-root <path> --output <manifest.json>");
    return 2;
}

try
{
    using OfflineWindowsImage image = OfflineWindowsImage.Open(imageRoot);
    if (!image.SourceHivesAreUnchanged())
        throw new InvalidOperationException("A source hive changed during analysis.");

    var manifest = new CorpusManifest(
        1,
        DateTimeOffset.UtcNow,
        image.Identity,
        image.HiveHashes);
    string destination = Path.GetFullPath(output);
    string? directory = Path.GetDirectoryName(destination);
    if (string.IsNullOrEmpty(directory))
        throw new InvalidOperationException("The manifest output directory is invalid.");
    Directory.CreateDirectory(directory);

    string temporary = destination + ".tmp";
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
    });
    await using (var stream = new FileStream(
        temporary,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        4096,
        FileOptions.WriteThrough | FileOptions.Asynchronous))
    {
        await stream.WriteAsync(json);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }
    File.Move(temporary, destination, overwrite: true);
    Console.WriteLine(
        $"Windows 11 {image.Identity.DisplayVersion} build " +
        $"{image.Identity.Build}.{image.Identity.UpdateBuildRevision} " +
        $"{image.Identity.Edition}");
    Console.WriteLine(destination);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static bool TryReadArgument(string[] arguments, string name, out string value)
{
    for (int index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            value = arguments[index + 1];
            return !string.IsNullOrWhiteSpace(value);
        }
    }

    value = string.Empty;
    return false;
}

internal sealed record CorpusManifest(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    OfflineWindowsImageIdentity Windows,
    IReadOnlyDictionary<string, string> HiveSha256);
