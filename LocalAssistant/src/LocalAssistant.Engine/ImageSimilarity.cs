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

    // 8x8 평균-해시는 "같은 템플릿(제목 위치, 글머리 기호 배치)"의 슬라이드끼리는
    // 본문 텍스트가 완전히 달라도 구분을 못할 수 있다(실측: 34~36쪽 중복을
    // 못 잡아냄) - 더 촘촘한 격자로 각 칸의 평균 밝기 차이를 직접 합산해서
    // 점수를 낸다(0에 가까울수록 비슷, 클수록 다름). 임계값 없이 점수만 반환.
    public static double GridDissimilarity(Bitmap a, Bitmap b, int gridSize)
    {
        var grayA = GrayscaleGrid(a, gridSize);
        var grayB = GrayscaleGrid(b, gridSize);

        double sum = 0;
        for (var i = 0; i < grayA.Length; i++)
            sum += Math.Abs(grayA[i] - grayB[i]);

        return sum / grayA.Length;
    }

    private static int[] GrayscaleGrid(Bitmap source, int gridSize)
    {
        using var small = new Bitmap(source, new Size(gridSize, gridSize));
        var gray = new int[gridSize * gridSize];
        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var p = small.GetPixel(x, y);
                gray[y * gridSize + x] = (p.R + p.G + p.B) / 3;
            }
        }
        return gray;
    }
}
