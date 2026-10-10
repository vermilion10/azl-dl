using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AzlDl.Core.Art;

// One converted motion: the AnimationClip's name, whether the game loops it, and its motion3.json text.
public sealed record Live2DMotion(string Name, bool Loop, string Json);

// Converts the AnimationClips of a Cubism for Unity prefab into Live2D motion3.json files.
//
// In a bundle the clips are compiled: their curves live in m_MuscleClip as a streamed part (cubic segments), a dense
// part (sampled frames) and a constant part, in that curve order. Each curve's target is a GenericBinding whose path is
// the CRC32 of the GameObject path below the animated root (Parameters/ParamAngleX), with the component's script
// telling a parameter value (CubismParameter) from a part opacity (CubismPart).
public static class Live2DMotionConverter
{
    private const int MonoBehaviourType = 114;
    private const int TransformType = 4;

    public static IReadOnlyList<Live2DMotion> Convert(AssetsManager manager, AssetsFileInstance instance, IReadOnlyDictionary<long, string> scripts)
    {
        var names = PathNames(manager, instance);
        var motions = new List<Live2DMotion>();
        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.AnimationClip))
        {
            var clip = manager.GetBaseField(instance, info);
            if (Convert(clip, names, scripts) is { } motion)
            {
                motions.Add(motion);
            }
        }

        return motions;
    }

    // Parameter ids driven by the eye blink and lip sync controllers, for the model3.json groups.
    public static (IReadOnlyList<string> EyeBlink, IReadOnlyList<string> LipSync) Groups(
        AssetsManager manager, AssetsFileInstance instance, IReadOnlyDictionary<long, string> scripts)
    {
        var eyeBlink = new List<string>();
        var lipSync = new List<string>();
        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            var header = manager.GetBaseField(instance, info, AssetReadFlags.SkipMonoBehaviourFields);
            var script = scripts.GetValueOrDefault(header["m_Script.m_PathID"].AsLong);
            var target = script switch { "CubismEyeBlinkParameter" => eyeBlink, "CubismMouthParameter" => lipSync, _ => null };
            if (target is not null && instance.file.GetAssetInfo(header["m_GameObject.m_PathID"].AsLong) is { } gameObject)
            {
                target.Add(manager.GetBaseField(instance, gameObject)["m_Name"].AsString);
            }
        }

        return (eyeBlink.Distinct().Order(StringComparer.Ordinal).ToList(), lipSync.Distinct().Order(StringComparer.Ordinal).ToList());
    }

    // CRC32 of every GameObject path, relative to each of its ancestors, mapped to the GameObject's own name (the
    // parameter or part id).
    private static Dictionary<uint, string> PathNames(AssetsManager manager, AssetsFileInstance instance)
    {
        var nodes = new Dictionary<long, (string Name, long Parent)>();
        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.Transform).Concat(instance.file.GetAssetsOfType(AssetClassID.RectTransform)))
        {
            var transform = manager.GetBaseField(instance, info);
            var gameObject = instance.file.GetAssetInfo(transform["m_GameObject.m_PathID"].AsLong);
            var name = gameObject is null ? "" : manager.GetBaseField(instance, gameObject)["m_Name"].AsString;
            nodes[info.PathId] = (name, transform["m_Father.m_PathID"].AsLong);
        }

        var names = new Dictionary<uint, string>();
        foreach (var (_, (name, parent)) in nodes)
        {
            var path = name;
            names.TryAdd(Crc32(path), name);
            for (var up = parent; nodes.TryGetValue(up, out var ancestor) && ancestor.Parent != 0; up = ancestor.Parent)
            {
                path = ancestor.Name + "/" + path;
                names.TryAdd(Crc32(path), name);
            }
        }

        return names;
    }

    private static Live2DMotion? Convert(AssetTypeValueField clip, Dictionary<uint, string> names, IReadOnlyDictionary<long, string> scripts)
    {
        var muscle = clip["m_MuscleClip"];
        var data = muscle["m_Clip.data"];
        var start = muscle["m_StartTime"].AsFloat;
        var stop = muscle["m_StopTime"].AsFloat;
        var sampleRate = clip["m_SampleRate"].AsFloat;

        // Curve index -> (target, id). Transform bindings take several curves each; everything else takes one.
        var targets = new List<(string Target, string Id)?>();
        foreach (var binding in clip["m_ClipBindingConstant.genericBindings.Array"].Children)
        {
            var type = binding["typeID"].AsInt;
            var width = type == TransformType ? binding["attribute"].AsUInt switch { 2 => 4, _ => 3 } : 1;
            (string, string)? target = null;
            if (type == MonoBehaviourType && names.TryGetValue(binding["path"].AsUInt, out var id))
            {
                target = scripts.GetValueOrDefault(binding["script.m_PathID"].AsLong) switch
                {
                    "CubismParameter" => ("Parameter", id),
                    "CubismPart" => ("PartOpacity", id),
                    _ => null,
                };
            }

            for (var i = 0; i < width; i++)
            {
                targets.Add(i == 0 ? target : null);
            }
        }

        var curves = new Dictionary<int, List<double>>();
        var streamedCount = data["m_StreamedClip.curveCount"].AsInt;
        ReadStreamed(data["m_StreamedClip.data.Array"], start, stop, curves);

        var dense = data["m_DenseClip"];
        var denseCount = (int)dense["m_CurveCount"].AsUInt;
        var samples = dense["m_SampleArray.Array"].Children.Select(c => c.AsFloat).ToArray();
        var frames = dense["m_FrameCount"].AsInt;
        for (var curve = 0; curve < denseCount; curve++)
        {
            var segments = new List<double>();
            for (var frame = 0; frame < frames && (frame * denseCount) + curve < samples.Length; frame++)
            {
                var time = dense["m_BeginTime"].AsFloat + (frame / dense["m_SampleRate"].AsFloat) - start;
                var value = samples[(frame * denseCount) + curve];
                if (segments.Count > 0)
                {
                    segments.Add(0);
                }

                segments.Add(time);
                segments.Add(value);
            }

            curves[streamedCount + curve] = segments;
        }

        var constants = data["m_ConstantClip.data.Array"].Children.Select(c => c.AsFloat).ToArray();
        for (var i = 0; i < constants.Length; i++)
        {
            curves[streamedCount + denseCount + i] = [0, constants[i], 0, stop - start, constants[i]];
        }

        var output = new JsonArray();
        int segmentCount = 0, pointCount = 0;
        foreach (var (index, segments) in curves.OrderBy(c => c.Key))
        {
            if (index >= targets.Count || targets[index] is not { } target || segments.Count < 2)
            {
                continue;
            }

            var (segmentsHere, pointsHere) = Count(segments);
            segmentCount += segmentsHere;
            pointCount += pointsHere;
            output.Add(new JsonObject
            {
                ["Target"] = target.Target,
                ["Id"] = target.Id,
                ["Segments"] = new JsonArray(segments.Select(v => (JsonNode)Math.Round(v, 4)).ToArray()),
            });
        }

        if (output.Count == 0)
        {
            return null;
        }

        var loop = muscle["m_LoopTime"].AsBool;
        var motion = new JsonObject
        {
            ["Version"] = 3,
            ["Meta"] = new JsonObject
            {
                ["Duration"] = Math.Round(stop - start, 4),
                ["Fps"] = sampleRate,
                ["Loop"] = loop,
                ["AreBeziersRestricted"] = true,
                ["CurveCount"] = output.Count,
                ["TotalSegmentCount"] = segmentCount,
                ["TotalPointCount"] = pointCount,
                ["UserDataCount"] = 0,
                ["TotalUserDataSize"] = 0,
            },
            ["Curves"] = output,
        };

        return new Live2DMotion(clip["m_Name"].AsString, loop, motion.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    // The streamed part is a run of frames: time, key count, then per key the curve index and the four coefficients
    // of the cubic that runs from this key to the curve's next key. The first and last frames are sentinels at
    // -/+ float.MaxValue. Each cubic becomes one bezier segment with control points at a third of its length, which
    // reproduces it exactly; a cubic with non-finite coefficients is a step.
    private static void ReadStreamed(AssetTypeValueField words, float start, float stop, Dictionary<int, List<double>> curves)
    {
        var bytes = new byte[words.Children.Count * 4];
        for (var i = 0; i < words.Children.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words.Children[i].AsUInt);
        }

        var keys = new Dictionary<int, List<(float Time, float[] Coefficients)>>();
        var at = 0;
        while (at + 8 <= bytes.Length)
        {
            var time = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            at += 8;
            for (var k = 0; k < count && at + 20 <= bytes.Length; k++)
            {
                var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
                var coefficients = new float[4];
                for (var c = 0; c < 4; c++)
                {
                    coefficients[c] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 4 + (c * 4)));
                }

                at += 20;
                if (time > float.MinValue && time < float.MaxValue)
                {
                    (keys.TryGetValue(index, out var list) ? list : keys[index] = []).Add((time, coefficients));
                }
            }
        }

        foreach (var (index, list) in keys)
        {
            var segments = new List<double>();
            for (var i = 0; i < list.Count; i++)
            {
                var (time, c) = list[i];
                var t0 = time - start;
                var v0 = c[3];
                if (i == 0)
                {
                    segments.Add(t0);
                    segments.Add(v0);
                }

                if (i + 1 >= list.Count)
                {
                    // The last key holds its value to the end of the clip.
                    if (stop - start > t0 + 1e-4)
                    {
                        segments.AddRange([0, stop - start, v0]);
                    }

                    break;
                }

                var t1 = list[i + 1].Time - start;
                var v1 = list[i + 1].Coefficients[3];
                var dt = t1 - t0;
                if (c.Any(v => !float.IsFinite(v)))
                {
                    segments.AddRange([2, t1, v1]);
                    continue;
                }

                var outSlope = c[2];
                var inSlope = (3 * c[0] * dt * dt) + (2 * c[1] * dt) + c[2];
                segments.AddRange([1, t0 + (dt / 3), v0 + (outSlope * dt / 3), t1 - (dt / 3), v1 - (inSlope * dt / 3), t1, v1]);
            }

            curves[index] = segments;
        }
    }

    // Segment and point counts for the Meta block: the first point, then one point per linear or step segment and
    // three per bezier.
    private static (int Segments, int Points) Count(List<double> segments)
    {
        int count = 0, points = 1, at = 2;
        while (at < segments.Count)
        {
            var type = (int)segments[at];
            var width = type == 1 ? 6 : 2;
            count++;
            points += type == 1 ? 3 : 1;
            at += 1 + width;
        }

        return (count, points);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(string text)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
