using System.Collections;

namespace Godot
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GlobalClassAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ExportAttribute : Attribute;

    public class Resource;
}

namespace Godot.Collections
{
    public sealed class Array<T> : List<T>, IEnumerable
    {
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
