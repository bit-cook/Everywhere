using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

public abstract class FunctionContentMessagePackFormatter<T> : IMessagePackFormatter<T?> where T : class
{
    public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        SerializeCore(ref writer, value, options);
    }

    public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            return LegacyDeserializeCore(ref reader, options);
        }

        return DeserializeCore(ref reader, options);
    }

    protected abstract void SerializeCore(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options);

    protected abstract T DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options);

    protected abstract T LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options);

    protected void SerializeDictionary(
        ref MessagePackWriter writer,
        IReadOnlyDictionary<string, object?>? value,
        MessagePackSerializerOptions options)
    {
        writer.WriteMapHeader(value?.Count ?? 0);
        if (value is null) return;

        foreach (var (key, val) in value)
        {
            writer.Write(key);

            if (val is null)
            {
                writer.WriteNil();
            }
            else
            {
                MessagePackSerializer.Serialize(val.GetType(), ref writer, val, options);
            }
        }
    }

    protected Dictionary<string, object?>? DeserializeDictionary(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        var count = reader.ReadMapHeader();
        if (count == 0) return null;

        var dict = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString() ?? throw new MessagePackSerializationException("Dictionary key cannot be null.");
            var val = MessagePackSerializer.Deserialize<object>(ref reader, options);
            dict[key] = val;
        }

        return dict;
    }
}