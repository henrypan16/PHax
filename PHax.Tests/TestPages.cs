using PHax.Core.Models;
using PHax.Imaging.Conversion;

namespace PHax.Tests;

internal static class TestPages
{
    public static FaxBitmap Text(string title = "Test page", int seed = 1) =>
        CoverPageRenderer.Render(new CoverPageInfo("Recipient " + seed, "555-0100", "PHax tests", "", title,
            string.Join(" ", Enumerable.Range(0, 120).Select(i => $"word{(i * 7 + seed) % 97}")), 3, new DateTime(2026, 1, 2, 3, 4, 5)));

    public static FaxBitmap Noise(int height = 300, int seed = 42, double density = 0.5, FaxResolution res = FaxResolution.Fine)
    {
        var rnd = new Random(seed);
        var page = new FaxBitmap(FaxBitmap.A4Width, height, res);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < page.Width; x++)
            if (rnd.NextDouble() < density) page.SetPixel(x, y, true);
        return page;
    }

    /// <summary>Patterns that exercise long runs, make-up codes, pass and vertical modes.</summary>
    public static FaxBitmap Patterns(FaxResolution res = FaxResolution.Fine)
    {
        var page = new FaxBitmap(FaxBitmap.A4Width, 400, res);
        for (int y = 0; y < page.Height; y++)
        for (int x = 0; x < page.Width; x++)
        {
            bool black = (y / 50) switch
            {
                0 => true,                                  // all black
                1 => false,                                 // all white
                2 => x % 2 == 0,                            // 1-pel stripes
                3 => (x / 64 + y) % 2 == 0,                 // staircase
                4 => x >= y * 3 % 1728 && x < (y * 3 % 1728) + 1700,
                5 => (x + y) % 9 < 3,                       // diagonals
                6 => x is > 10 and < 1720 && y % 3 != 0,
                _ => x == 0 || x == 1727 || (x * 31 + y * 17) % 101 < 2,
            };
            if (black) page.SetPixel(x, y, true);
        }
        return page;
    }
}
