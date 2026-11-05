using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Elements;

/// <summary>An editable property of an element, as TerrainHeaven's properties panel shows it.</summary>
public abstract record PropertyItem(string Label);

public sealed record Vector3Property(string Label, Func<Vec3> Get, Action<Vec3> Set) : PropertyItem(Label);

public sealed record NumberProperty(string Label, Func<double> Get, Action<double> Set, double? Min = null, double? Max = null, double? Step = null) : PropertyItem(Label);

public sealed record BooleanProperty(string Label, Func<bool> Get, Action<bool> Set) : PropertyItem(Label);

public sealed record SelectOption(string Label, string Value);

public sealed record SelectProperty(string Label, IReadOnlyList<SelectOption> Options, Func<string> Get, Action<string> Set) : PropertyItem(Label);

public sealed record ButtonProperty(string Label, Action OnClick) : PropertyItem(Label);

public sealed record PropertySection(string Label, IReadOnlyList<PropertyItem> Properties);

/// <summary>What the properties panel shows for a selection: a title, an icon and sections of properties.</summary>
public sealed record PropertyDefinition(string Title, string Icon, IReadOnlyList<PropertySection> Sections);
