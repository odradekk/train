using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Contracts;

namespace Train.App.Assets;

internal sealed class GodotAssetInspector(GodotMainThreadDispatcher dispatcher) : IAssetInspector
{
    public AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes)
    {
        try
        {
            switch (kind)
            {
                case AssetKind.Png:
                    using (var image = GodotAssetDecoder.Png(bytes))
                        return new AssetInspection.Success(new AssetMetadata.Png(image.GetWidth(), image.GetHeight()));
                case AssetKind.Glb:
                    using (var input = new GlbInput(bytes))
                        return dispatcher.Invoke<AssetInspection>(() =>
                        {
                            using var decoded = new GodotAssetDecoder(input);
                            return new AssetInspection.Success(decoded.Metadata());
                        });
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        catch (AssetDecodeException error) { return new AssetInspection.Failure(error.Kind, error.Message); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return new AssetInspection.Failure(AssetInspectionFailure.CannotDecode, error.Message);
        }
    }
}
