using System.Buffers.Binary;
using ImageMagick;

namespace JacaDownloader;

// Local image conversion (Magick.NET). Converted files wait in a temp folder until the user saves them.
public static class Converter
{
    // Formats that can be read. Camera RAW, HEIC/HEIF and XCF are source-only.
    public static readonly string[] Sources =
    {
        "arw", "avif", "bmp", "cr2", "cr3", "crw", "dcr", "dng", "erf", "gif", "heic", "heif", "icns", "ico",
        "jfif", "jpeg", "jpg", "mos", "mrw", "nef", "orf", "pef", "png", "ppm", "psb", "psd", "raf", "rw2",
        "tga", "tif", "tiff", "webp", "x3f", "xcf"
    };

    // Formats that can be written.
    public static readonly string[] Destinations =
    {
        "avif", "bmp", "eps", "gif", "icns", "ico", "jfif", "jpeg", "jpg", "png", "ppm", "ps", "psb", "psd", "tga",
        "tif", "tiff", "webp"
    };

    public static readonly string TempDir = Path.Combine(Tools.AppDataDir, "convert-tmp");

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static void CleanTemp()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
    }

    public static string TempPath(string id, string ext) => Path.Combine(TempDir, id + "." + ext);

    public static string? FindTemp(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit) || !Directory.Exists(TempDir)) return null;
        return Directory.GetFiles(TempDir, id + ".*").FirstOrDefault();
    }

    // Reads the source file and writes the converted one into the temp folder. Returns the temp path.
    public static async Task<string> ConvertAsync(string sourcePath, string fromExt, string toExt, string id)
    {
        fromExt = fromExt.ToLowerInvariant();
        toExt = toExt.ToLowerInvariant();
        if (!Sources.Contains(fromExt)) throw new ArgumentException("Formato de origem não suportado.");
        if (!Destinations.Contains(toExt)) throw new ArgumentException("Formato de destino não suportado.");

        var output = TempPath(id, toExt);
        Directory.CreateDirectory(TempDir);

        // One conversion at a time keeps memory use predictable with big RAW files.
        await Gate.WaitAsync();
        try
        {
            await Task.Run(() => Convert(sourcePath, fromExt, toExt, output));
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }
        finally { Gate.Release(); }
        return output;
    }

    static void Convert(string sourcePath, string fromExt, string toExt, string output)
    {
        try
        {
            if (Animated.Contains(fromExt) && Animated.Contains(toExt) && SaveAnimation(sourcePath, fromExt, toExt, output)) return;
            using var image = Load(sourcePath, fromExt);
            Save(image, toExt, output);
        }
        catch (MagickCorruptImageErrorException)
        {
            throw new InvalidOperationException("O arquivo está corrompido ou não é uma imagem válida.");
        }
        catch (MagickException)
        {
            throw new InvalidOperationException("Não foi possível converter este arquivo.");
        }
    }

    static readonly string[] Animated = { "gif", "webp", "avif" };

    // Keeps every frame when an animated image goes to another animated format. Returns false for single frames.
    static bool SaveAnimation(string path, string fromExt, string toExt, string output)
    {
        using var frames = new MagickImageCollection(path, new MagickReadSettings { Format = ReadFormat(fromExt) });
        if (frames.Count < 2) return false;
        frames.Coalesce();
        frames.Write(output, toExt == "gif" ? MagickFormat.Gif : toExt == "webp" ? MagickFormat.WebP : MagickFormat.Avif);
        return true;
    }

    static IMagickImage<ushort> Load(string path, string fromExt)
    {
        if (fromExt == "icns") return LoadIcns(path);

        // Forcing the decoder from the extension stops the file from being sniffed as another format.
        var settings = new MagickReadSettings { Format = ReadFormat(fromExt) };

        if (fromExt == "xcf")
        {
            using var layers = new MagickImageCollection(path, settings);
            return layers.Flatten();
        }

        // For PSD/PSB/TIFF the first image is the merged composite.
        var image = new MagickImage(path, settings);
        image.AutoOrient();
        if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
        return image;
    }

    static MagickFormat ReadFormat(string ext) => ext switch
    {
        "jfif" or "jpeg" or "jpg" => MagickFormat.Jpeg,
        "tif" or "tiff" => MagickFormat.Tiff,
        "heic" => MagickFormat.Heic,
        "heif" => MagickFormat.Heic,
        _ => Enum.Parse<MagickFormat>(ext, true)
    };

    static void Save(IMagickImage<ushort> image, string toExt, string output)
    {
        switch (toExt)
        {
            case "jpg" or "jpeg" or "jfif":
                Flatten(image, MagickColors.White);
                image.Quality = 92;
                image.Write(output, MagickFormat.Jpeg);
                break;
            case "bmp" or "ppm" or "eps" or "ps":
                Flatten(image, MagickColors.White);
                image.Write(output, toExt switch { "bmp" => MagickFormat.Bmp, "ppm" => MagickFormat.Ppm, "eps" => MagickFormat.Eps, _ => MagickFormat.Ps });
                break;
            case "ico":
                if (image.Width > 256 || image.Height > 256) image.Resize(new MagickGeometry(256, 256));
                image.Write(output, MagickFormat.Ico);
                break;
            case "icns":
                WriteIcns(image, output);
                break;
            case "psd" or "psb":
                if (image.Depth < 8) image.Depth = 8;
                image.Write(output, toExt == "psd" ? MagickFormat.Psd : MagickFormat.Psb);
                break;
            case "webp":
                image.Quality = 90;
                image.Write(output, MagickFormat.WebP);
                break;
            case "avif":
                image.Quality = 80;
                image.Write(output, MagickFormat.Avif);
                break;
            case "tif" or "tiff":
                image.Write(output, MagickFormat.Tiff);
                break;
            default:
                image.Write(output, Enum.Parse<MagickFormat>(toExt, true));
                break;
        }
    }

    // Formats without transparency get the image over a solid background instead of black.
    static void Flatten(IMagickImage<ushort> image, IMagickColor<ushort> background)
    {
        if (!image.HasAlpha) return;
        image.BackgroundColor = background;
        image.Alpha(AlphaOption.Remove);
    }

    // ICNS stores PNG-compressed icons: 128, 256 and 512 px squares with the image centered.
    static void WriteIcns(IMagickImage<ushort> image, string output)
    {
        var entries = new List<(string Type, byte[] Data)>();
        foreach (var (type, size) in new[] { ("ic07", 128), ("ic08", 256), ("ic09", 512) })
        {
            using var square = image.Clone();
            square.Resize(new MagickGeometry((uint)size, (uint)size));
            square.BackgroundColor = MagickColors.Transparent;
            square.Extent((uint)size, (uint)size, Gravity.Center);
            entries.Add((type, square.ToByteArray(MagickFormat.Png)));
        }

        using var stream = File.Create(output);
        Span<byte> header = stackalloc byte[8];
        var total = 8 + entries.Sum(e => 8 + e.Data.Length);
        "icns"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], total);
        stream.Write(header);
        foreach (var (type, data) in entries)
        {
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], 8 + data.Length);
            stream.Write(header);
            stream.Write(data);
        }
    }

    // Reads the largest PNG-compressed entry of an ICNS file.
    static IMagickImage<ushort> LoadIcns(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("icns"u8))
            throw new InvalidOperationException("Arquivo ICNS inválido.");

        byte[]? best = null;
        var offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 4));
            if (length < 8 || offset + length > bytes.Length) break;
            var data = bytes.AsSpan(offset + 8, length - 8);
            if (data.Length > 8 && data[..4].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) && (best == null || data.Length > best.Length))
                best = data.ToArray();
            offset += length;
        }

        if (best == null) throw new InvalidOperationException("Este ICNS não tem uma imagem que o app consiga ler.");
        return new MagickImage(best, new MagickReadSettings { Format = MagickFormat.Png });
    }
}
