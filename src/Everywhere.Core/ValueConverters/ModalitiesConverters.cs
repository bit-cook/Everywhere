using Avalonia.Data.Converters;
using Everywhere.AI;

namespace Everywhere.ValueConverters;

public static class ModalitiesConverters
{
    public static IValueConverter SupportsImage { get; } = new FuncValueConverter<Modalities, bool>(m => m.SupportsImage);
}