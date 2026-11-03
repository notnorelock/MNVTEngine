using Newtonsoft.Json.Linq;

namespace MNVTEngine.Serialization;

/// <summary>
/// Upgrades JSON documents carrying an integer schema version, one version at a time, before they are
/// bound to types. Each step receives the document at version N and must leave it valid for N + 1.
/// </summary>
public sealed class JsonMigrationPipeline
{
    private readonly Dictionary<int, Action<JObject>> _steps = [];

    public JsonMigrationPipeline(int currentVersion, string versionProperty = "schemaVersion")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentVersion);
        CurrentVersion = currentVersion;
        VersionProperty = versionProperty;
    }

    public int CurrentVersion { get; }

    public string VersionProperty { get; }

    /// <summary>Registers the step that upgrades documents from <paramref name="fromVersion"/> to <paramref name="fromVersion"/> + 1.</summary>
    public JsonMigrationPipeline Add(int fromVersion, Action<JObject> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        if (fromVersion < 0 || fromVersion >= CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion), fromVersion, $"Steps must start below the current version {CurrentVersion}.");
        }

        if (!_steps.TryAdd(fromVersion, migrate))
        {
            throw new InvalidOperationException($"A migration from version {fromVersion} is already registered.");
        }

        return this;
    }

    /// <summary>Migrates <paramref name="document"/> in place to <see cref="CurrentVersion"/>.</summary>
    /// <returns>The version the document had before migration.</returns>
    /// <exception cref="InvalidDataException">Version missing, invalid, newer than supported, or no migration path.</exception>
    public int Migrate(JObject document, ICollection<SerializationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (document[VersionProperty] is not JValue { Type: JTokenType.Integer } token)
        {
            throw new InvalidDataException($"Missing or non-integer '{VersionProperty}'.");
        }

        var original = token.Value<int>();
        if (original > CurrentVersion)
        {
            throw new InvalidDataException($"Schema version {original} is newer than the supported version {CurrentVersion}. Update the engine.");
        }

        if (original < 0)
        {
            throw new InvalidDataException($"Invalid schema version {original}.");
        }

        for (var version = original; version < CurrentVersion; version++)
        {
            if (!_steps.TryGetValue(version, out var step))
            {
                throw new InvalidDataException($"No migration from schema version {version} to {version + 1}.");
            }

            step(document);
            document[VersionProperty] = version + 1;
        }

        if (original != CurrentVersion)
        {
            diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Info, VersionProperty, $"Migrated from schema version {original} to {CurrentVersion}."));
        }

        return original;
    }
}
