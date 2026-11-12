using System.Text.Json;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;

namespace Jiten.Api.Services.MetadataEdits;

/// <summary>Result of replaying one field of an edit onto the deck's current value.</summary>
public sealed record FieldMerge(JsonElement Value, bool Clash, bool Changes);

public sealed partial class MetadataEditService
{
    public static bool IsListField(MetadataField field) =>
        field is MetadataField.Aliases or MetadataField.CharacterNames or MetadataField.Genres or MetadataField.Tags or MetadataField.Links
            or MetadataField.Relationships;

    /// <summary>
    /// Replays the edit's change (before to after) onto the current value: scalars take the edit's value, lists apply only
    /// the items the edit added or removed. A clash is a field the edit and a later change set to different values.
    /// </summary>
    public static FieldMerge Merge(MetadataField field, JsonElement before, JsonElement after, JsonElement current, bool preferEdit)
    {
        if (field == MetadataField.Franchise && IsUnresolvedFranchise(after))
            return new FieldMerge(after, false, true);

        if (!IsListField(field))
        {
            var clash = !DeckMetadataSnapshot.KeyEquals(field, current, before) && !DeckMetadataSnapshot.KeyEquals(field, current, after);
            var value = clash && !preferEdit ? current : after;
            return new FieldMerge(value, clash, !DeckMetadataSnapshot.KeyEquals(field, value, current));
        }

        var (merged, hasClash) = field switch
        {
            MetadataField.Aliases or MetadataField.CharacterNames => MergeSet<string>(before, after, current, StringComparer.Ordinal, s => s),
            MetadataField.Genres => MergeSet<int>(before, after, current, EqualityComparer<int>.Default, g => g.ToString("D5")),
            MetadataField.Tags => MergeTags(before, after, current, preferEdit),
            MetadataField.Links => MergeLinks(before, after, current, preferEdit),
            _ => MergeRelationships(before, after, current)
        };
        return new FieldMerge(merged, hasClash, !DeckMetadataSnapshot.KeyEquals(field, merged, current));
    }

    /// <summary>A franchise picked through a deck that had none when the edit was made; approval decides what it resolves to.</summary>
    public static bool IsUnresolvedFranchise(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.GetProperty("franchiseId").ValueKind != JsonValueKind.Number;

    private static List<T> ListOf<T>(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.Deserialize<List<T>>(DeckMetadataSnapshot.Json) ?? [] : [];

    /// <summary>Identity of one list item; the review page builds the same keys for its item toggles.</summary>
    public static string ItemKey(MetadataField field, JsonElement item) => field switch
    {
        MetadataField.Tags => item.GetProperty("tagId").GetInt32().ToString(),
        MetadataField.Links => $"{item.GetProperty("linkType").GetInt32()}|{item.GetProperty("url").GetString()}",
        MetadataField.Relationships =>
            $"{item.GetProperty("sourceDeckId").GetInt32()}>{item.GetProperty("targetDeckId").GetInt32()}:{item.GetProperty("relationshipType").GetInt32()}",
        MetadataField.Genres => item.GetInt32().ToString(),
        _ => item.GetString() ?? ""
    };

    /// <summary>Kept is the change without the excluded items, which stay as they were before; LeftOut is the change those items make alone.</summary>
    public static (JsonElement Kept, JsonElement LeftOut) SplitListChange(MetadataField field, JsonElement before, JsonElement after,
                                                                          IReadOnlySet<string> excludedKeys)
    {
        bool Excluded(JsonElement item) => excludedKeys.Contains(ItemKey(field, item));
        var b = ListOf<JsonElement>(before);
        var a = ListOf<JsonElement>(after);
        return (DeckMetadataSnapshot.ToElement(a.Where(i => !Excluded(i)).Concat(b.Where(Excluded)).ToList()),
                DeckMetadataSnapshot.ToElement(b.Where(i => !Excluded(i)).Concat(a.Where(Excluded)).ToList()));
    }

    private static (JsonElement, bool) MergeSet<T>(JsonElement before, JsonElement after, JsonElement current, IEqualityComparer<T> comparer,
                                                   Func<T, string> sortKey)
    {
        var b = ListOf<T>(before).ToHashSet(comparer);
        var a = ListOf<T>(after).ToHashSet(comparer);
        var result = ListOf<T>(current).ToHashSet(comparer);
        result.ExceptWith(b.Where(x => !a.Contains(x)));
        result.UnionWith(a.Where(x => !b.Contains(x)));
        var ordered = result.OrderBy(sortKey, StringComparer.Ordinal).ToList();
        return (DeckMetadataSnapshot.ToElement(ordered), false);
    }

    private static (JsonElement, bool) MergeTags(JsonElement before, JsonElement after, JsonElement current, bool preferEdit)
    {
        var b = ListOf<SnapshotTag>(before).ToDictionary(t => t.TagId);
        var a = ListOf<SnapshotTag>(after).ToDictionary(t => t.TagId);
        var result = ListOf<SnapshotTag>(current).ToDictionary(t => t.TagId);
        var clash = false;

        foreach (var id in b.Keys.Where(id => !a.ContainsKey(id)))
        {
            if (result.TryGetValue(id, out var retuned) && retuned.Percentage != b[id].Percentage)
            {
                clash = true;
                if (!preferEdit)
                    continue;
            }

            result.Remove(id);
        }

        foreach (var (id, tag) in a)
        {
            if (b.TryGetValue(id, out var old) && old.Percentage == tag.Percentage)
                continue;
            if (result.TryGetValue(id, out var now) && now.Percentage != tag.Percentage && (old == null || now.Percentage != old.Percentage))
            {
                clash = true;
                if (!preferEdit)
                    continue;
            }

            result[id] = tag;
        }

        return (DeckMetadataSnapshot.ToElement(result.Values.OrderBy(t => t.TagId).ToList()), clash);
    }

    /// <summary>Each non-Web link type holds one URL, so it merges like a scalar; Web links merge as a set.</summary>
    private static (JsonElement, bool) MergeLinks(JsonElement before, JsonElement after, JsonElement current, bool preferEdit)
    {
        var b = ListOf<SnapshotLink>(before);
        var a = ListOf<SnapshotLink>(after);
        var c = ListOf<SnapshotLink>(current);
        var clash = false;

        var result = c.Where(l => l.LinkType == LinkType.Web).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        var bWeb = b.Where(l => l.LinkType == LinkType.Web).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        var aWeb = a.Where(l => l.LinkType == LinkType.Web).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        result.ExceptWith(bWeb.Where(u => !aWeb.Contains(u)));
        result.UnionWith(aWeb.Where(u => !bWeb.Contains(u)));
        var links = result.Select(u => new SnapshotLink(LinkType.Web, u)).ToList();

        var types = b.Concat(a).Concat(c).Select(l => l.LinkType).Where(t => t != LinkType.Web).Distinct();
        foreach (var type in types)
        {
            string? Url(List<SnapshotLink> list) => list.FirstOrDefault(l => l.LinkType == type)?.Url;
            var (bu, au, cu) = (Url(b), Url(a), Url(c));
            var target = cu;
            if (bu != au)
            {
                if (cu == bu)
                    target = au;
                else if (cu != au)
                {
                    clash = true;
                    if (preferEdit)
                        target = au;
                }
            }

            if (target != null)
                links.Add(new SnapshotLink(type, target));
        }

        return (DeckMetadataSnapshot.ToElement(links.OrderBy(l => l.LinkType).ThenBy(l => l.Url, StringComparer.Ordinal).ToList()), clash);
    }

    private static (JsonElement, bool) MergeRelationships(JsonElement before, JsonElement after, JsonElement current)
    {
        static (int, int, DeckRelationshipType) Key(SnapshotRelationship r) => (r.SourceDeckId, r.TargetDeckId, r.RelationshipType);
        var b = ListOf<SnapshotRelationship>(before).ToDictionary(Key);
        var a = ListOf<SnapshotRelationship>(after).ToDictionary(Key);
        var result = ListOf<SnapshotRelationship>(current).ToDictionary(Key);
        foreach (var key in b.Keys.Where(k => !a.ContainsKey(k)))
            result.Remove(key);
        foreach (var (key, edge) in a.Where(e => !b.ContainsKey(e.Key)))
            result[key] = edge;
        var ordered = result.Values.OrderBy(r => r.SourceDeckId).ThenBy(r => r.TargetDeckId).ThenBy(r => r.RelationshipType).ToList();
        return (DeckMetadataSnapshot.ToElement(ordered), false);
    }
}
