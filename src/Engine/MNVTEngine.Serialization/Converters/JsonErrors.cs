// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-03.

using Newtonsoft.Json;

namespace MNVTEngine.Serialization.Converters;

internal static class JsonErrors
{
    public static JsonSerializationException At(JsonReader reader, string message)
    {
        return reader is IJsonLineInfo line && line.HasLineInfo()
            ? new JsonSerializationException($"{message} Path '{reader.Path}', line {line.LineNumber}, position {line.LinePosition}.", reader.Path, line.LineNumber, line.LinePosition, null)
            : new JsonSerializationException($"{message} Path '{reader.Path}'.");
    }

    public static float ReadFiniteFloat(JsonReader reader, string owner)
    {
        if (reader.TokenType is not (JsonToken.Float or JsonToken.Integer))
        {
            throw At(reader, $"Expected a number in {owner}, got {reader.TokenType}.");
        }

        var value = Convert.ToSingle(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
        return float.IsFinite(value) ? value : throw At(reader, $"Non-finite number in {owner}.");
    }
}
