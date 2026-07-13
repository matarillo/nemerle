using System.Text.Json;

namespace Nemerle.ProjectInfo;

public static class MsBuildJsonParser
{
    public static NemerleProjectSnapshot Parse(ProjectQueryKey key, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ProjectQueryException(ProjectQueryErrorKind.InvalidJson, "MSBuild returned invalid JSON.", innerException: ex);
        }

        using (document)
        {
            try
            {
                var root = document.RootElement;
                var properties = RequiredObject(root, "Properties");
                var items = RequiredObject(root, "Items");
                var projectPath = RequiredString(properties, "MSBuildProjectFullPath");
                var projectDirectory = RequiredString(properties, "MSBuildProjectDirectory");
                var targetFramework = RequiredString(properties, "TargetFramework");
                var configuration = RequiredString(properties, "Configuration");
                var platform = RequiredString(properties, "Platform");
                var defineText = OptionalString(properties, "DefineConstants");
                var additionalOptions = OptionalString(properties, "NemerleAdditionalOptions");

                var sources = ReadPaths(items, "NemerleCompile", static _ => true);
                var references = ReadPaths(items, "ReferencePath", static item =>
                    string.IsNullOrEmpty(OptionalString(item, "FrameworkReferenceName")));
                var macros = ReadPaths(items, "NemerleMacroReference", static _ => true);
                var optionSnapshot = ProjectOptionClassifier.Classify(additionalOptions);
                var defines = defineText
                    .Split([';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Concat(optionSnapshot.AdditionalDefines)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var warnings = new List<string>();
                if (optionSnapshot.UnsupportedSemanticOptions.Count > 0)
                    warnings.Add("Unsupported semantic-affecting NemerleAdditionalOptions were preserved but are not applied in WP-L2: " +
                                 string.Join(" ", optionSnapshot.UnsupportedSemanticOptions));
                if (optionSnapshot.UnsupportedDiagnosticOptions.Count > 0)
                    warnings.Add("Unsupported diagnostic-affecting NemerleAdditionalOptions were preserved but are not applied in WP-L2: " +
                                 string.Join(" ", optionSnapshot.UnsupportedDiagnosticOptions));

                return new NemerleProjectSnapshot(
                    key,
                    ProjectPathNormalizer.NormalizeFile(projectPath),
                    ProjectPathNormalizer.NormalizeDirectory(projectDirectory),
                    configuration,
                    platform,
                    targetFramework,
                    sources,
                    references,
                    macros,
                    defines,
                    optionSnapshot,
                    warnings,
                    DateTimeOffset.UtcNow);
            }
            catch (ProjectQueryException)
            {
                throw;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new ProjectQueryException(ProjectQueryErrorKind.MissingField, ex.Message, innerException: ex);
            }
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ProjectQueryException(ProjectQueryErrorKind.MissingField, $"MSBuild JSON is missing object '{name}'.");
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = OptionalString(parent, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ProjectQueryException(ProjectQueryErrorKind.MissingField, $"MSBuild JSON is missing required field '{name}'.");
        return value;
    }

    private static string OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadPaths(
        JsonElement items,
        string itemName,
        Func<JsonElement, bool> predicate)
    {
        if (!items.TryGetProperty(itemName, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new ProjectQueryException(ProjectQueryErrorKind.MissingField, $"MSBuild JSON is missing item array '{itemName}'.");

        var paths = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (!predicate(item))
                continue;
            var path = RequiredString(item, "FullPath");
            paths.Add(path);
        }
        return ProjectPathNormalizer.NormalizeDistinct(paths);
    }
}
