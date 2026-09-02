namespace Train.Domain.Goods;

public sealed class VoxelVisualDefinition
{
	public string IconPath { get; }
	public string TopPath { get; }
	public string SidePath { get; }
	public string BottomPath { get; }

	public VoxelVisualDefinition(string iconPath, string topPath, string sidePath, string bottomPath)
	{
		ArgumentException.ThrowIfNullOrEmpty(iconPath);
		ArgumentException.ThrowIfNullOrEmpty(topPath);
		ArgumentException.ThrowIfNullOrEmpty(sidePath);
		ArgumentException.ThrowIfNullOrEmpty(bottomPath);
		IconPath = iconPath;
		TopPath = topPath;
		SidePath = sidePath;
		BottomPath = bottomPath;
	}
}

public sealed class GoodDefinition
{
	public const int NameMinLength = 1;
	public const int NameMaxLength = 100;
	public const int DescriptionMinLength = 1;
	public const int DescriptionMaxLength = 2000;
	public const long BaseMonetaryValueMin = 1;
	public const long BaseMonetaryValueMax = 1_000_000_000;

	public GoodId Id { get; }
	public GoodCategory Category { get; }
	public string Name { get; }
	public string Description { get; }
	public long BaseMonetaryValue { get; }
	public VoxelVisualDefinition VoxelVisual { get; }

	public GoodDefinition(
		GoodId id,
		GoodCategory category,
		string name,
		string description,
		long baseMonetaryValue,
		VoxelVisualDefinition voxelVisual)
	{
		if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
			throw new ArgumentException("GoodId cannot be blank.", nameof(id));
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(description);
		ArgumentNullException.ThrowIfNull(voxelVisual);
		if (category != GoodCategory.NaturalResource)
			throw new ArgumentOutOfRangeException(nameof(category), category, "Version 1 only supports natural_resource.");
		if (baseMonetaryValue is < BaseMonetaryValueMin or > BaseMonetaryValueMax)
			throw new ArgumentOutOfRangeException(nameof(baseMonetaryValue), baseMonetaryValue, "Base monetary value is out of range.");

		var normalizedName = NormalizeDisplayText(name, NameMinLength, NameMaxLength, nameof(name));
		var normalizedDescription = NormalizeDisplayText(description, DescriptionMinLength, DescriptionMaxLength, nameof(description));

		Id = id;
		Category = category;
		Name = normalizedName;
		Description = normalizedDescription;
		BaseMonetaryValue = baseMonetaryValue;
		VoxelVisual = voxelVisual;
	}

	public static string NormalizeDisplayText(string value, int minLength, int maxLength, string paramName)
	{
		ArgumentNullException.ThrowIfNull(value, paramName);
		var trimmed = value.Trim();
		var runes = 0;
		foreach (var _ in trimmed.EnumerateRunes())
			runes++;
		if (runes < minLength || runes > maxLength)
			throw new ArgumentException($"Must be {minLength}-{maxLength} Unicode characters after trimming.", paramName);
		return trimmed;
	}
}
