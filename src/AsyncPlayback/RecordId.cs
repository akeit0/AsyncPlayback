namespace AsyncPlayback;

public readonly record struct RecordId(int Value)
{
    public static implicit operator int(RecordId id)
    {
        return id.Value;
    }

    public static explicit operator RecordId(int value)
    {
        return new(value);
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}
