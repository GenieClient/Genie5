using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #287 — persistence JSON uses source-generated metadata. The swap has
/// to be invisible: same bytes on disk, same parse results, and the same
/// failures (the strict live-reload parse is pinned separately by
/// <c>RuleFileLiveReloadSmokeTests.CorruptFileThrowsAndLeavesEngineUntouched</c>).
/// </summary>
public class PersistenceJsonContextTests
{
    public static TheoryData<Type> PersistedTypes() => new()
    {
        typeof(IEnumerable<AliasPersistenceModel>),        typeof(List<AliasPersistenceModel>),
        typeof(IEnumerable<TriggerPersistenceModel>),      typeof(List<TriggerPersistenceModel>),
        typeof(IEnumerable<VariablePersistenceModel>),     typeof(List<VariablePersistenceModel>),
        typeof(IEnumerable<HighlightPersistenceModel>),    typeof(List<HighlightPersistenceModel>),
        typeof(IEnumerable<ClassPersistenceModel>),        typeof(List<ClassPersistenceModel>),
        typeof(IEnumerable<NamePersistenceModel>),         typeof(List<NamePersistenceModel>),
        typeof(IEnumerable<SubstitutePersistenceModel>),   typeof(List<SubstitutePersistenceModel>),
        typeof(IEnumerable<GagPersistenceModel>),          typeof(List<GagPersistenceModel>),
        typeof(IEnumerable<MacroPersistenceModel>),        typeof(List<MacroPersistenceModel>),
        typeof(IEnumerable<PresetPersistenceModel>),       typeof(List<PresetPersistenceModel>),
        typeof(IEnumerable<WindowSettingsPersistenceModel>), typeof(List<WindowSettingsPersistenceModel>),
        typeof(LayoutState), typeof(ClientState),
    };

    /// <summary>Every type the service reads or writes is served by the generated
    /// context itself, not the reflection fallback behind it.</summary>
    [Theory]
    [MemberData(nameof(PersistedTypes))]
    public void Every_persisted_type_has_generated_metadata(Type type)
    {
        Assert.NotNull(PersistenceJsonContext.Default.GetTypeInfo(type));
    }

    private static readonly JsonSerializerOptions ReflectionWrite = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void Writes_are_byte_identical_to_the_reflection_serializer()
    {
        IEnumerable<TriggerPersistenceModel> triggers = new[]
        {
            new TriggerPersistenceModel { Pattern = @"^You \s+ <see> 'it' & \d+ — ünïcode$" },
        }.Select(t => t);
        var layout = new LayoutState { WindowWidth = 1024.5 };
        layout.MdiBounds["game-text"] = new MdiWindowBounds { X = 1, Y = 2, Width = 3, Height = 4 };

        Assert.Equal(JsonSerializer.Serialize(triggers, ReflectionWrite),
                     JsonSerializer.Serialize(triggers, PersistenceJsonContext.Write));
        Assert.Equal(JsonSerializer.Serialize(layout, ReflectionWrite),
                     JsonSerializer.Serialize(layout, PersistenceJsonContext.Write));
        Assert.Contains(@"\s+", JsonSerializer.Serialize(triggers, PersistenceJsonContext.Write));   // not \s+
    }

    [Fact]
    public void Reads_match_the_default_serializer_including_what_throws()
    {
        const string json = """[{"Pattern":"^a$","Action":"x","ExtraField":1}]""";
        var viaDefault = JsonSerializer.Deserialize<List<TriggerPersistenceModel>>(json)!;
        var viaContext = JsonSerializer.Deserialize<List<TriggerPersistenceModel>>(json, PersistenceJsonContext.Read)!;
        Assert.Equal(viaDefault.Single().Pattern, viaContext.Single().Pattern);

        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<List<TriggerPersistenceModel>>("[{ torn", PersistenceJsonContext.Read));
        // Case-sensitive property names, as the default serializer is.
        Assert.Equal("", JsonSerializer.Deserialize<List<TriggerPersistenceModel>>(
            """[{"pattern":"lower"}]""", PersistenceJsonContext.Read)!.Single().Pattern);
    }
}
