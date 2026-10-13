namespace TechCare.Clinic.Api.Features.Ai;

/// <summary>
/// PLAN_ADDENDUM.md 7.8: check what a file actually is, not just what its extension claims — the library must
/// never hold a "PDF" that is text or a "txt" that is binary, because the indexer and the doctor opening the file
/// would both meet the garbage later. PLAN_FULL.md 5.1 adds the two image types a visit attachment may be:
/// a patient's file must never hold a "PNG" that is not a PNG.
/// </summary>
public static class ContentSignature
{
    /// <summary>Returns null when the sample looks like the claimed type, otherwise the Arabic rejection reason.</summary>
    public static async Task<string?> ValidateAsync(string extension, Stream stream)
    {
        var sample = new byte[8192];
        int read;
        try
        {
            read = await stream.ReadAsync(sample, 0, sample.Length);
        }
        catch
        {
            return "تعذّرت قراءة الملف";
        }
        if (read == 0) return "الملف فارغ";

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => LooksWithLikePdf(sample, read) ? null : "الملف لا يحمل توقيع PDF صالح",
            ".png" => LooksWithLikePng(sample, read) ? null : "الصورة لا تحمل توقيع PNG صالح",
            ".jpg" or ".jpeg" => LooksWithLikeJpeg(sample, read) ? null : "الصورة لا تحمل توقيع JPEG صالح",
            ".txt" or ".md" or ".jsonl" => LooksWithLikeText(sample, read) ? null : "الملف لا يبدو نصاً صالحاً",
            _ => null
        };
    }

    private static bool LooksWithLikePdf(byte[] buffer, int length)
    {
        // PDF files may lead with whitespace before the header; the header itself is %PDF.
        var i = 0;
        while (i < Math.Min(length, 10) && buffer[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return length - i >= 4
            && buffer[i] == (byte)'%'
            && buffer[i + 1] == (byte)'P'
            && buffer[i + 2] == (byte)'D'
            && buffer[i + 3] == (byte)'F';
    }

    /// <summary>The 8-byte PNG magic: 0x89 'P' 'N' 'G' CR LF 0x1A LF — exact and fixed, unlike the PDF header.</summary>
    private static bool LooksWithLikePng(byte[] buffer, int length)
    {
        return length >= 8
            && buffer[0] == 0x89
            && buffer[1] == 0x50
            && buffer[2] == 0x4E
            && buffer[3] == 0x47
            && buffer[4] == 0x0D
            && buffer[5] == 0x0A
            && buffer[6] == 0x1A
            && buffer[7] == 0x0A;
    }

    /// <summary>A JPEG stream starts with the SOI marker 0xFFD8 followed by another marker (0xFFxx, e.g. APP0/APP1).</summary>
    private static bool LooksWithLikeJpeg(byte[] buffer, int length)
    {
        return length >= 3
            && buffer[0] == 0xFF
            && buffer[1] == 0xD8
            && buffer[2] == 0xFF;
    }

    private static bool LooksWithLikeText(byte[] buffer, int length)
    {
        var printable = 0;
        for (var i = 0; i < length; i++)
        {
            var b = buffer[i];
            if (b == 0) return false; // a NUL byte is a binary giveaway
            if (b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126) || b >= 128)
                printable++;
        }
        return (double)printable / length >= 0.9;
    }
}