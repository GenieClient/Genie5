using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Genie.Core.Persistence;

/// <summary>
/// Source-generated JSON metadata for the persistence models (public #287), so
/// loading the rule files on the launch / connect path no longer pays the
/// reflection warm-up for each model type.
///
/// <para>Saves serialize the <c>Select(…)</c> projection, so each model is
/// registered as both <c>IEnumerable&lt;T&gt;</c> (write) and <c>List&lt;T&gt;</c>
/// (read).</para>
/// </summary>
[JsonSerializable(typeof(IEnumerable<AliasPersistenceModel>))]
[JsonSerializable(typeof(List<AliasPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<TriggerPersistenceModel>))]
[JsonSerializable(typeof(List<TriggerPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<VariablePersistenceModel>))]
[JsonSerializable(typeof(List<VariablePersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<HighlightPersistenceModel>))]
[JsonSerializable(typeof(List<HighlightPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<ClassPersistenceModel>))]
[JsonSerializable(typeof(List<ClassPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<NamePersistenceModel>))]
[JsonSerializable(typeof(List<NamePersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<SubstitutePersistenceModel>))]
[JsonSerializable(typeof(List<SubstitutePersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<GagPersistenceModel>))]
[JsonSerializable(typeof(List<GagPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<MacroPersistenceModel>))]
[JsonSerializable(typeof(List<MacroPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<PresetPersistenceModel>))]
[JsonSerializable(typeof(List<PresetPersistenceModel>))]
[JsonSerializable(typeof(IEnumerable<WindowSettingsPersistenceModel>))]
[JsonSerializable(typeof(List<WindowSettingsPersistenceModel>))]
[JsonSerializable(typeof(LayoutState))]
[JsonSerializable(typeof(ClientState))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext
{
    /// <summary>The generated metadata first, reflection behind it. The fallback
    /// means a type nobody registered still serializes exactly as it did, rather
    /// than throwing <see cref="NotSupportedException"/>.</summary>
    private static readonly IJsonTypeInfoResolver Resolver =
        JsonTypeInfoResolver.Combine(Default, new DefaultJsonTypeInfoResolver());

    /// <summary>Options for reading rule / state files: the serializer defaults
    /// (what every load used before), plus the generated metadata. Nothing about
    /// which input throws and which parses changes — the loaders that swallow a
    /// parse error still swallow it, and <c>RuleFileLiveReload</c>'s strict parse
    /// still throws before anything is cleared.</summary>
    public static readonly JsonSerializerOptions Read = new() { TypeInfoResolver = Resolver };

    /// <summary>Options for writing: indented, and regex metacharacters and UTF-8
    /// left literal. These files are shared and hand-edited, and the default
    /// HTML-safe encoder turns <c>\s+</c> into <c>\\s+</c>. "Unsafe" only
    /// refers to embedding JSON in HTML/JS.</summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented    = true,
        Encoder          = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = Resolver,
    };
}
