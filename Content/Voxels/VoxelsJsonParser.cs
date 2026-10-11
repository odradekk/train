// Content 校验外部定义的严格 JSON 格式。
// 本文件解析体素字段和图片绑定，候选引用与图片检查由目录构建阶段完成。

using System.Text.Json;
using Train.Content.Behaviors;

namespace Train.Content.Voxels;

/// <summary>暂存通过字段校验的体素定义及其源文件，等待包和图片校验。</summary>
internal sealed class ParsedVoxel
{
    public string LocalId { get; }
    public string Name { get; }
    public string Description { get; }
    public long Value { get; }
    public double Mass { get; }
    public int MaxDurability { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<VoxelBehavior> Behaviors { get; }
    public VoxelOrientationConstraint AllowedOrientations { get; }
    public VoxelPresentation Presentation { get; }
    public VoxelCollision Collision { get; }
    public string SourceFile { get; }

    public ParsedVoxel(
        string localId,
        string name,
        string description,
        long value,
        double mass,
        int maxDurability,
        IReadOnlyList<string> tags,
        IReadOnlyList<VoxelBehavior> behaviors,
        VoxelOrientationConstraint allowedOrientations,
        VoxelPresentation presentation,
        VoxelCollision collision,
        string sourceFile)
    {
        LocalId = localId;
        Name = name;
        Description = description;
        Value = value;
        Mass = mass;
        MaxDurability = maxDurability;
        Tags = tags;
        Behaviors = behaviors;
        AllowedOrientations = allowedOrientations;
        Presentation = presentation;
        Collision = collision;
        SourceFile = sourceFile;
    }
}

/// <summary>严格解析当前体素内容格式，将格式错误归入所属包的诊断。</summary>
internal static class VoxelsJsonParser
{
    /// <summary>当前唯一支持的文件 schema 版本，固定为 2；格式约定见模块说明。</summary>
    private const int SchemaVersion = 2;

    /// <summary>解析 UTF-8 体素 JSON；结构或字段无效时记录诊断并返回 null。</summary>
    public static IReadOnlyList<ParsedVoxel>? Parse(
        byte[] utf8,
        string sourceFile,
        DiagnosticBag diagnostics)
    {
        return ContentJson.ParseDefinitions(utf8, sourceFile, SchemaVersion, "voxels", diagnostics,
            (element, path) => TryParseVoxel(element, sourceFile, path, diagnostics, out var voxel) ? voxel : null);
    }

    /// <summary>校验一个体素条目的身份、数值、标签、行为和表现字段。</summary>
    private static bool TryParseVoxel(
        JsonElement element,
        string sourceFile,
        string jsonPath,
        DiagnosticBag diagnostics,
        out ParsedVoxel voxel)
    {
        voxel = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Reject(ContentDiagnosticCodes.FieldInvalid, "Each voxel must be an object.", jsonPath);
            return false;
        }

        string? localId = null;
        string? name = null;
        string? description = null;
        long? value = null;
        double? mass = null;
        int? maxDurability = null;
        IReadOnlyList<string>? tags = null;
        IReadOnlyList<VoxelBehavior>? behaviors = null;
        string? orientationsText = null;
        JsonElement? presentation = null;
        JsonElement? collision = null;

        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "id":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.LocalIdInvalid, out localId))
                        return false;
                    break;
                case "name":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.NameInvalid, out name))
                        return false;
                    break;
                case "description":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.DescriptionInvalid, out description))
                        return false;
                    break;
                case "value":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var parsedValue))
                    {
                        diagnostics.Reject(ContentDiagnosticCodes.ValueInvalid, "Value must be an integer.", fieldPath);
                        return false;
                    }

                    value = parsedValue;
                    break;
                case "mass":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.MassInvalid, out var parsedMass))
                        return false;
                    mass = parsedMass;
                    break;
                case "max_durability":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedDurability))
                    {
                        diagnostics.Reject(ContentDiagnosticCodes.MaxDurabilityInvalid, "Max durability must be an integer.", fieldPath);
                        return false;
                    }

                    maxDurability = parsedDurability;
                    break;
                case "tags":
                    if (!TryParseTags(property.Value, fieldPath, diagnostics, out tags))
                        return false;
                    break;
                case "behaviors":
                    if (!TryParseBehaviors(property.Value, fieldPath, diagnostics, out behaviors))
                        return false;
                    break;
                case "allowed_orientations":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.OrientationInvalid, out orientationsText))
                        return false;
                    break;
                case "presentation":
                    presentation = property.Value;
                    break;
                case "collision":
                    collision = property.Value;
                    break;
                default:
                    diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (localId is null || !VoxelDefinitionId.IsValidPart(localId))
        {
            diagnostics.Reject(ContentDiagnosticCodes.LocalIdInvalid, "Local id is invalid.", jsonPath + "/id");
            return false;
        }

        if (!StrictJson.TryNormalizeText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, diagnostics, jsonPath + "/name", ContentDiagnosticCodes.NameInvalid, out var normalizedName))
            return false;
        if (!StrictJson.TryNormalizeText(description, VoxelDefinition.DescriptionMinLength, VoxelDefinition.DescriptionMaxLength, diagnostics, jsonPath + "/description", ContentDiagnosticCodes.DescriptionInvalid, out var normalizedDescription))
            return false;

        if (value is null or < VoxelDefinition.ValueMin or > VoxelDefinition.ValueMax)
        {
            diagnostics.Reject(ContentDiagnosticCodes.ValueInvalid, "Value is out of range.", jsonPath + "/value");
            return false;
        }

        if (mass is null ||
            double.IsNaN(mass.Value) ||
            double.IsInfinity(mass.Value) ||
            mass.Value <= VoxelDefinition.MassMinExclusive ||
            mass.Value > VoxelDefinition.MassMax)
        {
            diagnostics.Reject(ContentDiagnosticCodes.MassInvalid, "Mass is out of range.", jsonPath + "/mass");
            return false;
        }

        if (maxDurability is null or < VoxelDefinition.MaxDurabilityMin or > VoxelDefinition.MaxDurabilityMax)
        {
            diagnostics.Reject(ContentDiagnosticCodes.MaxDurabilityInvalid, "Max durability is out of range.", jsonPath + "/max_durability");
            return false;
        }

        if (tags is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.TagInvalid, "tags must be an array.", jsonPath + "/tags");
            return false;
        }

        if (behaviors is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "behaviors must be an array.", jsonPath + "/behaviors");
            return false;
        }

        if (!VoxelDefinition.TryParseOrientations(orientationsText, out var orientations))
        {
            diagnostics.Reject(ContentDiagnosticCodes.OrientationInvalid, "Unsupported orientation constraint.", jsonPath + "/allowed_orientations");
            return false;
        }

        if (presentation is null || !TryParsePresentation(presentation.Value, jsonPath + "/presentation", diagnostics, out var parsedPresentation))
        {
            if (presentation is null)
                diagnostics.Reject(ContentDiagnosticCodes.PresentationRequired, "presentation is required.", jsonPath + "/presentation");
            return false;
        }

        if (collision is null || !TryParseCollision(collision.Value, jsonPath + "/collision", diagnostics, out var parsedCollision))
        {
            if (collision is null)
                diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "collision is required.", jsonPath + "/collision");
            return false;
        }

        voxel = new ParsedVoxel(
            localId,
            normalizedName,
            normalizedDescription,
            value.Value,
            mass.Value,
            maxDurability.Value,
            tags,
            behaviors,
            orientations,
            parsedPresentation,
            parsedCollision,
            sourceFile);
        return true;
    }

    /// <summary>校验标签文本和唯一性，返回规范化的标签集合。</summary>
    private static bool TryParseTags(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Reject(ContentDiagnosticCodes.TagInvalid, "tags must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{jsonPath}/{index}";
            if (!StrictJson.TryGetString(item, diagnostics, itemPath, ContentDiagnosticCodes.TagInvalid, out var raw))
                return false;
            try
            {
                var tag = VoxelDefinition.NormalizeTag(raw, "tag");
                if (!seen.Add(tag))
                {
                    diagnostics.Reject(ContentDiagnosticCodes.TagInvalid, "Duplicate tag.", itemPath);
                    return false;
                }

                parsed.Add(tag);
            }
            catch (ArgumentException)
            {
                diagnostics.Reject(ContentDiagnosticCodes.TagInvalid, "Tag is invalid.", itemPath);
                return false;
            }

            index++;
        }

        tags = parsed;
        return true;
    }

    /// <summary>按行为种类校验字段和参数，返回保留声明顺序的类型化行为。</summary>
    private static bool TryParseBehaviors(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out IReadOnlyList<VoxelBehavior> behaviors)
    {
        behaviors = Array.Empty<VoxelBehavior>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "behaviors must be an array.", jsonPath);
            return false;
        }

        var parsed = new List<VoxelBehavior>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{jsonPath}/{index}";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Each behavior must be an object.", itemPath);
                return false;
            }

            string? type = null;
            double? traction = null;
            double? ratedSpeed = null;
            int? capacity = null;
            if (item.TryGetProperty("type", out var typeElement) &&
                !StrictJson.TryGetString(typeElement, diagnostics, itemPath + "/type", ContentDiagnosticCodes.BehaviorUnregistered, out type))
                return false;

            foreach (var property in item.EnumerateObject())
            {
                if (property.Name == "type")
                    continue;

                if (type == BehaviorTypes.Propulsion && property.Name == "traction")
                {
                    if (!TryGetFiniteDouble(property.Value, diagnostics, itemPath + "/traction", ContentDiagnosticCodes.BehaviorUnregistered, out var parsedTraction))
                        return false;
                    traction = parsedTraction;
                    continue;
                }

                if (type == BehaviorTypes.Propulsion && property.Name == "rated_speed")
                {
                    if (!TryGetFiniteDouble(property.Value, diagnostics, itemPath + "/rated_speed", ContentDiagnosticCodes.BehaviorUnregistered, out var parsedRatedSpeed))
                        return false;
                    ratedSpeed = parsedRatedSpeed;
                    continue;
                }

                if (type == BehaviorTypes.Cargo && property.Name == "capacity")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedCapacity))
                    {
                        diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Cargo capacity must be an integer.", itemPath + "/capacity");
                        return false;
                    }
                    capacity = parsedCapacity;
                    continue;
                }

                diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", itemPath + "/" + property.Name);
                return false;
            }

            if (string.IsNullOrEmpty(type))
            {
                diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Behavior type is required.", itemPath + "/type");
                return false;
            }

            if (!BehaviorRegistry.IsRegistered(type))
            {
                diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Unknown or unregistered behavior.", itemPath + "/type");
                return false;
            }
            if (!seen.Add(type))
            {
                diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Each behavior type may appear only once.", itemPath);
                return false;
            }

            VoxelBehavior behavior;
            if (type == BehaviorTypes.Propulsion)
            {
                if (traction is null || ratedSpeed is null || traction <= 0 || ratedSpeed <= 0)
                {
                    diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Propulsion requires finite positive traction and rated_speed.", itemPath);
                    return false;
                }
                behavior = new PropulsionBehavior(traction.Value, ratedSpeed.Value);
            }
            else if (type == BehaviorTypes.Cargo)
            {
                if (capacity is null or < 1)
                {
                    diagnostics.Reject(ContentDiagnosticCodes.BehaviorUnregistered, "Cargo requires capacity from 1 through Int32.MaxValue.", itemPath + "/capacity");
                    return false;
                }
                behavior = new CargoBehavior(capacity.Value);
            }
            else
                behavior = new ControlBehavior();

            parsed.Add(behavior);
            index++;
        }

        behaviors = parsed;
        return true;
    }

    /// <summary>校验方块表现绑定和各面图片的安全相对路径。</summary>
    private static bool TryParsePresentation(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out VoxelPresentation presentation)
    {
        presentation = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Reject(ContentDiagnosticCodes.PresentationRequired, "presentation must be an object.", jsonPath);
            return false;
        }

        string? kindText = null, icon = null, top = null, side = null, bottom = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "kind":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.PresentationInvalid, out kindText))
                        return false;
                    break;
                case "icon":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out icon))
                        return false;
                    break;
                case "voxel_top":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out top))
                        return false;
                    break;
                case "voxel_side":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out side))
                        return false;
                    break;
                case "voxel_bottom":
                    if (!TryGetResourcePath(property.Value, diagnostics, fieldPath, out bottom))
                        return false;
                    break;
                default:
                    diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (!VoxelDefinition.TryParsePresentationKind(kindText, out var kind))
        {
            diagnostics.Reject(ContentDiagnosticCodes.PresentationInvalid, "Unsupported presentation kind.", jsonPath + "/kind");
            return false;
        }

        if (icon is null || top is null || side is null || bottom is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.PresentationRequired, "cube presentation requires icon, voxel_top, voxel_side, and voxel_bottom.", jsonPath);
            return false;
        }

        presentation = new VoxelPresentation(kind, icon, top, side, bottom);
        return true;
    }

    /// <summary>校验碰撞种类和局部盒几何，包括 float 表现精度下的正体积。</summary>
    private static bool TryParseCollision(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out VoxelCollision collision)
    {
        collision = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "collision must be an object.", jsonPath);
            return false;
        }

        string? kindText = null;
        JsonElement? offset = null;
        JsonElement? size = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "kind":
                    if (!StrictJson.TryGetString(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out kindText))
                        return false;
                    break;
                case "offset":
                    offset = property.Value;
                    break;
                case "size":
                    size = property.Value;
                    break;
                default:
                    diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (!VoxelDefinition.TryParseCollisionKind(kindText, out var kind))
        {
            diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "Unsupported collision kind.", jsonPath + "/kind");
            return false;
        }

        if (kind is VoxelCollisionKind.None or VoxelCollisionKind.FullBox)
        {
            if (offset is not null || size is not null)
            {
                diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "none and full_box cannot include a local box.", jsonPath);
                return false;
            }

            collision = new VoxelCollision(kind);
            return true;
        }

        if (offset is null || size is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "local_box requires offset and size.", jsonPath);
            return false;
        }

        if (!TryParseLocalVector(offset.Value, jsonPath + "/offset", diagnostics, out var offsetX, out var offsetY, out var offsetZ) ||
            !TryParseLocalVector(size.Value, jsonPath + "/size", diagnostics, out var sizeX, out var sizeY, out var sizeZ))
            return false;

        if (!VoxelCollision.TryCreateLocalBox(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ, out collision))
        {
            diagnostics.Reject(
                ContentDiagnosticCodes.CollisionInvalid,
                "local_box must be finite, strictly positive, and a distinguishable interval inside the unit cell.",
                jsonPath);
            return false;
        }

        if (!VoxelCollisionValidation.CanDerivePositiveVolume(collision))
        {
            diagnostics.Reject(
                ContentDiagnosticCodes.CollisionInvalid,
                "local_box must keep a positive volume after the collision adapter conversion.",
                jsonPath);
            return false;
        }

        return true;
    }

    /// <summary>读取局部盒的三轴有限数值，保留各分量的诊断路径。</summary>
    private static bool TryParseLocalVector(
        JsonElement element,
        string jsonPath,
        DiagnosticBag diagnostics,
        out double x,
        out double y,
        out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "Local box vector must be an object.", jsonPath);
            return false;
        }

        double? parsedX = null, parsedY = null, parsedZ = null;
        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = jsonPath + "/" + property.Name;
            switch (property.Name)
            {
                case "x":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out var valueX))
                        return false;
                    parsedX = valueX;
                    break;
                case "y":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out var valueY))
                        return false;
                    parsedY = valueY;
                    break;
                case "z":
                    if (!TryGetFiniteDouble(property.Value, diagnostics, fieldPath, ContentDiagnosticCodes.CollisionInvalid, out var valueZ))
                        return false;
                    parsedZ = valueZ;
                    break;
                default:
                    diagnostics.Reject(ContentDiagnosticCodes.UnknownField, "Unknown field.", fieldPath);
                    return false;
            }
        }

        if (parsedX is null || parsedY is null || parsedZ is null)
        {
            diagnostics.Reject(ContentDiagnosticCodes.CollisionInvalid, "Local box vector requires x, y, and z.", jsonPath);
            return false;
        }

        x = parsedX.Value;
        y = parsedY.Value;
        z = parsedZ.Value;
        return true;
    }

    /// <summary>读取并校验资源路径，拒绝不安全或不支持的图片路径。</summary>
    private static bool TryGetResourcePath(JsonElement element, DiagnosticBag diagnostics, string jsonPath, out string path)
    {
        path = "";
        if (!StrictJson.TryGetString(element, diagnostics, jsonPath, ContentDiagnosticCodes.PathInvalid, out var raw))
            return false;
        if (!ContentPath.TryNormalize(raw, out var normalized) || !ContentPath.IsPngPath(normalized))
        {
            diagnostics.Reject(ContentDiagnosticCodes.PathInvalid, "Visual path is not a safe relative PNG path.", jsonPath);
            return false;
        }

        path = normalized;
        return true;
    }

    /// <summary>校验 JSON 数值为有限 double，并在失败时记录字段诊断。</summary>
    private static bool TryGetFiniteDouble(
        JsonElement element,
        DiagnosticBag diagnostics,
        string jsonPath,
        string code,
        out double value)
    {
        value = 0;
        var message = code == ContentDiagnosticCodes.MassInvalid
            ? "Mass must be a finite number."
            : "Number must be finite.";
        if (element.ValueKind != JsonValueKind.Number)
        {
            diagnostics.Reject(code, message, jsonPath);
            return false;
        }

        if (!element.TryGetDouble(out value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            diagnostics.Reject(code, message, jsonPath);
            return false;
        }

        return true;
    }
}
