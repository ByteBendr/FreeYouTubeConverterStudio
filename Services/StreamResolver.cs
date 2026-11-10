using FreeYouTubeConverterStudio.Models;
using YoutubeExplode.Videos.Streams;

namespace FreeYouTubeConverterStudio.Services;

public sealed class ResolvedStreams
{
    public List<IStreamInfo> Streams { get; } = new();
    public string Extension { get; set; } = "mp4";
    /// <summary>True when the single stream can be saved as-is (no ffmpeg needed).</summary>
    public bool Direct { get; set; }
}

public static class StreamResolver
{
    public static ResolvedStreams Resolve(StreamManifest manifest, string format, string quality)
    {
        var r = new ResolvedStreams();

        // ---------- Audio only ----------
        if (Catalog.IsAudioFormat(format))
        {
            var mp3 = format.StartsWith("MP3", StringComparison.OrdinalIgnoreCase);
            r.Extension = mp3 ? "mp3" : "m4a";

            var all = manifest.GetAudioOnlyStreams().ToList();
            if (all.Count == 0) return r;

            var pool = all;
            if (!mp3)
            {
                // YouTube's AAC audio lives in an mp4 container; saving it as .m4a needs no re-encode.
                var aac = all.Where(s => IsContainer(s, "mp4")).ToList();
                if (aac.Count > 0)
                {
                    pool = aac;
                    r.Direct = true;
                }
            }

            r.Streams.Add(PickAudio(pool, quality));
            return r;
        }

        // ---------- Video ----------
        var wantsMp4 = format.StartsWith("MP4", StringComparison.OrdinalIgnoreCase);
        r.Extension = wantsMp4 ? "mp4" : "webm";

        var videoOnlyAll = manifest.GetVideoOnlyStreams().Cast<IVideoStreamInfo>().ToList();
        var videoOnly = videoOnlyAll.Where(s => IsContainer(s, r.Extension)).ToList();
        if (videoOnly.Count == 0) videoOnly = videoOnlyAll;

        var muxed = manifest.GetMuxedStreams()
            .Where(s => IsContainer(s, r.Extension))
            .Cast<IVideoStreamInfo>()
            .ToList();

        var bestVideoOnly = PickVideo(videoOnly, quality, wantsMp4);
        var bestMuxed = PickVideo(muxed, quality, wantsMp4);

        var isWorst = quality.Equals("Worst", StringComparison.OrdinalIgnoreCase);
        var useSeparate = bestVideoOnly != null &&
            (bestMuxed == null ||
             (isWorst ? Height(bestVideoOnly) < Height(bestMuxed) : Height(bestVideoOnly) > Height(bestMuxed)));

        if (!useSeparate && bestMuxed != null)
        {
            // Pre-muxed stream (YouTube only offers these up to ~360p): no ffmpeg needed.
            r.Streams.Add(bestMuxed);
            r.Direct = true;
            return r;
        }

        if (bestVideoOnly == null) return r;

        var audioAll = manifest.GetAudioOnlyStreams().ToList();
        var audioPool = audioAll.Where(s => IsContainer(s, wantsMp4 ? "mp4" : "webm")).ToList();
        if (audioPool.Count == 0) audioPool = audioAll;

        r.Streams.Add(bestVideoOnly);
        if (audioPool.Count > 0)
        {
            var audio = isWorst
                ? audioPool.OrderBy(s => s.Bitrate.BitsPerSecond).First()
                : audioPool.OrderByDescending(s => s.Bitrate.BitsPerSecond).First();
            r.Streams.Add(audio);
        }

        r.Direct = false;
        return r;
    }

    private static bool IsContainer(IStreamInfo s, string name) =>
        string.Equals(s.Container.Name, name, StringComparison.OrdinalIgnoreCase);

    private static int Height(IVideoStreamInfo s) => s.VideoQuality.MaxHeight;

    private static IVideoStreamInfo? PickVideo(List<IVideoStreamInfo> candidates, string quality, bool preferH264)
    {
        if (candidates.Count == 0) return null;

        int CodecRank(IVideoStreamInfo s) =>
            preferH264 && s.VideoCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        if (quality.Equals("Best", StringComparison.OrdinalIgnoreCase))
        {
            return candidates
                .OrderByDescending(Height)
                .ThenByDescending(s => s.VideoQuality.Framerate)
                .ThenByDescending(CodecRank)
                .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                .First();
        }

        if (quality.Equals("Worst", StringComparison.OrdinalIgnoreCase))
        {
            return candidates.OrderBy(Height).ThenBy(s => s.Bitrate.BitsPerSecond).First();
        }

        var target = ParseHeight(quality);
        var atOrBelow = candidates.Where(s => Height(s) <= target).ToList();

        if (atOrBelow.Count > 0)
        {
            return atOrBelow
                .OrderByDescending(Height)
                .ThenByDescending(s => s.VideoQuality.Framerate)
                .ThenByDescending(CodecRank)
                .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                .First();
        }

        // Nothing at or below the target: take the lowest available above it.
        return candidates.OrderBy(Height).ThenByDescending(CodecRank).First();
    }

    private static int ParseHeight(string quality) =>
        int.TryParse(quality.TrimEnd('p', 'P'), out var h) ? h : int.MaxValue;

    /// <summary>Best / High / Medium / Low / Worst by relative bitrate rank.</summary>
    private static AudioOnlyStreamInfo PickAudio(List<AudioOnlyStreamInfo> candidates, string quality)
    {
        var ordered = candidates.OrderByDescending(s => s.Bitrate.BitsPerSecond).ToList();
        var last = ordered.Count - 1;

        var index = quality switch
        {
            "Worst" => last,
            "High" => last / 4,
            "Medium" => last / 2,
            "Low" => last * 3 / 4,
            _ => 0
        };

        return ordered[Math.Clamp(index, 0, last)];
    }
}
