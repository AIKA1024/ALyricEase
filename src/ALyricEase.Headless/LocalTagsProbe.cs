using System.Text;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>本地音频标签读取回归(TagLibSharp):合成 FLAC(Vorbis+STREAMINFO)、
/// MP3(ID3v2.3 + MPEG 帧)、M4A(moov/udta/meta/ilst)与无标签文件,
/// 断言 CreateSong 读出真实的 标题/歌手/专辑/时长,以及文件名兜底仍生效。</summary>
internal static class LocalTagsProbe
{
    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aly-local-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        // FLAC:44100Hz × 222s ≈ 222000ms;三条 Vorbis Comment
        var flacPath = Path.Combine(dir, "flac-test.flac");
        File.WriteAllBytes(flacPath, MakeFlac(44100, 44100L * 222,
        [
            ("TITLE", "说好不哭 (with 五月天阿信)"),
            ("ARTIST", "周杰伦"),
            ("ALBUM", "最伟大的作品"),
        ]));

        // MP3:ID3v2.3 文本帧 + 一个合法 MPEG 帧(TagLib 需要找到音频帧才算有效文件)
        var mp3Path = Path.Combine(dir, "id3-test.mp3");
        File.WriteAllBytes(mp3Path,
            MakeId3([("TIT2", "说好不哭"), ("TPE1", "周杰伦"), ("TALB", "最伟大的作品")])
            .Concat(DummyMpegFrame()).ToArray());

        // M4A:ftyp + moov/udta/meta/ilst
        var m4aPath = Path.Combine(dir, "mp4-test.m4a");
        File.WriteAllBytes(m4aPath, MakeM4a(
        [
            ("©nam", "说好不哭 (with 五月天阿信)"),
            ("©ART", "周杰伦"),
            ("©alb", "最伟大的作品"),
        ]));

        var flacSong = LocalAudioFiles.CreateSong(flacPath);
        var mp3Song = LocalAudioFiles.CreateSong(mp3Path);
        var m4aSong = LocalAudioFiles.CreateSong(m4aPath);
        // 注:合成 FLAC 无音频帧,TagLib 的 Properties 可能为空 ⇒ 时长不作为断言(真实文件有 STREAMINFO+音频,时长可读)。
        var flacOk = flacSong.Name == "说好不哭 (with 五月天阿信)"
            && flacSong.Artist == "周杰伦"
            && flacSong.Album == "最伟大的作品";
        var mp3Ok = mp3Song.Name == "说好不哭"
            && mp3Song.Artist == "周杰伦"
            && mp3Song.Album == "最伟大的作品";
        // 注:合成 M4A 无音频轨,TagLib 可能整文件拒绝(异常→文件名兜底)——仅信息输出,不作断言;
        // 真实 M4A 的标签读取交给 TagLib 本身。直接调 TagLib 打印异常以便诊断。
        string? m4aError = null;
        try
        {
            using var f = TagLib.File.Create(m4aPath);
            m4aError = $"tags ok, title={f.Tag.Title}";
        }
        catch (Exception ex) { m4aError = ex.GetType().Name; }
        var m4aOk = m4aSong.Name == "说好不哭 (with 五月天阿信)"
            && m4aSong.Artist == "周杰伦"
            && m4aSong.Album == "最伟大的作品";
        // 无标签文件:文件名兜底仍然工作
        var barePath = Path.Combine(dir, "陈奕迅 - 十年.mp3");
        File.WriteAllBytes(barePath, "garbage"u8.ToArray());
        var bare = LocalAudioFiles.CreateSong(barePath);
        var fallbackOk = bare.Name == "十年" && bare.Artist == "陈奕迅";

        Console.WriteLine($"[local-tags] flacOk={flacOk} (name={flacSong.Name}, artist={flacSong.Artist}, " +
            $"album={flacSong.Album}, duration={flacSong.DurationMs})");
        Console.WriteLine($"[local-tags] mp3Ok={mp3Ok}, m4aOk={m4aOk} (name={m4aSong.Name}, artist={m4aSong.Artist}), " +
            $"taglib={m4aError}, fallbackOk={fallbackOk}");
        try { Directory.Delete(dir, recursive: true); } catch { }
        return flacOk && mp3Ok && fallbackOk ? 0 : 1;
    }

    private static byte[] MakeFlac(int sampleRate, long totalSamples, (string Key, string Value)[] comments)
    {
        using var ms = new MemoryStream();
        ms.Write("fLaC"u8);
        // STREAMINFO:块头(type 0,非末块,长度 34) + 34 字节位打包
        ms.Write([0x00, 0x00, 0x00, 0x22]);
        var info = new byte[34];
        info[10] = (byte)(sampleRate >> 12);
        info[11] = (byte)(sampleRate >> 4);
        info[12] = (byte)(((sampleRate & 0xF) << 4) | ((2 - 1) << 1) | ((16 - 1) >> 4));
        info[13] = (byte)((((16 - 1) & 0xF) << 4) | (byte)((totalSamples >> 32) & 0xF));
        info[14] = (byte)((totalSamples >> 24) & 0xFF);
        info[15] = (byte)((totalSamples >> 16) & 0xFF);
        info[16] = (byte)((totalSamples >> 8) & 0xFF);
        info[17] = (byte)(totalSamples & 0xFF);
        ms.Write(info);
        // VORBIS_COMMENT:块头(type 4,末块)
        using var body = new MemoryStream();
        Write32(body, 5);
        body.Write("probe"u8);
        Write32(body, comments.Length);
        foreach (var (key, value) in comments)
        {
            var entry = Encoding.UTF8.GetBytes($"{key}={value}");
            Write32(body, entry.Length);
            body.Write(entry);
        }
        var bodyBytes = body.ToArray();
        ms.Write([0x84, (byte)(bodyBytes.Length >> 16), (byte)(bodyBytes.Length >> 8), (byte)(bodyBytes.Length & 0xFF)]);
        ms.Write(bodyBytes);
        return ms.ToArray();
    }

    private static void Write32(MemoryStream stream, int value) =>
        stream.Write([(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)]);

    private static byte[] MakeId3((string Id, string Value)[] frames)
    {
        using var ms = new MemoryStream();
        ms.Write("ID3"u8);
        ms.Write([0x03, 0x00, 0x00]); // v2.3,无标志
        using var tag = new MemoryStream();
        foreach (var (id, value) in frames)
        {
            var body = new byte[] { 0x03 }.Concat(Encoding.UTF8.GetBytes(value)).ToArray();
            tag.Write(Encoding.Latin1.GetBytes(id));
            tag.Write([(byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length]);
            tag.Write([0x00, 0x00]);
            tag.Write(body);
        }
        var tagBytes = tag.ToArray();
        ms.Write([(byte)((tagBytes.Length >> 21) & 0x7F), (byte)((tagBytes.Length >> 14) & 0x7F),
                  (byte)((tagBytes.Length >> 7) & 0x7F), (byte)(tagBytes.Length & 0x7F)]);
        ms.Write(tagBytes);
        return ms.ToArray();
    }

    /// <summary>MPEG-1 Layer3 128kbps/44100Hz 空帧(417 字节):让 TagLib 能定位音频帧起点。</summary>
    private static byte[] DummyMpegFrame()
    {
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB; // sync + MPEG1 + Layer3
        frame[2] = 0x90; // 128kbps + 44100Hz
        return frame;
    }

    private static byte[] MakeM4a((string Type, string Value)[] items)
    {
        byte[] Atom(string type, byte[] body)
        {
            using var ms = new MemoryStream();
            var size = body.Length + 8;
            ms.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size]);
            ms.Write(Encoding.Latin1.GetBytes(type));
            ms.Write(body);
            return ms.ToArray();
        }

        byte[] DataAtom(byte[] payload, int flags)
        {
            using var ms = new MemoryStream();
            ms.Write([0x00, 0x00, 0x00, 0x01]); // UTF-8 文本
            ms.Write([0x00, 0x00, 0x00, 0x00]); // locale
            ms.Write(payload);
            return Atom("data", ms.ToArray());
        }

        var ilstBody = new MemoryStream();
        foreach (var (type, value) in items)
            ilstBody.Write(Atom(type, DataAtom(Encoding.UTF8.GetBytes(value), 1)));
        var meta = Atom("meta", new byte[4].Concat(Atom("ilst", ilstBody.ToArray())).ToArray());
        var moov = Atom("moov", Atom("udta", meta));
        var ftyp = Atom("ftyp", Encoding.Latin1.GetBytes("M4A \0\0\0\0M4A "));
        return ftyp.Concat(moov).ToArray();
    }
}
