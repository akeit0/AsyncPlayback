using System.Runtime.CompilerServices;

namespace MinimumPlayback;

internal class CloneUtility
{
    public static T Clone<T>(T obj)
    {
        var cloneable = Unsafe.As<CloneUtility>((object)obj!);
        return (T)cloneable.MemberwiseClone();
    }
}
