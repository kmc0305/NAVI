using System.Drawing;

namespace LocalAssistant.Engine;

// Simple 8x8 grayscale average-hash perceptual comparison. Good enough to tell
// "basically the same slide" apart from "wrong/empty/corrupted paste" without
// requiring pixel-exact equality (rendering passes through clipboard/paste add
// small compression/antialiasing differences that exact comparison would flag
// as different forever).
public static class ImageSimilarity
{
    private const int Size = 8;

    public static ulong AverageHash(Bitmap source)
    {
        using var small = new Bitmap(source, new Size(ImageSimilarity.Size, ImageSimilarity.Size));
        var gray = new int[ImageSimilarity.Size * ImageSimilarity.Size];

        for (var y = 0; y < ImageSimilarity.Size; y++)
        {
            for (var x = 0; x < ImageSimilarity.Size; x++)
            {
                var p = small.GetPixel(x, y);
                gray[y * ImageSimilarity.Size + x] = (p.R + p.G + p.B) / 3;
            }
        }

        var avg = gray.Average();

        ulong hash = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            if (gray[i] >= avg)
                hash |= 1UL << i;
        }

        return hash;
    }

    public static int HammingDistance(ulong a, ulong b)
    {
        var x = a ^ b;
        var count = 0;
        while (x != 0)
        {
            count++;
            x &= x - 1;
        }
        return count;
    }

    // threshold: max differing bits (out of 64) still considered "same slide".
    public static bool IsSimilar(Bitmap a, Bitmap b, int threshold = 10) =>
        HammingDistance(AverageHash(a), AverageHash(b)) <= threshold;
}
