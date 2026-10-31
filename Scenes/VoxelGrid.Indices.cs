namespace Train.Scenes;

internal sealed partial class VoxelGrid
{
    private const int HalfSize = ChunkSize * ChunkSize * ChunkSize / 2;

    private sealed class Indices
    {
        private Array values;
        internal int Length => values.Length;
        internal long ArrayBytes => Buffer.ByteLength(values);

        internal Indices() => values = new byte[HalfSize];

        internal Indices(ReadOnlySpan<int> source)
        {
            foreach (var value in source)
                if (value > byte.MaxValue)
                {
                    values = source.ToArray();
                    return;
                }
            var bytes = new byte[source.Length];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)source[index];
            values = bytes;
        }

        internal int this[int index]
        {
            get => values is byte[] bytes ? bytes[index] : ((int[])values)[index];
            set
            {
                if (values is byte[] bytes)
                {
                    if (value <= byte.MaxValue) { bytes[index] = (byte)value; return; }
                    var wide = new int[bytes.Length];
                    for (var offset = 0; offset < bytes.Length; offset++) wide[offset] = bytes[offset];
                    values = wide;
                }
                ((int[])values)[index] = value;
            }
        }

        internal bool Matches(ReadOnlySpan<int> other)
        {
            if (values is int[] wide) return other.SequenceEqual(wide);
            var bytes = (byte[])values;
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] != other[index]) return false;
            return true;
        }
    }
}
